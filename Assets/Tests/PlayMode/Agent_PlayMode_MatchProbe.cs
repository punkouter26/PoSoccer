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
    /// Diagnostic (not a pass/fail gate): plays a real exhibition match through the
    /// menu entry point and reports what actually happened, so a lineup can be judged
    /// from numbers rather than from a Game view that barely renders when unfocused.
    ///
    /// Goes through Agent_MatchSetup exactly as Agent_MainMenu does (UNITY_RULES: the
    /// game always starts from SCN_Menu) - loading SCN_Exhibition directly would use
    /// its serialized defaults instead of the requested squads.
    /// </summary>
    public class Agent_PlayMode_MatchProbe
    {
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            Agent_MatchSetup.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator Match_TwoStandardsVsTwoBots()
        {
            yield return RunMatch("2 STANDARD vs 2 BOT", "STANDARD", "BOT", 2);
        }

        IEnumerator RunMatch(string label, string blueName, string redName, int squad)
        {
            // Menu first: it is the scene that references every Reward_Settings asset,
            // so they are loaded and findable without touching UnityEditor APIs.
            SceneManager.LoadScene("SCN_Menu");
            yield return null;
            yield return null;

            var profiles = Resources.FindObjectsOfTypeAll<Reward_Settings>();
            var blueProfile = profiles.FirstOrDefault(p => p.playerName == blueName);
            var redProfile = profiles.FirstOrDefault(p => p.playerName == redName);
            Assert.IsNotNull(blueProfile, $"no Reward_Settings named {blueName}");
            Assert.IsNotNull(redProfile, $"no Reward_Settings named {redName}");

            var blueSquad = new Reward_Settings[squad];
            var redSquad = new Reward_Settings[squad];
            for (int i = 0; i < squad; i++) { blueSquad[i] = blueProfile; redSquad[i] = redProfile; }
            Agent_MatchSetup.BlueSquad = blueSquad;
            Agent_MatchSetup.RedSquad = redSquad;
            Agent_MatchSetup.Applied = true;

            SceneManager.LoadScene("SCN_Exhibition");
            // Agent_MatchLoader (order -60) clones/destroys players to hit the requested
            // squad size, so env.agents is still empty for the first few frames. Sampling
            // it too early silently reports zero agents.
            for (int f = 0; f < 10; f++) yield return new WaitForFixedUpdate();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(env, "no Agent_EnvController in SCN_Exhibition");
            var ball = env.Ball;
            Assert.IsNotNull(ball, "no ball");

            int blueGoals = 0, redGoals = 0, stalemates = 0;
            System.Action<Agent_Soccer.Team?> onEnd = t =>
            {
                if (t == null) stalemates++;
                else if (t == Agent_Soccer.Team.Blue) blueGoals++;
                else redGoals++;
            };
            env.EpisodeEnded += onEnd;

            var agents = env.agents.Where(a => a != null).ToArray();
            int n = agents.Length;
            var prev = new Vector2[n];
            var path = new float[n];
            var sumBallDist = new double[n];
            var maxSpeed = new float[n];
            for (int i = 0; i < n; i++) prev[i] = agents[i].Body.position;

            const int STEPS = 6000;          // 60 s at the 0.01 fixed timestep
            int samples = 0;
            for (int step = 0; step < STEPS; step++)
            {
                yield return new WaitForFixedUpdate();
                for (int i = 0; i < n; i++)
                {
                    var b = agents[i].Body;
                    if (b == null) continue;
                    Vector2 p = b.position;
                    path[i] += Vector2.Distance(p, prev[i]);
                    prev[i] = p;
                    sumBallDist[i] += Vector2.Distance(p, ball.position);
                    float v = b.linearVelocity.magnitude;
                    if (v > maxSpeed[i]) maxSpeed[i] = v;
                }
                samples++;
            }
            env.EpisodeEnded -= onEnd;

            float simSeconds = samples * Time.fixedDeltaTime;
            var sb = new StringBuilder();
            sb.AppendLine($"[MATCH-PROBE] {label}");
            sb.AppendLine($"  pitch={env.PitchHalfExtents.x * 2f:0.0} x {env.PitchHalfExtents.y * 2f:0.0} m " +
                          $"(training pitch is 36 x 54) | botStrength={env.CurrentBotStrength:0.00} | {simSeconds:0}s simulated");
            sb.AppendLine($"  SCORE  blue {blueGoals} - {redGoals} red   (stalemates {stalemates})");
            for (int i = 0; i < n; i++)
            {
                var a = agents[i];
                var bp = a.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                string brain = bp == null || bp.Model == null ? "BOT(heuristic)" : bp.Model.name;
                sb.AppendLine($"  {a.name,-12} {a.team,-4} {brain,-14} " +
                              $"traveled={path[i]:0.0}m  meanSpeed={path[i] / simSeconds:0.00}m/s  " +
                              $"maxSpeed={maxSpeed[i]:0.00}m/s  meanDistToBall={sumBallDist[i] / samples:0.0}m");
            }
            Debug.Log(sb.ToString());

            Assert.Greater(samples, 0);
        }
    }
}
