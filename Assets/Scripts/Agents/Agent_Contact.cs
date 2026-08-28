using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Physical response to player-on-player contact, without articulation.
    ///
    /// THIS IS NOT A RAGDOLL, AND DELIBERATELY SO. The project contains no
    /// Joint2D, HingeJoint or ArticulationBody anywhere: every player is a single
    /// Rigidbody2D, and "active-ragdoll articulation" is an accepted open
    /// deviation in docs/rules-exemptions.md. Building one would change the action
    /// space and obsolete every policy, so it belongs after the benchmark is met.
    /// What actually reads as physical on a top-down pitch is much cheaper: mass
    /// deciding who wins a collision, and a brief loss of control after a heavy
    /// one.
    ///
    /// Stagger reduces drive authority for a moment after a hard hit. It is
    /// deliberately a multiplier on output rather than a lockout: a policy cannot
    /// be left with no control at all, or the resulting transitions teach it that
    /// its actions sometimes do nothing, which is exactly the unmodellable
    /// dynamics problem the wall kick had.
    ///
    /// Impulse is read from the collision rather than recomputed, so a light
    /// player bouncing off a heavy one is handled by the solver, not by a rule.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [DisallowMultipleComponent]
    public sealed class Agent_Contact : MonoBehaviour
    {
        [Tooltip("Collision impulse (N*s) below which contact is ignored. A jostle should " +
                 "not stagger anybody.")]
        [SerializeField] private float _staggerThreshold = 12f;
        [Tooltip("Impulse at which stagger is at its deepest.")]
        [SerializeField] private float _staggerSaturation = 45f;
        [Tooltip("Seconds a full-strength stagger takes to clear.")]
        [SerializeField] private float _staggerSeconds = 0.45f;
        [Tooltip("Drive authority remaining at the deepest stagger. Never 0 - an agent " +
                 "that briefly cannot act at all learns that its actions are unreliable.")]
        [Range(0.3f, 1f)] [SerializeField] private float _minAuthority = 0.55f;
        [SerializeField] private bool _enableStagger = true;

        float _stagger;   // 0 = composed, 1 = fully rocked

        /// <summary>
        /// Drive-force multiplier, 1 when composed. Read by Agent_Soccer each
        /// physics tick.
        /// </summary>
        public float DriveAuthority => Mathf.Lerp(1f, _minAuthority, _stagger);

        /// <summary>True while still recovering from a heavy contact.</summary>
        public bool IsStaggered => _stagger > 0.01f;

        public void ResetForEpisode() => _stagger = 0f;

        void FixedUpdate()
        {
            if (_stagger <= 0f) return;
            _stagger = Mathf.Max(0f, _stagger - Time.fixedDeltaTime / Mathf.Max(0.05f, _staggerSeconds));
        }

        void OnCollisionEnter2D(Collision2D collision)
        {
            if (!_enableStagger) return;
            // Players only. Walls and the ball do not knock anyone off balance.
            if (collision.collider.GetComponentInParent<Agent_Soccer>() == null) return;

            // Collision2D has no `impulse` (that is the 3D API); the 2D solver
            // reports per-contact normal impulses in N*s, so sum them.
            float impulse = 0f;
            for (int i = 0; i < collision.contactCount; i++)
                impulse += collision.GetContact(i).normalImpulse;
            if (impulse < _staggerThreshold) return;

            float strength = Mathf.InverseLerp(_staggerThreshold, _staggerSaturation, impulse);
            _stagger = Mathf.Clamp01(Mathf.Max(_stagger, strength));
        }
    }
}
