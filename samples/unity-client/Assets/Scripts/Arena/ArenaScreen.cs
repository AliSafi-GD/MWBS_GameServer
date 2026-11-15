using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Game.Core;
using Game.Meta;
using GameServer.Client;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Arena
{
    /// <summary>
    /// The 2D Tank Arena client. The server runs the whole game; this scene sends input (move, aim, fire) at 20 Hz and
    /// draws the snapshots it gets back. Own movement is not predicted yet, so it feels exactly like the connection is.
    /// </summary>
    public sealed class ArenaScreen : MonoBehaviour
    {
        private const float Scale = 0.01f;            // server units -> Unity units
        private const float SendInterval = 0.05f;     // 20 Hz
        private const float Smoothing = 18f;
        private const int MaxReconnects = 3;

        private static readonly Color MyColor = Theme.Hex(0x4AA8FF);
        private static readonly Color EnemyColor = Theme.Hex(0xFF5A50);
        private static readonly Color Ink = Theme.Hex(0x0A1136);

        private sealed class TankView
        {
            public GameObject Root;
            public Transform Hull;
            public Transform Turret;
            public Transform Gun;
            public Transform Marker;
            public SpriteRenderer BarFill;
            /// <summary>Every part painted in the tank's color, with how much lighter (+) or darker (-) it is.</summary>
            public readonly List<(SpriteRenderer Part, float Tint)> Painted = new List<(SpriteRenderer, float)>();
            public Vector2 LastServer;
            public float HullAngle;
            public int Hp = -1;
            public float Flash;
            public float Recoil;
            public float Age;
            public Color Color;
        }

        /// <summary>One short-lived piece of an effect: a ring, a spark, a puff of smoke, a flash.</summary>
        private sealed class Fx
        {
            public Transform Root;
            public SpriteRenderer Picture;
            public Vector3 Velocity;
            public float Age, Life, From, To, Alpha, Drag;
        }

        private readonly ArenaState _state = new ArenaState();
        private readonly Dictionary<int, TankView> _tanks = new Dictionary<int, TankView>();
        private readonly List<Transform> _bullets = new List<Transform>();
        private readonly List<Vector3> _bulletLast = new List<Vector3>();
        private readonly List<Fx> _fx = new List<Fx>();
        private int _bulletCount;
        private readonly Queue<(string Kind, string Text)> _notices = new Queue<(string, string)>();

        private GameSession _session;
        private MatchConnection _connection;
        private MatchConnectInfo _info;
        private bool _worldBuilt;
        private bool _leaving;
        private bool _resultShown;
        private int _reconnects;
        private long _seq;
        private float _sendTimer;
        private float _shake;
        private int _lastCountdown = -1;
        private float _countdownPop;

        private RectTransform _hud;
        private ToastLayer _toasts;
        private Text _center;
        private Text _timer;
        private readonly Text[] _names = new Text[2];
        private readonly Image[][] _hearts = { new Image[3], new Image[3] };
        private readonly RectTransform[] _plates = new RectTransform[2];
        private readonly RectTransform[] _avatars = new RectTransform[2];
        private readonly string[] _shownNames = new string[2];
        private readonly int[] _shownHp = { -1, -1 };
        private Image _timerFill;
        private StickPad _moveStick;
        private FirePad _firePad;
        private Text _ping;
        private GButton _aimToggle;
        private float _pingAt;
        private float _mouseAimUntil;
        private Vector3 _cameraHome;

        private void Start()
        {
            Ui.EnsureEventSystem();
            _session = GameSession.Instance;
            if (_session == null || !_session.IsReady || _session.Play?.Pending == null)
            {
                SceneManager.LoadScene("Menu");
                return;
            }

            _info = _session.Play.Pending;
            _session.Play.Pending = null;

            var cam = Camera.main;
            if (cam != null)
            {
                // Sprites live at z = 0, so the camera must sit in front of them or it clips them away.
                var p = cam.transform.position;
                cam.transform.position = new Vector3(p.x, p.y, -10f);
                cam.orthographic = true;
                cam.backgroundColor = Theme.Hex(0x0C1438);
                _cameraHome = cam.transform.position;
            }

            BuildBackdrop();
            BuildHud();
            _state.Notice += OnNotice;
            GameSettings.Changed += PaintAimToggle;
            Connect();
        }

        private void OnDestroy()
        {
            GameSettings.Changed -= PaintAimToggle;
            Disconnect();
        }

        // ---- connection --------------------------------------------------------------------------------------

        private async void Connect()
        {
            try
            {
                _center.text = "Connecting...";
                var skin = _session.Loadout.SkinForMatch;
                _connection = await _session.Play.ConnectAsync(_info);
                _connection.DataReceived += OnData;
                _connection.Closed += OnClosed;
                Debug.Log("[Arena] connected over " + _connection.Transport);
                if (skin != null)
                    Send(new { t = "hello", skin });
            }
            catch (Exception ex)
            {
                _center.text = Parts.ErrorText(ex);
                Debug.LogWarning("[Arena] connect failed: " + ex);
                ShowResult("COULD NOT JOIN", Theme.Red, null);
            }
        }

        private void OnData(byte[] data) => _state.Apply(data);

        private async void OnClosed(string reason)
        {
            if (_leaving || _state.IsOver)
                return;

            // Dropped mid-match: the server keeps the seat for a while, so ask for a new token and come back.
            while (_reconnects < MaxReconnects && !_leaving && !_state.IsOver)
            {
                _reconnects++;
                _center.text = "Reconnecting " + _reconnects + "/" + MaxReconnects;
                try
                {
                    await Task.Delay(1000);
                    _info = await _session.Play.RejoinAsync(_info.MatchId);
                    _connection = await _session.Play.ConnectAsync(_info);
                    _connection.DataReceived += OnData;
                    _connection.Closed += OnClosed;
                    _center.text = "";
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Arena] reconnect failed: " + ex.Message);
                }
            }

            if (!_state.IsOver && !_leaving)
            {
                _center.text = "";
                ShowResult("DISCONNECTED", Theme.Red, null);
            }
        }

        private async void Disconnect()
        {
            _leaving = true;
            var connection = _connection;
            _connection = null;
            if (connection == null)
                return;

            try
            {
                await connection.CloseAsync();
            }
            catch (Exception)
            {
                // already closed
            }
        }

        private async void Send(object message, MatchDelivery delivery = MatchDelivery.Reliable)
        {
            var connection = _connection;
            if (connection == null || !connection.IsOpen)
                return;

            try
            {
                await connection.SendJsonAsync(message, delivery);
            }
            catch (Exception)
            {
                // the Closed event handles lost connections
            }
        }

        private void OnNotice(string kind, string text)
        {
            if (kind != "hit")
                _notices.Enqueue((kind, text));
        }

        // ---- frame -------------------------------------------------------------------------------------------

        private void Update()
        {
            if (_hud == null)
                return;

            lock (_state.Sync)
            {
                if (_state.HasInit && !_worldBuilt)
                    BuildWorld();

                if (_worldBuilt)
                    Render();
            }

            while (_notices.Count > 0)
            {
                var n = _notices.Dequeue();
                _toasts.Show(n.Text, n.Kind == "warn" ? Theme.Red : Theme.Blue);
            }

            UpdateFx();
            UpdatePing();

            if (_state.IsOver)
                ShowOutcome();
            else
                SendInput();
        }

        private void SendInput()
        {
            _sendTimer -= Time.deltaTime;
            if (_sendTimer > 0 || _connection == null || !_state.HasInit)
                return;

            _sendTimer = SendInterval;

            var kb = Keyboard.current;
            var mouse = Mouse.current;
            float dx = 0, dy = 0;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) dx -= 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) dx += 1;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) dy -= 1;   // server y points down
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) dy += 1;
            }

            if (dx == 0 && dy == 0)
            {
                dx = _moveStick.Value.x;
                dy = -_moveStick.Value.y;
            }

            var overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            var fire = (kb != null && kb.spaceKey.isPressed) || (mouse != null && mouse.leftButton.isPressed && !overUi) || _firePad.Held;

            // A moving mouse means a desktop player aiming with it; otherwise (a phone, the fire button) the gun turns to the enemy by itself.
            if (mouse != null && mouse.delta.ReadValue().sqrMagnitude > 0.5f && !_firePad.Held)
                _mouseAimUntil = Time.unscaledTime + 3f;
            var mouseAim = mouse != null && Time.unscaledTime < _mouseAimUntil && !overUi;

            double aim = 0;
            lock (_state.Sync)
            {
                if (_state.TryGetTank(_state.You, out var me))
                    aim = me.Aim;

                if (mouseAim && Camera.main != null && _tanks.TryGetValue(_state.You, out var view))
                {
                    var world = Camera.main.ScreenToWorldPoint(mouse.position.ReadValue());
                    var d = world - view.Root.transform.position;
                    if (d.sqrMagnitude > 0.0004f)
                        aim = Math.Atan2(-d.y, d.x);        // server angles are measured with y down
                }
                else if (GameSettings.AutoAim)
                {
                    if (_state.TryGetTank(1 - _state.You, out var enemy) && enemy.Hp > 0 && _state.TryGetTank(_state.You, out var self))
                        aim = Math.Atan2(enemy.Y - self.Y, enemy.X - self.X);
                }
                else if (_firePad.Held && _firePad.Aiming)
                {
                    // Manual: the drag on the fire button points the gun; without a drag it stays where it is.
                    aim = Math.Atan2(-_firePad.Aim.y, _firePad.Aim.x);
                }
            }

            Send(new { t = "in", dx, dy, aim = Math.Round(aim, 3), fire, seq = ++_seq }, MatchNet.Input);
        }

        // ---- world -------------------------------------------------------------------------------------------

        private Vector3 ToWorld(float x, float y) => new Vector3((x - _state.Width / 2f) * Scale, (_state.Height / 2f - y) * Scale, 0f);

        private static SpriteRenderer Block(string name, Transform parent, Color color, Vector2 size, int order, bool circle = false)
        {
            var go = new GameObject(name, typeof(SpriteRenderer));
            go.transform.SetParent(parent, false);
            var sr = go.GetComponent<SpriteRenderer>();
            sr.sprite = circle ? Art.CircleUnit() : Art.SquareUnit();
            sr.color = color;
            sr.sortingOrder = order;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            return sr;
        }

        /// <summary>A rounded rectangle of a size in world units; the corners stay round whatever the shape.</summary>
        private static SpriteRenderer Box(string name, Transform parent, Color color, Vector2 size, int order, float radius = 0.04f)
        {
            var go = new GameObject(name, typeof(SpriteRenderer));
            go.transform.SetParent(parent, false);
            var sr = go.GetComponent<SpriteRenderer>();
            sr.sprite = Art.RoundedWorld(Mathf.Min(radius, Mathf.Min(size.x, size.y) * 0.5f));
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = size;
            sr.color = color;
            sr.sortingOrder = order;
            return sr;
        }

        private static SpriteRenderer At(SpriteRenderer sr, float x, float y)
        {
            sr.transform.localPosition = new Vector3(x, y, 0f);
            return sr;
        }

        private void BuildWorld()
        {
            _worldBuilt = true;
            var w = _state.Width * Scale;
            var h = _state.Height * Scale;

            var root = new GameObject("World").transform;
            root.SetParent(transform, false);

            // A steel frame around a grass field.
            At(Box("Shadow", root, new Color(0, 0, 0.03f, 0.45f), new Vector2(w + 0.8f, h + 0.8f), -20, 0.3f), 0.05f, -0.12f);
            Box("FrameRim", root, Ink, new Vector2(w + 0.52f, h + 0.52f), -19, 0.24f);
            Box("Frame", root, Theme.Hex(0x3C4C74), new Vector2(w + 0.42f, h + 0.42f), -18, 0.2f);
            At(Box("FrameLight", root, Theme.Hex(0x6F83B5), new Vector2(w + 0.34f, 0.06f), -17, 0.03f), 0f, h / 2f + 0.15f);
            Box("FrameInner", root, Ink, new Vector2(w + 0.1f, h + 0.1f), -16, 0.08f);
            Block("Grass", root, Theme.Hex(0x58A84C), new Vector2(w, h), -15);

            var tile = 0.5f;
            var cols = Mathf.RoundToInt(w / tile);
            var rows = Mathf.RoundToInt(h / tile);
            for (var cx = 0; cx < cols; cx++)
            {
                for (var cy = (cx + 1) % 2; cy < rows; cy += 2)
                    At(Block("Tile", root, Theme.Hex(0x4F9D45), new Vector2(tile, tile), -14), -w / 2f + tile * (cx + 0.5f), -h / 2f + tile * (cy + 0.5f));
            }

            // Tufts and pebbles, the same every match.
            var random = new System.Random(11);
            for (var i = 0; i < 46; i++)
            {
                var size = 0.05f + (float)random.NextDouble() * 0.07f;
                var dark = i % 3 != 0;
                var dot = Block("Tuft", root, dark ? new Color(0.16f, 0.42f, 0.17f, 0.5f) : new Color(0.8f, 0.95f, 0.6f, 0.35f), new Vector2(size, size * 0.8f), -13, true);
                At(dot, ((float)random.NextDouble() - 0.5f) * (w - 0.3f), ((float)random.NextDouble() - 0.5f) * (h - 0.3f));
            }

            // Arena markings and a soft light in the middle.
            Block("MidLine", root, new Color(1, 1, 1, 0.13f), new Vector2(0.05f, h), -12);
            Block("MidRing", root, new Color(1, 1, 1, 0.16f), new Vector2(1.7f, 1.7f), -12, true).sprite = Art.RingUnit();
            Block("MidDot", root, new Color(1, 1, 1, 0.16f), new Vector2(0.16f, 0.16f), -12, true);
            Block("Light", root, new Color(1f, 1f, 0.8f, 0.16f), new Vector2(w * 1.25f, h * 1.6f), -11, true).sprite = Art.GlowUnit();

            // The frame casts a thin shadow onto the grass.
            At(Block("EdgeTop", root, new Color(0, 0.05f, 0, 0.22f), new Vector2(w, 0.1f), -10), 0f, h / 2f - 0.05f);
            At(Block("EdgeLeft", root, new Color(0, 0.05f, 0, 0.12f), new Vector2(0.07f, h), -10), -w / 2f + 0.035f, 0f);
            At(Block("EdgeRight", root, new Color(0, 0.05f, 0, 0.12f), new Vector2(0.07f, h), -10), w / 2f - 0.035f, 0f);

            foreach (var wall in _state.Walls)
            {
                var size = new Vector2(wall[2] * Scale, wall[3] * Scale);
                var pos = ToWorld(wall[0] + wall[2] / 2f, wall[1] + wall[3] / 2f);
                var lift = Mathf.Min(0.07f, size.y * 0.3f);

                // A stone block seen slightly from the front: its dark side shows under the lighter top.
                At(Box("WallShadow", root, new Color(0, 0.04f, 0, 0.4f), size + new Vector2(0.1f, 0.08f), -8, 0.07f), pos.x + 0.05f, pos.y - 0.09f);
                At(Box("WallRim", root, Ink, size + new Vector2(0.07f, 0.07f), -7, 0.07f), pos.x, pos.y);
                At(Box("WallSide", root, Theme.Hex(0x4D5878), size, -6, 0.05f), pos.x, pos.y);
                At(Box("WallTop", root, Theme.Hex(0x8B96B8), new Vector2(size.x, size.y - lift), -5, 0.05f), pos.x, pos.y + lift / 2f);
                At(Box("WallLight", root, Theme.Hex(0xAEB8D6), new Vector2(Mathf.Max(0.02f, size.x - 0.08f), Mathf.Max(0.02f, size.y - lift - 0.08f)), -4, 0.03f), pos.x, pos.y + lift / 2f);
            }
        }

        private TankView MakeTank(int slot)
        {
            var mine = slot == _state.You;
            var view = new TankView { Color = mine ? MyColor : EnemyColor };

            view.Root = new GameObject("Tank" + slot);
            view.Root.transform.SetParent(transform, false);
            var root = view.Root.transform;

            At(Block("Shadow", root, new Color(0, 0.03f, 0, 0.5f), new Vector2(0.78f, 0.6f), 0, true), 0.03f, -0.08f).sprite = Art.GlowUnit();

            if (mine)
            {
                // So you never lose your own tank: a slow pulsing ring under it.
                var marker = Block("Marker", root, new Color(1, 1, 1, 0.6f), new Vector2(0.74f, 0.74f), 0, true);
                marker.sprite = Art.RingUnit();
                view.Marker = marker.transform;
            }

            view.Hull = new GameObject("Hull").transform;
            view.Hull.SetParent(root, false);
            for (var side = -1; side <= 1; side += 2)
            {
                At(Box("TreadRim", view.Hull, Ink, new Vector2(0.44f, 0.135f), 1, 0.05f), 0f, side * 0.15f);
                At(Box("Tread", view.Hull, Theme.Hex(0x232A3D), new Vector2(0.4f, 0.095f), 2, 0.035f), 0f, side * 0.15f);
                for (var i = 0; i < 6; i++)
                    At(Block("Link", view.Hull, Theme.Hex(0x4A556F), new Vector2(0.024f, 0.07f), 3), -0.15f + i * 0.06f, side * 0.15f);
            }

            Box("BodyRim", view.Hull, Ink, new Vector2(0.38f, 0.3f), 4, 0.07f);
            view.Painted.Add((Box("Body", view.Hull, view.Color, new Vector2(0.335f, 0.255f), 5, 0.05f), 0f));
            view.Painted.Add((At(Box("BodyShade", view.Hull, view.Color, new Vector2(0.335f, 0.09f), 6, 0.04f), 0f, -0.082f), -0.28f));
            At(Box("BodyGloss", view.Hull, new Color(1, 1, 1, 0.32f), new Vector2(0.28f, 0.04f), 7, 0.02f), 0f, 0.085f);
            view.Painted.Add((At(Box("Front", view.Hull, view.Color, new Vector2(0.035f, 0.17f), 7, 0.015f), 0.135f, 0f), 0.3f));
            for (var i = 0; i < 2; i++)
                view.Painted.Add((At(Block("Vent", view.Hull, view.Color, new Vector2(0.014f, 0.11f), 7), -0.13f + i * 0.03f, 0f), -0.45f));

            view.Turret = new GameObject("Turret").transform;
            view.Turret.SetParent(root, false);
            view.Gun = new GameObject("Gun").transform;
            view.Gun.SetParent(view.Turret, false);
            At(Box("BarrelRim", view.Gun, Ink, new Vector2(0.3f, 0.115f), 8, 0.03f), 0.17f, 0f);
            At(Box("Barrel", view.Gun, Theme.Hex(0x5D6885), new Vector2(0.265f, 0.07f), 9, 0.02f), 0.17f, 0f);
            At(Box("BarrelLight", view.Gun, Theme.Hex(0x8C98B6), new Vector2(0.24f, 0.018f), 10, 0.008f), 0.17f, 0.018f);
            At(Box("MuzzleRim", view.Gun, Ink, new Vector2(0.085f, 0.155f), 8, 0.03f), 0.31f, 0f);
            At(Box("Muzzle", view.Gun, Theme.Hex(0x3B445C), new Vector2(0.05f, 0.115f), 9, 0.02f), 0.31f, 0f);

            Block("TurretRim", view.Turret, Ink, new Vector2(0.27f, 0.27f), 11, true);
            view.Painted.Add((Block("TurretBase", view.Turret, view.Color, new Vector2(0.225f, 0.225f), 12, true), -0.2f));
            view.Painted.Add((At(Block("TurretTop", view.Turret, view.Color, new Vector2(0.185f, 0.185f), 13, true), 0f, 0.012f), 0.12f));
            view.Painted.Add((Block("Hatch", view.Turret, view.Color, new Vector2(0.095f, 0.095f), 14, true), -0.38f));
            view.Painted.Add((At(Block("Cap", view.Turret, view.Color, new Vector2(0.06f, 0.06f), 15, true), 0f, 0.008f), 0.5f));

            At(Box("BarRim", root, Ink, new Vector2(0.52f, 0.105f), 20, 0.05f), 0f, 0.42f);
            At(Box("BarBack", root, Theme.Hex(0x1A2450), new Vector2(0.47f, 0.06f), 21, 0.03f), 0f, 0.42f);
            view.BarFill = At(Box("BarFill", root, Theme.Green, new Vector2(0.45f, 0.04f), 22, 0.02f), 0f, 0.42f);
            return view;
        }

        private static void Paint(TankView view)
        {
            foreach (var (part, tint) in view.Painted)
                part.color = view.Flash > 0 ? Color.white : Ui.Tint(view.Color, tint);
        }

        private void Render()
        {
            var smooth = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);

            foreach (var t in _state.Tanks)
            {
                if (!_tanks.TryGetValue(t.Slot, out var view))
                {
                    view = MakeTank(t.Slot);
                    view.Root.transform.position = ToWorld(t.X, t.Y);
                    view.LastServer = new Vector2(t.X, t.Y);
                    // Both tanks start facing the middle of the field.
                    view.HullAngle = t.X > _state.Width / 2f ? 180f : 0f;
                    view.Hull.localRotation = Quaternion.Euler(0, 0, view.HullAngle);
                    _tanks[t.Slot] = view;
                }

                // A tank wears its owner's skin when it has one (the server only lets owned skins through), else its team color.
                var skin = _state.Players.TryGetValue(t.Slot, out var info) ? info.Skin : null;
                view.Color = Skins.ColorOf(skin) ?? (t.Slot == _state.You ? MyColor : EnemyColor);

                var target = ToWorld(t.X, t.Y);
                view.Root.transform.position = Vector3.Lerp(view.Root.transform.position, target, smooth);
                view.Turret.localRotation = Quaternion.Euler(0, 0, -t.Aim * Mathf.Rad2Deg);

                var move = new Vector2(t.X, t.Y) - view.LastServer;
                if (move.sqrMagnitude > 0.5f)
                {
                    var wanted = Mathf.Atan2(-move.y, move.x) * Mathf.Rad2Deg;
                    view.HullAngle = Mathf.LerpAngle(view.HullAngle, wanted, smooth);
                    view.Hull.localRotation = Quaternion.Euler(0, 0, view.HullAngle);
                }

                view.LastServer = new Vector2(t.X, t.Y);

                var hpT = Mathf.Clamp01(t.Hp / (float)Mathf.Max(_state.MaxHp, 1));
                view.BarFill.size = new Vector2(Mathf.Max(0.04f, 0.45f * hpT), 0.04f);
                view.BarFill.transform.localPosition = new Vector3(-0.225f + 0.225f * hpT, 0.42f, 0);
                view.BarFill.color = hpT > 0.5f ? Theme.Green : (hpT > 0.3f ? Theme.Gold : Theme.Red);

                if (view.Hp >= 0 && t.Hp < view.Hp)
                {
                    view.Flash = 0.14f;
                    var at = view.Root.transform.position;
                    if (t.Hp <= 0)
                    {
                        SpawnExplosion(at, view.Color);
                        _shake = 0.5f;
                    }
                    else
                    {
                        SpawnHit(at, view.Color);
                        _shake = Mathf.Max(_shake, t.Slot == _state.You ? 0.28f : 0.14f);
                    }
                }

                view.Hp = t.Hp;
                view.Root.SetActive(t.Hp > 0);
                view.Flash = Mathf.Max(0, view.Flash - Time.deltaTime);
                Paint(view);

                // Pop in when the tank first appears, kick the gun back after a shot, pulse the marker.
                view.Age += Time.deltaTime;
                var pop = Mathf.LerpUnclamped(0.3f, 1f, Anim.EaseOutBack(Mathf.Clamp01(view.Age / 0.35f)));
                view.Root.transform.localScale = new Vector3(pop, pop, 1f);
                view.Recoil = Mathf.MoveTowards(view.Recoil, 0f, Time.deltaTime * 6f);
                view.Gun.localPosition = new Vector3(-0.07f * view.Recoil, 0f, 0f);
                if (view.Marker != null)
                    view.Marker.localScale = Vector3.one * (0.74f + 0.05f * Mathf.Sin(Time.time * 4f));
            }

            RenderBullets();
            RenderHud();
        }

        private void RenderBullets()
        {
            var count = _state.Bullets.Count;
            while (_bullets.Count < count)
            {
                var b = new GameObject("Bullet").transform;
                b.SetParent(transform, false);
                At(Block("Tail", b, new Color(1f, 0.75f, 0.2f, 0.55f), new Vector2(0.6f, 0.16f), 23, true), -0.2f, 0f).sprite = Art.GlowUnit();
                Block("Glow", b, new Color(1f, 0.8f, 0.3f, 0.6f), new Vector2(0.38f, 0.38f), 23, true).sprite = Art.GlowUnit();
                Block("Rim", b, Theme.Hex(0x7A3B00), new Vector2(0.15f, 0.15f), 24, true);
                Block("Core", b, Theme.Hex(0xFFD23F), new Vector2(0.115f, 0.115f), 25, true);
                Block("Hot", b, Theme.Hex(0xFFF6C8), new Vector2(0.06f, 0.06f), 26, true);
                _bullets.Add(b);
                _bulletLast.Add(Vector3.zero);
            }

            for (var i = 0; i < _bullets.Count; i++)
            {
                var active = i < count;
                var wasActive = _bullets[i].gameObject.activeSelf && i < _bulletCount;
                _bullets[i].gameObject.SetActive(active);
                if (!active)
                    continue;

                var bullet = _state.Bullets[i];
                var at = ToWorld(bullet.X, bullet.Y);
                var step = at - _bulletLast[i];
                if (wasActive && step.sqrMagnitude > 0.000001f && step.sqrMagnitude < 0.5f)
                {
                    _bullets[i].rotation = Quaternion.Euler(0, 0, Mathf.Atan2(step.y, step.x) * Mathf.Rad2Deg);
                }
                else if (!wasActive && _tanks.TryGetValue(bullet.Owner, out var owner))
                {
                    // A bullet that was not there last frame was just fired: flash at the muzzle and kick the gun.
                    _bullets[i].rotation = owner.Turret.rotation;
                    owner.Recoil = 1f;
                    SpawnMuzzle(at, owner.Turret.right);
                }

                _bullets[i].position = at;
                _bulletLast[i] = at;
            }

            _bulletCount = count;
        }

        // ---- effects -----------------------------------------------------------------------------------------

        private void AddFx(Sprite sprite, Vector3 at, Color color, float from, float to, float life, Vector3 velocity = default(Vector3), float drag = 0f, float delay = 0f, int order = 30)
        {
            var sr = Block("Fx", transform, color, Vector2.one * from, order, true);
            sr.sprite = sprite;
            sr.transform.position = at;
            sr.transform.rotation = Quaternion.Euler(0, 0, UnityEngine.Random.value * 360f);
            sr.enabled = delay <= 0f;
            _fx.Add(new Fx { Root = sr.transform, Picture = sr, Velocity = velocity, Age = -delay, Life = life, From = from, To = to, Alpha = color.a, Drag = drag });
        }

        private void Sparks(Vector3 at, Color color, int count, float speed, float life)
        {
            for (var i = 0; i < count; i++)
            {
                var dir = (Vector3)(UnityEngine.Random.insideUnitCircle.normalized * speed * UnityEngine.Random.Range(0.45f, 1f));
                AddFx(Art.SquareUnit(), at, i % 2 == 0 ? color : Theme.Hex(0xFFF2B0), 0.07f, 0.02f, life * UnityEngine.Random.Range(0.7f, 1f), dir, 4f);
            }
        }

        private void SpawnHit(Vector3 at, Color color)
        {
            AddFx(Art.GlowUnit(), at, new Color(1f, 0.95f, 0.7f, 0.9f), 0.5f, 1.3f, 0.18f);
            AddFx(Art.RingUnit(), at, new Color(1, 1, 1, 0.9f), 0.3f, 1.2f, 0.32f);
            Sparks(at, Ui.Tint(color, 0.3f), 10, 3.2f, 0.4f);
        }

        private void SpawnExplosion(Vector3 at, Color color)
        {
            AddFx(Art.GlowUnit(), at, new Color(1f, 0.9f, 0.6f, 1f), 0.8f, 3.2f, 0.3f);
            AddFx(Art.RingUnit(), at, new Color(1, 1, 1, 0.95f), 0.4f, 2.6f, 0.45f);
            AddFx(Art.RingUnit(), at, new Color(1f, 0.75f, 0.25f, 0.9f), 0.3f, 1.9f, 0.5f, default(Vector3), 0f, 0.08f);
            for (var i = 0; i < 7; i++)
            {
                var offset = (Vector3)(UnityEngine.Random.insideUnitCircle * 0.22f);
                AddFx(Art.CircleUnit(), at + offset, new Color(1f, 0.6f + UnityEngine.Random.value * 0.3f, 0.15f, 0.95f), 0.18f, 0.55f, 0.35f, offset * 3f, 3f, i * 0.02f, 31);
                AddFx(Art.CircleUnit(), at + offset, new Color(0.2f, 0.22f, 0.3f, 0.6f), 0.25f, 0.75f, 0.9f, offset * 2.2f + Vector3.up * 0.25f, 1.5f, 0.1f + i * 0.03f, 29);
            }

            Sparks(at, Ui.Tint(color, 0.2f), 18, 5f, 0.6f);
        }

        private void SpawnMuzzle(Vector3 at, Vector3 forward)
        {
            AddFx(Art.GlowUnit(), at, new Color(1f, 0.9f, 0.5f, 0.95f), 0.45f, 0.75f, 0.1f);
            for (var i = 0; i < 4; i++)
            {
                var spread = Quaternion.Euler(0, 0, UnityEngine.Random.Range(-28f, 28f)) * forward;
                AddFx(Art.SquareUnit(), at, Theme.Hex(0xFFE9A0), 0.06f, 0.02f, 0.2f, spread * UnityEngine.Random.Range(1.5f, 3f), 5f);
            }
        }

        /// <summary>Keeps the whole field in view whatever the window shape (rotating a phone, resizing the editor).</summary>
        private void FitCamera()
        {
            var cam = Camera.main;
            if (cam == null || !_worldBuilt || cam.pixelHeight <= 0)
                return;

            var aspect = Mathf.Max((float)cam.pixelWidth / cam.pixelHeight, 0.2f);
            var w = _state.Width * Scale;
            var h = _state.Height * Scale;
            cam.orthographicSize = Mathf.Max(h / 2f + 0.95f, (w / 2f + 0.36f) / aspect);
        }

        private void UpdateFx()
        {
            FitCamera();
            for (var i = _fx.Count - 1; i >= 0; i--)
            {
                var f = _fx[i];
                f.Age += Time.deltaTime;
                if (f.Age < 0f)
                    continue;

                var t = f.Age / f.Life;
                if (t >= 1f)
                {
                    Destroy(f.Root.gameObject);
                    _fx.RemoveAt(i);
                    continue;
                }

                f.Picture.enabled = true;
                f.Root.position += f.Velocity * Time.deltaTime;
                f.Velocity *= Mathf.Exp(-f.Drag * Time.deltaTime);
                f.Root.localScale = Vector3.one * Mathf.Lerp(f.From, f.To, Anim.EaseOut(t));
                var c = f.Picture.color;
                f.Picture.color = new Color(c.r, c.g, c.b, f.Alpha * (1f - t * t));
            }

            var cam = Camera.main;
            if (cam != null)
            {
                _shake = Mathf.MoveTowards(_shake, 0f, Time.deltaTime * 0.9f);
                cam.transform.position = _cameraHome + (Vector3)(UnityEngine.Random.insideUnitCircle * _shake * 0.4f);
            }
        }

        // ---- hud ---------------------------------------------------------------------------------------------

        private void BuildBackdrop()
        {
            var canvas = Ui.CreateCanvas("ArenaBackdrop", -20);
            canvas.transform.SetParent(transform, false);
            Backdrop.Create(canvas.transform, Theme.Hex(0x1F3F96), Theme.Hex(0x080D2E), 10);
        }

        private void BuildHud()
        {
            // Above every world sprite (their sorting orders go up to the 30s).
            var canvas = Ui.CreateCanvas("ArenaHud", 100);
            canvas.transform.SetParent(transform, false);
            _hud = Ui.NewRect(canvas.transform, "Hud");
            _toasts = ToastLayer.Create(_hud);

            BuildPlate(true);
            BuildPlate(false);

            var timer = Ui.Panel(_hud, Theme.Outline, 46f, "Timer", true);
            Ui.Pin(timer, 0.5f, 1f, 170, 100, 0, -26);
            var timerEdge = Ui.Panel(timer.transform, Theme.Gold, 42f, "Edge");
            Ui.Inset(timerEdge, 4, 4, 4, 4);
            _timerFill = Ui.Panel(timer.transform, Theme.Hex(0x16286B), 38f, "Fill");
            Ui.Inset(_timerFill, 9, 9, 9, 9);
            _timer = Ui.Label(timer.transform, "VS", 54);
            Ui.Stretch(_timer.rectTransform);

            _center = Ui.Headline(_hud, "", 260);
            Ui.Pin(_center, 0.5f, 0.5f, 1400, 360);

            _moveStick = StickPad.Create(_hud, Theme.Blue);
            Ui.Pin(_moveStick, 0f, 0f, 360, 360, 70, 70);
            _firePad = FirePad.Create(_hud);
            Ui.Pin(_firePad, 1f, 0f, 300, 300, -90, 100);

            _aimToggle = GButton.Create(_hud, "AUTO AIM", Theme.Green, Theme.GreenLip, 32, 8);
            Ui.Pin(_aimToggle, 1f, 0f, 300, 78, -90, 490);
            _aimToggle.Button.onClick.AddListener(() => GameSettings.AutoAim = !GameSettings.AutoAim);
            PaintAimToggle();

            _ping = Ui.Label(_hud, "", 38, Color.white);
            Ui.Pin(_ping, 0.5f, 1f, 460, 56, 0, -142);
        }

        private void PaintAimToggle()
        {
            var auto = GameSettings.AutoAim;
            _aimToggle.SetText(auto ? "AUTO AIM" : "MANUAL AIM");
            _aimToggle.SetColors(auto ? Theme.Green : Theme.Blue, auto ? Theme.GreenLip : Theme.BlueLip);
            _firePad.Manual = !auto;
        }

        /// <summary>"UDP  32 ms" under the timer, green when good, red when the connection is bad. Refreshed twice a second.</summary>
        private void UpdatePing()
        {
            var show = GameSettings.ShowPing;
            _ping.gameObject.SetActive(show);
            if (!show || Time.unscaledTime - _pingAt < 0.5f)
                return;

            _pingAt = Time.unscaledTime;
            var connection = _connection;
            if (connection == null || !connection.IsOpen)
            {
                _ping.text = "OFFLINE";
                _ping.color = Theme.Red;
                return;
            }

            var tag = connection.Transport == "udp" ? "UDP" : "WS";
            var ms = connection.RoundTripMs;
            if (ms < 0)
            {
                _ping.text = tag + "  -- ms";
                _ping.color = Theme.Muted;
                return;
            }

            _ping.text = tag + "  " + ms + " ms";
            _ping.color = ms < 60 ? Theme.Green : (ms < 120 ? Theme.Gold : Theme.Red);
        }

        /// <summary>A player card in a top corner: avatar, name and one heart per hit point.</summary>
        private void BuildPlate(bool left)
        {
            var index = left ? 0 : 1;
            var tint = left ? Theme.Blue : Theme.Red;
            var rim = Ui.Panel(_hud, Theme.Outline, 48f, left ? "PlateMe" : "PlateEnemy", true);
            Ui.Pin(rim, left ? 0f : 1f, 1f, 414, 150, left ? 16 : -16, -20);
            var edge = Ui.Panel(rim.transform, tint, 44f, "Edge");
            Ui.Inset(edge, 4, 4, 4, 4);
            var plate = Ui.Panel(rim.transform, Theme.Hex(0x16286B), 40f, "Fill");
            Ui.Inset(plate, 9, 9, 9, 9);
            _plates[index] = plate.rectTransform;

            _names[index] = Ui.Label(plate.transform, left ? "YOU" : "ENEMY", 40, Color.white, left ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight);
            Ui.Place(_names[index], left ? 0.32f : 0.04f, 0.5f, left ? 0.96f : 0.68f, 0.96f);

            for (var i = 0; i < 3; i++)
            {
                var heart = Ui.IconPic(plate.transform, Icon.Heart, Theme.Red);
                Ui.Pin(heart, left ? 0f : 1f, 0.28f, 50, 50, left ? 132 + i * 58 : -132 - i * 58, 0);
                heart.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                heart.rectTransform.anchoredPosition += new Vector2(left ? 25 : -25, 0);
                _hearts[index][i] = heart;
            }
        }

        private void RenderHud()
        {
            foreach (var t in _state.Tanks)
            {
                var plate = t.Slot == _state.You ? 0 : 1;
                var name = _state.NameOf(t.Slot);
                if (name != _shownNames[plate])
                {
                    _shownNames[plate] = name;
                    _names[plate].text = name.ToUpperInvariant();
                    if (_avatars[plate] != null)
                        Destroy(_avatars[plate].gameObject);
                    _avatars[plate] = Ui.Avatar(_plates[plate], name, 104);
                    Ui.Pin(_avatars[plate], plate == 0 ? 0f : 1f, 0.5f, 104, 104, plate == 0 ? 10 : -10, 0);
                }

                for (var i = 0; i < 3; i++)
                {
                    var heart = _hearts[plate][i];
                    heart.color = i < t.Hp ? Theme.Red : new Color(0.12f, 0.17f, 0.4f, 1f);
                    // The heart that was just lost jumps.
                    if (_shownHp[plate] > t.Hp && i >= t.Hp && i < _shownHp[plate])
                    {
                        var rt = heart.rectTransform;
                        Anim.Run(this, 0.4f, k => rt.localScale = Vector3.one * (1f + Mathf.Sin(k * Mathf.PI) * 0.7f));
                    }
                }

                _shownHp[plate] = t.Hp;
            }

            var hurry = _state.Phase == ArenaState.PhasePlaying && _state.Time <= 10f;
            _timer.color = hurry ? Theme.Hex(0xFF7A70) : Color.white;
            _timerFill.color = hurry ? Theme.Hex(0x5A1626) : Theme.Hex(0x16286B);
            _timer.rectTransform.localScale = Vector3.one * (hurry ? 1f + 0.08f * Mathf.Abs(Mathf.Sin(Time.time * 6f)) : 1f);

            switch (_state.Phase)
            {
                case ArenaState.PhaseWaiting:
                    _center.text = "";
                    _timer.text = "VS";
                    NudgeWaiting();
                    break;
                case ArenaState.PhaseCountdown:
                {
                    var n = Mathf.Max(1, Mathf.CeilToInt(_state.Time));
                    if (n != _lastCountdown)
                    {
                        _lastCountdown = n;
                        _countdownPop = 0f;
                    }

                    _countdownPop = Mathf.Min(1f, _countdownPop + Time.deltaTime * 4f);
                    _center.text = n.ToString();
                    _center.rectTransform.localScale = Vector3.one * Mathf.LerpUnclamped(1.8f, 1f, Anim.EaseOutBack(_countdownPop));
                    _timer.text = "READY";
                    break;
                }

                case ArenaState.PhasePlaying:
                    if (_lastCountdown != 0)
                    {
                        _lastCountdown = 0;
                        _center.text = "FIGHT!";
                        _center.rectTransform.localScale = Vector3.one * 1.3f;
                        Anim.Run(this, 0.7f, t =>
                        {
                            if (_center != null && _lastCountdown == 0 && !_state.IsOver)
                                _center.color = new Color(1, 1, 1, 1f - t);
                        }, () =>
                        {
                            if (_center != null && !_state.IsOver)
                            {
                                _center.text = "";
                                _center.color = Color.white;
                            }
                        });
                    }

                    _timer.text = Mathf.CeilToInt(_state.Time).ToString();
                    break;
            }
        }

        private float _waitingToastAt = -100f;

        private void NudgeWaiting()
        {
            if (Time.unscaledTime - _waitingToastAt > 12f && _state.Tanks.Count < 2)
            {
                _waitingToastAt = Time.unscaledTime;
                _toasts.Show("Waiting for your opponent...", Theme.Blue, Icon.Swords);
            }
        }

        // ---- result ------------------------------------------------------------------------------------------

        private void ShowOutcome()
        {
            if (_resultShown)
                return;

            ArenaResult mine;
            lock (_state.Sync)
                mine = _state.Results.FirstOrDefault(r => r.Slot == _state.You);

            var draw = _state.Winner < 0;
            var won = _state.Winner == _state.You;
            ShowResult(draw ? "DRAW" : won ? "VICTORY!" : "DEFEAT", draw ? Theme.Muted : won ? Theme.Gold : Theme.Red, mine);
            RefreshProgressSoon();
        }

        private void ShowResult(string title, Color titleColor, ArenaResult mine)
        {
            if (_resultShown)
                return;

            _resultShown = true;
            _center.text = "";
            var modal = Modal.Create(_hud, "RESULT", 900, 900, false);
            var won = mine != null && mine.Won;

            if (won)
            {
                var rays = Ui.Pic(modal.Body, Art.Rays(), new Color(1f, 0.85f, 0.35f, 0.4f), "Rays");
                Ui.Pin(rays, 0.5f, 0.78f, 900, 900);
                rays.gameObject.AddComponent<Spin>().DegreesPerSecond = 20f;
                for (var i = 0; i < 5; i++)
                {
                    var sparkle = Ui.Pic(modal.Body, Art.Sparkle(), Theme.Hex(0xFFF0B0), "Sparkle");
                    Ui.Pin(sparkle, 0.5f, 0.78f, 50 + i * 8, 50 + i * 8, -340 + i * 170, i % 2 == 0 ? 90 : -70);
                    sparkle.gameObject.AddComponent<Twinkle>().Phase = i * 1.3f;
                }
            }

            var glow = Ui.Pic(modal.Body, Art.Glow(), new Color(titleColor.r, titleColor.g, titleColor.b, 0.35f), "Glow");
            Ui.Pin(glow, 0.5f, 0.78f, 760, 420);
            glow.preserveAspect = false;

            var big = Ui.Headline(modal.Body, title, 130, Ui.Tint(titleColor, 0.7f), titleColor);
            Ui.Place(big, 0f, 0.62f, 1f, 0.96f);
            Anim.Run(this, 0.45f, t => big.rectTransform.localScale = Vector3.one * Mathf.LerpUnclamped(2.2f, 1f, Anim.EaseOutBack(t)));

            if (mine != null)
            {
                var card = Parts.Card(modal.Body, Theme.PanelDark, 30f);
                Ui.Place(card.parent.GetComponent<Image>(), 0.06f, 0.27f, 0.94f, 0.6f);

                var accuracy = mine.Shots > 0 ? Mathf.RoundToInt(100f * mine.Hits / mine.Shots) : 0;
                Stat(card, 0f, "HITS", mine.Hits.ToString());
                Stat(card, 1f / 3f, "SHOTS", mine.Shots.ToString());
                Stat(card, 2f / 3f, "ACCURACY", accuracy + "%");

                var chip = Chip.Create(card, Icon.Coin, Theme.Gold);
                Ui.Pin(chip, 0.5f, 0.24f, 340, 84);
                chip.Set(0, false);
                chip.Set(mine.Coins, true);
            }

            var go = GButton.Create(modal.Body, "CONTINUE", Theme.Gold, Theme.GoldLip, 64, 16);
            Ui.Place(go, 0.1f, 0f, 0.9f, 0.22f);
            go.Button.onClick.AddListener(() => SceneManager.LoadScene("Menu"));
        }

        private static void Stat(RectTransform card, float x, string caption, string value)
        {
            var label = Ui.Label(card, caption, 28, Theme.Muted);
            Ui.Place(label, x, 0.74f, x + 1f / 3f, 0.94f);
            var number = Ui.Label(card, value, 62);
            Ui.Place(number, x, 0.44f, x + 1f / 3f, 0.76f);
        }

        /// <summary>The server pays after it announces the result, so refresh balances a moment later.</summary>
        private async void RefreshProgressSoon()
        {
            try
            {
                await Task.Delay(1500);
                if (_session.Wallet != null)
                    await Task.WhenAll(_session.Wallet.RefreshAsync(), _session.Goals.RefreshAsync());
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Arena] refresh after match failed: " + ex.Message);
            }
        }
    }
}
