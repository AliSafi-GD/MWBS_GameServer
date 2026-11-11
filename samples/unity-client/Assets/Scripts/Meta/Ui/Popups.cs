using System;
using Game.Core;
using UnityEngine;

namespace Game.Meta
{
    /// <summary>
    /// The blocking dialogs a live game needs: server maintenance, a required or optional update, an ended session.
    /// They cannot be dismissed by tapping outside; every one ends in a button that does something.
    /// </summary>
    public static class Popups
    {
        public static Modal Maintenance(Transform parent, string message, string until, Action tryAgain)
        {
            var text = string.IsNullOrWhiteSpace(message) ? "We are making the game better.\nPlease come back in a little while." : message;
            if (!string.IsNullOrWhiteSpace(until))
                text += "\n\nBack around: " + until;

            return Dialog(parent, "MAINTENANCE", Icon.Gear, Theme.Gold, text, "TRY AGAIN", Theme.Green, Theme.GreenLip, tryAgain);
        }

        public static Modal UpdateRequired(Transform parent, string latest, string url)
        {
            var text = "A new version is needed to keep playing." + (string.IsNullOrEmpty(latest) ? "" : "\nLatest version: " + latest);
            // Stays open after the button: the player may come back from the store without updating.
            return Dialog(parent, "UPDATE REQUIRED", Icon.Bolt, Theme.Red, text, "UPDATE NOW", Theme.Green, Theme.GreenLip, () => OpenStore(url), closeOnPrimary: false);
        }

        public static Modal UpdateAvailable(Transform parent, string latest, string url, Action later)
        {
            var text = "A new version is ready" + (string.IsNullOrEmpty(latest) ? "." : ": " + latest + ".") + "\nUpdate for the newest fixes and features.";
            return Dialog(parent, "NEW VERSION", Icon.Bolt, Theme.Blue, text, "UPDATE", Theme.Green, Theme.GreenLip, () => OpenStore(url), "LATER", later);
        }

        public static Modal SessionEnded(Transform parent, Action signIn) =>
            Dialog(parent, "SIGNED OUT", Icon.People, Theme.Blue, "Your session has ended.\nPlease sign in again to continue.", "SIGN IN", Theme.Green, Theme.GreenLip, signIn);

        /// <summary>Asks before something that cannot be undone. The safe answer (cancel) is the easy one to reach.</summary>
        public static Modal Confirm(Transform parent, string title, string message, string confirmText, Action onConfirm) =>
            Dialog(parent, title, Icon.Bolt, Theme.Red, message, "CANCEL", Theme.Blue, Theme.BlueLip, null, confirmText, onConfirm, true, true);

        public static Modal Message(Transform parent, string title, string body, string button, Action onTap) =>
            Dialog(parent, title, Icon.Crown, Theme.Gold, body, button, Theme.Green, Theme.GreenLip, onTap);

        private static void OpenStore(string url)
        {
            if (!string.IsNullOrEmpty(url))
                Application.OpenURL(url);
            else
                Debug.LogWarning("[Popups] the server sent no update link");
        }

        /// <summary>One icon, a message and one or two buttons.</summary>
        private static Modal Dialog(Transform parent, string title, Icon icon, Color iconColor, string message,
            string primary, Color face, Color lip, Action onPrimary, string secondary = null, Action onSecondary = null, bool closeOnPrimary = true, bool dangerSecondary = false)
        {
            var modal = Modal.Create(parent, title, 860, 840, false);

            var glow = Ui.Pic(modal.Body, Art.Glow(), new Color(iconColor.r, iconColor.g, iconColor.b, 0.45f), "Glow");
            Ui.Pin(glow, 0.5f, 0.8f, 420, 420);
            var picture = Ui.IconPic(modal.Body, icon, iconColor);
            Ui.Pin(picture, 0.5f, 0.8f, 190, 190);
            if (icon == Icon.Gear)
                picture.gameObject.AddComponent<Spin>().DegreesPerSecond = -40f;
            else
                picture.gameObject.AddComponent<Bob>().Height = 8f;

            var label = Ui.Label(modal.Body, message, 42, Color.white, TextAnchor.MiddleCenter, true, true);
            Ui.Place(label, 0.02f, 0.3f, 0.98f, 0.66f);

            var first = GButton.Create(modal.Body, primary, face, lip, 52, 14);
            first.Button.onClick.AddListener(() =>
            {
                if (closeOnPrimary)
                    modal.Close();
                onPrimary?.Invoke();
            });

            if (secondary == null)
            {
                Ui.Place(first, 0.12f, 0.02f, 0.88f, 0.24f);
                return modal;
            }

            Ui.Place(first, 0.04f, 0.02f, 0.52f, 0.24f);
            var second = GButton.Create(modal.Body, secondary, dangerSecondary ? Theme.Red : Theme.PanelLight, dangerSecondary ? Theme.RedLip : Theme.Panel, 44, 12);
            Ui.Place(second, 0.56f, 0.02f, 0.96f, 0.24f);
            second.Button.onClick.AddListener(() =>
            {
                modal.Close();
                onSecondary?.Invoke();
            });
            return modal;
        }
    }
}
