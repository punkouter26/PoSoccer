using System.Collections;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Diagnostic probe (not a pass/fail gate): measures how the agent body
    /// actually moves so locomotion realism can be judged from numbers instead
    /// of from an unfocused Game view. Reports via Debug.Log; the asserts are
    /// deliberately loose so this never blocks CI.
    /// </summary>
    public class Agent_PlayMode_MovementProbe
    {
        Agent_EnvController _env;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            _env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(_env);
        }

        /// <summary>
        /// Pure-physics replica of the drive model (mass/damping/force only, no
        /// policy) so the body's acceleration curve is isolated from decisions.
        /// </summary>
        [UnityTest]
        public IEnumerator Probe_A_StraightLineLocomotion()
        {
            var go = new GameObject("probe");
            var rb = go.AddComponent<Rigidbody2D>();
            rb.mass = 75f;
            rb.linearDamping = 0.7f;
            rb.gravityScale = 0f;

            const float MoveForce = 236f;
            const float Boost = 2.2f;
            const float Slew = 1200f;

            var sb = new StringBuilder();
            sb.AppendLine("[PROBE-A] straight-line locomotion (75kg, damping 0.7, traction-limited)");

            foreach (float mult in new[] { 1f, Boost })
            {
                rb.position = Vector2.zero;
                rb.linearVelocity = Vector2.zero;
                float drive = 0f;
                float t = 0f, t63 = -1f, t95 = -1f, top = 0f;
                float target = MoveForce * mult;

                for (int i = 0; i < 500; i++)   // 5 s @ 100 Hz
                {
                    drive = Mathf.MoveTowards(drive, target, Slew * Time.fixedDeltaTime);
                    rb.AddForce(Vector2.up * drive);
                    yield return new WaitForFixedUpdate();
                    t += Time.fixedDeltaTime;
                    float v = rb.linearVelocity.magnitude;
                    if (v > top) top = v;
                }

                // Re-run to time the approach to the measured terminal speed.
                rb.position = Vector2.zero;
                rb.linearVelocity = Vector2.zero;
                drive = 0f; t = 0f;
                for (int i = 0; i < 500; i++)
                {
                    drive = Mathf.MoveTowards(drive, target, Slew * Time.fixedDeltaTime);
                    rb.AddForce(Vector2.up * drive);
                    yield return new WaitForFixedUpdate();
                    t += Time.fixedDeltaTime;
                    float v = rb.linearVelocity.magnitude;
                    if (t63 < 0f && v >= top * 0.63f) t63 = t;
                    if (t95 < 0f && v >= top * 0.95f) t95 = t;
                }

                sb.AppendLine($"  x{mult:0.0} force={target:0}N  top={top:0.00} m/s  " +
                              $"t63={t63:0.00}s  t95={t95:0.00}s");
            }
            Object.Destroy(go);
            Debug.Log(sb.ToString());
            Assert.Pass();
        }

        /// <summary>
        /// Same chase, but forced onto the rule-based bot so the trained policy
        /// can be compared against the baseline it is supposed to beat.
        /// </summary>
        [UnityTest]
        public IEnumerator Probe_C_ChaseEfficiency_HeuristicBot()
        {
            foreach (var a in _env.agents)
            {
                var p = a.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                if (p != null) p.BehaviorType = Unity.MLAgents.Policies.BehaviorType.HeuristicOnly;
            }
            yield return new WaitForFixedUpdate();
            yield return RunChase("PROBE-C (forced heuristic bot)");
        }

        /// <summary>
        /// Real decision pipeline chasing a ball: measures path efficiency and
        /// how much heading churn the tank-style steering costs.
        /// </summary>
        [UnityTest]
        public IEnumerator Probe_B_ChaseEfficiency()
        {
            yield return RunChase("PROBE-B (as shipped)");
        }

        /// <summary>
        /// Chase efficiency for the BLUE agent - the side the trained policy occupies in
        /// both training and eval. This is the honest read on "can the brain reach a ball".
        /// </summary>
        [UnityTest]
        public IEnumerator Probe_D_ChaseEfficiency_BlueTrainedSide()
        {
            yield return RunChase("PROBE-D (blue = trained side)", Agent_Soccer.Team.Blue);
        }

        /// <summary>
        /// Single-variable isolation probe (2026-08-11): does the policy reach the
        /// ball when <see cref="Reward_Settings.actionJitterScale"/> is set to 0?
        /// Phase 10 hit the same plateau as p5-p9 (16.4% over 1000 ep) but the
        /// chase probe showed top speed 2x and travel 3x — the policy learned to
        /// thrash, not to drive. actionJitterScale penalizes per-step action
        /// change, which trains smoothness; a policy trained smooth cannot commit
        /// to "cut toward the ball" and ends up spinning. This probe mutates the
        /// in-memory reward profile (no asset write) so the chase runs with the
        /// anti-twitch reward neutralised and the chassis limit becomes the only
        /// shaping. If it now reaches the ball, jitter is the bottleneck.
        /// </summary>
        [UnityTest]
        public IEnumerator Probe_E_ChaseEfficiency_JitterZeroed()
        {
            var mover = _env.agents.First(a => a.team == Agent_Soccer.Team.Blue);
            var rs = mover.rewards;
            Assert.IsNotNull(rs, "Blue agent has no Reward_Settings assigned");

            float saved = rs.actionJitterScale;
            rs.actionJitterScale = 0f;
            try
            {
                yield return RunChase("PROBE-E (jitter = 0, blue)", Agent_Soccer.Team.Blue);
            }
            finally
            {
                rs.actionJitterScale = saved;
            }
        }

        IEnumerator RunChase(string label,
            Agent_Soccer.Team mover = Agent_Soccer.Team.Red)
        {
            // 2026-08-04: the trained policy is ALWAYS Blue - scripts/train-phase1.ps1 sets
            // POSOCCER_OPPONENT=bot, which forces Red to HeuristicOnly. Measuring Red with a
            // Blue-trained model runs it out of distribution (self velocity, eye axis and
            // relBall are world-frame while the goal terms are team-relative), which reads
            // as "the policy cannot move". Probe the side the policy actually trained on.
            var red = _env.agents.First(a => a.team == mover);
            var blue = _env.agents.First(a => a.team != mover);

            // Park the other agent out of the way so it cannot interfere.
            blue.Body.position = new Vector2(-5f, -8f);

            red.Body.position = new Vector2(0f, -6f);
            red.Body.linearVelocity = Vector2.zero;
            red.transform.rotation = Quaternion.identity;

            var ball = _env.Ball;
            ball.position = new Vector2(3f, 4f);
            ball.linearVelocity = Vector2.zero;

            Vector2 start = red.Body.position;
            float straight = Vector2.Distance(start, ball.position);

            Vector2 prev = start;
            float prevHeading = red.transform.eulerAngles.z;
            float path = 0f, headingChurn = 0f, maxSpeed = 0f, sumSpeed = 0f;
            float arrival = -1f, t = 0f;
            int samples = 0;

            for (int i = 0; i < 400; i++)      // 4 s
            {
                yield return new WaitForFixedUpdate();
                t += Time.fixedDeltaTime;

                Vector2 p = red.Body.position;
                path += Vector2.Distance(p, prev);
                prev = p;

                float h = red.transform.eulerAngles.z;
                headingChurn += Mathf.Abs(Mathf.DeltaAngle(prevHeading, h));
                prevHeading = h;

                float v = red.Body.linearVelocity.magnitude;
                if (v > maxSpeed) maxSpeed = v;
                sumSpeed += v; samples++;

                if (arrival < 0f && Vector2.Distance(p, ball.position) < 1.0f) arrival = t;
            }

            var bp = red.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
            var botComp = red.GetComponent<Agent_HeuristicBot>();
            var dr = red.GetComponent<Unity.MLAgents.DecisionRequester>();

            var sb = new StringBuilder();
            sb.AppendLine($"[{label}] chase efficiency");
            sb.AppendLine($"  CONTROLLER behaviorType={bp?.BehaviorType} " +
                          $"model={(bp?.Model == null ? "NULL(heuristic bot)" : bp.Model.name)} " +
                          $"botComponent={(botComp == null ? "absent" : botComp.enabled ? "enabled" : "disabled")} " +
                          $"decisionPeriod={dr?.DecisionPeriod} takeActionsBetween={dr?.TakeActionsBetweenDecisions}");
            sb.AppendLine($"  straight={straight:0.00}m  traveled={path:0.00}m  " +
                          $"(needed {straight:0.00}m; covered {100f * path / straight:0}%)");
            sb.AppendLine($"  arrival={(arrival < 0f ? -1f : arrival):0.00}s  " +
                          $"maxSpeed={maxSpeed:0.00} m/s  meanSpeed={sumSpeed / samples:0.00} m/s");
            sb.AppendLine($"  headingChurn={headingChurn:0}deg over {t:0.0}s " +
                          $"({headingChurn / t:0} deg/s avg)");
            Debug.Log(sb.ToString());
        }
    }
}
