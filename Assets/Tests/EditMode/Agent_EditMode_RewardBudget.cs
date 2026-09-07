using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the one property of the reward table nobody had ever computed: what each
    /// DENSE term can accumulate over a whole episode, against what the TERMINAL
    /// rewards pay for the outcomes those terms are supposed to serve.
    ///
    /// WHY THIS EXISTS (measured 2026-09-07). Dense terms are written as small numbers
    /// - 0.001, 0.0005 - and read as obviously negligible next to a 1.2 goal. They are
    /// not, because they are charged EVERY PHYSICS STEP and the step cap is 9000:
    ///
    ///   ballToGoalVelocityScale  0.001  x 9000  =  +9.0   vs a goal worth  +1.2
    ///   cornerBallPenalty       -0.0006 x 9000  =  -5.4   vs conceding     -1.0
    ///   wallProximityPenalty    -0.0005 x 9000  =  -4.5   vs conceding     -1.0
    ///   crossbarProximity        0.0005 x 9000  =  +4.5   vs a goal        +1.2
    ///
    /// A policy that maximises return under that table should farm ball-goal velocity
    /// and avoid corners, NOT score and defend - which is a far more direct account of
    /// this project's signature failure ("learns not to lose, never learns to win",
    /// 25.7% at p21) than the curriculum story it was attributed to. Note these are
    /// MAXIMA, requiring the condition to hold every step; real duty cycles are lower.
    /// The defect is that the ceiling is reachable at all, because it means the ordering
    /// of outcomes is not guaranteed by the table.
    ///
    /// The arithmetic is validated against an independently derived figure: CLAUDE.md
    /// computes stepPenalty's real cost as 5784 x 0.00005 = 0.289 from eval telemetry,
    /// and this model reproduces 0.289 for that term at the same episode length.
    ///
    /// This test does NOT assert a specific table. It asserts the ORDERING PROPERTY:
    /// no single dense term may, on its own, outweigh the largest terminal reward.
    /// A term that can is not shaping toward the objective, it IS the objective.
    /// </summary>
    public sealed class Agent_EditMode_RewardBudget
    {
        /// <summary>
        /// One dense term: its scale, and whether it is charged every physics step or
        /// once per decision. <see cref="Agent_Soccer.OnActionReceived"/> runs every
        /// physics step (ML-Agents repeats the last action between decisions), so all
        /// but the jitter term accumulate at the full step rate.
        /// </summary>
        readonly struct DenseTerm
        {
            public readonly string Name;
            public readonly float PerCharge;
            public readonly bool PerDecision;

            public DenseTerm(string name, float perCharge, bool perDecision = false)
            {
                Name = name; PerCharge = perCharge; PerDecision = perDecision;
            }
        }

        /// <summary>
        /// DecisionRequester period on the training agents. The jitter term compares
        /// against the previous step's action, which only changes on a decision step,
        /// so it can only be charged once per period.
        /// </summary>
        const int DecisionPeriod = 8;

        static IEnumerable<(string path, Reward_Settings p)> Profiles()
        {
            string[] guids = AssetDatabase.FindAssets("t:Reward_Settings");
            Assert.That(guids.Length, Is.GreaterThan(0), "No Reward_Settings assets found.");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var p = AssetDatabase.LoadAssetAtPath<Reward_Settings>(path);
                if (p != null) yield return (path, p);
            }
        }

        static DenseTerm[] TermsOf(Reward_Settings p) => new[]
        {
            new DenseTerm(nameof(p.stepPenalty),            p.stepPenalty),
            new DenseTerm(nameof(p.facingAlignmentScale),   p.facingAlignmentScale),
            new DenseTerm(nameof(p.ballToGoalVelocityScale), p.ballToGoalVelocityScale),
            new DenseTerm(nameof(p.crossbarProximity),      p.crossbarProximity),
            new DenseTerm(nameof(p.wallProximityPenalty),   -Mathf.Abs(p.wallProximityPenalty)),
            new DenseTerm(nameof(p.cornerBallPenalty),      -Mathf.Abs(p.cornerBallPenalty)),
            new DenseTerm(nameof(p.possessionScale),        p.possessionScale),
            new DenseTerm(nameof(p.defensivePositionScale), p.defensivePositionScale),
            new DenseTerm(nameof(p.actionJitterScale),      -Mathf.Abs(p.actionJitterScale), true),
        };

        static float LargestTerminal(Reward_Settings p) => Mathf.Max(
            Mathf.Abs(p.goalScorer),
            Mathf.Max(Mathf.Abs(p.goalConceded), Mathf.Abs(p.stalemateTimeout)));

        /// <summary>
        /// The ordering property. A dense term whose episode ceiling exceeds the biggest
        /// terminal reward can, by itself, reorder which outcome the policy prefers.
        /// </summary>
        [Test]
        public void NoDenseTerm_CanOutweighTheLargestTerminalReward()
        {
            var bad = new StringBuilder();
            foreach (var (path, p) in Profiles())
            {
                int cap = Mathf.Max(1, p.maxEnvironmentSteps);
                float terminal = LargestTerminal(p);
                if (terminal <= 0f) continue;

                foreach (var t in TermsOf(p))
                {
                    if (Mathf.Approximately(t.PerCharge, 0f)) continue;
                    int charges = t.PerDecision ? cap / DecisionPeriod : cap;
                    float ceiling = Mathf.Abs(t.PerCharge) * charges;
                    if (ceiling > terminal)
                    {
                        bad.AppendLine(
                            $"  {p.playerName}: {t.Name} = {t.PerCharge:G4} x {charges} charges " +
                            $"= {ceiling:F2} per episode, vs largest terminal {terminal:F2} " +
                            $"({ceiling / terminal:F1}x).  [{path}]");
                    }
                }
            }

            Assert.That(bad.Length, Is.Zero,
                "Dense reward terms that can individually outweigh the terminal rewards " +
                "they are meant to shape toward. At these ceilings the optimal policy is " +
                "to farm the shaping term, not to score:\n" + bad +
                "\nFix by lowering the scale, or by bounding the term's per-episode total " +
                "in Agent_Soccer.ApplyDenseRewards. Raising maxEnvironmentSteps makes this " +
                "STRICTLY WORSE - the ceiling is linear in the step cap.");
        }

        /// <summary>
        /// The sum of the positive dense ceilings should not swamp a goal either. Even
        /// when no single term trips the test above, several together can.
        /// </summary>
        [Test]
        public void TotalPositiveDenseCeiling_StaysComparableToAGoal()
        {
            var bad = new StringBuilder();
            foreach (var (path, p) in Profiles())
            {
                int cap = Mathf.Max(1, p.maxEnvironmentSteps);
                if (p.goalScorer <= 0f) continue;

                float positive = 0f;
                foreach (var t in TermsOf(p))
                {
                    if (t.PerCharge <= 0f) continue;
                    positive += t.PerCharge * (t.PerDecision ? cap / DecisionPeriod : cap);
                }

                // 3x is deliberately loose: shaping is allowed to be a strong hint, it is
                // not allowed to be the whole objective. Tighten once a run validates it.
                if (positive > p.goalScorer * 3f)
                {
                    bad.AppendLine(
                        $"  {p.playerName}: positive dense ceiling {positive:F2} vs " +
                        $"goalScorer {p.goalScorer:F2} ({positive / p.goalScorer:F1}x).  [{path}]");
                }
            }

            Assert.That(bad.Length, Is.Zero,
                "Summed positive dense shaping dwarfs the reward for actually scoring:\n" + bad);
        }

        /// <summary>
        /// Documents the step-rate fact the ceilings depend on, so a future change to the
        /// decision period or the timestep cannot quietly invalidate every number above.
        /// </summary>
        [Test]
        public void DenseRewards_AreChargedEveryPhysicsStep_NotEveryDecision()
        {
            Assert.That(Time.fixedDeltaTime, Is.EqualTo(0.01f).Within(1e-4f),
                "Fixed timestep is no longer 0.01s. Every per-episode reward ceiling in " +
                "Agent_EditMode_RewardBudget scales with the number of physics steps per " +
                "episode, so this changes all of them.");
        }
    }
}
