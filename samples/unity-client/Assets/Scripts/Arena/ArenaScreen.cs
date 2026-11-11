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

        private sealed class TankView
        {
            public GameObject Root;
            public Transform Hull;
            public Transform Turret;
            public SpriteRenderer HullBody;
            public Transform BarFill;
            public Vector2 LastServer;
            public float HullAngle;
            public int Hp = -1;
            public float Flash;
            public Color Color;
        }

        private sealed class Burst
        {
            public Transform Root;
            public SpriteRenderer Ring;
            public float Age;
        }

        private readonly ArenaState _state = new ArenaState();
        private readonly Dictionary<int, TankView> _tanks = new Dictionary<int, TankView>();
        private readonly List<Transform> _bullets = new List<Transform>();
        private readonly List<Burst> _bursts = new List<Burst>();
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
        private StickPad _moveStick;
        private StickPad _aimStick;
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
            Connect();
        }

        private void OnDestroy() => Disconnect();

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

        private async void Send(object message)
        {
            var connection = _connection;
            if (connection == null || !connection.IsOpen)
                return;

            try
            {
                await connection.SendJsonAsync(message);
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
            var fire = (kb != null && kb.spaceKey.isPressed) || (mouse != null && mouse.leftButton.isPressed && !overUi);

            double aim = 0;
            lock (_state.Sync)
            {
                if (_state.TryGetTank(_state.You, out var me))
                    aim = me.Aim;

                if (_aimStick.Held && _aimStick.Value.sqrMagnitude > 0.04f)
                {
                    aim = Math.Atan2(-_aimStick.Value.y, _aimStick.Value.x);
                    fire = true;
                }
                else if (mouse != null && Camera.main != null && _tanks.TryGetValue(_state.You, out var view))
                {
                    var world = Camera.main.ScreenToWorldPoint(mouse.position.ReadValue());
                    var d = world - view.Root.transform.position;
                    if (d.sqrMagnitude > 0.0004f)
                        aim = Math.Atan2(-d.y, d.x);        // server angles are measured with y down
                }
            }

            Send(new { t = "in", dx, dy, aim = Math.Round(aim, 3), fire, seq = ++_seq });
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

        private void BuildWorld()
        {
            _worldBuilt = true;
            var w = _state.Width * Scale;
            var h = _state.Height * Scale;

            var root = new GameObject("World").transform;
            root.SetParent(transform, false);

            Block("Shadow", root, new Color(0, 0, 0, 0.35f), new Vector2(w + 0.5f, h + 0.5f), -12).transform.position = new Vector3(0.04f, -0.08f, 0);
            Block("Border", root, Theme.Hex(0x1A2A1A), new Vector2(w + 0.28f, h + 0.28f), -11);

            // checkered grass
            var tile = 0.5f;
            var cols = Mathf.RoundToInt(w / tile);
            var rows = Mathf.RoundToInt(h / tile);
            for (var cx = 0; cx < cols; cx++)
            {
                for (var cy = 0; cy < rows; cy++)
                {
                    var c = (cx + cy) % 2 == 0 ? Theme.Hex(0x4C8A44) : Theme.Hex(0x438039);
                    var t = Block("Tile", root, c, new Vector2(tile, tile), -10);
                    t.transform.position = new Vector3(-w / 2f + tile * (cx + 0.5f), -h / 2f + tile * (cy + 0.5f), 0);
                }
            }

            foreach (var wall in _state.Walls)
            {
                var size = new Vector2(wall[2] * Scale, wall[3] * Scale);
                var pos = ToWorld(wall[0] + wall[2] / 2f, wall[1] + wall[3] / 2f);
                Block("WallBase", root, Theme.Hex(0x2B3042), size + new Vector2(0.06f, 0.06f), -6).transform.position = pos + new Vector3(0.02f, -0.04f, 0);
                Block("Wall", root, Theme.Hex(0x7C849B), size, -5).transform.position = pos;
                Block("WallTop", root, Theme.Hex(0xA3ABC2), size - new Vector2(0.1f, 0.1f), -4).transform.position = pos;
            }
        }

        private TankView MakeTank(int slot)
        {
            var mine = slot == _state.You;
            var color = mine ? Theme.Hex(0x4AA8FF) : Theme.Hex(0xFF5A50);
            var view = new TankView { Color = color };

            view.Root = new GameObject("Tank" + slot);
            view.Root.transform.SetParent(transform, false);
            var root = view.Root.transform;

            Block("Shadow", root, new Color(0, 0, 0, 0.35f), new Vector2(0.46f, 0.34f), 0, true).transform.localPosition = new Vector3(0.03f, -0.07f, 0);

            view.Hull = new GameObject("Hull").transform;
            view.Hull.SetParent(root, false);
            Block("TreadL", view.Hull, Theme.Hex(0x1D2330), new Vector2(0.38f, 0.1f), 1).transform.localPosition = new Vector3(0, 0.15f, 0);
            Block("TreadR", view.Hull, Theme.Hex(0x1D2330), new Vector2(0.38f, 0.1f), 1).transform.localPosition = new Vector3(0, -0.15f, 0);
            view.HullBody = Block("Body", view.Hull, color, new Vector2(0.32f, 0.24f), 2);
            Block("Plate", view.Hull, Color.Lerp(color, Color.white, 0.35f), new Vector2(0.18f, 0.12f), 3).transform.localPosition = new Vector3(-0.04f, 0, 0);

            view.Turret = new GameObject("Turret").transform;
            view.Turret.SetParent(root, false);
            Block("Barrel", view.Turret, Theme.Hex(0x2A3040), new Vector2(0.26f, 0.08f), 4).transform.localPosition = new Vector3(0.17f, 0, 0);
            Block("Muzzle", view.Turret, Theme.Hex(0x161B27), new Vector2(0.05f, 0.11f), 5).transform.localPosition = new Vector3(0.3f, 0, 0);
            Block("TurretBase", view.Turret, Color.Lerp(color, Color.black, 0.15f), new Vector2(0.22f, 0.22f), 5, true);
            Block("TurretCap", view.Turret, Color.Lerp(color, Color.white, 0.4f), new Vector2(0.11f, 0.11f), 6, true);

            Block("BarBack", root, new Color(0.05f, 0.07f, 0.15f, 0.9f), new Vector2(0.46f, 0.08f), 7).transform.localPosition = new Vector3(0, 0.36f, 0);
            var fill = Block("BarFill", root, Theme.Hex(0x5CD94B), new Vector2(0.4f, 0.04f), 8);
            fill.transform.localPosition = new Vector3(-0.2f, 0.36f, 0);
            view.BarFill = fill.transform;
            return view;
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
                    _tanks[t.Slot] = view;
                }

                // A tank wears its owner's skin when it has one (the server only lets owned skins through), else its team color.
                var skin = _state.Players.TryGetValue(t.Slot, out var info) ? info.Skin : null;
                view.Color = Skins.ColorOf(skin) ?? (t.Slot == _state.You ? Theme.Hex(0x4AA8FF) : Theme.Hex(0xFF5A50));

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
                view.BarFill.localScale = new Vector3(Mathf.Max(0.001f, 0.4f * hpT), 0.04f, 1f);
                view.BarFill.localPosition = new Vector3(-0.2f + 0.2f * hpT, 0.36f, 0);
                view.BarFill.GetComponent<SpriteRenderer>().color = hpT > 0.5f ? Theme.Hex(0x5CD94B) : (hpT > 0.3f ? Theme.Gold : Theme.Red);

                if (view.Hp >= 0 && t.Hp < view.Hp)
                {
                    view.Flash = 0.14f;
                    SpawnBurst(view.Root.transform.position, view.Color);
                    _shake = t.Slot == _state.You ? 0.28f : 0.14f;
                }

                view.Hp = t.Hp;
                view.Root.SetActive(t.Hp > 0);
                view.Flash = Mathf.Max(0, view.Flash - Time.deltaTime);
                view.HullBody.color = view.Flash > 0 ? Color.white : view.Color;
            }

            while (_bullets.Count < _state.Bullets.Count)
            {
                var b = new GameObject("Bullet").transform;
                b.SetParent(transform, false);
                Block("Glow", b, new Color(1f, 0.85f, 0.3f, 0.5f), new Vector2(0.3f, 0.3f), 8, true).sprite = Art.GlowUnit();
                Block("Core", b, Theme.Hex(0xFFE066), new Vector2(0.11f, 0.11f), 9, true);
                _bullets.Add(b);
            }

            for (var i = 0; i < _bullets.Count; i++)
            {
                var active = i < _state.Bullets.Count;
                _bullets[i].gameObject.SetActive(active);
                if (active)
                    _bullets[i].position = ToWorld(_state.Bullets[i].X, _state.Bullets[i].Y);
            }

            RenderHud();
        }

        private void SpawnBurst(Vector3 at, Color color)
        {
            var root = new GameObject("Burst").transform;
            root.SetParent(transform, false);
            root.position = at;
            var ring = Block("Ring", root, new Color(color.r, color.g, color.b, 0.9f), new Vector2(0.3f, 0.3f), 10, true);
            _bursts.Add(new Burst { Root = root, Ring = ring });
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
            cam.orthographicSize = Mathf.Max(h / 2f + 0.95f, (w / 2f + 0.2f) / aspect);
        }

        private void UpdateFx()
        {
            FitCamera();
            for (var i = _bursts.Count - 1; i >= 0; i--)
            {
                var b = _bursts[i];
                b.Age += Time.deltaTime;
                var t = b.Age / 0.35f;
                if (t >= 1f)
                {
                    Destroy(b.Root.gameObject);
                    _bursts.RemoveAt(i);
                    continue;
                }

                b.Root.localScale = Vector3.one * Mathf.Lerp(0.6f, 2.6f, Anim.EaseOut(t));
                var c = b.Ring.color;
                b.Ring.color = new Color(c.r, c.g, c.b, 0.9f * (1f - t));
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
            var back = Ui.Gradient(canvas.transform, Theme.Hex(0x1E3470), Theme.Hex(0x0A1030), "Backdrop");
            back.raycastTarget = false;
        }

        private void BuildHud()
        {
            var canvas = Ui.CreateCanvas("ArenaHud", 20);
            canvas.transform.SetParent(transform, false);
            _hud = Ui.NewRect(canvas.transform, "Hud");
            _toasts = ToastLayer.Create(_hud);

            BuildPlate(true);
            BuildPlate(false);

            var timer = Ui.Panel(_hud, new Color(Theme.PanelDark.r, Theme.PanelDark.g, Theme.PanelDark.b, 0.9f), 40f, "Timer");
            Ui.Pin(timer, 0.5f, 1f, 150, 90, 0, -30);
            _timer = Ui.Label(timer.transform, "VS", 52);
            Ui.Stretch(_timer.rectTransform);

            _center = Ui.Label(_hud, "", 260);
            Ui.Pin(_center, 0.5f, 0.5f, 1400, 360);

            _moveStick = StickPad.Create(_hud, Theme.Blue);
            Ui.Pin(_moveStick, 0f, 0f, 360, 360, 70, 70);
            _aimStick = StickPad.Create(_hud, Theme.Red, Icon.Swords);
            Ui.Pin(_aimStick, 1f, 0f, 360, 360, -70, 70);
        }

        private void BuildPlate(bool left)
        {
            var index = left ? 0 : 1;
            var tint = left ? Theme.Blue : Theme.Red;
            var plate = Ui.Panel(_hud, new Color(Theme.PanelDark.r, Theme.PanelDark.g, Theme.PanelDark.b, 0.9f), 40f, left ? "PlateMe" : "PlateEnemy");
            Ui.Pin(plate, left ? 0f : 1f, 1f, 450, 140, left ? 24 : -24, -24);

            var stripe = Ui.Panel(plate.transform, tint, 20f, "Stripe");
            Ui.Place(stripe, left ? 0f : 0.96f, 0.1f, left ? 0.04f : 1f, 0.9f);

            _names[index] = Ui.Label(plate.transform, left ? "YOU" : "ENEMY", 40, Color.white, left ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight);
            Ui.Place(_names[index], 0.09f, 0.48f, 0.91f, 0.95f);

            for (var i = 0; i < 3; i++)
            {
                var dot = Ui.Circle(plate.transform, Theme.Red, "Heart" + i);
                var x = left ? 0.12f + i * 0.14f : 0.88f - i * 0.14f;
                Ui.Pin(dot, x, 0.26f, 46, 46);
                _hearts[index][i] = dot;
            }
        }

        private void RenderHud()
        {
            foreach (var t in _state.Tanks)
            {
                var plate = t.Slot == _state.You ? 0 : 1;
                _names[plate].text = _state.NameOf(t.Slot).ToUpperInvariant();
                for (var i = 0; i < 3; i++)
                    _hearts[plate][i].color = i < t.Hp ? Theme.Red : new Color(0.2f, 0.24f, 0.4f, 1f);
            }

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
            var modal = Modal.Create(_hud, "RESULT", 900, 860, false);

            var big = Ui.Label(modal.Body, title, 130, titleColor);
            Ui.Place(big, 0f, 0.6f, 1f, 1f);

            if (mine != null)
            {
                var stats = Ui.Label(modal.Body, "Hits " + mine.Hits + " / " + mine.Shots + " shots", 46, Theme.Muted);
                Ui.Place(stats, 0f, 0.46f, 1f, 0.6f);

                var chip = Chip.Create(modal.Body, Icon.Coin, Theme.Gold);
                Ui.Pin(chip, 0.5f, 0.36f, 380, 100);
                chip.Set(0, false);
                chip.Set(mine.Coins, true);
                var plus = Ui.Label(modal.Body, "REWARD", 34, Theme.Muted);
                Ui.Pin(plus, 0.5f, 0.45f, 300, 44);
            }

            var go = GButton.Create(modal.Body, "CONTINUE", Theme.Gold, Theme.GoldLip, 64, 16);
            Ui.Place(go, 0.1f, 0f, 0.9f, 0.22f);
            go.Button.onClick.AddListener(() => SceneManager.LoadScene("Menu"));
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
