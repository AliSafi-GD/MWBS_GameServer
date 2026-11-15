using System.Collections.Generic;
using System.Linq;
using Game.Core;
using GameServer.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>Home: the arena, the Battle button, and four reward chests (the daily reward and three quests).</summary>
    public sealed class BattlePage : Page
    {
        private sealed class Slot
        {
            public RectTransform Root;
            public Image Glow;
            public Image Chest;
            public Text Title;
            public Text State;
            public ProgressBar Bar;
            public Image ReadyTag;
            public System.Action OnTap;
        }

        private readonly Slot[] _slots = new Slot[4];
        private static readonly Color PlainTank = Theme.Hex(0x4AA8FF);

        private Text _wins;
        private GButton _battle;
        private RectTransform _tankHolder;
        private Image _heroGlow;
        private string _shownSkin = "?";

        protected override void Build()
        {
            // The name of the game, in two tones.
            var tank = Ui.Headline(Root, "TANK", 118, Color.white, Theme.Hex(0x9CCBFF));
            tank.alignment = TextAnchor.MiddleRight;
            Ui.Pin(tank, 0f, 0.93f, 430, 160, 24, 0);
            var arena = Ui.Headline(Root, "ARENA", 118);
            arena.alignment = TextAnchor.MiddleLeft;
            Ui.Pin(arena, 1f, 0.93f, 570, 160, -24, 0);

            // League and wins in one pill under the title.
            var league = Ui.Panel(Root, Theme.Outline, 34f, "League", true);
            Ui.Pin(league, 0.5f, 0.858f, 660, 66);
            var leagueFill = Ui.Panel(league.transform, Theme.Purple, 30f, "Fill");
            Ui.Inset(leagueFill, 4, 4, 4, 4);
            _wins = Ui.Label(leagueFill.transform, "", 36);
            Ui.Stretch(_wins.rectTransform);
            Ui.Inset(_wins, 64, 0, 64, 0);
            for (var side = -1; side <= 1; side += 2)
            {
                var star = Ui.IconPic(leagueFill.transform, Icon.Star, Theme.Gold);
                Ui.Pin(star, 0.5f, 0.5f, 40, 40, side * 282, 0);
            }

            BuildHero();
            BuildChests();
            BuildButtons();

            S.Goals.Changed += Refresh;
            S.Loadout.Changed += ShowTank;
        }

        public override void Dispose()
        {
            S.Goals.Changed -= Refresh;
            S.Loadout.Changed -= ShowTank;
        }

        public override void OnShow()
        {
            ShowTank();
            Refresh();
            C.Run(async () => await S.Goals.RefreshAsync());
        }

        /// <summary>The player's own tank on a lit stand: rays turning behind it, sparkles around it, and a way to the garage.</summary>
        private void BuildHero()
        {
            var holder = Ui.NewRect(Root, "Hero");
            Ui.Pin(holder, 0.5f, 0.645f, 900, 600);

            var rays = Ui.Pic(holder, Art.Rays(), new Color(1f, 0.9f, 0.55f, 0.3f), "Rays");
            Ui.Pin(rays, 0.5f, 0.5f, 1120, 1120, 0, 10);
            rays.gameObject.AddComponent<Spin>().DegreesPerSecond = 9f;

            _heroGlow = Ui.Pic(holder, Art.Glow(), new Color(1f, 0.82f, 0.3f, 0.5f), "Glow");
            Ui.Pin(_heroGlow, 0.5f, 0.5f, 800, 800, 0, 10);
            _heroGlow.gameObject.AddComponent<Pulse>().Amount = 0.05f;

            // The stand: flat ellipses, wider than tall, so it reads as a round platform seen from the front.
            Ellipse(holder, new Color(0f, 0.01f, 0.08f, 0.45f), 640, 170, -212, "StandShadow");
            Ellipse(holder, Theme.Outline, 580, 178, -178, "StandRim");
            Ellipse(holder, Theme.Hex(0x1B2F78), 560, 160, -180, "StandSide");
            Ellipse(holder, Theme.Edge, 560, 150, -160, "StandEdge");
            Ellipse(holder, Theme.PanelLight, 544, 138, -160, "StandTop");
            Ellipse(holder, Theme.Panel, 430, 100, -160, "StandCore");
            var standLight = Ellipse(holder, new Color(1f, 0.9f, 0.5f, 0.35f), 360, 70, -160, "StandLight");
            standLight.sprite = Art.Glow();

            _tankHolder = Ui.NewRect(holder, "TankHolder");
            Ui.Pin(_tankHolder, 0.5f, 0.5f, 10, 10, 0, 6);
            _tankHolder.gameObject.AddComponent<Bob>().Height = 12f;

            var spots = new[] { new Vector3(-330, 150, 54), new Vector3(300, 210, 70), new Vector3(360, -40, 44), new Vector3(-380, -70, 60), new Vector3(-160, 250, 40), new Vector3(150, -230, 36) };
            for (var i = 0; i < spots.Length; i++)
            {
                var sparkle = Ui.Pic(holder, Art.Sparkle(), i % 2 == 0 ? Color.white : Theme.Hex(0xFFE9A0), "Sparkle");
                Ui.Pin(sparkle, 0.5f, 0.5f, spots[i].z, spots[i].z, spots[i].x, spots[i].y);
                var twinkle = sparkle.gameObject.AddComponent<Twinkle>();
                twinkle.Phase = i * 1.7f;
                twinkle.Speed = 1.3f + i * 0.17f;
            }

            var garage = GButton.Create(holder, "GARAGE", Theme.Blue, Theme.BlueLip, 34, 8);
            Ui.Pin(garage, 0.5f, 0.5f, 240, 78, 0, -268);
            garage.Button.onClick.AddListener(() => C.Host.GoToInventory());
        }

        private static Image Ellipse(Transform parent, Color color, float width, float height, float y, string name)
        {
            var img = Ui.Circle(parent, color, name);
            img.preserveAspect = false;
            Ui.Pin(img, 0.5f, 0.5f, width, height, 0, y);
            return img;
        }

        /// <summary>Draws the tank in the equipped skin (again only when the skin changed).</summary>
        private void ShowTank()
        {
            var skin = S.Loadout.EquippedSkin ?? "";
            if (skin == _shownSkin || _tankHolder == null)
                return;

            _shownSkin = skin;
            for (var i = _tankHolder.childCount - 1; i >= 0; i--)
                Object.Destroy(_tankHolder.GetChild(i).gameObject);

            var color = Skins.ColorOf(skin) ?? PlainTank;
            var tank = Parts.TankPreview(_tankHolder, color, 1.7f, new Vector2(0.5f, 0.5f));
            tank.localRotation = Quaternion.Euler(0, 0, 14f);
            _heroGlow.color = new Color(Mathf.Lerp(1f, color.r, 0.5f), Mathf.Lerp(0.82f, color.g, 0.5f), Mathf.Lerp(0.3f, color.b, 0.5f), 0.5f);

            Anim.Run(C.Host, 0.3f, t =>
            {
                if (tank != null)
                    tank.localScale = Vector3.one * Mathf.LerpUnclamped(0.6f, 1f, Anim.EaseOutBack(t));
            });
        }

        private void BuildChests()
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var slot = new Slot();
                var card = Parts.Card(Root, Theme.Panel, 30f);
                var rim = card.parent;
                slot.Root = (RectTransform)rim;
                Ui.Pin(slot.Root.GetComponent<Image>(), 0f, 0.3f, 240, 320, 30 + i * 260, 0);

                slot.Glow = Ui.Pic(card, Art.Glow(), new Color(0.4f, 1f, 0.4f, 0.0f), "Glow");
                Ui.Pin(slot.Glow, 0.5f, 0.55f, 260, 260);

                slot.Chest = Ui.IconPic(card, Icon.Chest);
                Ui.Pin(slot.Chest, 0.5f, 0.62f, 150, 150);

                slot.Title = Ui.Label(card, "", 34, Color.white);
                Ui.Pin(slot.Title, 0.5f, 0.97f, 220, 54, 0, -4);
                slot.Title.rectTransform.pivot = new Vector2(0.5f, 1f);

                slot.Bar = ProgressBar.Create(card, Theme.Green);
                Ui.Pin(slot.Bar, 0.5f, 0.0f, 200, 44, 0, 30);
                slot.Bar.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0f);

                slot.ReadyTag = Ui.Panel(card, Theme.Green, 26f, "Ready");
                Ui.Pin(slot.ReadyTag, 0.5f, 0.0f, 200, 62, 0, 24);
                slot.ReadyTag.rectTransform.pivot = new Vector2(0.5f, 0f);
                var readyLabel = Ui.Label(slot.ReadyTag.transform, "OPEN", 40);
                Ui.Stretch(readyLabel.rectTransform);
                slot.ReadyTag.gameObject.AddComponent<Pulse>();

                slot.State = Ui.Label(card, "", 28, Theme.Muted);
                Ui.Pin(slot.State, 0.5f, 0.0f, 220, 50, 0, 20);
                slot.State.rectTransform.pivot = new Vector2(0.5f, 0f);

                var tap = card.gameObject.AddComponent<Button>();
                tap.transition = Selectable.Transition.None;
                var s = slot;
                tap.onClick.AddListener(() => s.OnTap?.Invoke());
                card.GetComponent<Image>().raycastTarget = true;

                _slots[i] = slot;
            }
        }

        private void BuildButtons()
        {
            _battle = GButton.Create(Root, "BATTLE", Theme.Gold, Theme.GoldLip, 96, 22);
            Ui.Pin(_battle, 0.5f, 0.092f, 600, 220);
            _battle.gameObject.AddComponent<Pulse>().Amount = 0.03f;
            _battle.Button.onClick.AddListener(StartSearch);
            Ui.Place(_battle.Label, 0f, 0.26f, 1f, 0.98f);
            var mode = Ui.Label(_battle.Face.transform, "1 VS 1   RANKED", 30, Theme.Hex(0x7A4300), TextAnchor.MiddleCenter, false);
            Ui.Place(mode, 0f, 0.06f, 1f, 0.3f);
            ShineSweep.Create(_battle.Face, 26f);

            SideButton("PARTY", Icon.People, Theme.Blue, Theme.BlueLip, false, () => C.Host.GoToSocial(1));
            SideButton("FRIENDLY", Icon.Crown, Theme.Purple, Theme.PurpleLip, true, OpenFriendly);
        }

        private void SideButton(string caption, Icon icon, Color face, Color lip, bool right, UnityEngine.Events.UnityAction onTap)
        {
            var button = GButton.Create(Root, "", face, lip, 30, 14);
            button.name = "Button_" + caption;
            Ui.Pin(button, right ? 1f : 0f, 0.105f, 176, 170, right ? -42 : 42, 0);
            var picture = Ui.IconPic(button.Face.transform, icon);
            Ui.Place(picture, 0.2f, 0.2f, 0.8f, 0.8f);
            button.Button.onClick.AddListener(onTap);

            var label = Ui.Label(Root, caption, 30);
            Ui.Pin(label, right ? 1f : 0f, 0.105f, 220, 40, right ? -20 : 20, -118);
        }

        private void StartSearch()
        {
            if (!S.Play.HasRanked)
            {
                C.Toasts.Show("Quick match is not set up on this server", Theme.Red);
                return;
            }

            C.Run(() => S.Play.FindMatchAsync(), _battle);
        }

        // ---- chests ------------------------------------------------------------------------------------------

        private void Refresh()
        {
            _wins.text = "TRAINING CAMP  -  " + Ui.Num(S.Goals.Wins) + (S.Goals.Wins == 1 ? " WIN" : " WINS");

            UpdateDailySlot(_slots[0]);
            var quests = S.Goals.Quests;
            for (var i = 1; i < _slots.Length; i++)
                UpdateQuestSlot(_slots[i], i - 1 < quests.Count ? quests[i - 1] : null);
        }

        private void UpdateDailySlot(Slot slot)
        {
            var daily = S.Goals.Daily;
            slot.Title.text = "DAILY";
            slot.Bar.gameObject.SetActive(false);

            if (daily == null)
            {
                SetSlot(slot, ready: false, done: false, state: "...");
                slot.OnTap = null;
                return;
            }

            if (daily.ClaimedToday)
            {
                SetSlot(slot, false, true, "Day " + daily.Streak + " done");
                slot.OnTap = () => C.Toast("Next reward tomorrow", Icon.Chest);
                return;
            }

            SetSlot(slot, true, false, "");
            slot.OnTap = () => C.Run(async () =>
            {
                var claim = await S.Goals.ClaimDailyAsync();
                C.Reward(claim.Reward, "Day " + claim.Day);
            });
        }

        private void UpdateQuestSlot(Slot slot, Goal goal)
        {
            if (goal == null)
            {
                slot.Title.text = "QUEST";
                slot.Bar.gameObject.SetActive(false);
                SetSlot(slot, false, false, "No quest");
                slot.OnTap = null;
                return;
            }

            slot.Title.text = goal.Name.ToUpperInvariant();
            if (goal.Claimed)
            {
                slot.Bar.gameObject.SetActive(false);
                SetSlot(slot, false, true, "Done");
                slot.OnTap = null;
            }
            else if (goal.Completed)
            {
                slot.Bar.gameObject.SetActive(false);
                SetSlot(slot, true, false, "");
                var id = goal.Id;
                slot.OnTap = () => C.Run(async () =>
                {
                    var claim = await S.Goals.ClaimQuestAsync(id);
                    C.Reward(claim.Reward, goal.Name);
                });
            }
            else
            {
                SetSlot(slot, false, false, "");
                slot.Bar.gameObject.SetActive(true);
                slot.Bar.Set(goal.Target <= 0 ? 0 : (float)goal.Progress / goal.Target, goal.Progress + "/" + goal.Target);
                slot.OnTap = () => C.Toast(goal.Name + ": " + goal.Progress + " / " + goal.Target, Icon.Scroll);
            }
        }

        private static void SetSlot(Slot slot, bool ready, bool done, string state)
        {
            slot.ReadyTag.gameObject.SetActive(ready);
            slot.Glow.color = new Color(0.4f, 1f, 0.4f, ready ? 0.55f : 0f);
            slot.Chest.color = done ? new Color(1, 1, 1, 0.4f) : (ready ? Color.white : new Color(0.75f, 0.78f, 0.9f, 1f));
            slot.State.text = state;

            var bob = slot.Chest.GetComponent<Bob>();
            if (ready && bob == null)
                slot.Chest.gameObject.AddComponent<Bob>().Height = 8f;
            else if (!ready && bob != null)
                Object.Destroy(bob);
        }

        // ---- friendly matches --------------------------------------------------------------------------------

        private void OpenFriendly()
        {
            if (!S.Play.HasFriendly)
            {
                C.Toasts.Show("Friendly battles are not set up on this server", Theme.Red);
                return;
            }

            var modal = Modal.Create(C.Overlay, "FRIENDLY BATTLE", 920, 1100);
            var create = GButton.Create(modal.Body, "CREATE ROOM", Theme.Green, Theme.GreenLip, 56, 14);
            Ui.Place(create, 0.05f, 0.88f, 0.95f, 1f);
            create.Button.onClick.AddListener(() => C.Run(() => S.Play.CreatePrivateAsync(S.Account.Username + "'s room"), create));

            var info = Ui.Label(modal.Body, "or join an open room", 36, Theme.Muted);
            Ui.Place(info, 0f, 0.8f, 1f, 0.87f);

            var list = ScrollList.Create(modal.Body);
            Ui.Place(list, 0f, 0f, 1f, 0.8f);
            Fill(list);
        }

        private void Fill(ScrollList list)
        {
            C.Run(async () =>
            {
                var rooms = await S.Play.ListPrivateAsync();
                if (list == null)
                    return;

                list.Clear();
                if (rooms.Count == 0)
                {
                    var empty = Ui.Label(list.Content, "No open rooms yet.", 40, Theme.Muted);
                    Parts.Size(empty, null, 140);
                    return;
                }

                foreach (var room in rooms)
                {
                    var id = room.Id;
                    var card = Parts.Card(list.Content, Theme.PanelLight, 30f);
                    Parts.Size(card.parent.GetComponent<Image>(), null, 150);

                    var name = Ui.Label(card, string.IsNullOrEmpty(room.Label) ? "Room" : room.Label, 44, Color.white, TextAnchor.MiddleLeft);
                    Ui.Place(name, 0.04f, 0.4f, 0.62f, 0.95f);
                    var count = Ui.Label(card, room.Players + " / " + room.MaxPlayers + " players", 32, Theme.Muted, TextAnchor.MiddleLeft);
                    Ui.Place(count, 0.04f, 0.05f, 0.62f, 0.45f);

                    var join = GButton.Create(card, "JOIN", Theme.Green, Theme.GreenLip, 44, 12);
                    Ui.Place(join, 0.64f, 0.14f, 0.97f, 0.86f);
                    join.Interactable = room.Open && room.Players < room.MaxPlayers;
                    join.Button.onClick.AddListener(() => C.Run(() => S.Play.JoinPrivateAsync(id), join));
                }
            });
        }
    }
}
