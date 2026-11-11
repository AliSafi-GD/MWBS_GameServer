using System.Linq;
using System.Threading.Tasks;
using Game.Core;
using GameServer.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>
    /// Two tabs: the shop (products from the server's shop config, in a two-column grid) and the inventory
    /// (items the player owns, with Equip for tank skins).
    /// </summary>
    public sealed class ShopPage : Page
    {
        private const int ShopTab = 0, InventoryTab = 1;

        private Segmented _tabs;
        private ScrollList _list;

        protected override void Build()
        {
            Parts.Title(Root, "SHOP");

            _tabs = Segmented.Create(Root, new[] { "SHOP", "INVENTORY" }, i => Rebuild());
            Ui.Place(_tabs, 0.05f, 0.80f, 0.95f, 0.875f);
            _tabs.Select(ShopTab, false);

            _list = ScrollList.Create(Root, 24, 24, 2, new Vector2(486, 600));
            Ui.Place(_list, 0f, 0f, 1f, 0.79f);

            S.Shop.Changed += Rebuild;
            S.Wallet.Changed += Rebuild;
            S.Loadout.Changed += Rebuild;
        }

        /// <summary>Shows the inventory tab the next time the page opens (or right away if it is open).</summary>
        public void OpenInventory() => _tabs.Select(InventoryTab, false);

        public override void Dispose()
        {
            S.Shop.Changed -= Rebuild;
            S.Wallet.Changed -= Rebuild;
            S.Loadout.Changed -= Rebuild;
        }

        public override void OnShow()
        {
            Rebuild();
            C.Run(async () => await Task.WhenAll(S.Shop.RefreshAsync(), S.Wallet.RefreshAsync()));
        }

        private void Rebuild()
        {
            _list.Clear();
            if (_tabs.Selected == InventoryTab)
                BuildInventory();
            else
                BuildShop();
        }

        // ---- shop --------------------------------------------------------------------------------------------

        private void BuildShop()
        {
            var products = S.Shop.Products;
            if (products.Count == 0)
            {
                var empty = Ui.Label(_list.Content, "The shop is empty.", 44, Theme.Muted);
                Parts.Size(empty, 900, 200);
                return;
            }

            foreach (var p in products)
                AddProductCard(p);
        }

        private void AddProductCard(ShopProduct p)
        {
            var available = S.Shop.IsAvailable(p);
            var affordable = S.Wallet.CanAfford(p.Cost);

            var card = Parts.Card(_list.Content, Theme.PanelLight);

            var name = Ui.Label(card, p.Name, 44);
            Ui.Place(name, 0.05f, 0.85f, 0.95f, 0.98f);

            var glow = Ui.Pic(card, Art.Glow(), new Color(1f, 0.85f, 0.4f, 0.35f), "Glow");
            Ui.Pin(glow, 0.5f, 0.6f, 380, 380);

            var head = Parts.Headline(p.Gives);
            var skin = p.Gives?.Items != null ? p.Gives.Items.Keys.FirstOrDefault(Skins.IsSkin) : null;
            if (skin != null)
                Parts.TankPreview(card, Skins.ColorOf(skin).Value, 1.2f, new Vector2(0.5f, 0.62f));
            else
            {
                var icon = Ui.IconPic(card, head.Icon);
                Ui.Pin(icon, 0.5f, 0.64f, 190, 190);
            }

            var amount = Ui.Label(card, head.Text, 62, Theme.Gold);
            Ui.Pin(amount, 0.5f, 0.4f, 380, 80);

            if (!string.IsNullOrEmpty(p.Description))
            {
                var desc = Ui.Label(card, p.Description, 26, Theme.Muted, TextAnchor.MiddleCenter, false);
                Ui.Pin(desc, 0.5f, 0.3f, 400, 54);
            }

            if (p.Remaining.HasValue)
            {
                var tag = Ui.Panel(card, Theme.Red, 20f, "Left");
                Ui.Pin(tag, 0f, 0.77f, 130, 48, 18, 0);
                var tagText = Ui.Label(tag.transform, p.Remaining.Value + " left", 28);
                Ui.Stretch(tagText.rectTransform);
            }

            GButton buy;
            if (!available)
            {
                buy = GButton.Create(card, "SOLD OUT", Theme.Grey, Theme.GreyLip, 40, 12);
                buy.Interactable = false;
            }
            else
            {
                buy = PriceButton.Create(card, p.Cost, affordable);
                var id = p.Id;
                var title = p.Name;
                buy.Button.onClick.AddListener(() => C.Run(async () =>
                {
                    await S.Shop.BuyAsync(id);
                    C.Toasts.Show("Bought " + title, Theme.Green, head.Icon);
                }, buy));
            }

            Ui.Place(buy, 0.08f, 0.04f, 0.92f, 0.22f);
        }

        // ---- inventory ---------------------------------------------------------------------------------------

        private void BuildInventory()
        {
            var equipped = S.Loadout.EquippedSkin;

            // The plain tank is always available, so a skin can be taken off again.
            AddItemCard("Standard tank", Theme.Hex(0x4AA8FF), null, "", equipped == null, () => S.Loadout.Equip(null));

            var items = S.Loadout.OwnedItems();
            foreach (var item in items)
            {
                var id = item.Key;
                var name = ItemName(id);
                if (Skins.IsSkin(id))
                    AddItemCard(name, Skins.ColorOf(id).Value, id, "", equipped == id, () => S.Loadout.Equip(id));
                else
                    AddOtherItemCard(name, item.Value);
            }

            if (items.Count == 0)
            {
                var hint = Ui.Label(_list.Content, "No items yet.\nBuy skins in the shop!", 40, Theme.Muted);
                Parts.Size(hint, 900, 160);
            }
        }

        private void AddItemCard(string name, Color color, string skinId, string note, bool isEquipped, System.Action equip)
        {
            var card = Parts.Card(_list.Content, isEquipped ? Theme.PanelLight : Theme.Panel);

            var title = Ui.Label(card, name, 44);
            Ui.Place(title, 0.05f, 0.85f, 0.95f, 0.98f);

            var glow = Ui.Pic(card, Art.Glow(), new Color(color.r, color.g, color.b, 0.4f), "Glow");
            Ui.Pin(glow, 0.5f, 0.58f, 380, 380);
            Parts.TankPreview(card, color, 1.2f, new Vector2(0.5f, 0.58f));

            if (!string.IsNullOrEmpty(note))
            {
                var label = Ui.Label(card, note, 30, Theme.Muted);
                Ui.Pin(label, 0.5f, 0.34f, 380, 50);
            }

            var button = GButton.Create(card, isEquipped ? "EQUIPPED" : "EQUIP", isEquipped ? Theme.Grey : Theme.Green, isEquipped ? Theme.GreyLip : Theme.GreenLip, 42, 12);
            button.Interactable = !isEquipped;
            Ui.Place(button, 0.08f, 0.04f, 0.92f, 0.22f);
            button.Button.onClick.AddListener(() => equip());

            if (isEquipped)
            {
                var check = Ui.IconPic(card, Icon.Check, Theme.Green);
                Ui.Pin(check, 1f, 1f, 64, 64, -16, -16);
            }
        }

        private void AddOtherItemCard(string name, long count)
        {
            var card = Parts.Card(_list.Content, Theme.Panel);
            var title = Ui.Label(card, name, 44);
            Ui.Place(title, 0.05f, 0.85f, 0.95f, 0.98f);
            var icon = Ui.IconPic(card, Icon.Chest);
            Ui.Pin(icon, 0.5f, 0.58f, 190, 190);
            var amount = Ui.Label(card, "x" + Ui.Num(count), 62, Theme.Gold);
            Ui.Pin(amount, 0.5f, 0.22f, 380, 80);
        }

        /// <summary>The shop's name for an item (the product that gives it), else a readable version of its id.</summary>
        private string ItemName(string itemId)
        {
            var product = S.Shop.Products.FirstOrDefault(p => p.Gives?.Items != null && p.Gives.Items.ContainsKey(itemId));
            return product != null ? product.Name : Parts.Pretty(itemId);
        }
    }
}
