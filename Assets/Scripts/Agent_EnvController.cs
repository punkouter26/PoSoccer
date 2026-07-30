using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Per-pitch episode orchestrator: kickoff resets, last-touch/assist tracking,
    /// terminal rewards, stalemate timeout, and goal-width curriculum.
    /// One instance sits on each pitch root so 16 pitches train independently.
    /// Registers agents in SimpleMultiAgentGroups so MA-POCA group rewards work for 2v2/3v3.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class Agent_EnvController : MonoBehaviour
    {
        [Header("Wiring")]
        public Reward_Settings rewards;
        public Rigidbody2D ball;
        public Transform blueGoal;
        public Transform redGoal;
        public List<Agent_Soccer> agents = new();

        [Header("Pitch")]
        [Tooltip("Half width (x) and half height (y) of the playable area, pitch-local.")]
        public Vector2 pitchHalfExtents = new(10f, 6f);
        [Tooltip("Random kickoff jitter applied to the ball spawn.")]
        public float ballSpawnJitter = 1.0f;

        [Header("Curriculum")]
        [Tooltip("Fallback goal width when no trainer curriculum is driving 'goal_width'.")]
        public float defaultGoalWidth = 6.0f;

        public Rigidbody2D Ball => ball;
        public Vector2 PitchHalfExtents => pitchHalfExtents;
        public int StepCount { get; private set; }
        public float CurrentGoalWidth { get; private set; }

        /// <summary>
        /// Fires once per episode after terminal rewards are applied but BEFORE
        /// EndEpisode/ResetPitch, so subscribers can still sample
        /// agent.GetCumulativeReward() and StepCount. Winning team, null = stalemate.
        /// </summary>
        public event System.Action<Agent_Soccer.Team?> EpisodeEnded;

        SimpleMultiAgentGroup _blueGroup;
        SimpleMultiAgentGroup _redGroup;
        Agent_Soccer _lastToucher;
        Agent_Soccer _previousToucher;
        readonly Dictionary<Agent_Soccer, Vector3> _spawnPositions = new();
        readonly Dictionary<Agent_Soccer, Quaternion> _spawnRotations = new();
        Vector3 _ballSpawn;
        bool _episodeEnding;

        void Start()
        {
            // Safety net: keep the pitch functional even if no asset is wired.
            if (rewards == null) rewards = ScriptableObject.CreateInstance<Reward_Settings>();

            // Self-discover agents under this pitch; also heals null serialized entries
            // and keeps runtime-instantiated grid clones self-contained.
            agents.RemoveAll(a => a == null);
            if (agents.Count == 0)
                agents.AddRange(GetComponentsInChildren<Agent_Soccer>());

            foreach (var agent in agents)
            {
                agent.env = this;
                if (agent.rewards == null) agent.rewards = rewards;
                _spawnPositions[agent] = agent.transform.position;
                _spawnRotations[agent] = agent.transform.rotation;
            }
            _ballSpawn = ball != null ? ball.transform.position : transform.position;

            bool useGroups = TeamSize(Agent_Soccer.Team.Blue) > 1 || TeamSize(Agent_Soccer.Team.Red) > 1;
            if (useGroups)
            {
                _blueGroup = new SimpleMultiAgentGroup();
                _redGroup = new SimpleMultiAgentGroup();
                foreach (var agent in agents)
                    GroupFor(agent.team).RegisterAgent(agent);
            }

            ResetPitch();
        }

        void FixedUpdate()
        {
            StepCount++;
            if (rewards != null && StepCount >= rewards.maxEnvironmentSteps)
                OnStalemate();
        }

        // ── Goal / touch bookkeeping ────────────────────────────────────────

        public void NotifyBallTouch(Agent_Soccer toucher)
        {
            if (_lastToucher != toucher)
            {
                _previousToucher = _lastToucher;
                _lastToucher = toucher;
            }
        }

        /// <summary>Called by Reward_GoalTrigger when the ball fully enters a net.</summary>
        public void OnGoalScored(Agent_Soccer.Team concedingTeam)
        {
            if (_episodeEnding) return;
            _episodeEnding = true;

            var scoringTeam = Agent_Soccer.Opponent(concedingTeam);

            foreach (var agent in agents)
            {
                if (agent.team == concedingTeam)
                {
                    agent.AddReward(rewards.goalConceded);
                }
                else if (agent == _lastToucher && _lastToucher.team == scoringTeam)
                {
                    agent.AddReward(rewards.goalScorer);
                }
                else if (agent == _previousToucher && _previousToucher.team == scoringTeam)
                {
                    agent.AddReward(rewards.assist);
                }
                else
                {
                    agent.AddReward(rewards.teamBaselineVictory);
                }
            }

            EpisodeEnded?.Invoke(scoringTeam);

            // Group-level signal for MA-POCA credit assignment (2v2 / 3v3)
            if (_blueGroup != null)
            {
                var winners = GroupFor(scoringTeam);
                var losers = GroupFor(concedingTeam);
                winners.AddGroupReward(1f);
                losers.AddGroupReward(-1f);
                winners.EndGroupEpisode();
                losers.EndGroupEpisode();
            }
            else
            {
                foreach (var agent in agents) agent.EndEpisode();
            }

            ResetPitch();
        }

        void OnStalemate()
        {
            if (_episodeEnding) return;
            _episodeEnding = true;

            foreach (var agent in agents) agent.AddReward(rewards.stalemateTimeout);

            EpisodeEnded?.Invoke(null);

            if (_blueGroup != null)
            {
                _blueGroup.GroupEpisodeInterrupted();
                _redGroup.GroupEpisodeInterrupted();
            }
            else
            {
                foreach (var agent in agents) agent.EpisodeInterrupted();
            }

            ResetPitch();
        }

        // ── Reset & curriculum ──────────────────────────────────────────────

        void ResetPitch()
        {
            StepCount = 0;
            _lastToucher = null;
            _previousToucher = null;

            CurrentGoalWidth = Academy.Instance.EnvironmentParameters
                .GetWithDefault("goal_width", defaultGoalWidth);
            ApplyGoalWidth(blueGoal, CurrentGoalWidth);
            ApplyGoalWidth(redGoal, CurrentGoalWidth);

            if (ball != null)
            {
                Vector2 jitter = Random.insideUnitCircle * ballSpawnJitter;
                ball.transform.position = _ballSpawn + (Vector3)jitter;
                ball.linearVelocity = Vector2.zero;
                ball.angularVelocity = 0f;
            }

            foreach (var agent in agents)
            {
                Vector3 basePos = _spawnPositions[agent];
                Vector2 jitter = Random.insideUnitCircle * 0.75f;
                agent.transform.position = basePos + (Vector3)jitter;
                agent.transform.rotation = _spawnRotations[agent]
                    * Quaternion.Euler(0f, 0f, Random.Range(-20f, 20f));
            }

            _episodeEnding = false;
        }

        static void ApplyGoalWidth(Transform goal, float width)
        {
            if (goal == null) return;
            Vector3 s = goal.localScale;
            // Goal mouths span local X (pitch runs along Y for mobile portrait).
            goal.localScale = new Vector3(width, s.y, s.z);
        }

        // ── Queries used by agent observations ──────────────────────────────

        public Vector2 GetGoalPosition(Agent_Soccer.Team ownerTeam) =>
            GetGoalTransform(ownerTeam) is { } t ? (Vector2)t.position : Vector2.zero;

        public Transform GetGoalTransform(Agent_Soccer.Team ownerTeam) =>
            ownerTeam == Agent_Soccer.Team.Blue ? blueGoal : redGoal;

        public Agent_Soccer GetTeammate(Agent_Soccer self, int index)
        {
            int seen = 0;
            foreach (var agent in agents)
            {
                if (agent == self || agent.team != self.team) continue;
                if (seen == index) return agent;
                seen++;
            }
            return null;
        }

        int TeamSize(Agent_Soccer.Team t)
        {
            int n = 0;
            foreach (var agent in agents) if (agent.team == t) n++;
            return n;
        }

        SimpleMultiAgentGroup GroupFor(Agent_Soccer.Team t) =>
            t == Agent_Soccer.Team.Blue ? _blueGroup : _redGroup;
    }
}
