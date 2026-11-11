using Game.Core;
using GameServer.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>The leaderboards.</summary>
    public sealed class RankPage : Page
    {
        private Segmented _boards;
        private ScrollList _list;
        private int _request;

        protected override void Build()
        {
            Parts.Title(Root, "RANKING");

            _boards = Segmented.Create(Root, GameIds.BoardNames, i => Load());
            Ui.Place(_boards, 0.05f, 0.80f, 0.95f, 0.875f);
            _boards.Select(0, false);

            _list = ScrollList.Create(Root, 12, 16);
            Ui.Place(_list, 0f, 0f, 1f, 0.79f);
        }

        public override void OnShow() => Load();

        private void Load()
        {
            var id = GameIds.BoardIds[Mathf.Max(_boards.Selected, 0)];
            var ticket = ++_request;
            C.Run(async () =>
            {
                var board = await S.Ranks.GetAsync(id);
                if (ticket != _request || _list == null)
                    return;

                Show(board);
            });
        }

        private void Show(Board board)
        {
            _list.Clear();
            var me = S.Account.Id;

            if (board.Entries == null || board.Entries.Count == 0)
            {
                var empty = Ui.Label(_list.Content, "Nobody here yet.\nWin a battle to take the first place!", 44, Theme.Muted);
                Parts.Size(empty, 900, 300);
                return;
            }

            var shown = false;
            foreach (var e in board.Entries)
            {
                AddRow(e, e.UserId == me);
                shown |= e.UserId == me;
            }

            if (!shown && board.Me != null)
            {
                var gap = Ui.Label(_list.Content, "...", 44, Theme.Muted);
                Parts.Size(gap, null, 50);
                AddRow(board.Me, true);
            }
        }

        private void AddRow(BoardEntry e, bool mine)
        {
            var rim = Parts.Card(_list.Content, mine ? Theme.Blue : Theme.PanelLight, 30f).parent;
            var card = (RectTransform)rim.GetChild(0);
            Parts.Size(rim.GetComponent<Image>(), null, 140);

            var medal = Ui.Circle(card, MedalColor(e.Rank), "Rank");
            Ui.Pin(medal, 0f, 0.5f, 92, 92, 24, 0);
            var rank = Ui.Label(medal.transform, e.Rank.ToString(), 46);
            Ui.Stretch(rank.rectTransform);
            if (e.Rank == 1)
            {
                var crown = Ui.IconPic(card, Icon.Crown, Theme.Gold);
                Ui.Pin(crown, 0f, 0.5f, 60, 60, 44, 56);
            }

            var name = Parts.NameOf(e.DisplayName, e.Username);
            var avatar = Ui.Avatar(card, name, 96, e.Avatar);
            Ui.Pin(avatar, 0f, 0.5f, 96, 96, 140, 0);

            var label = Ui.Label(card, mine ? name + " (you)" : name, 44, Color.white, TextAnchor.MiddleLeft);
            Ui.Place(label, 0.3f, 0.1f, 0.72f, 0.9f);

            var score = Ui.Label(card, Ui.Num(e.Score), 52, Theme.Gold, TextAnchor.MiddleRight);
            Ui.Place(score, 0.7f, 0.1f, 0.97f, 0.9f);
        }

        private static Color MedalColor(int rank)
        {
            switch (rank)
            {
                case 1: return Theme.Hex(0xE0A200);
                case 2: return Theme.Hex(0x9AA5BC);
                case 3: return Theme.Hex(0xB86A2B);
                default: return Theme.PanelDark;
            }
        }
    }
}
