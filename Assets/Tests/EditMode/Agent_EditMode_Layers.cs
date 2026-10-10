using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the two hand-edited project settings behind the 2026-10-10 layer change:
    /// the layer slots in TagManager.asset and the FX row of Physics2DSettings'
    /// m_LayerCollisionMatrix. Both were written as YAML with no editor running, and
    /// both fail silently when wrong - a missing layer leaves a sensor on its fallback
    /// mask, and a wrong matrix row is a ball that passes through a wall.
    /// </summary>
    public sealed class Agent_EditMode_Layers
    {
        static readonly string[] GameplayLayers =
        {
            Agent_Layers.PLAYER, Agent_Layers.BALL, Agent_Layers.PITCH,
            Agent_Layers.PLAYER_BLUE, Agent_Layers.PLAYER_RED,
        };

        [Test]
        public void EveryLayerTheCodeNames_ExistsInTheSlotItWasAuthoredIn()
        {
            Assert.That(LayerMask.NameToLayer(Agent_Layers.PLAYER), Is.EqualTo(6));
            Assert.That(LayerMask.NameToLayer(Agent_Layers.BALL), Is.EqualTo(7));
            Assert.That(LayerMask.NameToLayer(Agent_Layers.PITCH), Is.EqualTo(8));
            Assert.That(LayerMask.NameToLayer(Agent_Layers.FX), Is.EqualTo(9));
            Assert.That(LayerMask.NameToLayer(Agent_Layers.PLAYER_BLUE), Is.EqualTo(10));
            Assert.That(LayerMask.NameToLayer(Agent_Layers.PLAYER_RED), Is.EqualTo(11));
        }

        [Test]
        public void EverySensorInTheBattery_NamesALayerThatExists()
        {
            foreach (var spec in Sensor_Vision.Battery)
            {
                if (spec.Layer == Sensor_Vision.OPPONENT_TAG_PLACEHOLDER) continue;
                Assert.That(LayerMask.NameToLayer(spec.Layer), Is.GreaterThanOrEqualTo(0),
                    $"{spec.Name} casts against layer '{spec.Layer}', which is not defined.");
            }
        }

        [Test]
        public void Fx_CollidesWithNothing_AndGameplayLayersStillCollideWithEachOther()
        {
            int fx = LayerMask.NameToLayer(Agent_Layers.FX);
            Assert.That(fx, Is.GreaterThanOrEqualTo(0));
            for (int layer = 0; layer < 32; layer++)
            {
                Assert.IsTrue(Physics2D.GetIgnoreLayerCollision(fx, layer),
                    $"FX still collides with layer {layer} ('{LayerMask.LayerToName(layer)}').");
            }

            int defaultLayer = 0;
            for (int a = 0; a < GameplayLayers.Length; a++)
            {
                int layerA = LayerMask.NameToLayer(GameplayLayers[a]);
                Assert.IsFalse(Physics2D.GetIgnoreLayerCollision(layerA, defaultLayer),
                    $"{GameplayLayers[a]} no longer collides with Default.");
                for (int b = a; b < GameplayLayers.Length; b++)
                {
                    int layerB = LayerMask.NameToLayer(GameplayLayers[b]);
                    Assert.IsFalse(Physics2D.GetIgnoreLayerCollision(layerA, layerB),
                        $"{GameplayLayers[a]} no longer collides with {GameplayLayers[b]} - " +
                        "the collision matrix was meant to change for FX only.");
                }
            }
        }
    }
}
