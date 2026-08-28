using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Covers the graphics and audio layer added 2026-08-27: tonemapping, the
    /// custom sprite shader, normal maps, the audio bus, the design system and
    /// the telemetry overlay.
    ///
    /// These assert the things that fail SILENTLY. A missing tonemapper still
    /// renders - just clipped. A shader stripped from a player build still runs -
    /// with the fallback material. A stylesheet that failed to load still lays
    /// out - just unstyled. None of that throws, so none of it would show up in
    /// a console-error check; it has to be asserted directly.
    /// </summary>
    public class Agent_PlayMode_Presentation
    {
        static IEnumerator LoadExhibition()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            yield return null;
            yield return null;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            yield return null;
        }

        /// <summary>
        /// The project renders in HDR and drives bloom to 2.5 on a goal, while
        /// the default volume profile pins Tonemapping to None. Without an
        /// override the celebration clips to flat white.
        /// </summary>
        [UnityTest]
        public IEnumerator Stadium_OverridesTonemappingAndGrading()
        {
            yield return LoadExhibition();

            var stadium = Object.FindAnyObjectByType<Agent_Stadium>();
            Assert.IsNotNull(stadium, "No Agent_Stadium in SCN_Exhibition");

            var volumes = stadium.GetComponentsInChildren<Volume>();
            Assert.Greater(volumes.Length, 0, "Stadium built no volume");

            Volume runtime = null;
            for (int i = 0; i < volumes.Length; i++)
                if (volumes[i].profile != null) runtime = volumes[i];
            Assert.IsNotNull(runtime, "Stadium volume has no profile");

            Assert.IsTrue(runtime.profile.TryGet(out Tonemapping tonemapping),
                "No Tonemapping override - HDR highlights will clip.");
            Assert.AreNotEqual(TonemappingMode.None, tonemapping.mode.value,
                "Tonemapping is present but set to None, which is the bug it exists to fix.");
            Assert.IsTrue(runtime.profile.TryGet(out ColorAdjustments _),
                "No ColorAdjustments override");
            Assert.Greater(runtime.priority, 0f,
                "Runtime volume must outrank the default profile at priority 0");
        }

        /// <summary>
        /// Shader.Find only resolves shaders reachable from a build. Materials
        /// here are created at runtime, so nothing references the shader and it
        /// would be stripped unless it is in Always Included Shaders.
        /// </summary>
        [UnityTest]
        public IEnumerator Surfaces_ApplyCustomShaderAndNormalMaps()
        {
            yield return LoadExhibition();

            var shader = Shader.Find("PoSoccer/SpriteLitFX");
            Assert.IsNotNull(shader, "PoSoccer/SpriteLitFX did not resolve at runtime");
            Assert.IsTrue(shader.isSupported, "PoSoccer/SpriteLitFX failed to compile");

            Assert.IsNotNull(Resources.Load<Texture2D>("sphere_normal"), "sphere_normal missing");
            Assert.IsNotNull(Resources.Load<Texture2D>("turf_normal"), "turf_normal missing");

            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(env);

            var pitch = env.transform.Find("PitchBG");
            Assert.IsNotNull(pitch, "PitchBG not found under the pitch root");
            var pitchRenderer = pitch.GetComponent<SpriteRenderer>();
            Assert.AreEqual(shader, pitchRenderer.sharedMaterial.shader,
                "Pitch kept the stock material - Agent_Surfaces did not run, or " +
                "Agent_Stadium overwrote it afterwards.");
            Assert.Greater(pitchRenderer.sharedMaterial.GetFloat("_StripeStrength"), 0f,
                "Pitch material has no mow stripes");
            Assert.IsNotNull(pitchRenderer.sharedMaterial.GetTexture("_NormalMap"),
                "Pitch material has no normal map, so 2D lighting stays flat");

            for (int i = 0; i < env.agents.Count; i++)
            {
                var body = env.agents[i].GetComponent<SpriteRenderer>();
                if (body == null) continue;
                Assert.AreEqual(shader, body.sharedMaterial.shader,
                    $"Agent {env.agents[i].name} kept the stock material");
                Assert.Greater(body.sharedMaterial.GetFloat("_RimStrength"), 0f,
                    "Player material has no team rim");
            }

            Assert.IsNotNull(env.Ball.transform.Find("BallLight"), "Ball has no light");
        }

        /// <summary>
        /// The three audio defects this replaced: no spatialisation at all, one
        /// shared one-shot source whose pitch every overlapping impact fought
        /// over, and no low-pass while the clock is stopped.
        /// </summary>
        [UnityTest]
        public IEnumerator Audio_BuildsSpatialVoicePoolAndFilters()
        {
            yield return LoadExhibition();

            var audio = Object.FindAnyObjectByType<Agent_Audio>();
            Assert.IsNotNull(audio, "No Agent_Audio in SCN_Exhibition");

            var voices = audio.transform.Find("Audio_Voices");
            Assert.IsNotNull(voices, "No voice pool - one-shots would share a source again");
            var pool = voices.GetComponentsInChildren<AudioSource>();
            Assert.GreaterOrEqual(pool.Length, 4, "Voice pool is too small to overlap impacts");

            bool anySpatial = false;
            for (int i = 0; i < pool.Length; i++)
                if (pool[i].spatialBlend > 0.1f) anySpatial = true;
            Assert.IsTrue(anySpatial,
                "Every voice is spatialBlend 0 - that is exactly the bug this replaced.");

            var crowd = audio.transform.Find("Audio_Crowd");
            var music = audio.transform.Find("Audio_Music");
            Assert.IsNotNull(crowd, "No crowd bus");
            Assert.IsNotNull(music, "No music bus");
            Assert.IsNotNull(crowd.GetComponent<AudioLowPassFilter>(),
                "Crowd bus has no low-pass, so a replay cannot duck the stadium");
            Assert.IsNotNull(music.GetComponent<AudioLowPassFilter>(), "Music bus has no low-pass");
        }

        [UnityTest]
        public IEnumerator DesignSystem_StyleSheetIsAttachedToTheHud()
        {
            yield return LoadExhibition();

            var sheet = Resources.Load<StyleSheet>("PoSoccerTheme");
            Assert.IsNotNull(sheet, "PoSoccerTheme.uss did not import as a StyleSheet");

            var hud = Object.FindAnyObjectByType<Agent_HUD>();
            Assert.IsNotNull(hud);
            var root = hud.GetComponent<UIDocument>().rootVisualElement;
            Assert.IsTrue(root.styleSheets.Contains(sheet),
                "HUD root has no theme attached - every class in the USS is inert.");
        }

        [UnityTest]
        public IEnumerator Telemetry_InstallsInBothScenesAndCostsNothingWhileHidden()
        {
            yield return LoadExhibition();
            Assert.IsNotNull(Object.FindAnyObjectByType<Agent_Telemetry>(),
                "Telemetry missing from the match scene");

            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            yield return null;

            var telemetry = Object.FindAnyObjectByType<Agent_Telemetry>();
            Assert.IsNotNull(telemetry,
                "Telemetry must exist in SCN_Training too - a diagnostic you have to " +
                "switch scenes to reach is one nobody uses.");
            // Hidden by default: no UIDocument is built until it is first opened.
            Assert.IsNull(telemetry.GetComponent<UIDocument>(),
                "Telemetry built its overlay while hidden");
        }

        [UnityTest]
        public IEnumerator ParticleFX_BuildsSystemsAndLeavesTrainingAlone()
        {
            yield return LoadExhibition();

            var fx = Object.FindAnyObjectByType<Agent_ParticleFX>();
            Assert.IsNotNull(fx, "No Agent_ParticleFX in the match scene");
            var systems = fx.GetComponentsInChildren<ParticleSystem>();
            Assert.GreaterOrEqual(systems.Length, 3,
                "Expected turf, debris and confetti systems");

            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            yield return null;

            Assert.IsNull(Object.FindAnyObjectByType<Agent_ParticleFX>(),
                "Particle FX must never be installed in SCN_Training");
            Assert.IsNull(Object.FindAnyObjectByType<Agent_Surfaces>(),
                "Agent_Surfaces must never be installed in SCN_Training");
        }
    }
}
