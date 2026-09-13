using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Portrait framing, safe area, pause and back-button behaviour.
    ///
    /// The framing test is the one that earns its keep. The old wide shot was
    /// max(halfY, halfX / aspect), which fits the pitch but on 18:9 and taller
    /// screens becomes WIDTH-bound: the camera pulls back and the pitch shrinks,
    /// leaving ~2.5 world units of dead ground past each goal line on a 20:9
    /// phone. Nothing threw, nothing logged - it just framed badly on exactly the
    /// devices this game targets. So the assertion is on the measured framing,
    /// not on the absence of an error.
    /// </summary>
    public class Agent_PlayMode_Portrait
    {
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            yield return null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

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

        [Test]
        public void Orientation_IsLockedToUprightPortrait()
        {
            // A fixed default orientation means autorotation never runs, so the
            // allowedAutorotate* flags are inert - this is the setting that
            // actually decides it.
            Assert.AreEqual(ScreenOrientation.Portrait, Screen.orientation,
                "Runtime orientation is not upright portrait");
        }

        /// <summary>The aspect ratios the game actually ships to.</summary>
        static readonly (string Label, float Aspect)[] ShippedAspects =
        {
            ("16:9", 1080f / 1920f),
            ("18:9", 1080f / 2160f),
            ("19.5:9", 1170f / 2532f),
            ("20:9", 1080f / 2400f),
            ("21:9", 1080f / 2520f),
        };

        /// <summary>
        /// Requires the pitch to keep filling the screen across every shipped
        /// aspect AND every squad size the menu can produce.
        ///
        /// TWO THINGS CHANGED HERE ON 2026-09-06, both about what the test was
        /// actually measuring.
        ///
        /// 1. IT USED TO RESTATE THE FORMULA. The body carried its own
        ///    `Mathf.Max(padded.y, padded.x * (1 - CROP) / aspect)` under a comment
        ///    reading "must match Agent_CameraFollow" - so it asserted against a
        ///    copy, and the single edit it exists to catch (someone changing the
        ///    real wide shot) would have left it green. It now calls
        ///    Agent_CameraFollow.WideOrthoSize, and reads the crop and margin from
        ///    that class's own constants, so there is nothing left to drift.
        ///
        /// 2. IT ONLY EVER CHECKED 2v2, hard-coded. Agent_PitchSizing resizes the
        ///    pitch per head count, so the aspect ratio of the thing being framed
        ///    is itself a function of squad size - which is exactly the axis a
        ///    fixed-extent test cannot see. 1v1 and 10v10 clamp to opposite ends of
        ///    [MIN_WIDTH, MAX_WIDTH] and are the two cases most likely to break.
        /// </summary>
        [Test]
        public void WideShot_KeepsThePitchFilledOnEveryShippedAspectAndSquadSize()
        {
            const float EDGE = Agent_CameraFollow.DEFAULT_EDGE_MARGIN;
            const float MAX_CROP = Agent_CameraFollow.DEFAULT_MAX_WIDTH_CROP;

            for (int perSide = 1; perSide <= Agent_MatchSetup.MAX_SQUAD; perSide++)
            {
                Vector2 half = Agent_PitchSizing.HalfExtentsFor(perSide, perSide);
                Vector2 padded = half + Vector2.one * EDGE;

                foreach (var (label, aspect) in ShippedAspects)
                {
                    float ortho = Agent_CameraFollow.WideOrthoSize(padded, aspect, MAX_CROP);
                    string where = $"{perSide}v{perSide} @ {label}";

                    float fill = half.y / ortho;
                    Assert.Greater(fill, 0.90f,
                        $"{where}: pitch fills only {fill:P0} of the screen height - the " +
                        "wide shot has gone back to being width-bound.");

                    float visibleHalfWidth = ortho * aspect;
                    float crop = Mathf.Max(0f, 1f - visibleHalfWidth / half.x);
                    Assert.LessOrEqual(crop, MAX_CROP + 0.001f,
                        $"{where}: crops {crop:P0} of the pitch width, beyond the budget.");
                }
            }
        }

        /// <summary>
        /// The formula must never letterbox: on a screen at least as tall as the
        /// pitch, the wide shot is the pitch height exactly, not something larger.
        /// This is the property the pre-2026-09-06 `max(halfY, halfX / aspect)`
        /// version failed, and it failed silently - nothing threw, the pitch just
        /// shrank on precisely the phones this game targets.
        /// </summary>
        [Test]
        public void WideShot_IsHeightBound_WhereverTheScreenIsTallEnough()
        {
            Vector2 padded = Agent_PitchSizing.HalfExtentsFor(2, 2)
                             + Vector2.one * Agent_CameraFollow.DEFAULT_EDGE_MARGIN;

            float ortho = Agent_CameraFollow.WideOrthoSize(
                padded, 1080f / 1920f, Agent_CameraFollow.DEFAULT_MAX_WIDTH_CROP);

            Assert.AreEqual(padded.y, ortho, 0.001f,
                "At 16:9 the pitch is narrower than the viewport, so the wide shot must " +
                "resolve to the pitch height. A larger value means the width term won " +
                "and the pitch is being letterboxed.");
        }

        /// <summary>A camera reporting no aspect yet must not divide by ~zero.</summary>
        [Test]
        public void WideShot_FallsBackToPortraitWhenTheAspectIsNotReadyYet()
        {
            Vector2 padded = Agent_PitchSizing.HalfExtentsFor(2, 2) + Vector2.one * 0.5f;

            float unset = Agent_CameraFollow.WideOrthoSize(padded, 0f, 0.14f);
            float portrait = Agent_CameraFollow.WideOrthoSize(
                padded, Agent_CameraFollow.FALLBACK_ASPECT, 0.14f);

            Assert.AreEqual(portrait, unset, 0.001f);
            Assert.IsFalse(float.IsInfinity(unset) || float.IsNaN(unset));
        }

        [UnityTest]
        public IEnumerator Pause_HoldsAndReleasesTheClock()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            yield return Frames(3);
            yield return WaitForClockToSettle(15f);

            var hud = Object.FindAnyObjectByType<Agent_HUD>();
            Assert.IsNotNull(hud);
            Assert.IsFalse(hud.IsPaused, "Match started paused");

            hud.TogglePause();
            yield return null;
            Assert.IsTrue(hud.IsPaused, "Pause did not engage");
            Assert.IsTrue(Agent_TimeFreeze.IsFrozen, "Pause did not stop the clock");
            Assert.AreEqual(0f, Time.timeScale, 0.0001f);

            hud.TogglePause();
            yield return null;
            Assert.IsFalse(hud.IsPaused, "Resume did not clear the pause panel");
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                "Resume left a freeze holder. Held by: " + Agent_TimeFreeze.DescribeHolders());
            Assert.AreEqual(1f, Time.timeScale, 0.0001f);
        }

        /// <summary>
        /// Pause composes with the other holders rather than fighting them: while
        /// something else already owns the clock, pausing and resuming must not
        /// hand it back underneath that other holder.
        /// </summary>
        [UnityTest]
        public IEnumerator Pause_DoesNotStealTheClockFromTheCountdown()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            yield return Frames(3);

            Assert.IsTrue(Agent_TimeFreeze.IsFrozen, "Expected the opening countdown to hold");

            var hud = Object.FindAnyObjectByType<Agent_HUD>();
            hud.TogglePause();
            yield return null;
            hud.TogglePause();
            yield return null;

            Assert.IsTrue(Agent_TimeFreeze.IsFrozen,
                "Resuming from pause released the countdown's hold as well - the match " +
                "would start under its own countdown.");

            yield return WaitForClockToSettle(15f);
            Assert.IsFalse(Agent_TimeFreeze.IsFrozen,
                "Held by: " + Agent_TimeFreeze.DescribeHolders());
        }

        [UnityTest]
        public IEnumerator Menu_HasASoundToggle()
        {
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(3);

            var menu = Object.FindAnyObjectByType<Agent_MainMenu>();
            var root = menu.GetComponent<UIDocument>().rootVisualElement;

            bool found = false;
            root.Query<Button>().ForEach(b =>
            {
                if (b.text != null && b.text.StartsWith("SND")) found = true;
            });
            Assert.IsTrue(found,
                "Menu has no sound toggle - mute was only reachable from inside a match, " +
                "which you cannot open without starting one.");
        }

        [UnityTest]
        public IEnumerator Telemetry_SitsInsideTheSafeArea()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            yield return Frames(3);

            var telemetry = Object.FindAnyObjectByType<Agent_Telemetry>();
            Assert.IsNotNull(telemetry);
            telemetry.SetVisible(true);
            yield return Frames(2);

            var doc = telemetry.GetComponent<UIDocument>();
            Assert.IsNotNull(doc, "Telemetry never built its overlay");

            var label = doc.rootVisualElement.Q(className: "telemetry");
            Assert.IsNotNull(label, "Telemetry label missing");
            Assert.AreNotSame(doc.rootVisualElement, label.parent,
                "Telemetry is parented straight to the panel root, so it ignores the " +
                "safe area and can land under a notch.");
        }

        /// <summary>
        /// Every button the player can tap is at least --touch-min tall.
        ///
        /// Agent_Chrome had this policy in C# (BUTTON_HEIGHT = 120, with a comment
        /// telling the next person not to shrink it) and enforced it on the two
        /// buttons it happens to own. Nothing else did. Measured live on
        /// 2026-09-06 the HUD's sound and pause buttons were 80.75 units tall -
        /// about 5.1 mm of glass against Android's 48dp (~126 units at this
        /// reference width) - and the menu's presets, steppers and roster buttons
        /// were 74, 86 and 78. A policy that holds on one screen and not the
        /// others is not a policy, so it now lives in the stylesheet as one token
        /// on .btn, and this is the test that makes it true everywhere.
        ///
        /// The assertion is on RESOLVED layout, not on the declared rule, because
        /// the failure being guarded against is a more specific selector quietly
        /// setting `height` under the minimum - which is exactly how three of the
        /// four cases above happened.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryTappableButton_MeetsTheTouchTargetMinimum()
        {
            const float MIN = 120f;      // must equal --touch-min in PoSoccerTheme.uss

            foreach (string scene in new[] { "SCN_Menu", "SCN_Exhibition" })
            {
                SceneManager.LoadScene(scene);
                yield return Frames(4);   // build, then a frame for layout to resolve

                int checkedCount = 0;
                var offenders = new System.Text.StringBuilder();

                foreach (var doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                {
                    var root = doc.rootVisualElement;
                    if (root == null) continue;

                    root.Query<Button>().ForEach(b =>
                    {
                        // Skip anything not actually laid out this frame: a hidden
                        // panel reports NaN, and asserting on that would fail for a
                        // reason that has nothing to do with touch targets.
                        float h = b.resolvedStyle.height;
                        if (float.IsNaN(h) || b.resolvedStyle.display == DisplayStyle.None) return;

                        checkedCount++;
                        if (h < MIN - 0.5f)
                        {
                            offenders.AppendLine(
                                $"  '{b.text}' on {doc.gameObject.name}: {h:F1} units tall");
                        }
                    });
                }

                Assert.Greater(checkedCount, 0,
                    $"{scene}: found no laid-out buttons at all, so this asserted nothing");
                Assert.AreEqual(string.Empty, offenders.ToString(),
                    $"{scene}: buttons below the {MIN}-unit touch target " +
                    $"(~7.6 mm, Android's 48dp floor):\n{offenders}");
            }
        }

        /// <summary>
        /// The menu still fits the screen after the touch targets grew.
        ///
        /// Raising every button to 120 units added real height to a portrait
        /// column that was already full, and the honest failure mode is PLAY
        /// sliding off the bottom of the safe area where nothing reports it.
        /// </summary>
        [UnityTest]
        public IEnumerator Menu_FitsWithinTheSafeArea()
        {
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(4);

            var root = RootOfMenu();
            var safe = root.Q<VisualElement>("safe");
            Assert.IsNotNull(safe, "Menu template has no 'safe' container");

            Button play = null;
            root.Query<Button>().ForEach(b => { if (b.text == "PLAY") play = b; });
            Assert.IsNotNull(play, "Menu has no PLAY button");

            Rect playRect = play.worldBound;
            Rect safeRect = safe.worldBound;

            Assert.LessOrEqual(playRect.yMax, safeRect.yMax + 1f,
                $"PLAY runs past the bottom of the safe area ({playRect.yMax:F0} vs " +
                $"{safeRect.yMax:F0}) - the button that starts the game is off screen.");
            Assert.GreaterOrEqual(playRect.yMin, safeRect.yMin - 1f,
                "PLAY runs past the top of the safe area.");
        }

        /// <summary>
        /// No primary screen asks the player to scroll.
        ///
        /// The project targets locked portrait on a phone, and the design rule is
        /// that a screen fits. Menu_FitsWithinTheSafeArea already pins that for
        /// the menu, but it pins ONE element (PLAY) against ONE container, which
        /// is the thing that was known to be at risk in 2026-09-06 when the touch
        /// targets grew. It says nothing about the HUD, the pause panel or the end
        /// panel, and nothing at all about the mechanism by which a full screen
        /// stops being a full screen: somebody wraps the overflowing column in a
        /// ScrollView and the symptom disappears while the layout defect stays.
        ///
        /// A ScrollView is not banned because scrolling is wrong - it is banned
        /// HERE because it is the silent fix. Content that no longer fits is a
        /// design decision someone should have to make on purpose; a scroll
        /// container makes it never surface.
        ///
        /// ONE DOCUMENTED EXEMPTION, and it is deliberately not in this list.
        /// Agent_MainMenu.OpenCards builds a scrolling player card, because
        /// personalityNotes is free-authored text that runs to several lines on
        /// KIM and NICK, so that card's height is content-driven and genuinely
        /// cannot be bounded at authoring time - the comment there records the
        /// alternative, which is the provenance line and the buttons walking off
        /// the bottom on exactly those two profiles. It is a transient modal, so
        /// it does not exist on a freshly loaded screen and this test never sees
        /// it. If it ever becomes permanent, this test starts failing, which is
        /// the correct outcome: that would be a new decision.
        /// </summary>
        [UnityTest]
        public IEnumerator NoPrimaryScreen_RequiresVerticalScrolling()
        {
            foreach (string scene in new[] { "SCN_Menu", "SCN_Exhibition" })
            {
                SceneManager.LoadScene(scene);
                yield return Frames(4);

                var offenders = new System.Text.StringBuilder();
                foreach (var doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                {
                    VisualElement root = doc.rootVisualElement;
                    if (root == null) continue;

                    root.Query<ScrollView>().ForEach(sv =>
                        offenders.AppendLine(
                            $"  ScrollView '{sv.name}' on {doc.gameObject.name}"));
                }

                Assert.AreEqual(string.Empty, offenders.ToString(),
                    $"{scene} builds a scrolling container on a screen that is " +
                    $"supposed to fit:\n{offenders}" +
                    "Either the content genuinely no longer fits portrait - in which " +
                    "case cut or paginate it - or this is hiding a layout defect.");
            }
        }

        /// <summary>
        /// Every laid-out label and button sits inside its own panel.
        ///
        /// This is the general form of Menu_FitsWithinTheSafeArea. That test
        /// measures PLAY against the safe container because PLAY was the element
        /// known to be at risk; this one measures everything that carries text or
        /// takes a tap, on both player-facing scenes, against the panel it is
        /// drawn on. An element outside the panel is content the player cannot
        /// read or reach, and UI Toolkit reports it exactly the way it reports a
        /// correct layout: silently.
        ///
        /// Measured on RESOLVED world bounds rather than declared style, for the
        /// same reason the touch-target test is - the failures this catches come
        /// from cascade and layout, not from a rule anyone wrote down.
        ///
        /// NaN and display:none are skipped: a panel that is not laid out this
        /// frame reports NaN, and failing on that would report a scene-timing
        /// problem as an overflow. The HUD's toasts legitimately start off screen
        /// and animate in, so only the horizontal bound is enforced on elements
        /// that are mid-transition vertically - a toast off the TOP is a toast
        /// doing its job, while anything past the left or right edge is clipped
        /// on every device regardless of animation state.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryVisibleLabelAndButton_StaysInsideItsPanel()
        {
            const float SLACK = 2f;   // sub-pixel rounding in resolved layout

            foreach (string scene in new[] { "SCN_Menu", "SCN_Exhibition" })
            {
                SceneManager.LoadScene(scene);
                yield return Frames(4);

                int checkedCount = 0;
                var offenders = new System.Text.StringBuilder();

                foreach (var doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                {
                    VisualElement root = doc.rootVisualElement;
                    if (root == null) continue;

                    Rect panel = root.worldBound;
                    if (panel.width <= 1f || float.IsNaN(panel.width)) continue;

                    root.Query<VisualElement>().ForEach(element =>
                    {
                        if (element is not (Label or Button)) return;
                        if (element.resolvedStyle.display == DisplayStyle.None) return;

                        Rect r = element.worldBound;
                        if (float.IsNaN(r.x) || float.IsNaN(r.y)) return;
                        if (r.width <= 0.5f || r.height <= 0.5f) return;

                        checkedCount++;

                        string label = element is Button b && !string.IsNullOrEmpty(b.text)
                            ? b.text
                            : element.name;

                        if (r.xMin < panel.xMin - SLACK || r.xMax > panel.xMax + SLACK)
                        {
                            offenders.AppendLine(
                                $"  '{label}' on {doc.gameObject.name} runs off the side: " +
                                $"x {r.xMin:F0}..{r.xMax:F0} vs panel {panel.xMin:F0}..{panel.xMax:F0}");
                        }
                        else if (r.yMin > panel.yMax + SLACK)
                        {
                            offenders.AppendLine(
                                $"  '{label}' on {doc.gameObject.name} sits below the screen: " +
                                $"y {r.yMin:F0}..{r.yMax:F0} vs panel {panel.yMin:F0}..{panel.yMax:F0}");
                        }
                    });
                }

                Assert.Greater(checkedCount, 0,
                    $"{scene}: found no laid-out labels or buttons, so this asserted nothing");
                Assert.AreEqual(string.Empty, offenders.ToString(),
                    $"{scene}: UI outside the panel - unreadable or untappable on " +
                    $"every device:\n{offenders}");
            }
        }

        static VisualElement RootOfMenu()
        {
            var menu = Object.FindAnyObjectByType<Agent_MainMenu>();
            Assert.IsNotNull(menu, "SCN_Menu has no Agent_MainMenu");
            return menu.GetComponent<UIDocument>().rootVisualElement;
        }
    }
}
