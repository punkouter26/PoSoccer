using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins which presentation components may be SERIALIZED into a scene and which
    /// must stay code-installed, because the answer is not a style preference - it
    /// is forced by the fact that SCN_Exhibition hosts two different modes.
    ///
    /// THE RULE. Agent_Presentation has two entry points. InstallVisuals adds the
    /// per-pitch drawing components and runs in BOTH the match and the gallery.
    /// Install adds those PLUS the components that own global state - the clock
    /// (Agent_Replay, Agent_Hitstop), the camera (Agent_Director), the scoreboard
    /// (Agent_WinProbability, Agent_MatchStats), the pitch geometry
    /// (Agent_Overtime) - and runs ONLY in the match.
    ///
    /// WHY THAT MAKES BAKING A ONE-WAY DOOR. Agent_Gallery CLONES the pitch root.
    /// A component serialized on that root is therefore present on every clone,
    /// which is exactly right for the nine drawing components and catastrophic for
    /// the global-state owners: six Agent_Directors would fight over one camera and
    /// six Agent_Replays over one clock. Today the gallery is spared that only
    /// because InstallGallery does not add them - not because anything gates them
    /// out. Serializing one into the scene removes the only thing standing between
    /// the gallery and six copies of it.
    ///
    /// So the nine ARE baked into SCN_Exhibition (2026-09-13), which is what makes
    /// them tunable in the Inspector instead of only through code, and the rest
    /// stay code-installed. Agent_Presentation is unchanged and still find-or-create,
    /// so the scene and the installer cannot disagree: whichever runs first wins and
    /// the other is a no-op.
    ///
    /// SCN_Training must carry NONE of them. Agent_PlayMode_Overtime already makes
    /// that argument for Agent_Overtime specifically (it moves the walls, so a
    /// training run that got it would convert stalemates into goals and inflate the
    /// benchmark's headline number). This widens it to the whole set.
    /// </summary>
    public sealed class Agent_EditMode_SceneComposition
    {
        /// <summary>
        /// Agent_Presentation.InstallVisuals' set, in its order. Safe to serialize:
        /// every gallery clone is supposed to have these.
        /// </summary>
        static readonly string[] BakeableVisuals =
        {
            "Agent_Surfaces", "Agent_ParticleFX", "Agent_ImpactFX", "Agent_Shadows",
            "Agent_Wear", "Agent_Limbs", "Agent_Intent", "Agent_VisionView", "Agent_ShotTracer",
        };

        /// <summary>
        /// Global-state owners. Must NEVER be serialized onto the pitch root - see
        /// the class docstring for what the gallery does with them.
        /// </summary>
        static readonly string[] MustStayCodeInstalled =
        {
            "Agent_Replay", "Agent_MatchFlow", "Agent_Commentary", "Agent_Crowd", "Agent_Hitstop",
            "Agent_WinProbability", "Agent_Director", "Agent_MatchStats", "Agent_Overtime",
            "Agent_BrainCam", "Agent_Dossier", "Agent_ScreenFX", "Agent_Haptics", "Agent_Quality",
        };

        static System.Type Resolve(string typeName)
        {
            System.Type type = System.Type.GetType("PoSoccer." + typeName + ", PoSoccer.Runtime");
            Assert.IsNotNull(type, "PoSoccer." + typeName + " no longer exists. If it was renamed, "
                + "rename it here too - a typo in this list silently checks nothing.");
            return type;
        }

        /// <summary>
        /// Opens a scene additively without disturbing whatever the editor has open,
        /// and hands back the pitch root.
        /// </summary>
        static GameObject OpenPitch(string scenePath, out Scene scene)
        {
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                var env = roots[rootIndex].GetComponentInChildren<Agent_EnvController>(true);
                if (env != null) return env.gameObject;
            }
            return null;
        }

        [Test]
        public void TheExhibitionPitch_CarriesEveryBakeableVisual()
        {
            GameObject pitch = OpenPitch("Assets/Scenes/SCN_Exhibition.unity", out Scene scene);
            try
            {
                Assert.IsNotNull(pitch, "SCN_Exhibition has no Agent_EnvController");

                var missing = new StringBuilder();
                for (int visualIndex = 0; visualIndex < BakeableVisuals.Length; visualIndex++)
                {
                    System.Type type = Resolve(BakeableVisuals[visualIndex]);
                    if (pitch.GetComponent(type) == null)
                    {
                        missing.AppendLine("  " + BakeableVisuals[visualIndex]);
                    }
                }

                Assert.That(missing.Length, Is.Zero,
                    "These are serialized into SCN_Exhibition so they can be tuned in the "
                    + "Inspector, and are missing:\n" + missing
                    + "\nThe game still RUNS without them - Agent_Presentation.InstallVisuals "
                    + "adds them at runtime - which is exactly why their absence needs a test "
                    + "rather than a bug report.");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void NoGlobalStateOwner_IsSerializedIntoTheExhibitionPitch()
        {
            GameObject pitch = OpenPitch("Assets/Scenes/SCN_Exhibition.unity", out Scene scene);
            try
            {
                Assert.IsNotNull(pitch, "SCN_Exhibition has no Agent_EnvController");

                var baked = new StringBuilder();
                for (int ownerIndex = 0; ownerIndex < MustStayCodeInstalled.Length; ownerIndex++)
                {
                    System.Type type = Resolve(MustStayCodeInstalled[ownerIndex]);
                    if (pitch.GetComponent(type) != null)
                    {
                        baked.AppendLine("  " + MustStayCodeInstalled[ownerIndex]);
                    }
                }

                Assert.That(baked.Length, Is.Zero,
                    "These own global state and were serialized onto the pitch root:\n" + baked
                    + "\nAgent_Gallery CLONES that root, so each one is now present on every "
                    + "pitch in the grid - six cameras, six clocks, six scoreboards. They are "
                    + "added by Agent_Presentation.Install, which the gallery deliberately does "
                    + "not call. Remove them from the scene; do not 'fix' this by adding a gate.");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void TheTrainingPitch_CarriesNoPresentationComponentAtAll()
        {
            GameObject pitch = OpenPitch("Assets/Scenes/SCN_Training.unity", out Scene scene);
            try
            {
                Assert.IsNotNull(pitch, "SCN_Training has no Agent_EnvController");

                var found = new StringBuilder();
                var all = new List<string>(BakeableVisuals);
                all.AddRange(MustStayCodeInstalled);
                for (int typeIndex = 0; typeIndex < all.Count; typeIndex++)
                {
                    System.Type type = Resolve(all[typeIndex]);
                    if (pitch.GetComponent(type) != null) found.AppendLine("  " + all[typeIndex]);
                }

                Assert.That(found.Length, Is.Zero,
                    "SCN_Training carries presentation components:\n" + found
                    + "\nA training or eval run must never pay for, or have its timing perturbed "
                    + "by, a light show - and Agent_Overtime in particular moves the walls and "
                    + "widens the goals, which would convert stalemates into goals and inflate "
                    + "the benchmark's headline number with nothing in any eval JSON able to "
                    + "reveal it.");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
