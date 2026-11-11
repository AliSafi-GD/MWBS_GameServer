using System;
using System.Linq;
using System.Threading.Tasks;
using Game.Arena;
using Game.Testing;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Two headless players find each other through the matchmaker, play one Arena round over the match transport and
    /// check the result and the coins paid out. <c>Unity -batchmode -executeMethod Game.Editor.ArenaBots.Run</c>.
    /// </summary>
    public static class ArenaBots
    {
        public static void Run()
        {
            var url = Environment.GetEnvironmentVariable("GAME_SERVER_URL") ?? "http://localhost:5000";
            var code = 1;
            try
            {
                var task = Task.Run(() => PlayAsync(url));
                if (!task.Wait(TimeSpan.FromSeconds(120)))
                    throw new TimeoutException("match took over 120 s");

                Debug.Log("[ArenaBots] OK " + task.Result);
                code = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("[ArenaBots] FAILED " + (ex is AggregateException agg ? agg.GetBaseException() : ex));
            }

            EditorApplication.Exit(code);
        }

        private static async Task<string> PlayAsync(string url)
        {
            var bots = await Task.WhenAll(ArenaBot.CreateAsync(url, Debug.Log), ArenaBot.CreateAsync(url, Debug.Log));
            var before = await Task.WhenAll(bots.Select(b => b.Client.Economy.WalletAsync()));

            var found = bots.Select(b => b.ExpectMatch()).ToArray();
            await Task.WhenAll(bots.Select(b => b.QueueAsync()));
            var infos = await Task.WhenAll(found);

            if (infos[0].MatchId != infos[1].MatchId)
                throw new Exception("bots were put into different matches");

            for (var i = 0; i < bots.Length; i++)
                await bots[i].ConnectAsync(infos[i]);

            // Whoever got slot 0 attacks; the other one stands still.
            await Task.WhenAll(bots.Select(b => Task.Run(() => b.PlayAsync(b.State.You == 0))));

            var a = bots[0].State;
            ArenaResult[] results;
            lock (a.Sync)
                results = a.Results.ToArray();

            if (!a.IsOver || results.Length != 2)
                throw new Exception("round did not finish");
            if (a.Winner < 0)
                throw new Exception("expected a winner, got a draw (" + a.Reason + ")");

            var winnerBot = bots.First(b => b.State.You == a.Winner);
            var loserBot = bots.First(b => b.State.You != a.Winner);

            // The server sends "over" first and pays afterwards, so wait for the coins to arrive.
            long win = 0, lose = 0;
            for (var i = 0; i < 20; i++)
            {
                win = (await winnerBot.Client.Economy.WalletAsync()).TryGetValue("coins", out var wc) ? wc : 0;
                lose = (await loserBot.Client.Economy.WalletAsync()).TryGetValue("coins", out var lc) ? lc : 0;
                if (win > 0 && lose > 0)
                    break;
                await Task.Delay(250);
            }

            var winBefore = before[Array.IndexOf(bots, winnerBot)].TryGetValue("coins", out var wb) ? wb : 0;
            var loseBefore = before[Array.IndexOf(bots, loserBot)].TryGetValue("coins", out var lb) ? lb : 0;

            if (win - winBefore != 30 || lose - loseBefore != 10)
                throw new Exception($"coins wrong: winner +{win - winBefore} (want 30), loser +{lose - loseBefore} (want 10)");

            foreach (var bot in bots)
                await bot.CloseAsync();

            return $"winner=slot{a.Winner} reason={a.Reason} winnerCoins=+{win - winBefore} loserCoins=+{lose - loseBefore} " +
                   $"hits={results.First(r => r.Won).Hits}/{results.First(r => r.Won).Shots}";
        }
    }
}
