using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Covers the GFX/audio pass added 2026-09-06: the goal impact overlays,
    /// pitch wear, the adaptive quality controller, haptics, the music stems and
    /// the templated menu.
    ///
    /// THE LOAD-BEARING TEST IS <see cref="TrainingScene_HasNoneOfIt"/>, for the
    /// same reason every other suite here has one. Three of these components own
    /// GLOBAL state - the camera, the frame budget, and a controller that
    /// switches other components off - and a headless training run must never pay
    /// for, or be perturbed by, any of it.
    ///
    /// Like the broadcast suite these drive SCN_Exhibition directly, which normal
    /// play must never do; nothing under test depends on the lineup.
    /// </summary>
    public class Agent_PlayMode_Gfx
    {
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();

            // Agent_MainMenu.OnEnable caps the frame rate at 60 - correct for the
            // game, wrong to leave behind for the rest of the suite. This fixture
            // is the first PlayMode test that ever loads SCN_Menu, so it is also
            // the first that can leak this, and several later tests race a real
            // clock (Agent_PlayMode_Spectator's countdown allows 12 s of wall time
            // for a ~3 s freeze). Handing the cap back keeps the fixtures
            // independent instead of coupling them through a static.
            Application.targetFrameRate = -1;
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
        public IEnumerator TrainingScene_HasNoneOfIt()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            yield return null;

            Assert.IsNull(Object.FindAnyObjectByType<Agent_ScreenFX>(),
                "Agent_ScreenFX must never be installed in SCN_Training - it parents " +
                "an overlay to the camera, and training clones 16 pitches around one.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Wear>(),
                "Agent_Wear must never be installed in SCN_Training - 16 cloned pitches " +
                "would each upload a texture several times a second.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Quality>(),
                "Agent_Quality must never be installed in SCN_Training - it switches " +
                "other components off based on frame time.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Haptics>(),
                "Agent_Haptics must never be installed in SCN_Training.");
            Assert.IsNull(GameObject.Find("PitchWear"),
                "A wear quad leaked into the training scene.");
            Assert.IsNull(GameObject.Find("GoalFlash"),
                "A screen overlay leaked into the training scene.");
            Assert.IsNull(GameObject.Find("GoalShockwave"),
                "A screen overlay leaked into the training scene.");
        }

        [UnityTest]
        public IEnumerator ExhibitionScene_InstallsTheLayer()
        {
            yield return LoadExhibition();

            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_ScreenFX>());
            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_Wear>());
            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_Quality>());
            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_Haptics>());
        }

        /// <summary>
        /// The impact overlays cost nothing between goals.
        ///
        /// They are created on first use and DISABLED rather than destroyed, so
        /// the failure this guards against is not a leak but a permanent extra
        /// transparent full-view quad - which on a phone is real overdraw for
        /// something that is invisible 99% of a match.
        /// </summary>
        [UnityTest]
        public IEnumerator ImpactOverlays_AreIdleUntilSomethingHappens()
        {
            yield return LoadExhibition();

            var screenFX = Object.FindAnyObjectByType<Agent_ScreenFX>();
            Assert.IsNotNull(screenFX);

            var flash = GameObject.Find("GoalFlash");
            if (flash != null)
            {
                Assert.IsFalse(flash.GetComponent<SpriteRenderer>().enabled,
                    "The goal flash is drawing with no goal to announce.");
            }

            // Fire one, and it must both appear and clear itself.
            screenFX.Impact(Vector2.zero, 1f);
            yield return null;

            var ring = GameObject.Find("GoalShockwave");
            Assert.IsNotNull(ring, "Impact() drew no shockwave.");
            Assert.IsTrue(ring.GetComponent<SpriteRenderer>().enabled,
                "Impact() left the shockwave disabled, so nothing was drawn.");

            float deadline = Time.realtimeSinceStartup + 4f;
            while (ring.GetComponent<SpriteRenderer>().enabled
                   && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsFalse(ring.GetComponent<SpriteRenderer>().enabled,
                "The shockwave never cleared itself, so it now costs a draw call " +
                "for the rest of the match.");
        }

        /// <summary>
        /// The wear quad has to sit ON the ground: above the pitch background and
        /// below everything that moves. Drawn over the players it reads as fog;
        /// drawn under the pitch it is invisible, and both look like "the effect
        /// is too subtle" rather than like a sorting bug.
        /// </summary>
        [UnityTest]
        public IEnumerator PitchWear_SitsOnTheGround()
        {
            yield return LoadExhibition();

            var quad = GameObject.Find("PitchWear");
            Assert.IsNotNull(quad, "No wear quad was built.");

            var wearRenderer = quad.GetComponent<SpriteRenderer>();
            Assert.IsNotNull(wearRenderer);

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            var pitch = env.transform.Find("PitchBG");
            Assert.IsNotNull(pitch, "PitchBG is gone; the wear quad cannot be placed.");
            var pitchRenderer = pitch.GetComponent<SpriteRenderer>();

            Assert.Greater(wearRenderer.sortingOrder, pitchRenderer.sortingOrder,
                "The wear is drawn under the pitch, so it can never be seen.");

            var agents = env.GetComponentsInChildren<Agent_Soccer>();
            for (int i = 0; i < agents.Length; i++)
            {
                if (!agents[i].TryGetComponent(out SpriteRenderer body)) continue;
                Assert.Less(wearRenderer.sortingOrder, body.sortingOrder,
                    "The wear is drawn over the players.");
            }

            // And it covers the pitch it is recording, at whatever size the pitch
            // currently is - Agent_PitchSizing rescales per squad.
            Vector2 half = env.PitchHalfExtents;
            Assert.AreEqual(half.x * 2f, quad.transform.localScale.x, 0.01f);
            Assert.AreEqual(half.y * 2f, quad.transform.localScale.y, 0.01f);
        }

        /// <summary>
        /// The quality controller sheds in the documented order. Checked by
        /// forcing tiers rather than by starving the frame rate, which no test
        /// can do reliably on an unknown machine.
        /// </summary>
        [UnityTest]
        public IEnumerator QualityTiers_ShedInOrder()
        {
            yield return LoadExhibition();

            var quality = Object.FindAnyObjectByType<Agent_Quality>();
            var screenFX = Object.FindAnyObjectByType<Agent_ScreenFX>();
            var wear = Object.FindAnyObjectByType<Agent_Wear>();
            Assert.IsNotNull(quality);

            quality.SetTier(0);
            Assert.IsTrue(screenFX.AllowContinuous, "Tier 0 must keep everything on.");
            Assert.IsTrue(wear.Visible, "Tier 0 must keep everything on.");

            quality.SetTier(1);
            Assert.IsFalse(screenFX.AllowContinuous, "Tier 1 must shed the impact overlays first.");
            Assert.IsTrue(wear.Visible, "Tier 1 must NOT shed the pitch wear yet.");

            quality.SetTier(2);
            Assert.IsFalse(wear.Visible, "Tier 2 must shed the pitch wear.");

            quality.SetTier(0);
            Assert.IsTrue(screenFX.AllowContinuous, "Recovery must restore what it shed.");
            Assert.IsTrue(wear.Visible, "Recovery must restore what it shed.");
        }

        /// <summary>
        /// Haptics are wired to real events and respect the mute. The buzz itself
        /// cannot be observed off-device, so the REQUEST is what is asserted -
        /// which is the whole reason Agent_Haptics counts them.
        /// </summary>
        [UnityTest]
        public IEnumerator Haptics_RespectTheMute()
        {
            yield return LoadExhibition();

            var haptics = Object.FindAnyObjectByType<Agent_Haptics>();
            Assert.IsNotNull(haptics);

            bool wasMuted = Agent_Audio.Muted;
            try
            {
                Agent_Audio.Muted = true;
                int before = Agent_Haptics.RequestCount;
                haptics.Play(30, 1f);
                Assert.AreEqual(before, Agent_Haptics.RequestCount,
                    "A muted game still asked the phone to vibrate. A player who has " +
                    "silenced the game in public has told you what they want.");

                Agent_Audio.Muted = false;
                haptics.Play(30, 1f);
                Assert.AreEqual(before + 1, Agent_Haptics.RequestCount,
                    "An unmuted game did not request a buzz.");
            }
            finally
            {
                Agent_Audio.Muted = wasMuted;
            }
        }

        /// <summary>
        /// The menu builds from its template and every control is live.
        ///
        /// THIS IS THE TEST THAT REPLACES LOOKING AT IT. Agent_MainMenu.Build
        /// resolves fourteen elements by name and returns early with a logged
        /// error if any is missing, which leaves a BLANK dark-green screen - and
        /// a blank screen is also what an unfocused editor produces when it barely
        /// runs play-mode frames (see CLAUDE.md). The two are indistinguishable in
        /// a screenshot, so the structure is asserted here instead.
        ///
        /// Agent_EditMode_Theme checks the template file contains the names; this
        /// checks the running panel actually has the elements, which is the half a
        /// text scan cannot prove.
        /// </summary>
        [UnityTest]
        public IEnumerator Menu_BuildsFromItsTemplate()
        {
            SceneManager.LoadScene("SCN_Menu");
            yield return null;
            yield return null;
            yield return null;

            var menu = Object.FindAnyObjectByType<Agent_MainMenu>();
            Assert.IsNotNull(menu, "SCN_Menu has no Agent_MainMenu.");

            var root = menu.GetComponent<UIDocument>().rootVisualElement;
            Assert.IsNotNull(root, "The menu's UIDocument has no root.");

            Assert.IsNotNull(root.Q<Label>("title"),
                "The template never cloned - the menu is a blank screen.");

            var play = root.Q<Button>("play");
            Assert.IsNotNull(play, "No PLAY button.");
            // The menu applies the 2v2 preset on load, so PLAY must be live
            // immediately: both sides have players.
            Assert.IsTrue(play.enabledSelf,
                "PLAY is disabled on a freshly loaded menu, so the default 2v2 preset " +
                "did not reach the squads.");

            var strip = root.Q<VisualElement>("strip-blue");
            Assert.IsNotNull(strip);
            Assert.AreEqual(2, strip.childCount,
                "The blue strip does not hold the 2v2 preset's two squad cards.");

            var roster = root.Q<VisualElement>("roster-blue");
            Assert.IsNotNull(roster);
            Assert.Greater(roster.childCount, 0, "The roster picker was never filled.");

            // The accessibility shelf: three buttons, on the first screen.
            var settings = root.Q<VisualElement>("settings");
            Assert.IsNotNull(settings);
            Assert.AreEqual(3, settings.childCount,
                "The accessibility shelf should hold palette, type scale and haptics.");

            // And the stylesheet is attached, or every class above is inert.
            Assert.Greater(root.styleSheets.count, 0,
                "PoSoccerTheme.uss is not attached to the menu panel; every class-driven " +
                "size and colour falls back to UI Toolkit's defaults.");
        }

        /// <summary>
        /// Switching the palette re-renders the menu with the new colours.
        ///
        /// The mechanism is a USS class on the panel root and nothing else, so
        /// the failure mode is silent: the setting persists, the label updates,
        /// and not one pixel changes.
        /// </summary>
        [UnityTest]
        public IEnumerator PaletteSwitch_ReachesTheRunningPanel()
        {
            SceneManager.LoadScene("SCN_Menu");
            yield return null;
            yield return null;
            yield return null;

            var menu = Object.FindAnyObjectByType<Agent_MainMenu>();
            var root = menu.GetComponent<UIDocument>().rootVisualElement;

            var previous = Agent_Palette.Current;
            try
            {
                Agent_Palette.Current = Agent_Palette.Mode.Standard;
                Agent_UIStyle.ApplyAccessibility(root);
                Assert.IsFalse(root.ClassListContains("palette--safe"));
                Assert.IsFalse(root.ClassListContains("palette--contrast"));

                Agent_Palette.Current = Agent_Palette.Mode.ColourSafe;
                Agent_UIStyle.ApplyAccessibility(root);
                Assert.IsTrue(root.ClassListContains("palette--safe"),
                    "The colour-safe class never reached the panel root, so the " +
                    "stylesheet's palette override cannot apply.");

                Agent_Palette.Current = Agent_Palette.Mode.HighContrast;
                Agent_UIStyle.ApplyAccessibility(root);
                Assert.IsTrue(root.ClassListContains("palette--contrast"));
                Assert.IsFalse(root.ClassListContains("palette--safe"),
                    "ApplyAccessibility left the previous palette's class behind, so two " +
                    "palettes are fighting over the same variables.");
            }
            finally
            {
                Agent_Palette.Current = previous;
            }
        }

        /// <summary>
        /// The three music stems load and start together.
        ///
        /// SAMPLE-LENGTH EQUALITY IS THE POINT. They are crossfaded live against
        /// each other, so a stem even a few milliseconds longer drifts out of
        /// phase with the others over a match and the harmony smears. That is
        /// exactly the property a hand-edited replacement would break, and it is
        /// invisible until you have listened for several minutes.
        /// </summary>
        [UnityTest]
        public IEnumerator MusicStems_AreLoadedAndAligned()
        {
            yield return LoadExhibition();

            var audio = Object.FindAnyObjectByType<Agent_Audio>();
            Assert.IsNotNull(audio);
            Assert.IsNotNull(audio.musicBed, "music_bed did not load from Resources/Audio.");
            Assert.IsNotNull(audio.musicTension, "music_tension did not load.");
            Assert.IsNotNull(audio.musicDrive, "music_drive did not load.");

            Assert.AreEqual(audio.musicBed.samples, audio.musicTension.samples,
                "The tension stem is a different length from the bed, so the two drift " +
                "apart as the match runs. Regenerate both with Editor_MakeAudioStems.");
            Assert.AreEqual(audio.musicBed.samples, audio.musicDrive.samples,
                "The drive stem is a different length from the bed.");
            Assert.AreEqual(audio.musicBed.frequency, audio.musicDrive.frequency,
                "The stems were rendered at different sample rates.");
        }
    }
}
