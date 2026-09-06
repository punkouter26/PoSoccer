using NUnit.Framework;
using PoSoccer;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the property that makes Agent_Art's runtime shapes batchable: they
    /// all come off ONE texture.
    ///
    /// This is worth a test rather than a comment because the regression is
    /// invisible. The old implementation allocated a Texture2D per cache entry
    /// and keyed the cache on shape AND world size, so it kept returning correct
    /// sprites that simply refused to batch - nothing looked wrong, nothing
    /// logged, and the only symptom was a draw-call count in a profiler window
    /// nobody had open. A future "simplification" back to one-texture-per-sprite
    /// would look tidier and cost the same silent regression, so the invariant is
    /// asserted rather than described.
    /// </summary>
    public sealed class Agent_EditMode_Atlas
    {
        [Test]
        public void EveryShape_SharesOneTexture()
        {
            var square = Agent_Art.Square(1f);
            var disc = Agent_Art.Disc(1f);
            var ring = Agent_Art.Disc(1f, 0.72f);
            var blob = Agent_Art.Blob(1.4f);

            Assert.IsNotNull(square.texture);
            Assert.AreSame(square.texture, disc.texture,
                "A square and a disc came back on different textures - they cannot batch.");
            Assert.AreSame(square.texture, ring.texture,
                "A ring came back on a different texture from the square.");
            Assert.AreSame(square.texture, blob.texture,
                "A shadow blob came back on a different texture from the square.");
            Assert.AreSame(square.texture, Agent_Art.Page,
                "Shapes are not being written into the page Agent_Art exposes.");
        }

        /// <summary>
        /// The same shape at two world sizes must NOT cost a second slot. Pixels
        /// do not depend on world size - only pixelsPerUnit does - and that
        /// distinction is the whole reason the atlas fits in one page.
        /// </summary>
        [Test]
        public void SameShapeAtDifferentSizes_ReusesOneSlot()
        {
            Agent_Art.Disc(1f);                       // ensure the slot exists
            int before = Agent_Art.SlotCount;

            var small = Agent_Art.Disc(0.4f);
            var large = Agent_Art.Disc(9f);

            Assert.AreEqual(before, Agent_Art.SlotCount,
                "Requesting an existing shape at a new world size allocated another " +
                "atlas slot. The slot cache must be keyed on shape only.");
            Assert.AreSame(small.texture, large.texture);
            Assert.AreNotEqual(small.pixelsPerUnit, large.pixelsPerUnit,
                "World size must still change pixelsPerUnit, or every sprite draws the same size.");
        }

        /// <summary>
        /// Distinct shapes must occupy distinct rects. A shelf allocator that
        /// handed out overlapping boxes would still pass the sharing test above
        /// while drawing one shape as another.
        /// </summary>
        [Test]
        public void DistinctShapes_OccupyDistinctRects()
        {
            var square = Agent_Art.Square(1f);
            var disc = Agent_Art.Disc(1f);
            var blob = Agent_Art.Blob(1f);

            Assert.AreNotEqual(square.rect, disc.rect);
            Assert.AreNotEqual(disc.rect, blob.rect);
            Assert.IsFalse(Overlaps(disc.rect, blob.rect),
                "Two shapes were allocated overlapping regions of the atlas page.");
        }

        static bool Overlaps(Rect a, Rect b)
        {
            return a.xMin < b.xMax && b.xMin < a.xMax && a.yMin < b.yMax && b.yMin < a.yMax;
        }
    }
}
