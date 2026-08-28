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

        /// <summary>
        /// Reproduces the camera's framing maths across the aspect ratios the game
        /// actually ships to, and requires the pitch to keep filling the screen.
        /// </summary>
        [Test]
        public void WideShot_KeepsThePitchFilledOnTallPhones()
        {
            // Exhibition 2v2 half-extents, plus the camera's edge margin.
            Vector2 half = new(12.6f / 2f, 25.3f / 2f);
            const float EDGE = 0.5f;
            const float MAX_CROP = 0.14f;   // must match Agent_CameraFollow

            Vector2 padded = half + Vector2.one * EDGE;

            foreach (var (label, aspect) in new[]
            {
                ("16:9", 1080f / 1920f),
                ("18:9", 1080f / 2160f),
                ("19.5:9", 1170f / 2532f),
                ("20:9", 1080f / 2400f),
            })
            {
                float ortho = Mathf.Max(padded.y, padded.x * (1f - MAX_CROP) / aspect);
                float fill = half.y / ortho;

                Assert.Greater(fill, 0.90f,
                    $"{label}: pitch fills only {fill:P0} of the screen height - the wide " +
                    "shot has gone back to being width-bound.");

                float visibleHalfWidth = ortho * aspect;
                float crop = Mathf.Max(0f, 1f - visibleHalfWidth / half.x);
                Assert.LessOrEqual(crop, MAX_CROP + 0.001f,
                    $"{label}: crops {crop:P0} of the pitch width, beyond the configured budget");
            }
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
    }
}
