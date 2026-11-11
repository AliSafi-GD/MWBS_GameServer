using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Game.Arena
{
    public sealed class ArenaPlayer
    {
        public int Slot;
        public string Name;
        public string Skin;
        public bool Online;
    }

    public struct ArenaTank
    {
        public int Slot;
        public float X, Y, Aim;
        public int Hp;
        public long Seq;
    }

    public struct ArenaBullet
    {
        public float X, Y;
        public int Owner;
    }

    public sealed class ArenaResult
    {
        public int Slot, Hp, Hits, Shots, Coins;
        public bool Won;
    }

    /// <summary>
    /// What the client knows about the match, built only from server messages (see ArenaMatch.cs on the server).
    /// No Unity types, so a bot or a test can use it too. Apply() may run on any thread; lock <see cref="Sync"/> to read.
    /// </summary>
    public sealed class ArenaState
    {
        public const int PhaseWaiting = 0, PhaseCountdown = 1, PhasePlaying = 2, PhaseOver = 3;

        public readonly object Sync = new object();

        public int You = -1;
        public float Width = 800, Height = 500, Radius = 16;
        public int MaxHp = 3;
        public readonly List<float[]> Walls = new List<float[]>();
        public readonly Dictionary<int, ArenaPlayer> Players = new Dictionary<int, ArenaPlayer>();
        public readonly List<ArenaTank> Tanks = new List<ArenaTank>();
        public readonly List<ArenaBullet> Bullets = new List<ArenaBullet>();
        public int Phase;
        public float Time;
        public long Tick;

        public bool IsOver;
        public int Winner = -1;
        public string Reason;
        public readonly List<ArenaResult> Results = new List<ArenaResult>();

        /// <summary>Raised for events worth telling the player: the message type ("hit", "left", "chat", "warn") and a sentence.</summary>
        public event Action<string, string> Notice;

        public bool HasInit => You >= 0;

        public bool TryGetTank(int slot, out ArenaTank tank)
        {
            foreach (var t in Tanks)
            {
                if (t.Slot == slot)
                {
                    tank = t;
                    return true;
                }
            }

            tank = default(ArenaTank);
            return false;
        }

        public string NameOf(int slot) => Players.TryGetValue(slot, out var p) && !string.IsNullOrEmpty(p.Name) ? p.Name : "P" + (slot + 1);

        public void Apply(byte[] data)
        {
            JObject msg;
            try
            {
                msg = JObject.Parse(Encoding.UTF8.GetString(data));
            }
            catch (Exception)
            {
                return;
            }

            string notice = null;
            var kind = (string)msg["t"];
            lock (Sync)
            {
                switch (kind)
                {
                    case "init":
                        You = (int)msg["you"];
                        Width = (float)msg["w"];
                        Height = (float)msg["h"];
                        Radius = (float)msg["r"];
                        MaxHp = (int)msg["hp"];
                        Walls.Clear();
                        foreach (var w in (JArray)msg["walls"])
                            Walls.Add(new[] { (float)w[0], (float)w[1], (float)w[2], (float)w[3] });
                        break;

                    case "players":
                        foreach (var p in (JArray)msg["list"])
                        {
                            var slot = (int)p["slot"];
                            if (!Players.TryGetValue(slot, out var player))
                                Players[slot] = player = new ArenaPlayer { Slot = slot };
                            player.Name = (string)p["name"];
                            player.Skin = (string)p["skin"];
                            player.Online = (bool)p["online"];
                        }
                        break;

                    case "s":
                        Tick = (long)msg["k"];
                        Phase = (int)msg["ph"];
                        Time = (float)msg["tm"];
                        Tanks.Clear();
                        foreach (var t in (JArray)msg["p"])
                            Tanks.Add(new ArenaTank { Slot = (int)t[0], X = (float)t[1], Y = (float)t[2], Aim = (float)t[3], Hp = (int)t[4], Seq = (long)t[5] });
                        Bullets.Clear();
                        foreach (var b in (JArray)msg["b"])
                            Bullets.Add(new ArenaBullet { X = (float)b[0], Y = (float)b[1], Owner = (int)b[2] });
                        break;

                    case "hit":
                        notice = NameOf((int)msg["slot"]) + " hit by " + NameOf((int)msg["by"]) + ", hp " + (int)msg["hp"];
                        break;

                    case "left":
                        notice = NameOf((int)msg["slot"]) + " disconnected";
                        break;

                    case "chat":
                        notice = NameOf((int)msg["from"]) + ": " + (string)msg["text"];
                        break;

                    case "warn":
                        notice = "Server: " + (string)msg["msg"];
                        break;

                    case "over":
                        IsOver = true;
                        Phase = PhaseOver;
                        Winner = (int)msg["winner"];
                        Reason = (string)msg["reason"];
                        Results.Clear();
                        foreach (var r in (JArray)msg["results"])
                        {
                            Results.Add(new ArenaResult
                            {
                                Slot = (int)r["slot"],
                                Hp = (int)r["hp"],
                                Hits = (int)r["hits"],
                                Shots = (int)r["shots"],
                                Coins = (int)r["coins"],
                                Won = (bool)r["won"]
                            });
                        }
                        break;
                }
            }

            if (notice != null)
                Notice?.Invoke(kind, notice);
        }
    }
}
