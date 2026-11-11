using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Game.Arena;
using Game.Core;
using Game.Meta;
using Game.Testing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Tests
{
    /// <summary>
    /// Plays the real scenes against a live server and saves screenshots to logs/shots.
    /// Needs a graphics device (do not pass -nographics) and a server with the Arena module:
    /// <c>GAME_SERVER_URL=http://localhost:5001 Unity -runTests -testPlatform PlayMode</c>.
    /// </summary>
    public class GameTours
    {
        private const string ShotDir = @"D:\GameServer-full\GameServer\logs\shots";

        private static string Url => Environment.GetEnvironmentVariable("GAME_SERVER_URL") ?? "http://localhost:5001";

        private RenderTexture _rt;

        // ---- helpers -----------------------------------------------------------------------------------------

        /// <summary>Draws the main camera into a texture of a fixed size, so screenshots do not depend on the Game view.</summary>
        private void Attach(int width, int height)
        {
            if (_rt != null)
                _rt.Release();

            _rt = new RenderTexture(width, height, 24) { antiAliasing = 4 };
            Camera.main.targetTexture = _rt;
        }

        private IEnumerator Shot(string name)
        {
            // WaitForEndOfFrame never fires in batch mode, so let two frames pass and render the camera by hand.
            yield return null;
            yield return null;
            Camera.main.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = _rt;
            var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        private static IEnumerator Wait(float seconds)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
                yield return null;
        }

        private static IEnumerator WaitFor(Func<bool> condition, float timeout, string what)
        {
            var end = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end)
                    Assert.Fail("Timed out waiting for " + what);
                yield return null;
            }
        }

        private IEnumerator OpenMenu()
        {
            // Headless rendering to a texture makes Unity log a harmless back-buffer size mismatch that would fail the test.
            LogAssert.ignoreFailingMessages = true;
            Directory.CreateDirectory(ShotDir);
            PlayerPrefs.SetString("game.serverUrl", Url);
            MenuScreen.StartPage = MenuScreen.BattleIndex;

            SceneManager.LoadScene("Boot");
            yield return null;
            yield return null;
            Attach(1080, 1920);
            yield return Shot("00_boot");

            yield return WaitFor(() => SceneManager.GetActiveScene().name == "Menu" && UnityEngine.Object.FindAnyObjectByType<MenuScreen>() != null, 60, "the menu");
            yield return null;
            Attach(1080, 1920);
            yield return Wait(1.5f);
        }

        // ---- tours -------------------------------------------------------------------------------------------

        [UnityTest, Timeout(300000)]
        public IEnumerator MenuTour()
        {
            yield return OpenMenu();
            var menu = UnityEngine.Object.FindAnyObjectByType<MenuScreen>();
            var names = new[] { "shop", "quests", "battle", "social_friends", "rank" };

            for (var i = 0; i < 5; i++)
            {
                menu.Show(i);
                yield return Wait(1.6f);
                yield return Shot("01_menu_" + names[i]);
            }

            menu.GoToSocial(1);
            yield return Wait(1.2f);
            yield return Shot("02_social_party");
            menu.GoToSocial(2);
            yield return Wait(1.5f);
            yield return Shot("03_social_chat");

            menu.Show(MenuScreen.BattleIndex);
            Attach(1920, 1080);
            yield return Wait(0.8f);
            yield return Shot("04_battle_landscape");

            Assert.That(GameSession.Instance.IsReady, "session should be signed in");
        }

        [Test]
        public void StartupCheckReadsMaintenanceConfig()
        {
            var none = StartupCheck.FromConfig(new System.Collections.Generic.Dictionary<string, Newtonsoft.Json.Linq.JToken>());
            Assert.That(none.InMaintenance, Is.False);

            var off = StartupCheck.FromConfig(new System.Collections.Generic.Dictionary<string, Newtonsoft.Json.Linq.JToken>
            {
                ["maintenance"] = Newtonsoft.Json.Linq.JObject.Parse("{\"enabled\":false,\"message\":\"x\"}")
            });
            Assert.That(off.InMaintenance, Is.False);

            var on = StartupCheck.FromConfig(new System.Collections.Generic.Dictionary<string, Newtonsoft.Json.Linq.JToken>
            {
                ["maintenance"] = Newtonsoft.Json.Linq.JObject.Parse("{\"enabled\":true,\"message\":\"Back soon\",\"until\":\"18:00\"}")
            });
            Assert.That(on.InMaintenance, Is.True);
            Assert.That(on.MaintenanceMessage, Is.EqualTo("Back soon"));
            Assert.That(on.MaintenanceUntil, Is.EqualTo("18:00"));

            var update = new StartupCheck();
            update.ApplyVersion(new GameServer.Client.VersionCheck { Status = "update_required", Latest = "2.0", Url = "https://store" });
            Assert.That(update.MustUpdate, Is.True);
            Assert.That(update.CanUpdate, Is.False);
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator ProfileTour()
        {
            yield return OpenMenu();
            var menu = UnityEngine.Object.FindAnyObjectByType<MenuScreen>();
            var session = GameSession.Instance;

            menu.OpenProfile();
            yield return Wait(0.8f);
            yield return Shot("30_profile_open");
            GameObject.Destroy(GameObject.Find("Modal"));
            yield return null;

            // Save through the service like the SAVE button does, then look at the result.
            var save = session.Profile.SaveAsync("Tank Hero", null, Ui.AvatarId(3));
            while (!save.IsCompleted)
                yield return null;
            if (save.IsFaulted)
                Assert.Fail("saving the profile failed: " + save.Exception.GetBaseException().Message);

            Assert.That(session.Account.DisplayName, Is.EqualTo("Tank Hero"));
            Assert.That(session.Account.Avatar, Is.EqualTo("av_3"));

            yield return Wait(0.6f);
            yield return Shot("31_profile_saved_topbar");
            menu.OpenProfile();
            yield return Wait(0.8f);
            yield return Shot("32_profile_after_save");
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator ChatTour()
        {
            yield return OpenMenu();
            var menu = UnityEngine.Object.FindAnyObjectByType<MenuScreen>();
            var session = GameSession.Instance;

            menu.GoToSocial(2);
            yield return Wait(2.0f);
            Debug.Log("[ChatTour] lines after opening chat: " + session.Chat.Lines.Count);

            // another player says something in the lobby
            ArenaBot bot = null;
            var botTask = System.Threading.Tasks.Task.Run(async () =>
            {
                bot = await ArenaBot.CreateAsync(Url);
                await bot.Client.Realtime.JoinChannelAsync(GameServer.Client.Channels.Room(GameIds.LobbyRoom));
                await bot.Client.Realtime.SendMessageAsync(GameServer.Client.Channels.Room(GameIds.LobbyRoom), "hello from the bot");
            });
            while (!botTask.IsCompleted)
                yield return null;
            if (botTask.IsFaulted)
                Debug.Log("[ChatTour] bot failed: " + botTask.Exception.GetBaseException().Message);

            yield return Wait(1.5f);
            Debug.Log("[ChatTour] lines after the bot spoke: " + session.Chat.Lines.Count + " -> " + string.Join(" | ", session.Chat.Lines.Select(l => l.Sender + ": " + l.Text)));

            // the player says something
            var send = session.Chat.SendAsync("hello from me");
            while (!send.IsCompleted)
                yield return null;
            if (send.IsFaulted)
                Debug.Log("[ChatTour] send failed: " + send.Exception.GetBaseException().Message);

            yield return Wait(1.5f);
            Debug.Log("[ChatTour] lines after I spoke: " + session.Chat.Lines.Count + " -> " + string.Join(" | ", session.Chat.Lines.Select(l => l.Sender + ": " + l.Text)));
            Assert.That(session.Chat.Lines.Count, Is.EqualTo(2), "the bot's message and mine, each once");
            yield return Shot("50_chat_lobby");

            if (bot != null)
                bot.CloseAsync().Wait(3000);
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator InventoryTour()
        {
            yield return OpenMenu();
            var menu = UnityEngine.Object.FindAnyObjectByType<MenuScreen>();
            var session = GameSession.Instance;

            // Own a skin: buy one if the wallet allows it (a repeat purchase is refused, which is fine).
            var buyError = "";
            if (!session.Loadout.OwnedItems().Any(kv => Skins.IsSkin(kv.Key)))
            {
                var buy = session.Shop.BuyAsync("skin_red");
                while (!buy.IsCompleted)
                    yield return null;
                if (buy.IsFaulted)
                    buyError = " (buying failed: " + buy.Exception.GetBaseException().Message + "; coins " + session.Wallet.Coins + ")";
            }

            var skin = session.Loadout.OwnedItems().FirstOrDefault(kv => Skins.IsSkin(kv.Key)).Key;
            Assert.That(skin, Is.Not.Null, "the account should own a skin (needs 60 coins to buy the first one)" + buyError);

            session.Loadout.Equip(skin);
            Assert.That(session.Loadout.EquippedSkin, Is.EqualTo(skin));

            var refresh = session.Wallet.RefreshAsync();
            while (!refresh.IsCompleted)
                yield return null;
            Debug.Log("[InventoryTour] wallet after refresh: " + string.Join(", ", session.Wallet.Wallet.Select(kv => kv.Key + "=" + kv.Value)) +
                      " | items: " + string.Join(", ", session.Wallet.Items.Select(kv => kv.Key + "=" + kv.Value)));

            menu.GoToInventory();
            yield return Wait(1.6f);
            yield return Shot("40_inventory_equipped");

            session.Loadout.Equip(null);
            Assert.That(session.Loadout.EquippedSkin, Is.Null);
            yield return Wait(0.6f);
            yield return Shot("41_inventory_plain");
            session.Loadout.Equip(skin);
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator PopupTour()
        {
            yield return OpenMenu();

            var canvas = Ui.CreateCanvas("PopupTest", 50);
            var column = Ui.Column(canvas.transform);
            var cases = new (string Name, Func<Modal> Make)[]
            {
                ("maintenance", () => Popups.Maintenance(column, "We are upgrading the servers.\nThanks for waiting!", "18:00 (UTC+3:30)", () => { })),
                ("update_required", () => Popups.UpdateRequired(column, "1.4.0", "https://example.com/store")),
                ("update_available", () => Popups.UpdateAvailable(column, "1.4.0", "https://example.com/store", () => { })),
                ("signed_out", () => Popups.SessionEnded(column, () => { })),
            };

            foreach (var c in cases)
            {
                var modal = c.Make();
                yield return Wait(0.6f);
                yield return Shot("20_popup_" + c.Name);
                UnityEngine.Object.Destroy(modal.gameObject);
                yield return null;
            }
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator PlayOneMatch()
        {
            yield return OpenMenu();
            var session = GameSession.Instance;
            var coinsBefore = session.Wallet.Coins;

            // Fight with the owned skin on, so the screenshots show it on the tank.
            var owned = session.Loadout.OwnedItems().FirstOrDefault(kv => Skins.IsSkin(kv.Key)).Key;
            if (owned != null)
                session.Loadout.Equip(owned);

            // The opponent: a headless bot that queues for the same mode and attacks.
            ArenaBot bot = null;
            var botTask = Task.Run(async () =>
            {
                bot = await ArenaBot.CreateAsync(Url);
                var found = bot.ExpectMatch(60);
                await bot.QueueAsync();
                await bot.ConnectAsync(await found);
                await bot.PlayAsync(true);
            });

            // The player presses BATTLE like a person would.
            var battle = GameObject.Find("Button_BATTLE");
            Assert.That(battle, Is.Not.Null, "BATTLE button");
            battle.GetComponent<Button>().onClick.Invoke();
            yield return Wait(1.0f);
            yield return Shot("10_searching");

            yield return WaitFor(() => SceneManager.GetActiveScene().name == "Arena", 60, "the Arena scene");
            yield return null;
            Attach(1080, 1920);
            yield return Wait(1.2f);
            yield return Shot("11_arena_countdown");

            yield return WaitFor(() => UnityEngine.Object.FindAnyObjectByType<ArenaScreen>() != null, 5, "the arena screen");
            yield return Wait(4.2f);
            yield return Shot("12_arena_fight");
            Attach(1920, 1080);
            yield return Wait(0.5f);
            yield return Shot("13_arena_landscape");

            yield return WaitFor(() => GameObject.Find("Modal") != null, 90, "the result popup");
            Attach(1080, 1920);
            yield return Wait(1.0f);
            yield return Shot("14_arena_result");

            while (!botTask.IsCompleted)
                yield return null;
            if (botTask.IsFaulted)
                Assert.Fail("bot failed: " + botTask.Exception.GetBaseException());

            var go = GameObject.Find("Button_CONTINUE");
            Assert.That(go, Is.Not.Null, "CONTINUE button");
            go.GetComponent<Button>().onClick.Invoke();

            yield return WaitFor(() => SceneManager.GetActiveScene().name == "Menu", 20, "the menu after the match");
            yield return Wait(3f);
            Attach(1080, 1920);
            yield return Shot("15_menu_after");

            Assert.That(session.Wallet.Coins, Is.GreaterThan(coinsBefore), "coins should be paid after the match");
            if (bot != null)
                bot.CloseAsync().Wait(3000);
        }
    }
}
