using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Thick colored rectangle at the goal mouth so the team and the goal width
    /// read at a glance from the portrait camera. Auto-attaches to the goal
    /// transforms from Agent_EnvController.Awake so the scene never has to
    /// carry a serialized reference. Width tracks EnvController.CurrentGoalWidth
    /// every episode so the curriculum readout is visible.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class Agent_GoalFrame : MonoBehaviour
    {
        [Tooltip("Half-height (in pitch units) of the goal frame above/below the mouth.")]
        [SerializeField] private float _frameHalfHeight = 1.0f;
        [Tooltip("Line width for the goal frame.")]
        [SerializeField] private float _lineThickness = 0.12f;
        [Tooltip("Z offset so the frame sits in front of the pitch but behind the ball/agents.")]
        [SerializeField] private float _zOffset = -0.05f;

        Agent_EnvController _env;
        LineRenderer _top;
        LineRenderer _bottom;
        LineRenderer _left;
        LineRenderer _right;

        void Awake()
        {
            _env = GetComponentInParent<Agent_EnvController>();
            BuildEdges();
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
            // Goal frame is anchored to the goal transform; ensures it follows
            // any pitch shifts without per-frame allocations.
            if (_env != null) ApplyWidth(_env.CurrentGoalWidth);
        }

        void BuildEdges()
        {
            _top = BuildEdge("GoalFrame_Top");
            _bottom = BuildEdge("GoalFrame_Bottom");
            _left = BuildEdge("GoalFrame_Left");
            _right = BuildEdge("GoalFrame_Right");
        }

        LineRenderer BuildEdge(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.numCornerVertices = 0;
            lr.numCapVertices = 0;
            lr.alignment = LineAlignment.View;
            lr.startWidth = _lineThickness;
            lr.endWidth = _lineThickness;
            var mat = new Material(Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Sprites/Default"));
            mat.color = Color.white;
            lr.sharedMaterial = mat;
            return lr;
        }

        void ApplyWidth(float width)
        {
            // Goal mouth spans local X (pitch runs along Y for mobile portrait).
            float halfW = width * 0.5f + 0.1f;
            float halfH = _frameHalfHeight;
            float z = _zOffset;
            _top.SetPosition(0, new Vector3(-halfW, halfH, z));
            _top.SetPosition(1, new Vector3(halfW, halfH, z));
            _bottom.SetPosition(0, new Vector3(-halfW, -halfH, z));
            _bottom.SetPosition(1, new Vector3(halfW, -halfH, z));
            _left.SetPosition(0, new Vector3(-halfW, -halfH, z));
            _left.SetPosition(1, new Vector3(-halfW, halfH, z));
            _right.SetPosition(0, new Vector3(halfW, -halfH, z));
            _right.SetPosition(1, new Vector3(halfW, halfH, z));
        }

        /// <summary>Team-colored frame (Blue = red goal, Red = blue goal).</summary>
        public void SetColor(Color color)
        {
            if (_top != null) { _top.startColor = color; _top.endColor = color; }
            if (_bottom != null) { _bottom.startColor = color; _bottom.endColor = color; }
            if (_left != null) { _left.startColor = color; _left.endColor = color; }
            if (_right != null) { _right.startColor = color; _right.endColor = color; }
            var mat = _top != null ? _top.sharedMaterial : null;
            if (mat != null) mat.color = color;
        }
    }
}
