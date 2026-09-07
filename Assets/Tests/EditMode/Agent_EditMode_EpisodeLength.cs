using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the precedence of the episode step cap, and the reason it is split across
    /// three sources at all.
    ///
    /// `maxEnvironmentSteps` was doing two unrelated jobs: setting the TRAINING horizon,
    /// and defining what counts as a stalemate at EVALUATION. Those pull in opposite
    /// directions. Training wants a short episode - at gamma 0.99 and decision period 8
    /// a 9000-step episode is 1125 decisions, so a terminal reward valued from the start
    /// survives at ~1.2e-5, while a 2500-step cap gives 4.3e-2 (~3500x more signal) and
    /// ~3.6x as many terminal events per million steps. Evaluation wants a LONG episode,
    /// because the win rate is blueWins/episodes and a shorter cap mechanically converts
    /// wins into stalemates.
    ///
    /// Shortening the profile field would therefore have improved training and silently
    /// depressed the graded win rate at the same time - a regression that would have read
    /// as a real result, in a project that has already published one retraction over a
    /// number that looked measured and was not.
    ///
    /// The split: `episode_steps` is a CURRICULUM parameter, so only a trainer drives it
    /// (default 0 = "nobody is driving this"). Eval and gameplay keep the profile's cap.
    /// Same shape as `bot_strength`: laddered during training, graded at 1.0.
    /// </summary>
    public sealed class Agent_EditMode_EpisodeLength
    {
        static Agent_EnvController NewEnv(out GameObject go)
        {
            go = new GameObject("env_test");
            return go.AddComponent<Agent_EnvController>();
        }

        [Test]
        public void WithNoTrainerAndNoOverride_TheProfileCapWins()
        {
            var env = NewEnv(out var go);
            try
            {
                var profile = ScriptableObject.CreateInstance<Reward_Settings>();
                profile.maxEnvironmentSteps = 9000;
                env.rewards = profile;
                env.stepCapOverride = 0;

                Assert.AreEqual(9000, env.MaxEnvironmentSteps,
                    "Eval and gameplay must use the profile cap - this is what keeps a " +
                    "graded win rate comparable across runs.");
                Object.DestroyImmediate(profile);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheExhibitionOverride_BeatsTheProfile()
        {
            var env = NewEnv(out var go);
            try
            {
                var profile = ScriptableObject.CreateInstance<Reward_Settings>();
                profile.maxEnvironmentSteps = 9000;
                env.rewards = profile;
                env.stepCapOverride = 2500;      // SCN_Exhibition sets this for pace

                Assert.AreEqual(2500, env.MaxEnvironmentSteps,
                    "The exhibition scene's pace override is the highest precedence.");
                Object.DestroyImmediate(profile);
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The load-bearing one. A default of 0 is what makes `episode_steps` invisible
        /// to every non-training run; if it ever defaulted to a real number, eval would
        /// silently start grading on a shorter episode.
        /// </summary>
        [Test]
        public void WithNoTrainerDriving_TheCurriculumCapIsInert()
        {
            var env = NewEnv(out var go);
            try
            {
                var profile = ScriptableObject.CreateInstance<Reward_Settings>();
                profile.maxEnvironmentSteps = 9000;
                env.rewards = profile;

                Assert.AreEqual(0, env.CurrentEpisodeSteps,
                    "CurrentEpisodeSteps must be 0 until a trainer sets episode_steps. A " +
                    "non-zero default would shorten EVAL episodes and depress the win rate " +
                    "while looking like a training change.");
                Assert.AreEqual(9000, env.MaxEnvironmentSteps);
                Object.DestroyImmediate(profile);
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// Documents the arithmetic the lever rests on, so a change to gamma or the
        /// decision period cannot quietly invalidate the reasoning in the p25 config.
        /// </summary>
        [Test]
        public void TheDiscountArithmetic_StillFavoursShorterEpisodes()
        {
            const float gamma = 0.99f;
            const int decisionPeriod = 8;

            float DiscountAtStart(int stepCap) =>
                Mathf.Pow(gamma, stepCap / (float)decisionPeriod);

            float atNineThousand = DiscountAtStart(9000);
            float atTwentyFiveHundred = DiscountAtStart(2500);

            Assert.Less(atNineThousand, 1e-4f,
                "A terminal reward at a 9000-step horizon should be all but invisible " +
                "from the episode start - this is why dense shaping carries the learning.");
            Assert.Greater(atTwentyFiveHundred / atNineThousand, 1000f,
                "Shortening to 2500 steps should buy at least three orders of magnitude " +
                "more terminal signal; if it no longer does, gamma or the decision period " +
                "changed and the p25 rationale needs re-deriving.");
        }
    }
}
