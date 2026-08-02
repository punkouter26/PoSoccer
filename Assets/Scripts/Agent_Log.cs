using System.Diagnostics;

namespace PoSoccer
{
    /// <summary>
    /// Informational logging that is compiled out of release player builds
    /// (.claude/rules/performance.md: no Debug.Log in production).
    ///
    /// [Conditional] strips the whole call site, so interpolated-string arguments
    /// are never even built in a release player - passing $"..." here is free.
    ///
    /// This covers *informational* logging only. Warnings and errors that report
    /// real misconfiguration (missing PanelSettings, eval mode with no model,
    /// out-of-bounds watchdog) stay as Debug.LogWarning/LogError so they survive
    /// into player builds, and Agent_EvalStats keeps raw Debug.Log because those
    /// lines are the headless evaluation telemetry.
    /// </summary>
    internal static class Agent_Log
    {
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        internal static void Info(string message) => UnityEngine.Debug.Log(message);
    }
}
