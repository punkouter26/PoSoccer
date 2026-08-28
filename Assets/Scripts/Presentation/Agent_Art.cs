using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Procedural sprite factory for runtime-built presentation (replay ghosts,
    /// crowd stands, advertising boards).
    ///
    /// Deliberately generates textures in code rather than shipping PNGs. Two
    /// reasons, both project-specific: overwriting a PNG in place is a known way
    /// to corrupt its cached sprite geometry in this project (see CLAUDE.md), and
    /// a serialized Sprite reference would mean a scene edit on every scene that
    /// wants the feature. A handful of tiny textures cost nothing and cannot rot.
    ///
    /// Sprites are cached by shape+size so repeated calls share one texture, which
    /// keeps the crowd tilemap and every ghost inside a single draw call each.
    /// </summary>
    public static class Agent_Art
    {
        static readonly Dictionary<string, Sprite> Cache = new();

        /// <summary>White square exactly <paramref name="worldSize"/> units across.</summary>
        public static Sprite Square(float worldSize)
        {
            string key = $"sq:{worldSize:0.###}";
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached;

            const int RES = 4;
            var tex = NewTexture(RES);
            var pixels = new Color32[RES * RES];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(pixels);
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, RES, RES), new Vector2(0.5f, 0.5f),
                RES / Mathf.Max(0.0001f, worldSize), 0, SpriteMeshType.FullRect);
            Cache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Filled disc, or an annulus when <paramref name="innerRadius01"/> &gt; 0
        /// (0.8 = a thin ring). Anti-aliased across one texel so it does not read
        /// as a staircase when the replay camera pushes in.
        /// </summary>
        public static Sprite Disc(float worldDiameter, float innerRadius01 = 0f)
        {
            string key = $"disc:{worldDiameter:0.###}:{innerRadius01:0.##}";
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached;

            const int RES = 64;
            var tex = NewTexture(RES);
            var pixels = new Color32[RES * RES];
            const float CENTRE = (RES - 1) * 0.5f;
            float outer = CENTRE;
            float inner = outer * Mathf.Clamp01(innerRadius01);
            // One texel of feather on each edge.
            const float FEATHER = 1.0f;

            for (int y = 0; y < RES; y++)
            {
                for (int x = 0; x < RES; x++)
                {
                    float dx = x - CENTRE;
                    float dy = y - CENTRE;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    float a = Mathf.Clamp01((outer - d) / FEATHER);
                    if (inner > 0f) a = Mathf.Min(a, Mathf.Clamp01((d - inner) / FEATHER));

                    pixels[y * RES + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, RES, RES), new Vector2(0.5f, 0.5f),
                RES / Mathf.Max(0.0001f, worldDiameter), 0, SpriteMeshType.FullRect);
            Cache[key] = sprite;
            return sprite;
        }

        static Texture2D NewTexture(int res)
        {
            return new Texture2D(res, res, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }
    }
}
