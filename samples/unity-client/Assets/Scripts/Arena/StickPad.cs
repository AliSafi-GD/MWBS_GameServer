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
            var baseImg = Ui.Circle(parent, new Color(tint.r, tint.g, tint.b, 0.28f), "Pad");
            baseImg.raycastTarget = true;
            var pad = baseImg.gameObject.AddComponent<StickPad>();

            var ring = Ui.Circle(baseImg.transform, new Color(1, 1, 1, 0.18f), "Ring");
            Ui.Place(ring, 0.06f, 0.06f, 0.94f, 0.94f);

            var knob = Ui.Circle(baseImg.transform, new Color(tint.r, tint.g, tint.b, 0.8f), "Knob");
            pad._knob = knob.rectTransform;
            Ui.Pin(knob, 0.5f, 0.5f, 120, 120);

            if (icon.HasValue)
            {
                var ic = Ui.IconPic(knob.transform, icon.Value, new Color(1, 1, 1, 0.9f));
                Ui.Place(ic, 0.2f, 0.2f, 0.8f, 0.8f);
            }

            return pad;
        }

        private void Awake() => _radius = 120f;

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

            var v = Vector2.ClampMagnitude(local / _radius, 1f);
            Value = v;
            _knob.anchoredPosition = v * _radius;
        }
    }
}
