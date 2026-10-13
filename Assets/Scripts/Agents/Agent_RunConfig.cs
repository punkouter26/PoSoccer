using System;

namespace PoSoccer
{
    /// <summary>
    /// Every POSOCCER_* environment variable the runtime reads, named once.
    ///
    /// These variables are the launcher's only door into a headless player
    /// (scripts/train-phase1.ps1, evaluate.ps1, train-all.ps1 set them before
    /// Start-Process), and they used to be string literals repeated across four
    /// files - so a typo in one read was a run that silently ignored its own flag,
    /// which is the POSOCCER_OPPONENT failure shape (see CLAUDE.md).
    ///
    /// EVERY ACCESSOR READS LIVE. Nothing here caches, on purpose: the variables
    /// are consulted at different points of startup (Sensor_Vision.Awake at -100,
    /// Agent_EnvController.Awake at -50, Agent_Soccer.Awake, then report time), and
    /// a cached value would freeze whatever the first reader saw. That also keeps
    /// a test free to set a variable mid-session and have the next read see it.
    ///
    /// Parsing is the caller's culture-default TryParse, exactly as the inline
    /// reads were - this class is a rename, not a behaviour change.
    /// </summary>
    public static class Agent_RunConfig
    {
        // ── Evaluation (scripts/evaluate.ps1) ───────────────────────────────
        /// <summary>"1" = grade the trained blue side against the bot.</summary>
        public const string EVAL = "POSOCCER_EVAL";
        /// <summary>"1" = bot-vs-bot sanity run. Implies eval mode.</summary>
        public const string BASELINE = "POSOCCER_BASELINE";
        /// <summary>Episode count at which the eval player writes its report and quits.</summary>
        public const string EPISODES = "POSOCCER_EPISODES";
        /// <summary>Run id stamped into the eval JSON.</summary>
        public const string RUN_ID = "POSOCCER_RUNID";
        /// <summary>Path of the eval JSON.</summary>
        public const string OUT = "POSOCCER_OUT";

        // ── Training (scripts/train-phase1.ps1, train-all.ps1) ──────────────
        /// <summary>
        /// "bot" = RED is the scripted opponent. "league" = the pitch grid mixes self-play
        /// pitches with pitches where one side is the scripted bot
        /// (Agent_EnvController.LeagueSeatFor). Unset = self-play on every pitch.
        /// </summary>
        public const string OPPONENT = "POSOCCER_OPPONENT";
        /// <summary>Players per side, cycled over the pitch grid ("2", "1,2").</summary>
        public const string SQUAD = "POSOCCER_SQUAD";
        /// <summary>Roster profile (by playerName) to train instead of the scene's.</summary>
        public const string PROFILE = "POSOCCER_PROFILE";
        /// <summary>Initial scripted-bot difficulty in [0, 1].</summary>
        public const string BOT_STRENGTH = "POSOCCER_BOT_STRENGTH";
        /// <summary>Scripted-bot opponent perception radius (0 = unlimited).</summary>
        public const string BOT_VISION = "POSOCCER_BOT_VISION";
        /// <summary>Scripted-bot version on RED: 1 = the benchmark bot (default), 2 = lead pursuit and bank shots.</summary>
        public const string BOT_VERSION = "POSOCCER_BOT_VERSION";
        /// <summary>Scripted-bot version on BLUE, for bot-vs-bot runs. Default 1.</summary>
        public const string BOT_VERSION_BLUE = "POSOCCER_BOT_VERSION_BLUE";

        // ── Demonstration recording ─────────────────────────────────────────
        /// <summary>Anything but empty / "0" = record the scripted bot to a .demo.</summary>
        public const string RECORD_DEMO = "POSOCCER_RECORD_DEMO";
        public const string DEMO_TIMESCALE = "POSOCCER_DEMO_TIMESCALE";
        public const string DEMO_DIR = "POSOCCER_DEMO_DIR";
        public const string DEMO_STEPS = "POSOCCER_DEMO_STEPS";

        // ── Diagnostics ─────────────────────────────────────────────────────
        /// <summary>
        /// Read by Agent_Telemetry, which keeps its own ENV_TELEMETRY constant.
        /// Listed so this file is the complete inventory.
        /// </summary>
        public const string TELEMETRY = "POSOCCER_TELEMETRY";

        /// <summary>The raw value, or null when the variable is not set.</summary>
        public static string GetString(string name) => Environment.GetEnvironmentVariable(name);

        /// <summary>The raw value, or <paramref name="fallback"/> when unset or empty.</summary>
        public static string GetString(string name, string fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(raw) ? fallback : raw;
        }

        /// <summary>True only when the variable is exactly "1" (the eval switches).</summary>
        public static bool IsOne(string name) => Environment.GetEnvironmentVariable(name) == "1";

        /// <summary>True when the variable is set to anything but "" or "0" (opt-in flags).</summary>
        public static bool IsSet(string name)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(raw) && raw != "0";
        }

        /// <summary>True when the variable equals <paramref name="value"/>, ignoring case.</summary>
        public static bool Is(string name, string value)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(raw)
                && raw.Equals(value, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Parses the variable as an int. False when unset or malformed.</summary>
        public static bool TryGetInt(string name, out int value) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out value);

        /// <summary>Parses the variable as a float. False when unset, empty or malformed.</summary>
        public static bool TryGetFloat(string name, out float value)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
            {
                value = 0f;
                return false;
            }
            return float.TryParse(raw, out value);
        }

        /// <summary>The variable as an int, or <paramref name="fallback"/> when unset or malformed.</summary>
        public static int GetInt(string name, int fallback) =>
            TryGetInt(name, out int value) ? value : fallback;

        /// <summary>The variable as a float, or <paramref name="fallback"/> when unset or malformed.</summary>
        public static float GetFloat(string name, float fallback) =>
            TryGetFloat(name, out float value) ? value : fallback;
    }
}
