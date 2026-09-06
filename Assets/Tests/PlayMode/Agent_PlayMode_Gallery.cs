using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.MLAgents.Policies;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Covers the checkpoint gallery: a grid of pitches, one brain each, all
    /// against the same bot.
    ///
    /// The gallery is the one feature in the broadcast layer that RESHAPES THE
    /// SCENE rather than drawing over it - it clones the pitch root, rewires
    /// policies on the clones, and takes the camera off Agent_CameraFollow. That
    /// makes it the one with failure modes a screenshot would not reveal:
    ///
    ///  - a clone that re-runs the cloning component (a fork bomb of pitches);
    ///  - a clone whose blue agent silently kept the model from pitch zero, so
    ///    six pitches show one brain while six captions claim otherwise;
    ///  - the match-flow layer coming along for the ride, which would put six
    ///    replays and six countdowns on one clock.
    ///
    /// Each of those is asserted below, because each one produces a screen that
    /// looks approximately right.
    /// </summary>
    public class Agent_PlayMode_Gallery
    {
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // Statics outlive a scene load, so a gallery left armed would turn the
            // next test's exhibition load into a grid.
            Agent_MatchSetup.Clear();
            Agent_TimeFreeze.ReleaseAll();
            yield return null;
        }

        /// <summary>
        /// Harvests the trained roster from SCN_MENU, not from the exhibition
        /// scene.
        ///
        /// The first version of this read Agent_EnvController.profileRoster out of
        /// SCN_Exhibition, where it is serialized as `profileRoster: []` - that
        /// field is wired in SCN_Training only. So every test below hit its
        /// Assert.Ignore guard and the suite reported 46 tests, 43 passed, 3
        /// skipped, green. A test that skips itself is not a test that passes, and
        /// the shape of the mistake - a guard that silently swallows the case it
        /// was meant to protect - is the same one this file's own subject matter
        /// keeps producing. The menu is where the roster actually lives, and it is
        /// also the scene a real user reaches the gallery from.
        /// </summary>
        static IEnumerator TrainedRoster(List<Reward_Settings> into)
        {
            Agent_MatchSetup.Clear();
            SceneManager.LoadScene("SCN_Menu");
            yield return null;
            yield return null;
            yield return null;

            var menu = Object.FindAnyObjectByType<Agent_MainMenu>();
            Assert.IsNotNull(menu, "No Agent_MainMenu in SCN_Menu.");

            var candidates = new[] { menu.standard, menu.matt, menu.kim, menu.nick };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] != null && candidates[i].brainModel != null)
                {
                    into.Add(candidates[i]);
                }
            }

            // The roster is not empty in this project and has not been since
            // 2026-08-30. If it ever is, that is a finding, not a reason to skip:
            // the gallery is the feature that exhibits trained brains, and "there
            // are none" is the one state in which it cannot be tested at all.
            Assert.IsNotEmpty(into,
                "No trained brains on the menu roster. Every gallery test below " +
                "would skip itself and the suite would report green - so this " +
                "fails loudly instead. Run update-model.ps1, or fix the wiring.");
        }

        static IEnumerator LoadGallery(List<Reward_Settings> roster)
        {
            Agent_MatchSetup.Clear();
            Agent_MatchSetup.GalleryMode = true;
            Agent_MatchSetup.GalleryProfiles = roster.ToArray();
            // A lineup for the pitch the grid starts from; Agent_Gallery reassigns
            // per pitch afterwards.
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BlueSquad = new[] { roster[0] };
            Agent_MatchSetup.RedSquad = new[] { roster[0] };

            SceneManager.LoadScene("SCN_Exhibition");
            yield return null;   // Awake
            yield return null;   // Start - the grid is built here
            yield return null;   // clones reach their own Start
            yield return null;
        }

        [UnityTest]
        public IEnumerator Gallery_BuildsOnePitchPerBrainAndDoesNotRecurse()
        {
            var roster = new List<Reward_Settings>();
            yield return TrainedRoster(roster);

            yield return LoadGallery(roster);

            var pitches = Object.FindObjectsByType<Agent_EnvController>(FindObjectsSortMode.None);
            Assert.AreEqual(roster.Count, pitches.Length,
                $"Expected one pitch per trained brain ({roster.Count}), found {pitches.Length}. " +
                "More than that means a clone re-ran the cloning component.");

            // Exactly one live Agent_Gallery: the original. Every clone's copy is
            // disabled and destroyed the moment it is made, because a clone that
            // clones is a fork bomb of full physics environments.
            var galleries = Object.FindObjectsByType<Agent_Gallery>(FindObjectsSortMode.None);
            Assert.LessOrEqual(galleries.Length, 1,
                "A cloned pitch kept a live Agent_Gallery.");
        }

        [UnityTest]
        public IEnumerator Gallery_GivesEveryPitchADifferentBrainAndTheSameBot()
        {
            var roster = new List<Reward_Settings>();
            yield return TrainedRoster(roster);

            Assert.GreaterOrEqual(roster.Count, 2,
                "Fewer than two trained brains, so the claim this test exists to " +
                "check - that every pitch runs a DIFFERENT model - is unverifiable.");

            yield return LoadGallery(roster);

            var pitches = Object.FindObjectsByType<Agent_EnvController>(FindObjectsSortMode.None);
            var seen = new HashSet<Unity.InferenceEngine.ModelAsset>();

            for (int p = 0; p < pitches.Length; p++)
            {
                var agents = pitches[p].agents;
                bool sawBlue = false;

                for (int i = 0; i < agents.Count; i++)
                {
                    var agent = agents[i];
                    if (agent == null || !agent.isActiveAndEnabled) continue;

                    var behavior = agent.GetComponent<BehaviorParameters>();
                    Assert.IsNotNull(behavior);

                    if (agent.team == Agent_Soccer.Team.Blue)
                    {
                        sawBlue = true;
                        Assert.IsNotNull(behavior.Model,
                            "A gallery pitch is exhibiting a blue player with no model.");
                        Assert.AreEqual(BehaviorType.InferenceOnly, behavior.BehaviorType,
                            "The exhibited brain is not actually running inference.");
                        Assert.IsTrue(seen.Add(behavior.Model),
                            "Two pitches are running the same model. The whole point of " +
                            "the grid is that each pitch shows a different brain, and the " +
                            "captions will claim they do whether or not it is true.");
                    }
                    else
                    {
                        // Every pitch must face the identical opponent, or the
                        // pitches are not comparable to each other at all.
                        Assert.AreEqual(BehaviorType.HeuristicOnly, behavior.BehaviorType,
                            "A gallery opponent is not the rule-based bot.");
                        Assert.IsTrue(agent.RuleBased);
                    }
                }

                Assert.IsTrue(sawBlue, $"Pitch {p} has no active blue player.");
            }
        }

        [UnityTest]
        public IEnumerator Gallery_InstallsVisualsButNoMatchFlow()
        {
            var roster = new List<Reward_Settings>();
            yield return TrainedRoster(roster);

            yield return LoadGallery(roster);

            // Global-state owners must stay out: six replays would fight over one
            // clock and six directors over one camera.
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Replay>(),
                "Agent_Replay must not be installed in the gallery.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_MatchFlow>(),
                "Agent_MatchFlow must not be installed in the gallery.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Director>(),
                "Agent_Director must not be installed in the gallery - it writes the " +
                "camera transform, and the gallery frames the whole grid instead.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_CameraFollow>(),
                "Agent_CameraFollow must not be attached in the gallery; there is no " +
                "single ball to follow when there are several.");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Hitstop>(),
                "Agent_Hitstop must not be installed in the gallery - it drives the clock.");

            // Per-pitch visuals SHOULD be there, one set per pitch, or the gallery
            // looks like a debug scene rather than the game.
            var intents = Object.FindObjectsByType<Agent_Intent>(FindObjectsSortMode.None);
            var pitches = Object.FindObjectsByType<Agent_EnvController>(FindObjectsSortMode.None);
            Assert.AreEqual(pitches.Length, intents.Length,
                "Every gallery pitch should carry its own intent overlay.");
            for (int i = 0; i < intents.Length; i++)
            {
                Assert.IsTrue(intents[i].enabled,
                    "Agent_Intent disabled itself in the gallery. The gallery gate is " +
                    "Agent_Presentation.IsVisualScene, not IsMatchScene.");
            }

            Assert.AreEqual(1f, Time.timeScale, 0.0001f,
                "Something in the gallery took hold of the clock.");
        }
    }
}
