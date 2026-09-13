using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Runs the non-training scenes the way a player reaches them and fails on
    /// anything the game logged as an Error, Assert or Exception.
    ///
    /// WHAT THIS CATCHES THAT THE REST OF THE SUITE DOES NOT. Every other test in
    /// this project asserts a specific claim that somebody thought of in advance:
    /// the clock resumes, the chip row exists, the tracer lands within 0.35 m. A
    /// null reference thrown once per frame inside a presentation component
    /// violates none of them - the match still starts, still scores, still ends -
    /// and the only evidence is a console nobody reads after an unattended run.
    /// Agent_ErrorSink turns that console into an assertion.
    ///
    /// WHY IT SWEEPS LINEUPS. The four personalities differ only in a
    /// Reward_Settings asset, so it is tempting to test one and infer the rest.
    /// But brainModel is per profile, and three of the four are p21 checkpoints
    /// while STANDARD is p22 - the profiles are exactly where the roster is NOT
    /// uniform. A profile-specific null costs one line in a scene nobody replays.
    ///
    /// WHY THE MENU IS ALWAYS THE ENTRY POINT. CLAUDE.md's standing rule: the
    /// exhibition scene loaded on its own falls back to whatever is serialized in
    /// it, so a direct load tests a lineup nobody chose.
    ///
    /// THE ERROR BUDGET IS ZERO AND THAT IS DELIBERATE. If this goes red for a
    /// message that is genuinely expected, the fix is to stop logging it at Error
    /// severity, not to add it to an ignore list here. An ignore list is how a
    /// suite ends up green against a game that throws.
    /// </summary>
    public class Agent_PlayMode_SmokeRun
    {
        /// <summary>Seconds of real play per lineup. Long enough for the countdown, kickoff and contact.</summary>
        const float PLAY_SECONDS = 6f;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Agent_ErrorSink.Reset();
            yield return null;
        }

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

        /// <summary>Waits on real time, so a frozen or scaled clock cannot hang the run.</summary>
        static IEnumerator PlayRealtime(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        /// <summary>
        /// The shipped roster, read off Agent_MainMenu in SCN_Menu.
        ///
        /// NOT Resources.FindObjectsOfTypeAll. The profiles live in Assets/Agents,
        /// not in a Resources folder, so nothing loads them into memory until
        /// something references them - the first version of this helper used that
        /// call and swept an empty list, which passes every assertion about the
        /// lineups it did not run. The menu is where the roster actually lives, so
        /// reading it here also puts the roster wiring under test, exactly as
        /// Agent_PlayMode_Gallery.TrainedRoster already argues.
        /// </summary>
        static IEnumerator LoadRoster(List<Reward_Settings> into)
        {
            Agent_MatchSetup.Clear();
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(3);

            var menu = Object.FindAnyObjectByType<Agent_MainMenu>();
            Assert.IsNotNull(menu, "No Agent_MainMenu in SCN_Menu");

            // The bot is included deliberately: it is a roster entry a player can
            // pick, and picking it is the documented way a trained brain gets
            // played against the benchmark opponent inside a normal match.
            var candidates = new[] { menu.standard, menu.matt, menu.kim, menu.nick, menu.ruleBot };
            for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                if (candidates[candidateIndex] != null) into.Add(candidates[candidateIndex]);
            }

            Assert.IsNotEmpty(into,
                "The menu has no roster profiles wired. Every lineup below would " +
                "be skipped and this test would report green against a game with " +
                "no players, so it fails here instead.");
        }

        static Reward_Settings[] Squad(Reward_Settings profile, int size)
        {
            var squad = new Reward_Settings[size];
            for (int slot = 0; slot < size; slot++) squad[slot] = profile;
            return squad;
        }

        /// <summary>
        /// Fails with the whole report rather than a count. A smoke run that says
        /// "3 errors" and makes you go and find them is a smoke run people stop
        /// running.
        /// </summary>
        static void AssertNoErrors(string context)
        {
            Assert.AreEqual(0, Agent_ErrorSink.TotalCount,
                context + " logged errors:\n" + Agent_ErrorSink.Report());
        }

        [UnityTest]
        public IEnumerator Menu_LoadsAndLogsNothing()
        {
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(5);

            Assert.AreEqual("SCN_Menu", SceneManager.GetActiveScene().name);
            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_MainMenu>(),
                "SCN_Menu has no Agent_MainMenu");

            // The menu animates and builds its roster over several frames; a
            // one-frame check would miss anything thrown by the second one.
            yield return PlayRealtime(3f);

            AssertNoErrors("SCN_Menu");
        }

        /// <summary>
        /// One lineup per profile per squad size, each played for real seconds
        /// rather than teleported through. The point is the frames, not the score.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryLineup_PlaysWithoutLoggingAnError(
            [Values(1, 2)] int squadSize)
        {
            var profiles = new List<Reward_Settings>();
            yield return LoadRoster(profiles);

            var failures = new List<string>();

            for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
            {
                Reward_Settings profile = profiles[profileIndex];

                // The menu is the only thing that sets these, and Agent_MatchLoader
                // reads them at order -60 during the scene load below.
                Agent_MatchSetup.Clear();
                Agent_MatchSetup.BlueSquad = Squad(profile, squadSize);
                Agent_MatchSetup.RedSquad = Squad(profile, squadSize);
                Agent_MatchSetup.Applied = true;

                Agent_ErrorSink.Reset();

                SceneManager.LoadScene("SCN_Exhibition");
                yield return Frames(5);

                var env = Object.FindAnyObjectByType<Agent_EnvController>();
                Assert.IsNotNull(env, "No pitch in SCN_Exhibition for " + profile.name);

                Agent_Soccer[] players = env.GetComponentsInChildren<Agent_Soccer>();
                Assert.AreEqual(squadSize * 2, players.Length,
                    "Lineup handoff dropped players for " + profile.name);

                yield return PlayRealtime(PLAY_SECONDS);

                if (Agent_ErrorSink.TotalCount > 0)
                {
                    failures.Add(profile.name + " " + squadSize + "v" + squadSize + ":\n"
                                 + Agent_ErrorSink.Report());
                }

                Agent_TimeFreeze.ReleaseAll();
            }

            if (failures.Count > 0)
            {
                Assert.Fail(failures.Count + " of " + profiles.Count
                            + " lineups logged errors at " + squadSize + "v" + squadSize
                            + ":\n\n" + string.Join("\n", failures));
            }
        }

        /// <summary>
        /// The gallery is the other non-training entry point, and it is the one
        /// that clones the pitch - so it is where a component that should have
        /// gated on IsVisualScene instead of IsMatchScene shows up as six copies
        /// fighting over one camera.
        /// </summary>
        [UnityTest]
        public IEnumerator Gallery_RunsWithoutLoggingAnError()
        {
            Agent_MatchSetup.Clear();
            Agent_MatchSetup.GalleryMode = true;
            Agent_MatchSetup.Applied = true;
            Agent_ErrorSink.Reset();

            SceneManager.LoadScene("SCN_Exhibition");
            yield return Frames(5);
            yield return PlayRealtime(PLAY_SECONDS);

            AssertNoErrors("The checkpoint gallery");
        }
    }
}
