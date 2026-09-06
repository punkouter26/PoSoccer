using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Procedural sprite factory for runtime-built presentation (replay ghosts,
    /// crowd stands, advertising boards, contact shadows).
    ///
    /// Deliberately generates textures in code rather than shipping PNGs. Two
    /// reasons, both project-specific: overwriting a PNG in place is a known way
    /// to corrupt its cached sprite geometry in this project (see CLAUDE.md), and
    /// a serialized Sprite reference would mean a scene edit on every scene that
    /// wants the feature. A handful of tiny textures cost nothing and cannot rot.
    ///
    /// 2026-09-05 - EVERY SHAPE NOW SHARES ONE ATLAS PAGE. The previous version
    /// allocated a Texture2D per cache entry and keyed that cache on shape AND
    /// world size, so Disc(1f) and Disc(0.8f) were two 64x64 textures holding
    /// byte-identical pixels. Different texture = different material state = a
    /// broken batch, and the callers are exactly the high-count ones: the crowd
    /// tilemap, every replay ghost, every ring, and now a shadow under every
    /// player and the ball. They all share one 512x512 page now.
    ///
    /// Two caches, and the split is the whole trick:
    ///  - SLOTS are keyed by SHAPE only. Pixels do not depend on world size.
    ///  - SPRITES are keyed by shape+size, and differ only in pixelsPerUnit,
    ///    which is sprite geometry rather than texture state and so does not
    ///    break batching.
    ///
    /// Slots are shelf-allocated with a bleed-safe gutter: each shape is written
    /// into a box padded by PAD texels whose border repeats the nearest inner
    /// texel, so bilinear filtering at a slot edge can never pull in the
    /// neighbouring shape.
    /// </summary>
    public static class Agent_Art
    {
        // 512 holds every shape this project asks for many times over: the whole
        // set below occupies well under one row of 64px shelves.
        const int PAGE = 512;
        const int PAD = 2;

        static Texture2D _page;
        static Color32[] _pixels;
        static int _cursorX, _shelfY, _shelfHeight;
        static bool _pendingApply;

        static readonly Dictionary<string, RectInt> Slots = new();
        static readonly Dictionary<string, Sprite> Sprites = new();

        /// <summary>
        /// The single shared texture behind every sprite this class returns.
        /// Exposed so a test can assert that two different shapes really do come
        /// back on one texture - the property that makes them batch, and the one
        /// that silently regressed before.
        /// </summary>
        public static Texture2D Page
        {
            get { EnsurePage(); return _page; }
        }

        /// <summary>Distinct shapes rasterised so far. Diagnostic only.</summary>
        public static int SlotCount => Slots.Count;

        // -- Shapes ----------------------------------------------------------

        /// <summary>White square exactly <paramref name="worldSize"/> units across.</summary>
        public static Sprite Square(float worldSize)
        {
            return GetSprite("sq", worldSize, 4, WriteSquare, 0f);
        }

        /// <summary>
        /// Filled disc, or an annulus when <paramref name="innerRadius01"/> is
        /// above zero (0.8 = a thin ring). Anti-aliased across one texel so it
        /// does not read as a staircase when the replay camera pushes in.
        /// </summary>
        public static Sprite Disc(float worldDiameter, float innerRadius01 = 0f)
        {
            return GetSprite($"disc:{innerRadius01:0.##}", worldDiameter, 64, WriteDisc, innerRadius01);
        }

        /// <summary>
        /// Soft-edged radial blob: opaque at the centre, falling to nothing at the
        /// rim with a shouldered falloff. This is the contact shadow under a
        /// player and under the ball, which is why the falloff is deliberately
        /// much softer than <see cref="Disc"/>'s one-texel feather - a hard-edged
        /// ellipse under a player reads as a second body, not as a shadow.
        /// </summary>
        public static Sprite Blob(float worldDiameter, float softness = 1f)
        {
            return GetSprite($"blob:{softness:0.##}", worldDiameter, 64, WriteBlob, softness);
        }

        // -- Cache -----------------------------------------------------------

        static Sprite GetSprite(string shapeKey, float worldSize, int resolution,
            System.Action<Color32[], int, float> writer, float parameter)
        {
            string spriteKey = $"{shapeKey}@{worldSize:0.###}";
            if (Sprites.TryGetValue(spriteKey, out var cached) && cached != null) return cached;

            RectInt slot = GetSlot(shapeKey, resolution, writer, parameter);
            Flush();

            // pixelsPerUnit is the ONLY thing that varies with world size, and it
            // is per-sprite geometry rather than per-texture state.
            float pixelsPerUnit = slot.width / Mathf.Max(0.0001f, worldSize);
            var sprite = Sprite.Create(
                _page,
                new Rect(slot.x, slot.y, slot.width, slot.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit, 0, SpriteMeshType.FullRect);
            sprite.name = spriteKey;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Sprites[spriteKey] = sprite;
            return sprite;
        }

        static RectInt GetSlot(string shapeKey, int resolution,
            System.Action<Color32[], int, float> writer, float parameter)
        {
            EnsurePage();
            if (Slots.TryGetValue(shapeKey, out var existing)) return existing;

            var buffer = new Color32[resolution * resolution];
            writer(buffer, resolution, parameter);

            RectInt slot = Allocate(resolution, resolution);
            Blit(buffer, resolution, slot);
            Slots[shapeKey] = slot;
            return slot;
        }

        // -- Page management --------------------------------------------------

        static void EnsurePage()
        {
            if (_page != null) return;

            _page = new Texture2D(PAGE, PAGE, TextureFormat.RGBA32, false)
            {
                name = "PoSoccer_RuntimeAtlas",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            _pixels = new Color32[PAGE * PAGE];
            // Transparent BLACK rather than transparent white: a premultiply
            // anywhere downstream would otherwise bleed a white halo out of the
            // gutter and rim every shadow with a bright edge.
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = new Color32(0, 0, 0, 0);

            _cursorX = 0;
            _shelfY = 0;
            _shelfHeight = 0;
            _pendingApply = true;

            // A domain reload disposes the texture but leaves these dictionaries
            // holding rects into a page that no longer exists.
            Slots.Clear();
            Sprites.Clear();
        }

        /// <summary>Shelf allocator. Shapes are few and small, so first-fit by row is enough.</summary>
        static RectInt Allocate(int width, int height)
        {
            int stride = width + PAD * 2;
            int rise = height + PAD * 2;

            if (_cursorX + stride > PAGE)
            {
                _cursorX = 0;
                _shelfY += _shelfHeight;
                _shelfHeight = 0;
            }
            if (_shelfY + rise > PAGE)
            {
                // Cannot happen with the current shape set. If a future shape
                // overflows the page, say so rather than writing out of bounds.
                Debug.LogWarning("Agent_Art: runtime atlas page is full; reusing origin.");
                _cursorX = 0;
                _shelfY = 0;
                _shelfHeight = 0;
            }

            var slot = new RectInt(_cursorX + PAD, _shelfY + PAD, width, height);
            _cursorX += stride;
            if (rise > _shelfHeight) _shelfHeight = rise;
            return slot;
        }

        /// <summary>
        /// Copy a shape into its slot and extend its border into the gutter, so a
        /// bilinear tap at the slot edge samples a repeat of the shape rather
        /// than whatever shape happens to sit next door.
        /// </summary>
        static void Blit(Color32[] source, int resolution, RectInt slot)
        {
            for (int y = -PAD; y < resolution + PAD; y++)
            {
                int sourceY = Mathf.Clamp(y, 0, resolution - 1);
                int destinationY = slot.y + y;
                if (destinationY < 0 || destinationY >= PAGE) continue;

                for (int x = -PAD; x < resolution + PAD; x++)
                {
                    int sourceX = Mathf.Clamp(x, 0, resolution - 1);
                    int destinationX = slot.x + x;
                    if (destinationX < 0 || destinationX >= PAGE) continue;

                    _pixels[destinationY * PAGE + destinationX] = source[sourceY * resolution + sourceX];
                }
            }
            _pendingApply = true;
        }

        static void Flush()
        {
            if (!_pendingApply || _page == null) return;
            _page.SetPixels32(_pixels);
            _page.Apply(false);
            _pendingApply = false;
        }

        // -- Rasterisers -------------------------------------------------------

        static void WriteSquare(Color32[] pixels, int resolution, float unused)
        {
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
        }

        static void WriteDisc(Color32[] pixels, int resolution, float innerRadius01)
        {
            float centre = (resolution - 1) * 0.5f;
            float outer = centre;
            float inner = outer * Mathf.Clamp01(innerRadius01);
            const float FEATHER = 1.0f;   // one texel of edge, as before

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float dx = x - centre;
                    float dy = y - centre;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    float a = Mathf.Clamp01((outer - d) / FEATHER);
                    if (inner > 0f) a = Mathf.Min(a, Mathf.Clamp01((d - inner) / FEATHER));

                    pixels[y * resolution + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
        }

        static void WriteBlob(Color32[] pixels, int resolution, float softness)
        {
            float centre = (resolution - 1) * 0.5f;
            float outer = centre;
            float power = Mathf.Lerp(1.2f, 3.2f, Mathf.Clamp01(softness));

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float dx = (x - centre) / outer;
                    float dy = (y - centre) / outer;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // 1 at the centre, 0 at the rim, with a shoulder that keeps
                    // the core dark and the edge genuinely soft.
                    float a = Mathf.Pow(Mathf.Clamp01(1f - d), power);
                    pixels[y * resolution + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
        }
    }
}
