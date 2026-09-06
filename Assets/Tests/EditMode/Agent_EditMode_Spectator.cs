using NUnit.Framework;
using PoSoccer;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// EditMode guards for the broadcast layer added on 2026-09-06: the checkpoint
    /// archive, the gallery's mode statics, and the vector-graphics batch every
    /// overlay draws through.
    ///
    /// Everything here is pure logic or a mesh - no scene, no panel, no play mode,
    /// no Academy. The parts that genuinely need a running pitch (the director's
    /// shot grammar, the win-probability features, the stat accumulators) are
    /// PlayMode territory and are not faked here; a test that constructs a fake
    /// Agent_Soccer to assert on its traction number is testing the fake.
    /// </summary>
    public sealed class Agent_EditMode_Spectator
    {
        // ── Agent_Checkpoint ────────────────────────────────────────────────

        [Test]
        public void Checkpoint_FallsBackToTheBaseProfile_WhenItCarriesNothingOfItsOwn()
        {
            var profile = ScriptableObject.CreateInstance<Reward_Settings>();
            profile.playerName = "STANDARD";
            profile.trainingSteps = 10_000_034;
            profile.trainingRunId = "soccer_p21curric_standard";
            profile.evalWinRate = 0.257f;
            profile.evalEpisodes = 350;

            var checkpoint = ScriptableObject.CreateInstance<Agent_Checkpoint>();
            checkpoint.baseProfile = profile;

            // This is the case that has to work out of the box: a checkpoint that
            // is nothing but a pointer at a live profile, which is how the gallery
            // exhibits the roster before anyone has archived anything.
            Assert.AreEqual(10_000_034, checkpoint.ResolvedSteps);
            Assert.AreEqual("soccer_p21curric_standard", checkpoint.ResolvedRunId);
            Assert.AreEqual(0.257f, checkpoint.ResolvedWinRate, 1e-5f);
            Assert.AreEqual(350, checkpoint.ResolvedEpisodes);
            Assert.AreEqual("STANDARD", checkpoint.ResolvedLabel);

            Object.DestroyImmediate(checkpoint);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void Checkpoint_PrefersItsOwnRecord_OverTheProfileItPointsAt()
        {
            // The whole reason the type exists: update-model.ps1 overwrites a
            // profile's slot in place, so an archived checkpoint must keep the
            // record of what THAT model scored, not what the slot scores today.
            var profile = ScriptableObject.CreateInstance<Reward_Settings>();
            profile.playerName = "STANDARD";
            profile.trainingSteps = 10_000_034;
            profile.evalWinRate = 0.257f;
            profile.evalEpisodes = 350;

            var checkpoint = ScriptableObject.CreateInstance<Agent_Checkpoint>();
            checkpoint.baseProfile = profile;
            checkpoint.label = "p18 · body frame";
            checkpoint.trainingSteps = 3_000_000;
            checkpoint.trainingRunId = "soccer_p18bf_standard";
            checkpoint.evalWinRate = 0.178f;
            checkpoint.evalEpisodes = 510;

            Assert.AreEqual(3_000_000, checkpoint.ResolvedSteps);
            Assert.AreEqual("soccer_p18bf_standard", checkpoint.ResolvedRunId);
            Assert.AreEqual(0.178f, checkpoint.ResolvedWinRate, 1e-5f);
            Assert.AreEqual(510, checkpoint.ResolvedEpisodes);
            Assert.AreEqual("p18 · body frame", checkpoint.ResolvedLabel);

            Object.DestroyImmediate(checkpoint);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void Checkpoint_FlagsModelsFromBeforeTheBodyFrameChange()
        {
            // CLAUDE.md's hardest landmine: the 2026-08-28 observation change kept
            // the tensor shape and changed the meaning, so a pre-boundary .onnx
            // loads without a warning and reads a different world. The gallery
            // labels those rather than racing them.
            var before = ScriptableObject.CreateInstance<Agent_Checkpoint>();
            before.trainedOn = "2026-08-27";
            Assert.IsTrue(before.IsPreBodyFrame,
                "A model exported the day before the frame change is not comparable " +
                "to one exported after it.");

            var onTheDay = ScriptableObject.CreateInstance<Agent_Checkpoint>();
            onTheDay.trainedOn = Agent_Checkpoint.BODY_FRAME_BOUNDARY;
            Assert.IsFalse(onTheDay.IsPreBodyFrame,
                "The boundary date itself is the first comparable day, not the last " +
                "incomparable one.");

            var after = ScriptableObject.CreateInstance<Agent_Checkpoint>();
            after.trainedOn = "2026-09-05";
            Assert.IsFalse(after.IsPreBodyFrame);

            var undated = ScriptableObject.CreateInstance<Agent_Checkpoint>();
            Assert.IsFalse(undated.IsPreBodyFrame,
                "An undated checkpoint is unknown, not suspect. Warning on every " +
                "undated exhibit would make the warning mean nothing.");

            Object.DestroyImmediate(before);
            Object.DestroyImmediate(onTheDay);
            Object.DestroyImmediate(after);
            Object.DestroyImmediate(undated);
        }

        // ── Agent_MatchSetup ────────────────────────────────────────────────

        [Test]
        public void Clear_ResetsGalleryMode()
        {
            // Statics outlive a scene load in a player build. A gallery visit that
            // did not clear itself would send the next PLAY into the grid, which
            // is the exact class of bug the Applied flag already exists for.
            Agent_MatchSetup.GalleryMode = true;
            Agent_MatchSetup.GalleryProfiles = new Reward_Settings[1];
            Agent_MatchSetup.GalleryEntries = new Agent_Checkpoint[1];
            Agent_MatchSetup.GalleryOpponent = ScriptableObject.CreateInstance<Reward_Settings>();

            var opponent = Agent_MatchSetup.GalleryOpponent;
            Agent_MatchSetup.Clear();

            Assert.IsFalse(Agent_MatchSetup.GalleryMode);
            Assert.IsNull(Agent_MatchSetup.GalleryProfiles);
            Assert.IsNull(Agent_MatchSetup.GalleryEntries);
            Assert.IsNull(Agent_MatchSetup.GalleryOpponent);

            Object.DestroyImmediate(opponent);
        }

        // ── Agent_Lines ─────────────────────────────────────────────────────

        [Test]
        public void Lines_BuildOneMeshWithOneMaterial()
        {
            // The reason this class exists instead of a LineRenderer per mark: at
            // 5v5 the vision overlay alone is 400 rays, and 400 renderers would
            // blow the draw-call budget in .claude/rules/performance.md on its own.
            var lines = new Agent_Lines("TestLines", sortingOrder: 5);

            lines.Begin();
            lines.AddSegment(Vector2.zero, Vector2.right, 0.1f, Color.white);
            lines.AddArrow(Vector2.zero, Vector2.up, 1f, 0.1f, Color.red);
            lines.AddArc(Vector2.zero, 1f, 0.5f, 0.05f, Color.green);
            lines.AddDiamond(Vector2.one, 0.2f, Color.blue);
            lines.Commit();

            Assert.IsNotNull(lines.Renderer, "Agent_Lines did not create its renderer.");
            Assert.AreEqual(1, lines.Renderer.sharedMaterials.Length,
                "More than one material means more than one draw call, which is the " +
                "whole thing this class exists to avoid.");

            var mesh = lines.Renderer.GetComponent<MeshFilter>().sharedMesh;
            Assert.Greater(mesh.vertexCount, 0, "Commit uploaded nothing.");
            Assert.AreEqual(mesh.vertexCount, mesh.colors.Length,
                "Every vertex needs a colour: Sprites/Default multiplies by vertex " +
                "colour, so a short colour array renders part of the batch black.");

            lines.Dispose();
        }

        [Test]
        public void Lines_BeginDiscardsThePreviousFrame()
        {
            // Begin/Commit is the per-frame contract. If Begin did not clear, the
            // overlay would accumulate every frame it has ever drawn - which looks
            // like a slow leak rather than a logic error, and would not show up
            // until a long match.
            var lines = new Agent_Lines("TestLinesReset", sortingOrder: 5);

            lines.Begin();
            lines.AddSegment(Vector2.zero, Vector2.right, 0.1f, Color.white);
            lines.Commit();
            int afterOne = lines.VertexCount;
            Assert.AreEqual(4, afterOne, "A segment is one quad.");

            lines.Begin();
            lines.AddSegment(Vector2.zero, Vector2.right, 0.1f, Color.white);
            lines.Commit();

            Assert.AreEqual(afterOne, lines.VertexCount,
                "A second identical frame produced a different vertex count, so " +
                "Begin is not clearing the previous one.");

            lines.Dispose();
        }

        [Test]
        public void Lines_DegenerateInputAddsNothing()
        {
            // Zero-length segments happen constantly in real use - a stationary
            // player's velocity arrow, a ray with HitFraction 0 - and a
            // zero-length normal is a NaN vertex, which poisons the whole mesh's
            // bounds and makes the entire batch vanish rather than just that mark.
            var lines = new Agent_Lines("TestLinesDegenerate", sortingOrder: 5);

            lines.Begin();
            lines.AddSegment(Vector2.zero, Vector2.zero, 0.1f, Color.white);
            lines.AddArrow(Vector2.zero, Vector2.up, 0f, 0.1f, Color.white);
            lines.AddArc(Vector2.zero, 1f, 0f, 0.05f, Color.white);
            lines.Commit();

            Assert.AreEqual(0, lines.VertexCount,
                "Degenerate geometry was written into the mesh.");

            lines.Dispose();
        }
    }
}
