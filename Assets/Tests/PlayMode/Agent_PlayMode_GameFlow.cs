using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// End-to-end state flow: Menu -> Gameplay -> Win -> Loop reset.
    ///
    /// This is the run that no existing test performed. Every prior PlayMode test
    /// loaded a scene directly, which is exactly the mode CLAUDE.md warns is NOT
    /// how the game is played - Agent_MatchLoader reads squad sizes and per-slot
    /// profiles from the Agent_MatchSetup statics, and only the menu sets them.
    /// A direct scene load silently falls back to whatever is serialized, so a
    /// broken menu-to-match handoff would never have been caught.
    ///
    /// The scoring here drives the ball into the net directly rather than waiting
    /// for agents to play, because the subject under test is the STATE MACHINE -
    /// score accumulation, the match-point transition, the deferred end panel and
    /// the rematch reset - not the football.
    /// </summary>
    public class Agent_PlayMode_GameFlow
    {
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            yield return null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        /// <summary>Waits on real time, so it survives a frozen clock.</summary>
        static IEnumerator WaitUntilRealtime(System.Func<bool> condition, float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
        }

        /// <summary>
        /// Waits for the goal sequence to finish and the clock to STAY running.
        ///
        /// A plain "wait until not frozen" races the sequence and fails
        /// intermittently: Agent_MatchFlow.GoalSequenceAsync yields one frame
        /// before it acquires its hold, so immediately after a goal the clock is
        /// briefly still running and a naive wait returns instantly - then the
        /// assertion lands on the very frame the hold is taken. Requiring the
        /// clock to stay free for a settle window removes the ambiguity.
        /// </summary>
        static IEnumerator WaitForClockToSettle(float timeout, int settleFrames = 30)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            int clear = 0;
            while (Time.realtimeSinceStartup < deadline)
            {
                clear = Agent_TimeFreeze.IsFrozen ? 0 : clear + 1;
                if (clear >= settleFrames) yield break;
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator FullLoop_MenuToMatchToWinToRematch()
        {
            // ---------- 1. Menu ----------
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(3);

            var menu = Object.FindAnyObjectByType<Agent_MainMenu>();
            Assert.IsNotNull(menu, "SCN_Menu has no Agent_MainMenu");

            var menuDoc = menu.GetComponent<UIDocument>();
            Assert.IsNotNull(menuDoc, "Menu has no UIDocument");
            Assert.IsNotNull(menuDoc.panelSettings, "Menu UIDocument has no PanelSettings");

            var menuRoot = menuDoc.rootVisualElement;
            Button play = null;
            menuRoot.Query<Button>().ForEach(b => { if (b.text == "PLAY") play = b; });
            Assert.IsNotNull(play, "No PLAY button in the menu - the entry point is broken");
            Assert.IsNotNull(play.clickable, "PLAY button has no click handler bound");

            // ---------- 2. Menu -> Gameplay ----------
            // Invokes the exact handler the button holds, so the statics are set
            // the way a real tap sets them.
            typeof(Agent_MainMenu)
                .GetMethod("StartMatch", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(menu, null);
            yield return Frames(4);

            Assert.IsTrue(Agent_MatchSetup.Applied,
                "Menu did not mark the match setup as applied");
            Assert.Greater(Agent_MatchSetup.BlueSize, 0, "Menu handed over an empty blue squad");
            Assert.Greater(Agent_MatchSetup.RedSize, 0, "Menu handed over an empty red squad");
            Assert.AreEqual("SCN_Exhibition", SceneManager.GetActiveScene().name,
                "PLAY did not land in the match scene");

            var hud = Object.FindAnyObjectByType<Agent_HUD>();
            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(hud, "No HUD in the match scene");
            Assert.IsNotNull(env, "No env controller in the match scene");

            // The squad the menu chose must be the squad on the pitch.
            Assert.AreEqual(Agent_MatchSetup.BlueSize + Agent_MatchSetup.RedSize,
                env.GetComponentsInChildren<Agent_Soccer>().Length,
                "Pitch lineup does not match the menu selection - the handoff is broken");

            // ---------- 3. Gameplay: score to the match target ----------
            yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 15f);
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen, "Opening countdown never released the clock");

            int target = hud.matchGoals;
            Assert.Greater(target, 0, "Match has no goal target, so it can never end");

            for (int goal = 0; goal < target; goal++)
            {
                yield return WaitForClockToSettle(25f);
                Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                    $"Clock never resumed before goal {goal + 1} - the goal sequence stalled");

                int before = hud.BlueScore + hud.RedScore;
                yield return ScoreOnce(env);

                yield return WaitUntilRealtime(() => hud.BlueScore + hud.RedScore > before, 10f);
                Assert.Greater(hud.BlueScore + hud.RedScore, before,
                    $"Goal {goal + 1} did not register on the scoreboard");
            }

            // ---------- 4. Win / end state ----------
            Assert.IsTrue(hud.MatchOver, "Reaching the goal target did not end the match");

            // The end panel is deferred behind the replay and the final whistle,
            // so it is allowed to take a few seconds - but it must arrive.
            yield return WaitUntilRealtime(
                () => Object.FindAnyObjectByType<Agent_HUD>() != null && PanelVisible(hud), 25f);
            Assert.IsTrue(PanelVisible(hud),
                "End panel never appeared - the match is unwinnable from the player's view");
            Assert.IsTrue(Agent_TimeFreeze.IsFrozen,
                "End panel is up but the pitch is still live underneath it");

            // ---------- 5. Loop reset ----------
            var root = hud.GetComponent<UIDocument>().rootVisualElement;
            Button rematch = null;
            root.Query<Button>().ForEach(b => { if (b.text == "REMATCH") rematch = b; });
            Assert.IsNotNull(rematch, "End panel has no REMATCH button - the loop is a dead end");

            Agent_TimeFreeze.ReleaseAll();
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            yield return Frames(4);

            var freshHud = Object.FindAnyObjectByType<Agent_HUD>();
            Assert.IsNotNull(freshHud, "Rematch did not produce a HUD");
            Assert.AreEqual(0, freshHud.BlueScore, "Rematch did not reset the blue score");
            Assert.AreEqual(0, freshHud.RedScore, "Rematch did not reset the red score");
            Assert.IsFalse(freshHud.MatchOver, "Rematch started already finished");
            Assert.IsTrue(Agent_MatchSetup.Applied,
                "Rematch lost the menu lineup - it would silently fall back to scene defaults");
        }

        static bool PanelVisible(Agent_HUD hud)
        {
            var root = hud.GetComponent<UIDocument>().rootVisualElement;
            return root.Q(className: "panel--scrim") != null;
        }

        /// <summary>Drives the ball into the blue net and waits for the episode to end.</summary>
        static IEnumerator ScoreOnce(Agent_EnvController env)
        {
            bool ended = false;
            System.Action<Agent_Soccer.Team?> handler = _ => ended = true;
            env.EpisodeEnded += handler;

            var ball = env.Ball;
            ball.position = new Vector2(0f, -(env.PitchHalfExtents.y - 1.2f));
            ball.linearVelocity = new Vector2(0f, -30f);
            ball.WakeUp();

            for (int i = 0; i < 400 && !ended; i++) yield return new WaitForFixedUpdate();
            env.EpisodeEnded -= handler;
        }

        /// <summary>
        /// The clock must always come back. Every freeze holder in the game -
        /// countdown, replay, halftime, end panel - goes through Agent_TimeFreeze,
        /// so a leaked holder anywhere presents to the player as a hung game with
        /// no error in the console.
        /// </summary>
        [UnityTest]
        public IEnumerator MatchScene_NeverLeavesTheClockStopped()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            yield return Frames(3);

            yield return WaitForClockToSettle(15f);
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                "Kickoff countdown leaked a freeze holder. Held by: "
                + Agent_TimeFreeze.DescribeHolders());
            Assert.AreEqual(1f, Time.timeScale, 0.0001f);

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            yield return ScoreOnce(env);

            yield return WaitForClockToSettle(25f);
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                "The goal sequence leaked a freeze holder - the game would hang. Held by: "
                + Agent_TimeFreeze.DescribeHolders());
            Assert.AreEqual(1f, Time.timeScale, 0.0001f);
        }
    }
}
