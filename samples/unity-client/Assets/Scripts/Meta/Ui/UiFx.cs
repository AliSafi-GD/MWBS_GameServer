using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Meta
{
    /// <summary>
    /// The look of game text: a thick dark stroke all around, a drop shadow under it and, for titles, a vertical
    /// gradient over the letters. One mesh effect instead of stacking Outline + Shadow, so the stroke is even.
    /// </summary>
    [RequireComponent(typeof(Text))]
    public sealed class TextFx : BaseMeshEffect
    {
        private static readonly List<UIVertex> Source = new List<UIVertex>();
        private static readonly List<UIVertex> Result = new List<UIVertex>();

        private static readonly Vector2[] Ring8 =
        {
            new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1),
            new Vector2(0.7071f, 0.7071f), new Vector2(-0.7071f, 0.7071f), new Vector2(0.7071f, -0.7071f), new Vector2(-0.7071f, -0.7071f)
        };

        [SerializeField] private Color _stroke = new Color(0.04f, 0.07f, 0.21f, 0.95f);
        [SerializeField] private float _width = 3f;
        [SerializeField] private float _drop = 3f;
        [SerializeField] private bool _eight = true;
        [SerializeField] private bool _gradient;
        [SerializeField] private Color _top = Color.white;
        [SerializeField] private Color _bottom = Color.white;

        public void SetStroke(Color color, float width, float drop, bool eightWay)
        {
            _stroke = color;
            _width = width;
            _drop = drop;
            _eight = eightWay;
            Dirty();
        }

        /// <summary>Multiplies the letters by <paramref name="top"/> at their top edge and <paramref name="bottom"/> at their bottom edge.</summary>
        public void SetGradient(Color top, Color bottom)
        {
            _gradient = true;
            _top = top;
            _bottom = bottom;
            Dirty();
        }

        private void Dirty()
        {
            if (graphic != null)
                graphic.SetVerticesDirty();
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive())
                return;

            Source.Clear();
            Result.Clear();
            vh.GetUIVertexStream(Source);
            var count = Source.Count;
            if (count == 0)
                return;

            if (_gradient)
                ApplyGradient(count);

            if (_width > 0.01f)
            {
                if (_drop > 0.01f)
                    AddCopy(count, new Vector2(0f, -(_width + _drop)));

                var steps = _eight ? 8 : 4;
                for (var i = 0; i < steps; i++)
                    AddCopy(count, (_eight ? Ring8[i] : Ring8[i + 4] * 1.4142f) * _width);
            }

            for (var i = 0; i < count; i++)
                Result.Add(Source[i]);

            vh.Clear();
            vh.AddUIVertexTriangleStream(Result);
        }

        /// <summary>Each letter is one quad (six vertices); shade it by its own height so every line gets the full gradient.</summary>
        private void ApplyGradient(int count)
        {
            for (var q = 0; q + 5 < count; q += 6)
            {
                var min = float.MaxValue;
                var max = float.MinValue;
                for (var i = q; i < q + 6; i++)
                {
                    var y = Source[i].position.y;
                    if (y < min) min = y;
                    if (y > max) max = y;
                }

                var span = Mathf.Max(max - min, 0.001f);
                for (var i = q; i < q + 6; i++)
                {
                    var v = Source[i];
                    var tint = Color.Lerp(_bottom, _top, (v.position.y - min) / span);
                    v.color = (Color)v.color * tint;
                    Source[i] = v;
                }
            }
        }

        private void AddCopy(int count, Vector2 offset)
        {
            for (var i = 0; i < count; i++)
            {
                var v = Source[i];
                var c = _stroke;
                c.a *= v.color.a / 255f;
                v.color = c;
                v.position += new Vector3(offset.x, offset.y, 0f);
                Result.Add(v);
            }
        }
    }

    /// <summary>
    /// The living background behind every menu screen: a deep gradient, two soft lights, drifting specks of light and
    /// dark corners. Fills the whole canvas (also in a wide window) and never catches a tap.
    /// </summary>
    public static class Backdrop
    {
        public static RectTransform Create(Transform canvas, Color? top = null, Color? bottom = null, int specks = 16)
        {
            var root = Ui.NewRect(canvas, "Backdrop");
            Ui.Gradient(root, top ?? Theme.BgTop, bottom ?? Theme.BgBottom, "Sky");

            var high = Ui.Pic(root, Art.Glow(), new Color(0.45f, 0.75f, 1f, 0.42f), "LightTop");
            high.preserveAspect = false;
            Ui.Place(high, -0.35f, 0.42f, 1.35f, 1.25f);

            var low = Ui.Pic(root, Art.Glow(), new Color(0.62f, 0.3f, 1f, 0.26f), "LightBottom");
            low.preserveAspect = false;
            Ui.Place(low, -0.5f, -0.35f, 0.9f, 0.4f);

            // Fixed seed: the same sky every run (and in every screenshot).
            var random = new System.Random(7);
            for (var i = 0; i < specks; i++)
            {
                var twinkle = i % 4 == 0;
                var speck = Ui.Pic(root, twinkle ? Art.Sparkle() : Art.Glow(), Color.white, "Speck");
                var size = twinkle ? 26f + (float)random.NextDouble() * 30f : 40f + (float)random.NextDouble() * 130f;
                Ui.Pin(speck, 0.5f, 0.5f, size, size);
                var drift = speck.gameObject.AddComponent<Drift>();
                drift.Setup((float)random.NextDouble(), (float)random.NextDouble(), 0.012f + (float)random.NextDouble() * 0.03f,
                    twinkle ? 0.75f : 0.1f + (float)random.NextDouble() * 0.1f, (float)random.NextDouble() * 6.28f);
            }

            var vignette = Ui.Pic(root, Art.Vignette(), new Color(0.01f, 0.02f, 0.1f, 0.75f), "Vignette");
            vignette.preserveAspect = false;
            Ui.Stretch(vignette.rectTransform);
            return root;
        }
    }

    /// <summary>Floats a speck slowly up its parent, swaying and fading in and out, and wraps it back to the bottom.</summary>
    public sealed class Drift : MonoBehaviour
    {
        private float _x, _y, _speed, _alpha, _phase;
        private Image _image;
        private RectTransform _rect;

        public void Setup(float x, float y, float speed, float alpha, float phase)
        {
            _x = x;
            _y = y;
            _speed = speed;
            _alpha = alpha;
            _phase = phase;
            _image = GetComponent<Image>();
            _rect = (RectTransform)transform;
            Apply();
        }

        private void Update()
        {
            _y += _speed * Time.unscaledDeltaTime;
            if (_y > 1.08f)
                _y = -0.08f;

            Apply();
        }

        private void Apply()
        {
            var t = Time.unscaledTime;
            var x = _x + Mathf.Sin(t * 0.35f + _phase) * 0.02f;
            _rect.anchorMin = _rect.anchorMax = new Vector2(x, _y);
            var blink = 0.55f + 0.45f * Mathf.Sin(t * (0.6f + _speed * 20f) + _phase);
            var edge = Mathf.Clamp01(Mathf.Min(_y + 0.08f, 1.08f - _y) * 8f);
            _image.color = new Color(0.85f, 0.93f, 1f, _alpha * blink * edge);
        }
    }

    /// <summary>A band of light that crosses its parent every few seconds. The parent must clip it (Mask or RectMask2D).</summary>
    public sealed class ShineSweep : MonoBehaviour
    {
        public float Every = 3.2f;
        public float Duration = 0.75f;
        public float Delay;

        private RectTransform _rect;

        /// <summary>Adds a clipped shine to <paramref name="face"/>, a rounded panel.</summary>
        public static ShineSweep Create(Image face, float radius, float alpha = 0.45f)
        {
            var clip = Ui.Panel(face.transform, Color.white, radius, "ShineClip");
            Ui.Stretch(clip.rectTransform);
            clip.gameObject.AddComponent<Mask>().showMaskGraphic = false;

            var band = Ui.Pic(clip.transform, Art.ShineBand(), new Color(1, 1, 1, alpha), "Shine");
            band.preserveAspect = false;
            var rt = band.rectTransform;
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.localRotation = Quaternion.Euler(0, 0, -22f);
            var sweep = band.gameObject.AddComponent<ShineSweep>();
            sweep._rect = rt;
            return sweep;
        }

        private void Update()
        {
            var parent = (RectTransform)_rect.parent;
            var size = parent.rect.size;
            _rect.sizeDelta = new Vector2(Mathf.Max(90f, size.x * 0.22f), size.y * 2.6f);

            var t = Mathf.Repeat(Time.unscaledTime + Delay, Every) / Duration;
            var x = Mathf.Lerp(-0.25f, 1.25f, Mathf.Clamp01(t));
            _rect.anchoredPosition = new Vector2(x * size.x, 0f);
        }
    }

    /// <summary>Grows, turns and fades a sparkle in a loop.</summary>
    public sealed class Twinkle : MonoBehaviour
    {
        public float Speed = 1.6f;
        public float Phase;

        private Image _image;
        private Color _color;

        private void Awake()
        {
            _image = GetComponent<Image>();
            _color = _image.color;
        }

        private void Update()
        {
            var s = Mathf.Max(0f, Mathf.Sin(Time.unscaledTime * Speed + Phase));
            transform.localScale = Vector3.one * (0.35f + 0.65f * s);
            transform.localRotation = Quaternion.Euler(0, 0, Time.unscaledTime * 25f + Phase * 57f);
            _image.color = new Color(_color.r, _color.g, _color.b, _color.a * s);
        }
    }
}
