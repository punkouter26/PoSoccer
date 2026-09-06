using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// A world-space vector-graphics batch: lines, arrows and arcs written into
    /// ONE dynamic mesh with vertex colours, so an overlay of any complexity
    /// costs exactly one draw call.
    ///
    /// This exists because the obvious implementations both fail the budget in
    /// .claude/rules/performance.md. A LineRenderer per mark ties the draw-call
    /// count to the squad size (and, for the vision overlay, to the ray count:
    /// 40 rays x 4 players is 160 renderers). Debug.DrawLine draws nothing in a
    /// player build at all. A single procedural mesh is the only option that
    /// scales and ships.
    ///
    /// ALLOCATION: the three lists are allocated once at construction and only
    /// ever Cleared, and Mesh.SetVertices(List&lt;T&gt;) takes the list directly.
    /// A steady-state frame therefore allocates nothing, which is the bar Update
    /// code has to clear here.
    ///
    /// WORLD SPACE, ALWAYS. The renderer's GameObject is created unparented with
    /// an identity transform, because every caller computes vertices from
    /// Rigidbody2D.position or a sensor's StartPositionWorld. Parenting it to the
    /// pitch root would silently offset the whole overlay by the root's position -
    /// which is not zero on the exhibition pitch.
    /// </summary>
    public sealed class Agent_Lines
    {
        readonly List<Vector3> _vertices;
        readonly List<Color> _colors;
        readonly List<int> _indices;

        readonly Mesh _mesh;
        readonly MeshRenderer _renderer;
        readonly GameObject _host;

        public Agent_Lines(string label, int sortingOrder, int vertexCapacity = 1024)
        {
            _vertices = new List<Vector3>(vertexCapacity);
            _colors = new List<Color>(vertexCapacity);
            _indices = new List<int>(vertexCapacity * 3 / 2);

            _host = new GameObject(label);

            _mesh = new Mesh { name = label };
            // Rewritten every frame; this skips the redundant upload optimisation.
            _mesh.MarkDynamic();
            _host.AddComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = _host.AddComponent<MeshRenderer>();
            var shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                // Sprites/Default multiplies by vertex colour, which is where every
                // tint comes from - so one material covers the whole batch and no
                // MaterialPropertyBlock is needed.
                _renderer.sharedMaterial = new Material(shader) { mainTexture = Texture2D.whiteTexture };
            }
            _renderer.sortingOrder = sortingOrder;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }

        /// <summary>Show or hide the whole batch without rebuilding it.</summary>
        public bool Visible
        {
            get => _renderer != null && _renderer.enabled;
            set { if (_renderer != null) _renderer.enabled = value; }
        }

        /// <summary>
        /// The batch's renderer, so a caller can see the draw-call cost it is
        /// paying (one, by construction) and check the sorting order it landed on.
        /// </summary>
        public MeshRenderer Renderer => _renderer;

        /// <summary>Vertices written since <see cref="Begin"/>. A budget number, not a cost.</summary>
        public int VertexCount => _vertices.Count;

        /// <summary>Drop everything this batch owns. Safe to call twice.</summary>
        public void Dispose()
        {
            if (_renderer != null) Kill(_renderer.sharedMaterial);
            Kill(_mesh);
            Kill(_host);
        }

        /// <summary>
        /// Object.Destroy is deferred and, outside play mode, an error rather than
        /// a no-op - so an EditMode test exercising this class would fail on the
        /// cleanup rather than on anything it was testing.
        /// </summary>
        static void Kill(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }

        /// <summary>Start a new frame's geometry.</summary>
        public void Begin()
        {
            _vertices.Clear();
            _colors.Clear();
            _indices.Clear();
        }

        /// <summary>Upload whatever was added since <see cref="Begin"/>.</summary>
        public void Commit()
        {
            if (_mesh == null) return;
            _mesh.Clear();
            if (_vertices.Count == 0) return;
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_indices, 0, calculateBounds: true);
        }

        /// <summary>Quad from a to b with the given half-width.</summary>
        public void AddSegment(Vector2 a, Vector2 b, float halfWidth, Color tint)
        {
            Vector2 delta = b - a;
            float length = delta.magnitude;
            if (length < 0.0001f) return;

            Vector2 normal = new Vector2(-delta.y, delta.x) / length * halfWidth;
            int baseIndex = _vertices.Count;

            _vertices.Add(a - normal);
            _vertices.Add(a + normal);
            _vertices.Add(b + normal);
            _vertices.Add(b - normal);
            for (int i = 0; i < 4; i++) _colors.Add(tint);

            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 1);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex + 3);
        }

        /// <summary>A line from a to b that fades along its length.</summary>
        public void AddFadedSegment(Vector2 a, Vector2 b, float halfWidth, Color from, Color to)
        {
            Vector2 delta = b - a;
            float length = delta.magnitude;
            if (length < 0.0001f) return;

            Vector2 normal = new Vector2(-delta.y, delta.x) / length * halfWidth;
            int baseIndex = _vertices.Count;

            _vertices.Add(a - normal);
            _vertices.Add(a + normal);
            _vertices.Add(b + normal);
            _vertices.Add(b - normal);
            _colors.Add(from);
            _colors.Add(from);
            _colors.Add(to);
            _colors.Add(to);

            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 1);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex + 3);
        }

        /// <summary>
        /// Triangle with automatic winding. A back-facing triangle in a procedural
        /// mesh silently vanishes, and tracking that down is an afternoon nobody
        /// needs to spend, so the winding is derived from the cross product rather
        /// than trusted to the caller's point order.
        /// </summary>
        public void AddTriangle(Vector2 a, Vector2 b, Vector2 c, Color tint)
        {
            int baseIndex = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            for (int i = 0; i < 3; i++) _colors.Add(tint);

            float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (cross >= 0f)
            {
                _indices.Add(baseIndex);
                _indices.Add(baseIndex + 1);
                _indices.Add(baseIndex + 2);
            }
            else
            {
                _indices.Add(baseIndex);
                _indices.Add(baseIndex + 2);
                _indices.Add(baseIndex + 1);
            }
        }

        /// <summary>Shaft plus head, along a unit direction.</summary>
        public void AddArrow(Vector2 from, Vector2 direction, float length, float halfWidth, Color tint)
        {
            if (length <= 0.001f) return;

            float headLength = Mathf.Min(length * 0.42f, halfWidth * 7f);
            Vector2 tip = from + direction * length;
            Vector2 shaftEnd = tip - direction * headLength;

            AddSegment(from, shaftEnd, halfWidth, tint);

            Vector2 normal = new(-direction.y, direction.x);
            AddTriangle(tip,
                shaftEnd + normal * (halfWidth * 2.4f),
                shaftEnd - normal * (halfWidth * 2.4f),
                tint);
        }

        /// <summary>
        /// Arc centred on <paramref name="centre"/>, running clockwise from
        /// straight up and covering <paramref name="fraction"/> of a full turn.
        /// </summary>
        public void AddArc(Vector2 centre, float radius, float fraction, float halfWidth,
                           Color tint, int segmentsPerTurn = 24)
        {
            if (fraction <= 0.001f) return;

            int segments = Mathf.Max(1, Mathf.RoundToInt(segmentsPerTurn * fraction));
            float step = fraction * Mathf.PI * 2f / segments;

            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.PI * 0.5f - i * step;
                float a1 = Mathf.PI * 0.5f - (i + 1) * step;
                Vector2 p0 = centre + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius;
                Vector2 p1 = centre + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;
                AddSegment(p0, p1, halfWidth, tint);
            }
        }

        /// <summary>Small filled diamond - a hit marker that stays legible when zoomed out.</summary>
        public void AddDiamond(Vector2 centre, float radius, Color tint)
        {
            AddTriangle(centre + Vector2.up * radius, centre + Vector2.right * radius,
                centre + Vector2.down * radius, tint);
            AddTriangle(centre + Vector2.up * radius, centre + Vector2.down * radius,
                centre + Vector2.left * radius, tint);
        }
    }
}
