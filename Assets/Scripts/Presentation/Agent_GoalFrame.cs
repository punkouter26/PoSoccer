using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Marks the goal mouth as a thick team-coloured segment laid over the grey
    /// boundary wall. Nothing is drawn on the playing surface: earlier versions
    /// projected a goal box onto the pitch, which read as a penalty area and
    /// cluttered the space the players actually use.
    ///
    /// Auto-attaches to the goal transforms from Agent_EnvController.Awake so the
    /// scene never has to carry a serialized reference, and tracks
    /// EnvController.CurrentGoalWidth (the curriculum and the squad-size pitch
    /// scaling both move it).
    ///
    /// Scale compensation matters: this component lives ON the goal transform,
    /// which is scaled non-uniformly (roughly 4.8 x 0.26) to stretch its sprite.
    /// Drawing directly under that made the marker ~5x too wide and flat - it
    /// spanned the whole boundary and read as a wall, not a goal. Everything
    /// therefore hangs off a child whose scale is the inverse of the goal's, which
    /// puts the geometry back into true pitch metres.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class Agent_GoalFrame : MonoBehaviour
    {
        [Tooltip("Marker thickness as a multiple of the boundary wall's thickness. " +
                 "Slightly over 1 so it covers the wall band rather than sitting inside it.")]
        [SerializeField] private float _thicknessOfWall = 1.15f;
        [Tooltip("Fallback thickness when no boundary wall can be found.")]
        [SerializeField] private float _fallbackThickness = 1.1f;
        [Tooltip("Drawn above the wall sprites; the scene's highest sorting order is 4.")]
        [SerializeField] private int _sortingOrder = 6;

        Agent_EnvController _env;
        Transform _frameRoot;
        LineRenderer _bar;
        Color _color = Color.white;
        float _shownWidth = -1f;

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

        void OnPitchReconfigured(Transform goal)
        {
            if (goal == transform) ApplyWidth(_env != null ? _env.CurrentGoalWidth : 0f);
        }

        void LateUpdate()
        {
            if (_env == null) return;
            CompensateScale();
            // The goal only changes on a curriculum step or a pitch resize, so skip
            // the rebuild on the frames where nothing moved.
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

            var go = new GameObject("GoalMouthBar");
            go.transform.SetParent(_frameRoot, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            _bar = go.AddComponent<LineRenderer>();
            _bar.useWorldSpace = false;
            _bar.positionCount = 2;
            _bar.numCornerVertices = 0;
            _bar.numCapVertices = 0;
            _bar.alignment = LineAlignment.View;
            _bar.sortingOrder = _sortingOrder;
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Sprites/Default");
            _bar.sharedMaterial = new Material(shader) { color = Color.white };
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

        void ApplyWidth(float width)
        {
            if (_bar == null || width <= 0f) return;
            _shownWidth = width;

            float halfW = width * 0.5f;
            var wall = OutwardWall();
            // Sit on the wall's centre line, in the goal's own local metres.
            float y = wall != null ? wall.localPosition.y - transform.localPosition.y : 0f;
            float thickness = wall != null
                ? Mathf.Abs(wall.localScale.y) * _thicknessOfWall
                : _fallbackThickness;

            _bar.startWidth = thickness;
            _bar.endWidth = thickness;
            _bar.SetPosition(0, new Vector3(-halfW, y, 0f));
            _bar.SetPosition(1, new Vector3(halfW, y, 0f));
        }

        /// <summary>
        /// The boundary wall behind this goal. Found by position rather than by name
        /// so it survives a pitch resize, which moves every wall.
        /// </summary>
        Transform OutwardWall()
        {
            if (_env == null) return null;
            float goalY = transform.localPosition.y;
            Transform best = null;
            float bestDistance = float.MaxValue;
            foreach (Transform child in _env.transform)
            {
                if (!child.name.StartsWith("Wall_")) continue;
                // Same side as the goal, and running across the pitch rather than along it.
                if (Mathf.Sign(child.localPosition.y) != Mathf.Sign(goalY)) continue;
                if (Mathf.Abs(child.localScale.x) < Mathf.Abs(child.localScale.y)) continue;
                float distance = Mathf.Abs(child.localPosition.y - goalY);
                if (distance < bestDistance) { bestDistance = distance; best = child; }
            }
            return best;
        }

        /// <summary>Team-colored goal mouth (Blue = red goal, Red = blue goal).</summary>
        public void SetColor(Color color)
        {
            _color = color;
            if (_bar == null) return;
            _bar.startColor = _color;
            _bar.endColor = _color;
            if (_bar.sharedMaterial != null) _bar.sharedMaterial.color = _color;
        }
    }
}
