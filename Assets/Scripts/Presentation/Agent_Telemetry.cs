using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Real-time diagnostic overlay: frame timing with percentiles, GC pressure,
    /// draw-call and geometry counts, memory, and PoSoccer-specific state.
    ///
    /// Built on ProfilerRecorder, which was installed in this project (Profiling
    /// Core, Memory Profiler, Profile Analyzer) and used exactly nowhere. Note
    /// that profiler stat NAMES drift between Unity versions, so every recorder
    /// is probed for Valid and silently dropped if this Unity does not publish it
    /// - the overlay degrades to fewer rows rather than throwing or, worse,
    /// printing zeroes that look like real measurements.
    ///
    /// Frame timing is measured directly from unscaledDeltaTime rather than from
    /// a recorder, because that is the number a player actually experiences and
    /// it is guaranteed to exist on every platform.
    ///
    /// COST WHEN HIDDEN IS ZERO: recorders are allocated on show and disposed on
    /// hide, and Update returns immediately while closed. Toggle with F3, or a
    /// three-finger tap on touch devices.
    ///
    /// 2026-09-05 - IT NOW ENFORCES BUDGETS AND WRITES A SESSION LOG. Reporting
    /// numbers is not the same as holding a line: every row that has a defensible
    /// budget carries one, breaches render in red and are counted, and a HOLD/OVER
    /// verdict sits at the top so the answer is legible without reading the table.
    ///
    /// The CSV matters more than the colours. This project's whole methodology is
    /// that a measurement nobody can re-read later becomes a claim nobody can
    /// falsify - the phase-10 retraction happened exactly that way. An overlay you
    /// can only read by squinting at a phone in your hand is that same trap in
    /// visual form, so every refresh is also a row, and the file is written to
    /// Application.persistentDataPath on hide and on quit. That is what makes an
    /// on-DEVICE session gradeable off-device, which is the only honest way to
    /// prove the sprite-atlas work actually paid off on the hardware that needed
    /// it rather than on a desktop that never had the problem.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class Agent_Telemetry : MonoBehaviour
    {
        [Tooltip("Show the overlay from the moment the scene loads.")]
        [SerializeField] private bool _visibleOnStart;
        [Tooltip("Frames retained for the percentile window.")]
        [SerializeField] private int _sampleWindow = 240;
        [Tooltip("Seconds between text refreshes. The samples are still collected every frame.")]
        [SerializeField] private float _refreshInterval = 0.25f;

        [Tooltip("Frame budget in milliseconds. 16.7 = 60 fps; raise to 33.3 to grade against 30 fps.")]
        [SerializeField] private float _frameBudgetMs = 16.7f;
        [Tooltip("Write a CSV of every refresh to persistentDataPath when the overlay is hidden or the app quits.")]
        [SerializeField] private bool _writeSessionCsv = true;
        [Tooltip("Maximum rows retained for the CSV. At 4 Hz this is about 40 minutes.")]
        [SerializeField] private int _maxCsvRows = 10000;

        readonly struct Stat
        {
            public readonly string Label;
            public readonly ProfilerCategory Category;
            public readonly string Name;
            public readonly float Scale;
            public readonly string Unit;
            /// <summary>Value above which this row is over budget. 0 = no budget.</summary>
            public readonly double Budget;

            public Stat(string label, ProfilerCategory category, string name, float scale,
                string unit, double budget = 0d)
            {
                Label = label;
                Category = category;
                Name = name;
                Scale = scale;
                Unit = unit;
                Budget = budget;
            }
        }

        // Budgets, and where each one comes from - a budget nobody can justify is
        // a number that gets raised the first time it goes red.
        //
        //   Draw calls / SetPass / Batches - .claude/rules/performance.md asks for
        //     the lowest count possible on a 2D mobile game. This scene draws a
        //     pitch, a backdrop, four players, a ball, two goals, the crowd
        //     tilemap, the ad boards and the runtime shapes. Post-atlas that is
        //     tens, not hundreds; 120 is deliberately loose enough that tripping
        //     it means something regressed rather than that the budget was tight.
        //   GC alloc/frame - the rule is ZERO allocation in Update/FixedUpdate.
        //     1 KB is the smallest threshold that does not fire on UI Toolkit's
        //     own per-frame churn, so anything above it is ours.
        //   System used - a 6-year-old midrange Android gives a game ~512 MB
        //     before the low-memory killer takes an interest.
        static readonly Stat[] Wanted =
        {
            new("Draw calls", ProfilerCategory.Render, "Draw Calls Count", 1f, "", 120),
            new("SetPass", ProfilerCategory.Render, "SetPass Calls Count", 1f, "", 60),
            new("Batches", ProfilerCategory.Render, "Batches Count", 1f, "", 120),
            new("Triangles", ProfilerCategory.Render, "Triangles Count", 1f, ""),
            new("Verts", ProfilerCategory.Render, "Vertices Count", 1f, ""),
            new("GC alloc/frame", ProfilerCategory.Memory, "GC Allocated In Frame", 1f / 1024f, " KB", 1.0),
            new("GC reserved", ProfilerCategory.Memory, "GC Reserved Memory", 1f / (1024f * 1024f), " MB"),
            new("System used", ProfilerCategory.Memory, "System Used Memory", 1f / (1024f * 1024f), " MB", 512),
            new("Audio voices", ProfilerCategory.Audio, "Playing Audio Sources", 1f, "", 24),
        };

        readonly List<ProfilerRecorder> _recorders = new();
        readonly List<Stat> _live = new();
        float[] _samples;
        int _sampleCount;
        int _sampleHead;
        float _nextRefresh;

        UIDocument _doc;
        Label _text;
        bool _visible;
        System.Text.StringBuilder _builder;

        Agent_EnvController _env;
        int _threeFingerLatch;

        System.Text.StringBuilder _csv;
        int _csvRows;
        int _breachCount;
        float _worstFrameMs;

        /// <summary>Shows or hides the overlay. Profiler recorders exist only while shown.</summary>
        /// <summary>Whether the overlay is currently on screen. Read by
        /// Agent_Chrome's DEBUG button, which toggles rather than sets.</summary>
        public bool IsVisible => _visible;

        public void SetVisible(bool visible)
        {
            if (visible == _visible && (visible || _text != null)) return;
            if (visible) Show();
            else Hide();
        }

        void Start()
        {
            _env = FindFirstObjectByType<Agent_EnvController>();
            _samples = new float[Mathf.Max(30, _sampleWindow)];
            _builder = new System.Text.StringBuilder(512);
            if (_visibleOnStart) Show();
        }

        void OnDestroy() => Hide();

        /// <summary>
        /// Android does not reliably call OnDestroy when the process is killed, so
        /// the session log is flushed here as well. Flush() is idempotent - it
        /// clears the buffer - so being called twice writes one file, not two.
        /// </summary>
        void OnApplicationQuit() => FlushCsv();

        void OnApplicationPause(bool paused)
        {
            if (paused) FlushCsv();
        }

        void Update()
        {
            if (ToggleRequested()) { if (_visible) Hide(); else Show(); }
            if (!_visible) return;

            // Sampled every frame; the percentile window is meaningless otherwise.
            _samples[_sampleHead] = Time.unscaledDeltaTime * 1000f;
            _sampleHead = (_sampleHead + 1) % _samples.Length;
            if (_sampleCount < _samples.Length) _sampleCount++;

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + _refreshInterval;
            Render();
        }

        bool ToggleRequested()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f3Key.wasPressedThisFrame) return true;

            // Three fingers down is a gesture nothing in the game uses, so it
            // cannot be hit by accident during play.
            var touch = Touchscreen.current;
            if (touch == null) return false;
            int down = 0;
            for (int i = 0; i < touch.touches.Count; i++)
                if (touch.touches[i].press.isPressed) down++;

            bool triggered = down >= 3 && _threeFingerLatch < 3;
            _threeFingerLatch = down;
            return triggered;
        }

        // -- Visibility ------------------------------------------------------

        void Show()
        {
            BuildOverlay();
            if (_text == null) return;

            for (int i = 0; i < Wanted.Length; i++)
            {
                var recorder = ProfilerRecorder.StartNew(Wanted[i].Category, Wanted[i].Name);
                // Valid is only meaningful once started; an unpublished stat on
                // this Unity version simply drops out of the table.
                if (!recorder.Valid)
                {
                    recorder.Dispose();
                    continue;
                }
                _recorders.Add(recorder);
                _live.Add(Wanted[i]);
            }

            _sampleCount = 0;
            _sampleHead = 0;
            _breachCount = 0;
            _worstFrameMs = 0f;
            _visible = true;
            _text.style.display = DisplayStyle.Flex;

            BeginCsv();
        }

        void Hide()
        {
            FlushCsv();
            for (int i = 0; i < _recorders.Count; i++) _recorders[i].Dispose();
            _recorders.Clear();
            _live.Clear();
            _visible = false;
            if (_text != null) _text.style.display = DisplayStyle.None;
        }

        void BuildOverlay()
        {
            if (_text != null) return;

            _doc = gameObject.GetComponent<UIDocument>();
            if (_doc == null) _doc = gameObject.AddComponent<UIDocument>();

            if (_doc.panelSettings == null)
            {
                // Share whatever panel the HUD already uses so scaling matches.
                //
                // Must scan ALL documents, not FindFirstObjectByType: that can
                // return the document this method just added to its own
                // GameObject, whereupon the `!= _doc` guard rejected the only
                // candidate it ever looked at, panelSettings stayed null, and the
                // overlay silently built nothing at all.
                var documents = FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
                for (int i = 0; i < documents.Length; i++)
                {
                    if (documents[i] == _doc || documents[i].panelSettings == null) continue;
                    _doc.panelSettings = documents[i].panelSettings;
                    break;
                }
            }
            if (_doc.panelSettings == null)
            {
                Debug.LogWarning("Agent_Telemetry: no PanelSettings available; overlay disabled.");
                return;
            }
            _doc.sortingOrder = 100;

            var root = _doc.rootVisualElement;
            if (root == null) return;

            Agent_UIStyle.ApplyTheme(root);

            // Inside a safe-area container: the overlay is absolutely positioned
            // near the top of the screen, which on a notched phone is exactly
            // where the cutout is.
            var safe = new VisualElement();
            safe.style.flexGrow = 1;
            safe.pickingMode = PickingMode.Ignore;
            Agent_UIStyle.BindSafeArea(safe);
            root.Add(safe);

            _text = new Label();
            _text.AddToClassList("telemetry");
            _text.pickingMode = PickingMode.Ignore;
            _text.style.display = DisplayStyle.None;
            safe.Add(_text);
        }

        // -- Readout ---------------------------------------------------------

        void Render()
        {
            float mean = 0f, worst = 0f;
            for (int i = 0; i < _sampleCount; i++)
            {
                float ms = _samples[i];
                mean += ms;
                if (ms > worst) worst = ms;
            }
            mean = _sampleCount > 0 ? mean / _sampleCount : 0f;
            float p95 = Percentile(0.95f);
            if (worst > _worstFrameMs) _worstFrameMs = worst;

            // p95, not mean, is graded against the frame budget. A mean inside
            // budget with a p95 outside it is a game that stutters, and the mean
            // is exactly the statistic that hides it.
            bool frameOver = p95 > _frameBudgetMs;
            int breachesNow = frameOver ? 1 : 0;

            _builder.Clear();
            _builder.Append("── PoSoccer telemetry (F3) ──\n");
            _builder.Append("fps ").Append((mean > 0.001f ? 1000f / mean : 0f).ToString("0"))
                    .Append("   frame ").Append(mean.ToString("0.0")).Append(" ms   p95 ");
            AppendGraded(p95.ToString("0.0"), frameOver);
            _builder.Append(" / ").Append(_frameBudgetMs.ToString("0.0"))
                    .Append("   max ").Append(worst.ToString("0.0")).Append('\n');

            for (int i = 0; i < _live.Count; i++)
            {
                var stat = _live[i];
                double value = _recorders[i].LastValue * stat.Scale;
                bool over = stat.Budget > 0d && value > stat.Budget;
                if (over) breachesNow++;

                _builder.Append(stat.Label).Append(' ');
                AppendGraded(value.ToString(stat.Scale < 1f ? "0.00" : "0") + stat.Unit, over);
                if (stat.Budget > 0d)
                    _builder.Append(" / ").Append(stat.Budget.ToString(stat.Scale < 1f ? "0.00" : "0"));
                _builder.Append('\n');
            }

            if (breachesNow > 0) _breachCount++;

            // The verdict line, first thing a reader's eye lands on. A table of
            // numbers requires you to already know the budgets; this does not.
            _builder.Append(breachesNow > 0
                ? $"<color=#ff6b6b>OVER BUDGET x{breachesNow}</color>"
                : "<color=#6bff8f>ALL WITHIN BUDGET</color>");
            _builder.Append("   breached refreshes ").Append(_breachCount)
                    .Append("   worst frame ").Append(_worstFrameMs.ToString("0.0")).Append(" ms\n");

            // Project state - the counters that would have made earlier bugs obvious.
            //
            // The clock line distinguishes FROZEN (a full hold: replay, countdown,
            // end panel) from SLOW-MO (Agent_Hitstop's dip). Both show a
            // sub-1 timeScale and only one of them is a bug when it persists, so
            // collapsing them into one flag is how a leaked hit-stop would get
            // mistaken for a legitimate freeze.
            _builder.Append("timeScale ").Append(Time.timeScale.ToString("0.00"))
                    .Append("   clock ")
                    .Append(Agent_TimeFreeze.IsFrozen ? "FROZEN"
                          : Agent_TimeFreeze.IsNormalSpeed ? "running" : "SLOW-MO")
                    .Append('\n');
            if (_env != null)
            {
                _builder.Append("episode step ").Append(_env.StepCount)
                        .Append(" / ").Append(_env.MaxEnvironmentSteps)
                        .Append("   agents ").Append(_env.agents.Count).Append('\n');
                _builder.Append("goal ").Append(_env.CurrentGoalWidth.ToString("0.0"))
                        .Append("m   bot ").Append(_env.CurrentBotStrength.ToString("0.00")).Append('\n');
            }
            _builder.Append("atlas shapes ").Append(Agent_Art.SlotCount)
                    .Append("   csv rows ").Append(_csvRows);

            _text.text = _builder.ToString();

            AppendCsvRow(mean, p95, worst);
        }

        void AppendGraded(string text, bool over)
        {
            if (over) _builder.Append("<color=#ff6b6b>").Append(text).Append("</color>");
            else _builder.Append(text);
        }

        // -- Session log -------------------------------------------------------

        void BeginCsv()
        {
            if (!_writeSessionCsv) return;

            _csv = new System.Text.StringBuilder(64 * 1024);
            _csvRows = 0;
            _csv.Append("t,frame_ms,p95_ms,max_ms");
            for (int i = 0; i < _live.Count; i++)
                _csv.Append(',').Append(_live[i].Label.Replace(' ', '_').Replace('/', '_'));
            _csv.Append(",time_scale,frozen,episode_step\n");
        }

        void AppendCsvRow(float mean, float p95, float worst)
        {
            if (_csv == null || _csvRows >= _maxCsvRows) return;

            _csv.Append(Time.unscaledTime.ToString("0.00")).Append(',')
                .Append(mean.ToString("0.000")).Append(',')
                .Append(p95.ToString("0.000")).Append(',')
                .Append(worst.ToString("0.000"));

            for (int i = 0; i < _live.Count; i++)
            {
                double value = _recorders[i].LastValue * _live[i].Scale;
                _csv.Append(',').Append(value.ToString("0.###"));
            }

            _csv.Append(',').Append(Time.timeScale.ToString("0.00"))
                .Append(',').Append(Agent_TimeFreeze.IsFrozen ? 1 : 0)
                .Append(',').Append(_env != null ? _env.StepCount : 0)
                .Append('\n');

            _csvRows++;
        }

        /// <summary>
        /// Write and clear. Clearing is what makes this safe to call from Hide,
        /// OnDestroy, OnApplicationQuit and OnApplicationPause - all four of which
        /// can fire for one session, and only the first should produce a file.
        /// </summary>
        void FlushCsv()
        {
            if (_csv == null || _csvRows == 0) { _csv = null; return; }

            string path = System.IO.Path.Combine(
                Application.persistentDataPath,
                $"posoccer-telemetry-{System.DateTime.Now:yyyyMMdd-HHmmss}.csv");

            try
            {
                System.IO.File.WriteAllText(path, _csv.ToString());
                Debug.Log($"Agent_Telemetry: wrote {_csvRows} rows to {path}");
            }
            catch (System.Exception exception)
            {
                // A telemetry overlay must never be the thing that crashes the
                // game it is measuring.
                Debug.LogWarning($"Agent_Telemetry: could not write the session log: {exception.Message}");
            }

            _csv = null;
            _csvRows = 0;
        }

        /// <summary>
        /// Percentile over the frame-time window. Copies and sorts only the
        /// populated part, and only on refresh (4x a second), never per frame.
        /// </summary>
        float Percentile(float fraction)
        {
            if (_sampleCount == 0) return 0f;
            var sorted = new float[_sampleCount];
            System.Array.Copy(_samples, sorted, _sampleCount);
            System.Array.Sort(sorted);
            int index = Mathf.Clamp(Mathf.RoundToInt(fraction * (_sampleCount - 1)), 0, _sampleCount - 1);
            return sorted[index];
        }
    }
}
