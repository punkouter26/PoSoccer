using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Thick team-colored goal mouth: a filled translucent target area inside a heavy
    /// border, so the goal reads instantly from the portrait camera. Auto-attaches to
    /// the goal transforms from Agent_EnvController.Awake so the scene never has to
    /// carry a serialized reference, and tracks EnvController.CurrentGoalWidth every
    /// episode (the curriculum and the squad-size pitch scaling both move it).
    ///
    /// Scale compensation matters here: this component lives ON the goal transform,
    /// which is scaled non-uniformly (roughly 4.8 x 0.26) to stretch the goal sprite.
    /// Drawing directly under that made the frame ~5x too wide and flat - it spanned
    /// the whole pitch and read as a boundary line rather than a goal. Everything is
    /// therefore drawn under a child whose scale is the inverse of the goal's, which
    /// puts the geometry back into true pitch metres.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class Agent_GoalFrame : MonoBehaviour
    {
        [Tooltip("Depth of the goal box as a fraction of goal width, floored so small goals stay readable.")]
        [SerializeField] private float _depthFraction = 0.85f;
        [SerializeField] private float _minDepth = 2.2f;
        [Tooltip("Border thickness as a fraction of goal width; the floor keeps small goals visible.")]
        [SerializeField] private float _thicknessFraction = 0.2f;
        [SerializeField] private float _minThickness = 0.55f;
        [Tooltip("Opacity of the filled goal mouth. High on purpose - on a green pitch under " +
                 "stadium lighting, a subtle tint disappears, especially on a phone screen.")]
        [Range(0f, 1f)]
        [SerializeField] private float _fillAlpha = 0.8f;
        [Tooltip("White inner keyline drawn against the fill so the colour reads against dark turf.")]
        [SerializeField] private float _keylineFraction = 0.07f;
        [Tooltip("Z offset so the frame sits in front of the pitch but behind the ball/agents.")]
        [SerializeField] private float _zOffset = -0.05f;

        Agent_EnvController _env;
        Transform _frameRoot;
        LineRenderer _top, _left, _right, _keyline;
        MeshFilter _fillFilter;
        Mesh _fillMesh;
        Color _color = Color.white;
        float _shownWidth = -1f;

        float Depth => Mathf.Max(_minDepth, _shownWidth * _depthFraction);

        void Awake()
        {
            _env = GetComponentInParent<Agent_EnvController>();
            BuildFrame();
        }

        void OnEnable()
        {
            Agent_EnvController.PitchReconfigured += OnPitchReconfigured;
            if (_env != null) ApplyWidth(_env.CurrentGoalWidth);
        }

        void OnDisable()
        {
            Agent_EnvController.PitchReconfigured -= OnPitchReconfigured;
        }

        void OnDestroy()
        {
            if (_fillMesh != null) Destroy(_fillMesh);
        }

        void OnPitchReconfigured(Transform goal)
        {
            if (goal == transform) ApplyWidth(_env != null ? _env.CurrentGoalWidth : 0f);
        }

        void LateUpdate()
        {
            if (_env == null) return;
            // Cheap guard: the goal only changes on a curriculum step or a resize,
            // so skip the rebuild on the frames where nothing moved.
            CompensateScale();
            if (!Mathf.Approximately(_env.CurrentGoalWidth, _shownWidth))
            {
                ApplyWidth(_env.CurrentGoalWidth);
            }
        }

        void BuildFrame()
        {
            var rootGo = new GameObject("GoalFrame");
            rootGo.transform.SetParent(transform, false);
            rootGo.transform.localPosition = Vector3.zero;
            rootGo.transform.localRotation = Quaternion.identity;
            _frameRoot = rootGo.transform;
            CompensateScale();

            // Filled mouth first so the border draws over it.
            var fillGo = new GameObject("GoalFill");
            fillGo.transform.SetParent(_frameRoot, false);
            _fillFilter = fillGo.AddComponent<MeshFilter>();
            _fillMesh = new Mesh { name = "GoalFillQuad" };
            _fillFilter.sharedMesh = _fillMesh;
            var fillRenderer = fillGo.AddComponent<MeshRenderer>();
            fillRenderer.sharedMaterial = NewMaterial();
            fillRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            fillRenderer.receiveShadows = false;
            fillRenderer.sortingOrder = 1;

            // Three sides only: the goal line itself is the open mouth.
            _keyline = BuildEdge("GoalFrame_Keyline");   // under the colour, so it rims the box
            _top = BuildEdge("GoalFrame_Back");
            _left = BuildEdge("GoalFrame_Left");
            _right = BuildEdge("GoalFrame_Right");
        }

        /// <summary>Undo the goal transform's non-uniform scale so children use pitch metres.</summary>
        void CompensateScale()
        {
            if (_frameRoot == null) return;
            Vector3 s = transform.lossyScale;
            _frameRoot.localScale = new Vector3(
                Mathf.Abs(s.x) < 0.0001f ? 1f : 1f / s.x,
                Mathf.Abs(s.y) < 0.0001f ? 1f : 1f / s.y,
                1f);
        }

        static Material NewMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Sprites/Default");
            return new Material(shader) { color = Color.white };
        }

        LineRenderer BuildEdge(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_frameRoot, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.numCornerVertices = 4;
            lr.numCapVertices = 4;
            lr.alignment = LineAlignment.View;
            lr.sharedMaterial = NewMaterial();
            lr.sortingOrder = 2;
            return lr;
        }

        void ApplyWidth(float width)
        {
            if (_top == null || width <= 0f) return;
            _shownWidth = width;

            float thickness = Mathf.Max(_minThickness, width * _thicknessFraction);
            float halfW = width * 0.5f;
            float depth = Depth;
            float z = _zOffset;

            // The goal sits on the pitch boundary facing inward, so the box extends
            // toward the centre: local -Y for the north goal is handled by the goal's
            // own rotation, which the parent already carries.
            // The back edge is the net itself - drawn double thickness so the goal
            // line reads as solid colour rather than an outline.
            SetEdge(_top, new Vector3(-halfW, 0f, z), new Vector3(halfW, 0f, z), thickness * 2f);
            SetEdge(_left, new Vector3(-halfW, 0f, z), new Vector3(-halfW, -depth, z), thickness);
            SetEdge(_right, new Vector3(halfW, 0f, z), new Vector3(halfW, -depth, z), thickness);

            // White keyline just inside the box: colour alone loses against dark turf
            // under the stadium lights, especially on a phone.
            float inset = thickness * 0.5f;
            float keyWidth = Mathf.Max(0.12f, width * _keylineFraction);
            SetEdge(_keyline,
                new Vector3(-halfW + inset, -depth + inset, z),
                new Vector3(halfW - inset, -depth + inset, z), keyWidth);

            RebuildFill(halfW, depth, z);
        }

        static void SetEdge(LineRenderer lr, Vector3 a, Vector3 b, float width)
        {
            if (lr == null) return;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
        }

        /// <summary>
        /// Quad covering the mouth. Colour is premultiplied by alpha because the URP
        /// 2D sprite shader blends One / OneMinusSrcAlpha - passing straight RGB at a
        /// low alpha renders additively and washes the pitch out.
        /// </summary>
        void RebuildFill(float halfW, float depth, float z)
        {
            if (_fillMesh == null) return;
            float a = _fillAlpha;
            var tint = new Color(_color.r * a, _color.g * a, _color.b * a, a);

            _fillMesh.Clear();
            _fillMesh.vertices = new[]
            {
                new Vector3(-halfW, 0f, z),
                new Vector3(halfW, 0f, z),
                new Vector3(halfW, -depth, z),
                new Vector3(-halfW, -depth, z),
            };
            _fillMesh.colors = new[] { tint, tint, tint, tint };
            _fillMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _fillMesh.RecalculateBounds();
        }

        /// <summary>Team-colored frame (Blue = red goal, Red = blue goal).</summary>
        public void SetColor(Color color)
        {
            _color = color;
            // Borders run brighter than the fill so the goal has an edge that survives
            // the vignette and the goal-moment bloom.
            Color border = Color.Lerp(color, Color.white, 0.25f);
            SetEdgeColor(_top, border);
            SetEdgeColor(_left, border);
            SetEdgeColor(_right, border);
            SetEdgeColor(_keyline, new Color(1f, 1f, 1f, 0.9f));
            if (_shownWidth > 0f) RebuildFill(_shownWidth * 0.5f, Depth, _zOffset);
        }

        static void SetEdgeColor(LineRenderer lr, Color color)
        {
            if (lr == null) return;
            lr.startColor = color;
            lr.endColor = color;
            if (lr.sharedMaterial != null) lr.sharedMaterial.color = color;
        }
    }
}
