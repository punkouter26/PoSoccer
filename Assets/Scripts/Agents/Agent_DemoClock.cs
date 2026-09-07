using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Holds <see cref="Time.timeScale"/> up for the duration of a demonstration
    /// recording run, by re-asserting it every frame.
    ///
    /// WHY RE-ASSERT RATHER THAN SET ONCE (measured 2026-09-07). A recording run has no
    /// trainer, so nothing applies the config's `time_scale: 20` - mlagents-learn
    /// normally sets that over the communicator. Setting it once from
    /// <see cref="Agent_Soccer"/>.Awake did not hold: the first recording attempt wrote
    /// ~12.5 demo entries per second, which is exactly the DecisionRequester period-8
    /// rate at timeScale 1.0, so 50k steps would have taken over an hour instead of the
    /// ~3 minutes intended. Something later in startup puts the clock back to 1.
    ///
    /// I first blamed <see cref="Agent_TimeFreeze"/>, which is the project's
    /// reference-counted owner of the clock and whose own docstring warns that writing
    /// Time.timeScale directly is how the clock ends up owned by two things. That was
    /// WRONG - it is not in SCN_Training at all. The writer was not identified, and
    /// chasing it was not the best use of the time: a per-frame re-assert is correct
    /// against ANY one-shot writer, including one nobody has found yet.
    ///
    /// This is deliberately scoped to recording only. It is created by
    /// Agent_Soccer.ApplyDemoRecording, which runs only under POSOCCER_RECORD_DEMO, so
    /// it can never contend with Agent_TimeFreeze during a real match - that component
    /// stays the sole owner of the clock everywhere it matters.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class Agent_DemoClock : MonoBehaviour
    {
        internal float Scale = 20f;

        void LateUpdate()
        {
            // LateUpdate so it lands after anything that reset the clock this frame.
            if (!Mathf.Approximately(Time.timeScale, Scale))
            {
                Time.timeScale = Scale;
            }
        }
    }
}
