using System;
using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>The profile page: avatar, display name, username, stats and account deletion.</summary>
    public sealed partial class MenuScreen
    {
        private const int NameMaxLength = 24;

        /// <summary>Redraws the avatar and name in the top bar (after the player edited the profile).</summary>
        private void RefreshIdentity()
        {
            var account = _session.Account;
            if (account == null || _identity == null)
                return;

            var name = Parts.NameOf(account.DisplayName, account.Username);
            if (_avatarView != null)
                Destroy(_avatarView.gameObject);

            _avatarView = Ui.Avatar(_identity, name, 112, account.Avatar);
            Ui.Pin(_avatarView, 0f, 0.5f, 112, 112, 28, 0);
            _avatarView.SetAsFirstSibling();
            _name.text = name;
        }

        public void OpenProfile()
        {
            var account = _session.Account;
            var modal = Modal.Create(_overlay, "PROFILE", 940, 1420);
            var body = modal.Body;

            var selected = account.Avatar;
            var shownName = Parts.NameOf(account.DisplayName, account.Username);

            // preview of the chosen avatar
            var previewHolder = Ui.NewRect(body, "Preview");
            Ui.Pin(previewHolder, 0.5f, 0.9f, 200, 200);
            RectTransform preview = null;
            void ShowPreview()
            {
                if (preview != null)
                    Destroy(preview.gameObject);
                preview = Ui.Avatar(previewHolder, shownName, 200, selected);
                Ui.Stretch(preview);
            }

            ShowPreview();

            var pick = Ui.Label(body, "CHOOSE YOUR AVATAR", 34, Theme.Muted);
            Ui.Place(pick, 0f, 0.775f, 1f, 0.82f);

            // avatar choices
            var row = Ui.NewRect(body, "Avatars");
            Ui.Place(row, 0f, 0.68f, 1f, 0.77f);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            var rings = new Image[Ui.AvatarPresets.Length];
            for (var i = 0; i < Ui.AvatarPresets.Length; i++)
            {
                var id = Ui.AvatarId(i);
                var holder = Ui.NewRect(row, "Choice" + i);
                holder.sizeDelta = new Vector2(96, 96);
                Parts.Size(holder.gameObject.AddComponent<LayoutElement>(), 96, 96);

                rings[i] = Ui.Circle(holder, Theme.Gold, "Selected");
                Ui.Pin(rings[i], 0.5f, 0.5f, 112, 112);
                rings[i].gameObject.SetActive(selected == id);

                var avatar = Ui.Avatar(holder, shownName, 96, id);
                Ui.Stretch(avatar);
                var hit = avatar.GetComponent<Image>();
                hit.raycastTarget = true;
                var index = i;
                avatar.gameObject.AddComponent<Button>().onClick.AddListener(() =>
                {
                    selected = Ui.AvatarId(index);
                    for (var r = 0; r < rings.Length; r++)
                        rings[r].gameObject.SetActive(r == index);
                    ShowPreview();
                });
            }

            // names
            var nameLabel = Ui.Label(body, "DISPLAY NAME", 32, Theme.Muted, TextAnchor.MiddleLeft);
            Ui.Place(nameLabel, 0.03f, 0.605f, 0.97f, 0.65f);
            var displayInput = InputBox.Create(body, "Your name");
            displayInput.Text = account.DisplayName ?? "";
            displayInput.Field.characterLimit = NameMaxLength;
            Ui.Place(displayInput, 0f, 0.51f, 1f, 0.6f);

            var userLabel = Ui.Label(body, "USERNAME", 32, Theme.Muted, TextAnchor.MiddleLeft);
            Ui.Place(userLabel, 0.03f, 0.455f, 0.97f, 0.5f);
            var userInput = InputBox.Create(body, "username");
            userInput.Text = account.Username ?? "";
            userInput.Field.characterLimit = 32;
            Ui.Place(userInput, 0f, 0.36f, 1f, 0.45f);

            // stats
            var played = _session.Goals.MatchesPlayed;
            var stats = Ui.Label(body,
                Ui.Num(_session.Goals.Wins) + " wins   -   " + Ui.Num(played) + " matches\nPlaying since " + account.CreatedUtc.ToString("yyyy-MM-dd"),
                36, Theme.Gold);
            Ui.Place(stats, 0f, 0.235f, 1f, 0.33f);

            var save = GButton.Create(body, "SAVE", Theme.Green, Theme.GreenLip, 56, 14);
            Ui.Place(save, 0.1f, 0.1f, 0.9f, 0.215f);
            save.Button.onClick.AddListener(() =>
            {
                var display = displayInput.Text.Trim();
                var user = userInput.Text.Trim();
                var newDisplay = display.Length > 0 && display != (account.DisplayName ?? "") ? display : null;
                var newUser = user.Length > 0 && user != account.Username ? user : null;
                var newAvatar = selected != account.Avatar ? selected : null;

                if (newDisplay == null && newUser == null && newAvatar == null)
                {
                    _context.Toast("Nothing to save", Icon.Gear);
                    return;
                }

                _context.Run(async () =>
                {
                    await _session.Profile.SaveAsync(newDisplay, newUser, newAvatar);
                    _context.Toasts.Show("Profile saved", Theme.Green, Icon.Check);
                    modal.Close();
                }, save);
            });

            var delete = GButton.Create(body, "DELETE ACCOUNT", Theme.Red, Theme.RedLip, 34, 10);
            Ui.Place(delete, 0.28f, 0.0f, 0.72f, 0.075f);
            delete.Button.onClick.AddListener(() => ConfirmDelete(modal));
        }

        private void ConfirmDelete(Modal profile)
        {
            Popups.Confirm(_overlay, "DELETE ACCOUNT?",
                "All your progress, coins and friends will be erased.\nThis cannot be undone.",
                "DELETE", () => _context.Run(async () =>
                {
                    await _session.DeleteAccountAsync();
                    profile.Close();
                    SceneManager.LoadScene("Boot");
                }));
        }
    }
}
