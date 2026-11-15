using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>
    /// The home hub, laid out like a mobile battle game: player and currencies on top, five pages
    /// (Shop, Quests, Battle, Social, Ranking) switched from a bar at the bottom.
    /// </summary>
    public sealed partial class MenuScreen : MonoBehaviour
    {
        // Profile editing lives in MenuScreen.Profile.cs.
        private RectTransform _identity;
        private RectTransform _avatarView;
        private Text _wins;

        public const int ShopIndex = 0, QuestsIndex = 1, BattleIndex = 2, SocialIndex = 3, RankIndex = 4;

        /// <summary>The page opened first. Tests set it to capture a given page.</summary>
        public static int StartPage = BattleIndex;

        private sealed class NavItem
        {
            public RectTransform Root;
            public Image Tile;
            public Image Rim;
            public Image Glow;
            public Image Gloss;
            public Image Icon;
            public Text Label;
            public Badge Badge;
            public float Raise;
        }

        private static readonly string[] PageNames = { "SHOP", "QUESTS", "BATTLE", "SOCIAL", "RANKING" };
        private static readonly Icon[] PageIcons = { Icon.Bag, Icon.Scroll, Icon.Swords, Icon.People, Icon.Trophy };

        private GameSession _session;
        private MenuContext _context;
        private readonly Page[] _pages = new Page[5];
        private readonly NavItem[] _nav = new NavItem[5];
        private int _current = -1;
        private bool _blocked;

        private RectTransform _column;
        private RectTransform _pageHost;
        private RectTransform _overlay;
        private Chip _coins;
        private Chip _gems;
        private Text _name;
        private Image _banner;
        private GameObject _search;
        private Text _searchTime;

        private void Start()
        {
            Ui.EnsureEventSystem();
            _session = GameSession.Instance;
            if (_session == null || !_session.IsReady)
            {
                SceneManager.LoadScene("Boot");
                return;
            }

            BuildChrome();
            BuildPages();
            Hook();
            RefreshHeader(false);
            Show(StartPage);
        }

        private void OnDestroy()
        {
            Unhook();
            foreach (var page in _pages)
                page?.Dispose();
        }

        private void Update()
        {
            if (_search != null && _search.activeSelf && _session.Play != null && _session.Play.IsSearching)
            {
                var t = (int)(DateTime.UtcNow - _session.Play.SearchStartedUtc).TotalSeconds;
                _searchTime.text = (t / 60).ToString("0") + ":" + (t % 60).ToString("00");
            }
        }

        // ---- layout ------------------------------------------------------------------------------------------

        private void BuildChrome()
        {
            var cam = Camera.main;
            if (cam != null)
                cam.backgroundColor = Theme.BgBottom;

            var canvas = Ui.CreateCanvas("MenuCanvas");
            canvas.transform.SetParent(transform, false);

            Backdrop.Create(canvas.transform);

            _column = Ui.Column(canvas.transform);

            _pageHost = Ui.NewRect(_column, "Pages");
            Ui.Place(_pageHost, 0f, 0.12f, 1f, 0.925f);

            BuildTopBar();
            BuildNavBar();

            _banner = Ui.Panel(_column, Theme.Outline, 28f, "Offline", true);
            Ui.Pin(_banner, 0.5f, 0.925f, 768, 72, 0, -14);
            var bannerFill = Ui.Panel(_banner.transform, Theme.Hex(0xF08A1C), 24f, "Fill");
            Ui.Inset(bannerFill, 4, 4, 4, 4);
            var bannerText = Ui.Label(_banner.transform, "Reconnecting...", 34);
            Ui.Stretch(bannerText.rectTransform);
            _banner.gameObject.SetActive(!_session.Backend.IsOnline);

            _overlay = Ui.NewRect(_column, "Overlay");
            var toasts = ToastLayer.Create(_column);
            BuildSearchOverlay();

            _context = new MenuContext
            {
                Host = this,
                Session = _session,
                Toasts = toasts,
                Overlay = _overlay,
                Navigate = Show
            };
        }

        private void BuildTopBar()
        {
            // A shadow under the bar, so the pages seem to slide beneath it.
            var under = Ui.Pic(_column, Art.Fade(), new Color(0f, 0.01f, 0.08f, 0.5f), "TopShade");
            under.preserveAspect = false;
            Ui.Place(under, 0f, 0.9f, 1f, 0.925f);

            var bar = Ui.Panel(_column, Theme.Hex(0x16286B), 6f, "TopBar");
            Ui.Place(bar, 0f, 0.925f, 1f, 1f);
            // Reaches past the top of the column, for phones taller than 16:9.
            Ui.Inset(bar, 0, 0, 0, -400);
            var edge = Ui.Panel(bar.transform, Theme.Edge, 4f, "Edge", true);
            edge.rectTransform.anchorMin = new Vector2(0f, 0f);
            edge.rectTransform.anchorMax = new Vector2(1f, 0f);
            edge.rectTransform.pivot = new Vector2(0.5f, 0f);
            edge.rectTransform.sizeDelta = new Vector2(0f, 6f);
            edge.rectTransform.anchoredPosition = Vector2.zero;

            var content = Ui.NewRect(bar.transform, "Content");
            Ui.Inset(content, 0, 0, 0, 400);

            // The whole left side (avatar and name) opens the profile; its pictures and texts do not catch taps themselves.
            var profileHit = Ui.Panel(content, new Color(0, 0, 0, 0), 4f, "ProfileButton");
            Ui.Place(profileHit, 0f, 0f, 0.42f, 1f);
            profileHit.raycastTarget = true;
            profileHit.gameObject.AddComponent<Button>().onClick.AddListener(OpenProfile);

            _identity = Ui.NewRect(content, "Identity");
            _identity.gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;
            var plate = Ui.Panel(_identity, new Color(0.03f, 0.06f, 0.2f, 0.75f), 40f, "NamePlate", true);
            Ui.Pin(plate, 0f, 0.5f, 280, 96, 142, 0);
            _name = Ui.Label(_identity, "", 40, Color.white, TextAnchor.MiddleLeft);
            Ui.Pin(_name, 0f, 0.5f, 240, 54, 160, 22);
            var cup = Ui.IconPic(_identity, Icon.Trophy, Theme.Gold);
            Ui.Pin(cup, 0f, 0.5f, 40, 40, 160, -24);
            _wins = Ui.Label(_identity, "", 30, Theme.Gold, TextAnchor.MiddleLeft);
            Ui.Pin(_wins, 0f, 0.5f, 220, 40, 208, -24);
            RefreshIdentity();

            var gear = GButton.Create(content, "", Theme.Blue, Theme.BlueLip, 30, 8);
            Ui.Pin(gear, 1f, 0.5f, 84, 84, -20, 2);
            var gearIcon = Ui.IconPic(gear.Face.transform, Icon.Gear);
            Ui.Place(gearIcon, 0.16f, 0.16f, 0.84f, 0.84f);
            gear.Button.onClick.AddListener(OpenSettings);

            _gems = Chip.Create(content, Icon.Gem, null, () => Show(ShopIndex));
            Ui.Pin(_gems, 1f, 0.5f, 228, 76, -122, 0);
            _coins = Chip.Create(content, Icon.Coin, Theme.Gold, () => Show(ShopIndex));
            Ui.Pin(_coins, 1f, 0.5f, 262, 76, -372, 0);
        }

        private void BuildNavBar()
        {
            var over = Ui.Pic(_column, Art.Fade(false), new Color(0f, 0.01f, 0.08f, 0.55f), "NavShade");
            over.preserveAspect = false;
            Ui.Place(over, 0f, 0.12f, 1f, 0.15f);

            var bar = Ui.Panel(_column, Theme.Hex(0x16286B), 6f, "NavBar");
            Ui.Place(bar, 0f, 0f, 1f, 0.12f);
            Ui.Inset(bar, 0, -400, 0, 0);
            var edge = Ui.Panel(bar.transform, Theme.Edge, 4f, "Edge", true);
            edge.rectTransform.anchorMin = new Vector2(0f, 1f);
            edge.rectTransform.anchorMax = new Vector2(1f, 1f);
            edge.rectTransform.pivot = new Vector2(0.5f, 1f);
            edge.rectTransform.sizeDelta = new Vector2(0f, 6f);
            edge.rectTransform.anchoredPosition = Vector2.zero;

            var content = Ui.NewRect(bar.transform, "Content");
            Ui.Inset(content, 0, 400, 0, 0);

            for (var i = 1; i < _nav.Length; i++)
            {
                var line = Ui.Panel(content, new Color(1, 1, 1, 0.07f), 2f, "Divider", true);
                Ui.Pin(line, i / 5f, 0.5f, 3, 130);
            }

            for (var i = 0; i < _nav.Length; i++)
            {
                var index = i;
                var accent = i == BattleIndex ? Theme.Gold : Theme.Blue;
                var item = new NavItem();
                var slot = Ui.NewRect(content, "Nav_" + PageNames[i]);
                Ui.Place(slot, i / 5f, 0f, (i + 1) / 5f, 1f);
                item.Root = slot;

                item.Glow = Ui.Pic(slot, Art.Glow(), new Color(accent.r, accent.g, accent.b, 0.55f), "Glow");
                Ui.Pin(item.Glow, 0.5f, 0.6f, 420, 420);

                item.Rim = Ui.Panel(slot, Theme.Outline, 34f, "TileRim", true);
                Ui.Place(item.Rim, 0.04f, 0.03f, 0.96f, 1.05f);

                item.Tile = Ui.Panel(slot, accent, 30f, "Tile");
                Ui.Place(item.Tile, 0.04f, 0.03f, 0.96f, 1.05f);
                Ui.Inset(item.Tile, 5, 5, 5, 5);
                item.Tile.raycastTarget = true;
                var button = item.Tile.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => Show(index));

                item.Gloss = Ui.Panel(item.Tile.transform, new Color(1, 1, 1, 0.25f), 24f, "Gloss", true);
                Ui.Place(item.Gloss, 0.05f, 0.6f, 0.95f, 0.95f);
                item.Gloss.raycastTarget = false;

                item.Icon = Ui.IconPic(slot, PageIcons[i], Theme.Muted);
                Ui.Pin(item.Icon, 0.5f, 0.6f, 100, 100);

                item.Label = Ui.Label(slot, PageNames[i], 30, Color.white);
                Ui.Pin(item.Label, 0.5f, 0.0f, 200, 40, 0, 14);
                item.Label.rectTransform.pivot = new Vector2(0.5f, 0f);
                item.Label.gameObject.SetActive(false);

                item.Badge = Badge.Create(slot);
                Ui.Pin(item.Badge, 0.5f, 0.8f, 52, 52, 50, 0);

                _nav[i] = item;
            }
        }

        private void BuildSearchOverlay()
        {
            _search = new GameObject("Search", typeof(RectTransform));
            var rt = (RectTransform)_search.transform;
            rt.SetParent(_column, false);
            Ui.Stretch(rt);

            var dim = Ui.Panel(rt, new Color(0.01f, 0.02f, 0.1f, 0.82f), 4f, "Dim", true);
            Ui.Stretch(dim.rectTransform);
            Ui.Inset(dim, -2000, -2000, -2000, -2000);
            dim.raycastTarget = true;

            var shadow = Ui.Shadow(rt, 0.6f);
            Ui.Pin(shadow, 0.5f, 0.5f, 900, 860, 0, -30);
            var rim = Ui.Panel(rt, Theme.Outline, 60f, "Rim", true);
            Ui.Pin(rim, 0.5f, 0.5f, 800, 760);
            var edge = Ui.Panel(rim.transform, Theme.Edge, 56f, "Edge");
            Ui.Inset(edge, 5, 5, 5, 5);
            var card = Ui.Panel(rim.transform, Theme.Panel, 52f, "Card");
            Ui.Inset(card, 10, 10, 10, 10);

            var title = Ui.Headline(card.transform, "FINDING OPPONENT", 60, Color.white, Theme.Hex(0xBFD9FF));
            Ui.Pin(title, 0.5f, 1f, 700, 100, 0, -50);

            var rays = Ui.Pic(card.transform, Art.Rays(), new Color(1f, 0.82f, 0.3f, 0.35f), "Rays");
            Ui.Pin(rays, 0.5f, 0.55f, 620, 620);
            rays.gameObject.AddComponent<Spin>().DegreesPerSecond = 30f;
            var ringRim = Ui.Circle(card.transform, Theme.Outline, "RingRim");
            Ui.Pin(ringRim, 0.5f, 0.55f, 346, 346);
            var ring = Ui.Circle(card.transform, Theme.PanelLight, "Ring");
            Ui.Pin(ring, 0.5f, 0.55f, 330, 330);
            var ringCore = Ui.Circle(ring.transform, Theme.PanelDark, "Core");
            Ui.Stretch(ringCore.rectTransform);
            Ui.Inset(ringCore, 18, 18, 18, 18);
            var spinner = Ui.IconPic(ring.transform, Icon.Swords, Theme.Gold);
            Ui.Place(spinner, 0.16f, 0.16f, 0.84f, 0.84f);
            spinner.gameObject.AddComponent<Spin>().DegreesPerSecond = -120f;

            _searchTime = Ui.Label(card.transform, "0:00", 72, Color.white);
            Ui.Pin(_searchTime, 0.5f, 0.26f, 400, 90);

            var cancel = GButton.Create(card.transform, "CANCEL", Theme.Red, Theme.RedLip, 48, 12);
            Ui.Pin(cancel, 0.5f, 0f, 380, 120, 0, 36);
            cancel.Button.onClick.AddListener(() => _context.Run(() => _session.Play.CancelSearchAsync(), cancel));

            _search.SetActive(false);
        }

        private void BuildPages()
        {
            var pages = new Page[] { new ShopPage(), new QuestsPage(), new BattlePage(), new SocialPage(), new RankPage() };
            for (var i = 0; i < pages.Length; i++)
            {
                var root = Ui.NewRect(_pageHost, "Page_" + PageNames[i]);
                root.gameObject.AddComponent<CanvasGroup>();
                pages[i].Init(_context, root);
                root.gameObject.SetActive(false);
                _pages[i] = pages[i];
            }
        }

        // ---- wiring ------------------------------------------------------------------------------------------

        private void Hook()
        {
            _session.Profile.Changed += RefreshIdentity;
            _session.Wallet.Changed += OnWalletChanged;
            _session.Goals.Changed += OnGoalsChanged;
            _session.Social.Changed += OnSocialChanged;
            _session.Chat.UnreadChanged += OnSocialChanged;
            _session.Chat.MessageAdded += OnChatMessage;
            _session.Backend.Lost += OnLost;
            _session.Backend.Restored += OnRestored;
            _session.Play.SearchChanged += OnSearchChanged;
            _session.Play.SearchEnded += OnSearchEnded;
            _session.Play.MatchReady += OnMatchReady;
            _session.SessionEnded += OnSessionEnded;
        }

        private void Unhook()
        {
            if (_session == null)
                return;

            if (_session.Profile != null) _session.Profile.Changed -= RefreshIdentity;
            if (_session.Wallet != null) _session.Wallet.Changed -= OnWalletChanged;
            if (_session.Goals != null) _session.Goals.Changed -= OnGoalsChanged;
            if (_session.Social != null) _session.Social.Changed -= OnSocialChanged;
            if (_session.Chat != null)
            {
                _session.Chat.UnreadChanged -= OnSocialChanged;
                _session.Chat.MessageAdded -= OnChatMessage;
            }

            if (_session.Backend != null)
            {
                _session.Backend.Lost -= OnLost;
                _session.Backend.Restored -= OnRestored;
            }

            if (_session.Play != null)
            {
                _session.Play.SearchChanged -= OnSearchChanged;
                _session.Play.SearchEnded -= OnSearchEnded;
                _session.Play.MatchReady -= OnMatchReady;
            }

            _session.SessionEnded -= OnSessionEnded;
        }

        private void OnWalletChanged() => RefreshHeader(true);

        private void OnGoalsChanged()
        {
            RefreshHeader(true);
            _nav[QuestsIndex].Badge.Set(_session.Goals.ClaimableCount);
        }

        private void OnSocialChanged() => _nav[SocialIndex].Badge.Set(_session.Social.PendingCount + _session.Chat.TotalUnread);

        /// <summary>A message for a conversation that is not on screen (never the lobby): say who wrote.</summary>
        private void OnChatMessage(ChatThread thread, ChatLine line)
        {
            if (line.Mine || thread.Kind == ChatKind.Lobby || thread == _session.Chat.Active)
                return;

            var text = line.Text.Replace('\n', ' ');
            if (text.Length > 28)
                text = text.Substring(0, 27) + "...";
            var from = thread.Kind == ChatKind.Direct ? thread.Title : line.Sender;
            _context.Toasts.Show(from + ": " + text, Theme.Blue, Icon.People);
        }

        private void OnLost(string reason) => _banner.gameObject.SetActive(true);

        private void OnRestored()
        {
            _banner.gameObject.SetActive(false);
            _context.Toasts.Show("Back online", Theme.Green);
        }

        private void OnSearchChanged() => _search.SetActive(_session.Play.IsSearching);

        private void OnSearchEnded(string reason) => _context.Toasts.Show("No opponent found (" + reason + ")", Theme.Red);

        private void OnMatchReady(GameServer.Client.MatchConnectInfo info) => SceneManager.LoadScene("Arena");

        private void OnSessionEnded()
        {
            if (_blocked)
                return;

            _blocked = true;
            Popups.SessionEnded(_column, () => SceneManager.LoadScene("Boot"));
        }

        /// <summary>
        /// Server states that stop play (maintenance, build too old) get a dialog instead of a toast.
        /// Returns true when the error was handled here.
        /// </summary>
        public bool HandleBlockingError(Exception ex)
        {
            if (!(ex is GameServer.Client.ApiException api))
                return false;

            if (api.Code == "maintenance")
            {
                if (!_blocked)
                {
                    _blocked = true;
                    Popups.Maintenance(_column, api.Message, null, () => SceneManager.LoadScene("Boot"));
                }

                return true;
            }

            if (api.Code == "app_version")
            {
                if (!_blocked)
                {
                    _blocked = true;
                    Popups.UpdateRequired(_column, _session.Startup?.Latest, _session.Startup?.Url);
                }

                return true;
            }

            return false;
        }

        private void RefreshHeader(bool animate)
        {
            _coins.Set(_session.Wallet.Coins, animate);
            _gems.Set(_session.Wallet.Gems, animate);
            _nav[QuestsIndex].Badge.Set(_session.Goals.ClaimableCount);
            _nav[SocialIndex].Badge.Set(_session.Social.PendingCount + _session.Chat.TotalUnread);

            _wins.text = Ui.Num(_session.Goals.Wins) + " wins";
        }

        // ---- navigation --------------------------------------------------------------------------------------

        /// <summary>Opens the Social page with one conversation on screen.</summary>
        public void GoToChat(ChatThread thread)
        {
            ((SocialPage)_pages[SocialIndex]).OpenChat(thread);
            Show(SocialIndex);
        }

        /// <summary>Opens the Social page on a given tab (0 friends, 1 party, 2 chat, 3 clan).</summary>
        public void GoToSocial(int tab)
        {
            ((SocialPage)_pages[SocialIndex]).Open(tab);
            Show(SocialIndex);
        }

        /// <summary>Opens the shop page on its inventory tab.</summary>
        public void GoToInventory()
        {
            ((ShopPage)_pages[ShopIndex]).OpenInventory();
            Show(ShopIndex);
        }

        public void Show(int index)
        {
            if (index < 0 || index >= _pages.Length)
                return;

            if (_current >= 0 && _current != index)
            {
                _pages[_current].OnHide();
                _pages[_current].Root.gameObject.SetActive(false);
            }

            var previous = _current;
            _current = index;
            var page = _pages[index];
            page.Root.gameObject.SetActive(true);
            page.OnShow();

            for (var i = 0; i < _nav.Length; i++)
                StyleNav(i, i == index);

            if (previous != index)
            {
                var group = page.Root.GetComponent<CanvasGroup>();
                var rt = page.Root;
                var dir = previous < index ? 1f : -1f;
                Anim.Run(this, 0.22f, t =>
                {
                    group.alpha = t;
                    rt.anchoredPosition = new Vector2((1f - Anim.EaseOut(t)) * 120f * dir, 0);
                });
            }
        }

        private void StyleNav(int i, bool active)
        {
            var item = _nav[i];
            var accent = i == BattleIndex ? Theme.Gold : Theme.Blue;
            item.Tile.color = active ? accent : Color.clear;
            item.Rim.color = active ? Theme.Outline : Color.clear;
            item.Gloss.color = new Color(1, 1, 1, active ? 0.25f : 0f);
            item.Glow.color = new Color(accent.r, accent.g, accent.b, active ? 0.5f : 0f);
            item.Icon.color = active ? Color.white : Theme.Muted;
            item.Label.gameObject.SetActive(active);

            var target = active ? 1.26f : (i == BattleIndex ? 1.12f : 1f);
            var rt = item.Icon.rectTransform;
            var from = rt.localScale.x;
            var fromY = rt.anchoredPosition.y;
            var toY = active ? 22f : 0f;
            Anim.Run(this, active ? 0.3f : 0.16f, t =>
            {
                var e = active ? Anim.EaseOutBack(t) : Anim.EaseOut(t);
                var s = Mathf.LerpUnclamped(from, target, e);
                rt.localScale = new Vector3(s, s, 1f);
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, Mathf.LerpUnclamped(fromY, toY, e));
            });
        }

        // ---- settings ----------------------------------------------------------------------------------------

        private void OpenSettings()
        {
            var modal = Modal.Create(_overlay, "SETTINGS", 900, 1080);

            var who = Ui.Label(modal.Body, "Signed in as\n" + _session.Account.Username, 42, Color.white);
            Ui.Place(who, 0f, 0.84f, 1f, 1f);

            var aim = Parts.SwitchRow(modal.Body, "AUTO AIM", "The gun turns to the enemy by itself", GameSettings.AutoAim, v => GameSettings.AutoAim = v);
            Ui.Place(aim, 0f, 0.67f, 1f, 0.82f);
            var ping = Parts.SwitchRow(modal.Body, "SHOW PING", "Connection delay during a match", GameSettings.ShowPing, v => GameSettings.ShowPing = v);
            Ui.Place(ping, 0f, 0.5f, 1f, 0.65f);

            var server = Ui.Label(modal.Body, _session.ServerUrl, 32, Theme.Muted);
            Ui.Place(server, 0f, 0.37f, 1f, 0.46f);

            var version = Ui.Label(modal.Body, "Version " + Application.version + (MatchNet.HasUdp ? "   -   UDP" : ""), 30, Theme.Muted);
            Ui.Place(version, 0f, 0.29f, 1f, 0.37f);

            var signOut = GButton.Create(modal.Body, "SIGN OUT", Theme.Red, Theme.RedLip, 54, 14);
            Ui.Place(signOut, 0.1f, 0.04f, 0.9f, 0.26f);
            signOut.Button.onClick.AddListener(() => _context.Run(async () =>
            {
                await _session.SignOutAsync();
                SceneManager.LoadScene("Boot");
            }, signOut));
        }
    }
}
