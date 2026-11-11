using System.Collections.Generic;
using System.Linq;
using Game.Core;
using GameServer.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>Home: the arena, the Battle button, and four reward chests (the daily reward and three quests).</summary>
    public sealed class BattlePage : Page
    {
        private sealed class Slot
        {
            public RectTransform Root;
            public Image Glow;
            public Image Chest;
            public Text Title;
            public Text State;
            public ProgressBar Bar;
            public Image ReadyTag;
            public System.Action OnTap;
        }

        private readonly Slot[] _slots = new Slot[4];
        private Text _wins;
        private GButton _battle;

        protected override void Build()
        {
            var title = Ui.Label(Root, "TANK ARENA", 118);
            Ui.Pin(title, 0.5f, 0.93f, 960, 150);
            var sub = Ui.Label(Root, "TRAINING CAMP", 44, Theme.Muted);
            Ui.Pin(sub, 0.5f, 0.865f, 700, 60);

            var plaque = Ui.Panel(Root, new Color(Theme.PanelDark.r, Theme.PanelDark.g, Theme.PanelDark.b, 0.85f), 40f, "Wins");
            Ui.Pin(plaque, 0.5f, 0.80f, 560, 96);
            var cup = Ui.IconPic(plaque.transform, Icon.Trophy, Theme.Gold);
            Ui.Pin(cup, 0f, 0.5f, 78, 78, 30, 0);
            _wins = Ui.Label(plaque.transform, "0 wins", 48, Color.white);
            Ui.Stretch(_wins.rectTransform);
            Ui.Inset(_wins, 110, 0, 20, 0);

            BuildEmblem();
            BuildChests();
            BuildButtons();

            S.Goals.Changed += Refresh;
        }

        public override void Dispose() => S.Goals.Changed -= Refresh;

        public override void OnShow()
        {
            Refresh();
            C.Run(async () => await S.Goals.RefreshAsync());
        }

        private void BuildEmblem()
        {
            var holder = Ui.NewRect(Root, "Emblem");
            Ui.Pin(holder, 0.5f, 0.62f, 700, 700);

            var glow = Ui.Pic(holder, Art.Glow(), new Color(1f, 0.82f, 0.3f, 0.55f), "Glow");
            Ui.Pin(glow, 0.5f, 0.5f, 820, 820);
            glow.gameObject.AddComponent<Pulse>().Amount = 0.05f;

            var plate = Ui.Circle(holder, new Color(Theme.Outline.r, Theme.Outline.g, Theme.Outline.b, 0.9f), "Plate");
            Ui.Pin(plate, 0.5f, 0.5f, 440, 440);
            var plateInner = Ui.Circle(holder, Theme.PanelLight, "PlateInner");
            Ui.Pin(plateInner, 0.5f, 0.5f, 408, 408);
            var core = Ui.Circle(holder, Theme.Panel, "PlateCore");
            Ui.Pin(core, 0.5f, 0.5f, 340, 340);

            var swords = Ui.IconPic(holder, Icon.Swords, Theme.Gold);
            Ui.Pin(swords, 0.5f, 0.5f, 280, 280);
            swords.gameObject.AddComponent<Bob>();
        }

        private void BuildChests()
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var slot = new Slot();
                var card = Parts.Card(Root, Theme.PanelDark, 30f);
                var rim = card.parent;
                slot.Root = (RectTransform)rim;
                Ui.Pin(slot.Root.GetComponent<Image>(), 0f, 0.31f, 240, 330, 30 + i * 260, 0);

                slot.Glow = Ui.Pic(card, Art.Glow(), new Color(0.4f, 1f, 0.4f, 0.0f), "Glow");
                Ui.Pin(slot.Glow, 0.5f, 0.55f, 260, 260);

                slot.Chest = Ui.IconPic(card, Icon.Chest);
                Ui.Pin(slot.Chest, 0.5f, 0.62f, 150, 150);

                slot.Title = Ui.Label(card, "", 34, Color.white);
                Ui.Pin(slot.Title, 0.5f, 0.97f, 220, 54, 0, -4);
                slot.Title.rectTransform.pivot = new Vector2(0.5f, 1f);

                slot.Bar = ProgressBar.Create(card, Theme.Green);
                Ui.Pin(slot.Bar, 0.5f, 0.0f, 200, 44, 0, 30);
                slot.Bar.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0f);

                slot.ReadyTag = Ui.Panel(card, Theme.Green, 26f, "Ready");
                Ui.Pin(slot.ReadyTag, 0.5f, 0.0f, 200, 62, 0, 24);
                slot.ReadyTag.rectTransform.pivot = new Vector2(0.5f, 0f);
                var readyLabel = Ui.Label(slot.ReadyTag.transform, "OPEN", 40);
                Ui.Stretch(readyLabel.rectTransform);
                slot.ReadyTag.gameObject.AddComponent<Pulse>();

                slot.State = Ui.Label(card, "", 28, Theme.Muted);
                Ui.Pin(slot.State, 0.5f, 0.0f, 220, 50, 0, 20);
                slot.State.rectTransform.pivot = new Vector2(0.5f, 0f);

                var tap = card.gameObject.AddComponent<Button>();
                tap.transition = Selectable.Transition.None;
                var s = slot;
                tap.onClick.AddListener(() => s.OnTap?.Invoke());
                card.GetComponent<Image>().raycastTarget = true;

                _slots[i] = slot;
            }
        }

        private void BuildButtons()
        {
            _battle = GButton.Create(Root, "BATTLE", Theme.Gold, Theme.GoldLip, 96, 22);
            Ui.Pin(_battle, 0.5f, 0.105f, 600, 230);
            _battle.gameObject.AddComponent<Pulse>().Amount = 0.035f;
            _battle.Button.onClick.AddListener(StartSearch);

            var party = GButton.Create(Root, "", Theme.Blue, Theme.BlueLip, 30, 14);
            Ui.Pin(party, 0f, 0.105f, 190, 190, 40, 0);
            var partyIcon = Ui.IconPic(party.Face.transform, Icon.People);
            Ui.Place(partyIcon, 0.18f, 0.18f, 0.82f, 0.82f);
            party.Button.onClick.AddListener(() => C.Host.GoToSocial(1));

            var friendly = GButton.Create(Root, "", Theme.Purple, Hex(0x6A2FC0), 30, 14);
            Ui.Pin(friendly, 1f, 0.105f, 190, 190, -40, 0);
            var crown = Ui.IconPic(friendly.Face.transform, Icon.Crown);
            Ui.Place(crown, 0.18f, 0.18f, 0.82f, 0.82f);
            friendly.Button.onClick.AddListener(OpenFriendly);
        }

        private static Color Hex(int rgb) => Theme.Hex(rgb);

        private void StartSearch()
        {
            if (!S.Play.HasRanked)
            {
                C.Toasts.Show("Quick match is not set up on this server", Theme.Red);
                return;
            }

            C.Run(() => S.Play.FindMatchAsync(), _battle);
        }

        // ---- chests ------------------------------------------------------------------------------------------

        private void Refresh()
        {
            _wins.text = Ui.Num(S.Goals.Wins) + (S.Goals.Wins == 1 ? " win" : " wins");

            UpdateDailySlot(_slots[0]);
            var quests = S.Goals.Quests;
            for (var i = 1; i < _slots.Length; i++)
                UpdateQuestSlot(_slots[i], i - 1 < quests.Count ? quests[i - 1] : null);
        }

        private void UpdateDailySlot(Slot slot)
        {
            var daily = S.Goals.Daily;
            slot.Title.text = "DAILY";
            slot.Bar.gameObject.SetActive(false);

            if (daily == null)
            {
                SetSlot(slot, ready: false, done: false, state: "...");
                slot.OnTap = null;
                return;
            }

            if (daily.ClaimedToday)
            {
                SetSlot(slot, false, true, "Day " + daily.Streak + " done");
                slot.OnTap = () => C.Toast("Next reward tomorrow", Icon.Chest);
                return;
            }

            SetSlot(slot, true, false, "");
            slot.OnTap = () => C.Run(async () =>
            {
                var claim = await S.Goals.ClaimDailyAsync();
                C.Reward(claim.Reward, "Day " + claim.Day);
            });
        }

        private void UpdateQuestSlot(Slot slot, Goal goal)
        {
            if (goal == null)
            {
                slot.Title.text = "QUEST";
                slot.Bar.gameObject.SetActive(false);
                SetSlot(slot, false, false, "No quest");
                slot.OnTap = null;
                return;
            }

            slot.Title.text = goal.Name.ToUpperInvariant();
            if (goal.Claimed)
            {
                slot.Bar.gameObject.SetActive(false);
                SetSlot(slot, false, true, "Done");
                slot.OnTap = null;
            }
            else if (goal.Completed)
            {
                slot.Bar.gameObject.SetActive(false);
                SetSlot(slot, true, false, "");
                var id = goal.Id;
                slot.OnTap = () => C.Run(async () =>
                {
                    var claim = await S.Goals.ClaimQuestAsync(id);
                    C.Reward(claim.Reward, goal.Name);
                });
            }
            else
            {
                SetSlot(slot, false, false, "");
                slot.Bar.gameObject.SetActive(true);
                slot.Bar.Set(goal.Target <= 0 ? 0 : (float)goal.Progress / goal.Target, goal.Progress + "/" + goal.Target);
                slot.OnTap = () => C.Toast(goal.Name + ": " + goal.Progress + " / " + goal.Target, Icon.Scroll);
            }
        }

        private static void SetSlot(Slot slot, bool ready, bool done, string state)
        {
            slot.ReadyTag.gameObject.SetActive(ready);
            slot.Glow.color = new Color(0.4f, 1f, 0.4f, ready ? 0.55f : 0f);
            slot.Chest.color = done ? new Color(1, 1, 1, 0.4f) : (ready ? Color.white : new Color(0.75f, 0.78f, 0.9f, 1f));
            slot.State.text = state;

            var bob = slot.Chest.GetComponent<Bob>();
            if (ready && bob == null)
                slot.Chest.gameObject.AddComponent<Bob>().Height = 8f;
            else if (!ready && bob != null)
                Object.Destroy(bob);
        }

        // ---- friendly matches --------------------------------------------------------------------------------

        private void OpenFriendly()
        {
            if (!S.Play.HasFriendly)
            {
                C.Toasts.Show("Friendly battles are not set up on this server", Theme.Red);
                return;
            }

            var modal = Modal.Create(C.Overlay, "FRIENDLY BATTLE", 920, 1100);
            var create = GButton.Create(modal.Body, "CREATE ROOM", Theme.Green, Theme.GreenLip, 56, 14);
            Ui.Place(create, 0.05f, 0.88f, 0.95f, 1f);
            create.Button.onClick.AddListener(() => C.Run(() => S.Play.CreatePrivateAsync(S.Account.Username + "'s room"), create));

            var info = Ui.Label(modal.Body, "or join an open room", 36, Theme.Muted);
            Ui.Place(info, 0f, 0.8f, 1f, 0.87f);

            var list = ScrollList.Create(modal.Body);
            Ui.Place(list, 0f, 0f, 1f, 0.8f);
            Fill(list);
        }

        private void Fill(ScrollList list)
        {
            C.Run(async () =>
            {
                var rooms = await S.Play.ListPrivateAsync();
                if (list == null)
                    return;

                list.Clear();
                if (rooms.Count == 0)
                {
                    var empty = Ui.Label(list.Content, "No open rooms yet.", 40, Theme.Muted);
                    Parts.Size(empty, null, 140);
                    return;
                }

                foreach (var room in rooms)
                {
                    var id = room.Id;
                    var card = Parts.Card(list.Content, Theme.PanelLight, 30f);
                    Parts.Size(card.parent.GetComponent<Image>(), null, 150);

                    var name = Ui.Label(card, string.IsNullOrEmpty(room.Label) ? "Room" : room.Label, 44, Color.white, TextAnchor.MiddleLeft);
                    Ui.Place(name, 0.04f, 0.4f, 0.62f, 0.95f);
                    var count = Ui.Label(card, room.Players + " / " + room.MaxPlayers + " players", 32, Theme.Muted, TextAnchor.MiddleLeft);
                    Ui.Place(count, 0.04f, 0.05f, 0.62f, 0.45f);

                    var join = GButton.Create(card, "JOIN", Theme.Green, Theme.GreenLip, 44, 12);
                    Ui.Place(join, 0.64f, 0.14f, 0.97f, 0.86f);
                    join.Interactable = room.Open && room.Players < room.MaxPlayers;
                    join.Button.onClick.AddListener(() => C.Run(() => S.Play.JoinPrivateAsync(id), join));
                }
            });
        }
    }
}
