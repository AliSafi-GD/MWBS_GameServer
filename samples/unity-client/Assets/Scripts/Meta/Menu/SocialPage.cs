using System.Linq;
using Game.Core;
using GameServer.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>Friends, party and the lobby chat.</summary>
    public sealed class SocialPage : Page
    {
        private const int FriendsTab = 0, PartyTab = 1, ChatTab = 2;

        private Segmented _tabs;
        private RectTransform _body;
        private ScrollList _list;
        private InputBox _input;
        private GButton _send;
        private bool _visible;

        protected override void Build()
        {
            Parts.Title(Root, "SOCIAL");

            _tabs = Segmented.Create(Root, new[] { "FRIENDS", "PARTY", "CHAT" }, i => Render());
            Ui.Place(_tabs, 0.05f, 0.80f, 0.95f, 0.875f);
            _tabs.Select(0, false);

            _body = Ui.NewRect(Root, "Body");
            Ui.Place(_body, 0f, 0f, 1f, 0.79f);

            S.Social.Changed += OnSocialChanged;
            S.Chat.MessageAdded += OnChatLine;
        }

        public override void Dispose()
        {
            S.Social.Changed -= OnSocialChanged;
            S.Chat.MessageAdded -= OnChatLine;
        }

        /// <summary>Lets other pages jump straight to a tab (the party button on the home page).</summary>
        public void Open(int tab) => _tabs.Select(tab, false);

        public override void OnShow()
        {
            _visible = true;
            Render();
            C.Run(async () =>
            {
                await S.Social.RefreshAsync();
                if (_tabs.Selected == ChatTab)
                    await S.Chat.JoinAsync();
            });
        }

        public override void OnHide() => _visible = false;

        private void OnSocialChanged()
        {
            if (_visible && _tabs.Selected != ChatTab)
                Render();
        }

        private void OnChatLine(ChatLine line)
        {
            if (_visible && _tabs.Selected == ChatTab && _list != null)
            {
                AddBubble(line);
                _list.ScrollToBottom();
            }
        }

        private void Render()
        {
            for (var i = _body.childCount - 1; i >= 0; i--)
                Object.Destroy(_body.GetChild(i).gameObject);
            _list = null;
            _input = null;

            switch (_tabs.Selected)
            {
                case PartyTab: RenderParty(); break;
                case ChatTab: RenderChat(); break;
                default: RenderFriends(); break;
            }
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
            var rim = Parts.Card(parent).parent;
            var card = (RectTransform)rim.GetChild(0);
            Parts.Size(rim.GetComponent<Image>(), null, 160);

            var avatar = Ui.Avatar(card, name, 110, f.Profile.Avatar);
            Ui.Pin(avatar, 0f, 0.5f, 110, 110, 24, 0);

            var label = Ui.Label(card, name, 44, Color.white, TextAnchor.MiddleLeft);
            Ui.Place(label, 0.19f, 0.5f, 0.6f, 0.95f);

            string status;
            Color statusColor;
            switch (f.State)
            {
                case FriendStates.InviteReceived: status = "wants to be friends"; statusColor = Theme.Gold; break;
                case FriendStates.InviteSent: status = "request sent"; statusColor = Theme.Muted; break;
                default: status = f.Online ? "online" : "offline"; statusColor = f.Online ? Theme.Green : Theme.Muted; break;
            }

            var sub = Ui.Label(card, status, 32, statusColor, TextAnchor.MiddleLeft);
            Ui.Place(sub, 0.19f, 0.05f, 0.6f, 0.5f);

            if (f.State == FriendStates.InviteReceived)
            {
                var accept = GButton.Create(card, "ACCEPT", Theme.Green, Theme.GreenLip, 36, 10);
                Ui.Place(accept, 0.62f, 0.2f, 0.97f, 0.8f);
                accept.Button.onClick.AddListener(() => C.Run(async () => await S.Social.AcceptFriendAsync(id), accept));
                return;
            }

            var remove = GButton.Create(card, f.State == FriendStates.InviteSent ? "CANCEL" : "REMOVE", Theme.Red, Theme.RedLip, 34, 10);
            Ui.Place(remove, 0.62f, 0.2f, 0.97f, 0.8f);
            remove.Button.onClick.AddListener(() => C.Run(async () => await S.Social.RemoveFriendAsync(id), remove));
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
                    var rim = Parts.Card(list.Content, Theme.Purple).parent;
                    var card = (RectTransform)rim.GetChild(0);
                    Parts.Size(rim.GetComponent<Image>(), null, 170);
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

            foreach (var m in party.Members)
            {
                var memberId = m.UserId;
                var name = Parts.NameOf(m.DisplayName, m.Username);
                var rim = Parts.Card(list.Content).parent;
                var card = (RectTransform)rim.GetChild(0);
                Parts.Size(rim.GetComponent<Image>(), null, 140);

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
                    var rim = Parts.Card(list.Content).parent;
                    var card = (RectTransform)rim.GetChild(0);
                    Parts.Size(rim.GetComponent<Image>(), null, 130);
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

        // ---- chat --------------------------------------------------------------------------------------------

        private void RenderChat()
        {
            _list = ScrollList.Create(_body, 10, 14);
            Ui.Place(_list, 0f, 0.13f, 1f, 1f);

            _input = InputBox.Create(_body, "Say something");
            Ui.Place(_input, 0.04f, 0.01f, 0.74f, 0.115f);
            _send = GButton.Create(_body, "SEND", Theme.Blue, Theme.BlueLip, 42, 12);
            Ui.Place(_send, 0.76f, 0.01f, 0.96f, 0.115f);
            _send.Button.onClick.AddListener(SendChat);

            foreach (var line in S.Chat.Lines)
                AddBubble(line);
            _list.ScrollToBottom();

            C.Run(async () =>
            {
                var before = S.Chat.Lines.Count;
                await S.Chat.JoinAsync();
                if (_list != null && S.Chat.Lines.Count != before)
                {
                    _list.Clear();
                    foreach (var line in S.Chat.Lines)
                        AddBubble(line);
                    _list.ScrollToBottom();
                }
            });
        }

        private void SendChat()
        {
            var text = _input.Text.Trim();
            if (text.Length == 0)
                return;

            _input.Text = "";
            C.Run(async () => await S.Chat.SendAsync(text), _send);
        }

        private void AddBubble(ChatLine line)
        {
            var row = Ui.NewRect(_list.Content, "Row");
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = line.Mine ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
            h.childControlWidth = false;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;

            var bubble = Ui.Panel(row, line.Mine ? Theme.Blue : Theme.PanelLight, 34f, "Bubble");
            Parts.Size(bubble, 700, null);
            var v = bubble.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(28, 28, 14, 18);
            v.spacing = 2;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandHeight = false;

            if (!line.Mine)
            {
                var name = Ui.Label(bubble.transform, line.Sender, 30, Theme.Gold, TextAnchor.MiddleLeft, true, false);
                Parts.Size(name, null, 38);
            }

            var text = Ui.Label(bubble.transform, line.Text, 38, Color.white, TextAnchor.UpperLeft, false, false);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
        }
    }
}
