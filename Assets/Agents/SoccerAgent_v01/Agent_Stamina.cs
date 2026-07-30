using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Energy system per PRD: 100 max, 60/s drain while boosting, 25/s recharge.
    /// Also implements UNITY_RULES exertion degradation: sustained boost accumulates
    /// wear that shrinks effective max stamina over continuous evaluation cycles.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_Stamina : MonoBehaviour
    {
        [Header("Stamina (PRD)")]
        public float maxStamina = 100f;
        public float drainPerSecond = 60f;
        public float rechargePerSecond = 25f;

        [Header("Exertion degradation (wear-and-tear)")]
        [Tooltip("Wear accumulated per second of boosting. Wear shrinks the effective stamina ceiling.")]
        public float wearPerBoostSecond = 0.002f;
        [Range(0.3f, 1f)]
        [Tooltip("Effective max stamina never degrades below this fraction of maxStamina.")]
        public float wearFloor = 0.6f;
        [Tooltip("If true, wear resets on every episode (training). Leave false for game-play fatigue across cycles.")]
        public bool resetWearOnEpisode = false;

        public float Current { get; private set; }
        public float Wear { get; private set; }

        public float EffectiveMax => maxStamina * Mathf.Max(wearFloor, 1f - Wear);
        public float Ratio => EffectiveMax > 0f ? Mathf.Clamp01(Current / EffectiveMax) : 0f;
        public bool HasStamina => Current > 0f;

        void Awake() => Current = maxStamina;

        /// <summary>Advance the stamina simulation one physics tick.</summary>
        public void Tick(bool boosting, float deltaTime)
        {
            if (boosting && Current > 0f)
            {
                Current = Mathf.Max(0f, Current - drainPerSecond * deltaTime);
                Wear = Mathf.Min(1f - wearFloor, Wear + wearPerBoostSecond * deltaTime);
            }
            else
            {
                Current = Mathf.Min(EffectiveMax, Current + rechargePerSecond * deltaTime);
            }
        }

        public void ResetForEpisode()
        {
            if (resetWearOnEpisode) Wear = 0f;
            Current = EffectiveMax;
        }
    }
}
