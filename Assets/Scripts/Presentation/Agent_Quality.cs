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
    ///   1. IMPACT OVERLAYS. A full-view transparent quad and an expanding ring
    ///      on every goal. Brief, but pure overdraw over the whole screen, and
    ///      the least load-bearing thing drawn.
    ///   2. PITCH WEAR. One full-pitch transparent quad of pure overdraw, plus a
    ///      texture upload a few times a second.
    ///   3. BLOOM. A multi-pass post effect. Kept for last of the three because
    ///      losing it changes the LOOK of the game rather than a layer on top of
    ///      it, and a dim game reads as broken where a sharp one does not.
    ///
    /// It is deliberately asymmetric and slow: three consecutive bad windows to
    /// step down, twelve consecutive good ones to step back up. A controller that
    /// recovers as eagerly as it degrades oscillates, and an oscillating quality
    /// setting is more distracting than the dropped frames it is chasing.
    /// </summary>
    [DefaultExecutionOrder(80)]
    public sealed class Agent_Quality : MonoBehaviour
    {
        [Tooltip("Frame time (ms) the p95 must stay under. 16.7 = 60 fps.")]
        [SerializeField] private float _budgetMs = 16.7f;
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

            if (p95 > _budgetMs)
            {
                Drops++;
                _goodWindows = 0;
                _badWindows++;
            }
            else if (p95 < _budgetMs * _recoverMargin)
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
            if (_screenFX != null) _screenFX.AllowContinuous = Tier < 1;
            if (_wear != null) _wear.Visible = Tier < 2;
            if (_stadium != null) _stadium.SetBloomEnabled(Tier < 3);
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
