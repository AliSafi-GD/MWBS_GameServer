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

            var backdrop = Ui.Gradient(canvas.transform, Theme.BgTop, Theme.BgBottom, "Backdrop");
            backdrop.raycastTarget = false;

            _column = Ui.Column(canvas.transform);
            var shine = Ui.Pic(_column, Art.Glow(), new Color(0.6f, 0.8f, 1f, 0.28f), "Shine");
            Ui.Pin(shine, 0.5f, 0.78f, 1500, 1500);

            _pageHost = Ui.NewRect(_column, "Pages");
            Ui.Place(_pageHost, 0f, 0.12f, 1f, 0.925f);

            BuildTopBar();
            BuildNavBar();

            _banner = Ui.Panel(_column, Theme.Hex(0xE57A12), 24f, "Offline");
            Ui.Pin(_banner, 0.5f, 0.925f, 760, 64, 0, -12);
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
            var bar = Ui.Panel(_column, new Color(Theme.PanelDark.r, Theme.PanelDark.g, Theme.PanelDark.b, 0.9f), 6f, "TopBar");
            Ui.Place(bar, 0f, 0.925f, 1f, 1f);

            // The whole left side (avatar and name) opens the profile; its pictures and texts do not catch taps themselves.
            var profileHit = Ui.Panel(bar.transform, new Color(0, 0, 0, 0), 4f, "ProfileButton");
            Ui.Place(profileHit, 0f, 0f, 0.45f, 1f);
            profileHit.raycastTarget = true;
            profileHit.gameObject.AddComponent<Button>().onClick.AddListener(OpenProfile);

            _identity = Ui.NewRect(bar.transform, "Identity");
            _identity.gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;
            _name = Ui.Label(_identity, "", 44, Color.white, TextAnchor.MiddleLeft);
            Ui.Pin(_name, 0f, 0.5f, 290, 60, 160, 28);
            var cup = Ui.IconPic(_identity, Icon.Trophy, Theme.Gold);
            Ui.Pin(cup, 0f, 0.5f, 46, 46, 160, -26);
            _wins = Ui.Label(_identity, "", 34, Theme.Muted, TextAnchor.MiddleLeft);
            Ui.Pin(_wins, 0f, 0.5f, 270, 44, 214, -26);
            RefreshIdentity();

            var gear = GButton.Create(bar.transform, "", Theme.Blue, Theme.BlueLip, 30, 10);
            Ui.Pin(gear, 1f, 0.5f, 88, 88, -22, 0);
            var gearIcon = Ui.IconPic(gear.Face.transform, Icon.Gear);
            Ui.Place(gearIcon, 0.15f, 0.15f, 0.85f, 0.85f);
            gear.Button.onClick.AddListener(OpenSettings);

            _gems = Chip.Create(bar.transform, Icon.Gem);
            Ui.Pin(_gems, 1f, 0.5f, 214, 76, -126, 0);
            _coins = Chip.Create(bar.transform, Icon.Coin, Theme.Gold);
            Ui.Pin(_coins, 1f, 0.5f, 250, 76, -352, 0);
        }

        private void BuildNavBar()
        {
            var bar = Ui.Panel(_column, Theme.PanelDark, 6f, "NavBar");
            Ui.Place(bar, 0f, 0f, 1f, 0.12f);
            var edge = Ui.Panel(bar.transform, Theme.Hex(0x4C7BE0), 4f, "Edge");
            Ui.Place(edge, 0f, 0.96f, 1f, 1f);

            for (var i = 0; i < _nav.Length; i++)
            {
                var index = i;
                var item = new NavItem();
                var slot = Ui.NewRect(bar.transform, "Nav_" + PageNames[i]);
                Ui.Place(slot, i / 5f, 0f, (i + 1) / 5f, 1f);
                item.Root = slot;

                item.Tile = Ui.Panel(slot, Color.clear, 30f, "Tile");
                Ui.Place(item.Tile, 0.06f, 0.04f, 0.94f, 1.02f);
                item.Tile.raycastTarget = true;
                var button = item.Tile.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => Show(index));

                item.Icon = Ui.IconPic(slot, PageIcons[i], Theme.Muted);
                Ui.Pin(item.Icon, 0.5f, 0.62f, 104, 104);

                item.Label = Ui.Label(slot, PageNames[i], 30, Color.white);
                Ui.Pin(item.Label, 0.5f, 0.0f, 200, 40, 0, 12);
                item.Label.rectTransform.pivot = new Vector2(0.5f, 0f);
                item.Label.gameObject.SetActive(false);

                item.Badge = Badge.Create(slot);
                Ui.Pin(item.Badge, 0.5f, 0.78f, 52, 52, 48, 0);

                _nav[i] = item;
            }
        }

        private void BuildSearchOverlay()
        {
            _search = new GameObject("Search", typeof(RectTransform));
            var rt = (RectTransform)_search.transform;
            rt.SetParent(_column, false);
            Ui.Stretch(rt);

            var dim = Ui.Panel(rt, new Color(0, 0, 0, 0.75f), 4f, "Dim");
            Ui.Stretch(dim.rectTransform);
            dim.raycastTarget = true;

            var rim = Ui.Panel(rt, Theme.Outline, 60f, "Rim");
            Ui.Pin(rim, 0.5f, 0.5f, 800, 760);
            var card = Ui.Panel(rim.transform, Theme.Panel, 54f, "Card");
            Ui.Inset(card, 7, 7, 7, 7);

            var title = Ui.Label(card.transform, "FINDING OPPONENT", 60);
            Ui.Pin(title, 0.5f, 1f, 700, 100, 0, -50);

            var ring = Ui.Circle(card.transform, Theme.PanelDark, "Ring");
            Ui.Pin(ring, 0.5f, 0.55f, 330, 330);
            var spinner = Ui.IconPic(ring.transform, Icon.Swords, Theme.Gold);
            Ui.Place(spinner, 0.12f, 0.12f, 0.88f, 0.88f);
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
            item.Tile.color = active ? new Color(accent.r, accent.g, accent.b, 0.95f) : Color.clear;
            item.Icon.color = active ? Color.white : Theme.Muted;
            item.Label.gameObject.SetActive(active);

            var target = active ? 1.28f : (i == BattleIndex ? 1.12f : 1f);
            var rt = item.Icon.rectTransform;
            var from = rt.localScale.x;
            var fromY = rt.anchoredPosition.y;
            var toY = active ? 18f : 0f;
            Anim.Run(this, 0.18f, t =>
            {
                var s = Mathf.Lerp(from, target, Anim.EaseOut(t));
                rt.localScale = new Vector3(s, s, 1f);
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, Mathf.Lerp(fromY, toY, Anim.EaseOut(t)));
            });
        }

        // ---- settings ----------------------------------------------------------------------------------------

        private void OpenSettings()
        {
            var modal = Modal.Create(_overlay, "SETTINGS", 880, 760);

            var who = Ui.Label(modal.Body, "Signed in as\n" + _session.Account.Username, 46, Color.white);
            Ui.Place(who, 0f, 0.62f, 1f, 1f);

            var server = Ui.Label(modal.Body, _session.ServerUrl, 34, Theme.Muted);
            Ui.Place(server, 0f, 0.46f, 1f, 0.62f);

            var version = Ui.Label(modal.Body, "Version " + Application.version, 32, Theme.Muted);
            Ui.Place(version, 0f, 0.34f, 1f, 0.46f);

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
