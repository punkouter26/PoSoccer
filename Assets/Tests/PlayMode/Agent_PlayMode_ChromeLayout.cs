using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the screen-chrome corner map, and - the part that actually earns its
    /// keep - pins that the top-centre FPS readout CLEARS whatever else owns the
    /// top centre.
    ///
    /// WHY A TEST AND NOT A COMMENT. This exact position has been wrong before.
    /// The readout used to sit top-centre with a hand-tuned marginTop of 168 meant
    /// to clear the scoreboard; measured 2026-09-07 at 1170x2532 it printed
    /// straight through the score, a 178x61 px overprint present in every match,
    /// because the constant had been derived from a band that the broadcast layer
    /// later grew a win-probability strip and a stat ticker onto. It was then
    /// moved to the left corner to dodge the problem. Putting it back in the
    /// centre re-opens exactly that failure mode, so the position is asserted
    /// here against the resolved rectangles of the OTHER documents on the panel.
    ///
    /// The readout now sits in Agent_Chrome's own top row, which is a strip
    /// nothing else draws into, rather than at a measured offset. Two runtime
    /// clearance algorithms were tried on device first and both put it somewhere
    /// worse - over the roster cards, then over a stepper - which is why the
    /// assertion below is on where it ENDS UP rather than on the rule that put
    /// it there.
    ///
    /// Note what a weaker version of this test would miss. Asserting that the
    /// label EXISTS, or that its style says centred, passes just as happily while
    /// the text sits on top of the score - the two elements live in different
    /// UIDocuments and neither knows about the other. The assertion has to be on
    /// the resolved rectangles.
    /// </summary>
    public class Agent_PlayMode_ChromeLayout
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
        /// Chrome measures its clearance on a 0.5 s timer, so a test that reads
        /// the layout on the frame after load reads the pre-measurement position.
        /// Wait in REAL time - a match scene can hold the game clock frozen during
        /// the kickoff countdown.
        /// </summary>
        static IEnumerator Settle(float seconds = 1.5f)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        static UIDocument ChromeDocument()
        {
            var chrome = Object.FindAnyObjectByType<Agent_Chrome>();
            Assert.IsNotNull(chrome, "No Agent_Chrome in the scene");
            var document = chrome.GetComponent<UIDocument>();
            Assert.IsNotNull(document, "Agent_Chrome has no UIDocument");
            Assert.IsNotNull(document.rootVisualElement, "Chrome UIDocument has no root");
            return document;
        }

        static VisualElement Corner(UIDocument document, string name)
        {
            VisualElement element = document.rootVisualElement.Q<VisualElement>(name);
            Assert.IsNotNull(element, $"Chrome is missing '{name}'");
            return element;
        }

        static bool Overlaps(Rect a, Rect b)
        {
            return a.xMin < b.xMax && b.xMin < a.xMax && a.yMin < b.yMax && b.yMin < a.yMax;
        }

        // ------------------------------------------------------------------
        // The corner map
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator EveryPlayerFacingScene_KeepsTheStandardCornerMap()
        {
            foreach (string sceneName in new[] { "SCN_Menu", "SCN_Exhibition" })
            {
                SceneManager.LoadScene(sceneName);
                yield return Frames(3);
                yield return Settle();

                UIDocument document = ChromeDocument();
                Rect panel = document.rootVisualElement.worldBound;
                Assert.Greater(panel.width, 1f, $"[{sceneName}] chrome panel never resolved");

                float third = panel.width / 3f;
                var checks = new (string Name, bool Left, bool Top)[]
                {
                    ("chrome-title", true, true),
                    ("chrome-menu", false, true),
                    ("chrome-debug", true, false),
                    ("chrome-version", false, false),
                };

                foreach ((string name, bool left, bool top) in checks)
                {
                    Rect bounds = Corner(document, name).worldBound;
                    Assert.IsFalse(float.IsNaN(bounds.x),
                        $"[{sceneName}] {name} has no resolved layout");
                    Assert.Greater(bounds.height, 1f,
                        $"[{sceneName}] {name} collapsed to {bounds.height:0.0} px high - " +
                        "laid out but invisible, which every existence check still passes");

                    // Assert on the EDGE, not the centre. The title and the
                    // version are stretched labels whose text is aligned to one
                    // side - deliberately, so that a changing string cannot
                    // re-measure the label and dirty the panel every frame - so
                    // their centres sit near the middle of the screen while the
                    // text they draw is hard against a corner.
                    if (left)
                    {
                        Assert.Less(bounds.xMin, panel.xMin + third,
                            $"[{sceneName}] {name} does not start in the left third " +
                            $"(left edge x={bounds.xMin:0})");
                    }
                    else
                    {
                        Assert.Greater(bounds.xMax, panel.xMax - third,
                            $"[{sceneName}] {name} does not reach the right third " +
                            $"(right edge x={bounds.xMax:0})");
                    }

                    float centreY = bounds.center.y;
                    if (top)
                    {
                        Assert.Less(centreY, panel.center.y,
                            $"[{sceneName}] {name} is not in the top half (centre y={centreY:0})");
                    }
                    else
                    {
                        Assert.Greater(centreY, panel.center.y,
                            $"[{sceneName}] {name} is not in the bottom half (centre y={centreY:0})");
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // The one that has been wrong before
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator TheFpsReadout_IsTopCentreAndClearsEverythingElseInTheCentre()
        {
            foreach (string sceneName in new[] { "SCN_Menu", "SCN_Exhibition" })
            {
                SceneManager.LoadScene(sceneName);
                yield return Frames(3);
                yield return Settle();

                UIDocument document = ChromeDocument();
                Rect panel = document.rootVisualElement.worldBound;
                var fps = (Label)Corner(document, "chrome-fps");
                Rect bounds = fps.worldBound;

                Assert.AreEqual(TextAnchor.MiddleCenter, fps.resolvedStyle.unityTextAlign,
                    $"[{sceneName}] the readout is not centred within its label");

                // The label is stretched edge to edge on purpose - see the note in
                // Agent_Chrome - so "centred" is the text alignment above plus a
                // band that is actually symmetric about the panel centre.
                Assert.That(bounds.center.x, Is.EqualTo(panel.center.x).Within(panel.width * 0.02f),
                    $"[{sceneName}] the readout band is not centred on the panel");

                Assert.Less(bounds.center.y, panel.height * 0.5f,
                    $"[{sceneName}] the readout has been pushed out of the top half " +
                    $"(centre y={bounds.center.y:0} of {panel.height:0})");

                // Unclipped: entirely on screen.
                Assert.GreaterOrEqual(bounds.yMin, panel.yMin - 0.5f,
                    $"[{sceneName}] the readout is clipped off the top");
                Assert.LessOrEqual(bounds.yMax, panel.yMax + 0.5f,
                    $"[{sceneName}] the readout is clipped off the bottom");

                // It shares the top bar with the title and the MENU button. This
                // is what makes the position stable without measuring anything:
                // if the readout ever leaves that row, it is loose on the screen
                // again and the next thing it lands on is a matter of luck.
                Rect title = Corner(document, "chrome-title").worldBound;
                Assert.That(bounds.center.y, Is.EqualTo(title.center.y).Within(title.height + 8f),
                    $"[{sceneName}] the readout has left the top bar " +
                    $"(readout centre y={bounds.center.y:0}, title centre y={title.center.y:0})");

                // THE ASSERTION THIS FILE EXISTS FOR. No text drawn by any other
                // document sharing this panel may sit under the readout.
                List<string> collisions = CentreCollisions(document, fps);
                Assert.IsEmpty(collisions,
                    $"[{sceneName}] the FPS readout at y={bounds.yMin:0}..{bounds.yMax:0} " +
                    "overprints: " + string.Join(", ", collisions));
            }
        }

        /// <summary>
        /// Names every piece of READABLE CONTENT from another UIDocument on the
        /// same panel that the readout's rectangle lands on.
        ///
        /// Text, specifically - a Label carrying a non-empty string. That is the
        /// defect this file exists to prevent: "60 FPS" printed through the
        /// score. An earlier version of this counted anything with a painted
        /// background too, which sounds stricter and is actually worse: it fires
        /// on decorative strips and transparent letterboxes that no reader would
        /// call a collision, and a test that cannot be satisfied gets relaxed
        /// rather than fixed. Overlapping a panel's backdrop is a layering
        /// question; overlapping its text is a bug.
        /// </summary>
        static List<string> CentreCollisions(UIDocument chromeDocument, VisualElement fps)
        {
            var hits = new List<string>();
            Rect target = fps.worldBound;
            if (float.IsNaN(target.x)) return hits;

            float panelHeight = chromeDocument.rootVisualElement.worldBound.height;
            UIDocument[] documents = Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);

            foreach (UIDocument document in documents)
            {
                if (document == null || document == chromeDocument) continue;
                if (document.panelSettings != chromeDocument.panelSettings) continue;
                VisualElement root = document.rootVisualElement;
                if (root == null) continue;

                var stack = new Stack<VisualElement>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    VisualElement element = stack.Pop();
                    for (int i = 0; i < element.childCount; i++) stack.Push(element[i]);

                    if (element.resolvedStyle.display == DisplayStyle.None) continue;
                    if (element.resolvedStyle.visibility == Visibility.Hidden) continue;

                    Rect bounds = element.worldBound;
                    if (float.IsNaN(bounds.height) || bounds.height < 1f) continue;
                    if (bounds.height > panelHeight * 0.6f) continue;

                    if (!(element is Label label) || string.IsNullOrWhiteSpace(label.text)) continue;
                    if (label.resolvedStyle.opacity < 0.05f) continue;

                    if (Overlaps(target, bounds))
                    {
                        hits.Add($"'{label.text}' [{bounds.yMin:0}..{bounds.yMax:0}]");
                    }
                }
            }
            return hits;
        }

        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator EveryChromeElement_StaysInsideTheSafeArea()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            yield return Frames(3);
            yield return Settle();

            UIDocument document = ChromeDocument();
            Rect panel = document.rootVisualElement.worldBound;

            // Screen.safeArea is in device pixels and the panel is scaled; convert
            // through the same ratio the panel itself resolved to, rather than
            // comparing two different coordinate spaces and calling it a pass.
            float scale = Screen.height > 0 ? panel.height / Screen.height : 1f;
            float insetTop = (Screen.height - Screen.safeArea.yMax) * scale;
            float insetBottom = Screen.safeArea.yMin * scale;

            foreach (string name in new[]
                     { "chrome-title", "chrome-fps", "chrome-menu", "chrome-debug", "chrome-version" })
            {
                Rect bounds = Corner(document, name).worldBound;
                Assert.GreaterOrEqual(bounds.yMin, panel.yMin + insetTop - 1f,
                    $"{name} reaches into the top cutout inset ({insetTop:0.0} px)");
                Assert.LessOrEqual(bounds.yMax, panel.yMax - insetBottom + 1f,
                    $"{name} reaches into the bottom gesture inset ({insetBottom:0.0} px)");
                Assert.GreaterOrEqual(bounds.xMin, panel.xMin - 1f, $"{name} is clipped off the left");
                Assert.LessOrEqual(bounds.xMax, panel.xMax + 1f, $"{name} is clipped off the right");
            }
        }
    }
}
