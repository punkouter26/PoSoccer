using System;
using System.IO;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Evaluation harness (v1 spec). Lives on the Pitch root (cloned with the grid);
    /// every instance subscribes to its pitch's EpisodeEnded, aggregation is static.
    /// Active only when POSOCCER_EVAL=1 or POSOCCER_BASELINE=1 (set by
    /// scripts/evaluate.ps1 before Start-Process). Writes JSON to POSOCCER_OUT and
    /// quits after POSOCCER_EPISODES episodes.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_EvalStats : MonoBehaviour
    {
        public static bool BaselineMode =>
            Environment.GetEnvironmentVariable("POSOCCER_BASELINE") == "1";
        public static bool EvalMode =>
            BaselineMode || Environment.GetEnvironmentVariable("POSOCCER_EVAL") == "1";

        static readonly object Gate = new();
        static Agent_EvalStats _primary;
        static int _target, _episodes, _blueWins, _redWins, _stalemates;
        static float _stepSum, _blueRewardSum;
        static bool _finished, _invalid;

        Agent_EnvController _env;

        /// <summary>Called by Agent_Soccer when eval preconditions fail (e.g. no model).</summary>
        public static void MarkInvalid() => _invalid = true;

        void Awake()
        {
            if (!EvalMode)
            {
                enabled = false;
                return;
            }

            if (_primary == null)
            {
                _primary = this;
                _target = int.TryParse(
                    Environment.GetEnvironmentVariable("POSOCCER_EPISODES"), out int n) ? n : 100;
                // Headless eval runs faster than real time; physics step stays 0.01s.
                Time.timeScale = 20f;
                Application.targetFrameRate = -1;
                // Deliberate raw Debug.Log (not Agent_Log): these lines are the
                // headless evaluation telemetry read from the player's stdout by
                // evaluate.ps1, so they must survive into non-development builds.
                Debug.Log($"[EvalStats] Eval mode: target={_target} baseline={BaselineMode}");
            }
        }

        void Start()
        {
            if (!enabled) return;
            _env = GetComponent<Agent_EnvController>();
            _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            lock (Gate)
            {
                if (_finished) return;

                _episodes++;
                _stepSum += _env.StepCount;

                foreach (var agent in _env.agents)
                    if (agent != null && agent.team == Agent_Soccer.Team.Blue)
                        _blueRewardSum += agent.GetCumulativeReward();

                if (winner == Agent_Soccer.Team.Blue) _blueWins++;
                else if (winner == Agent_Soccer.Team.Red) _redWins++;
                else _stalemates++;

                if (_episodes % 10 == 0)
                    Debug.Log($"[EvalStats] {_episodes}/{_target} blue={_blueWins} red={_redWins} stale={_stalemates}");

                if (_episodes >= _target)
                {
                    _finished = true;
                    WriteReportAndQuit();
                }
            }
        }

        static void WriteReportAndQuit()
        {
            var report = new Report
            {
                runId = Environment.GetEnvironmentVariable("POSOCCER_RUNID") ?? "unknown",
                episodes = _episodes,
                blueWins = _blueWins,
                redWins = _redWins,
                stalemates = _stalemates,
                meanEpisodeSteps = _episodes > 0 ? _stepSum / _episodes : 0f,
                meanBlueReward = _episodes > 0 ? _blueRewardSum / _episodes : 0f,
                modelFile = "SoccerAgent_v01.onnx",
                baseline = BaselineMode,
                invalid = _invalid,
                timestampUtc = DateTime.UtcNow.ToString("o"),
            };

            string path = Environment.GetEnvironmentVariable("POSOCCER_OUT");
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Application.persistentDataPath, "eval.json");

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log($"[EvalStats] Report written to {path}");

            if (!Application.isEditor) Application.Quit();
        }

        [Serializable]
        struct Report
        {
            public string runId;
            public int episodes;
            public int blueWins;
            public int redWins;
            public int stalemates;
            public float meanEpisodeSteps;
            public float meanBlueReward;
            public string modelFile;
            public bool baseline;
            public bool invalid;
            public string timestampUtc;
        }
    }
}
