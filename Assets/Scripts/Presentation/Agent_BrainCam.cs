using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The brain cam: the four numbers each policy emitted on its last decision,
    /// plus how hard it is committing and how much it is thrashing.
    ///
    /// WHY THIS IS THE FEATURE A PROJECT ABOUT WATCHING AI OWES ITS AUDIENCE. The
    /// intent overlay draws what a policy WANTS in world space, which is the right
    /// answer for one glance at the pitch. This is the other half: the action
    /// vector itself, unmodified, per channel, next to the same vector from the
    /// rule-based bot. Two brains asked the identical question, and you can read
    /// both answers at once.
    ///
    /// THE CHURN COLUMN IS THE POINT. CLAUDE.md's live probe on 2026-09-07 measured
    /// the trained brains at 174 degrees of heading churn over a 4 s chase against
    /// the scripted bot's 83 - spinning at 2.1x the bot's rate while covering 80%
    /// of the ground - and that single number is why ActionGain went back to 1.0.
    /// It was produced headless, printed to a console, and read by nobody watching
    /// a match. Here it is a live column, computed the same way: sign flips per
    /// decision on the move and turn channels.
    ///
    /// SAMPLED PER DECISION, NOT PER TICK. ML-Agents repeats the last action
    /// between decisions (DecisionRequester period 8), so counting sign flips per
    /// physics step would divide every rate by eight and make a thrashing policy
    /// look steady. A new decision is detected as a CHANGE in
    /// <see cref="Agent_Soccer.LastRawActions"/>, which is exact and costs one
    /// Vector4 comparison per player per tick.
    ///
    /// RAW ACTIONS, NOT GAINED ONES. <see cref="Agent_Soccer.ActionGain"/> is a
    /// multiply-then-clamp, so a gain above 1 destroys the distinction between
    /// every output past its dead-band edge - which is exactly the defect that
    /// took three brains out of comparability. The bars show what the network
    /// emitted; the gain is printed in the header so the two are never confused.
    ///
    /// COST WHEN HIDDEN IS ZERO: the panel is built on first show and Update
    /// returns immediately while closed. Toggle with B.
    /// Presentation only; self-disables in training and evaluation.
    /// </summary>
    [DefaultExecutionOrder(90)]
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_BrainCam : MonoBehaviour
    {
        [Tooltip("Show the panel from the moment the match starts.")]
        [SerializeField] private bool _visibleOnStart;
        [Tooltip("Decisions retained for the rolling commitment and churn figures. 50 " +
                 "decisions at period 8 on a 0.01 s timestep is four seconds of play - the " +
                 "same window Agent_PlayMode_MovementProbe grades a chase over.")]
        [SerializeField] private int _window = 50;
        [Tooltip("Seconds between text refreshes. Bars follow the live value every frame; " +
                 "only the numeric labels are rate-limited, because a string per label per " +
                 "frame is the one allocation this panel could plausibly make.")]
        [SerializeField] private float _refreshInterval = 0.12f;
        [Tooltip("Sorting order for the panel's own UIDocument. Above the HUD (0) and the " +
                 "chrome (10), below the telemetry overlay (100).")]
        [SerializeField] private int _sortingOrder = 40;
        [SerializeField] private bool _enableBrainCam = true;

        /// <summary>Channel order is the action vector's own: see Agent_Soccer.OnActionReceived.</summary>
        static readonly string[] ChannelNames = { "FWD", "LAT", "TURN", "BST" };

        /// <summary>
        /// One player's rolling decision record. A class, not a struct: it owns ring
        /// buffers, and a struct in a dictionary would copy them on every read.
        /// Allocated once per player on first sight, never per tick.
        /// </summary>
        sealed class Readout
        {
            public Vector4 Last;
            public bool Seeded;
            public float[] Move;
            public float[] Turn;
            public int Count;
            public int Head;

            public Readout(int window)
            {
                Move = new float[window];
                Turn = new float[window];
            }

            public void Push(Vector4 action)
            {
                Move[Head] = action.x;
                Turn[Head] = action.z;
                Head = (Head + 1) % Move.Length;
                if (Count < Move.Length) Count++;
            }

            /// <summary>Mean absolute forward demand over the window - the commitment figure.</summary>
            public float Commitment()
            {
                if (Count == 0) return 0f;
                float sum = 0f;
                for (int i = 0; i < Count; i++) sum += Mathf.Abs(Move[i]);
                return sum / Count;
            }

            /// <summary>
            /// Sign flips per 100 decisions on one channel. Walks the ring in
            /// insertion order so a wrapped buffer does not report a phantom flip at
            /// the seam - which would put a steady policy's churn at the same figure
            /// as a thrashing one on a full buffer.
            /// </summary>
            public float FlipsPer100(float[] channel)
            {
                if (Count < 2) return 0f;
                int start = Count < channel.Length ? 0 : Head;
                int flips = 0;
                float previous = channel[start];
                for (int i = 1; i < Count; i++)
                {
                    float value = channel[(start + i) % channel.Length];
                    // A crossing only counts when both sides are off the dead band;
                    // noise around zero is not a change of mind.
                    if (Mathf.Abs(value) > 0.05f && Mathf.Abs(previous) > 0.05f &&
                        Mathf.Sign(value) != Mathf.Sign(previous))
                    {
                        flips++;
                    }
                    previous = value;
                }
                return flips * 100f / Count;
            }
        }

        /// <summary>The UI elements of one player's row, held so Update never queries the tree.</summary>
        sealed class Row
        {
            public Agent_Soccer Agent;
            public VisualElement Root;
            public Label Name;
            public Label Figures;
            public VisualElement[] Fills = new VisualElement[4];
        }

        Agent_EnvController _env;
        Agent_HUD _hud;
        UIDocument _doc;
        VisualElement _panel;
        Label _heading;

        readonly Dictionary<Agent_Soccer, Readout> _readouts = new();
        readonly List<Row> _rows = new();
        bool _visible;
        float _nextRefresh;

        /// <summary>Whether the panel is on screen.</summary>
        public bool IsVisible => _visible;

        /// <summary>Rows currently rendered. Read by the tests.</summary>
        public int RowCount => _rows.Count;

        /// <summary>Rolling commitment (mean |forward|) for one player, or 0 before its first decision.</summary>
        public float CommitmentOf(Agent_Soccer agent) =>
            agent != null && _readouts.TryGetValue(agent, out var readout) ? readout.Commitment() : 0f;

        /// <summary>Forward-channel sign flips per 100 decisions, the churn figure.</summary>
        public float ChurnOf(Agent_Soccer agent) =>
            agent != null && _readouts.TryGetValue(agent, out var readout)
                ? readout.FlipsPer100(readout.Move) : 0f;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableBrainCam || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _window = Mathf.Max(4, _window);
            if (_visibleOnStart) SetVisible(true);
        }

        void OnDestroy()
        {
            if (_panel != null) _panel.RemoveFromHierarchy();
            if (_doc != null) Destroy(_doc.gameObject);
        }

        public void SetVisible(bool visible)
        {
            if (visible) Build();
            _visible = visible;
            if (_panel != null) _panel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.bKey.wasPressedThisFrame) SetVisible(!_visible);

            if (!_visible || _env == null) return;

            // Retry a build that bailed on a not-yet-ready document. Cheap: the
            // first line of Build returns once the panel exists.
            if (_panel == null) Build();

            SyncRows();

            bool refresh = Time.unscaledTime >= _nextRefresh;
            if (refresh) _nextRefresh = Time.unscaledTime + _refreshInterval;

            for (int i = 0; i < _rows.Count; i++) UpdateRow(_rows[i], refresh);
        }

        /// <summary>
        /// Decision sampling runs on the physics clock even though the panel draws
        /// on frames, because the thing being counted is decisions - and those are
        /// requested on the physics tick. Sampling in Update would miss decisions
        /// entirely on a slow frame and double-count nothing on a fast one.
        /// </summary>
        void FixedUpdate()
        {
            if (_env == null) return;
            if (Agent_TimeFreeze.IsFrozen) return;

            var roster = _env.agents;
            for (int i = 0; i < roster.Count; i++)
            {
                var agent = roster[i];
                if (agent == null || !agent.isActiveAndEnabled) continue;

                if (!_readouts.TryGetValue(agent, out var readout))
                {
                    readout = new Readout(_window);
                    _readouts[agent] = readout;
                }

                Vector4 action = agent.LastRawActions;
                // A new DECISION, not a new tick: ML-Agents repeats the last action
                // between decisions, and counting those repeats would divide every
                // churn figure by the decision period.
                if (readout.Seeded && action == readout.Last) continue;

                readout.Seeded = true;
                readout.Last = action;
                readout.Push(action);
            }
        }

        // -- Panel -----------------------------------------------------------

        /// <summary>
        /// ITS OWN GameObject, never the pitch root's.
        ///
        /// A UIDocument is taken over wholesale by whoever grabs it - sortingOrder
        /// and root both get rewritten - which is precisely the trap CLAUDE.md
        /// records against Agent_Chrome: landing chrome on the HUD would have
        /// replaced the scoreboard with it. The pitch root already carries a dozen
        /// installed components and there is no guarantee none of them ever wants a
        /// document of its own, so this takes a child object instead of betting on
        /// that.
        /// </summary>
        void Build()
        {
            if (_panel != null) return;
            if (_hud == null) return;

            var hudDoc = _hud.GetComponent<UIDocument>();
            if (hudDoc == null || hudDoc.panelSettings == null) return;

            if (_doc == null)
            {
                var host = new GameObject("BrainCamPanel");
                host.transform.SetParent(transform, false);

                _doc = host.AddComponent<UIDocument>();
                // The shared PanelSettings, never a fresh one: UNITY_RULES pins a
                // single ScaleWithScreenSize 1080x1920 panel, and a second settings
                // asset would scale this panel differently from the scoreboard it
                // sits next to.
                _doc.panelSettings = hudDoc.panelSettings;
                _doc.sortingOrder = _sortingOrder;
            }

            // Null on the frame the document is created on some Unity versions.
            // Bailing out here leaves _doc in place, so the next SetVisible retries
            // against the same object rather than spawning another host.
            var root = _doc.rootVisualElement;
            if (root == null) return;
            Agent_UIStyle.ApplyTheme(root);

            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.left = 0;
            _panel.style.right = 0;
            _panel.style.bottom = 0;
            _panel.style.backgroundColor = Agent_UIStyle.PanelBg;
            Agent_UIStyle.PadAll(_panel, Agent_UIStyle.Pad);
            root.Add(_panel);

            _heading = new Label();
            _heading.style.fontSize = Agent_UIStyle.FontXS;
            _heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            _heading.style.color = Agent_UIStyle.TextMuted;
            _heading.text = "BRAIN CAM  ·  RAW POLICY OUTPUT  ·  " +
                            $"ActionGain {Agent_Soccer.ActionGain:0.0}";
            _panel.Add(_heading);
        }

        /// <summary>
        /// Rebuild rows when the roster changes. Read from the live list rather than
        /// snapshotted in Start for the reason Agent_MatchStats gives for the same
        /// choice: the roster is populated by a component at a different execution
        /// order, and the gallery changes it after kickoff.
        /// </summary>
        void SyncRows()
        {
            if (_panel == null) return;

            var roster = _env.agents;
            bool stale = _rows.Count != roster.Count;
            for (int i = 0; !stale && i < _rows.Count; i++)
            {
                if (_rows[i].Agent != roster[i]) stale = true;
            }
            if (!stale) return;

            for (int i = 0; i < _rows.Count; i++) _rows[i].Root.RemoveFromHierarchy();
            _rows.Clear();

            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i] == null) continue;
                var row = BuildRow(roster[i]);
                _rows.Add(row);
                _panel.Add(row.Root);
            }
        }

        Row BuildRow(Agent_Soccer agent)
        {
            var row = new Row { Agent = agent };

            row.Root = new VisualElement();
            row.Root.style.marginTop = 10;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;

            row.Name = new Label(NameOf(agent));
            row.Name.style.fontSize = Agent_UIStyle.FontXS;
            row.Name.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Name.style.color = Agent_SoccerView.TeamColor(agent.team);
            header.Add(row.Name);

            row.Figures = new Label();
            row.Figures.style.fontSize = Agent_UIStyle.FontXS;
            row.Figures.style.color = Agent_UIStyle.TextMuted;
            header.Add(row.Figures);

            row.Root.Add(header);

            var bars = new VisualElement();
            bars.style.flexDirection = FlexDirection.Row;
            bars.style.marginTop = 4;
            for (int channel = 0; channel < 4; channel++)
            {
                bars.Add(BuildChannel(row, channel));
            }
            row.Root.Add(bars);

            return row;
        }

        /// <summary>
        /// One channel: a label and a centred bar that fills left or right from the
        /// middle, because three of the four actions are signed and a bar that only
        /// grew rightward would render -1 and +1 identically.
        /// </summary>
        VisualElement BuildChannel(Row row, int channel)
        {
            var column = new VisualElement();
            column.style.flexGrow = 1;
            column.style.marginRight = channel < 3 ? 8 : 0;

            var label = new Label(ChannelNames[channel]);
            label.style.fontSize = Agent_UIStyle.FontXS;
            label.style.color = Agent_UIStyle.TextMuted;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            column.Add(label);

            var track = new VisualElement();
            track.style.height = 14;
            track.style.backgroundColor = new Color(1f, 1f, 1f, 0.12f);
            track.style.overflow = Overflow.Hidden;
            Agent_UIStyle.Round(track, 6);

            var fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.top = 0;
            fill.style.bottom = 0;
            fill.style.left = Length.Percent(50f);
            fill.style.width = Length.Percent(0f);
            fill.style.backgroundColor = Agent_SoccerView.TeamColor(row.Agent.team);
            track.Add(fill);
            row.Fills[channel] = fill;

            column.Add(track);
            return column;
        }

        void UpdateRow(Row row, bool refreshText)
        {
            var agent = row.Agent;
            if (agent == null) return;

            Vector4 action = agent.LastRawActions;
            SetFill(row.Fills[0], action.x, signed: true);
            SetFill(row.Fills[1], action.y, signed: true);
            SetFill(row.Fills[2], action.z, signed: true);
            SetFill(row.Fills[3], action.w, signed: false);

            if (!refreshText) return;

            _readouts.TryGetValue(agent, out var readout);
            float commitment = readout != null ? readout.Commitment() : 0f;
            float moveChurn = readout != null ? readout.FlipsPer100(readout.Move) : 0f;
            float turnChurn = readout != null ? readout.FlipsPer100(readout.Turn) : 0f;

            // One string per row per refresh at ~8 Hz. Deliberately not per frame -
            // see the _refreshInterval tooltip.
            row.Figures.text = $"{(agent.RuleBased ? "BOT" : "AI")}  ·  commit {commitment:0.00}" +
                               $"  ·  churn {moveChurn:0}/{turnChurn:0} per 100";
        }

        /// <summary>
        /// Width and offset for one bar. Signed channels grow out from the centre;
        /// boost is 0..1 and grows rightward from it, which is what
        /// Agent_Soccer.OnActionReceived does to it (Clamp01, not Clamp).
        /// </summary>
        static void SetFill(VisualElement fill, float value, bool signed)
        {
            if (fill == null) return;

            float clamped = signed ? Mathf.Clamp(value, -1f, 1f) : Mathf.Clamp01(value);
            float half = Mathf.Abs(clamped) * 50f;

            fill.style.width = Length.Percent(half);
            fill.style.left = Length.Percent(clamped < 0f ? 50f - half : 50f);
        }

        static string NameOf(Agent_Soccer agent)
        {
            if (agent == null) return "-";
            return agent.rewards != null ? agent.rewards.playerName : agent.brainName;
        }
    }
}
