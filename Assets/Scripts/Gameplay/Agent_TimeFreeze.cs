using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Reference-counted owner of <see cref="Time.timeScale"/>.
    ///
    /// Four separate systems now want to stop the clock - the goal replay, the
    /// kickoff countdown, the halftime break and the end-of-match panel - and a
    /// goal that wins the match triggers three of them in the same frame. With
    /// each one writing timeScale directly, whichever finished first would
    /// un-freeze the game underneath the others: the end panel would appear over
    /// a pitch that had quietly resumed playing.
    ///
    /// Holders are tracked by identity, so the clock only runs again once the
    /// last holder releases. Acquire/Release are idempotent per holder.
    ///
    /// THREE INPUTS, ONE WRITER. Freeze holders, the hit-stop dip
    /// (<see cref="SlowMotion"/>) and the spectator's playback rate
    /// (<see cref="SpectatorSpeed"/>) all resolve in <c>Apply</c>, which is the
    /// only place in the match-facing code that assigns Time.timeScale. The two
    /// remaining direct writers are Agent_EvalStats (headless evaluation) and
    /// Agent_DemoClock (demonstration recording); both run only in SCN_Training,
    /// where nothing calls into this class, and Agent_DemoClock's docstring
    /// records why it was left alone.
    /// </summary>
    public static class Agent_TimeFreeze
    {
        static readonly HashSet<object> Holders = new();

        public static bool IsFrozen => Holders.Count > 0;

        public static int HolderCount => Holders.Count;

        /// <summary>
        /// Names every current holder. A leaked hold presents to the player as a
        /// hung game with nothing in the console, so the diagnostic has to name
        /// the culprit rather than just report that the clock is stopped.
        /// Used by Agent_Telemetry and by the flow tests' failure messages.
        /// </summary>
        public static string DescribeHolders()
        {
            if (Holders.Count == 0) return "none";
            var builder = new System.Text.StringBuilder();
            foreach (var holder in Holders)
            {
                if (builder.Length > 0) builder.Append(", ");
                builder.Append(holder is Object unityObject && unityObject != null
                    ? $"{holder.GetType().Name}({unityObject.name})"
                    : holder.ToString());
            }
            return builder.ToString();
        }

        /// <summary>
        /// A named hold. Use this instead of a bare <c>new object()</c>.
        ///
        /// The phase tokens in Agent_MatchFlow were plain objects, so
        /// <see cref="DescribeHolders"/> printed the type name - "Object" - for
        /// every one of them. That is exactly the case this class's diagnostic
        /// exists for and exactly where it said nothing: a test failure on
        /// 2026-09-06 read "Held by: Object", which is true of the countdown, the
        /// goal sequence, halftime, golden goal and full time alike. Naming the
        /// token turns a hung game into a one-line diagnosis.
        /// </summary>
        public sealed class Hold
        {
            readonly string _name;

            public Hold(string name) => _name = name;

            public override string ToString() => _name;
        }

        /// <summary>
        /// Sub-unity playback rate applied only while nothing holds a full
        /// freeze. This is the hit-stop channel (see <see cref="Agent_Hitstop"/>),
        /// and it lives here rather than in the effect that uses it for exactly
        /// the reason this class exists at all: two systems writing
        /// <see cref="Time.timeScale"/> directly is how the clock ends up owned
        /// by whichever one finished last.
        ///
        /// A full freeze always wins - a 0.35x hit-stop must never un-pause a
        /// game sitting on the end panel.
        /// </summary>
        public static float SlowMotion
        {
            get => _slowMotion;
            set
            {
                _slowMotion = Mathf.Clamp(value, 0.01f, 1f);
                Apply();
            }
        }

        static float _slowMotion = 1f;

        /// <summary>Slowest playback a spectator may pick.</summary>
        public const float MIN_SPECTATOR_SPEED = 0.25f;

        /// <summary>Fastest playback a spectator may pick.</summary>
        public const float MAX_SPECTATOR_SPEED = 4f;

        /// <summary>
        /// The spectator's playback rate, 0.25x .. 4x, default 1.
        ///
        /// A THIRD CHANNEL, COMPOSED RATHER THAN COMPETING. The clock already had
        /// two inputs - the freeze holders and the hit-stop dip - and the reason
        /// they live in one class is that a second writer of
        /// <see cref="Time.timeScale"/> is how the clock ends up owned by
        /// whichever one finished last. A speed control written straight to
        /// timeScale would be exactly that writer: the first hit-stop to end
        /// would "restore" 1x over a viewer's 2x, and the first freeze to release
        /// would do the same. So it multiplies instead:
        ///
        ///   frozen               -> 0
        ///   otherwise            -> SlowMotion * SpectatorSpeed
        ///
        /// A freeze always wins, so 4x can never un-pause an end panel, and a
        /// hit-stop still reads as a dip at any speed because it scales the rate
        /// the viewer chose rather than replacing it.
        ///
        /// Reset to 1 by <see cref="ReleaseAll"/>, i.e. on every scene entry (the
        /// menu, a match start, a rematch). It is a static and statics survive a
        /// scene load in a player build, so without that a viewer who left a match
        /// at 0.25x would find the next one in slow motion with no control on
        /// screen yet to explain why.
        ///
        /// NEVER SET FROM TRAINING OR EVAL. Nothing there calls it - the only
        /// writers are the match-scene spectator controls - and Agent_EvalStats
        /// sets its own time scale directly, by design, in a process where this
        /// class is never asked to Apply.
        /// </summary>
        public static float SpectatorSpeed
        {
            get => _spectatorSpeed;
            set
            {
                // NaN compares false against everything and would sail through a
                // Clamp into Time.timeScale, which throws on it.
                if (float.IsNaN(value)) return;
                _spectatorSpeed = Mathf.Clamp(value, MIN_SPECTATOR_SPEED, MAX_SPECTATOR_SPEED);
                Apply();
            }
        }

        static float _spectatorSpeed = 1f;

        /// <summary>
        /// True when the game is running at ordinary speed: no freeze holder, no
        /// hit-stop and no spectator speed change. The flow tests assert on
        /// Time.timeScale directly, which this keeps meaningful.
        /// </summary>
        public static bool IsNormalSpeed =>
            Holders.Count == 0 && _slowMotion >= 0.999f && Mathf.Abs(_spectatorSpeed - 1f) < 0.001f;

        static void Apply()
        {
            Time.timeScale = Holders.Count > 0 ? 0f : _slowMotion * _spectatorSpeed;
        }

        public static void Acquire(object holder)
        {
            if (holder == null || !Holders.Add(holder)) return;
            Apply();
        }

        public static void Release(object holder)
        {
            if (holder == null || !Holders.Remove(holder)) return;
            if (Holders.Count == 0) Apply();
        }

        /// <summary>
        /// Drops every holder and resumes the clock. Statics survive a scene load
        /// in a player build (no domain reload), so a REMATCH taken from a frozen
        /// end panel would otherwise load the new scene with a stale holder still
        /// registered and the game paused. Scene entry points call this.
        ///
        /// Clears the hit-stop too: a scene load in the middle of a dip would
        /// otherwise start the next match at 0.35x with nothing left running to
        /// restore it.
        ///
        /// Clears the spectator speed for the same reason - see
        /// <see cref="SpectatorSpeed"/>. Goes through <see cref="Apply"/> rather
        /// than writing 1 directly so there is exactly one line in the project's
        /// match-facing code that assigns Time.timeScale.
        /// </summary>
        public static void ReleaseAll()
        {
            Holders.Clear();
            _slowMotion = 1f;
            _spectatorSpeed = 1f;
            Apply();
        }
    }
}
