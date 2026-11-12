using System.Collections.Generic;
using System.Linq;
using Game.Core;
using GameServer.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>Friends, party, chat (lobby, friends, party, clan) and the clan.</summary>
    public sealed class SocialPage : Page
    {
        public const int FriendsTab = 0, PartyTab = 1, ChatTab = 2, ClanTab = 3;

        private Segmented _tabs;
        private RectTransform _body;
        private ScrollList _list;
        private InputBox _input;
        private GButton _send;
        private bool _visible;

        /// <summary>The conversation shown on the chat tab; null shows the list of conversations.</summary>
        private ChatThread _thread;

        /// <summary>Clans found by the last search; null until the first search of this visit.</summary>
        private List<Group> _clanResults;
        private bool _clanSearching;

        protected override void Build()
        {
            Parts.Title(Root, "SOCIAL");

            _tabs = Segmented.Create(Root, new[] { "FRIENDS", "PARTY", "CHAT", "CLAN" }, i => Render());
            Ui.Place(_tabs, 0.05f, 0.80f, 0.95f, 0.875f);
            _tabs.Select(0, false);

            _body = Ui.NewRect(Root, "Body");
            Ui.Place(_body, 0f, 0f, 1f, 0.79f);

            S.Social.Changed += OnSocialChanged;
            S.Chat.MessageAdded += OnChatLine;
            S.Chat.ThreadChanged += OnThreadChanged;
            S.Chat.UnreadChanged += OnUnreadChanged;
        }

        public override void Dispose()
        {
            S.Social.Changed -= OnSocialChanged;
            S.Chat.MessageAdded -= OnChatLine;
            S.Chat.ThreadChanged -= OnThreadChanged;
            S.Chat.UnreadChanged -= OnUnreadChanged;
            S.Chat.Close(_thread);
        }

        /// <summary>Lets other pages jump straight to a tab (the party button on the home page).</summary>
        public void Open(int tab)
        {
            S.Chat.Close(_thread);
            _thread = null;
            _tabs.Select(tab, false);
        }

        /// <summary>Jumps to the chat tab with one conversation open.</summary>
        public void OpenChat(ChatThread thread)
        {
            S.Chat.Close(_thread);
            _thread = thread;
            _tabs.Select(ChatTab, false);
        }

        public override void OnShow()
        {
            _visible = true;
            _clanResults = null;
            Render();
            C.Run(() => S.Social.RefreshAsync());
        }

        public override void OnHide()
        {
            _visible = false;
            S.Chat.Close(_thread);
        }

        private void OnSocialChanged()
        {
            // An open conversation is only redrawn when it went away (left the party, kicked from the clan).
            if (_visible && (_tabs.Selected != ChatTab || _thread == null || !S.Chat.Has(_thread)))
                Render();
        }

        private void OnChatLine(ChatThread thread, ChatLine line)
        {
            if (!_visible || _tabs.Selected != ChatTab)
                return;

            if (_thread == null)
            {
                Render();
            }
            else if (thread == _thread && _list != null)
            {
                AddBubble(line);
                _list.ScrollToBottom();
            }
        }

        private void OnThreadChanged(ChatThread thread)
        {
            if (!_visible || _tabs.Selected != ChatTab)
                return;

            if (_thread == null)
                Render();
            else if (thread == _thread && _list != null)
                FillBubbles();
        }

        private void OnUnreadChanged()
        {
            // The conversation list, and the chat buttons of the party and clan tabs, show unread counts.
            // Tabs with a text field are left alone so typing is not interrupted.
            if (!_visible)
                return;

            var tab = _tabs.Selected;
            if ((tab == ChatTab && _thread == null) || tab == PartyTab || (tab == ClanTab && S.Social.Clan != null))
                Render();
        }

        private void Render()
        {
            for (var i = _body.childCount - 1; i >= 0; i--)
                Object.Destroy(_body.GetChild(i).gameObject);
            _list = null;
            _input = null;

            if (_tabs.Selected != ChatTab)
            {
                S.Chat.Close(_thread);
                _thread = null;
            }

            switch (_tabs.Selected)
            {
                case PartyTab: RenderParty(); break;
                case ChatTab: RenderChat(); break;
                case ClanTab: RenderClan(); break;
                default: RenderFriends(); break;
            }
        }

        /// <summary>A framed card of a fixed height in a list.</summary>
        private static RectTransform Card(Transform parent, float height, Color? fill = null)
        {
            var card = Parts.Card(parent, fill);
            Parts.Size(card.parent.GetComponent<Image>(), null, height);
            return card;
        }

        private void ShowChat(ChatThread thread)
        {
            OpenChat(thread);
            Render();
        }

        // ---- friends -----------------------------------------------------------------------------------------

        private void RenderFriends()
        {
            var input = InputBox.Create(_body, "Add friend by username");
            Ui.Place(input, 0.04f, 0.905f, 0.68f, 0.995f);
            var add = GButton.Create(_body, "ADD", Theme.Green, Theme.GreenLip, 46, 12);
            Ui.Place(add, 0.7f, 0.905f, 0.96f, 0.995f);
            add.Button.onClick.AddListener(() =>
            {
                var name = input.Text.Trim();
                if (name.Length == 0)
                    return;

                input.Text = "";
                C.Run(async () =>
                {
                    var state = await S.Social.AddFriendAsync(name);
                    C.Toast(state == FriendStates.Friend ? name + " is now your friend" : "Request sent to " + name, Icon.People);
                }, add);
            });

            var list = ScrollList.Create(_body, 14, 14);
            Ui.Place(list, 0f, 0f, 1f, 0.89f);

            var friends = S.Social.Friends.Where(f => f.State != FriendStates.Blocked).OrderBy(f => f.State == FriendStates.InviteReceived ? 0 : f.Online ? 1 : 2).ToList();
            if (friends.Count == 0)
            {
                var empty = Ui.Label(list.Content, "No friends yet.\nType a username above.", 44, Theme.Muted);
                Parts.Size(empty, 900, 260);
                return;
            }

            foreach (var f in friends)
                AddFriendCard(list.Content, f);
        }

        private void AddFriendCard(Transform parent, Friend f)
        {
            var name = Parts.NameOf(f.Profile.DisplayName, f.Profile.Username);
            var id = f.Profile.Id;
            var isFriend = f.State == FriendStates.Friend;
            var card = Card(parent, 160);

            var avatar = Ui.Avatar(card, name, 110, f.Profile.Avatar);
            Ui.Pin(avatar, 0f, 0.5f, 110, 110, 24, 0);

            var textEnd = isFriend ? 0.5f : 0.6f;
            var label = Ui.Label(card, name, 44, Color.white, TextAnchor.MiddleLeft);
            Ui.Place(label, 0.19f, 0.5f, textEnd, 0.95f);

            string status;
            Color statusColor;
            switch (f.State)
            {
                case FriendStates.InviteReceived: status = "wants to be friends"; statusColor = Theme.Gold; break;
                case FriendStates.InviteSent: status = "request sent"; statusColor = Theme.Muted; break;
                default: status = f.Online ? "online" : "offline"; statusColor = f.Online ? Theme.Green : Theme.Muted; break;
            }

            var sub = Ui.Label(card, status, 32, statusColor, TextAnchor.MiddleLeft);
            Ui.Place(sub, 0.19f, 0.05f, textEnd, 0.5f);

            if (f.State == FriendStates.InviteReceived)
            {
                var accept = GButton.Create(card, "ACCEPT", Theme.Green, Theme.GreenLip, 36, 10);
                Ui.Place(accept, 0.62f, 0.2f, 0.97f, 0.8f);
                accept.Button.onClick.AddListener(() => C.Run(async () => await S.Social.AcceptFriendAsync(id), accept));
                return;
            }

            if (isFriend)
            {
                var thread = S.Chat.Direct(id, name);
                var chat = GButton.Create(card, "CHAT", Theme.Blue, Theme.BlueLip, 34, 10);
                Ui.Place(chat, 0.51f, 0.2f, 0.73f, 0.8f);
                chat.Button.onClick.AddListener(() => ShowChat(thread));
                AddUnread(chat.transform, thread);
            }

            var remove = GButton.Create(card, f.State == FriendStates.InviteSent ? "CANCEL" : "REMOVE", Theme.Red, Theme.RedLip, 34, 10);
            Ui.Place(remove, isFriend ? 0.75f : 0.62f, 0.2f, 0.97f, 0.8f);
            remove.Button.onClick.AddListener(() => C.Run(async () => await S.Social.RemoveFriendAsync(id), remove));
        }

        /// <summary>A red count on the corner of a chat button.</summary>
        private static void AddUnread(Transform button, ChatThread thread)
        {
            if (thread == null || thread.Unread == 0)
                return;

            var badge = Badge.Create(button);
            Ui.Pin(badge, 1f, 1f, 56, 56, 6, 6);
            badge.Set(thread.Unread);
        }

        // ---- party -------------------------------------------------------------------------------------------

        private void RenderParty()
        {
            var list = ScrollList.Create(_body, 14, 14);
            Ui.Place(list, 0f, 0f, 1f, 1f);

            var party = S.Social.Party;
            if (party == null)
            {
                var create = GButton.Create(list.Content, "CREATE PARTY", Theme.Green, Theme.GreenLip, 60, 14);
                Parts.Size(create, null, 150);
                create.Button.onClick.AddListener(() => C.Run(async () => await S.Social.CreatePartyAsync(), create));

                var invite = S.Social.PendingInvite;
                if (invite != null)
                {
                    var card = Card(list.Content, 170, Theme.Purple);
                    var text = Ui.Label(card, "You were invited to a party", 40, Color.white, TextAnchor.MiddleLeft);
                    Ui.Place(text, 0.04f, 0.1f, 0.62f, 0.9f);
                    var join = GButton.Create(card, "JOIN", Theme.Green, Theme.GreenLip, 44, 12);
                    Ui.Place(join, 0.66f, 0.18f, 0.97f, 0.82f);
                    join.Button.onClick.AddListener(() => C.Run(async () => await S.Social.JoinInvitedPartyAsync(), join));
                }
                else
                {
                    var hint = Ui.Label(list.Content, "Play with friends: make a party,\ninvite them, then hit Battle.", 40, Theme.Muted);
                    Parts.Size(hint, 900, 220);
                }

                return;
            }

            var me = S.Account.Id;
            var leader = party.LeaderId == me;
            var header = Ui.Label(list.Content, "YOUR PARTY  " + party.Members.Count + " / " + party.MaxSize, 48, Theme.Gold);
            Parts.Size(header, null, 90);

            var partyThread = S.Chat.PartyThread;
            if (partyThread != null)
            {
                var chat = GButton.Create(list.Content, "PARTY CHAT", Theme.Blue, Theme.BlueLip, 46, 12);
                Parts.Size(chat, null, 120);
                chat.Button.onClick.AddListener(() => ShowChat(partyThread));
                AddUnread(chat.transform, partyThread);
            }

            foreach (var m in party.Members)
            {
                var memberId = m.UserId;
                var name = Parts.NameOf(m.DisplayName, m.Username);
                var card = Card(list.Content, 140);

                var avatar = Ui.Avatar(card, name, 100, m.Avatar);
                Ui.Pin(avatar, 0f, 0.5f, 100, 100, 24, 0);
                var label = Ui.Label(card, name + (memberId == me ? " (you)" : ""), 42, Color.white, TextAnchor.MiddleLeft);
                Ui.Place(label, 0.18f, 0.1f, 0.62f, 0.9f);

                if (memberId == party.LeaderId)
                {
                    var crown = Ui.IconPic(card, Icon.Crown, Theme.Gold);
                    Ui.Pin(crown, 1f, 0.5f, 80, 80, -30, 0);
                }
                else if (leader)
                {
                    var kick = GButton.Create(card, "KICK", Theme.Red, Theme.RedLip, 34, 10);
                    Ui.Place(kick, 0.7f, 0.2f, 0.97f, 0.8f);
                    kick.Button.onClick.AddListener(() => C.Run(async () => await S.Social.KickFromPartyAsync(memberId), kick));
                }
            }

            var online = S.Social.Friends.Where(f => f.State == FriendStates.Friend && f.Online && party.Members.All(m => m.UserId != f.Profile.Id)).ToList();
            if (online.Count > 0)
            {
                var title = Ui.Label(list.Content, "INVITE FRIENDS", 40, Theme.Muted);
                Parts.Size(title, null, 80);
                foreach (var f in online)
                {
                    var friendId = f.Profile.Id;
                    var name = Parts.NameOf(f.Profile.DisplayName, f.Profile.Username);
                    var card = Card(list.Content, 130);
                    var label = Ui.Label(card, name, 42, Color.white, TextAnchor.MiddleLeft);
                    Ui.Place(label, 0.05f, 0.1f, 0.6f, 0.9f);
                    var invite = GButton.Create(card, "INVITE", Theme.Blue, Theme.BlueLip, 36, 10);
                    Ui.Place(invite, 0.64f, 0.2f, 0.97f, 0.8f);
                    invite.Button.onClick.AddListener(() => C.Run(async () =>
                    {
                        await S.Social.InviteToPartyAsync(friendId);
                        C.Toast("Invited " + name, Icon.People);
                    }, invite));
                }
            }

            var leave = GButton.Create(list.Content, "LEAVE PARTY", Theme.Red, Theme.RedLip, 50, 12);
            Parts.Size(leave, null, 130);
            leave.Button.onClick.AddListener(() => C.Run(async () => await S.Social.LeavePartyAsync(), leave));
        }

        // ---- clan --------------------------------------------------------------------------------------------

        private void RenderClan()
        {
            var clan = S.Social.Clan;
            if (clan == null)
            {
                RenderClanFinder();
                return;
            }

            var list = ScrollList.Create(_body, 14, 14);
            Ui.Place(list, 0f, 0f, 1f, 1f);

            var group = clan.Group;
            var header = Ui.Label(list.Content, group.Name.ToUpperInvariant() + "  " + S.Social.ClanMembers.Count + " / " + group.MaxMembers, 48, Theme.Gold);
            Parts.Size(header, null, 90);

            var clanThread = S.Chat.ClanThread;
            if (clanThread != null)
            {
                var chat = GButton.Create(list.Content, "CLAN CHAT", Theme.Blue, Theme.BlueLip, 46, 12);
                Parts.Size(chat, null, 120);
                chat.Button.onClick.AddListener(() => ShowChat(clanThread));
                AddUnread(chat.transform, clanThread);
            }

            var me = S.Account.Id;
            foreach (var m in S.Social.ClanMembers.OrderBy(m => m.Role == "owner" ? 0 : m.Role == "admin" ? 1 : 2))
            {
                var name = Parts.NameOf(m.Profile.DisplayName, m.Profile.Username);
                var card = Card(list.Content, 140);

                var avatar = Ui.Avatar(card, name, 100, m.Profile.Avatar);
                Ui.Pin(avatar, 0f, 0.5f, 100, 100, 24, 0);
                var label = Ui.Label(card, name + (m.Profile.Id == me ? " (you)" : ""), 42, Color.white, TextAnchor.MiddleLeft);
                Ui.Place(label, 0.18f, 0.5f, 0.8f, 0.95f);
                var role = Ui.Label(card, m.Role, 32, m.Role == "member" ? Theme.Muted : Theme.Gold, TextAnchor.MiddleLeft);
                Ui.Place(role, 0.18f, 0.05f, 0.8f, 0.5f);

                if (m.Role == "owner")
                {
                    var crown = Ui.IconPic(card, Icon.Crown, Theme.Gold);
                    Ui.Pin(crown, 1f, 0.5f, 80, 80, -30, 0);
                }
            }

            // The server only lets an owner leave an empty clan, so with members left the owner's way out is to disband it.
            var disband = S.Social.OwnsClan && S.Social.ClanMembers.Count > 1;
            var leave = GButton.Create(list.Content, disband ? "DISBAND CLAN" : "LEAVE CLAN", Theme.Red, Theme.RedLip, 50, 12);
            Parts.Size(leave, null, 130);
            leave.Button.onClick.AddListener(() =>
            {
                if (disband)
                    Popups.Confirm(C.Overlay, "DISBAND CLAN", "This removes " + group.Name + " and its chat\nfor every member. It cannot be undone.", "DISBAND",
                        () => C.Run(async () => await S.Social.DisbandClanAsync(), leave));
                else
                    C.Run(async () => await S.Social.LeaveClanAsync(), leave);
            });
        }

        /// <summary>No clan yet: create one, answer an invite, or search and join.</summary>
        private void RenderClanFinder()
        {
            var nameBox = InputBox.Create(_body, "New clan name");
            Ui.Place(nameBox, 0.04f, 0.905f, 0.68f, 0.995f);
            var create = GButton.Create(_body, "CREATE", Theme.Green, Theme.GreenLip, 42, 12);
            Ui.Place(create, 0.7f, 0.905f, 0.96f, 0.995f);
            create.Button.onClick.AddListener(() =>
            {
                var name = nameBox.Text.Trim();
                if (name.Length == 0)
                    return;

                C.Run(async () =>
                {
                    await S.Social.CreateClanAsync(name);
                    C.Toast("Clan " + name + " created", Icon.Crown);
                }, create);
            });

            var searchBox = InputBox.Create(_body, "Search clans");
            Ui.Place(searchBox, 0.04f, 0.795f, 0.68f, 0.885f);
            var find = GButton.Create(_body, "FIND", Theme.Blue, Theme.BlueLip, 42, 12);
            Ui.Place(find, 0.7f, 0.795f, 0.96f, 0.885f);
            find.Button.onClick.AddListener(() => SearchClans(searchBox.Text, find));

            var list = ScrollList.Create(_body, 14, 14);
            Ui.Place(list, 0f, 0f, 1f, 0.78f);

            foreach (var invited in S.Social.ClanInvites)
                AddClanCard(list.Content, invited, true);

            if (_clanResults == null)
            {
                if (!_clanSearching)
                    SearchClans("", null);
                return;
            }

            var invitedIds = S.Social.ClanInvites.Select(g => g.Id).ToList();
            var found = _clanResults.Where(g => !invitedIds.Contains(g.Id)).ToList();
            if (found.Count == 0 && invitedIds.Count == 0)
            {
                var empty = Ui.Label(list.Content, "No clans found.\nCreate the first one above.", 44, Theme.Muted);
                Parts.Size(empty, 900, 260);
                return;
            }

            foreach (var g in found)
                AddClanCard(list.Content, g, false);
        }

        private void SearchClans(string query, GButton button)
        {
            _clanSearching = true;
            C.Run(async () =>
            {
                try
                {
                    _clanResults = await S.Social.SearchClansAsync(query);
                }
                finally
                {
                    _clanSearching = false;
                }

                if (_visible && _tabs.Selected == ClanTab && S.Social.Clan == null)
                    Render();
            }, button);
        }

        private void AddClanCard(Transform parent, Group g, bool invited)
        {
            var id = g.Id;
            var name = g.Name;
            var card = Card(parent, 150, invited ? Theme.Purple : (Color?)null);

            var crest = Ui.IconPic(card, Icon.Crown, Theme.Gold);
            Ui.Pin(crest, 0f, 0.5f, 90, 90, 30, 0);
            var label = Ui.Label(card, name, 44, Color.white, TextAnchor.MiddleLeft);
            Ui.Place(label, 0.17f, 0.5f, 0.62f, 0.95f);
            var sub = Ui.Label(card, invited ? "invited you" : g.MemberCount + " / " + g.MaxMembers + " members", 32, invited ? Theme.Gold : Theme.Muted, TextAnchor.MiddleLeft);
            Ui.Place(sub, 0.17f, 0.05f, 0.62f, 0.5f);

            var full = g.MemberCount >= g.MaxMembers;
            var join = GButton.Create(card, full ? "FULL" : invited ? "ACCEPT" : g.Open ? "JOIN" : "REQUEST", Theme.Green, Theme.GreenLip, 36, 10);
            Ui.Place(join, 0.64f, 0.2f, 0.97f, 0.8f);
            join.Interactable = !full;
            join.Button.onClick.AddListener(() => C.Run(async () =>
            {
                var role = await S.Social.JoinClanAsync(id);
                C.Toast(role == "requested" ? "Request sent to " + name : "Welcome to " + name, Icon.Crown);
            }, join));
        }

        // ---- chat --------------------------------------------------------------------------------------------

        private void RenderChat()
        {
            if (_thread != null && !S.Chat.Has(_thread))
                _thread = null;

            if (_thread == null)
                RenderChatList();
            else
                RenderThread();
        }

        private void RenderChatList()
        {
            var list = ScrollList.Create(_body, 14, 14);
            Ui.Place(list, 0f, 0f, 1f, 1f);

            AddThreadCard(list.Content, S.Chat.Lobby, "LOBBY", "Everyone who is online", Icon.People, null, null);
            if (S.Chat.PartyThread != null)
                AddThreadCard(list.Content, S.Chat.PartyThread, "PARTY", "Your party", Icon.Swords, null, null);
            if (S.Chat.ClanThread != null)
                AddThreadCard(list.Content, S.Chat.ClanThread, S.Chat.ClanThread.Title.ToUpperInvariant(), "Your clan", Icon.Crown, null, null);

            var friends = S.Social.Friends.Where(f => f.State == FriendStates.Friend).ToDictionary(f => f.Profile.Id);

            // Friends, plus anyone else who wrote to us. Unread first, then the latest conversation.
            var direct = S.Chat.DirectThreads
                .Where(t => friends.ContainsKey(t.TargetId) || t.Lines.Count > 0)
                .OrderByDescending(t => t.Unread > 0)
                .ThenByDescending(t => t.Last != null ? t.Last.At : System.DateTime.MinValue)
                .ThenBy(t => t.Title)
                .ToList();

            if (direct.Count == 0)
            {
                var hint = Ui.Label(list.Content, "Add friends to send them messages.", 40, Theme.Muted);
                Parts.Size(hint, 900, 160);
                return;
            }

            var title = Ui.Label(list.Content, "MESSAGES", 40, Theme.Muted);
            Parts.Size(title, null, 80);
            foreach (var t in direct)
            {
                friends.TryGetValue(t.TargetId, out var f);
                AddThreadCard(list.Content, t, t.Title, f == null ? "" : f.Online ? "online" : "offline", null, t.Title, f?.Profile.Avatar);
            }
        }

        private void AddThreadCard(Transform parent, ChatThread thread, string title, string idle, Icon? icon, string avatarName, string avatarId)
        {
            var card = Card(parent, 150);
            var face = card.GetComponent<Image>();
            face.raycastTarget = true;
            var button = card.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            button.onClick.AddListener(() => ShowChat(thread));

            if (icon.HasValue)
            {
                var back = Ui.Circle(card, Theme.PanelDark, "IconBack");
                Ui.Pin(back, 0f, 0.5f, 110, 110, 24, 0);
                var pic = Ui.IconPic(back.transform, icon.Value, Theme.Gold);
                Ui.Place(pic, 0.18f, 0.18f, 0.82f, 0.82f);
            }
            else
            {
                var avatar = Ui.Avatar(card, avatarName, 110, avatarId);
                Ui.Pin(avatar, 0f, 0.5f, 110, 110, 24, 0);
            }

            var label = Ui.Label(card, title, 44, Color.white, TextAnchor.MiddleLeft);
            Ui.Place(label, 0.19f, 0.5f, 0.84f, 0.95f);

            var last = thread.Last;
            var preview = last == null ? idle : (last.Mine ? "You: " : thread.Kind == ChatKind.Direct ? "" : last.Sender + ": ") + last.Text;
            preview = preview.Replace('\n', ' ');
            if (preview.Length > 36)
                preview = preview.Substring(0, 35) + "...";
            var sub = Ui.Label(card, preview, 32, thread.Unread > 0 ? Color.white : Theme.Muted, TextAnchor.MiddleLeft);
            Ui.Place(sub, 0.19f, 0.05f, 0.84f, 0.5f);

            var badge = Badge.Create(card);
            Ui.Pin(badge, 1f, 0.5f, 64, 64, -30, 0);
            badge.Set(thread.Unread);
        }

        private void RenderThread()
        {
            var thread = _thread;

            var back = GButton.Create(_body, "BACK", Theme.PanelLight, Theme.Panel, 34, 10);
            Ui.Place(back, 0.04f, 0.905f, 0.26f, 0.995f);
            back.Button.onClick.AddListener(() =>
            {
                S.Chat.Close(_thread);
                _thread = null;
                Render();
            });

            var title = Ui.Label(_body, thread.Title.ToUpperInvariant(), 46, Theme.Gold);
            Ui.Place(title, 0.28f, 0.905f, 0.96f, 0.995f);

            _list = ScrollList.Create(_body, 10, 14);
            Ui.Place(_list, 0f, 0.13f, 1f, 0.895f);

            _input = InputBox.Create(_body, "Say something");
            Ui.Place(_input, 0.04f, 0.01f, 0.74f, 0.115f);
            _send = GButton.Create(_body, "SEND", Theme.Blue, Theme.BlueLip, 42, 12);
            Ui.Place(_send, 0.76f, 0.01f, 0.96f, 0.115f);
            _send.Button.onClick.AddListener(SendChat);

            FillBubbles();

            // Joins the channel and loads the stored messages; ThreadChanged then redraws the bubbles.
            C.Run(() => S.Chat.OpenAsync(thread));
        }

        private void FillBubbles()
        {
            _list.Clear();
            foreach (var line in _thread.Lines)
                AddBubble(line);
            _list.ScrollToBottom();
        }

        private void SendChat()
        {
            var text = _input.Text.Trim();
            var thread = _thread;
            if (text.Length == 0 || thread == null)
                return;

            _input.Text = "";
            C.Run(() => S.Chat.SendAsync(thread, text), _send);
        }

        private void AddBubble(ChatLine line)
        {
            const float MaxBubble = 760f, Pad = 28f, NameHeight = 40f, MinBubble = 180f;

            var row = Ui.NewRect(_list.Content, "Row");
            var bubble = Ui.Panel(row, line.Mine ? Theme.Blue : Theme.PanelLight, 34f, "Bubble");

            var text = Ui.Label(bubble.transform, line.Text, 38, Color.white, TextAnchor.UpperLeft, false, false);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            // Between two players the title already says who is writing.
            Text name = null;
            if (!line.Mine && _thread.Kind != ChatKind.Direct)
                name = Ui.Label(bubble.transform, line.Sender, 30, Theme.Gold, TextAnchor.MiddleLeft, true, false);

            // Measure the text first and size the bubble to it, so the list never depends on nested layout passes.
            var textWidth = Mathf.Min(Measure(text, line.Text, 0f).x, MaxBubble - Pad * 2f);
            if (name != null)
                textWidth = Mathf.Max(textWidth, Mathf.Min(Measure(name, line.Sender, 0f).x, MaxBubble - Pad * 2f));
            var width = Mathf.Clamp(textWidth + Pad * 2f + 4f, MinBubble, MaxBubble);
            var textHeight = Measure(text, line.Text, width - Pad * 2f).y;
            var nameHeight = name != null ? NameHeight : 0f;
            var height = Mathf.Max(84f, textHeight + nameHeight + 34f);

            Parts.Size(row, null, height);
            Ui.Pin(bubble, line.Mine ? 1f : 0f, 1f, width, height, line.Mine ? -8f : 8f, 0f);

            if (name != null)
                Ui.Pin(name, 0f, 1f, width - Pad * 2f, NameHeight - 4f, Pad, -10f);
            Ui.Pin(text, 0f, 1f, width - Pad * 2f, textHeight + 6f, Pad, -(12f + nameHeight));
        }

        /// <summary>The size a text needs: wrapped to <paramref name="maxWidth"/>, or on one line when it is 0.</summary>
        private static Vector2 Measure(Text label, string content, float maxWidth)
        {
            var settings = label.GetGenerationSettings(new Vector2(maxWidth, 0f));
            if (maxWidth <= 0f)
                settings.horizontalOverflow = HorizontalWrapMode.Overflow;

            var generator = label.cachedTextGeneratorForLayout;
            var w = generator.GetPreferredWidth(content, settings) / label.pixelsPerUnit;
            var h = generator.GetPreferredHeight(content, settings) / label.pixelsPerUnit;
            return new Vector2(w, h);
        }
    }
}
