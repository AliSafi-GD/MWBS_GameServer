using System;
using System.Threading.Tasks;
using Game.Arena;
using Game.Meta;
using GameServer.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>Batch-mode entry points: <c>Unity -batchmode -executeMethod Game.Editor.GameBuild.CreateScenes</c>.</summary>
    public static class GameBuild
    {
        private const string BootPath = "Assets/Scenes/Boot.unity";

        private const string MenuPath = "Assets/Scenes/Menu.unity";
        private const string ArenaPath = "Assets/Scenes/Arena.unity";

        [MenuItem("Game/Create Scenes")]
        public static void CreateScenes()
        {
            SaveScene(BootPath, "Boot", typeof(BootScreen));
            SaveScene(MenuPath, "Menu", typeof(MenuScreen));
            SaveScene(ArenaPath, "Arena", typeof(ArenaScreen));

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BootPath, true),
                new EditorBuildSettingsScene(MenuPath, true),
                new EditorBuildSettingsScene(ArenaPath, true)
            };
            AssetDatabase.SaveAssets();
            Debug.Log("[GameBuild] Scenes created: " + BootPath + ", " + MenuPath + ", " + ArenaPath);
        }

        private static void SaveScene(string path, string rootName, Type screen)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraGo.tag = "MainCamera";
            cameraGo.transform.position = new Vector3(0f, 0f, -10f);
            var camera = cameraGo.GetComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.07f, 0.08f, 0.12f);

            new GameObject(rootName, screen);
            EditorSceneManager.SaveScene(scene, path);
        }

        /// <summary>Signs in with the SDK from inside Unity's runtime against a live server, then exits with 0 or 1.</summary>
        public static void SmokeBoot()
        {
            var url = Environment.GetEnvironmentVariable("GAME_SERVER_URL") ?? "http://localhost:5000";
            var code = 1;
            try
            {
                var task = Task.Run(async () =>
                {
                    var client = new GameClient(new GameClientOptions
                    {
                        BaseUrl = url,
                        Http = new HttpClientHttp(15),
                        Storage = new MemoryStorage(),
                        Log = Debug.Log
                    });

                    var version = await client.Config.VersionCheckAsync("0.1.0");
                    var config = await client.Config.GetAsync();
                    await client.Auth.LoginDeviceAsync();
                    var account = await client.Account.GetAsync();
                    var restored = await client.Auth.TryRestoreSessionAsync();
                    var wallet = await client.Economy.WalletAsync();
                    var items = await client.Economy.InventoryAsync();
                    var shop = await client.Shop.ListAsync();
                    var daily = await client.Progress.DailyAsync();
                    var quests = await client.Progress.QuestsAsync();
                    var achievements = await client.Progress.AchievementsAsync();
                    var board = await client.Competitions.LeaderboardAsync("arena_wins", 10, 1);
                    var friends = await client.Social.Friends.ListAsync();
                    var party = await client.Social.Parties.MineAsync();

                    await client.Realtime.ConnectAsync();
                    var channel = Channels.Room("lobby");
                    await client.Realtime.JoinChannelAsync(channel);
                    var sent = await client.Realtime.SendMessageAsync(channel, "unity smoke " + DateTime.UtcNow.ToString("HH:mm:ss"));
                    var history = await client.Chat.HistoryAsync(channel, 10);
                    await client.Realtime.DisconnectAsync();
                    return $"board={board.Entries?.Count ?? 0} friends={friends.Count} party={(party == null ? "none" : "yes")} " +
                           $"chatSent={sent != null} chatHistory={history.Count} " +
                           $"version={version.Status} configKeys={config.Count} user={account.Username} restored={restored} " +
                           $"wallet={wallet.Count} items={items.Count} shop={shop.Count} dailyStreak={daily.Streak} " +
                           $"quests={quests.Count} achievements={achievements.Count}";
                });

                if (!task.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("sign-in flow took over 60 s");

                Debug.Log("[SmokeBoot] OK " + task.Result);
                code = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("[SmokeBoot] FAILED " + (ex is AggregateException agg ? agg.GetBaseException() : ex));
            }

            EditorApplication.Exit(code);
        }
    }
}
