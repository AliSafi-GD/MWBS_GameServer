using System;
using System.Collections.Generic;
using System.Linq;
using GameServer.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>A text field in the game's style.</summary>
    public sealed class InputBox : MonoBehaviour
    {
        public InputField Field { get; private set; }

        public string Text
        {
            get => Field.text;
            set => Field.text = value;
        }

        public static InputBox Create(Transform parent, string placeholder, int fontSize = 40)
        {
            var back = Ui.Panel(parent, Theme.PanelDark, 30f, "Input");
            var box = back.gameObject.AddComponent<InputBox>();

            var text = Ui.Label(back.transform, "", fontSize, Color.white, TextAnchor.MiddleLeft, false, false);
            Ui.Stretch(text.rectTransform);
            Ui.Inset(text, 28, 6, 28, 6);
            text.supportRichText = false;

            var hint = Ui.Label(back.transform, placeholder, fontSize, Theme.Muted, TextAnchor.MiddleLeft, false, false);
            Ui.Stretch(hint.rectTransform);
            Ui.Inset(hint, 28, 6, 28, 6);
            hint.fontStyle = FontStyle.Italic;

            var field = back.gameObject.AddComponent<InputField>();
            field.targetGraphic = back;
            field.textComponent = text;
            field.placeholder = hint;
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = 120;
            box.Field = field;
            return box;
        }
    }

    /// <summary>Small recipes shared by several pages.</summary>
    public static class Parts
    {
        /// <summary>A framed card; put children into the returned rect.</summary>
        public static RectTransform Card(Transform parent, Color? fill = null, float radius = 36f)
        {
            var rim = Ui.Panel(parent, Theme.Outline, radius + 6f, "Card");
            var inner = Ui.Panel(rim.transform, fill ?? Theme.PanelLight, radius, "Inner");
            Ui.Inset(inner, 6, 6, 6, 6);
            return inner.rectTransform;
        }

        public static T Size<T>(T component, float? width = null, float? height = null) where T : Component
        {
            var le = component.GetComponent<LayoutElement>() ?? component.gameObject.AddComponent<LayoutElement>();
            if (width.HasValue)
            {
                le.preferredWidth = width.Value;
                le.minWidth = width.Value;
            }

            if (height.HasValue)
            {
                le.preferredHeight = height.Value;
                le.minHeight = height.Value;
            }

            return component;
        }

        /// <summary>A small top-down tank drawn from rounded shapes, in the given body color. Used on skin cards.</summary>
        public static RectTransform TankPreview(Transform parent, Color body, float scale, Vector2 anchor)
        {
            var root = Ui.NewRect(parent, "Tank");
            Ui.Pin(root, anchor.x, anchor.y, 260 * scale, 200 * scale);

            var tread = Theme.Hex(0x1D2330);
            var top = Ui.Panel(root, tread, 14f, "TreadTop");
            Ui.Place(top, 0.05f, 0.76f, 0.95f, 0.98f);
            var bottom = Ui.Panel(root, tread, 14f, "TreadBottom");
            Ui.Place(bottom, 0.05f, 0.02f, 0.95f, 0.24f);

            var hull = Ui.Panel(root, body, 18f, "Hull");
            Ui.Place(hull, 0.1f, 0.2f, 0.9f, 0.8f);
            var plate = Ui.Panel(root, Color.Lerp(body, Color.white, 0.35f), 12f, "Plate");
            Ui.Place(plate, 0.16f, 0.36f, 0.5f, 0.64f);

            var barrel = Ui.Panel(root, Theme.Hex(0x2A3040), 8f, "Barrel");
            Ui.Place(barrel, 0.5f, 0.43f, 1.0f, 0.57f);
            var turret = Ui.Circle(root, Color.Lerp(body, Color.black, 0.15f), "Turret");
            Ui.Pin(turret, 0.5f, 0.5f, 100 * scale, 100 * scale);
            var cap = Ui.Circle(root, Color.Lerp(body, Color.white, 0.4f), "Cap");
            Ui.Pin(cap, 0.5f, 0.5f, 46 * scale, 46 * scale);
            return root;
        }

        /// <summary>Big banner title for the top of a page.</summary>
        public static Text Title(Transform parent, string text)
        {
            var ribbon = Ui.Panel(parent, Theme.Blue, 40f, "Title");
            var lip = Ui.Panel(parent, Theme.BlueLip, 40f, "TitleLip");
            Ui.Pin(lip, 0.5f, 1f, 640, 130, 0, -26);
            Ui.Pin(ribbon, 0.5f, 1f, 640, 130, 0, -14);
            lip.transform.SetSiblingIndex(ribbon.transform.GetSiblingIndex());
            var label = Ui.Label(ribbon.transform, text, 72);
            Ui.Stretch(label.rectTransform);
            return label;
        }

        public static Icon IconFor(string currencyOrItem)
        {
            switch (currencyOrItem)
            {
                case "coins": return Icon.Coin;
                case "gems": return Icon.Gem;
                default: return Icon.Chest;
            }
        }

        /// <summary>The first thing a bundle contains, as an icon and an amount ("x50").</summary>
        public static (Icon Icon, string Text, string Name) Headline(Bundle bundle)
        {
            if (bundle?.Wallet != null && bundle.Wallet.Count > 0)
            {
                var kv = bundle.Wallet.OrderByDescending(p => p.Key == "gems").First();
                return (IconFor(kv.Key), "x" + Ui.Num(kv.Value), kv.Key);
            }

            if (bundle?.Items != null && bundle.Items.Count > 0)
            {
                var kv = bundle.Items.First();
                return (Icon.Chest, "x" + Ui.Num(kv.Value), kv.Key);
            }

            return (Icon.Chest, "", "");
        }

        public static string Describe(Bundle bundle)
        {
            if (bundle == null)
                return "";

            var parts = new List<string>();
            if (bundle.Wallet != null)
                parts.AddRange(bundle.Wallet.Select(kv => "+" + Ui.Num(kv.Value) + " " + kv.Key));
            if (bundle.Items != null)
                parts.AddRange(bundle.Items.Select(kv => "+" + Ui.Num(kv.Value) + " " + Pretty(kv.Key)));
            return string.Join("  ", parts);
        }

        /// <summary>The name to show for a player: the display name when they set one (the server sends "" otherwise), else the username.</summary>
        public static string NameOf(string displayName, string username) =>
            !string.IsNullOrEmpty(displayName) ? displayName : (string.IsNullOrEmpty(username) ? "Player" : username);

        public static string Pretty(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "";

            var s = id.Replace('_', ' ');
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>Maps server error codes to words a player understands.</summary>
        public static string ErrorText(Exception ex)
        {
            if (ex is ApiException api)
            {
                if (api.IsNetworkError)
                    return "No connection to the server";

                switch (api.Code)
                {
                    case "insufficient_funds": return "Not enough coins";
                    case "insufficient_items": return "You are missing an item";
                    case "product_unavailable": return "Not available right now";
                    case "purchase_limit_reached":
                    case "limit_reached": return "Purchase limit reached";
                    case "goal_not_complete": return "Not finished yet";
                    case "already_claimed": return "Already claimed";
                    case "daily_already_claimed": return "Come back tomorrow";
                    case "already_queued": return "Already searching";
                    case "not_friends": return "You are not friends";
                    case "friend_limit": return "Friend list is full";
                    case "already_in_party": return "Already in a party";
                    case "party_full": return "The party is full";
                    case "not_party_leader": return "Only the leader can do that";
                    case "match_full": return "That match is full";
                    case "match_not_found": return "That match is gone";
                    case "app_version": return "Please update the game";
                    case "maintenance": return "The server is under maintenance";
                    default: return Pretty(api.Code ?? "error");
                }
            }

            return ex.Message;
        }
    }

    /// <summary>A GButton with a price: a currency icon and an amount.</summary>
    public static class PriceButton
    {
        public static GButton Create(Transform parent, Bundle cost, bool enabled, string freeText = "FREE")
        {
            var b = GButton.Create(parent, "", Theme.Green, Theme.GreenLip, 46, 12);
            b.Interactable = enabled;

            var head = Parts.Headline(cost);
            if (string.IsNullOrEmpty(head.Text))
            {
                b.SetText(freeText);
                return b;
            }

            b.SetText(head.Text.TrimStart('x'));
            var icon = Ui.IconPic(b.Face.transform, head.Icon);
            Ui.Pin(icon, 0f, 0.5f, 70, 70, 30, 0);
            Ui.Inset(b.Label, 90, 0, 10, 0);
            return b;
        }
    }
}
