using System;
using System.Linq;
using UnityEngine;

namespace Game.Core
{
    /// <summary>How a skin item looks on the tank. Skins are plain server items named "skin_*"; the look is the game's own.</summary>
    public static class Skins
    {
        public const string Prefix = "skin_";

        public static bool IsSkin(string itemId) => !string.IsNullOrEmpty(itemId) && itemId.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>The body color of a skin, or null when the id is empty (use the team color).</summary>
        public static Color? ColorOf(string skinId)
        {
            if (!IsSkin(skinId))
                return null;

            switch (skinId.Substring(Prefix.Length))
            {
                case "red": return new Color(0.93f, 0.25f, 0.25f);
                case "camo": return new Color(0.36f, 0.5f, 0.24f);
                case "gold": return new Color(1f, 0.78f, 0.16f);
                case "neon": return new Color(0.15f, 0.95f, 0.9f);
                default:
                    // An unknown skin still gets its own stable color.
                    var hash = 17;
                    foreach (var ch in skinId)
                        hash = hash * 31 + ch;
                    return Color.HSVToRGB(Mathf.Abs(hash % 360) / 360f, 0.65f, 0.95f);
            }
        }
    }

    /// <summary>What the player has chosen to use: the tank skin. Kept on the device, per account, and only if still owned.</summary>
    public sealed class LoadoutService
    {
        private readonly WalletService _wallet;
        private readonly string _key;

        public event Action Changed;

        public LoadoutService(WalletService wallet, string accountId)
        {
            _wallet = wallet;
            _key = "game.skin." + accountId;
        }

        /// <summary>The equipped skin id, or null when none is chosen or it is no longer in the inventory.</summary>
        public string EquippedSkin
        {
            get
            {
                var id = PlayerPrefs.GetString(_key, "");
                return Skins.IsSkin(id) && _wallet.Item(id) > 0 ? id : null;
            }
        }

        /// <summary>Equips an owned skin, or passes null to go back to the plain tank.</summary>
        public void Equip(string skinId)
        {
            if (skinId != null && (!Skins.IsSkin(skinId) || _wallet.Item(skinId) <= 0))
                return;

            PlayerPrefs.SetString(_key, skinId ?? "");
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        /// <summary>The skin to send to a match: the equipped one, else none (the match shows the plain tank).</summary>
        public string SkinForMatch => EquippedSkin;

        /// <summary>Items the player owns, skins first.</summary>
        public System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>> OwnedItems() =>
            _wallet.Items.Where(kv => kv.Value > 0).OrderByDescending(kv => Skins.IsSkin(kv.Key)).ThenBy(kv => kv.Key).ToList();
    }
}
