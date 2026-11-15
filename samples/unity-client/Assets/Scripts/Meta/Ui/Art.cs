using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Meta
{
    public enum Icon { Coin, Gem, Swords, Bag, Scroll, People, Trophy, Chest, Gear, Check, Crown, Bolt, Plus, Star, Heart }

    /// <summary>Sprites drawn in code (rounded panels, gradients, icons), so the game needs no imported art to look like a game.</summary>
    public static class Art
    {
        private const int RoundSize = 64;
        private const int RoundRadius = 24;
        private const int IconSize = 128;

        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        // ---- basic shapes ------------------------------------------------------------------------------------

        /// <summary>White rounded rectangle for 9-slice use. Corner size on screen = <see cref="RoundRadius"/> / Image.pixelsPerUnitMultiplier.</summary>
        public static Sprite Rounded()
        {
            return Get("rounded", () => Build(RoundSize, RoundSize, (x, y) =>
            {
                var px = x + 0.5f - RoundSize / 2f;
                var py = y + 0.5f - RoundSize / 2f;
                var q = new Vector2(Mathf.Abs(px) - (RoundSize / 2f - RoundRadius), Mathf.Abs(py) - (RoundSize / 2f - RoundRadius));
                var d = new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - RoundRadius;
                return new Color(1, 1, 1, Mathf.Clamp01(0.5f - d));
            }, new Vector4(RoundRadius, RoundRadius, RoundRadius, RoundRadius)));
        }

        /// <summary>
        /// The same rounded rectangle, lit from above: full brightness at the top edge, darker toward the bottom.
        /// Tinted by Image.color it gives every panel and button a soft gradient for free.
        /// </summary>
        public static Sprite RoundedShaded()
        {
            return Get("rounded_shaded", () => Build(RoundSize, RoundSize, (x, y) =>
            {
                var b = Mathf.Lerp(0.74f, 1f, Mathf.SmoothStep(0f, 1f, y / (RoundSize - 1f)));
                return new Color(b, b, b, RoundedAlpha(x, y));
            }, new Vector4(RoundRadius, RoundRadius, RoundRadius, RoundRadius)));
        }

        private static float RoundedAlpha(int x, int y)
        {
            var px = x + 0.5f - RoundSize / 2f;
            var py = y + 0.5f - RoundSize / 2f;
            var q = new Vector2(Mathf.Abs(px) - (RoundSize / 2f - RoundRadius), Mathf.Abs(py) - (RoundSize / 2f - RoundRadius));
            var d = new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - RoundRadius;
            return Mathf.Clamp01(0.5f - d);
        }

        /// <summary>A blurred rounded rectangle for drop shadows (9-slice, border 28 of 64).</summary>
        public static Sprite Shadow()
        {
            return Get("shadow", () => Build(64, 64, (x, y) =>
            {
                var px = Mathf.Abs(x + 0.5f - 32f) - 14f;
                var py = Mathf.Abs(y + 0.5f - 32f) - 14f;
                var d = new Vector2(Mathf.Max(px, 0), Mathf.Max(py, 0)).magnitude - 8f;
                var a = Mathf.Clamp01(1f - d / 10f);
                return new Color(1, 1, 1, a * a);
            }, new Vector4(28, 28, 28, 28)));
        }

        /// <summary>A sunburst: soft wedges of light that fade toward the rim. Spin it slowly behind a hero picture.</summary>
        public static Sprite Rays(int wedges = 14)
        {
            return Get("rays" + wedges, () => Build(384, 384, (x, y) =>
            {
                var u = (x + 0.5f) / 192f - 1f;
                var v = (y + 0.5f) / 192f - 1f;
                var r = Mathf.Sqrt(u * u + v * v);
                if (r >= 1f)
                    return Color.clear;

                var wave = Mathf.Cos(Mathf.Atan2(v, u) * wedges);
                var wedge = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.08f, 0.22f, wave));
                var fade = Mathf.Pow(1f - r, 1.4f) * Mathf.SmoothStep(0f, 1f, r * 5f);
                return new Color(1, 1, 1, wedge * fade);
            }, Vector4.zero));
        }

        /// <summary>Dark corners for a full-screen backdrop (stretch it; draw it black).</summary>
        public static Sprite Vignette()
        {
            return Get("vignette", () => Build(64, 64, (x, y) =>
            {
                var u = (x + 0.5f) / 32f - 1f;
                var v = (y + 0.5f) / 32f - 1f;
                var r = Mathf.Sqrt(u * u * 0.8f + v * v * 0.9f);
                return new Color(1, 1, 1, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1.3f, r)));
            }, Vector4.zero));
        }

        /// <summary>A four-point twinkle.</summary>
        public static Sprite Sparkle()
        {
            return Get("sparkle", () => Build(64, 64, (x, y) =>
            {
                var u = Mathf.Abs((x + 0.5f) / 32f - 1f);
                var v = Mathf.Abs((y + 0.5f) / 32f - 1f);
                var star = 1f - (Mathf.Sqrt(u) + Mathf.Sqrt(v));
                var core = 1f - Mathf.Sqrt(u * u + v * v) * 3.2f;
                return new Color(1, 1, 1, Mathf.Clamp01(Mathf.Max(star * 2.4f, core)));
            }, Vector4.zero));
        }

        /// <summary>A soft vertical band of light, for the shine that sweeps over a big button.</summary>
        public static Sprite ShineBand()
        {
            return Get("shine_band", () => Build(64, 4, (x, y) =>
            {
                var u = Mathf.Abs((x + 0.5f) / 32f - 1f);
                return new Color(1, 1, 1, Mathf.Pow(1f - u, 2.2f));
            }, Vector4.zero));
        }

        /// <summary>White at the top fading to nothing at the bottom (or the reverse): a cheap light or shade overlay.</summary>
        public static Sprite Fade(bool fromTop = true)
        {
            return Get("fade" + fromTop, () => Build(4, 64, (x, y) =>
            {
                var t = y / 63f;
                return new Color(1, 1, 1, Mathf.SmoothStep(0f, 1f, fromTop ? t : 1f - t));
            }, Vector4.zero));
        }

        public static Sprite Circle()
        {
            return Get("circle", () => Build(256, 256, (x, y) =>
            {
                var d = new Vector2(x + 0.5f - 128, y + 0.5f - 128).magnitude - 127f;
                return new Color(1, 1, 1, Mathf.Clamp01(0.5f - d));
            }, Vector4.zero));
        }

        /// <summary>A white 1 x 1 unit square, for world-space blocks.</summary>
        public static Sprite SquareUnit() =>
            Get("square_unit", () => Build(4, 4, (x, y) => Color.white, Vector4.zero, 4f));

        /// <summary>A white circle exactly 1 unit across, for world-space shapes.</summary>
        public static Sprite CircleUnit() =>
            Get("circle_unit", () => Build(64, 64, (x, y) =>
            {
                var d = new Vector2(x + 0.5f - 32, y + 0.5f - 32).magnitude - 31f;
                return new Color(1, 1, 1, Mathf.Clamp01(0.5f - d));
            }, Vector4.zero, 64f));

        /// <summary>A thin ring exactly 1 unit across (shock waves, the marker under your own tank, arena markings).</summary>
        public static Sprite RingUnit() =>
            Get("ring_unit", () => Build(128, 128, (x, y) =>
            {
                var d = Mathf.Abs(new Vector2(x + 0.5f - 64, y + 0.5f - 64).magnitude - 57f) - 5f;
                return new Color(1, 1, 1, Mathf.Clamp01(0.5f - d));
            }, Vector4.zero, 128f));

        /// <summary>
        /// A rounded rectangle for world-space SpriteRenderers in Sliced draw mode: set SpriteRenderer.size to the size in
        /// units and the corners keep <paramref name="radius"/> (in units) whatever the shape.
        /// </summary>
        public static Sprite RoundedWorld(float radius)
        {
            var r = Mathf.Clamp(Mathf.Round(radius * 100f), 1f, 50f);
            return Get("rounded_world" + r, () => Build(RoundSize, RoundSize, (x, y) => new Color(1, 1, 1, RoundedAlpha(x, y)),
                new Vector4(RoundRadius, RoundRadius, RoundRadius, RoundRadius), RoundRadius * 100f / r));
        }

        /// <summary>A soft round glow exactly 1 unit across.</summary>
        public static Sprite GlowUnit() =>
            Get("glow_unit", () => Build(128, 128, (x, y) =>
            {
                var d = new Vector2(x + 0.5f - 64, y + 0.5f - 64).magnitude / 64f;
                return new Color(1, 1, 1, Mathf.Pow(Mathf.Clamp01(1 - d), 2f));
            }, Vector4.zero, 128f));

        public static Sprite Glow()
        {
            return Get("glow", () => Build(128, 128, (x, y) =>
            {
                var d = new Vector2(x + 0.5f - 64, y + 0.5f - 64).magnitude / 64f;
                var a = Mathf.Pow(Mathf.Clamp01(1 - d), 2f);
                return new Color(1, 1, 1, a);
            }, Vector4.zero));
        }

        public static Sprite Gradient(Color top, Color bottom)
        {
            var key = "grad" + ColorUtility.ToHtmlStringRGBA(top) + ColorUtility.ToHtmlStringRGBA(bottom);
            return Get(key, () => Build(4, 128, (x, y) => Color.Lerp(bottom, top, y / 127f), Vector4.zero));
        }

        // ---- icons -------------------------------------------------------------------------------------------

        public static Sprite IconSprite(Icon icon)
        {
            return Get("icon" + icon, () => Build(IconSize, IconSize, (x, y) =>
            {
                var u = (x + 0.5f) / IconSize * 2f - 1f;
                var v = (y + 0.5f) / IconSize * 2f - 1f;
                return Shade(icon, u, v);
            }, Vector4.zero));
        }

        private static Color Shade(Icon icon, float x, float y)
        {
            switch (icon)
            {
                case Icon.Coin: return Coin(x, y);
                case Icon.Gem: return Gem(x, y);
                case Icon.Chest: return Chest(x, y);
                default: return Silhouette(icon, x, y);
            }
        }

        private static Color Silhouette(Icon icon, float x, float y)
        {
            float d;
            switch (icon)
            {
                case Icon.Swords:
                {
                    var blades = Mathf.Min(Seg(x, y, -0.7f, -0.7f, 0.62f, 0.62f) - 0.1f, Seg(x, y, 0.7f, -0.7f, -0.62f, 0.62f) - 0.1f);
                    var guards = Mathf.Min(Seg(x, y, -0.62f, -0.2f, -0.2f, -0.62f) - 0.09f, Seg(x, y, 0.62f, -0.2f, 0.2f, -0.62f) - 0.09f);
                    d = Mathf.Min(blades, guards);
                    break;
                }
                case Icon.Bag:
                {
                    var body = Box(x, y, 0f, -0.2f, 0.62f, 0.52f, 0.18f);
                    var handle = Mathf.Max(Mathf.Abs(Cir(x, y, 0f, 0.34f, 0.32f)) - 0.07f, -(y - 0.3f));
                    d = Mathf.Min(body, handle);
                    break;
                }
                case Icon.Scroll:
                {
                    var page = Box(x, y, 0f, 0f, 0.58f, 0.78f, 0.16f);
                    var tick = Mathf.Min(Seg(x, y, -0.32f, 0.0f, -0.08f, -0.26f), Seg(x, y, -0.08f, -0.26f, 0.36f, 0.3f)) - 0.1f;
                    d = Mathf.Max(page, -tick);
                    break;
                }
                case Icon.People:
                {
                    var head = Cir(x, y, 0f, 0.36f, 0.3f);
                    var body = Mathf.Max(Cir(x, y, 0f, -0.55f, 0.72f), y - 0f);
                    d = Mathf.Min(head, body);
                    break;
                }
                case Icon.Trophy:
                {
                    var bowl = Mathf.Max(Cir(x, y, 0f, 0.2f, 0.52f), y - 0.2f);
                    var rim = Box(x, y, 0f, 0.2f, 0.56f, 0.07f, 0.04f);
                    var handleL = Mathf.Max(Mathf.Abs(Cir(x, y, -0.58f, 0.3f, 0.22f)) - 0.06f, x + 0.5f);
                    var handleR = Mathf.Max(Mathf.Abs(Cir(x, y, 0.58f, 0.3f, 0.22f)) - 0.06f, 0.5f - x);
                    var stem = Box(x, y, 0f, -0.42f, 0.09f, 0.2f, 0.02f);
                    var foot = Box(x, y, 0f, -0.7f, 0.36f, 0.1f, 0.05f);
                    d = Mathf.Min(Mathf.Min(bowl, rim), Mathf.Min(Mathf.Min(handleL, handleR), Mathf.Min(stem, foot)));
                    break;
                }
                case Icon.Gear:
                {
                    var ring = Cir(x, y, 0f, 0f, 0.62f);
                    for (var i = 0; i < 8; i++)
                    {
                        var a = i * Mathf.PI / 4f;
                        var c = Mathf.Cos(a);
                        var s = Mathf.Sin(a);
                        var rx = x * c + y * s;
                        var ry = -x * s + y * c;
                        ring = Mathf.Min(ring, Box(rx, ry, 0.7f, 0f, 0.2f, 0.16f, 0.04f));
                    }
                    d = Mathf.Max(ring, -Cir(x, y, 0f, 0f, 0.3f));
                    break;
                }
                case Icon.Check:
                    d = Mathf.Min(Seg(x, y, -0.6f, 0.0f, -0.2f, -0.45f), Seg(x, y, -0.2f, -0.45f, 0.65f, 0.5f)) - 0.17f;
                    break;
                case Icon.Crown:
                {
                    var baseBox = Box(x, y, 0f, -0.4f, 0.72f, 0.2f, 0.06f);
                    var peaks = Mathf.Min(Seg(x, y, -0.7f, -0.3f, -0.6f, 0.5f), Mathf.Min(Seg(x, y, -0.6f, 0.5f, -0.2f, 0.0f), Mathf.Min(Seg(x, y, -0.2f, 0.0f, 0f, 0.62f),
                        Mathf.Min(Seg(x, y, 0f, 0.62f, 0.2f, 0.0f), Mathf.Min(Seg(x, y, 0.2f, 0.0f, 0.6f, 0.5f), Seg(x, y, 0.6f, 0.5f, 0.7f, -0.3f)))))) - 0.1f;
                    d = Mathf.Min(baseBox, peaks);
                    break;
                }
                case Icon.Plus:
                    d = Mathf.Min(Box(x, y, 0f, 0f, 0.66f, 0.2f, 0.1f), Box(x, y, 0f, 0f, 0.2f, 0.66f, 0.1f));
                    break;
                case Icon.Star:
                    d = Star(x, y, 0.92f, 0.46f) - 0.04f;
                    break;
                case Icon.Heart:
                    d = Mathf.Min(Mathf.Min(Cir(x, y, -0.4f, 0.3f, 0.44f), Cir(x, y, 0.4f, 0.3f, 0.44f)), Rhomb(x, y - 0.08f, 0.84f, 0.84f));
                    break;
                case Icon.Bolt:
                    d = Mathf.Min(Seg(x, y, 0.25f, 0.8f, -0.35f, -0.05f), Mathf.Min(Seg(x, y, -0.35f, -0.05f, 0.3f, -0.05f), Seg(x, y, 0.3f, -0.05f, -0.25f, -0.85f))) - 0.14f;
                    break;
                default:
                    d = Cir(x, y, 0, 0, 0.8f);
                    break;
            }

            return new Color(1, 1, 1, Aa(d));
        }

        private static Color Coin(float x, float y)
        {
            Color c = Color.clear;
            c = Over(c, new Color(0.45f, 0.27f, 0.02f), Aa(Cir(x, y, 0, 0, 0.97f)));
            c = Over(c, Color.Lerp(new Color(1f, 0.93f, 0.4f), new Color(0.95f, 0.62f, 0.05f), (1f - y) / 2f), Aa(Cir(x, y, 0, 0, 0.84f)));
            c = Over(c, new Color(0.78f, 0.5f, 0.03f), Aa(Mathf.Abs(Cir(x, y, 0, 0, 0.6f)) - 0.05f));
            c = Over(c, Color.Lerp(new Color(1f, 0.82f, 0.25f), new Color(1f, 0.7f, 0.1f), (1f - y) / 2f), Aa(Cir(x, y, 0, 0, 0.5f)));
            c = Over(c, new Color(1, 1, 1, 0.55f), Aa(Cir(x, y, -0.32f, 0.34f, 0.14f)) * 0.55f);
            return c;
        }

        private static Color Gem(float x, float y)
        {
            Color c = Color.clear;
            c = Over(c, new Color(0.22f, 0.07f, 0.45f), Aa(Rhomb(x, y, 0.92f, 0.98f)));
            var fill = y > 0.22f ? new Color(0.78f, 0.58f, 1f) : (x < 0f ? new Color(0.56f, 0.3f, 1f) : new Color(0.42f, 0.18f, 0.88f));
            if (Mathf.Abs(x) < 0.04f && y < 0.22f)
                fill = new Color(0.66f, 0.45f, 1f);
            c = Over(c, fill, Aa(Rhomb(x, y, 0.8f, 0.86f)));
            c = Over(c, new Color(1, 1, 1, 0.6f), Aa(Cir(x, y, -0.3f, 0.45f, 0.1f)) * 0.7f);
            return c;
        }

        private static Color Chest(float x, float y)
        {
            Color c = Color.clear;
            var bodyD = Box(x, y, 0f, -0.25f, 0.8f, 0.45f, 0.1f);
            var lidD = Mathf.Max(Cir(x, y, 0f, 0.12f, 0.8f), y - 0.12f);
            c = Over(c, new Color(0.25f, 0.1f, 0.02f), Aa(Mathf.Min(bodyD, lidD) - 0.05f));
            c = Over(c, new Color(0.7f, 0.4f, 0.14f), Aa(bodyD));
            c = Over(c, new Color(0.85f, 0.52f, 0.2f), Aa(lidD));
            c = Over(c, new Color(1f, 0.8f, 0.2f), Aa(Box(x, y, 0f, -0.05f, 0.8f, 0.07f, 0.02f)));
            c = Over(c, new Color(1f, 0.8f, 0.2f), Aa(Box(x, y, -0.5f, -0.25f, 0.07f, 0.45f, 0.02f)));
            c = Over(c, new Color(1f, 0.8f, 0.2f), Aa(Box(x, y, 0.5f, -0.25f, 0.07f, 0.45f, 0.02f)));
            c = Over(c, new Color(1f, 0.9f, 0.4f), Aa(Box(x, y, 0f, -0.1f, 0.16f, 0.2f, 0.05f)));
            c = Over(c, new Color(0.4f, 0.2f, 0.02f), Aa(Cir(x, y, 0f, -0.1f, 0.05f)));
            return c;
        }

        // ---- shape math (all in the icon's -1..1 space, y up) ------------------------------------------------

        private static float Aa(float d) => Mathf.Clamp01(0.5f - d * IconSize * 0.5f);

        private static float Cir(float x, float y, float cx, float cy, float r) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;

        private static float Box(float x, float y, float cx, float cy, float hx, float hy, float round)
        {
            var qx = Mathf.Abs(x - cx) - (hx - round);
            var qy = Mathf.Abs(y - cy) - (hy - round);
            return Mathf.Sqrt(Mathf.Max(qx, 0) * Mathf.Max(qx, 0) + Mathf.Max(qy, 0) * Mathf.Max(qy, 0)) + Mathf.Min(Mathf.Max(qx, qy), 0) - round;
        }

        private static float Seg(float x, float y, float ax, float ay, float bx, float by)
        {
            var pax = x - ax;
            var pay = y - ay;
            var bax = bx - ax;
            var bay = by - ay;
            var h = Mathf.Clamp01((pax * bax + pay * bay) / (bax * bax + bay * bay));
            var dx = pax - bax * h;
            var dy = pay - bay * h;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Five-point star: outer radius <paramref name="r"/>, inner radius as a fraction <paramref name="rf"/>.</summary>
        private static float Star(float x, float y, float r, float rf)
        {
            const float k1x = 0.809016994f, k1y = -0.587785252f;
            const float k2x = -k1x, k2y = k1y;
            x = Mathf.Abs(x);
            var m = 2f * Mathf.Max(k1x * x + k1y * y, 0f);
            x -= m * k1x;
            y -= m * k1y;
            m = 2f * Mathf.Max(k2x * x + k2y * y, 0f);
            x -= m * k2x;
            y -= m * k2y;
            x = Mathf.Abs(x);
            y -= r;
            var bax = rf * -k1y;
            var bay = rf * k1x - 1f;
            var h = Mathf.Clamp((x * bax + y * bay) / (bax * bax + bay * bay), 0f, r);
            var dx = x - bax * h;
            var dy = y - bay * h;
            return Mathf.Sqrt(dx * dx + dy * dy) * Mathf.Sign(y * bax - x * bay);
        }

        private static float Rhomb(float x, float y, float hx, float hy) => (Mathf.Abs(x) / hx + Mathf.Abs(y) / hy - 1f) * Mathf.Min(hx, hy) * 0.7f;

        private static Color Over(Color under, Color over, float coverage)
        {
            var a = over.a * coverage;
            var outA = a + under.a * (1 - a);
            if (outA <= 0.0001f)
                return Color.clear;

            var rgb = (new Vector3(over.r, over.g, over.b) * a + new Vector3(under.r, under.g, under.b) * under.a * (1 - a)) / outA;
            return new Color(rgb.x, rgb.y, rgb.z, outA);
        }

        // ---- sprite plumbing ---------------------------------------------------------------------------------

        private static Sprite Get(string key, Func<Sprite> make)
        {
            if (Cache.TryGetValue(key, out var sprite) && sprite != null)
                return sprite;

            return Cache[key] = make();
        }

        private static Sprite Build(int w, int h, Func<int, int, Color> pixel, Vector4 border, float pixelsPerUnit = 100f)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    pixels[y * w + x] = pixel(x, y);
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), pixelsPerUnit, 0, SpriteMeshType.FullRect, border);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}
