using System.Collections;
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
    /// Most of the scoring here drives the ball into the net directly rather than
    /// waiting for agents to play, because the subject under test is the STATE
    /// MACHINE - score accumulation, the match-point transition, the deferred end
    /// panel and the rematch reset - not the football.
    ///
    /// <see cref="Match_ProgressesUnderRealPlay"/> is the deliberate exception,
    /// added 2026-09-06. Teleporting the ball is right for the tests above and it
    /// leaves one question none of them ask: does the loop terminate when nobody
    /// is cheating? A pitch whose episode watchdog never fires, or whose agents
    /// never reach the ball, passes every other test in this file.
    ///
    /// Buttons are ACTIVATED, not bypassed. Reflecting into a handler and calling
    /// SceneManager.LoadScene by hand tests everything except the wiring between
    /// the button and the thing it does - see the note on <see cref="Click"/>.
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

        /// <summary>
        /// Activates a button through the event system, so the binding from the
        /// button to its handler is part of what is under test.
        ///
        /// WHY NOT JUST CALL THE HANDLER. Everything below used to reach past the
        /// UI - reflecting into Agent_MainMenu.StartMatch, calling
        /// SceneManager.LoadScene itself - and then assert only that a button with
        /// the right text EXISTED. So the one thing standing between the player and
        /// the game, the binding from that button to that handler, was the one
        /// thing never exercised: a PLAY button wired to nothing, or wired to the
        /// wrong preset, passed every assertion in this file.
        ///
        /// WHY NavigationSubmitEvent AND NOT ClickEvent. Measured in the live
        /// editor on 2026-09-06 against a throwaway Button with a counting
        /// handler: a dispatched ClickEvent fired it ZERO times, a dispatched
        /// NavigationSubmitEvent fired it once. ClickEvent is what Clickable
        /// EMITS after a pointer down/up pair on the target - it is an output of
        /// the manipulator, not an input to it - so sending one synthesises the
        /// symptom of a click without any of its cause, and every assertion
        /// downstream then fails for a reason that has nothing to do with the
        /// code under test. Submit is the keyboard/gamepad activation path and
        /// Button handles it directly; it also needs no resolved layout, so it
        /// works on the frame a panel is built rather than one frame later.
        /// </summary>
        static void Click(Button button)
        {
            Assert.IsNotNull(button, "Cannot click a button that is not there");
            Assert.IsNotNull(button.clickable, $"'{button.text}' has no Clickable manipulator");
            Assert.IsTrue(button.enabledInHierarchy, $"'{button.text}' is disabled");

            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = button;
            button.SendEvent(evt);
        }

        static Button FindButton(VisualElement root, string text)
        {
            Button found = null;
            root.Query<Button>().ForEach(b => { if (b.text == text) found = b; });
            return found;
        }

        static VisualElement RootOf<T>() where T : MonoBehaviour
        {
            var host = Object.FindAnyObjectByType<T>();
            Assert.IsNotNull(host, $"No {typeof(T).Name} in the active scene");
            var doc = host.GetComponent<UIDocument>();
            Assert.IsNotNull(doc, $"{typeof(T).Name} has no UIDocument");
            return doc.rootVisualElement;
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
            // A real ClickEvent, not a reflected call to StartMatch: the binding
            // between the button and the handler is part of the path under test.
            Click(play);
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
            var rematch = FindButton(root, "REMATCH");
            Assert.IsNotNull(rematch, "End panel has no REMATCH button - the loop is a dead end");
            Assert.IsNotNull(FindButton(root, "MENU"),
                "End panel has no MENU button - the only other way out of a finished match");

            // Clicked, not simulated. REMATCH owns two things the test used to do
            // on its behalf - releasing every freeze holder and reloading the
            // scene - so doing them here proved the loop worked without ever
            // proving the button did.
            Click(rematch);
            yield return Frames(4);

            var freshHud = Object.FindAnyObjectByType<Agent_HUD>();
            Assert.IsNotNull(freshHud, "Rematch did not produce a HUD");
            Assert.AreEqual(0, freshHud.BlueScore, "Rematch did not reset the blue score");
            Assert.AreEqual(0, freshHud.RedScore, "Rematch did not reset the red score");
            Assert.IsFalse(freshHud.MatchOver, "Rematch started already finished");
            Assert.IsTrue(Agent_MatchSetup.Applied,
                "Rematch lost the menu lineup - it would silently fall back to scene defaults");

            // The new match's kickoff countdown legitimately owns the clock for
            // the first couple of seconds, so "is it frozen right now" is the
            // wrong question - it fails on correct behaviour, which is what the
            // first version of this assertion did (Held by:
            // MatchFlow.OpeningCountdown). The property that actually matters is
            // that the clock COMES BACK: the end panel's hold must not survive
            // the scene load.
            yield return WaitForClockToSettle(20f);
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                "The rematch never handed the clock back, so the new match is unplayable. "
                + "Held by: " + Agent_TimeFreeze.DescribeHolders());
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

        /// <summary>
        /// The match makes progress when nobody is cheating: real agents, real
        /// physics, no teleported ball.
        ///
        /// WHY THIS IS NOT "PLAY UNTIL SOMEBODY WINS". Every other test in this
        /// file fires the ball into the net at 30 m/s, because the subject there
        /// is the state machine. That is the right call for those tests and the
        /// wrong one for this question: it means NOTHING here has ever confirmed
        /// that the loop terminates under the only conditions a player will ever
        /// see. A pitch where the episode watchdog never fires, or where the
        /// agents never touch the ball, would pass all of them.
        ///
        /// It stops short of asserting a full first-to-5 on purpose. STANDARD
        /// grades at 25.7% against the bot with a 24.6% stalemate rate, so the
        /// time to five goals is a heavy-tailed random variable and a test that
        /// waited for it would be a coin flip dressed as an assertion - the exact
        /// mistake this project has already documented for 100-episode evals.
        /// What is asserted instead is bounded and deterministic: the agents
        /// reach the ball, an episode terminates ON ITS OWN, and the pitch resets
        /// and keeps stepping afterwards.
        /// </summary>
        [UnityTest]
        public IEnumerator Match_ProgressesUnderRealPlay()
        {
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(3);

            Click(FindButton(RootOf<Agent_MainMenu>(), "PLAY"));
            yield return Frames(4);

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(env, "PLAY did not land on a pitch");

            int touches = 0;
            int endings = 0;
            System.Action<Agent_Soccer> onTouch = _ => touches++;
            System.Action<Agent_Soccer.Team?> onEnd = _ => endings++;
            env.BallTouched += onTouch;
            env.EpisodeEnded += onEnd;

            try
            {
                yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 15f);
                Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                    "Opening countdown never released the clock, so play never started");

                // The exhibition stepCapOverride is 2500 steps at a 0.01 s fixed
                // timestep = 25 s of game time, so the watchdog alone guarantees a
                // termination well inside this window even if nobody ever scores.
                yield return WaitUntilRealtime(() => touches > 0 && endings > 0, 90f);

                Assert.Greater(touches, 0,
                    "No player touched the ball in 90 seconds of real play. The pitch is " +
                    "live and the clock is running, so this is locomotion or spawning, not " +
                    "the state machine - run Agent_PlayMode_MovementProbe.");
                Assert.Greater(endings, 0,
                    "No episode ended by itself in 90 seconds. Neither a goal nor the " +
                    "stalemate watchdog fired, so the match cannot advance without the " +
                    "test cheating - the loop is unreachable by playing.");

                // ...and the pitch is still alive on the other side of the reset.
                var hud = Object.FindAnyObjectByType<Agent_HUD>();
                if (hud != null && !hud.MatchOver)
                {
                    yield return WaitForClockToSettle(25f);
                    Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                        "The pitch never resumed after an episode ended under real play. " +
                        "Held by: " + Agent_TimeFreeze.DescribeHolders());

                    int stepsBefore = env.StepCount;
                    int endingsBefore = endings;
                    yield return Frames(30);

                    // Either the counter advanced, or another episode ended inside
                    // the window and reset it. Asserting only the former would fail
                    // on a goal landing in these 30 frames - a passing outcome
                    // reported as a stalled simulation.
                    Assert.IsTrue(env.StepCount > stepsBefore || endings > endingsBefore,
                        "The simulation stopped stepping after the reset");
                }
            }
            finally
            {
                env.BallTouched -= onTouch;
                env.EpisodeEnded -= onEnd;
            }
        }

        /// <summary>
        /// MENU is the only way out of a match or the gallery, so the chrome that
        /// carries it has to be present wherever a human is looking - and absent
        /// from training, where it would be pure cost.
        ///
        /// It used to be a serialized GameObject in two scenes and nothing else.
        /// That is the one component whose absence strands the player, and scene
        /// authoring here is MCP-only, so "remember to add the object" was a
        /// manual step guarding a dead end. Agent_Presentation.EnsureChrome now
        /// guarantees it; this is the assertion that keeps it guaranteed.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryPlayerFacingScene_CanBeLeftAgain()
        {
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(3);
            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_Chrome>(),
                "SCN_Menu has no Agent_Chrome");

            SceneManager.LoadScene("SCN_Exhibition");
            yield return Frames(3);
            var chrome = Object.FindAnyObjectByType<Agent_Chrome>();
            Assert.IsNotNull(chrome, "The match scene has no MENU chrome - the match is a dead end");

            Assert.AreEqual(1, Object.FindObjectsByType<Agent_Chrome>(FindObjectsSortMode.None).Length,
                "Two Agent_Chrome instances: the serialized object and a code-installed one " +
                "are both present, so the screen carries two MENU buttons.");

            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Chrome>(),
                "Agent_Chrome must never be installed in SCN_Training - a headless run " +
                "must not pay for screen chrome.");
        }

        /// <summary>
        /// Arriving at the menu clears the launch flags.
        ///
        /// GalleryMode decides whether SCN_Exhibition builds ONE pitch or a grid
        /// of six, and statics survive a scene load in a player build. Before
        /// this, the flag was correct only because StartMatch and OpenGallery both
        /// remembered to Clear() first - an invariant held by two call sites
        /// agreeing, whose failure mode is PLAY silently opening the gallery.
        /// </summary>
        [UnityTest]
        public IEnumerator ReturningToTheMenu_ClearsTheLaunchFlags()
        {
            Agent_MatchSetup.Clear();
            Agent_MatchSetup.GalleryMode = true;
            Agent_MatchSetup.Applied = true;

            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(3);

            Assert.IsFalse(Agent_MatchSetup.GalleryMode,
                "The menu inherited GalleryMode from the last launch, so PLAY would open " +
                "the checkpoint grid instead of a match.");
            Assert.IsFalse(Agent_MatchSetup.Applied,
                "The menu inherited a stale lineup rather than starting from nothing chosen.");
        }
    }
}
