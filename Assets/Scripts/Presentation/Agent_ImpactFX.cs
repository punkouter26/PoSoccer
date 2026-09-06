using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Impact and effort FX, all sourced from the traction model rather than from
    /// the animation of it.
    ///
    /// Agent_ParticleFX already covers the BALL: turf spray from lateral slip,
    /// debris on ball contacts, the wall-kick shockwave, goal confetti. What it
    /// does not cover is the part of the physics that has never been drawn at all:
    ///
    ///  - PLAYER-ON-PLAYER CONTACT. Agent_Contact has computed a real summed
    ///    normal impulse on every collision since it was written, used it to
    ///    stagger the loser, and rendered exactly nothing. A shoulder charge that
    ///    changes who wins a duel currently looks like two sprites overlapping.
    ///    Now it throws a normal-aligned shock, scaled by that same impulse.
    ///
    ///  - THE FRICTION CIRCLE. Agent_Soccer.TractionSaturation is the single
    ///    number that decides whether a cut is free or expensive, and it was
    ///    invisible. A player at the limit of grip now lays a scuff streak, so a
    ///    hard change of direction reads as costing something.
    ///
    ///  - EXHAUSTION. Agent_Stamina drains, wears permanently and throttles drive
    ///    force to 60% at empty. Nothing on screen said so.
    ///
    ///  - BOOST IGNITION. A 2.2x force multiplier deserves more than a trail.
    ///
    /// SHARED SYSTEMS, NOT PER-PLAYER ONES: the emit call carries position and
    /// velocity, so three systems and one sprite pool cover any squad size and the
    /// draw-call count does not scale with players. Same reasoning as
    /// Agent_ParticleFX, and the two deliberately look like one layer.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_ImpactFX : MonoBehaviour
    {
        [Header("Player contact")]
        [Tooltip("Summed normal impulse (N*s) below which a collision is a jostle and draws " +
                 "nothing. Below Agent_Contact's own stagger threshold of 12 on purpose - a " +
                 "shoulder that rocks nobody is still worth a puff.")]
        [SerializeField] private float _contactThreshold = 6f;
        [Tooltip("Impulse (N*s) at which the shock is at full size. Matches Agent_Contact's " +
                 "stagger saturation so the visual peaks where the gameplay effect does.")]
        [SerializeField] private float _contactSaturation = 45f;

        [Header("Traction scuff")]
        [Tooltip("Fraction of the friction circle above which the feet start laying scuffs. " +
                 "0.9 keeps it to genuine limit-of-grip moments rather than every stride.")]
        [Range(0.5f, 1f)] [SerializeField] private float _scuffSaturation = 0.9f;
        [Tooltip("Speed (m/s) below which a saturated foot is a standing shove, not a skid.")]
        [SerializeField] private float _scuffMinSpeed = 2f;
        [Tooltip("Scuff marks per second per saturated player.")]
        [SerializeField] private float _scuffRate = 26f;

        [Header("Effort")]
        [Tooltip("Stamina ratio below which a player visibly labours.")]
        [Range(0f, 0.6f)] [SerializeField] private float _tiredBelow = 0.22f;
        [Tooltip("Effort wisps per second at zero stamina.")]
        [SerializeField] private float _effortRate = 7f;

        [SerializeField] private bool _enableImpactFX = true;

        Agent_EnvController _env;
        ParticleSystem _scuff, _effort, _shrapnel;
        Transform _shockPool;
        readonly List<SpriteRenderer> _shocks = new();
        int _nextShock;

        float _scuffCredit, _effortCredit;

        // Rising-edge detection for boost, one flag per agent.
        readonly Dictionary<Agent_Soccer, bool> _wasBoosting = new();

        // Both bodies in a collision carry an Agent_Contact, so every player-on-
        // player hit is reported twice - once from each side, same frame, same
        // point, opposite normal. Drawing both doubles the shock and looks like a
        // stutter. Deduped on (time, place) rather than on instance ids because
        // Object.GetInstanceID is deprecated-to-error in Unity 6.5.
        float _lastContactTime = float.NegativeInfinity;
        Vector2 _lastContactPoint;

        const float DedupeSeconds = 0.03f;
        const float DedupeDistance = 0.6f;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableImpactFX || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            BuildSystems();
            BuildShockPool();

            for (int i = 0; i < _env.agents.Count; i++)
            {
                var agent = _env.agents[i];
                if (agent == null) continue;
                var contact = agent.GetComponent<Agent_Contact>();
                if (contact != null) contact.PlayerContact += OnPlayerContact;
            }
        }

        void OnDestroy()
        {
            if (_env == null) return;
            for (int i = 0; i < _env.agents.Count; i++)
            {
                var agent = _env.agents[i];
                if (agent == null) continue;
                var contact = agent.GetComponent<Agent_Contact>();
                if (contact != null) contact.PlayerContact -= OnPlayerContact;
            }
        }

        // -- Construction ----------------------------------------------------

        void BuildSystems()
        {
            // Scuff: stretched along its own velocity so a skid mark points the way
            // the foot slid, which is the whole information content of the effect.
            _scuff = BuildSystem("TractionScuff", new Color(0.22f, 0.17f, 0.12f, 0.85f),
                size: 0.16f, life: 0.9f, max: 220, sortingOrder: 3);
            var scuffRenderer = _scuff.GetComponent<ParticleSystemRenderer>();
            scuffRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            scuffRenderer.velocityScale = 0.12f;
            scuffRenderer.lengthScale = 2.4f;

            _effort = BuildSystem("Effort", new Color(0.85f, 0.9f, 1f, 0.5f),
                size: 0.13f, life: 0.85f, max: 120, sortingOrder: 16);

            _shrapnel = BuildSystem("ContactShrapnel", new Color(0.95f, 0.93f, 0.85f),
                size: 0.09f, life: 0.5f, max: 220, sortingOrder: 16);
        }

        ParticleSystem BuildSystem(string label, Color tint, float size, float life,
                                   int max, int sortingOrder)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = life;
            main.startSize = size;
            main.startSpeed = 0f;               // velocity comes from EmitParams
            main.startColor = tint;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;          // top-down: nothing falls in-plane

            var emission = ps.emission;
            emission.enabled = false;           // emission is entirely manual

            var shape = ps.shape;
            shape.enabled = false;

            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = 0.15f;
            limit.limit = 0.4f;

            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            colorOverLife.color = gradient;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Sprites/Default");
            if (shader != null) renderer.material = new Material(shader);
            renderer.sortingOrder = sortingOrder;

            return ps;
        }

        /// <summary>
        /// Directional shocks are a scaled sprite rather than particles: one quad
        /// squashed along the contact normal reads as a slam, where a particle
        /// ring needs dozens of billboards to look like an arc at all. Same
        /// decision, and the same pooling, as Agent_ParticleFX's shockwaves.
        /// </summary>
        void BuildShockPool()
        {
            var go = new GameObject("ContactShocks");
            go.transform.SetParent(transform, false);
            _shockPool = go.transform;

            for (int i = 0; i < 4; i++)
            {
                var shockGo = new GameObject($"Shock_{i}");
                shockGo.transform.SetParent(_shockPool, false);
                var renderer = shockGo.AddComponent<SpriteRenderer>();
                renderer.sprite = Agent_Art.Disc(1f, 0.62f);
                renderer.sortingOrder = 7;
                renderer.enabled = false;
                _shocks.Add(renderer);
            }
        }

        // -- Emission --------------------------------------------------------

        void OnPlayerContact(Vector2 point, Vector2 normal, float impulse)
        {
            if (impulse < _contactThreshold) return;

            // Second report of the same collision, from the other body.
            if (Time.time - _lastContactTime < DedupeSeconds
                && (point - _lastContactPoint).sqrMagnitude < DedupeDistance * DedupeDistance)
            {
                return;
            }
            _lastContactTime = Time.time;
            _lastContactPoint = point;

            float strength = Mathf.Clamp01(
                Mathf.InverseLerp(_contactThreshold, _contactSaturation, impulse));

            var shock = NextShock();
            if (shock != null)
            {
                ShockAsync(shock, point, normal, strength,
                    this.GetCancellationTokenOnDestroy()).Forget();
            }

            if (_shrapnel == null) return;
            int count = Mathf.Clamp(Mathf.RoundToInt(3f + strength * 14f), 3, 18);
            Vector2 along = new(-normal.y, normal.x);
            for (int i = 0; i < count; i++)
            {
                // Sprayed ALONG the contact plane, which is where material actually
                // goes when two bodies meet - not radially, which reads as an
                // explosion rather than a collision.
                var emit = new ParticleSystem.EmitParams
                {
                    position = point + Random.insideUnitCircle * 0.14f,
                    velocity = (along * Random.Range(-1f, 1f) + normal * Random.Range(-0.35f, 0.35f))
                               .normalized * Random.Range(1.5f, 2f + strength * 7f),
                    startLifetime = Random.Range(0.2f, 0.5f),
                };
                _shrapnel.Emit(emit, 1);
            }
        }

        void Update()
        {
            if (_env == null || _scuff == null) return;

            float dt = Time.deltaTime;
            var agents = _env.agents;

            for (int i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent == null || agent.Body == null || !agent.isActiveAndEnabled) continue;

                EmitScuff(agent, dt);
                EmitEffort(agent, dt);
                CheckBoostIgnition(agent);
            }
        }

        /// <summary>
        /// A scuff is laid when the feet are at the limit of the friction circle
        /// AND the body is moving - which is exactly the condition under which the
        /// traction model is taking speed away rather than adding it.
        /// </summary>
        void EmitScuff(Agent_Soccer agent, float dt)
        {
            if (agent.TractionSaturation < _scuffSaturation) return;

            Vector2 velocity = agent.Body.linearVelocity;
            float speed = velocity.magnitude;
            if (speed < _scuffMinSpeed) return;

            float strength = Mathf.InverseLerp(_scuffSaturation, 1f, agent.TractionSaturation);
            _scuffCredit += strength * _scuffRate * dt;
            int count = Mathf.FloorToInt(_scuffCredit);
            if (count <= 0) return;
            _scuffCredit -= count;

            Vector2 heel = (Vector2)agent.transform.position - velocity.normalized * 0.3f;
            for (int i = 0; i < count; i++)
            {
                var emit = new ParticleSystem.EmitParams
                {
                    position = heel + Random.insideUnitCircle * 0.18f,
                    velocity = velocity * 0.18f,
                    startLifetime = Random.Range(0.6f, 1.1f),
                };
                _scuff.Emit(emit, 1);
            }
        }

        void EmitEffort(Agent_Soccer agent, float dt)
        {
            if (_effort == null || agent.Stamina == null) return;
            float ratio = agent.Stamina.Ratio;
            if (ratio > _tiredBelow) return;

            float fatigue = 1f - Mathf.InverseLerp(0f, _tiredBelow, ratio);
            _effortCredit += fatigue * _effortRate * dt;
            int count = Mathf.FloorToInt(_effortCredit);
            if (count <= 0) return;
            _effortCredit -= count;

            for (int i = 0; i < count; i++)
            {
                var emit = new ParticleSystem.EmitParams
                {
                    position = (Vector2)agent.transform.position + Random.insideUnitCircle * 0.3f,
                    // Drifting off the body rather than trailing it, so it reads as
                    // heat coming off a labouring player and not as a movement trail.
                    velocity = new Vector2(Random.Range(-0.4f, 0.4f), Random.Range(0.6f, 1.5f)),
                    startLifetime = Random.Range(0.5f, 1f),
                };
                _effort.Emit(emit, 1);
            }
        }

        void CheckBoostIgnition(Agent_Soccer agent)
        {
            _wasBoosting.TryGetValue(agent, out bool was);
            bool now = agent.IsBoosting;
            _wasBoosting[agent] = now;
            if (!now || was) return;

            var shock = NextShock();
            if (shock == null) return;

            // Fired backwards out of the heels: an ignition, not an impact.
            Vector2 facing = agent.transform.up;
            ShockAsync(shock, (Vector2)agent.transform.position - facing * 0.35f,
                facing, 0.4f, this.GetCancellationTokenOnDestroy()).Forget();
        }

        SpriteRenderer NextShock()
        {
            if (_shocks.Count == 0) return null;
            var shock = _shocks[_nextShock];
            _nextShock = (_nextShock + 1) % _shocks.Count;
            return shock;
        }

        /// <summary>
        /// An expanding ellipse, wide across the contact plane and shallow along
        /// the normal, so the shock points the way the impulse did.
        /// </summary>
        async UniTaskVoid ShockAsync(SpriteRenderer shock, Vector2 point, Vector2 normal,
                                     float strength, CancellationToken token)
        {
            shock.transform.position = point;
            shock.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal);
            shock.enabled = true;

            float duration = Mathf.Lerp(0.18f, 0.36f, strength);
            float reach = Mathf.Lerp(1.2f, 3.4f, strength);
            float t = 0f;

            while (t < duration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / duration);
                float span = Mathf.Lerp(0.3f, reach, k);
                shock.transform.localScale = new Vector3(span, span * 0.45f, 1f);
                shock.color = new Color(1f, 0.97f, 0.9f, (1f - k) * Mathf.Lerp(0.5f, 0.95f, strength));
                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }

            if (shock != null) shock.enabled = false;
        }
    }
}
