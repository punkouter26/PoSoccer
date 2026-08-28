using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.InferenceEngine;
using UnityEditor;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Guards the one failure mode that cost phases 9 and 10 (2026-08-12): a brain
    /// whose input shape disagrees with the runtime that will run it.
    ///
    /// This is NOT covered by the sensor-geometry landmine note in CLAUDE.md. That
    /// one is about arc/range changes leaving tensor shapes identical while the rays
    /// report a different world. This is the opposite and cruder case - the shapes
    /// genuinely differ, ML-Agents declines to run inference, the agent degrades to
    /// doing nothing, and the eval harness still writes a plausible-looking win rate.
    /// Three published phase-10 results were produced that way.
    ///
    /// Expected size is computed from the shipped code (Sensor_Vision's battery plus
    /// Agent_Soccer's vector contract), never hardcoded, so it tracks the runtime
    /// automatically. Change the sensors or the obs count and this test tells you
    /// exactly which brains you just obsoleted.
    /// </summary>
    public sealed class Agent_EditMode_ObsContract
    {
        static int ExpectedModelInputSize =>
            Sensor_Vision.TotalRayObservationSize
            + Agent_Soccer.BaseObservationSize * Agent_Soccer.StackedObservations;

        [Test]
        public void RuntimeObservationContract_IsTheDocumentedSize()
        {
            // Pins the arithmetic the docs quote, so a silent drift in either half
            // shows up here rather than in a training run three hours later.
            Assert.That(Sensor_Vision.TotalRayObservationSize, Is.EqualTo(120),
                "Ray observations changed. Sensor_Vision.Battery is the source of truth - " +
                "update the tables in Sensor_Vision, Agent_Soccer and CLAUDE.md together.");
            Assert.That(Agent_Soccer.BaseObservationSize * Agent_Soccer.StackedObservations,
                Is.EqualTo(58), "Vector observations changed (BaseObservationSize x StackedObservations).");
            Assert.That(ExpectedModelInputSize, Is.EqualTo(178));
        }

        [Test]
        public void EveryAssignedBrain_MatchesTheRuntimeObservationContract()
        {
            string[] guids = AssetDatabase.FindAssets("t:Reward_Settings");
            Assert.That(guids.Length, Is.GreaterThan(0), "No Reward_Settings assets found.");

            var failures = new StringBuilder();
            int checkedCount = 0;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(path);

                // A null brainModel is the documented "plays as the rule-based bot"
                // state (BOT is permanently null), not a failure.
                if (profile == null || profile.brainModel == null)
                {
                    continue;
                }

                checkedCount++;
                Model model = ModelLoader.Load(profile.brainModel);

                int total = 0;
                var breakdown = new StringBuilder();
                for (int inputIndex = 0; inputIndex < model.inputs.Count; inputIndex++)
                {
                    Model.Input input = model.inputs[inputIndex];
                    // Axis -1 is the feature count; axis 0 is the dynamic batch dim.
                    int features = input.shape.Get(-1);
                    total += features;
                    breakdown.Append(input.name).Append('=').Append(features).Append(' ');
                }

                if (total != ExpectedModelInputSize)
                {
                    failures.AppendLine(
                        $"  {path}: model declares {total} inputs ({breakdown.ToString().Trim()}), " +
                        $"runtime produces {ExpectedModelInputSize} " +
                        $"({Sensor_Vision.TotalRayObservationSize} ray + " +
                        $"{Agent_Soccer.BaseObservationSize * Agent_Soccer.StackedObservations} vector). " +
                        $"Trained on {profile.trainingRunId}, {profile.trainingSteps} steps.");
                }
            }

            if (failures.Length > 0)
            {
                Assert.Fail(
                    "Assigned brain(s) cannot load against this runtime. Inference will be " +
                    "refused and the agent will do nothing, while evaluate.ps1 still reports a " +
                    "win rate - retrain, or clear brainModel so the profile falls back to the " +
                    "rule-based bot.\n" + failures);
            }

            Assert.That(checkedCount, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void NoSceneReferencesAnIncompatibleBrain()
        {
            // Profiles are only half the story: BehaviorParameters.m_Model is serialized
            // per agent in the scene, and SCN_Training carries its own reference that a
            // profile sweep does not see. Read the scene files as text rather than
            // opening them - opening a scene in a test would discard whatever the user
            // has unsaved in the editor.
            string[] sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            var modelRef = new Regex(@"m_Model:\s*\{fileID:\s*[-0-9]+,\s*guid:\s*([0-9a-f]{32})");
            var failures = new StringBuilder();

            for (int i = 0; i < sceneGuids.Length; i++)
            {
                string scenePath = AssetDatabase.GUIDToAssetPath(sceneGuids[i]);
                string text = File.ReadAllText(scenePath);

                foreach (Match match in modelRef.Matches(text))
                {
                    string modelPath = AssetDatabase.GUIDToAssetPath(match.Groups[1].Value);
                    var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(modelPath);
                    if (asset == null)
                    {
                        continue;
                    }

                    Model model = ModelLoader.Load(asset);
                    int total = 0;
                    for (int inputIndex = 0; inputIndex < model.inputs.Count; inputIndex++)
                    {
                        total += model.inputs[inputIndex].shape.Get(-1);
                    }

                    if (total != ExpectedModelInputSize)
                    {
                        failures.AppendLine(
                            $"  {scenePath} -> {modelPath}: {total} inputs, runtime produces " +
                            $"{ExpectedModelInputSize}.");
                    }
                }
            }

            if (failures.Length > 0)
            {
                Assert.Fail(
                    "Scene-serialized brain(s) cannot load against this runtime. Clear m_Model on " +
                    "the affected agents via MCP manage_gameobject (UNITY_RULES: scenes are edited " +
                    "through MCP only), or retrain against the current contract.\n" + failures);
            }
        }
    }
}
