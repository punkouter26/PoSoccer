using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Turns the frame budget from a REPORT into a CONTROL LOOP.
    ///
    /// Agent_Telemetry already knows when the game is over budget: it grades p95
    /// frame time against 16.7 ms, colours the breach red and counts it into a
    /// CSV. Nothing acted on any of that. On the device that actually needs it -
    /// a phone, where the overlay is a three-finger tap away and nobody is
    /// looking - the measurement existed and the response did not.
    ///
    /// WHY IT MEASURES ITS OWN FRAME TIME instead of reading the overlay's. The
    /// overlay allocates its ProfilerRecorders on show and disposes them on hide,
    /// and it is hidden by default; a controller that only works while a debug
    /// panel is open is a controller that never works. Frame time from
    /// unscaledDeltaTime needs no recorder, exists on every platform, and is the
    /// number the player actually feels - the same argument the overlay itself
    /// makes for grading on it.
    ///
    /// WHAT IT SHEDS, IN ORDER, AND WHY THAT ORDER. Cheapest thing to lose
    /// first, measured by cost-per-pixel rather than by taste:
    ///
    ///   1. PITCH WEAR. One full-pitch transparent quad of CONTINUOUS overdraw,
    ///      plus a texture upload a few times a second. Paid every frame for
    ///      something nobody watches directly.
    ///   2. BLOOM. A multi-pass post effect, also paid every frame.
    ///   3. IMPACT OVERLAYS. The goal flash and shockwave - LAST, because they
    ///      cost half a second at the one moment the whole match builds towards.
    ///      They were first until a probe caught a real goal being celebrated
    ///      with nothing at all on a machine that had quietly walked to the
    ///      bottom tier. Continuous cost is spent before momentary meaning.
    ///
    /// It is deliberately asymmetric and slow: three consecutive bad windows to
    /// step down, twelve consecutive good ones to step back up. A controller that
    /// recovers as eagerly as it degrades oscillates, and an oscillating quality
    /// setting is more distracting than the dropped frames it is chasing.
    /// </summary>
    [DefaultExecutionOrder(80)]
    public sealed class Agent_Quality : MonoBehaviour
    {
        [Tooltip("Fallback frame budget (ms) when the app sets no target frame rate. " +
                 "16.7 = 60 fps.")]
        [SerializeField] private float _budgetMs = 16.7f;
        [Tooltip("How far past the budget a window must sit before it counts as a breach. " +
                 "1.25 = 20.9 ms against a 60 fps target.")]
        [Range(1f, 2f)] [SerializeField] private float _breachMargin = 1.25f;
        [Tooltip("Frames per judged window. At 60 fps, 90 frames is ~1.5 s.")]
        [SerializeField] private int _window = 90;
        [Tooltip("Consecutive over-budget windows before dropping a tier.")]
        [SerializeField] private int _windowsToDrop = 3;
        [Tooltip("Consecutive comfortable windows before recovering a tier.")]
        [SerializeField] private int _windowsToRecover = 12;
        [Tooltip("Headroom required to recover: p95 must be under budget * this.")]
        [Range(0.5f, 1f)] [SerializeField] private float _recoverMargin = 0.8f;
        [Tooltip("Off = the tier never moves. The measurement still runs, so the " +
                 "telemetry line stays honest about what the frame time is doing.")]
        [SerializeField] private bool _adaptive = true;

        float[] _samples;
        float _effectiveBudgetMs = 16.7f;
        int _count;
        int _badWindows, _goodWindows;

        Agent_ScreenFX _screenFX;
        Agent_Wear _wear;
        Agent_Stadium _stadium;

        /// <summary>
        /// 0 = everything on, 3 = everything sheddable shed. Static so the
        /// telemetry overlay can print it without holding a reference - there is
        /// exactly one of these, on the match pitch.
        /// </summary>
        public static int Tier { get; private set; }

        /// <summary>Windows judged over budget since load. Printed by the overlay.</summary>
        public static int Drops { get; private set; }

        /// <summary>Most recent judged p95, in ms. -1 before the first window closes.</summary>
        public static float LastP95 { get; private set; } = -1f;

        void Start()
        {
            var hud = FindFirstObjectByType<Agent_HUD>();
            if (!Agent_Presentation.IsMatchScene(hud))
            {
                enabled = false;
                return;
            }

            _samples = new float[Mathf.Max(30, _window)];

            // The budget is whatever the app ASKED FOR, not a constant. Both the
            // menu and Agent_Bootstrap set Application.targetFrameRate, and a
            // controller grading a 30 fps target against a 60 fps budget would
            // shed every effect on a device that is performing exactly as
            // intended.
            _effectiveBudgetMs = Application.targetFrameRate > 0
                ? 1000f / Application.targetFrameRate
                : _budgetMs;
            _screenFX = FindFirstObjectByType<Agent_ScreenFX>();
            _wear = FindFirstObjectByType<Agent_Wear>();
            _stadium = Agent_Stadium.Instance;

            // A scene entered at a degraded tier from a previous match would be
            // dishonest about why it looks the way it does.
            Tier = 0;
            Drops = 0;
            LastP95 = -1f;
            Apply();
        }

        void Update()
        {
            // unscaledDeltaTime: the goal replay and the hit-stop both scale the
            // clock, and a controller reading scaled time would see a hit-stop as
            // a stutter and degrade the game for a deliberate effect.
            float ms = Time.unscaledDeltaTime * 1000f;
            _samples[_count++] = ms;
            if (_count < _samples.Length) return;

            Judge();
            _count = 0;
        }

        void Judge()
        {
            float p95 = Percentile95();
            LastP95 = p95;

            // A breach has to CLEAR the margin. Ordinary jitter around the budget
            // is not trouble, and treating it as trouble is how the editor - which
            // never holds 16.7 ms - reached the bottom tier inside a minute and
            // celebrated a real goal with nothing at all. Measured, then fixed.
            if (p95 > _effectiveBudgetMs * _breachMargin)
            {
                Drops++;
                _goodWindows = 0;
                _badWindows++;
            }
            else if (p95 < _effectiveBudgetMs * _recoverMargin)
            {
                _badWindows = 0;
                _goodWindows++;
            }
            else
            {
                // In the dead band: neither a breach nor headroom. Hold, and let
                // neither counter run - this is the state a well-tuned tier sits
                // in, and treating it as "good" would ratchet the game back up
                // into the breach it just escaped.
                _badWindows = 0;
                _goodWindows = 0;
            }

            if (!_adaptive) return;

            if (_badWindows >= _windowsToDrop && Tier < 3)
            {
                _badWindows = 0;
                Tier++;
                Apply();
            }
            else if (_goodWindows >= _windowsToRecover && Tier > 0)
            {
                _goodWindows = 0;
                Tier--;
                Apply();
            }
        }

        /// <summary>
        /// p95 by selection over a copy-free partial sort.
        ///
        /// Insertion sort on a 90-element array of floats, once every 90 frames.
        /// That is ~2000 comparisons a second in the worst case against a budget
        /// of 60 frames each costing 16 ms - immaterial, and it avoids the
        /// allocation that System.Array.Sort on a subrange would still need for
        /// a comparer. Sorting in place is fine: the window is finished.
        /// </summary>
        float Percentile95()
        {
            int n = _samples.Length;
            for (int i = 1; i < n; i++)
            {
                float value = _samples[i];
                int j = i - 1;
                while (j >= 0 && _samples[j] > value)
                {
                    _samples[j + 1] = _samples[j];
                    j--;
                }
                _samples[j + 1] = value;
            }
            int index = Mathf.Clamp(Mathf.CeilToInt(0.95f * n) - 1, 0, n - 1);
            return _samples[index];
        }

        void Apply()
        {
            // ORDER CORRECTED 2026-09-06 AFTER WATCHING IT RUN, and the correction
            // is the whole reason to have watched. The goal flash and shockwave
            // used to be shed FIRST. A probe logged during a live match read
            // `winner=Red allow=False tier=3` - a real goal, celebrated with
            // nothing, because the editor never holds 16.7 ms and the controller
            // had walked to the bottom tier inside a minute.
            //
            // Continuous cost is spent before momentary meaning: the wear quad is
            // paid every frame, bloom is paid every frame, and the goal overlays
            // are paid for half a second at the one moment the whole match is
            // building towards. So they go last.
            if (_wear != null) _wear.Visible = Tier < 1;
            if (_stadium != null) _stadium.SetBloomEnabled(Tier < 2);
            if (_screenFX != null) _screenFX.AllowContinuous = Tier < 3;
        }

        /// <summary>
        /// Forces a tier. For tests and for a settings screen; the adaptive loop
        /// keeps running afterwards and may move it again.
        /// </summary>
        public void SetTier(int tier)
        {
            Tier = Mathf.Clamp(tier, 0, 3);
            Apply();
        }

        /// <summary>One line for the telemetry overlay.</summary>
        public static string StatusLine()
        {
            return LastP95 < 0f
                ? $"quality tier {Tier}   p95 (pending)"
                : $"quality tier {Tier}   p95 {LastP95:0.0} ms   drops {Drops}";
        }
    }
}
