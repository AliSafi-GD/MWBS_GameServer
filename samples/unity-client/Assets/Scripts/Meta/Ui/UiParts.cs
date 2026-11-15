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
        /// <summary>
        /// A framed card: a dark rim that is thicker at the bottom (so the card looks raised), a bright edge line and a
        /// fill lit from above. Put children into the returned rect; its parent is the rim (size and place that one).
        /// </summary>
        public static RectTransform Card(Transform parent, Color? fill = null, float radius = 36f)
        {
            var color = fill ?? Theme.PanelLight;
            var rim = Ui.Panel(parent, Theme.Outline, radius + 6f, "Card", true);
            var inner = Ui.Panel(rim.transform, color, radius, "Inner");
            Ui.Inset(inner, 6, 13, 6, 6);

            var edge = Ui.Panel(inner.transform, Ui.Tint(color, 0.28f), radius, "Edge");
            edge.raycastTarget = false;
            var body = Ui.Panel(inner.transform, color, radius - 3f, "Body");
            Ui.Inset(body, 3, 3, 3, 4);
            body.raycastTarget = false;
            var light = Ui.Panel(inner.transform, new Color(1, 1, 1, 0.035f), radius - 8f, "Light", true);
            Ui.Place(light, 0f, 0.5f, 1f, 1f);
            Ui.Inset(light, 9, 0, 9, 9);
            light.raycastTarget = false;
            return inner.rectTransform;
        }

        /// <summary>A settings row: a title, a hint under it and an ON / OFF button on the right.</summary>
        public static RectTransform SwitchRow(Transform parent, string title, string hint, bool value, Action<bool> onChange)
        {
            var rim = Ui.Panel(parent, Theme.Outline, 34f, "Row_" + title, true);
            var fill = Ui.Panel(rim.transform, Theme.PanelDark, 30f, "Fill");
            Ui.Inset(fill, 4, 4, 4, 4);

            var name = Ui.Label(rim.transform, title, 40, Color.white, TextAnchor.MiddleLeft);
            Ui.Place(name, 0.05f, 0.5f, 0.64f, 0.94f);
            var sub = Ui.Label(rim.transform, hint, 26, Theme.Muted, TextAnchor.MiddleLeft, false);
            Ui.Place(sub, 0.05f, 0.06f, 0.64f, 0.52f);

            var button = GButton.Create(rim.transform, "", Theme.Green, Theme.GreenLip, 42, 10);
            Ui.Place(button, 0.68f, 0.17f, 0.96f, 0.83f);

            var state = value;
            Action paint = () =>
            {
                button.SetText(state ? "ON" : "OFF");
                button.SetColors(state ? Theme.Green : Theme.Grey, state ? Theme.GreenLip : Theme.GreyLip);
            };
            paint();
            button.Button.onClick.AddListener(() =>
            {
                state = !state;
                paint();
                onChange(state);
            });
            return rim.rectTransform;
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

        /// <summary>
        /// A top-down tank drawn from rounded shapes, in the given body color, facing right. Used on skin cards and,
        /// larger, as the hero of the home page.
        /// </summary>
        public static RectTransform TankPreview(Transform parent, Color body, float scale, Vector2 anchor)
        {
            var root = Ui.NewRect(parent, "Tank");
            Ui.Pin(root, anchor.x, anchor.y, 260 * scale, 200 * scale);

            var shadow = Ui.Shadow(root, 0.45f, 14f * scale, 16f * scale);
            shadow.raycastTarget = false;

            var dark = Color.Lerp(body, Theme.Outline, 0.62f);
            var steel = Theme.Hex(0x232A3D);
            var steelLight = Theme.Hex(0x4A556F);

            for (var side = 0; side < 2; side++)
            {
                var y0 = side == 0 ? 0.76f : 0.0f;
                var treadRim = Ui.Panel(root, Theme.Outline, 16f * scale, "TreadRim", true);
                Ui.Place(treadRim, 0.02f, y0 - 0.01f, 0.98f, y0 + 0.25f);
                var tread = Ui.Panel(root, steel, 13f * scale, "Tread");
                Ui.Place(tread, 0.035f, y0 + 0.012f, 0.965f, y0 + 0.228f);
                for (var i = 0; i < 9; i++)
                {
                    var link = Ui.Panel(tread.transform, steelLight, 3f * scale, "Link", true);
                    var x = 0.06f + i * 0.1f;
                    Ui.Place(link, x, 0.14f, x + 0.045f, 0.86f);
                }
            }

            var hullRim = Ui.Panel(root, Theme.Outline, 22f * scale, "HullRim", true);
            Ui.Place(hullRim, 0.075f, 0.17f, 0.925f, 0.83f);
            var hull = Ui.Panel(root, body, 18f * scale, "Hull");
            Ui.Place(hull, 0.095f, 0.2f, 0.905f, 0.8f);
            var hullShade = Ui.Panel(hull.transform, new Color(dark.r, dark.g, dark.b, 0.45f), 16f * scale, "Shade", true);
            Ui.Place(hullShade, 0f, 0f, 1f, 0.3f);
            var hullGloss = Ui.Panel(hull.transform, new Color(1, 1, 1, 0.3f), 12f * scale, "Gloss", true);
            Ui.Place(hullGloss, 0.04f, 0.74f, 0.96f, 0.93f);

            // Engine grille at the back, a lighter plate at the front.
            for (var i = 0; i < 3; i++)
            {
                var vent = Ui.Panel(hull.transform, new Color(dark.r, dark.g, dark.b, 0.85f), 3f * scale, "Vent", true);
                Ui.Place(vent, 0.07f + i * 0.06f, 0.3f, 0.1f + i * 0.06f, 0.7f);
            }

            var plate = Ui.Panel(hull.transform, Ui.Tint(body, 0.16f), 8f * scale, "Plate");
            Ui.Place(plate, 0.8f, 0.2f, 0.93f, 0.8f);

            var barrelRim = Ui.Panel(root, Theme.Outline, 9f * scale, "BarrelRim", true);
            Ui.Place(barrelRim, 0.5f, 0.405f, 1.08f, 0.595f);
            var barrel = Ui.Panel(root, Theme.Hex(0x59647F), 6f * scale, "Barrel");
            Ui.Place(barrel, 0.5f, 0.43f, 1.065f, 0.57f);
            var muzzleRim = Ui.Panel(root, Theme.Outline, 8f * scale, "MuzzleRim", true);
            Ui.Place(muzzleRim, 0.97f, 0.375f, 1.1f, 0.625f);
            var muzzle = Ui.Panel(root, Theme.Hex(0x39425A), 6f * scale, "Muzzle");
            Ui.Place(muzzle, 0.985f, 0.4f, 1.085f, 0.6f);

            var turretRim = Ui.Circle(root, Theme.Outline, "TurretRim");
            Ui.Pin(turretRim, 0.47f, 0.5f, 116 * scale, 116 * scale);
            var turret = Ui.Circle(root, Color.Lerp(body, Theme.Outline, 0.22f), "Turret");
            Ui.Pin(turret, 0.47f, 0.5f, 102 * scale, 102 * scale);
            var turretTop = Ui.Circle(root, Ui.Tint(body, 0.12f), "TurretTop");
            Ui.Pin(turretTop, 0.47f, 0.5f, 84 * scale, 84 * scale, 0, 4 * scale);
            var hatch = Ui.Circle(root, Color.Lerp(body, Theme.Outline, 0.4f), "Hatch");
            Ui.Pin(hatch, 0.47f, 0.5f, 42 * scale, 42 * scale);
            var cap = Ui.Circle(root, Ui.Tint(body, 0.45f), "Cap");
            Ui.Pin(cap, 0.47f, 0.5f, 28 * scale, 28 * scale, 0, 3 * scale);
            return root;
        }

        /// <summary>Big banner title for the top of a page: a ribbon with folded tails and gold letters.</summary>
        public static Text Title(Transform parent, string text)
        {
            var holder = Ui.NewRect(parent, "Title");
            Ui.Pin(holder, 0.5f, 1f, 700, 150, 0, -6);

            for (var side = -1; side <= 1; side += 2)
            {
                var tail = Ui.Panel(holder, Theme.Outline, 20f, "TailRim", true);
                Ui.Pin(tail, 0.5f, 0.5f, 120, 104, side * 300, -22);
                var tailFill = Ui.Panel(tail.transform, Theme.BlueLip, 16f, "Tail");
                Ui.Inset(tailFill, 5, 5, 5, 5);
            }

            Ui.Shadow(holder, 0.4f, 6f, 16f);
            var rim = Ui.Panel(holder, Theme.Outline, 44f, "Rim", true);
            Ui.Pin(rim, 0.5f, 0.5f, 590, 142, 0, -2);
            var lip = Ui.Panel(holder, Theme.BlueLip, 40f, "Lip");
            Ui.Pin(lip, 0.5f, 0.5f, 580, 130, 0, -3);
            var ribbon = Ui.Panel(holder, Theme.Blue, 40f, "Ribbon");
            Ui.Pin(ribbon, 0.5f, 0.5f, 580, 120, 0, 5);
            var gloss = Ui.Panel(ribbon.transform, new Color(1, 1, 1, 0.22f), 28f, "Gloss", true);
            Ui.Place(gloss, 0.03f, 0.52f, 0.97f, 0.92f);

            var label = Ui.Headline(ribbon.transform, text, 74, Color.white, Theme.Hex(0xFFE27A));
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
                    case "group_name_taken": return "That clan name is taken";
                    case "group_limit_reached": return "You are already in a clan";
                    case "group_full": return "The clan is full";
                    case "group_not_found": return "That clan is gone";
                    case "already_member": return "You are already a member";
                    case "already_requested": return "Request already sent";
                    case "banned_from_group": return "You were banned from this clan";
                    case "owner_cannot_leave": return "Disband the clan or hand it over first";
                    case "not_a_member": return "You are not a member";
                    case "blocked": return "You cannot message this player";
                    case "muted": return "You are muted in chat";
                    case "rate_limited": return "Slow down a little";
                    case "invalid_message": return "That message cannot be sent";
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
