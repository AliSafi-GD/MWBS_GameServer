using System;
using Game.Core;
using GameServer.Client;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>
    /// The splash and loading screen: connects to the server, signs in (saved session or guest), loads what the home
    /// screen needs while a progress bar fills, then opens the menu. If something fails it says what and offers Retry.
    /// </summary>
    public sealed class BootScreen : MonoBehaviour
    {
        /// <summary>Where to go when loading is done.</summary>
        public static string NextScene = "Menu";

        private RectTransform _column;
        private ProgressBar _bar;
        private Text _status;
        private RectTransform _errorCard;
        private Text _errorText;
        private RectTransform _serverCard;
        private InputBox _server;
        private float _shown;
        private float _target;
        private bool _busy;
        private bool _updateDeclined;

        private void Start()
        {
            Application.runInBackground = true;
            Ui.EnsureEventSystem();
            GameSession.Ensure();
            Build();
            Connect();
        }

        private void Update()
        {
            if (_bar == null)
                return;

            _shown = Mathf.MoveTowards(_shown, _target, Time.unscaledDeltaTime * 0.8f);
            _bar.Set(_shown, Mathf.RoundToInt(_shown * 100f) + "%");
        }

        private void Build()
        {
            var cam = Camera.main;
            if (cam != null)
                cam.backgroundColor = Theme.BgBottom;

            var canvas = Ui.CreateCanvas("BootCanvas");
            canvas.transform.SetParent(transform, false);

            var backdrop = Ui.Gradient(canvas.transform, Theme.Hex(0x4A7BE0), Theme.Hex(0x101A52), "Backdrop");
            backdrop.raycastTarget = false;

            _column = Ui.Column(canvas.transform);

            var glow = Ui.Pic(_column, Art.Glow(), new Color(1f, 0.85f, 0.4f, 0.55f), "Glow");
            Ui.Pin(glow, 0.5f, 0.6f, 1300, 1300);
            glow.gameObject.AddComponent<Pulse>().Amount = 0.04f;

            var plate = Ui.Circle(_column, new Color(Theme.Outline.r, Theme.Outline.g, Theme.Outline.b, 0.9f), "Plate");
            Ui.Pin(plate, 0.5f, 0.6f, 560, 560);
            var plateInner = Ui.Circle(_column, Theme.PanelLight, "PlateInner");
            Ui.Pin(plateInner, 0.5f, 0.6f, 520, 520);
            var core = Ui.Circle(_column, Theme.Panel, "PlateCore");
            Ui.Pin(core, 0.5f, 0.6f, 430, 430);
            var swords = Ui.IconPic(_column, Icon.Swords, Theme.Gold);
            Ui.Pin(swords, 0.5f, 0.6f, 360, 360);
            swords.gameObject.AddComponent<Bob>();

            var title = Ui.Label(_column, "TANK", 170);
            Ui.Pin(title, 0.5f, 0.885f, 900, 190);
            var title2 = Ui.Label(_column, "ARENA", 170, Theme.Gold);
            Ui.Pin(title2, 0.5f, 0.805f, 900, 190);

            _bar = ProgressBar.Create(_column, Theme.Gold);
            Ui.Pin(_bar, 0.5f, 0.2f, 800, 66);
            _status = Ui.Label(_column, "Connecting...", 40, Theme.Muted);
            Ui.Pin(_status, 0.5f, 0.15f, 900, 70);

            var version = Ui.Label(_column, "v" + Application.version, 28, new Color(1, 1, 1, 0.5f));
            Ui.Pin(version, 0.5f, 0.02f, 400, 44);

            BuildErrorCard();
            BuildServerCard();

            var gear = GButton.Create(_column, "", Theme.Blue, Theme.BlueLip, 30, 10);
            Ui.Pin(gear, 1f, 0f, 96, 96, -30, 30);
            var gearIcon = Ui.IconPic(gear.Face.transform, Icon.Gear);
            Ui.Place(gearIcon, 0.15f, 0.15f, 0.85f, 0.85f);
            gear.Button.onClick.AddListener(() => _serverCard.gameObject.SetActive(!_serverCard.gameObject.activeSelf));
        }

        private void BuildErrorCard()
        {
            _errorCard = Parts.Card(_column, Theme.Panel, 44f);
            var rim = _errorCard.parent;
            Ui.Pin(rim.GetComponent<Image>(), 0.5f, 0.38f, 880, 420);

            var head = Ui.Label(_errorCard, "COULD NOT CONNECT", 54, Theme.Hex(0xFF8A80));
            Ui.Place(head, 0.05f, 0.72f, 0.95f, 0.97f);
            _errorText = Ui.Label(_errorCard, "", 34, Color.white, TextAnchor.MiddleCenter, false, true);
            Ui.Place(_errorText, 0.06f, 0.36f, 0.94f, 0.72f);
            var retry = GButton.Create(_errorCard, "RETRY", Theme.Green, Theme.GreenLip, 54, 14);
            Ui.Place(retry, 0.25f, 0.06f, 0.75f, 0.32f);
            retry.Button.onClick.AddListener(Connect);
            rim.gameObject.SetActive(false);
        }

        private void BuildServerCard()
        {
            _serverCard = Parts.Card(_column, Theme.Panel, 40f);
            var rim = _serverCard.parent;
            Ui.Pin(rim.GetComponent<Image>(), 0.5f, 0.07f, 880, 220);

            var label = Ui.Label(_serverCard, "SERVER", 34, Theme.Muted);
            Ui.Place(label, 0.04f, 0.68f, 0.96f, 0.96f);
            _server = InputBox.Create(_serverCard, GameSession.DefaultUrl, 38);
            _server.Text = GameSession.SavedUrl;
            Ui.Place(_server, 0.04f, 0.08f, 0.66f, 0.68f);
            var go = GButton.Create(_serverCard, "GO", Theme.Green, Theme.GreenLip, 48, 12);
            Ui.Place(go, 0.69f, 0.1f, 0.96f, 0.68f);
            go.Button.onClick.AddListener(Connect);

            _serverCard = (RectTransform)rim;
            rim.gameObject.SetActive(false);
        }

        // ---- flow --------------------------------------------------------------------------------------------

        private void SetProgress(float t, string status)
        {
            _target = Mathf.Max(_target, t);
            _status.text = status;
        }

        public async void Connect()
        {
            if (_busy)
                return;

            _busy = true;
            _errorCard.parent.gameObject.SetActive(false);
            _serverCard.gameObject.SetActive(false);
            _shown = _target = 0f;

            var session = GameSession.Instance;
            try
            {
                SetProgress(0.1f, "Connecting to the server");
                session.Configure(_server.Text.Trim());
                var check = await session.CheckStartupAsync();

                if (check.InMaintenance)
                {
                    _status.text = "Under maintenance";
                    Popups.Maintenance(_column, check.MaintenanceMessage, check.MaintenanceUntil, Connect);
                    return;
                }

                if (check.MustUpdate)
                {
                    _status.text = "Update required";
                    Popups.UpdateRequired(_column, check.Latest, check.Url);
                    return;
                }

                if (check.CanUpdate && !_updateDeclined)
                {
                    // An optional update: ask once, then carry on whichever way the player answers.
                    var answered = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    Popups.UpdateAvailable(_column, check.Latest, check.Url, () => answered.TrySetResult(true));
                    await answered.Task;
                    _updateDeclined = true;
                }

                SetProgress(0.3f, "Signing in");
                await session.SignInAsync();

                await session.LoadHomeAsync(SetProgress);
                SetProgress(1f, "Ready!");
                await System.Threading.Tasks.Task.Delay(350);
                SceneManager.LoadScene(NextScene);
            }
            catch (ApiException api) when (api.Code == "maintenance")
            {
                // The server went into maintenance between the startup check and the next call.
                _status.text = "Under maintenance";
                Popups.Maintenance(_column, api.Message, null, Connect);
            }
            catch (ApiException api) when (api.Code == "app_version")
            {
                _status.text = "Update required";
                Popups.UpdateRequired(_column, session.Startup?.Latest, session.Startup?.Url);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Boot] " + ex);
                _errorText.text = Parts.ErrorText(ex) + "\n" + session.ServerUrl;
                _errorCard.parent.gameObject.SetActive(true);
                _target = _shown;
                _status.text = "Connection failed";
            }
            finally
            {
                _busy = false;
            }
        }
    }
}
