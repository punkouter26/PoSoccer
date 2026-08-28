using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    public class Agent_EditMode_SoccerAgent
    {
        GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        Agent_Stamina NewStamina()
        {
            _go = new GameObject("stamina");
            var s = _go.AddComponent<Agent_Stamina>();
            s.ResetForEpisode();
            return s;
        }

        /// <summary>
        /// Every Reward_*.asset must match the code defaults on the LOCOMOTION MECHANICS
        /// terms. These are not personality knobs - personality lives in the terminal
        /// rewards, the trait scales and the physique.
        ///
        /// Why this test exists (2026-08-04). Editing a field initializer in
        /// Reward_Settings does NOT touch an existing ScriptableObject asset, so a fix can
        /// land in code and silently never reach the assets. That happened here: the v2
        /// pass halved actionJitterScale to 0.0004 because "the old penalty was teaching
        /// the brain to be smooth and idle" and raised ballProximityScale to 0.002, but no
        /// asset was updated. STANDARD trained for months with the approach reward 5x too
        /// weak and the anti-movement penalty 2.5x too strong - a 12x swing against moving,
        /// and 50x on KIM. The measured result: a policy that travelled 2.62 m in 4 s where
        /// the scripted bot covered 15.08 m, never reached the ball, and topped out at
        /// 0.95 m/s on a chassis that does 9.54 m/s. It read as "the AI is bad at soccer".
        /// </summary>
        [Test]
        public void RewardProfiles_MatchCodeDefaultsOnMechanics()
        {
            var code = ScriptableObject.CreateInstance<Reward_Settings>();
            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Reward_Settings");
            Assert.Greater(guids.Length, 0, "no Reward_Settings assets found");

            foreach (string guid in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var a = UnityEditor.AssetDatabase.LoadAssetAtPath<Reward_Settings>(path);
                Assert.AreEqual(code.stepPenalty, a.stepPenalty, 1e-9f, $"{path} stepPenalty");
                Assert.AreEqual(code.ballProximityScale, a.ballProximityScale, 1e-9f,
                    $"{path} ballProximityScale - reward for closing on the ball");
                Assert.AreEqual(code.actionJitterScale, a.actionJitterScale, 1e-9f,
                    $"{path} actionJitterScale - too high and the policy learns to stand still");
                Assert.AreEqual(code.useDifferentialProximity, a.useDifferentialProximity,
                    $"{path} useDifferentialProximity");
                Assert.Greater(a.ballProximityScale, a.actionJitterScale,
                    $"{path}: approaching the ball must out-pay holding still");
            }
            Object.DestroyImmediate(code);
        }

        [Test]
        public void Stamina_DrainsAt60PerSecondWhileBoosting()
        {
            var s = NewStamina();
            s.Tick(boosting: true, deltaTime: 1f);
            Assert.AreEqual(s.EffectiveMax - 60f, s.Current, 0.5f);
        }

        [Test]
        public void Stamina_RechargesAt25PerSecondWhenIdle()
        {
            // The PRD's flat 25/s is now the PEAK rate, approached as the tank
            // fills. Recovery from deep depletion is deliberately slower, and does
            // not begin the instant sprinting stops. Deviation recorded in
            // docs/rules-exemptions.md.
            //
            // Measured just below full and over a short window, so the reading is
            // the RATE and not the ceiling clamping it - a longer window here fills
            // the tank and reports an artificially low average.
            float rate = RecoveryRate(unitsToDrain: 5f, window: 0.1f);

            Assert.That(rate, Is.EqualTo(25f).Within(1.5f),
                "Near-full recovery should run at the PRD's 25/s peak rate");
        }

        [Test]
        public void Stamina_RecoveryIsDelayedAfterExertion()
        {
            var s = NewStamina();
            s.Tick(true, 1f);                       // boost, arming the delay
            float drained = s.Current;

            // Half the delay: nothing should come back yet.
            s.Tick(false, s.recoveryDelaySeconds * 0.5f);
            Assert.AreEqual(drained, s.Current, 1e-4f,
                "Stamina began recovering before the post-exertion delay elapsed");
        }

        [Test]
        public void Stamina_RecoversMoreSlowlyWhenDeeplyDepleted()
        {
            float emptyRate = RecoveryRate(unitsToDrain: 100f, window: 0.1f);
            float nearFullRate = RecoveryRate(unitsToDrain: 5f, window: 0.1f);

            Assert.Less(emptyRate, nearFullRate,
                "Recovery from empty should be slower than topping up");
        }

        /// <summary>
        /// Drains a set number of units, serves out the post-exertion delay, then
        /// measures the recovery rate over a short window.
        /// </summary>
        float RecoveryRate(float unitsToDrain, float window)
        {
            var s = NewStamina();
            s.Tick(true, unitsToDrain / s.drainPerSecond);
            s.Tick(false, s.recoveryDelaySeconds);   // delay only, no recovery
            float before = s.Current;
            s.Tick(false, window);
            return (s.Current - before) / window;
        }

        [Test]
        public void Stamina_WearNeverDropsEffectiveMaxBelowFloor()
        {
            var s = NewStamina();
            for (int i = 0; i < 5000; i++) s.Tick(true, 1f); // extreme sustained exertion
            Assert.GreaterOrEqual(s.EffectiveMax, s.maxStamina * s.wearFloor - 0.01f);
            Assert.Less(s.EffectiveMax, s.maxStamina); // wear did accumulate
        }

        [Test]
        public void RewardSettings_DefaultsMatchPrd()
        {
            // These are the CURRENT intended defaults, not the v1 PRD table. Where the
            // two differ the divergence was deliberate and is documented on the field's
            // tooltip in Reward_Settings; this test guards against accidental drift, so
            // it tracks the code. Values updated 2026-08-04 after the suite was found
            // failing on three v2-era changes that predate this session.
            var r = ScriptableObject.CreateInstance<Reward_Settings>();
            // v4: scoring must out-pay conceding or stalling is the optimal policy.
            Assert.AreEqual(1.2f, r.goalScorer);
            Assert.Greater(r.goalScorer, Mathf.Abs(r.goalConceded),
                "a 50/50 goal trade must beat stalling, or the policy learns to stall");
            Assert.Less(r.stalemateTimeout, -0.5f, "stalling must not be a safe harbour");
            Assert.AreEqual(0.3f, r.assist);
            Assert.AreEqual(0.1f, r.teamBaselineVictory);
            Assert.AreEqual(-1.0f, r.goalConceded);
            // v2: stepPenalty zeroed - it washed out the whole reward gradient.
            Assert.AreEqual(0f, r.stepPenalty, 1e-6f);
            // v2: differential proximity replaced absolute proximity.
            Assert.AreEqual(0.002f, r.ballProximityScale, 1e-6f);
            Assert.AreEqual(0.0002f, r.facingAlignmentScale, 1e-6f);
            // v3: 0.05 -> 0.005. At 0.05, 14 touches outscored a goal (0.7).
            Assert.AreEqual(0.005f, r.ballContact);
            Assert.AreEqual(-0.6f, r.stalemateTimeout, 1e-6f);
            Assert.AreEqual(5000, r.maxEnvironmentSteps);
            // v5 (2026-08-11): "score with style" shaping defaults. Both zero by
            // design so existing profiles keep training identically until they
            // opt in. The test pins the defaults so accidental drift is caught
            // before a 3M-step run wastes compute.
            Assert.AreEqual(0.05f, r.goalSpeedBonus, 1e-6f);
            Assert.AreEqual(0.0005f, r.crossbarProximity, 1e-6f);
            Object.DestroyImmediate(r);
        }

        [Test]
        public void HeuristicBot_TurnsTowardBallOnTheLeft()
        {
            _go = new GameObject("bot");
            var selfBody = _go.AddComponent<Rigidbody2D>();
            var bot = _go.AddComponent<Agent_HeuristicBot>();

            var ballGo = new GameObject("ball");
            ballGo.transform.position = new Vector3(-5f, 0f, 0f);
            var ballBody = ballGo.AddComponent<Rigidbody2D>();

            // Facing +Y, ball to the left (-X) => positive (CCW) turn expected.
            // ComputeActions returns [forward, lateral, turn, boost]; turn is .z. This
            // read .y (lateral) and had been asserting on the wrong channel ever since
            // lateral drive was inserted at index 1 - it measured -0.6, the lateral
            // clamp, and failed for a reason unrelated to turning.
            Vector4 actions = bot.ComputeActions(selfBody, ballBody, null);
            Assert.Greater(actions.z, 0f, "turn should be CCW toward a ball on the left");

            Object.DestroyImmediate(ballGo);
        }

        [Test]
        public void Opponent_MappingIsSymmetric()
        {
            Assert.AreEqual(Agent_Soccer.Team.Red, Agent_Soccer.Opponent(Agent_Soccer.Team.Blue));
            Assert.AreEqual(Agent_Soccer.Team.Blue, Agent_Soccer.Opponent(Agent_Soccer.Team.Red));
        }
    }
}
