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
        [Tooltip("All trainable personality profiles. POSOCCER_PROFILE (set by the training " +
                 "scripts before launch) picks one by playerName, so a single build can train " +
                 "any brain without a scene edit per run.")]
        public Reward_Settings[] profileRoster;
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
        [Tooltip("Fallback opponent difficulty when no trainer curriculum is driving 'bot_strength'. " +
                 "1 = full-strength bot, which is what evaluation always faces.")]
        [Range(0f, 1f)]
        public float defaultBotStrength = 1.0f;
        [Tooltip("Episode step cap override for exhibition scenes (0 = use the reward profile's cap).")]
        public int stepCapOverride = 0;

        [Header("Ball physics")]
        [Tooltip("Magnus-lite: curl force = scale * spin * perpendicular(velocity). Gives swerving shots.")]
        public float magnusScale = 0.0005f;
        [Tooltip("Ball rolling drag randomized per episode (domain randomization for robust policies).")]
        public Vector2 ballDampingRange = new(0.08f, 0.15f);

        [Header("Domain randomization")]
        [Tooltip("Agents respawn anywhere in their own half within these margins instead of fixed spots.")]
        public bool randomizeSpawns = true;

        public Rigidbody2D Ball => ball;
        public Vector2 PitchHalfExtents => pitchHalfExtents;
        public int StepCount { get; private set; }
        /// <summary>
        /// Effective episode step cap (reward profile value, or 5000 fallback).
        /// Agents read this in CollectObservations to normalise the
        /// time-remaining scalar so the obs shape doesn't change when the
        /// profile's maxEnvironmentSteps does.
        /// </summary>
        public int MaxEnvironmentSteps =>
            rewards != null ? rewards.maxEnvironmentSteps : 5000;
        public float CurrentGoalWidth { get; private set; }
        /// <summary>Opponent difficulty applied at the last kickoff (curriculum readout).</summary>
        public float CurrentBotStrength { get; private set; }

        /// <summary>
        /// Fires once per episode after terminal rewards are applied but BEFORE
        /// EndEpisode/ResetPitch, so subscribers can still sample
        /// agent.GetCumulativeReward() and StepCount. Winning team, null = stalemate.
        /// </summary>
        public event System.Action<Agent_Soccer.Team?> EpisodeEnded;

        /// <summary>
        /// Fires after ApplyGoalWidth has changed the goal mouth width during
        /// ResetPitch. Listeners (GoalFrame, HUD readout) can re-read the new
        /// CurrentGoalWidth and re-render. Arg is the goal transform that was
        /// resized (or null if both).
        /// </summary>
        public static event System.Action<Transform> PitchReconfigured;

        /// <summary>
        /// Fires when possession changes hands - i.e. when a DIFFERENT agent
        /// touches the ball than the one who touched it last. Deliberately not
        /// once per physical contact: a player dribbling generates a contact
        /// every few ticks, which would make the commentary and the man-of-the-
        /// match touch counts read as noise. Presentation-only; no reward or
        /// observation depends on it.
        /// </summary>
        public event System.Action<Agent_Soccer> BallTouched;

        /// <summary>Most recent ball toucher (the scorer credit holder), for UI.</summary>
        public Agent_Soccer LastToucher => _lastToucher;

        /// <summary>Toucher before <see cref="LastToucher"/> - the assist holder.</summary>
        public Agent_Soccer PreviousToucher => _previousToucher;

        SimpleMultiAgentGroup _blueGroup;
        SimpleMultiAgentGroup _redGroup;
        Agent_Soccer _lastToucher;
        Agent_Soccer _previousToucher;
        readonly List<Agent_HeuristicBot> _bots = new();
        readonly Dictionary<Agent_Soccer, Vector3> _spawnPositions = new();
        readonly Dictionary<Agent_Soccer, Quaternion> _spawnRotations = new();
        bool _spawnCacheWarned;
        Vector3 _ballSpawn;
        bool _episodeEnding;

        // Execution order (-50) puts this before Agent_Soccer.Awake, which is where
        // the brain contract is frozen - the profile swap must land before that.
        void Awake()
        {
            ApplyProfileOverride();
        }

        bool _profileOverridden;

        void ApplyProfileOverride()
        {
            string wanted = System.Environment.GetEnvironmentVariable("POSOCCER_PROFILE");
            if (string.IsNullOrEmpty(wanted) || profileRoster == null) return;

            for (int i = 0; i < profileRoster.Length; i++)
            {
                var profile = profileRoster[i];
                if (profile == null || profile.playerName == null) continue;
                if (!string.Equals(profile.playerName, wanted,
                        System.StringComparison.OrdinalIgnoreCase)) continue;

                rewards = profile;
                _profileOverridden = true;

                // Push onto the agents now, while they are still pre-Awake.
                var children = GetComponentsInChildren<Agent_Soccer>(true);
                for (int a = 0; a < children.Length; a++)
                {
                    children[a].rewards = profile;
                    children[a].brainName = profile.playerName;
                }
                Debug.Log($"[Env] POSOCCER_PROFILE={wanted} -> training brain '{profile.playerName}'");
                return;
            }
            Debug.LogError($"[Env] POSOCCER_PROFILE='{wanted}' matches no entry in profileRoster.");
        }

        void Start()
        {
            // Safety net: keep the pitch functional even if no asset is wired.
            if (rewards == null) rewards = ScriptableObject.CreateInstance<Reward_Settings>();

            // Rounded slick corners + bouncy pads (anti corner-jam), idempotent per pitch.
            Agent_PitchGuard.Build(this);

            // Self-discover agents under this pitch; also heals null serialized entries
            // and keeps runtime-instantiated grid clones self-contained.
            agents.RemoveAll(a => a == null);
            if (agents.Count == 0)
                agents.AddRange(GetComponentsInChildren<Agent_Soccer>());

            foreach (var agent in agents)
            {
                agent.env = this;
                // The profile override owns both agents so the run trains one brain
                // against a mirror of itself; without it, the serialized asset wins.
                if (_profileOverridden) agent.rewards = rewards;
                if (agent.rewards == null) agent.rewards = rewards;
                if (_profileOverridden) agent.brainName = rewards.playerName;
                _spawnPositions[agent] = agent.transform.position;
                _spawnRotations[agent] = agent.transform.rotation;

                // Cached once so the per-episode curriculum push costs no lookups.
                var bot = agent.GetComponent<Agent_HeuristicBot>();
                if (bot != null) _bots.Add(bot);
            }
            ApplyBotBounds();
            _ballSpawn = ball != null ? ball.transform.position : transform.position;

            // Goal-line frames: red frame around the blue goal (the one Red defends),
            // blue frame around the red goal. Tints match the team that OWN-EYE
            // sees the goal as their own net - so the UI reads "my goal = my color"
            // even though the ball goes in the opposite color's net to score.
            // Convention: redGoal is the goal Red scores into, blueGoal is what Blue scores into.
            // A red frame around redGoal reads as "where Red has to put the ball".
            // Orange rather than the team's red: laid over the grey wall, red goes
            // muddy while orange stays legible on a phone screen.
            EnsureGoalFrame(blueGoal, new Color(0.15f, 0.55f, 1f, 1f));   // Blue goal mouth
            EnsureGoalFrame(redGoal, new Color(1f, 0.5f, 0.05f, 1f));     // Orange goal mouth

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
            int cap = stepCapOverride > 0 ? stepCapOverride
                : rewards != null ? rewards.maxEnvironmentSteps : 5000;
            if (StepCount >= cap)
                OnStalemate();
            // OOB watchdog intentionally removed (bouncier walls in Agent_PitchGuard
            // now keep play contained, so a match should never need to be reset for
            // an escape - gameplay never stops).

            // Magnus-lite: spinning balls curve. angularVelocity is deg/s in 2D.
            if (ball != null && magnusScale > 0f)
            {
                Vector2 v = ball.linearVelocity;
                float spin = ball.angularVelocity * Mathf.Deg2Rad;
                ball.AddForce(magnusScale * spin * new Vector2(-v.y, v.x));
            }
        }

        // ── Goal / touch bookkeeping ────────────────────────────────────────

        public void NotifyBallTouch(Agent_Soccer toucher)
        {
            if (_lastToucher != toucher)
            {
                _previousToucher = _lastToucher;
                _lastToucher = toucher;
                BallTouched?.Invoke(toucher);
            }
        }

        /// <summary>Called by Reward_GoalTrigger when the ball fully enters a net.</summary>
        public void OnGoalScored(Agent_Soccer.Team concedingTeam)
        {
            if (_episodeEnding) return;
            _episodeEnding = true;

            var scoringTeam = Agent_Soccer.Opponent(concedingTeam);

            // v5 (2026-08-11): goalSpeedBonus rewards fast scoring. Compute once per
            // event from the per-agent episode start step (cached in OnEpisodeBegin)
            // so a slow spawn doesn't penalise a quick counter-attack. Bonus is small
            // enough not to compete with goalScorer (1.2) but visible enough to bias
            // late-game urgency when the new time-remaining obs starts paying off.
            int maxSteps = rewards != null ? rewards.maxEnvironmentSteps : 5000;
            float bonus = 0f;
            if (rewards != null && rewards.goalSpeedBonus > 0f)
            {
                // Use the last toucher's elapsed time as the scoring-side anchor -
                // it represents the possession that actually ended the episode.
                int anchorStep = _lastToucher != null ? _lastToucher.StepCount : StepCount;
                int elapsed = Mathf.Clamp(anchorStep, 0, maxSteps);
                float secsLeft = (maxSteps - elapsed) * Time.fixedDeltaTime;
                bonus = rewards.goalSpeedBonus * secsLeft;
            }

            foreach (var agent in agents)
            {
                if (agent.team == concedingTeam)
                {
                    agent.AddReward(rewards.goalConceded);
                }
                else if (agent == _lastToucher && _lastToucher.team == scoringTeam)
                {
                    agent.AddReward(rewards.goalScorer + bonus);
                }
                else if (agent == _previousToucher && _previousToucher.team == scoringTeam)
                {
                    agent.AddReward(rewards.assist + bonus);
                }
                else
                {
                    agent.AddReward(rewards.teamBaselineVictory + bonus);
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

            // Opponent curriculum: a full-strength bot beats a fresh policy so
            // reliably that the brain never sees a goal to learn from, so the
            // difficulty ramps instead. Applied at kickoff so a lesson change
            // never lands mid-play.
            CurrentBotStrength = Academy.Instance.EnvironmentParameters
                .GetWithDefault("bot_strength", defaultBotStrength);
            for (int botIndex = 0; botIndex < _bots.Count; botIndex++)
            {
                if (_bots[botIndex] != null) _bots[botIndex].SetStrength(CurrentBotStrength);
            }

            if (ball != null)
            {
                Vector2 jitter = Random.insideUnitCircle * ballSpawnJitter;
                ball.transform.position = _ballSpawn + (Vector3)jitter;
                ball.linearVelocity = Vector2.zero;
                ball.angularVelocity = 0f;
                // Domain randomization: pitch conditions vary slightly every episode.
                ball.linearDamping = Random.Range(ballDampingRange.x, ballDampingRange.y);
            }

            EnsureSpawnCache();

            foreach (var agent in agents)
            {
                if (randomizeSpawns)
                {
                    // Anywhere in the agent's own half, clear of walls and center line.
                    float sign = agent.team == Agent_Soccer.Team.Blue ? -1f : 1f;
                    Vector2 local = new(
                        Random.Range(-pitchHalfExtents.x + 1.5f, pitchHalfExtents.x - 1.5f),
                        sign * Random.Range(1.5f, pitchHalfExtents.y - 1.5f));
                    agent.transform.position = transform.position + (Vector3)local;
                }
                else
                {
                    Vector3 basePos = _spawnPositions[agent];
                    agent.transform.position = basePos + (Vector3)(Random.insideUnitCircle * 0.75f);
                }
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
            PitchReconfigured?.Invoke(goal);
        }

        // ── Queries used by agent observations ──────────────────────────────

        public Vector2 GetGoalPosition(Agent_Soccer.Team ownerTeam) =>
            GetGoalTransform(ownerTeam) is { } t ? (Vector2)t.position : Vector2.zero;

        public Transform GetGoalTransform(Agent_Soccer.Team ownerTeam) =>
            ownerTeam == Agent_Soccer.Team.Blue ? blueGoal : redGoal;

        /// <summary>First teammate of the given agent, or null in 1v1.</summary>
        /// <summary>
        /// The teammate this agent observes. The brain contract carries exactly one
        /// teammate slot (4 of the 18 vector observations), so on squads larger than
        /// two we hand it the NEAREST teammate - the one whose position actually
        /// matters for spacing decisions. At 2v2 this is identical to the old
        /// "first other same-team agent", so trained brains are unaffected.
        /// </summary>
        public Agent_Soccer GetTeammate(Agent_Soccer self)
        {
            Agent_Soccer nearest = null;
            float bestSqr = float.MaxValue;
            for (int agentIndex = 0; agentIndex < agents.Count; agentIndex++)
            {
                var other = agents[agentIndex];
                if (other == null || other == self || other.team != self.team) continue;
                if (other.Body == null || self.Body == null) { nearest = nearest != null ? nearest : other; continue; }
                float sqr = (other.Body.position - self.Body.position).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; nearest = other; }
            }
            return nearest;
        }

        /// <summary>
        /// Rebuild any missing spawn-cache entries instead of throwing.
        ///
        /// 2026-08-04: `Start` fills these once, but a domain reload while play mode is
        /// running - which is what "Enter Play Mode Options -> DisableDomainReload" causes -
        /// wipes non-serialized state WITHOUT re-running Start. `agents` is serialized so it
        /// comes back populated, while these dictionaries come back empty. ResetPitch then
        /// threw KeyNotFoundException on `_spawnRotations[agent]` every FixedUpdate, so every
        /// episode reset aborted, `_episodeEnding` stayed true and the pitch froze: agents
        /// motionless, all four actions exactly 0, stamina untouched. It reads as "the brain
        /// is broken" and is not - it is an editor-configuration fault with no visible cause.
        ///
        /// The setting must stay off (UNITY_RULES / ML-Agents Academy needs the reload), but
        /// a reset loop should degrade rather than brick the pitch, and it should say so once.
        /// </summary>
        void EnsureSpawnCache()
        {
            for (int agentIndex = 0; agentIndex < agents.Count; agentIndex++)
            {
                var agent = agents[agentIndex];
                if (agent == null) continue;
                if (_spawnPositions.ContainsKey(agent) && _spawnRotations.ContainsKey(agent)) continue;

                if (!_spawnCacheWarned)
                {
                    _spawnCacheWarned = true;
                    Debug.LogWarning(
                        "[Env] Spawn cache was missing entries and has been rebuilt from current " +
                        "transforms. This normally means a domain reload happened during play mode: " +
                        "check Project Settings > Editor > Enter Play Mode Settings and keep domain " +
                        "reload ON. Spawns for this episode may be off; later episodes are correct.");
                }
                _spawnPositions[agent] = agent.transform.position;
                _spawnRotations[agent] = agent.transform.rotation;
            }
        }

        /// <summary>
        /// Opponents of <paramref name="self"/>, nearest first, written into
        /// <paramref name="buffer"/>. Returns how many slots were filled; the caller
        /// zero-pads the rest so one brain contract covers 1v1, 2v2 and 3v3.
        ///
        /// Added 2026-08-04: until then CollectObservations carried no opponent term at
        /// all, so a policy's only view of an opponent was the 11-ray sensor - which
        /// guarantees detection only within ~1.9 units (0.6% of the pitch) and cannot
        /// separate friend from foe, since every player carries the "Agent" tag.
        /// Agent_HeuristicBot meanwhile reads nearestOpponent straight off the
        /// transforms at unlimited range. That asymmetry, not training budget, is what
        /// pinned five runs (5M-30M steps) to a flat 16-17% win rate.
        /// </summary>
        public int GetOpponents(Agent_Soccer self, Agent_Soccer[] buffer)
        {
            if (buffer == null || self == null) return 0;
            int count = 0;
            for (int agentIndex = 0; agentIndex < agents.Count; agentIndex++)
            {
                var other = agents[agentIndex];
                if (other == null || other.team == self.team || other.Body == null) continue;
                if (count < buffer.Length)
                {
                    buffer[count++] = other;
                    continue;
                }
                // Buffer full: keep the nearest `buffer.Length` opponents by swapping
                // out the current farthest. No allocation, no LINQ (performance.md).
                if (self.Body == null) continue;
                float incoming = (other.Body.position - self.Body.position).sqrMagnitude;
                int farthest = -1;
                float worst = incoming;
                for (int slot = 0; slot < count; slot++)
                {
                    float sqr = (buffer[slot].Body.position - self.Body.position).sqrMagnitude;
                    if (sqr > worst) { worst = sqr; farthest = slot; }
                }
                if (farthest >= 0) buffer[farthest] = other;
            }

            // Insertion sort by distance, nearest first. `count` is 2-3 in practice, so
            // this is cheaper than any comparer-based sort and allocates nothing.
            if (self.Body != null)
            {
                for (int i = 1; i < count; i++)
                {
                    var key = buffer[i];
                    float keySqr = (key.Body.position - self.Body.position).sqrMagnitude;
                    int j = i - 1;
                    while (j >= 0 &&
                           (buffer[j].Body.position - self.Body.position).sqrMagnitude > keySqr)
                    {
                        buffer[j + 1] = buffer[j];
                        j--;
                    }
                    buffer[j + 1] = key;
                }
            }
            return count;
        }

        /// <summary>
        /// Rescale the pitch for a given squad size. Every child except the ball and
        /// the players is transformed proportionally (position and size), so walls,
        /// corner cushions, goal mouths and the backdrop all stay consistent with
        /// however the pitch was authored. Ball and player bodies keep their real
        /// dimensions - a futsal ball is a futsal ball on any pitch.
        ///
        /// Must run before Start captures spawn positions, so Agent_MatchLoader
        /// (order -60) calls it from Awake.
        /// </summary>
        public void ResizePitch(Vector2 newHalfExtents)
        {
            if (pitchHalfExtents.x <= 0.01f || pitchHalfExtents.y <= 0.01f) return;
            Vector2 ratio = new(
                newHalfExtents.x / pitchHalfExtents.x,
                newHalfExtents.y / pitchHalfExtents.y);
            if (Mathf.Approximately(ratio.x, 1f) && Mathf.Approximately(ratio.y, 1f)) return;

            foreach (Transform child in transform)
            {
                // Players are positioned by the spawn logic; the ball is FIFA-spec.
                if (child.GetComponent<Agent_Soccer>() != null) continue;
                if (ball != null && child == ball.transform) continue;

                Vector3 p = child.localPosition;
                child.localPosition = new Vector3(p.x * ratio.x, p.y * ratio.y, p.z);
                Vector3 s = child.localScale;
                child.localScale = new Vector3(s.x * ratio.x, s.y * ratio.y, s.z);
            }

            pitchHalfExtents = newHalfExtents;
            defaultGoalWidth = Agent_PitchSizing.GoalWidthFor(newHalfExtents);

            ApplyBotBounds();
        }

        /// <summary>
        /// Keep the bot's steering clamp inside the current pitch. Called from Start
        /// (once the bot list is cached) and again after any resize - a bot still
        /// clamping to the old pitch would refuse to chase into the new corners.
        /// </summary>
        void ApplyBotBounds()
        {
            Vector2 bounds = pitchHalfExtents - new Vector2(1f, 1f);
            if (_bots.Count == 0)
            {
                // Resize can land before Start has cached the list.
                var found = GetComponentsInChildren<Agent_HeuristicBot>(true);
                for (int i = 0; i < found.Length; i++) found[i].interiorHalfExtents = bounds;
                return;
            }
            for (int botIndex = 0; botIndex < _bots.Count; botIndex++)
            {
                if (_bots[botIndex] != null) _bots[botIndex].interiorHalfExtents = bounds;
            }
        }

        int TeamSize(Agent_Soccer.Team t)
        {
            int n = 0;
            foreach (var agent in agents) if (agent.team == t) n++;
            return n;
        }

        SimpleMultiAgentGroup GroupFor(Agent_Soccer.Team t) =>
            t == Agent_Soccer.Team.Blue ? _blueGroup : _redGroup;

        // Idempotent GoalFrame attach: ensures the colored mouth frame is present
        // on a goal transform without ever leaving duplicates across scene reloads.
        static void EnsureGoalFrame(Transform goal, Color tint)
        {
            if (goal == null) return;
            var frame = goal.GetComponent<Agent_GoalFrame>();
            if (frame == null) frame = goal.gameObject.AddComponent<Agent_GoalFrame>();
            frame.SetColor(tint);
        }
    }
}
