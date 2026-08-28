using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Match audio: a small mixing desk built from AudioSources.
    ///
    /// WHY NOT AN AudioMixer ASSET. Everything else in this project's
    /// presentation layer is constructed at runtime precisely so no scene has to
    /// carry a serialized reference that can drift. An .mixer asset would need
    /// authoring plus per-scene wiring for exposed parameters, and it cannot be
    /// created through the sanctioned MCP asset path anyway. The four things a
    /// mixer would have bought here - category buses, ducking, a low-pass on
    /// pause, and a master mute - are all implemented directly below, and they
    /// are testable from code, which a mixer's DSP graph is not.
    ///
    /// Three defects in the previous version, all fixed here:
    ///  1. every AudioSource had spatialBlend = 0, so despite every clip being
    ///     imported as 3D the game had NO spatial audio at all;
    ///  2. all one-shots shared a single AudioSource and set `.pitch` on it
    ///     before each PlayOneShot - pitch is a property of the SOURCE, so two
    ///     overlapping impacts detuned each other;
    ///  3. crowd and music had no ducking, so the goal horn fought the bed it
    ///     was supposed to sit on top of.
    ///
    /// Positional sounds (ball impacts) play through a round-robin voice pool of
    /// real 3D sources. Broadcast sounds (whistle, horn) deliberately stay 2D -
    /// a referee's whistle in a televised match is not panned to the touchline.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Audio : MonoBehaviour
    {
        public static bool Muted
        {
            get => PlayerPrefs.GetInt("posoccer_muted", 0) == 1;
            set => PlayerPrefs.SetInt("posoccer_muted", value ? 1 : 0);
        }

        [Header("Clips (placeholders generated; swap with Store packs freely)")]
        public AudioClip kick;
        public AudioClip wall;
        public AudioClip goalHorn;
        public AudioClip whistle;
        public AudioClip crowdLoop;
        public AudioClip music;
        [Tooltip("Optional. When empty, a boo is faked by pitching the crowd bed down - " +
                 "audible enough to read as disapproval without shipping another WAV.")]
        public AudioClip crowdBoo;

        [Header("Crowd dynamics")]
        [Range(0f, 1f)] public float crowdBase = 0.16f;
        [Range(0f, 1f)] public float crowdSwellMax = 0.45f;

        [Header("Buses")]
        [Range(0f, 1f)] [SerializeField] private float _masterVolume = 1f;
        [Range(0f, 1f)] [SerializeField] private float _sfxVolume = 1f;
        [Range(0f, 1f)] [SerializeField] private float _crowdVolume = 1f;
        [Range(0f, 1f)] [SerializeField] private float _musicVolume = 0.25f;

        [Header("Spatialisation")]
        [Tooltip("Concurrent positional voices. Each holds its own pitch, which is what fixes overlapping impacts.")]
        [SerializeField] private int _voices = 8;
        [Range(0f, 1f)] [SerializeField] private float _spatialBlend = 0.8f;
        [SerializeField] private float _minDistance = 6f;
        [SerializeField] private float _maxDistance = 40f;
        [Tooltip("Extra stereo spread applied on top of 3D panning - phone speakers barely resolve the 3D image.")]
        [Range(0f, 1f)] [SerializeField] private float _stereoSpread = 0.7f;

        [Header("Dynamics")]
        [Tooltip("How far the crowd and music duck under a whistle or horn.")]
        [Range(0f, 1f)] [SerializeField] private float _duckAmount = 0.55f;
        [Tooltip("Seconds for a duck to recover.")]
        [SerializeField] private float _duckRelease = 1.4f;
        [Tooltip("Low-pass cutoff while the clock is frozen (replay, countdown, end panel).")]
        [SerializeField] private float _frozenCutoff = 850f;

        Agent_EnvController _env;
        AudioSource _crowd, _music;
        AudioLowPassFilter _crowdFilter, _musicFilter;
        AudioSource[] _pool;
        int _nextVoice;
        float _goalSpike;
        float _booTime;
        float _duck;

        // -- Public reactions ------------------------------------------------

        /// <summary>
        /// Swell the crowd without a goal - a near miss, a great block, a
        /// counter-attack. <paramref name="amount"/> is 0..1 and stacks with the
        /// proximity-driven bed rather than replacing it.
        /// </summary>
        public void Cheer(float amount)
        {
            _goalSpike = Mathf.Clamp01(Mathf.Max(_goalSpike, amount));
        }

        /// <summary>Crowd disapproval: a pitched-down swell (or the boo clip when one is wired).</summary>
        public void Boo(float amount)
        {
            _booTime = Mathf.Max(_booTime, 1.2f * Mathf.Clamp01(amount));
            _goalSpike = Mathf.Clamp01(Mathf.Max(_goalSpike, amount * 0.6f));
            if (crowdBoo != null) Play2D(crowdBoo, 0.6f * Mathf.Clamp01(amount));
        }

        /// <summary>Referee's whistle - kickoff countdown, halftime, full time.</summary>
        public void Whistle(float volume = 0.5f)
        {
            Play2D(whistle, volume);
            Duck();
        }

        // -- Lifecycle -------------------------------------------------------

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _env.EpisodeEnded += OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit += OnBallHit;

            BuildMixingDesk();

            if (crowdLoop != null) { _crowd.clip = crowdLoop; _crowd.volume = 0f; _crowd.Play(); }
            if (music != null) { _music.clip = music; _music.volume = 0f; _music.Play(); }
            Whistle(0.5f);
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit -= OnBallHit;
        }

        /// <summary>
        /// Crowd and music each get their own GameObject because an
        /// AudioLowPassFilter applies to every source on the object it sits on -
        /// hanging one on the shared root would smother the impacts too.
        /// </summary>
        void BuildMixingDesk()
        {
            _crowd = NewBus("Audio_Crowd", out _crowdFilter);
            _music = NewBus("Audio_Music", out _musicFilter);

            var poolRoot = new GameObject("Audio_Voices");
            poolRoot.transform.SetParent(transform, false);

            _pool = new AudioSource[Mathf.Max(1, _voices)];
            for (int i = 0; i < _pool.Length; i++)
            {
                var go = new GameObject($"Voice_{i}");
                go.transform.SetParent(poolRoot.transform, false);
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = _spatialBlend;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = _minDistance;
                source.maxDistance = _maxDistance;
                source.dopplerLevel = 0f;   // a top-down pitch has no fly-bys to sell
                _pool[i] = source;
            }
        }

        AudioSource NewBus(string label, out AudioLowPassFilter filter)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);
            var source = go.AddComponent<AudioSource>();
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;   // beds are non-positional by design
            filter = go.AddComponent<AudioLowPassFilter>();
            filter.cutoffFrequency = 22000f;
            return source;
        }

        // -- Playback --------------------------------------------------------

        /// <summary>Non-positional cue on the SFX bus (whistle, horn, UI).</summary>
        void Play2D(AudioClip clip, float volume, float pitch = 1f)
        {
            var source = NextVoice();
            if (clip == null || source == null || Muted) return;
            source.spatialBlend = 0f;
            source.panStereo = 0f;
            source.pitch = pitch;
            source.volume = Mathf.Clamp01(volume) * _sfxVolume * _masterVolume;
            source.clip = clip;
            source.Play();
        }

        /// <summary>Positional cue on the SFX bus - ball impacts.</summary>
        void PlayAt(AudioClip clip, Vector3 worldPosition, float volume, float pitch)
        {
            var source = NextVoice();
            if (clip == null || source == null || Muted) return;
            source.transform.position = worldPosition;
            source.spatialBlend = _spatialBlend;

            // Explicit stereo spread on top of the 3D image: on a phone speaker
            // the HRTF-free 3D pan is nearly inaudible, but panStereo is not.
            float halfWidth = Mathf.Max(0.01f, _env.PitchHalfExtents.x);
            float offset = (worldPosition.x - transform.position.x) / halfWidth;
            source.panStereo = Mathf.Clamp(offset, -1f, 1f) * _stereoSpread;

            source.pitch = pitch;
            source.volume = Mathf.Clamp01(volume) * _sfxVolume * _masterVolume;
            source.clip = clip;
            source.Play();
        }

        /// <summary>
        /// Round-robin over the pool. Prefers a free voice so a long clip is not
        /// cut off while short ones are idle; falls back to the oldest slot.
        /// </summary>
        AudioSource NextVoice()
        {
            if (_pool == null) return null;
            for (int i = 0; i < _pool.Length; i++)
            {
                var candidate = _pool[(_nextVoice + i) % _pool.Length];
                if (candidate != null && !candidate.isPlaying)
                {
                    _nextVoice = (_nextVoice + i + 1) % _pool.Length;
                    return candidate;
                }
            }
            var source = _pool[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _pool.Length;
            return source;
        }

        void Duck() => _duck = 1f;

        // -- Events ----------------------------------------------------------

        void OnBallHit(Collision2D collision)
        {
            float impact = collision.relativeVelocity.magnitude;
            if (impact < 1.5f) return;

            float volume = Mathf.Clamp01(impact / 14f);
            float pitch = Random.Range(0.92f, 1.08f);
            // The contact point, not the ball centre - a ball hitting the far post
            // should sound like it happened at the far post.
            Vector3 at = collision.contactCount > 0
                ? (Vector3)collision.GetContact(0).point
                : collision.transform.position;

            if (collision.collider.CompareTag("Wall")) PlayAt(wall, at, volume * 0.8f, pitch);
            else PlayAt(kick, at, volume, pitch);
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner != null)
            {
                Play2D(goalHorn, 0.85f);
                Duck();
                _goalSpike = 1f;
            }
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _duck = Mathf.Max(0f, _duck - dt / Mathf.Max(0.05f, _duckRelease));
            _goalSpike = Mathf.Max(0f, _goalSpike - dt * 0.7f);
            _booTime = Mathf.Max(0f, _booTime - dt);

            UpdateCrowd(dt);
            UpdateMusic(dt);
            UpdateFilters(dt);
        }

        void UpdateCrowd(float dt)
        {
            if (_crowd == null || _crowd.clip == null) return;

            float target = 0f;
            if (!Muted && _env.Ball != null)
            {
                float distToGoal = Mathf.Min(
                    Vector2.Distance(_env.Ball.position, _env.GetGoalPosition(Agent_Soccer.Team.Blue)),
                    Vector2.Distance(_env.Ball.position, _env.GetGoalPosition(Agent_Soccer.Team.Red)));
                float tension = Mathf.Clamp01(1f - distToGoal / 9f);
                target = crowdBase + (crowdSwellMax - crowdBase) * tension + _goalSpike * 0.5f;
                target *= _crowdVolume * _masterVolume;
                target *= 1f - _duck * _duckAmount;
            }
            _crowd.volume = Mathf.MoveTowards(_crowd.volume, target, dt * 0.6f);

            // A boo is the crowd bed dropped a fourth; it resolves back to unity
            // pitch so a stalled passage does not leave the stadium detuned.
            float wantPitch = _booTime > 0f ? 0.78f : 1f;
            _crowd.pitch = Mathf.MoveTowards(_crowd.pitch, wantPitch, dt * 1.5f);
        }

        void UpdateMusic(float dt)
        {
            if (_music == null || _music.clip == null) return;
            float target = Muted ? 0f : _musicVolume * _masterVolume * (1f - _duck * _duckAmount);
            _music.volume = Mathf.MoveTowards(_music.volume, target, dt * 0.8f);
        }

        /// <summary>
        /// Everything muffles while the clock is stopped. This is what makes a
        /// goal replay feel like a deliberate cut rather than the game hanging.
        /// </summary>
        void UpdateFilters(float dt)
        {
            float target = Agent_TimeFreeze.IsFrozen ? _frozenCutoff : 22000f;
            float speed = Mathf.Abs(22000f - _frozenCutoff) * 4f * dt;
            if (_crowdFilter != null)
                _crowdFilter.cutoffFrequency =
                    Mathf.MoveTowards(_crowdFilter.cutoffFrequency, target, speed);
            if (_musicFilter != null)
                _musicFilter.cutoffFrequency =
                    Mathf.MoveTowards(_musicFilter.cutoffFrequency, target, speed);
        }
    }
}
