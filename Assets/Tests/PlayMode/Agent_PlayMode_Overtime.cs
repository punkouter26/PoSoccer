using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Covers the four features added 2026-09-12: sudden-death overtime, the shot
    /// tracer, the brain cam and the post-match dossier.
    ///
    /// THE LOAD-BEARING TEST IS <see cref="TrainingScene_HasNoneOfTheseComponents"/>,
    /// and it is load-bearing for a harder reason than its siblings in
    /// Agent_PlayMode_Broadcast. Those components only DRAW. Agent_Overtime MOVES
    /// THE WALLS AND WIDENS THE GOALS, and the benchmark's headline number is
    /// blueWins/episodes over a fixed episode count. A mechanism that converts
    /// stalemates into goals would inflate that number directly, and no field in
    /// any eval JSON would reveal it - the run id, the model path and the input
    /// count would all still be correct. "Training and evaluation never get this"
    /// has to be an assertion.
    ///
    /// THE SECOND LOAD-BEARING TEST IS
    /// <see cref="ShotTracer_PredictsWhereTheBallActuallyGoes"/>. The tracer draws a
    /// line claiming to know the future, on a scoreboard that sits next to an
    /// explicitly uncalibrated win-probability strip. This project has already
    /// published one retraction over a number that looked measured and was not, so
    /// the projection is stepped against the real ball rather than eyeballed.
    ///
    /// Private serialized fields are driven by reflection rather than by adding
    /// setters. .claude/rules/csharp-unity.md asks for the minimum viable
    /// visibility and for a named caller before anything becomes public; a test is
    /// not a reason to widen the runtime API of a component the game ships.
    ///
    /// Like the other spectator suites these drive SCN_Exhibition directly, which
    /// normal play must never do. Fine here: nothing under test reads the lineup.
    /// </summary>
    public class Agent_PlayMode_Overtime
    {
        /// <summary>
        /// Clear the global clock and lineup statics BEFORE each test, not only
        /// after.
        ///
        /// Every fixture here releases its holds on the way out, but a fixture whose
        /// test FAILS between an Acquire and its Release does not - Agent_PlayMode_
        /// Portrait's pause test is one Assert away from exactly that, and it is
        /// currently red for an unrelated reason. The leaked hold then belongs to a
        /// HUD that no longer exists, and the next fixture to wait on the clock
        /// waits forever. Measured: this fixture passed 13/13 alone and failed
        /// WaitForPlay with "holders: HUD.Pause" in the full suite.
        ///
        /// Releasing an orphan is correct rather than a workaround - the owner is
        /// destroyed - and it keeps this fixture's result a statement about this
        /// fixture rather than about what ran before it.
        /// </summary>
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            yield return null;
        }

        static IEnumerator LoadExhibition()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            yield return null;   // Awake / OnEnable
            yield return null;   // Start
            yield return null;   // components added in Awake reach their own Start
        }

        /// <summary>
        /// The opening countdown holds the clock through Agent_TimeFreeze, and every
        /// component here deliberately accrues nothing while it is held. Waiting it
        /// out is the difference between measuring the feature and measuring the
        /// countdown.
        /// </summary>
        static IEnumerator WaitForPlay()
        {
            float deadline = Time.realtimeSinceStartup + 8f;
            while (Agent_TimeFreeze.IsFrozen && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                $"Clock still frozen after 8 s; holders: {Agent_TimeFreeze.DescribeHolders()}");
        }

        static void Set(object target, string field, object value)
        {
            var info = target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(info, $"{target.GetType().Name} has no field '{field}' - " +
                                   "the test is out of date with the component.");
            info.SetValue(target, value);
        }

        /// <summary>
        /// Stop the players interfering without hiding them from the components
        /// under test. Deactivating the GameObject would take them out of the
        /// rosters these components iterate, which is a different scenario.
        /// </summary>
        static void ParkPlayers(Agent_EnvController env)
        {
            for (int i = 0; i < env.agents.Count; i++)
            {
                var agent = env.agents[i];
                if (agent == null || agent.Body == null) continue;
                agent.Body.linearVelocity = Vector2.zero;
                agent.Body.angularVelocity = 0f;
                agent.Body.simulated = false;
            }
        }

        // -- The gate --------------------------------------------------------

        [UnityTest]
        public IEnumerator TrainingScene_HasNoneOfTheseComponents()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            yield return null;

            Assert.IsNull(Object.FindAnyObjectByType<Agent_Overtime>(),
                "Agent_Overtime must never be installed in SCN_Training. It resizes the " +
                "pitch and widens the goals, so a training or eval run that got it would " +
                "convert stalemates into goals and inflate the benchmark's own headline " +
                "number with nothing in the eval JSON to reveal it.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_BrainCam>(),
                "Agent_BrainCam must never be installed in SCN_Training - it builds a " +
                "UIDocument panel a headless run would pay for and nobody would see.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Dossier>(),
                "Agent_Dossier must never be installed in SCN_Training - it writes the " +
                "end panel and allocates a texture per player.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_ShotTracer>(),
                "Agent_ShotTracer must never be installed in SCN_Training.");
        }

        [UnityTest]
        public IEnumerator MatchScene_InstallsAllFour()
        {
            yield return LoadExhibition();

            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_Overtime>(), "Agent_Overtime missing.");
            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_BrainCam>(), "Agent_BrainCam missing.");
            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_Dossier>(), "Agent_Dossier missing.");
            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_ShotTracer>(), "Agent_ShotTracer missing.");
        }

        // -- 1. Overtime -----------------------------------------------------

        [UnityTest]
        public IEnumerator Overtime_ClosesThePitchIn_AndWidensTheGoals()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var overtime = Object.FindAnyObjectByType<Agent_Overtime>();
            Assert.IsNotNull(env);
            Assert.IsNotNull(overtime);

            yield return WaitForPlay();
            ParkPlayers(env);

            Vector2 startExtents = env.PitchHalfExtents;
            float startGoalWidth = env.CurrentGoalWidth;
            Assert.Greater(startGoalWidth, 0.01f, "Goal width unset at kickoff.");

            // 40 s of goalless play compressed into a fraction of a second. The
            // mechanism is identical; only the trigger time is under test elsewhere.
            Set(overtime, "_armAfterSeconds", 0.15f);
            Set(overtime, "_squeezeSeconds", 0.35f);

            float deadline = Time.realtimeSinceStartup + 6f;
            while (overtime.Squeeze01 < 0.99f && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.GreaterOrEqual(overtime.Squeeze01, 0.99f,
                $"Overtime never reached the full squeeze (got {overtime.Squeeze01:0.00} " +
                $"after {overtime.GoallessSeconds:0.0} s of goalless play).");

            Assert.Less(env.PitchHalfExtents.x, startExtents.x * 0.95f,
                "The pitch did not close in.");
            Assert.Less(env.PitchHalfExtents.y, startExtents.y * 0.95f,
                "The pitch closed in on one axis only. The squeeze must be uniform - " +
                "Agent_PitchGuard's corner arcs are circles, and a non-uniform scale " +
                "turns them into ellipses Box2D approximates by the larger axis.");

            float aspectBefore = startExtents.x / startExtents.y;
            float aspectAfter = env.PitchHalfExtents.x / env.PitchHalfExtents.y;
            Assert.AreEqual(aspectBefore, aspectAfter, 0.01f, "The squeeze changed the pitch aspect.");

            Assert.Greater(env.CurrentGoalWidth, startGoalWidth * 1.4f,
                "The goal mouths did not widen. ResizePitch scales the goal transforms " +
                "down with everything else, so SetGoalWidth has to run after it and win.");
        }

        [UnityTest]
        public IEnumerator Overtime_RestoresTheOriginalPitch_WhenTheEpisodeEnds()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var overtime = Object.FindAnyObjectByType<Agent_Overtime>();
            yield return WaitForPlay();
            ParkPlayers(env);

            Vector2 startExtents = env.PitchHalfExtents;
            float startGoalWidth = env.CurrentGoalWidth;

            Set(overtime, "_armAfterSeconds", 0.15f);
            Set(overtime, "_squeezeSeconds", 0.35f);

            float deadline = Time.realtimeSinceStartup + 6f;
            while (overtime.Squeeze01 < 0.99f && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.GreaterOrEqual(overtime.Squeeze01, 0.99f, "Overtime never armed.");

            // A goal is the real trigger; OnGoalScored fires EpisodeEnded and then
            // resets the pitch in the same call stack, which is exactly the window
            // the restore has to land in.
            env.OnGoalScored(Agent_Soccer.Team.Red);
            yield return null;
            yield return null;

            Assert.AreEqual(0f, overtime.Squeeze01, 0.0001f, "Overtime stayed armed after a goal.");
            Assert.AreEqual(startExtents.x, env.PitchHalfExtents.x, 0.01f,
                "The pitch was not restored to the size the match is played on.");
            Assert.AreEqual(startExtents.y, env.PitchHalfExtents.y, 0.01f,
                "The pitch was not restored to the size the match is played on.");
            Assert.AreEqual(startGoalWidth, env.CurrentGoalWidth, 0.05f,
                "The goal mouths stayed wide after the goal that ended overtime.");
        }

        [UnityTest]
        public IEnumerator Overtime_DoesNotAccrueWhileTheClockIsFrozen()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var overtime = Object.FindAnyObjectByType<Agent_Overtime>();
            yield return WaitForPlay();
            ParkPlayers(env);

            float before = overtime.GoallessSeconds;

            var hold = new Agent_TimeFreeze.Hold("Test.PauseDuringOvertime");
            Agent_TimeFreeze.Acquire(hold);
            for (int frame = 0; frame < 20; frame++) yield return null;
            Agent_TimeFreeze.Release(hold);

            Assert.AreEqual(before, overtime.GoallessSeconds, 0.02f,
                "A paused match accrued goalless time. A long look at the pause menu " +
                "would squeeze the pitch while nobody was playing.");
        }

        // -- 5. Shot tracer --------------------------------------------------

        [UnityTest]
        public IEnumerator ShotTracer_PredictsWhereTheBallActuallyGoes()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var tracer = Object.FindAnyObjectByType<Agent_ShotTracer>();
            Assert.IsNotNull(env);
            Assert.IsNotNull(tracer);
            Assert.IsNotNull(env.Ball);

            yield return WaitForPlay();
            ParkPlayers(env);
            Agent_ShotTracer.Visible = true;

            // Across the pitch rather than along it: the long axis ends in a net, and
            // a projection that terminates at a goal line is a different assertion
            // than the one this test is making.
            env.Ball.position = env.transform.position;
            env.Ball.linearVelocity = new Vector2(8f, 0f);
            env.Ball.angularVelocity = 0f;

            // Two frames: one for the physics step that makes the velocity real, one
            // for the tracer's own Update to take a projection from it.
            yield return null;
            yield return null;

            Assert.Greater(tracer.PathPointCount, 4,
                "The tracer produced no path for a ball travelling at 8 m/s.");

            // SNAPSHOT THE PREDICTION BEFORE LETTING REALITY RUN. The tracer
            // recomputes about 20 times a second, so reading a path point after the
            // wait would read a projection taken from where the ball had already
            // got to - which measures nothing and fails by roughly one flight's
            // worth of distance. The experiment is: write the prediction down,
            // then check it.
            float step = tracer.StepSeconds;
            // 0.4 s of flight: long enough that damping and the integration order
            // matter, short enough that an 8 m/s ball has not yet reached a wall on
            // the exhibition pitch, so this grades the integrator and not the bounce.
            const float FLIGHT = 0.4f;
            int index = Mathf.Clamp(Mathf.RoundToInt(FLIGHT / step), 1, tracer.PathPointCount - 1);

            Vector2 origin = tracer.PathPoint(0);
            Vector2 predicted = tracer.PathPoint(index);
            float horizon = index * step;
            float t0 = Time.fixedTime;

            float deadline = Time.realtimeSinceStartup + 6f;
            while (Time.fixedTime - t0 < horizon && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            float elapsed = Time.fixedTime - t0;
            Vector2 actual = env.Ball.position;

            float travelled = Vector2.Distance(origin, actual);
            float error = Vector2.Distance(predicted, actual);

            Assert.Greater(travelled, 1.5f,
                $"The ball barely moved ({travelled:0.00} m); nothing was actually predicted.");
            Assert.Less(error, 0.35f,
                $"The projection missed the real ball by {error:0.00} m over {elapsed:0.00} s " +
                $"of flight against a {horizon:0.00} s prediction ({travelled:0.00} m " +
                "travelled). The tracer integrates the same " +
                "damping and Magnus terms Agent_EnvController applies, so a drift this " +
                "large means the two have gone out of step - which is exactly the kind " +
                "of claim this project retracts rather than ships.");
        }

        [UnityTest]
        public IEnumerator ShotTracer_DrawsNothingForABallAtRest()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var tracer = Object.FindAnyObjectByType<Agent_ShotTracer>();
            yield return WaitForPlay();
            ParkPlayers(env);
            Agent_ShotTracer.Visible = true;

            env.Ball.linearVelocity = Vector2.zero;
            env.Ball.angularVelocity = 0f;
            yield return null;
            yield return null;

            Assert.AreEqual(Agent_ShotTracer.Verdict.Idle, tracer.LastVerdict,
                "A stationary ball is not a shot.");
            Assert.AreEqual(0, tracer.PathPointCount, "A stationary ball produced a path.");
        }

        [UnityTest]
        public IEnumerator ShotTracer_CallsAShotOnTarget()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var tracer = Object.FindAnyObjectByType<Agent_ShotTracer>();
            yield return WaitForPlay();
            ParkPlayers(env);
            Agent_ShotTracer.Visible = true;

            // Straight at the net Blue scores into, from close enough that the 1.6 s
            // horizon reaches it without a bounce.
            Transform target = env.GetGoalTransform(Agent_Soccer.Team.Red);
            Assert.IsNotNull(target, "Exhibition pitch has no red goal transform.");

            Vector2 goal = target.position;
            Vector2 from = Vector2.Lerp(env.transform.position, goal, 0.45f);
            env.Ball.position = from;
            env.Ball.linearVelocity = (goal - from).normalized * 9f;
            env.Ball.angularVelocity = 0f;

            yield return null;
            yield return null;

            Assert.AreEqual(Agent_ShotTracer.Verdict.OnTarget, tracer.LastVerdict,
                $"A 9 m/s shot fired straight at the goal mouth from {Vector2.Distance(from, goal):0.0} m " +
                "was not called on target.");
        }

        // -- 8. Brain cam ----------------------------------------------------

        [UnityTest]
        public IEnumerator BrainCam_ReadsOneRowPerPlayer_AndMeasuresCommitment()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var brainCam = Object.FindAnyObjectByType<Agent_BrainCam>();
            Assert.IsNotNull(brainCam);

            yield return WaitForPlay();
            brainCam.SetVisible(true);

            // Long enough for several decisions: DecisionRequester period 8 on a
            // 0.01 s timestep is a decision every 0.08 s.
            for (int frame = 0; frame < 60; frame++) yield return null;

            Assert.AreEqual(env.agents.Count, brainCam.RowCount,
                "The brain cam did not render one row per player.");

            // The rule-based bot definitely drives, so its forward channel cannot be
            // flat. This is the assertion that the panel is reading the live action
            // vector rather than rendering an empty template.
            bool anyCommitment = false;
            for (int i = 0; i < env.agents.Count; i++)
            {
                float commitment = brainCam.CommitmentOf(env.agents[i]);
                Assert.GreaterOrEqual(commitment, 0f);
                Assert.LessOrEqual(commitment, 1.01f,
                    "Commitment is the mean of |raw forward action|, which is bounded by 1. " +
                    "A value above it means gained actions leaked into the panel.");
                if (commitment > 0.01f) anyCommitment = true;
            }
            Assert.IsTrue(anyCommitment,
                "No player registered any forward demand over 60 frames. The panel is " +
                "not sampling Agent_Soccer.LastRawActions.");
        }

        [UnityTest]
        public IEnumerator BrainCam_CostsNothingWhileHidden()
        {
            yield return LoadExhibition();

            var brainCam = Object.FindAnyObjectByType<Agent_BrainCam>();
            yield return WaitForPlay();

            brainCam.SetVisible(false);
            for (int frame = 0; frame < 10; frame++) yield return null;

            Assert.IsFalse(brainCam.IsVisible);
            Assert.AreEqual(0, brainCam.RowCount,
                "Rows were built while the panel was hidden.");
        }

        // -- 9. Dossier ------------------------------------------------------

        [UnityTest]
        public IEnumerator Dossier_MeasuresAdvancementRelativeToEachTeamsOwnGoal()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var dossier = Object.FindAnyObjectByType<Agent_Dossier>();
            Assert.IsNotNull(dossier);

            yield return WaitForPlay();

            Agent_Soccer blue = null;
            Agent_Soccer red = null;
            for (int i = 0; i < env.agents.Count; i++)
            {
                var agent = env.agents[i];
                if (agent == null) continue;
                if (blue == null && agent.team == Agent_Soccer.Team.Blue) blue = agent;
                if (red == null && agent.team == Agent_Soccer.Team.Red) red = agent;
            }
            Assert.IsNotNull(blue, "No blue player on the exhibition pitch.");
            Assert.IsNotNull(red, "No red player on the exhibition pitch.");

            // Park BOTH deep in their own halves. Blue spawns at -y and attacks +y
            // (Agent_EnvController.ResetPitch), so Red's long axis has to be mirrored
            // for the two to be comparable at all - and that mirror is the single
            // most likely thing in this component to be wrong.
            Vector2 centre = env.transform.position;
            float deep = env.PitchHalfExtents.y * 0.75f;
            ParkPlayers(env);
            blue.Body.position = centre + new Vector2(0f, -deep);
            red.Body.position = centre + new Vector2(0f, deep);

            for (int frame = 0; frame < 30; frame++) yield return null;

            float blueAdvance = dossier.AdvancementOf(blue);
            float redAdvance = dossier.AdvancementOf(red);

            Assert.Less(blueAdvance, -0.4f,
                $"Blue sat on its own goal line and the dossier read advancement {blueAdvance:0.00}.");
            Assert.Less(redAdvance, -0.4f,
                $"Red sat on its own goal line and the dossier read advancement {redAdvance:0.00}. " +
                "Red's long axis is not being mirrored, so 'advanced' means the opposite " +
                "thing for the two teams and the two heatmaps cannot be compared.");

            Assert.Less(dossier.AttackingShareOf(blue), 0.2f);
            Assert.Less(dossier.AttackingShareOf(red), 0.2f);
        }

        [UnityTest]
        public IEnumerator Dossier_AccumulatesOccupancyIntoItsGrid()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var dossier = Object.FindAnyObjectByType<Agent_Dossier>();
            yield return WaitForPlay();

            for (int frame = 0; frame < 30; frame++) yield return null;

            Assert.Greater(dossier.TrackedSeconds, 0.05f, "The dossier tracked no play.");

            var grid = dossier.GridOf(env.agents[0]);
            Assert.IsNotNull(grid, "No occupancy grid for the first player.");

            float total = 0f;
            int occupied = 0;
            for (int i = 0; i < grid.Length; i++)
            {
                total += grid[i];
                if (grid[i] > 0f) occupied++;
            }

            Assert.Greater(occupied, 0, "Every cell is empty after 30 frames of play.");
            Assert.AreEqual(dossier.TrackedSeconds, total, dossier.TrackedSeconds * 0.25f,
                "Grid dwell time does not add up to the tracked match time. Occupancy is " +
                "the integral of dt over the cell the body was in - if the two disagree, " +
                "cells are being dropped or double-counted.");
        }

        // -- Budget ----------------------------------------------------------

        [UnityTest]
        public IEnumerator ShotTracerOverlay_CostsOneDrawCall()
        {
            yield return LoadExhibition();

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            yield return WaitForPlay();
            ParkPlayers(env);
            Agent_ShotTracer.Visible = true;

            env.Ball.position = env.transform.position;
            env.Ball.linearVelocity = new Vector2(8f, 0f);
            yield return null;
            yield return null;

            var host = GameObject.Find("ShotTracer");
            Assert.IsNotNull(host, "The tracer's Agent_Lines batch was never created.");

            var renderers = host.GetComponentsInChildren<Renderer>(true);
            Assert.AreEqual(1, renderers.Length,
                "The shot tracer must stay ONE mesh. A LineRenderer per segment would " +
                "tie the draw-call count to the horizon length, and a refactor back to " +
                "per-mark renderers passes every other test in this file.");
        }
    }
}
