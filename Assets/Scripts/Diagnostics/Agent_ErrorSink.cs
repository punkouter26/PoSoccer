using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Records every Error, Assert and Exception the game logs, with the scene and
    /// the clock reading at the moment it fired, and writes them to a file on quit.
    ///
    /// WHY THIS EXISTS AT ALL. This project's verification story is the test suite
    /// and the headless eval, and both of them answer a question that was asked in
    /// advance. Nothing was watching for the failure nobody predicted: a null
    /// reference thrown once per frame inside a presentation component costs no
    /// test, fails no assertion, and produces a match that still ends 5-3. The
    /// console shows it, and the console is not read after an unattended run.
    ///
    /// IT INSTALLS ITSELF, BEFORE THE FIRST SCENE. A component that has to be put
    /// in a scene is a component that is missing from the scene where the bug is -
    /// the exact argument Agent_Presentation.EnsureChrome already makes about
    /// Agent_Chrome. RuntimeInitializeOnLoadMethod means it is present in every
    /// scene, in the editor and in a player build, with nothing to wire.
    ///
    /// COST IS A LOG CALLBACK AND NOTHING ELSE. There is no Update. The delegate
    /// runs only when something is logged, and returns immediately for the Log and
    /// Warning types that make up all of the normal traffic. That is what makes it
    /// safe to leave installed during training and evaluation, where perturbing the
    /// timing would corrupt the numbers the benchmark exists to produce.
    ///
    /// Stack traces are kept for the first few only. A game that throws every frame
    /// produces tens of thousands of identical traces, and the useful information -
    /// that it happened, where, and how often - is in the count.
    /// </summary>
    public sealed class Agent_ErrorSink : MonoBehaviour
    {
        /// <summary>Distinct messages retained. Past this the sink counts and stops storing.</summary>
        private const int MAX_DISTINCT = 64;

        /// <summary>Entries that keep their stack trace. The rest keep the message and the count.</summary>
        private const int MAX_TRACES = 8;

        public sealed class Entry
        {
            public string Message;
            public string StackTrace;
            public LogType Type;
            public string Scene;
            public float FirstSeenTime;
            public int Count;
        }

        private static Agent_ErrorSink _instance;

        private readonly List<Entry> _entries = new();
        private readonly Dictionary<string, Entry> _byMessage = new();
        private int _suppressed;

        /// <summary>Total Error/Assert/Exception logs seen, including repeats and suppressed ones.</summary>
        public static int TotalCount { get; private set; }

        /// <summary>Distinct messages recorded, first seen first. Empty when nothing has failed.</summary>
        public static IReadOnlyList<Entry> Entries =>
            _instance != null ? (IReadOnlyList<Entry>)_instance._entries : System.Array.Empty<Entry>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (_instance != null) return;

            var go = new GameObject("ErrorSink") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<Agent_ErrorSink>();
        }

        /// <summary>
        /// Forgets everything recorded so far. A test that wants to assert on ITS
        /// OWN scene has to be able to discount whatever the previous test logged,
        /// and a sink that can only accumulate makes the first failure poison every
        /// assertion after it.
        /// </summary>
        public static void Reset()
        {
            TotalCount = 0;
            if (_instance == null) return;
            _instance._entries.Clear();
            _instance._byMessage.Clear();
            _instance._suppressed = 0;
        }

        /// <summary>
        /// One line per distinct failure, or a single line saying there were none.
        /// Safe to put straight into an assertion message.
        /// </summary>
        public static string Report()
        {
            if (_instance == null) return "Agent_ErrorSink was never installed.";
            return _instance.BuildReport();
        }

        private void OnEnable() => Application.logMessageReceived += OnLog;

        private void OnDisable() => Application.logMessageReceived -= OnLog;

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;

            TotalCount++;

            if (_byMessage.TryGetValue(message, out Entry existing))
            {
                existing.Count++;
                return;
            }

            if (_entries.Count >= MAX_DISTINCT)
            {
                _suppressed++;
                return;
            }

            var entry = new Entry
            {
                Message = message,
                // A trace is only kept for the first few. See the class docstring.
                StackTrace = _entries.Count < MAX_TRACES ? stackTrace : null,
                Type = type,
                Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                FirstSeenTime = Time.unscaledTime,
                Count = 1,
            };
            _entries.Add(entry);
            _byMessage[message] = entry;
        }

        private string BuildReport()
        {
            if (_entries.Count == 0)
            {
                return TotalCount == 0
                    ? "No errors, asserts or exceptions were logged."
                    : TotalCount + " error(s) logged but none retained.";
            }

            var builder = new StringBuilder(4096);
            builder.Append(TotalCount).Append(" error/assert/exception log(s), ")
                   .Append(_entries.Count).Append(" distinct");
            if (_suppressed > 0)
            {
                builder.Append(", ").Append(_suppressed).Append(" beyond the retention cap");
            }
            builder.Append(":\n");

            for (int entryIndex = 0; entryIndex < _entries.Count; entryIndex++)
            {
                Entry entry = _entries[entryIndex];
                builder.Append("  [").Append(entry.Type).Append(" x").Append(entry.Count)
                       .Append("] ").Append(entry.Scene).Append(" @ ")
                       .Append(entry.FirstSeenTime.ToString("0.0")).Append("s: ")
                       .Append(entry.Message).Append('\n');
                if (!string.IsNullOrEmpty(entry.StackTrace))
                {
                    builder.Append("      ").Append(FirstFrameOf(entry.StackTrace)).Append('\n');
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// The first line of a stack trace that names project code. Unity's traces
        /// open with the throw site inside the engine or inside a logging helper,
        /// which is the same for every failure and says nothing about which one
        /// this is.
        /// </summary>
        private static string FirstFrameOf(string stackTrace)
        {
            string[] lines = stackTrace.Split('\n');
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (lines[lineIndex].Contains("PoSoccer.")) return lines[lineIndex].Trim();
            }
            return lines.Length > 0 ? lines[0].Trim() : string.Empty;
        }

        private void OnApplicationQuit() => Flush();

        /// <summary>
        /// Writes the report next to the telemetry CSV, so an unattended or
        /// on-device session leaves both halves of its evidence in one folder.
        /// Nothing is written when nothing failed - an empty file in that folder
        /// would be indistinguishable from a run that never started.
        /// </summary>
        private void Flush()
        {
            if (_entries.Count == 0) return;

            string path = System.IO.Path.Combine(
                Application.persistentDataPath,
                "posoccer-errors-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            try
            {
                System.IO.File.WriteAllText(path, BuildReport());
            }
            catch (System.IO.IOException exception)
            {
                // Reporting that reporting failed is all that is left to do here.
                Debug.LogWarning("Agent_ErrorSink could not write " + path + ": " + exception.Message);
            }
        }
    }
}
