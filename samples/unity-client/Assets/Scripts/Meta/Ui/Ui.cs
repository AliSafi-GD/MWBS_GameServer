using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>The look of the game: one place for every color.</summary>
    public static class Theme
    {
        public static readonly Color BgTop = Hex(0x2F6FE4);
        public static readonly Color BgBottom = Hex(0x0C1340);
        public static readonly Color Panel = Hex(0x243F8C);
        public static readonly Color PanelLight = Hex(0x3559B8);
        public static readonly Color PanelDark = Hex(0x0F1B4D);
        public static readonly Color Outline = Hex(0x0A1136);
        public static readonly Color Edge = Hex(0x5C8BF2);

        public static readonly Color Gold = Hex(0xFFC926);
        public static readonly Color GoldLip = Hex(0xC27A00);
        public static readonly Color GoldDeep = Hex(0xFF9A1F);
        public static readonly Color Green = Hex(0x62E04E);
        public static readonly Color GreenLip = Hex(0x279021);
        public static readonly Color Blue = Hex(0x3EA2FF);
        public static readonly Color BlueLip = Hex(0x1A55B4);
        public static readonly Color Red = Hex(0xF4504F);
        public static readonly Color RedLip = Hex(0xA32427);
        public static readonly Color Purple = Hex(0xA862FF);
        public static readonly Color PurpleLip = Hex(0x6A2FC0);
        public static readonly Color Grey = Hex(0x8C93AD);
        public static readonly Color GreyLip = Hex(0x575D75);
        public static readonly Color Muted = Hex(0xAEBBE6);

        public static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
    }

    /// <summary>Code-built uGUI helpers. Layout uses normalized anchors inside a 1080 x 1920 design column.</summary>
    public static class Ui
    {
        public const float DesignWidth = 1080f;
        public const float DesignHeight = 1920f;

        private static Font _font;

        private static bool _ownFont;

        /// <summary>
        /// The game's display font (Assets/Fonts/Resources/GameFont.ttf: Lilita One, SIL Open Font License), or Unity's
        /// built-in font when that file is missing. Letters the font lacks fall back to the device's own fonts.
        /// </summary>
        public static Font Font
        {
            get
            {
                if (_font != null)
                    return _font;

                _font = Resources.Load<Font>("GameFont");
                _ownFont = _font != null;
                return _font != null ? _font : _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
        }

        /// <summary>The display font is heavy already; only the built-in fallback needs faked bold.</summary>
        public static FontStyle Weight => Font != null && _ownFont ? FontStyle.Normal : FontStyle.Bold;

        public static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null)
                return;

            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        // ---- canvas and layout -------------------------------------------------------------------------------

        public static Canvas CreateCanvas(string name, int sortingOrder = 0)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            var cam = Camera.main;
            if (cam != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 5f;
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            }

            canvas.sortingOrder = sortingOrder;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(DesignWidth, DesignHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            return canvas;
        }

        /// <summary>A 1080 x 1920 area centered in the canvas, scaled to fit it: portrait phones fill it, wide windows get a phone-shaped column.</summary>
        public static RectTransform Column(Transform canvas)
        {
            var rt = NewRect(canvas, "Column");
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(DesignWidth, DesignHeight);
            rt.anchoredPosition = Vector2.zero;
            rt.gameObject.AddComponent<ColumnFit>();
            return rt;
        }

        public static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            Stretch(rt);
            return rt;
        }

        public static RectTransform Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        /// <summary>Anchors a rect to a fraction of its parent.</summary>
        public static T Place<T>(T component, float x0, float y0, float x1, float y1) where T : Component
        {
            var rt = (RectTransform)component.transform;
            rt.anchorMin = new Vector2(x0, y0);
            rt.anchorMax = new Vector2(x1, y1);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return component;
        }

        /// <summary>
        /// Fixed-size rect at an anchor point, offset in design units. An anchor at an edge (0 or 1) makes the rect hug that
        /// edge; anywhere in between the rect is centered on the anchor.
        /// </summary>
        public static T Pin<T>(T component, float anchorX, float anchorY, float width, float height, float x = 0, float y = 0) where T : Component
        {
            var rt = (RectTransform)component.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(anchorX, anchorY);
            rt.pivot = new Vector2(Edge(anchorX), Edge(anchorY));
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(x, y);
            return component;
        }

        private static float Edge(float anchor) => anchor <= 0f || anchor >= 1f ? Mathf.Clamp01(anchor) : 0.5f;

        public static void Inset(Component component, float left, float bottom, float right, float top)
        {
            var rt = (RectTransform)component.transform;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        // ---- pictures ----------------------------------------------------------------------------------------

        /// <summary>A rounded panel, lit from above (a soft top-to-bottom gradient of <paramref name="color"/>) unless <paramref name="flat"/>.</summary>
        public static Image Panel(Transform parent, Color color, float radius = 28f, string name = "Panel", bool flat = false)
        {
            var rt = NewRect(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = flat ? Art.Rounded() : Art.RoundedShaded();
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 24f / Mathf.Max(radius, 4f);
            img.color = color;
            return img;
        }

        public static Image Pic(Transform parent, Sprite sprite, Color? color = null, string name = "Pic")
        {
            var rt = NewRect(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color ?? Color.white;
            img.preserveAspect = true;
            img.raycastTarget = false;
            return img;
        }

        public static Image IconPic(Transform parent, Icon icon, Color? tint = null) => Pic(parent, Art.IconSprite(icon), tint, "Icon_" + icon);

        public static Image Gradient(Transform parent, Color top, Color bottom, string name = "Gradient")
        {
            var img = Pic(parent, Art.Gradient(top, bottom), Color.white, name);
            img.preserveAspect = false;
            Stretch(img.rectTransform);
            return img;
        }

        /// <summary>A soft dark blur behind a panel. Add it before the panel so it draws underneath.</summary>
        public static Image Shadow(Transform parent, float alpha = 0.4f, float spread = 18f, float drop = 12f)
        {
            var img = Pic(parent, Art.Shadow(), new Color(0f, 0.01f, 0.08f, alpha), "Shadow");
            img.preserveAspect = false;
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 0.55f;
            Stretch(img.rectTransform);
            Inset(img, -spread, -spread - drop, -spread, -spread + drop);
            return img;
        }

        public static Image Circle(Transform parent, Color color, string name = "Circle")
        {
            var img = Pic(parent, Art.Circle(), color, name);
            return img;
        }

        // ---- text --------------------------------------------------------------------------------------------

        public static Text Label(Transform parent, string text, int size, Color? color = null, TextAnchor anchor = TextAnchor.MiddleCenter, bool outline = true, bool bestFit = true)
        {
            var rt = NewRect(parent, "Label");
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.fontStyle = Weight;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = color ?? Color.white;
            t.text = text;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.resizeTextForBestFit = bestFit;
            t.resizeTextMinSize = Mathf.Max(10, size / 2);
            t.resizeTextMaxSize = size;

            if (outline)
            {
                // Big text gets an even eight-way stroke; small text (lists, chat) the cheaper four-way one.
                var fx = rt.gameObject.AddComponent<TextFx>();
                var width = Mathf.Clamp(size * 0.055f, 1.5f, 6f);
                fx.SetStroke(new Color(Theme.Outline.r, Theme.Outline.g, Theme.Outline.b, 0.96f), width, width * 0.9f, size >= 40);
            }

            return t;
        }

        /// <summary>A headline: stroked text with a vertical gradient over the letters (gold by default).</summary>
        public static Text Headline(Transform parent, string text, int size, Color? top = null, Color? bottom = null)
        {
            var t = Label(parent, text, size);
            t.GetComponent<TextFx>().SetGradient(top ?? Theme.Hex(0xFFF3A6), bottom ?? Theme.GoldDeep);
            return t;
        }

        public static string Num(long value) => value.ToString("N0");

        public static Color Tint(Color c, float amount) => Color.Lerp(c, amount > 0 ? Color.white : Color.black, Mathf.Abs(amount));

        /// <summary>The avatars a player can pick. The server stores the id ("av_3") in the profile.</summary>
        public static readonly (Color Color, Icon Icon)[] AvatarPresets =
        {
            (Theme.Hex(0xFF6B5A), Icon.Swords),
            (Theme.Hex(0x4AA8FF), Icon.Crown),
            (Theme.Hex(0x5CD94B), Icon.Bolt),
            (Theme.Hex(0xFFC21A), Icon.Trophy),
            (Theme.Hex(0xA45BFF), Icon.Gem),
            (Theme.Hex(0xFF8A3D), Icon.Chest),
            (Theme.Hex(0x2EC4B6), Icon.People),
            (Theme.Hex(0xE84393), Icon.Coin),
        };

        public static string AvatarId(int index) => "av_" + index;

        public static int AvatarIndex(string avatarId)
        {
            if (!string.IsNullOrEmpty(avatarId) && avatarId.StartsWith("av_") && int.TryParse(avatarId.Substring(3), out var i) && i >= 0 && i < AvatarPresets.Length)
                return i;

            return -1;
        }

        /// <summary>A round avatar: the chosen preset, or a colored circle with the first letter of the name.</summary>
        public static RectTransform Avatar(Transform parent, string name, float size, string avatarId = null)
        {
            var preset = AvatarIndex(avatarId);
            var rim = Circle(parent, Theme.Outline, "Avatar");
            var ring = Circle(rim.transform, Theme.Gold, "Ring");
            Stretch(ring.rectTransform);
            Inset(ring, 4, 4, 4, 4);

            if (preset >= 0)
            {
                var p = AvatarPresets[preset];
                var back = Circle(rim.transform, p.Color, "Fill");
                Stretch(back.rectTransform);
                Inset(back, 10, 10, 10, 10);
                var icon = IconPic(back.transform, p.Icon, Color.white);
                Place(icon, 0.2f, 0.2f, 0.8f, 0.8f);
                return rim.rectTransform;
            }

            var hash = 17;
            foreach (var ch in name ?? "?")
                hash = hash * 31 + ch;
            var hue = Mathf.Abs(hash % 360) / 360f;

            var fill = Circle(rim.transform, Color.HSVToRGB(hue, 0.55f, 0.95f), "Fill");
            Stretch(fill.rectTransform);
            Inset(fill, 10, 10, 10, 10);
            var letter = Label(fill.transform, string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(), (int)(size * 0.55f));
            Stretch(letter.rectTransform);
            return rim.rectTransform;
        }
    }

    // ---- components -----------------------------------------------------------------------------------------

    /// <summary>Scales the 1080 x 1920 column to fit its parent canvas.</summary>
    public sealed class ColumnFit : MonoBehaviour
    {
        private void LateUpdate()
        {
            var parent = (RectTransform)transform.parent;
            var r = parent.rect;
            if (r.width <= 1 || r.height <= 1)
                return;

            var s = Mathf.Min(r.width / Ui.DesignWidth, r.height / Ui.DesignHeight);
            transform.localScale = new Vector3(s, s, 1f);
        }
    }

    /// <summary>
    /// The chunky game button: a soft shadow, a dark lip and a bright glossy face that sinks when pressed and pops
    /// back when released.
    /// </summary>
    public sealed class GButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private float _lip;
        private bool _pressed;
        private bool _interactable = true;
        private Color _face, _lipColor;
        private RectTransform _visual;
        private Coroutine _pop;

        public Button Button { get; private set; }
        public Image Face { get; private set; }
        public Image LipImage { get; private set; }
        public Text Label { get; private set; }

        public RectTransform Rect => (RectTransform)transform;

        public bool Interactable
        {
            get => _interactable;
            set
            {
                _interactable = value;
                Button.interactable = value;
                Paint();
            }
        }

        public static GButton Create(Transform parent, string text, Color face, Color lip, int fontSize = 40, float lipHeight = 12f)
        {
            var root = Ui.Panel(parent, Color.clear, 8f, "Button_" + text);
            root.sprite = null;
            root.color = new Color(0, 0, 0, 0);
            root.raycastTarget = true;
            var b = root.gameObject.AddComponent<GButton>();
            b._lip = lipHeight;
            b._face = face;
            b._lipColor = lip;

            // Everything that moves on a press lives under Visual, so the tap area itself never changes size.
            b._visual = Ui.NewRect(root.transform, "Visual");
            Ui.Shadow(b._visual, 0.42f, 16f, 4f + lipHeight * 0.5f);

            var rim = Ui.Panel(b._visual, Theme.Outline, 30f, "Rim", true);
            Ui.Inset(rim, -4, -4, -4, -4);
            b.LipImage = Ui.Panel(b._visual, lip, 26f, "Lip");
            b.Face = Ui.Panel(b._visual, face, 26f, "Face");
            b.Face.raycastTarget = true;

            var gloss = Ui.Panel(b.Face.transform, new Color(1, 1, 1, 0.24f), 20f, "Gloss", true);
            Ui.Place(gloss, 0.03f, 0.52f, 0.97f, 0.93f);
            gloss.raycastTarget = false;

            b.Label = Ui.Label(b.Face.transform, text, fontSize);
            Ui.Stretch(b.Label.rectTransform);

            b.Button = root.gameObject.AddComponent<Button>();
            b.Button.transition = Selectable.Transition.None;
            b.Button.targetGraphic = b.Face;
            b.Layout();
            return b;
        }

        public void SetText(string text) => Label.text = text;

        public void SetColors(Color face, Color lip)
        {
            _face = face;
            _lipColor = lip;
            Paint();
        }

        private void Paint()
        {
            Face.color = _interactable ? _face : Theme.Grey;
            LipImage.color = _interactable ? _lipColor : Theme.GreyLip;
            Label.color = _interactable ? Color.white : new Color(1, 1, 1, 0.7f);
        }

        private void Layout()
        {
            Paint();
            var lip = LipImage.rectTransform;
            Ui.Stretch(lip);
            var face = Face.rectTransform;
            Ui.Stretch(face);
            face.offsetMin = new Vector2(0, _pressed ? _lip * 0.3f : _lip);
            face.offsetMax = new Vector2(0, _pressed ? -_lip * 0.7f : 0);
        }

        private void Press(bool down)
        {
            if (_pressed == down)
                return;

            _pressed = down;
            Layout();

            if (_pop != null)
                StopCoroutine(_pop);
            _pop = null;

            if (down)
                _visual.localScale = new Vector3(0.96f, 0.96f, 1f);
            else if (isActiveAndEnabled)
                _pop = StartCoroutine(Pop());
            else
                _visual.localScale = Vector3.one;
        }

        private System.Collections.IEnumerator Pop()
        {
            const float seconds = 0.22f;
            var t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                var s = Mathf.LerpUnclamped(0.96f, 1f, Anim.EaseOutBack(Mathf.Clamp01(t / seconds)));
                _visual.localScale = new Vector3(s, s, 1f);
                yield return null;
            }

            _visual.localScale = Vector3.one;
            _pop = null;
        }

        private void OnDisable()
        {
            _pressed = false;
            _pop = null;
            if (_visual != null)
                _visual.localScale = Vector3.one;
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (_interactable)
                Press(true);
        }

        public void OnPointerUp(PointerEventData e) => Press(false);

        public void OnPointerExit(PointerEventData e) => Press(false);
    }

    /// <summary>A dark pill with an icon and a number that counts up and down, and an optional "+" that opens the shop.</summary>
    public sealed class Chip : MonoBehaviour
    {
        private Text _value;
        private RectTransform _icon;
        private float _shown;
        private long _target;
        private float _bump;

        public static Chip Create(Transform parent, Icon icon, Color? accent = null, Action onPlus = null)
        {
            var rim = Ui.Panel(parent, new Color(Theme.Edge.r, Theme.Edge.g, Theme.Edge.b, 0.55f), 40f, "Chip_" + icon, true);
            var chip = rim.gameObject.AddComponent<Chip>();
            var pill = Ui.Panel(rim.transform, Theme.Hex(0x091238), 38f, "Pill", true);
            Ui.Inset(pill, 3, 3, 3, 3);

            var icn = Ui.IconPic(rim.transform, icon);
            var irt = icn.rectTransform;
            irt.anchorMin = new Vector2(0, 0.5f);
            irt.anchorMax = new Vector2(0, 0.5f);
            irt.pivot = new Vector2(0.5f, 0.5f);
            irt.sizeDelta = new Vector2(88, 88);
            irt.anchoredPosition = new Vector2(14, 0);
            chip._icon = irt;

            chip._value = Ui.Label(rim.transform, "0", 40, accent ?? Color.white, TextAnchor.MiddleCenter);
            Ui.Stretch(chip._value.rectTransform);
            chip._value.rectTransform.offsetMin = new Vector2(56, 0);
            chip._value.rectTransform.offsetMax = new Vector2(onPlus != null ? -62 : -16, 0);

            if (onPlus != null)
            {
                var plus = GButton.Create(rim.transform, "", Theme.Green, Theme.GreenLip, 30, 6);
                Ui.Pin(plus, 1f, 0.5f, 58, 58, -8, 0);
                var cross = Ui.IconPic(plus.Face.transform, Icon.Plus);
                Ui.Place(cross, 0.2f, 0.2f, 0.8f, 0.8f);
                plus.Button.onClick.AddListener(() => onPlus());
            }

            return chip;
        }

        public void Set(long value, bool animate = true)
        {
            if (animate && value != _target)
                _bump = 1f;

            _target = value;
            if (!animate)
            {
                _shown = value;
                _value.text = Ui.Num(value);
            }
        }

        private void Update()
        {
            if (_bump > 0f)
            {
                _bump = Mathf.Max(0f, _bump - Time.unscaledDeltaTime * 3f);
                var s = 1f + Mathf.Sin(_bump * Mathf.PI) * 0.3f;
                _icon.localScale = new Vector3(s, s, 1f);
            }

            if (Mathf.Approximately(_shown, _target))
                return;

            _shown = Mathf.MoveTowards(_shown, _target, Mathf.Max(1f, Mathf.Abs(_target - _shown) * 6f * Time.unscaledDeltaTime + 1f));
            _value.text = Ui.Num((long)Mathf.Round(_shown));
        }
    }

    /// <summary>A framed bar whose glossy fill glides to its new value.</summary>
    public sealed class ProgressBar : MonoBehaviour
    {
        private Image _fill;
        private Text _label;
        private float _shown = -1f;
        private float _target;

        public static ProgressBar Create(Transform parent, Color fill)
        {
            var rim = Ui.Panel(parent, Theme.Outline, 22f, "Bar", true);
            var bar = rim.gameObject.AddComponent<ProgressBar>();
            var back = Ui.Panel(rim.transform, Theme.Hex(0x0A1440), 20f, "Back", true);
            Ui.Inset(back, 3, 3, 3, 3);

            bar._fill = Ui.Panel(back.transform, fill, 20f, "Fill");
            bar._fill.rectTransform.anchorMin = Vector2.zero;
            bar._fill.rectTransform.anchorMax = new Vector2(0.02f, 1f);
            Ui.Inset(bar._fill, 3, 3, 0, 3);
            var gloss = Ui.Panel(bar._fill.transform, new Color(1, 1, 1, 0.3f), 14f, "Gloss", true);
            Ui.Place(gloss, 0f, 0.55f, 1f, 0.9f);
            Ui.Inset(gloss, 6, 0, 6, 0);
            gloss.raycastTarget = false;

            bar._label = Ui.Label(rim.transform, "", 28);
            Ui.Stretch(bar._label.rectTransform);
            return bar;
        }

        public void Set(float t, string text)
        {
            _target = Mathf.Clamp01(t);
            _label.text = text;

            // The first value (and any value set while hidden) is shown at once; later ones glide.
            if (_shown < 0f || !isActiveAndEnabled)
                Show(_target);
        }

        private void Update()
        {
            if (_shown >= 0f && !Mathf.Approximately(_shown, _target))
                Show(Mathf.MoveTowards(_shown, _target, Mathf.Max(0.02f, Mathf.Abs(_target - _shown) * 6f) * Time.unscaledDeltaTime));
        }

        private void Show(float t)
        {
            _shown = t;
            _fill.gameObject.SetActive(t > 0.001f);
            _fill.rectTransform.anchorMax = new Vector2(Mathf.Max(t, 0.1f), 1f);
        }
    }

    public sealed class Badge : MonoBehaviour
    {
        private Text _text;

        public static Badge Create(Transform parent)
        {
            var rim = Ui.Circle(parent, Color.white, "Badge");
            var badge = rim.gameObject.AddComponent<Badge>();
            var dot = Ui.Circle(rim.transform, Theme.Red, "Dot");
            Ui.Stretch(dot.rectTransform);
            Ui.Inset(dot, 4, 4, 4, 4);
            badge._text = Ui.Label(dot.transform, "1", 30);
            Ui.Stretch(badge._text.rectTransform);
            badge.gameObject.SetActive(false);
            return badge;
        }

        public void Set(int count)
        {
            gameObject.SetActive(count > 0);
            _text.text = count > 9 ? "9+" : count.ToString();
        }
    }

    /// <summary>A row of exclusive tabs (Friends | Party | Chat).</summary>
    public sealed class Segmented : MonoBehaviour
    {
        private GButton[] _buttons;
        private Action<int> _onSelect;

        public int Selected { get; private set; } = -1;

        public static Segmented Create(Transform parent, string[] names, Action<int> onSelect)
        {
            var rt = Ui.NewRect(parent, "Segmented");
            var seg = rt.gameObject.AddComponent<Segmented>();
            seg._onSelect = onSelect;
            seg._buttons = new GButton[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                var index = i;
                var b = GButton.Create(rt, names[i], Theme.Panel, Theme.PanelDark, 34, 10);
                Ui.Place(b, i / (float)names.Length + 0.006f, 0f, (i + 1) / (float)names.Length - 0.006f, 1f);
                b.Button.onClick.AddListener(() => seg.Select(index, true));
                seg._buttons[i] = b;
            }

            return seg;
        }

        public void Select(int index, bool notify)
        {
            Selected = index;
            for (var i = 0; i < _buttons.Length; i++)
            {
                if (i == index)
                    _buttons[i].SetColors(Theme.Blue, Theme.BlueLip);
                else
                    _buttons[i].SetColors(Theme.Panel, Theme.PanelDark);
            }

            if (notify)
                _onSelect?.Invoke(index);
        }
    }

    /// <summary>Vertical (or grid) scroll area; add children to <see cref="Content"/>.</summary>
    public sealed class ScrollList : MonoBehaviour
    {
        public RectTransform Content { get; private set; }
        public ScrollRect Scroll { get; private set; }

        public static ScrollList Create(Transform parent, float spacing = 14f, int padding = 12, int gridColumns = 0, Vector2? cell = null)
        {
            var root = Ui.NewRect(parent, "ScrollList");
            var list = root.gameObject.AddComponent<ScrollList>();
            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.scrollSensitivity = 40f;
            scroll.inertia = true;

            var viewport = Ui.NewRect(root, "Viewport");
            viewport.gameObject.AddComponent<RectMask2D>();
            var vimg = viewport.gameObject.AddComponent<Image>();
            vimg.color = new Color(0, 0, 0, 0.001f);

            var content = Ui.NewRect(viewport, "Content");
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.offsetMin = content.offsetMax = Vector2.zero;

            if (gridColumns > 0)
            {
                var grid = content.gameObject.AddComponent<GridLayoutGroup>();
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = gridColumns;
                grid.cellSize = cell ?? new Vector2(480, 520);
                grid.spacing = new Vector2(spacing, spacing);
                grid.padding = new RectOffset(padding, padding, padding, padding);
                grid.childAlignment = TextAnchor.UpperCenter;
            }
            else
            {
                var v = content.gameObject.AddComponent<VerticalLayoutGroup>();
                v.spacing = spacing;
                v.padding = new RectOffset(padding, padding, padding, padding);
                v.childControlWidth = true;
                v.childControlHeight = true;
                v.childForceExpandWidth = true;
                v.childForceExpandHeight = false;
            }

            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;
            list.Content = content;
            list.Scroll = scroll;
            return list;
        }

        public void Clear()
        {
            for (var i = Content.childCount - 1; i >= 0; i--)
                Destroy(Content.GetChild(i).gameObject);
        }

        public void ScrollToBottom()
        {
            Canvas.ForceUpdateCanvases();
            Scroll.verticalNormalizedPosition = 0f;
        }
    }

    // ---- motion ---------------------------------------------------------------------------------------------

    public sealed class Pulse : MonoBehaviour
    {
        public float Amount = 0.06f;
        public float Speed = 3f;
        private Vector3 _base;

        private void OnEnable() => _base = transform.localScale;

        private void Update() => transform.localScale = _base * (1f + Mathf.Sin(Time.unscaledTime * Speed) * Amount);
    }

    public sealed class Bob : MonoBehaviour
    {
        public float Height = 14f;
        public float Speed = 2f;
        private Vector2 _base;

        private void OnEnable() => _base = ((RectTransform)transform).anchoredPosition;

        private void Update() => ((RectTransform)transform).anchoredPosition = _base + new Vector2(0, Mathf.Sin(Time.unscaledTime * Speed) * Height);
    }

    public sealed class Spin : MonoBehaviour
    {
        public float DegreesPerSecond = -220f;

        private void Update() => transform.Rotate(0, 0, DegreesPerSecond * Time.unscaledDeltaTime);
    }

    public static class Anim
    {
        public static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
        }

        public static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

        public static void Run(MonoBehaviour host, float seconds, Action<float> step, Action done = null)
        {
            if (host == null || !host.isActiveAndEnabled)
            {
                step?.Invoke(1f);
                done?.Invoke();
                return;
            }

            host.StartCoroutine(Routine(seconds, step, done));
        }

        private static System.Collections.IEnumerator Routine(float seconds, Action<float> step, Action done)
        {
            var t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                step?.Invoke(Mathf.Clamp01(t / seconds));
                yield return null;
            }

            step?.Invoke(1f);
            done?.Invoke();
        }
    }

    /// <summary>Short messages that slide in at the top (rewards, errors) and fade away.</summary>
    public sealed class ToastLayer : MonoBehaviour
    {
        private const int MaxToasts = 3;
        private int _active;

        public static ToastLayer Create(Transform parent)
        {
            var rt = Ui.NewRect(parent, "Toasts");
            return rt.gameObject.AddComponent<ToastLayer>();
        }

        public void Show(string text, Color accent, Icon? icon = null)
        {
            if (_active >= MaxToasts)
                return;

            _active++;
            var slot = _active - 1;
            var pill = Ui.Panel(transform, new Color(Theme.PanelDark.r, Theme.PanelDark.g, Theme.PanelDark.b, 0.96f), 44f, "Toast");
            var rim = Ui.Panel(pill.transform, accent, 44f, "Rim");
            Ui.Stretch(rim.rectTransform);
            rim.rectTransform.SetAsFirstSibling();
            var body = Ui.Panel(pill.transform, Theme.PanelDark, 40f, "Body");
            Ui.Inset(body, 5, 5, 5, 5);

            var group = pill.gameObject.AddComponent<CanvasGroup>();
            var rt = pill.rectTransform;
            Ui.Pin(pill, 0.5f, 1f, 900, 110, 0, 40);

            var textLeft = 36f;
            if (icon.HasValue)
            {
                var ic = Ui.IconPic(body.transform, icon.Value);
                Ui.Pin(ic, 0f, 0.5f, 80, 80, 54, 0);
                textLeft = 100f;
            }

            var label = Ui.Label(body.transform, text, 38);
            Ui.Stretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(textLeft, 0);
            label.rectTransform.offsetMax = new Vector2(-24, 0);

            Anim.Run(this, 2.6f, t =>
            {
                var slide = t < 0.1f ? Anim.EaseOutBack(t / 0.1f) : 1f;
                rt.anchoredPosition = new Vector2(0, Mathf.Lerp(40, -170, slide) - slot * 126f);
                group.alpha = t > 0.85f ? 1f - (t - 0.85f) / 0.15f : 1f;
            }, () =>
            {
                _active--;
                if (pill != null)
                    Destroy(pill.gameObject);
            });
        }
    }

    /// <summary>A centered popup over a dimmed screen.</summary>
    public sealed class Modal : MonoBehaviour
    {
        public RectTransform Body { get; private set; }

        public static Modal Create(Transform parent, string title, float width = 920f, float height = 1000f, bool closable = true)
        {
            var root = Ui.NewRect(parent, "Modal");
            var modal = root.gameObject.AddComponent<Modal>();

            var dim = Ui.Panel(root, new Color(0.01f, 0.02f, 0.1f, 0.78f), 4f, "Dim", true);
            Ui.Stretch(dim.rectTransform);
            Ui.Inset(dim, -2000, -2000, -2000, -2000);
            dim.raycastTarget = true;

            var shadow = Ui.Shadow(root, 0.6f);
            Ui.Pin(shadow, 0.5f, 0.5f, width + 110, height + 110, 0, -30);
            var rim = Ui.Panel(root, Theme.Outline, 54f, "Rim", true);
            Ui.Pin(rim, 0.5f, 0.5f, width + 14, height + 14);
            var edge = Ui.Panel(rim.transform, Theme.Edge, 50f, "Edge");
            Ui.Inset(edge, 5, 5, 5, 5);
            var card = Ui.Panel(rim.transform, Theme.Panel, 46f, "Card");
            Ui.Inset(card, 10, 10, 10, 10);
            var head = Ui.Pic(card.transform, Art.Fade(), new Color(1, 1, 1, 0.1f), "Light");
            head.preserveAspect = false;
            Ui.Place(head, 0.03f, 0.72f, 0.97f, 0.99f);

            var ribbonRim = Ui.Panel(rim.transform, Theme.Outline, 40f, "RibbonRim", true);
            Ui.Pin(ribbonRim, 0.5f, 1f, width * 0.7f + 10, 142, 0, 45);
            var ribbonLip = Ui.Panel(rim.transform, Theme.BlueLip, 36f, "RibbonLip");
            Ui.Pin(ribbonLip, 0.5f, 1f, width * 0.7f, 120, 0, 28);
            var ribbon = Ui.Panel(rim.transform, Theme.Blue, 36f, "Ribbon");
            Ui.Pin(ribbon, 0.5f, 1f, width * 0.7f, 120, 0, 40);
            var ribbonGloss = Ui.Panel(ribbon.transform, new Color(1, 1, 1, 0.2f), 26f, "Gloss", true);
            Ui.Place(ribbonGloss, 0.03f, 0.52f, 0.97f, 0.92f);
            var titleLabel = Ui.Label(ribbon.transform, title, 54);
            Ui.Stretch(titleLabel.rectTransform);

            var close = GButton.Create(rim.transform, "X", Theme.Red, Theme.RedLip, 44, 10);
            close.SetText("");
            Ui.Pin(close, 1f, 1f, 100, 100, -10, 10);
            var cross = Ui.IconPic(close.Face.transform, Icon.Plus);
            Ui.Place(cross, 0.2f, 0.2f, 0.8f, 0.8f);
            cross.rectTransform.localRotation = Quaternion.Euler(0, 0, 45f);
            close.Button.onClick.AddListener(modal.Close);
            close.gameObject.SetActive(closable);

            modal.Body = Ui.NewRect(card.transform, "Body");
            Ui.Inset(modal.Body, 30, 30, 30, 100);

            var dimColor = dim.color;
            Anim.Run(modal, 0.22f, t =>
            {
                rim.rectTransform.localScale = Vector3.one * Mathf.LerpUnclamped(0.7f, 1f, Anim.EaseOutBack(t));
                dim.color = new Color(dimColor.r, dimColor.g, dimColor.b, dimColor.a * Mathf.Clamp01(t * 2f));
            });
            return modal;
        }

        public void Close() => Destroy(gameObject);
    }
}
