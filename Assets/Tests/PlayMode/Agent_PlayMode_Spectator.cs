using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.Tilemaps;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Covers the spectator layer added 2026-08-27: goal replay, match flow,
    /// crowd dressing and commentary.
    ///
    /// The load-bearing test here is <see cref="TrainingScene_HasNoSpectatorLayer"/>.
    /// Everything else is polish; that one protects the benchmark. A replay
    /// freezing Time.timeScale during an evaluation run would silently corrupt
    /// episode timing and therefore the win rates the whole project is measured
    /// on, so "training never gets any of this" has to be an assertion rather
    /// than an intention.
    ///
    /// Note these tests drive SCN_Exhibition directly, which normal play must
    /// never do (Agent_MatchSetup statics come from the menu). That is fine here:
    /// the scene falls back to its serialized lineup, and nothing under test
    /// depends on who is playing.
    /// </summary>
    public class Agent_PlayMode_Spectator
    {
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // A test that fails mid-replay must not leave the editor's clock at 0.
            Agent_TimeFreeze.ReleaseAll();
            yield return null;
        }

        static IEnumerator LoadExhibition()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            yield return null;   // Awake / OnEnable
            yield return null;   // Start
            yield return null;   // components added in Awake reach their own Start
        }

        [UnityTest]
        public IEnumerator TrainingScene_HasNoSpectatorLayer()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            yield return null;

            Assert.IsNull(Object.FindAnyObjectByType<Agent_Replay>(),
                "Agent_Replay must never be installed in SCN_Training - a replay " +
                "freezes the clock and would corrupt training/eval episode timing.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_MatchFlow>(),
                "Agent_MatchFlow must never be installed in SCN_Training.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Crowd>(),
                "Agent_Crowd must never be installed in SCN_Training.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Commentary>(),
                "Agent_Commentary must never be installed in SCN_Training.");

            Assert.AreEqual(1f, Time.timeScale, 0.0001f,
                "Training must run at an unmodified time scale.");
        }

        [UnityTest]
        public IEnumerator ExhibitionScene_InstallsSpectatorLayer()
        {
            yield return LoadExhibition();

            var replay = Object.FindAnyObjectByType<Agent_Replay>();
            var flow = Object.FindAnyObjectByType<Agent_MatchFlow>();
            var crowd = Object.FindAnyObjectByType<Agent_Crowd>();
            var commentary = Object.FindAnyObjectByType<Agent_Commentary>();

            Assert.IsNotNull(replay, "Agent_Replay was not installed in SCN_Exhibition");
            Assert.IsNotNull(flow, "Agent_MatchFlow was not installed in SCN_Exhibition");
            Assert.IsNotNull(crowd, "Agent_Crowd was not installed in SCN_Exhibition");
            Assert.IsNotNull(commentary, "Agent_Commentary was not installed in SCN_Exhibition");

            // Each component gates itself in Start; enabled proves the gate opened.
            Assert.IsTrue(replay.enabled, "Agent_Replay disabled itself in a match scene");
            Assert.IsTrue(flow.enabled, "Agent_MatchFlow disabled itself in a match scene");
            Assert.IsTrue(crowd.enabled, "Agent_Crowd disabled itself in a match scene");
            Assert.IsTrue(commentary.enabled, "Agent_Commentary disabled itself in a match scene");
        }

        [UnityTest]
        public IEnumerator Crowd_BuildsTilemapsAndFlashbulbs()
        {
            yield return LoadExhibition();

            var root = GameObject.Find("Stadium_Crowd");
            Assert.IsNotNull(root, "Crowd root was not built");

            var tilemaps = root.GetComponentsInChildren<Tilemap>();
            Assert.AreEqual(2, tilemaps.Length, "Expected a stands tilemap and a boards tilemap");

            int totalTiles = 0;
            for (int i = 0; i < tilemaps.Length; i++)
            {
                // Not GetUsedTilesCount(): that counts DISTINCT tile assets, and
                // every cell here shares one runtime Tile, so it always reports 1.
                int cells = CountCells(tilemaps[i]);
                totalTiles += cells;
                Assert.Greater(cells, 0, $"Tilemap '{tilemaps[i].name}' was built empty");
            }
            Assert.Greater(totalTiles, 100, "Crowd ring is implausibly small");
        }

        /// <summary>
        /// The opening 3-2-1 stops the clock, so the match must not be able to
        /// start before it finishes - and, more importantly, must always resume.
        /// A countdown that failed to release would hang the game on a black pitch.
        /// </summary>
        [UnityTest]
        public IEnumerator OpeningCountdown_FreezesThenResumes()
        {
            yield return LoadExhibition();

            Assert.AreEqual(0f, Time.timeScale, 0.0001f,
                "The opening countdown should hold the clock at zero");

            yield return WaitForClock(1f, 12f);

            Assert.AreEqual(1f, Time.timeScale, 0.0001f,
                "The opening countdown never handed the clock back");
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen, "A freeze holder leaked after the countdown");
        }

        /// <summary>
        /// End to end: score, and confirm the replay takes the clock, builds its
        /// ghosts, and gives everything back. The ghost cleanup matters - the
        /// ghosts are re-created per goal, so a leak would accumulate all match.
        /// </summary>
        [UnityTest]
        public IEnumerator Goal_PlaysReplayThenRestoresClockAndCleansUp()
        {
            yield return LoadExhibition();
            yield return WaitForClock(1f, 12f);        // let the kickoff countdown clear

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(env, "No env controller in SCN_Exhibition");

            // The replay needs a populated capture ring before it will play.
            for (int i = 0; i < 120; i++) yield return new WaitForFixedUpdate();

            bool ended = false;
            env.EpisodeEnded += _ => ended = true;

            // Shoot into the blue net. Mirrors Agent_PlayMode_GoalFlow's approach.
            var ball = env.Ball;
            ball.position = new Vector2(0f, -(env.PitchHalfExtents.y - 1.2f));
            ball.linearVelocity = new Vector2(0f, -30f);
            ball.WakeUp();

            for (int i = 0; i < 300 && !ended; i++) yield return new WaitForFixedUpdate();
            Assert.IsTrue(ended, "Test goal never registered");

            Assert.IsTrue(Agent_TimeFreeze.IsFrozen,
                "A goal should freeze the clock for the replay");

            // Frame-based from here: FixedUpdate does not tick at timeScale 0, so
            // WaitForFixedUpdate would deadlock the test.
            bool sawGhosts = false;
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (GameObject.Find("ReplayGhosts") != null) sawGhosts = true;
                if (!Agent_TimeFreeze.IsFrozen) break;
                yield return null;
            }

            Assert.IsTrue(sawGhosts, "The replay never built its ghost renderers");
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                "The goal sequence never handed the clock back");
            Assert.AreEqual(1f, Time.timeScale, 0.0001f, "Time scale was not restored");
            Assert.IsNull(GameObject.Find("ReplayGhosts"), "Replay ghosts leaked");
            Assert.IsNull(GameObject.Find("ReplayScrim"), "Replay scrim leaked");
        }

        static int CountCells(Tilemap tilemap)
        {
            int count = 0;
            foreach (var position in tilemap.cellBounds.allPositionsWithin)
                if (tilemap.HasTile(position)) count++;
            return count;
        }

        static IEnumerator WaitForClock(float target, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!Mathf.Approximately(Time.timeScale, target)
                   && Time.realtimeSinceStartup < deadline)
                yield return null;
        }
    }
}
