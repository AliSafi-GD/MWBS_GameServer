using System.Collections.Generic;
using GameServer.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>The daily reward, quests and achievements.</summary>
    public sealed class QuestsPage : Page
    {
        private Segmented _tabs;
        private ScrollList _list;

        protected override void Build()
        {
            Parts.Title(Root, "QUESTS");

            _tabs = Segmented.Create(Root, new[] { "QUESTS", "ACHIEVEMENTS" }, i => Rebuild());
            Ui.Place(_tabs, 0.05f, 0.80f, 0.95f, 0.875f);
            _tabs.Select(0, false);

            _list = ScrollList.Create(Root, 18, 18);
            Ui.Place(_list, 0f, 0f, 1f, 0.79f);

            S.Goals.Changed += Rebuild;
        }

        public override void Dispose() => S.Goals.Changed -= Rebuild;

        public override void OnShow()
        {
            Rebuild();
            C.Run(async () => await S.Goals.RefreshAsync());
        }

        private void Rebuild()
        {
            _list.Clear();
            var showQuests = _tabs.Selected <= 0;

            if (showQuests)
                AddDaily();

            var goals = showQuests ? S.Goals.Quests : S.Goals.Achievements;
            if (goals.Count == 0)
            {
                var empty = Ui.Label(_list.Content, showQuests ? "No quests today." : "No achievements yet.", 44, Theme.Muted);
                Parts.Size(empty, 900, 160);
                return;
            }

            foreach (var g in goals)
                AddGoal(g, showQuests);
        }

        private void AddDaily()
        {
            var daily = S.Goals.Daily;
            if (daily == null)
                return;

            var rim = Parts.Card(_list.Content, Theme.Purple).parent;
            var card = (RectTransform)rim.GetChild(0);
            Parts.Size(rim.GetComponent<Image>(), null, 230);

            var chest = Ui.IconPic(card, Icon.Chest);
            Ui.Pin(chest, 0f, 0.5f, 170, 170, 30, 0);

            var title = Ui.Label(card, "DAILY REWARD", 48, Color.white, TextAnchor.MiddleLeft);
            Ui.Place(title, 0.22f, 0.55f, 0.66f, 0.95f);

            var reward = "";
            if (daily.Rewards != null && daily.NextDay >= 1 && daily.NextDay <= daily.Rewards.Count)
                reward = Parts.Describe(daily.Rewards[daily.NextDay - 1]);
            var detail = Ui.Label(card, "Day " + daily.NextDay + "   " + reward, 32, Theme.Gold, TextAnchor.MiddleLeft);
            Ui.Place(detail, 0.22f, 0.12f, 0.66f, 0.56f);

            var claim = GButton.Create(card, daily.ClaimedToday ? "CLAIMED" : "CLAIM", Theme.Green, Theme.GreenLip, 42, 12);
            Ui.Place(claim, 0.68f, 0.18f, 0.97f, 0.82f);
            claim.Interactable = !daily.ClaimedToday;
            claim.Button.onClick.AddListener(() => C.Run(async () =>
            {
                var result = await S.Goals.ClaimDailyAsync();
                C.Reward(result.Reward, "Day " + result.Day);
            }, claim));
        }

        private void AddGoal(Goal g, bool isQuest)
        {
            var rim = Parts.Card(_list.Content).parent;
            var card = (RectTransform)rim.GetChild(0);
            Parts.Size(rim.GetComponent<Image>(), null, 210);

            var medal = Ui.Circle(card, g.Completed ? Theme.Gold : Theme.PanelDark, "Medal");
            Ui.Pin(medal, 0f, 0.5f, 130, 130, 28, 0);
            var medalIcon = Ui.IconPic(medal.transform, isQuest ? Icon.Scroll : Icon.Crown, g.Completed ? Color.white : Theme.Muted);
            Ui.Place(medalIcon, 0.18f, 0.18f, 0.82f, 0.82f);

            var name = Ui.Label(card, g.Name, 44, Color.white, TextAnchor.MiddleLeft);
            Ui.Place(name, 0.2f, 0.58f, 0.68f, 0.95f);

            var bar = ProgressBar.Create(card, g.Completed ? Theme.Green : Theme.Blue);
            Ui.Place(bar, 0.2f, 0.22f, 0.66f, 0.52f);
            bar.Set(g.Target <= 0 ? 0 : (float)g.Progress / g.Target, Ui.Num(System.Math.Min(g.Progress, g.Target)) + " / " + Ui.Num(g.Target));

            var head = Parts.Headline(g.Reward);
            if (!string.IsNullOrEmpty(head.Text))
            {
                var icon = Ui.IconPic(card, head.Icon);
                Ui.Pin(icon, 1f, 0.8f, 56, 56, -150, 0);
                var amount = Ui.Label(card, head.Text, 38, Theme.Gold, TextAnchor.MiddleRight);
                Ui.Pin(amount, 1f, 0.8f, 120, 56, -26, 0);
            }

            var claim = GButton.Create(card, g.Claimed ? "DONE" : "CLAIM", Theme.Green, Theme.GreenLip, 36, 12);
            Ui.Pin(claim, 1f, 0.26f, 220, 80, -24, 0);
            claim.Interactable = g.Completed && !g.Claimed;
            var id = g.Id;
            claim.Button.onClick.AddListener(() => C.Run(async () =>
            {
                var result = isQuest ? await S.Goals.ClaimQuestAsync(id) : await S.Goals.ClaimAchievementAsync(id);
                C.Reward(result.Reward, g.Name);
            }, claim));
        }
    }
}
