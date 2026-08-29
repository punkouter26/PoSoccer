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

        readonly struct Stat
        {
            public readonly string Label;
            public readonly ProfilerCategory Category;
            public readonly string Name;
            public readonly float Scale;
            public readonly string Unit;

            public Stat(string label, ProfilerCategory category, string name, float scale, string unit)
            {
                Label = label;
                Category = category;
                Name = name;
                Scale = scale;
                Unit = unit;
            }
        }

        static readonly Stat[] Wanted =
        {
            new("Draw calls", ProfilerCategory.Render, "Draw Calls Count", 1f, ""),
            new("SetPass", ProfilerCategory.Render, "SetPass Calls Count", 1f, ""),
            new("Batches", ProfilerCategory.Render, "Batches Count", 1f, ""),
            new("Triangles", ProfilerCategory.Render, "Triangles Count", 1f, ""),
            new("Verts", ProfilerCategory.Render, "Vertices Count", 1f, ""),
            new("GC alloc/frame", ProfilerCategory.Memory, "GC Allocated In Frame", 1f / 1024f, " KB"),
            new("GC reserved", ProfilerCategory.Memory, "GC Reserved Memory", 1f / (1024f * 1024f), " MB"),
            new("System used", ProfilerCategory.Memory, "System Used Memory", 1f / (1024f * 1024f), " MB"),
            new("Audio voices", ProfilerCategory.Audio, "Playing Audio Sources", 1f, ""),
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
            _visible = true;
            _text.style.display = DisplayStyle.Flex;
        }

        void Hide()
        {
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

            _builder.Clear();
            _builder.Append("── PoSoccer telemetry (F3) ──\n");
            _builder.Append("fps ").Append((mean > 0.001f ? 1000f / mean : 0f).ToString("0"))
                    .Append("   frame ").Append(mean.ToString("0.0")).Append(" ms")
                    .Append("   p95 ").Append(p95.ToString("0.0"))
                    .Append("   max ").Append(worst.ToString("0.0")).Append('\n');

            for (int i = 0; i < _live.Count; i++)
            {
                double value = _recorders[i].LastValue * _live[i].Scale;
                _builder.Append(_live[i].Label).Append(' ')
                        .Append(value.ToString(_live[i].Scale < 1f ? "0.00" : "0"))
                        .Append(_live[i].Unit).Append('\n');
            }

            // Project state - the counters that would have made earlier bugs obvious.
            _builder.Append("timeScale ").Append(Time.timeScale.ToString("0.00"))
                    .Append("   frozen ").Append(Agent_TimeFreeze.IsFrozen ? "YES" : "no").Append('\n');
            if (_env != null)
            {
                _builder.Append("episode step ").Append(_env.StepCount)
                        .Append(" / ").Append(_env.MaxEnvironmentSteps)
                        .Append("   agents ").Append(_env.agents.Count).Append('\n');
                _builder.Append("goal ").Append(_env.CurrentGoalWidth.ToString("0.0"))
                        .Append("m   bot ").Append(_env.CurrentBotStrength.ToString("0.00"));
            }

            _text.text = _builder.ToString();
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
