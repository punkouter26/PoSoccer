using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PoSoccer
{
    /// <summary>
    /// Slow-motion goal replay, played back from a recorded pose buffer as ghost
    /// sprites over a darkened, frozen pitch.
    ///
    /// WHY GHOSTS AND NOT THE REAL BODIES. Agent_EnvController.OnGoalScored fires
    /// EpisodeEnded and then calls ResetPitch() synchronously in the same call
    /// stack, so by the time any handler resumes, every player and the ball have
    /// already been teleported back to kickoff. There is no window in which the
    /// real bodies still hold the scoring positions. Replaying onto separate
    /// ghost renderers sidesteps that entirely and - more importantly - keeps the
    /// replay strictly read-only: it never writes to a Rigidbody2D, never touches
    /// an Agent, and so cannot perturb the simulation it is showing.
    ///
    /// The clock is stopped through Agent_TimeFreeze for the duration, which also
    /// stops FixedUpdate - so the capture ring cannot advance mid-playback and the
    /// buffer is safe to read without double-buffering.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Replay : MonoBehaviour
    {
        [Tooltip("Seconds of match time replayed after a goal.")]
        [SerializeField] private float _replaySeconds = 2.2f;
        [Tooltip("Playback rate. 0.5 = half speed, so 2.2s of play takes 4.4s of wall time.")]
        [Range(0.15f, 1f)] [SerializeField] private float _playbackSpeed = 0.55f;
        [Tooltip("Seconds the GOAL callout holds before the replay cuts in.")]
        [SerializeField] private float _preRollSeconds = 0.9f;
        [Tooltip("Orthographic size for the replay shot - smaller is a tighter cut.")]
        [SerializeField] private float _replayOrtho = 5.5f;
        [Tooltip("Skip a replay shorter than this - a goal in the opening moments has no build-up worth showing.")]
        [SerializeField] private float _minimumSeconds = 0.6f;
        [SerializeField] private bool _enableReplay = true;

        /// <summary>True from the goal until the pitch is handed back. Read by Agent_MatchFlow.</summary>
        public bool IsPlaying { get; private set; }

        struct Pose
        {
            public Vector2 Position;
            public float Rotation;
        }

        Agent_EnvController _env;
        Agent_HUD _hud;
        Agent_CameraFollow _camera;

        Pose[] _ring;          // frames x slots, slot 0 = ball, 1.. = agents
        int _slots;
        int _frames;
        int _head;             // next write index
        int _filled;

        Transform _ghostRoot;
        Transform[] _ghosts;
        SpriteRenderer _scrim;
        CancellationTokenSource _cts;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindFirstObjectByType<Agent_HUD>();
            _camera = Camera.main != null ? Camera.main.GetComponent<Agent_CameraFollow>() : null;

            if (!_enableReplay || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _slots = 1 + _env.agents.Count;
            _frames = Mathf.Max(8, Mathf.CeilToInt(_replaySeconds / Mathf.Max(0.001f, Time.fixedDeltaTime)));
            _ring = new Pose[_frames * _slots];

            _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            // A replay interrupted by a scene change must not leave the clock stopped.
            Agent_TimeFreeze.Release(this);
        }

        // -- Capture ---------------------------------------------------------

        void FixedUpdate()
        {
            if (_ring == null || _env == null || _env.Ball == null) return;

            int at = _head * _slots;
            _ring[at].Position = _env.Ball.position;
            _ring[at].Rotation = _env.Ball.rotation;

            var agents = _env.agents;
            for (int i = 0; i < agents.Count && i + 1 < _slots; i++)
            {
                var agent = agents[i];
                if (agent == null || agent.Body == null) continue;
                _ring[at + 1 + i].Position = agent.Body.position;
                _ring[at + 1 + i].Rotation = agent.Body.rotation;
            }

            _head = (_head + 1) % _frames;
            if (_filled < _frames) _filled++;
        }

        // -- Playback --------------------------------------------------------

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null || IsPlaying) return;
            if (_filled * Time.fixedDeltaTime < _minimumSeconds) return;

            // Set synchronously so Agent_MatchFlow can await this flag regardless
            // of which of the two subscribed to EpisodeEnded first.
            IsPlaying = true;
            Agent_TimeFreeze.Acquire(this);

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(
                this.GetCancellationTokenOnDestroy());
            PlayAsync(winner.Value, _cts.Token).Forget();
        }

        async UniTaskVoid PlayAsync(Agent_Soccer.Team winner, CancellationToken token)
        {
            int count = _filled;
            int start = (_head - count + _frames) % _frames;

            try
            {
                // Let the GOAL callout and the horn land before cutting to the replay.
                await UniTask.Delay(System.TimeSpan.FromSeconds(_preRollSeconds),
                    DelayType.UnscaledDeltaTime, cancellationToken: token);

                BuildGhosts(winner);
                if (_hud != null) _hud.SetReplayChrome(true);

                float cursor = 0f;
                float step = _playbackSpeed / Mathf.Max(0.001f, Time.fixedDeltaTime);

                while (cursor < count - 1)
                {
                    if (Skipped()) break;
                    ApplyFrame(start, count, cursor);
                    UpdateScrim();
                    cursor += Time.unscaledDeltaTime * step;
                    await UniTask.Yield(PlayerLoopTiming.Update, token);
                }

                if (!token.IsCancellationRequested)
                {
                    ApplyFrame(start, count, count - 1);
                    // Hold on the finish for a beat - the goal is the point of the shot.
                    await UniTask.Delay(System.TimeSpan.FromSeconds(0.45f),
                        DelayType.UnscaledDeltaTime, cancellationToken: token);
                }
            }
            finally
            {
                TeardownGhosts();
                if (_hud != null) _hud.SetReplayChrome(false);
                if (_camera != null) _camera.ClearOverrideTarget();
                // The next goal must replay the next episode, not this one's tail.
                _filled = 0;
                _head = 0;
                IsPlaying = false;
                Agent_TimeFreeze.Release(this);
            }
        }

        static bool Skipped()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null
                && (keyboard.spaceKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame))
                return true;

            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) return true;

            var touch = Touchscreen.current;
            return touch != null && touch.primaryTouch.press.wasPressedThisFrame;
        }

        /// <summary>Samples the ring at a fractional frame and writes the ghost transforms.</summary>
        void ApplyFrame(int start, int count, float cursor)
        {
            int index = Mathf.Clamp(Mathf.FloorToInt(cursor), 0, count - 1);
            int next = Mathf.Min(index + 1, count - 1);
            float blend = Mathf.Clamp01(cursor - index);

            int a = ((start + index) % _frames) * _slots;
            int b = ((start + next) % _frames) * _slots;

            for (int slot = 0; slot < _slots; slot++)
            {
                var ghost = _ghosts[slot];
                if (ghost == null) continue;
                Vector2 position = Vector2.Lerp(_ring[a + slot].Position, _ring[b + slot].Position, blend);
                float rotation = Mathf.LerpAngle(_ring[a + slot].Rotation, _ring[b + slot].Rotation, blend);
                // Poses were captured in world space; ghosts are parented to the
                // pitch root, so convert back through it.
                ghost.position = new Vector3(position.x, position.y, 0f);
                ghost.rotation = Quaternion.Euler(0f, 0f, rotation);
            }
        }

        // -- Ghost construction ----------------------------------------------

        void BuildGhosts(Agent_Soccer.Team winner)
        {
            var rootGo = new GameObject("ReplayGhosts");
            _ghostRoot = rootGo.transform;
            _ghostRoot.SetParent(transform, false);
            _ghosts = new Transform[_slots];

            // Scrim: pushes the live, already-reset pitch back so the ghosts read
            // as the subject. Parented to the camera so it covers any zoom.
            if (Camera.main != null)
            {
                var scrimGo = new GameObject("ReplayScrim");
                scrimGo.transform.SetParent(Camera.main.transform, false);
                scrimGo.transform.localPosition = new Vector3(0f, 0f, 1f);
                _scrim = scrimGo.AddComponent<SpriteRenderer>();
                _scrim.sprite = Agent_Art.Square(1f);
                _scrim.color = new Color(0f, 0f, 0f, 0.62f);
                _scrim.sortingOrder = 400;
                UpdateScrim();
            }

            var ballRenderer = _env.Ball != null
                ? _env.Ball.GetComponentInChildren<SpriteRenderer>() : null;
            _ghosts[0] = MakeGhost("GhostBall", ballRenderer, Color.white, 0f, Color.white);

            var agents = _env.agents;
            for (int i = 0; i < agents.Count && i + 1 < _slots; i++)
            {
                var agent = agents[i];
                if (agent == null) continue;
                var body = agent.GetComponent<SpriteRenderer>();
                Color tint = agent.rewards != null ? agent.rewards.playerColor : Color.white;
                // The scoring side gets a brighter team ring so the eye knows who to follow.
                float ring = agent.team == winner ? 1f : 0.45f;
                _ghosts[i + 1] = MakeGhost("Ghost_" + agent.name, body, tint, ring,
                    Agent_SoccerView.TeamColor(agent.team));
            }

            // A trail on the ball ghost is what makes a slow-motion shot readable.
            if (_ghosts[0] != null)
            {
                var trail = _ghosts[0].gameObject.AddComponent<TrailRenderer>();
                trail.time = 1.2f;
                trail.startWidth = 0.2f;
                trail.endWidth = 0f;
                trail.numCapVertices = 4;
                var shader = Shader.Find("Sprites/Default");
                if (shader != null) trail.material = new Material(shader);
                trail.startColor = new Color(1f, 0.95f, 0.5f, 0.9f);
                trail.endColor = new Color(1f, 0.95f, 0.5f, 0f);
                trail.sortingOrder = 401;
            }

            if (_camera != null && _ghosts[0] != null)
                _camera.SetOverrideTarget(_ghosts[0], _replayOrtho);
        }

        Transform MakeGhost(string label, SpriteRenderer source, Color tint,
            float ringStrength, Color ringColor)
        {
            var go = new GameObject(label);
            go.transform.SetParent(_ghostRoot, false);

            var renderer = go.AddComponent<SpriteRenderer>();
            if (source != null)
            {
                renderer.sprite = source.sprite;
                // lossyScale, not localScale: the ball's visual is a scaled child,
                // and the pitch itself is rescaled per squad size by Agent_PitchSizing.
                go.transform.localScale = source.transform.lossyScale;
            }
            else
            {
                renderer.sprite = Agent_Art.Disc(1f);
            }
            renderer.color = new Color(tint.r, tint.g, tint.b, 0.95f);
            renderer.sortingOrder = 402;

            if (ringStrength > 0f)
            {
                var ringGo = new GameObject("Ring");
                ringGo.transform.SetParent(go.transform, false);
                ringGo.transform.localScale = Vector3.one * 1.55f;
                var ringRenderer = ringGo.AddComponent<SpriteRenderer>();
                ringRenderer.sprite = Agent_Art.Disc(1f, 0.72f);
                ringRenderer.color = new Color(ringColor.r, ringColor.g, ringColor.b, ringStrength);
                ringRenderer.sortingOrder = 401;
            }

            return go.transform;
        }

        void UpdateScrim()
        {
            if (_scrim == null || Camera.main == null) return;
            float height = Camera.main.orthographicSize * 2.4f;
            _scrim.transform.localScale = new Vector3(height * Camera.main.aspect, height, 1f);
        }

        void TeardownGhosts()
        {
            if (_ghostRoot != null) Destroy(_ghostRoot.gameObject);
            if (_scrim != null) Destroy(_scrim.gameObject);
            _ghostRoot = null;
            _scrim = null;
            _ghosts = null;
        }
    }
}
