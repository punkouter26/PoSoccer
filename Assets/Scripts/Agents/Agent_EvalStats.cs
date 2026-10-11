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
        public static bool BaselineMode => Agent_RunConfig.IsOne(Agent_RunConfig.BASELINE);
        public static bool EvalMode =>
            BaselineMode || Agent_RunConfig.IsOne(Agent_RunConfig.EVAL);

        static readonly object Gate = new();
        static Agent_EvalStats _primary;
        static int _target, _episodes, _blueWins, _redWins, _stalemates;
        static float _stepSum, _blueRewardSum;
        static int _bluePasses, _redPasses, _blueAssistedGoals, _redAssistedGoals;
        static float _bluePassProgress, _redPassProgress;
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
                _target = Agent_RunConfig.GetInt(Agent_RunConfig.EPISODES, 100);
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

                // Team play. Read here because EpisodeEnded fires before the reset.
                _bluePasses += _env.PassesThisEpisode(Agent_Soccer.Team.Blue);
                _redPasses += _env.PassesThisEpisode(Agent_Soccer.Team.Red);
                _bluePassProgress += _env.PassProgressThisEpisode(Agent_Soccer.Team.Blue);
                _redPassProgress += _env.PassProgressThisEpisode(Agent_Soccer.Team.Red);
                if (winner == Agent_Soccer.Team.Blue
                    && _env.LastTouchWasFromTeammate(Agent_Soccer.Team.Blue)) _blueAssistedGoals++;
                if (winner == Agent_Soccer.Team.Red
                    && _env.LastTouchWasFromTeammate(Agent_Soccer.Team.Red)) _redAssistedGoals++;

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
                runId = Agent_RunConfig.GetString(Agent_RunConfig.RUN_ID) ?? "unknown",
                episodes = _episodes,
                blueWins = _blueWins,
                redWins = _redWins,
                stalemates = _stalemates,
                meanEpisodeSteps = _episodes > 0 ? _stepSum / _episodes : 0f,
                meanBlueReward = _episodes > 0 ? _blueRewardSum / _episodes : 0f,
                bluePasses = _bluePasses,
                redPasses = _redPasses,
                bluePassProgressM = _bluePassProgress,
                redPassProgressM = _redPassProgress,
                blueAssistedGoals = _blueAssistedGoals,
                redAssistedGoals = _redAssistedGoals,
                // Resolved from the brain the agents are ACTUALLY running, not a literal.
                // This field used to be the hardcoded string "SoccerAgent_v01.onnx" and was
                // written into every eval JSON regardless of what was graded - a filename
                // that has not existed since the legacy behavior name was retired. An eval
                // report that states the wrong model with total confidence is precisely the
                // artefact behind the phase-10 retraction (three published win rates that
                // all described a model nobody meant to grade). evaluate.ps1 also stamps
                // modelPath/modelWrittenUtc/playerBuiltUtc afterwards; this makes the
                // player's own half of the record honest too.
                modelFile = ResolveModelName(),
                baseline = BaselineMode,
                // The lineup the grade was played at. A 2v2 grade and a 1v1 grade of the
                // same brain are different measurements; without this field the JSON
                // could not tell them apart. Empty POSOCCER_SQUAD = the authored 1v1.
                squad = Agent_RunConfig.GetString(Agent_RunConfig.SQUAD, "1"),
                invalid = _invalid,
                timestampUtc = DateTime.UtcNow.ToString("o"),
            };

            string path = Agent_RunConfig.GetString(Agent_RunConfig.OUT);
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Application.persistentDataPath, "eval.json");

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log($"[EvalStats] Report written to {path}");

            if (!Application.isEditor) Application.Quit();
        }

        /// <summary>
        /// Name of the model the blue side is actually running this eval, or an explicit
        /// marker when there is none. Never a literal - see the call site.
        /// </summary>
        static string ResolveModelName()
        {
            if (BaselineMode) return "(baseline: heuristic bot, no model)";

            var agents = UnityEngine.Object.FindObjectsByType<Agent_Soccer>();
            for (int i = 0; i < agents.Length; i++)
            {
                var a = agents[i];
                if (a == null || a.team != Agent_Soccer.Team.Blue) continue;
                var bp = a.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                if (bp != null && bp.Model != null) return bp.Model.name + ".onnx";
            }
            return "(no model assigned - blue ran as heuristic bot)";
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
            // Completed passes (teammate to teammate, 3 m or more, no opponent touch
            // between), the ground they gained toward goal, and goals whose last two
            // touches were two players of the scoring side. All zero at 1v1.
            public int bluePasses;
            public int redPasses;
            public float bluePassProgressM;
            public float redPassProgressM;
            public int blueAssistedGoals;
            public int redAssistedGoals;
            public string modelFile;
            public bool baseline;
            public string squad;
            public bool invalid;
            public string timestampUtc;
        }
    }
}
