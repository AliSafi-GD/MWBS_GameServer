using Game.Meta;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Arena
{
    /// <summary>An on-screen stick for touch (and mouse) play: drag from the center; <see cref="Value"/> is -1..1 on both axes.</summary>
    public sealed class StickPad : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        private RectTransform _knob;
        private float _radius;

        public Vector2 Value { get; private set; }
        public bool Held { get; private set; }

        public static StickPad Create(Transform parent, Color tint, Icon? icon = null)
        {
            var baseImg = Ui.Circle(parent, new Color(tint.r, tint.g, tint.b, 0.2f), "Pad");
            baseImg.raycastTarget = true;
            var pad = baseImg.gameObject.AddComponent<StickPad>();

            var ring = Ui.Pic(baseImg.transform, Art.RingUnit(), new Color(1, 1, 1, 0.45f), "Ring");
            Ui.Stretch(ring.rectTransform);
            var inner = Ui.Circle(baseImg.transform, new Color(0.02f, 0.04f, 0.15f, 0.3f), "Inner");
            Ui.Place(inner, 0.2f, 0.2f, 0.8f, 0.8f);

            var knobRim = Ui.Circle(baseImg.transform, new Color(Theme.Outline.r, Theme.Outline.g, Theme.Outline.b, 0.9f), "Knob");
            pad._knob = knobRim.rectTransform;
            Ui.Pin(knobRim, 0.5f, 0.5f, 136, 136);
            var knob = Ui.Circle(knobRim.transform, new Color(tint.r, tint.g, tint.b, 1f), "Fill");
            Ui.Stretch(knob.rectTransform);
            Ui.Inset(knob, 7, 7, 7, 7);
            var gloss = Ui.Circle(knob.transform, new Color(1, 1, 1, 0.28f), "Gloss");
            gloss.preserveAspect = false;
            Ui.Place(gloss, 0.16f, 0.52f, 0.84f, 0.92f);

            if (icon.HasValue)
            {
                var ic = Ui.IconPic(knob.transform, icon.Value, new Color(1, 1, 1, 0.95f));
                Ui.Place(ic, 0.22f, 0.22f, 0.78f, 0.78f);
            }

            return pad;
        }

        private void Awake() => _radius = 120f;

        private void OnDisable()
        {
            Held = false;
            Value = Vector2.zero;
            if (_knob != null)
                _knob.anchoredPosition = Vector2.zero;
        }

        public void OnPointerDown(PointerEventData e)
        {
            Held = true;
            Move(e);
        }

        public void OnDrag(PointerEventData e) => Move(e);

        public void OnPointerUp(PointerEventData e)
        {
            Held = false;
            Value = Vector2.zero;
            _knob.anchoredPosition = Vector2.zero;
        }

        private void Move(PointerEventData e)
        {
            var rt = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out var local))
                return;

            // The pad is pinned to a screen corner, so its pivot is that corner: measure from the middle of the circle.
            var v = Vector2.ClampMagnitude((local - rt.rect.center) / _radius, 1f);
            Value = v;
            _knob.anchoredPosition = v * _radius;
        }
    }

    /// <summary>
    /// The fire button: a big round button that shoots for as long as a finger is on it. In manual aim mode, dragging the
    /// finger from where it first touched turns the gun that way (see <see cref="Aim"/>); in auto aim mode the drag is ignored.
    /// </summary>
    public sealed class FirePad : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        private const float DeadZone = 26f;     // canvas units
        private const float FullDrag = 140f;
        private const float KnobTravel = 34f;

        private RectTransform _visual;
        private RectTransform _ring;
        private Vector2 _start;

        public bool Held { get; private set; }

        /// <summary>Drag direction, y up, 0..1 long. Zero inside the dead zone.</summary>
        public Vector2 Aim { get; private set; }

        /// <summary>The finger has been dragged far enough to point the gun.</summary>
        public bool Aiming => Aim.sqrMagnitude > 0.0001f;

        /// <summary>Manual aim: the pad invites a drag (a ring around it) and follows the finger a little.</summary>
        public bool Manual
        {
            get => _ring != null && _ring.gameObject.activeSelf;
            set
            {
                if (_ring != null)
                    _ring.gameObject.SetActive(value);
            }
        }

        public static FirePad Create(Transform parent)
        {
            var root = Ui.Circle(parent, new Color(0, 0, 0, 0), "FirePad");
            root.raycastTarget = true;
            var pad = root.gameObject.AddComponent<FirePad>();

            var ring = Ui.Pic(root.transform, Art.RingUnit(), new Color(1f, 1f, 1f, 0.5f), "AimRing");
            Ui.Place(ring, -0.2f, -0.2f, 1.2f, 1.2f);
            pad._ring = ring.rectTransform;
            pad._ring.gameObject.SetActive(false);

            pad._visual = Ui.NewRect(root.transform, "Visual");
            var halo = Ui.Pic(pad._visual, Art.Glow(), new Color(1f, 0.3f, 0.25f, 0.55f), "Halo");
            Ui.Place(halo, -0.15f, -0.15f, 1.15f, 1.15f);
            var rim = Ui.Circle(pad._visual, Theme.Outline, "Rim");
            Ui.Stretch(rim.rectTransform);
            var lip = Ui.Circle(pad._visual, Theme.RedLip, "Lip");
            Ui.Stretch(lip.rectTransform);
            Ui.Inset(lip, 8, 8, 8, 8);
            var face = Ui.Circle(pad._visual, Theme.Red, "Face");
            Ui.Stretch(face.rectTransform);
            Ui.Inset(face, 8, 22, 8, 8);
            var gloss = Ui.Circle(face.transform, new Color(1, 1, 1, 0.26f), "Gloss");
            gloss.preserveAspect = false;
            Ui.Place(gloss, 0.14f, 0.55f, 0.86f, 0.93f);
            var icon = Ui.IconPic(face.transform, Icon.Swords, Color.white);
            Ui.Place(icon, 0.24f, 0.2f, 0.76f, 0.72f);
            return pad;
        }

        public void OnPointerDown(PointerEventData e)
        {
            Held = true;
            Aim = Vector2.zero;
            _start = Local(e);
            _visual.localScale = new Vector3(0.92f, 0.92f, 1f);
        }

        public void OnDrag(PointerEventData e)
        {
            var d = Local(e) - _start;
            if (d.magnitude < DeadZone)
            {
                Aim = Vector2.zero;
                _visual.anchoredPosition = Vector2.zero;
                return;
            }

            Aim = Vector2.ClampMagnitude(d / FullDrag, 1f).normalized * Mathf.Min(1f, d.magnitude / FullDrag);
            _visual.anchoredPosition = Manual ? Aim * KnobTravel : Vector2.zero;
        }

        public void OnPointerUp(PointerEventData e) => Release();

        private void OnDisable() => Release();

        private void Release()
        {
            Held = false;
            Aim = Vector2.zero;
            if (_visual != null)
            {
                _visual.localScale = Vector3.one;
                _visual.anchoredPosition = Vector2.zero;
            }
        }

        private Vector2 Local(PointerEventData e)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)transform, e.position, e.pressEventCamera, out var local);
            return local;
        }
    }
}
