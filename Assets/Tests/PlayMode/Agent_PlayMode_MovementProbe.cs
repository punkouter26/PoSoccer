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
            // Blue-trained model used to run it badly out of distribution, which read as
            // "the policy cannot move", so probe the side the policy actually trained on.
            //
            // 2026-08-28: the out-of-distribution half of that reasoning is now largely
            // obsolete. Self velocity, the ball and every opponent/teammate term are emitted
            // in the agent's BODY frame (Agent_Soccer.ToBodyFrame), and the goal terms were
            // already team-relative, so a Blue-trained policy no longer sees a mirrored world
            // when it is driven as Red. Keep probing the trained side anyway - it is still
            // the honest measurement, and the side choice now costs nothing either way.
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

            float sumAbsMove = 0f, sumAbsLat = 0f, sumAbsTurn = 0f, sumBoost = 0f;
            float prevMove = 0f, prevTurn = 0f, peakMove = 0f;
            int flipsMove = 0, flipsTurn = 0;

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

                // Split "collapsed policy" from "thrashing policy": a collapsed one
                // emits near-zero magnitudes, a thrashing one emits large values whose
                // sign keeps flipping so they cancel. Mean |action| tells them apart;
                // the sign-flip counts say how much of the output is self-cancelling.
                Vector4 a = red.LastRawActions;
                sumAbsMove += Mathf.Abs(a.x); sumAbsLat += Mathf.Abs(a.y);
                sumAbsTurn += Mathf.Abs(a.z); sumBoost += a.w;
                if (a.x * prevMove < 0f) flipsMove++;
                if (a.z * prevTurn < 0f) flipsTurn++;
                prevMove = a.x; prevTurn = a.z;
                if (Mathf.Abs(a.x) > peakMove) peakMove = Mathf.Abs(a.x);

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
            sb.AppendLine($"  RAW ACTIONS (pre-gain) mean|move|={sumAbsMove / samples:0.000}  " +
                          $"mean|lat|={sumAbsLat / samples:0.000}  " +
                          $"mean|turn|={sumAbsTurn / samples:0.000}  " +
                          $"meanBoost={sumBoost / samples:0.000}");
            sb.AppendLine($"  peak|move|={peakMove:0.000}  " +
                          $"signFlips move={flipsMove} turn={flipsTurn} of {samples} steps " +
                          $"=> {(sumAbsMove / samples < 0.15f ? "COLLAPSED (near-zero output)" : flipsMove > samples / 8 ? "THRASHING (large, sign-flipping)" : "healthy magnitudes")}");
            Debug.Log(sb.ToString());
        }
    }
}
