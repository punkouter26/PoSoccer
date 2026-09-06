using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Covers the broadcast layer added 2026-09-06: the shot director, the win
    /// probability strip, the intent and vision overlays, the impact FX and the
    /// live stat ticker.
    ///
    /// The load-bearing test is <see cref="TrainingScene_HasNoBroadcastLayer"/>,
    /// and for exactly the reason Agent_PlayMode_Spectator gives for its own
    /// equivalent: this layer now includes a component that WRITES THE CAMERA
    /// TRANSFORM every LateUpdate and one that reads the scoreboard. Neither
    /// belongs anywhere near a headless run, and "training never gets any of
    /// this" has to be an assertion rather than an intention.
    ///
    /// The second load-bearing test is <see cref="Overlays_CostOneDrawCallEach"/>.
    /// Both overlays scale their geometry with the squad size - the vision fan is
    /// 40 rays per player - and the single-mesh design is the only thing keeping
    /// that off the draw-call budget in .claude/rules/performance.md. A refactor
    /// back to a renderer per mark would pass every other test in this file.
    ///
    /// Like the spectator suite, these drive SCN_Exhibition directly, which normal
    /// play must never do. Fine here: nothing under test depends on the lineup.
    /// </summary>
    public class Agent_PlayMode_Broadcast
    {
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

        [UnityTest]
        public IEnumerator TrainingScene_HasNoBroadcastLayer()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            yield return null;

            Assert.IsNull(Object.FindAnyObjectByType<Agent_Director>(),
                "Agent_Director must never be installed in SCN_Training - it writes " +
                "the camera transform every LateUpdate.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_WinProbability>(),
                "Agent_WinProbability must never be installed in SCN_Training - there " +
                "is no score for it to read.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_MatchStats>(),
                "Agent_MatchStats must never be installed in SCN_Training.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Intent>(),
                "Agent_Intent must never be installed in SCN_Training.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_VisionView>(),
                "Agent_VisionView must never be installed in SCN_Training.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_ImpactFX>(),
                "Agent_ImpactFX must never be installed in SCN_Training.");

            Assert.IsNull(GameObject.Find("IntentOverlay"),
                "An overlay mesh leaked into the training scene.");
            Assert.IsNull(GameObject.Find("VisionOverlay"),
                "An overlay mesh leaked into the training scene.");
        }

        [UnityTest]
        public IEnumerator ExhibitionScene_InstallsBroadcastLayer()
        {
            yield return LoadExhibition();

            var director = Object.FindAnyObjectByType<Agent_Director>();
            var probability = Object.FindAnyObjectByType<Agent_WinProbability>();
            var stats = Object.FindAnyObjectByType<Agent_MatchStats>();
            var intent = Object.FindAnyObjectByType<Agent_Intent>();
            var vision = Object.FindAnyObjectByType<Agent_VisionView>();
            var impact = Object.FindAnyObjectByType<Agent_ImpactFX>();

            Assert.IsNotNull(director, "Agent_Director was not installed");
            Assert.IsNotNull(probability, "Agent_WinProbability was not installed");
            Assert.IsNotNull(stats, "Agent_MatchStats was not installed");
            Assert.IsNotNull(intent, "Agent_Intent was not installed");
            Assert.IsNotNull(vision, "Agent_VisionView was not installed");
            Assert.IsNotNull(impact, "Agent_ImpactFX was not installed");

            // Each gates itself in Start; still enabled proves the gate opened.
            Assert.IsTrue(director.enabled, "Agent_Director disabled itself in a match");
            Assert.IsTrue(probability.enabled, "Agent_WinProbability disabled itself in a match");
            Assert.IsTrue(stats.enabled, "Agent_MatchStats disabled itself in a match");
            Assert.IsTrue(intent.enabled, "Agent_Intent disabled itself in a match");
            Assert.IsTrue(vision.enabled, "Agent_VisionView disabled itself in a match");
            Assert.IsTrue(impact.enabled, "Agent_ImpactFX disabled itself in a match");
        }

        [UnityTest]
        public IEnumerator Overlays_CostOneDrawCallEach()
        {
            yield return LoadExhibition();

            Agent_Intent.Visible = true;
            Agent_VisionView.CurrentMode = Agent_VisionView.Mode.All;
            yield return null;
            yield return null;

            AssertSingleBatch("IntentOverlay");
            AssertSingleBatch("VisionOverlay");

            Agent_VisionView.CurrentMode = Agent_VisionView.Mode.Off;
        }

        static void AssertSingleBatch(string label)
        {
            var host = GameObject.Find(label);
            Assert.IsNotNull(host, $"{label} was never built.");

            var renderer = host.GetComponent<MeshRenderer>();
            Assert.IsNotNull(renderer, $"{label} has no MeshRenderer.");
            Assert.AreEqual(1, renderer.sharedMaterials.Length,
                $"{label} uses {renderer.sharedMaterials.Length} materials. The whole " +
                "point of the single-mesh design is that an overlay costs one draw " +
                "call no matter how many players or rays it is drawing.");

            // World space, identity transform. A parented overlay is offset by its
            // parent's position, and the exhibition pitch root is not at the origin
            // - so this silently draws every mark in the wrong place.
            Assert.AreEqual(Vector3.zero, host.transform.position,
                $"{label} is not at the world origin; its vertices are world-space.");
        }

        [UnityTest]
        public IEnumerator IntentOverlay_TogglesWithoutRebuilding()
        {
            yield return LoadExhibition();

            Agent_Intent.Visible = true;
            yield return null;

            var host = GameObject.Find("IntentOverlay");
            Assert.IsNotNull(host);
            var renderer = host.GetComponent<MeshRenderer>();
            Assert.IsTrue(renderer.enabled, "Overlay should be drawing while visible.");

            Agent_Intent.Visible = false;
            yield return null;
            Assert.IsFalse(renderer.enabled, "Hiding the overlay must disable the renderer.");

            Agent_Intent.Visible = true;
            yield return null;
            Assert.IsTrue(renderer.enabled);
            Assert.AreSame(host, GameObject.Find("IntentOverlay"),
                "Toggling rebuilt the overlay instead of enabling the existing one.");
        }

        [UnityTest]
        public IEnumerator WinProbability_StartsEvenAndStaysInRange()
        {
            yield return LoadExhibition();

            var probability = Object.FindAnyObjectByType<Agent_WinProbability>();
            Assert.IsNotNull(probability);

            // Nobody has scored, so an even scoreline should not produce a lopsided
            // estimate on the strength of field position alone.
            Assert.AreEqual(0.5f, probability.BlueWinProbability, 0.25f,
                "A 0-0 opening should read near even.");

            for (int frame = 0; frame < 60; frame++)
            {
                Assert.GreaterOrEqual(probability.BlueWinProbability, 0f);
                Assert.LessOrEqual(probability.BlueWinProbability, 1f);
                Assert.GreaterOrEqual(probability.Threat, 0f);
                Assert.LessOrEqual(probability.Threat, 1f);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Director_StandsDownWhileTheReplayOwnsTheCamera()
        {
            yield return LoadExhibition();

            var director = Object.FindAnyObjectByType<Agent_Director>();
            var follow = Object.FindAnyObjectByType<Agent_CameraFollow>();
            Assert.IsNotNull(director);
            Assert.IsNotNull(follow);

            // First get it actually directing. The director stands down for its
            // opening-silence window so the kickoff wide shot plays, and asserting
            // "not directing" during that window would pass without testing
            // anything at all.
            float deadline = Time.realtimeSinceStartup + 12f;
            while (!director.IsDirecting && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.IsTrue(director.IsDirecting,
                "The director never took the camera, so this test could not observe " +
                "it handing the camera back.");

            // Stand in for Agent_Replay: take the camera the way it does.
            var target = new GameObject("FakeReplayTarget");
            follow.SetOverrideTarget(target.transform, 5f);
            yield return null;
            yield return null;

            Assert.IsTrue(follow.HasOverrideTarget);
            Assert.IsFalse(director.IsDirecting,
                "The director kept requesting shots while a replay owned the frame. " +
                "Two components steering one camera resolves to whichever ran last.");

            follow.ClearOverrideTarget();
            Object.DestroyImmediate(target);
        }

        [UnityTest]
        public IEnumerator CameraShot_FramesWhatWasRequestedAndThenExpires()
        {
            yield return LoadExhibition();

            var follow = Object.FindAnyObjectByType<Agent_CameraFollow>();
            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var camera = Camera.main;
            Assert.IsNotNull(follow);
            Assert.IsNotNull(env);
            Assert.IsNotNull(camera);

            // Drive the channel directly so the assertion is about the channel and
            // not about whatever shot the director happens to want this second.
            var director = Object.FindAnyObjectByType<Agent_Director>();
            if (director != null) director.enabled = false;

            float wide = follow.CurrentWideOrtho;
            float requested = wide * 0.6f;   // inside [tightest, wide], so not clamped

            // Hold the shot. Orthographic size is not pan-clamped, so it is the one
            // part of the framing that converges to exactly what was asked for.
            float deadline = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < deadline)
            {
                follow.RequestShot(Vector2.zero, requested);
                yield return null;
            }

            Assert.IsTrue(follow.ShotActive, "The shot lapsed while it was being held.");
            Assert.AreEqual(requested, camera.orthographicSize, requested * 0.08f,
                "The camera did not converge on the requested framing while the shot " +
                "was being held.");

            // Now stop asking. A director that dies mid-shot must not freeze the
            // camera on the last frame it managed to request: after SHOT_TIMEOUT
            // the rig owns the framing again and drives it from the live ball.
            deadline = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < deadline) yield return null;

            Assert.IsFalse(follow.ShotActive,
                "The shot never expired, so a director that stops ticking would " +
                "leave the camera stuck on its last request.");
            Assert.IsFalse(float.IsNaN(camera.orthographicSize));
            Assert.Greater(camera.orthographicSize, 0f);
        }

        [UnityTest]
        public IEnumerator MatchStats_AccumulateDistanceAndPossession()
        {
            yield return LoadExhibition();

            var stats = Object.FindAnyObjectByType<Agent_MatchStats>();
            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(stats);
            Assert.IsNotNull(env);
            Assert.Greater(env.agents.Count, 0, "No agents to measure.");

            // WAIT OUT THE KICKOFF COUNTDOWN FIRST. Agent_MatchFlow holds the clock
            // at zero for ~2.5 s at the start of a match, and Unity runs no
            // FixedUpdate at all while timeScale is 0 - so a fixed two-second
            // window from scene load samples exactly nothing and reads as "the
            // stats component is broken". It was not; the match had not started.
            float countdownDeadline = Time.realtimeSinceStartup + 10f;
            while (Agent_TimeFreeze.IsFrozen && Time.realtimeSinceStartup < countdownDeadline)
            {
                yield return null;
            }
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                "The kickoff countdown never released the clock.");

            // Two seconds of real play. Bodies move under their own policies, so
            // this asserts the accumulator runs and stays sane - not a speed.
            float deadline = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < deadline) yield return null;

            float possessionTotal = 0f;
            for (int i = 0; i < env.agents.Count; i++)
            {
                var agent = env.agents[i];
                if (agent == null) continue;

                Assert.IsTrue(stats.TryGet(agent, out var stat),
                    $"No stat record for {agent.name}.");
                Assert.Greater(stat.TrackedSeconds, 0f, "The sampler never ticked.");
                Assert.GreaterOrEqual(stat.DistanceMetres, 0f);
                Assert.GreaterOrEqual(stat.TopSpeed, 0f);
                Assert.LessOrEqual(stat.PossessionShare, 1.0001f);
                possessionTotal += stat.PossessionShare;
            }

            // Possession is "closest body to the ball", so exactly one player owns
            // every tick and the shares must sum to one.
            Assert.AreEqual(1f, possessionTotal, 0.05f,
                "Possession shares do not sum to 1, so some ticks were credited to " +
                "nobody or to more than one player.");
        }
    }
}
