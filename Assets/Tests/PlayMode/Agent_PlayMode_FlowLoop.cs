using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Menu -> Match -> Full time -> Menu, five times over, with the global state
    /// checked on every arrival.
    ///
    /// WHAT ONE LAP CANNOT SHOW. Agent_PlayMode_GameFlow already walks the loop
    /// once. Everything it asserts is about a process that has only ever seen one
    /// match - and every defect in this family is about the SECOND one. Statics
    /// survive a scene load in a player build, so a freeze holder leaked by the
    /// first match, a playback speed left at 2x, a GalleryMode flag nobody cleared
    /// or a second Agent_Chrome that the find-or-create missed are all invisible
    /// on lap one and present on lap two. CLAUDE.md's Agent_TimeFreeze landmine is
    /// exactly this shape: a HUD destroyed while paused left a holder that froze
    /// every LATER match, and the suite was green because no test played a later
    /// match.
    ///
    /// Five laps rather than two because some leaks are additive (a chrome object
    /// per visit, a holder per match) and a count of two can coincide with a
    /// legitimate value where a count of five cannot.
    ///
    /// THE MATCH IS ENDED THE WAY A MATCH ENDS. matchGoals is dropped to 1 and the
    /// ball is fired into the net, so the goal sequence, the replay, the full-time
    /// whistle and the end panel all run for real, and the way back is the end
    /// panel's own MENU button, activated through the event system. Nothing here
    /// calls LoadScene on the game's behalf - see Agent_PlayMode_GameFlow.Click
    /// for why that distinction is the whole test.
    ///
    /// ALTERNATE LAPS LEAVE THE SPEED CHANGED ON PURPOSE. A SpectatorSpeed that is
    /// never moved off 1 passes "is it 1 at the menu" without anything having
    /// reset it.
    /// </summary>
    public class Agent_PlayMode_FlowLoop
    {
        const int LAPS = 5;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            yield return null;
        }

        static IEnumerator Frames(int count)
        {
            for (int frameIndex = 0; frameIndex < count; frameIndex++) yield return null;
        }

        /// <summary>Waits on real time, so it survives a frozen or scaled clock.</summary>
        static IEnumerator WaitUntilRealtime(System.Func<bool> condition, float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
        }

        /// <summary>
        /// Waits for the clock to be free and STAY free. Same reasoning as the
        /// helper of the same name in Agent_PlayMode_GameFlow: a bare "not frozen"
        /// races the frame on which the next hold is taken.
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

        /// <summary>Activates a button through the event system. See Agent_PlayMode_GameFlow.Click.</summary>
        static void Click(Button button, string what)
        {
            Assert.IsNotNull(button, what + ": no such button");
            Assert.IsNotNull(button.clickable, what + ": button has no Clickable manipulator");
            Assert.IsTrue(button.enabledInHierarchy, what + ": button is disabled");

            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = button;
            button.SendEvent(evt);
        }

        static Button FindButton(VisualElement root, string text)
        {
            Button found = null;
            root.Query<Button>().ForEach(candidate => { if (candidate.text == text) found = candidate; });
            return found;
        }

        static VisualElement RootOf<T>() where T : MonoBehaviour
        {
            var host = Object.FindAnyObjectByType<T>();
            Assert.IsNotNull(host, $"No {typeof(T).Name} in the active scene");
            var document = host.GetComponent<UIDocument>();
            Assert.IsNotNull(document, $"{typeof(T).Name} has no UIDocument");
            return document.rootVisualElement;
        }

        static bool EndPanelVisible(Agent_HUD hud)
        {
            if (hud == null) return false;
            var root = hud.GetComponent<UIDocument>().rootVisualElement;
            return root != null && root.Q(className: "panel--scrim") != null;
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

            for (int tick = 0; tick < 400 && !ended; tick++) yield return new WaitForFixedUpdate();
            env.EpisodeEnded -= handler;
        }

        /// <summary>
        /// Everything that must be true standing on the menu, whatever happened in
        /// the match before it. Messages name the lap, because "holders: HUD.Pause"
        /// on lap 1 and on lap 4 are different bugs.
        /// </summary>
        static void AssertCleanMenu(string where)
        {
            Assert.AreEqual("SCN_Menu", SceneManager.GetActiveScene().name,
                where + ": not on the menu");

            Assert.AreEqual(0, Agent_TimeFreeze.HolderCount,
                where + ": freeze holders survived the trip back to the menu, so the next " +
                "match starts with a stopped clock. Held by: " + Agent_TimeFreeze.DescribeHolders());
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen, where + ": the clock is frozen on the menu");

            Assert.AreEqual(1f, Agent_TimeFreeze.SpectatorSpeed, 0.0001f,
                where + ": the spectator speed outlived the match it was set in. The next " +
                "match would open at that speed with no control on screen to explain it.");
            Assert.AreEqual(1f, Time.timeScale, 0.0001f,
                where + ": Time.timeScale is not 1 on the menu");
            Assert.IsTrue(Agent_TimeFreeze.IsNormalSpeed,
                where + ": Agent_TimeFreeze does not consider the clock to be at normal speed");

            Assert.AreEqual(1, Object.FindObjectsByType<Agent_Chrome>().Length,
                where + ": expected exactly one Agent_Chrome. Zero strands the player on " +
                "screens whose only exit is its MENU button; two is a duplicated button row.");

            Assert.IsFalse(Agent_MatchSetup.Applied,
                where + ": the menu inherited the last match's lineup instead of starting " +
                "from nothing chosen");
            Assert.IsFalse(Agent_MatchSetup.GalleryMode,
                where + ": the menu inherited GalleryMode, so PLAY would open the gallery");
        }

        [UnityTest]
        public IEnumerator MenuToMatchToMenu_FiveLaps_LeavesNoGlobalStateBehind()
        {
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(3);
            AssertCleanMenu("Before the first lap");

            for (int lap = 1; lap <= LAPS; lap++)
            {
                string where = $"Lap {lap}/{LAPS}";

                // ---------- Menu -> Match ----------
                Click(FindButton(RootOf<Agent_MainMenu>(), "PLAY"), where + " PLAY");
                yield return Frames(4);

                Assert.AreEqual("SCN_Exhibition", SceneManager.GetActiveScene().name,
                    where + ": PLAY did not land in the match scene");
                Assert.IsTrue(Agent_MatchSetup.Applied,
                    where + ": the menu did not hand a lineup to the match");
                Assert.IsFalse(Agent_MatchSetup.GalleryMode,
                    where + ": PLAY opened the gallery");

                var hud = Object.FindAnyObjectByType<Agent_HUD>();
                var env = Object.FindAnyObjectByType<Agent_EnvController>();
                Assert.IsNotNull(hud, where + ": no HUD in the match scene");
                Assert.IsNotNull(env, where + ": no env controller in the match scene");

                Assert.AreEqual(1, Object.FindObjectsByType<Agent_Chrome>().Length,
                    where + ": expected exactly one Agent_Chrome in the match");
                Assert.AreEqual(1, Object.FindObjectsByType<Agent_SpectatorInput>().Length,
                    where + ": Agent_Presentation.Install should add exactly one spectator input");
                Assert.AreEqual(1, Object.FindObjectsByType<Agent_MatchLog>().Length,
                    where + ": Agent_Presentation.Install should add exactly one match log");

                // A match always opens at 1x, whatever the last one was left at.
                Assert.AreEqual(1f, Agent_TimeFreeze.SpectatorSpeed, 0.0001f,
                    where + ": the match opened at a spectator speed it did not choose");

                // One goal ends it. The field is read live when a goal lands, so
                // this shortens the match without bypassing any of its machinery.
                hud.matchGoals = 1;

                yield return WaitForClockToSettle(20f);
                Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                    where + ": the opening countdown never released the clock. Held by: "
                    + Agent_TimeFreeze.DescribeHolders());

                // Leave something for the menu to have to reset - on alternate laps,
                // so both "was changed" and "was never touched" are walked.
                if (lap % 2 == 1)
                {
                    Agent_TimeFreeze.SpectatorSpeed = 2f;
                    Assert.AreEqual(2f, Agent_TimeFreeze.SpectatorSpeed, 0.0001f,
                        where + ": SpectatorSpeed did not take the value it was given");
                }

                // ---------- Match -> Full time ----------
                yield return ScoreOnce(env);
                yield return WaitUntilRealtime(() => hud.MatchOver, 10f);
                Assert.IsTrue(hud.MatchOver,
                    where + ": a goal at matchGoals = 1 did not end the match");

                // Deferred behind the replay and the whistle; allowed to take a while.
                yield return WaitUntilRealtime(() => EndPanelVisible(hud), 30f);
                Assert.IsTrue(EndPanelVisible(hud),
                    where + ": the end panel never appeared, so there is no way out of " +
                    "the finished match. Held by: " + Agent_TimeFreeze.DescribeHolders());
                Assert.IsTrue(Agent_TimeFreeze.IsFrozen,
                    where + ": the end panel is up over a live pitch");
                Assert.AreEqual(0f, Time.timeScale, 0.0001f,
                    where + ": a freeze must win over the spectator speed - the end panel " +
                    "is up and the clock is running");

                // ---------- Full time -> Menu ----------
                var hudRoot = hud.GetComponent<UIDocument>().rootVisualElement;
                Click(FindButton(hudRoot, "MENU"), where + " end-panel MENU");
                yield return Frames(4);

                AssertCleanMenu(where);
            }
        }
    }
}
