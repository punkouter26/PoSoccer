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
    /// a referee's whistle in a televised match is not panned to the touchline -
    /// and now run through a shared reverb tail so they read as a stadium rather
    /// than as a phone speaker.
    ///
    /// 2026-09-05 - THE CROWD IS THREE LAYERS, NOT ONE. A single loop whose
    /// volume tracks ball-to-goal distance can only ever get louder; a crowd that
    /// is interested sounds DIFFERENT, not just bigger. The bed
    /// (crowd_loop) now carries a swell layer (crowd_swell, more upper-mid
    /// energy) crossfaded in on pressure, and a roar one-shot on goals and near
    /// misses. All three are the same crowd, which is why the swell is the bed's
    /// own material refiltered rather than a second recording.
    ///
    /// Physics now drives audio parameters directly, using values the simulation
    /// already computes and previously threw away:
    ///  - a rolling loop under the ball whose low-pass cutoff and volume track
    ///    ball speed, so the ball is audible while it is moving rather than only
    ///    when it hits something;
    ///  - a post ring, distinct from the wall thud, chosen by how close the
    ///    contact was to the goal centre (the goal frame is a sprite with no
    ///    collider - see Agent_Commentary - so a post hit IS a wall hit, and the
    ///    only thing that separates them is where it happened);
    ///  - a breathing layer on the most tired player, driven by Agent_Stamina.
    ///
    /// New clips are loaded from Resources/Audio rather than added as serialized
    /// fields: filling a new AudioClip field means editing every scene, and scene
    /// authoring is MCP-only per UNITY_RULES. A serialized field that is set still
    /// wins, so a Store pack dropped into the Inspector overrides the generated
    /// placeholder without any code change.
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

        [Header("Adaptive layers (auto-loaded from Resources/Audio when empty)")]
        [Tooltip("Brighter crowd bed crossfaded in under pressure.")]
        public AudioClip crowdSwell;
        [Tooltip("One-shot roar on a goal or a near miss.")]
        public AudioClip crowdRoar;
        [Tooltip("Looping rumble under a moving ball; cutoff and level track speed.")]
        public AudioClip ballRoll;
        [Tooltip("Looping breath on the most tired player.")]
        public AudioClip breath;
        [Tooltip("Metallic ring for a contact near the goal centre.")]
        public AudioClip post;

        [Header("Crowd dynamics")]
        [Range(0f, 1f)] public float crowdBase = 0.16f;
        [Range(0f, 1f)] public float crowdSwellMax = 0.45f;
        [Tooltip("Pressure (0..1) at which the bright swell layer is fully faded in.")]
        [Range(0.1f, 1f)] [SerializeField] private float _swellFullAt = 0.75f;
        [Tooltip("Loudest the swell layer ever gets, relative to the bed.")]
        [Range(0f, 1f)] [SerializeField] private float _swellMaxLevel = 0.55f;

        [Header("Stadium space")]
        [Tooltip("Reverb tail on broadcast cues (whistle, horn). 0 disables it.")]
        [Range(0f, 1f)] [SerializeField] private float _reverbMix = 0.32f;
        [Tooltip("Reverb decay in seconds - a bowl this size rings for about this long.")]
        [SerializeField] private float _reverbDecay = 1.6f;

        [Header("Physics-driven")]
        [Tooltip("Ball speed (m/s) at which the rolling loop is fully open and loudest.")]
        [SerializeField] private float _rollFullSpeed = 12f;
        [Range(0f, 1f)] [SerializeField] private float _rollVolume = 0.35f;
        [Tooltip("Distance from the goal centre, as a fraction of goal width, inside which " +
                 "a wall contact rings like a post instead of thudding like a wall.")]
        [Range(0.1f, 1.5f)] [SerializeField] private float _postZone = 0.62f;
        [Tooltip("Stamina ratio below which the tired player starts to be heard breathing.")]
        [Range(0f, 1f)] [SerializeField] private float _breathBelow = 0.55f;
        [Range(0f, 1f)] [SerializeField] private float _breathVolume = 0.3f;

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
        AudioSource _crowd, _music, _swell;
        AudioLowPassFilter _crowdFilter, _musicFilter, _swellFilter;
        AudioSource[] _pool;
        AudioSource[] _broadcast;
        int _nextVoice;
        int _nextBroadcast;
        AudioSource _roll, _breath;
        AudioLowPassFilter _rollFilter;
        Agent_Soccer[] _players;
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
            // Headless training/eval has no listener and no reason to mix audio.
            // `--no-graphics` only kills rendering: mlagents launches the player
            // with -batchmode -nographics, and audio kept running underneath, so
            // 4 env processes x 16 pitches at time_scale 20 turned the looping
            // crowd bed into a constant drone on the training machine. Bail out
            // before subscribing so no handler can wake the desk back up.
            if (Application.isBatchMode ||
                SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                enabled = false;
                return;
            }

            _env = GetComponent<Agent_EnvController>();
            _env.EpisodeEnded += OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit += OnBallHit;

            LoadMissingClips();
            _players = GetComponentsInChildren<Agent_Soccer>();

            BuildMixingDesk();

            if (crowdLoop != null) { _crowd.clip = crowdLoop; _crowd.volume = 0f; _crowd.Play(); }
            if (music != null) { _music.clip = music; _music.volume = 0f; _music.Play(); }
            if (crowdSwell != null) { _swell.clip = crowdSwell; _swell.volume = 0f; _swell.Play(); }
            if (ballRoll != null && _roll != null) { _roll.clip = ballRoll; _roll.volume = 0f; _roll.Play(); }
            if (breath != null && _breath != null) { _breath.clip = breath; _breath.volume = 0f; _breath.Play(); }
            Whistle(0.5f);
        }

        /// <summary>
        /// Fill any clip field the scene did not serialize from Resources/Audio.
        /// An Inspector-assigned clip always wins - this is a floor, not an
        /// override, so dropping a Store pack into the fields keeps working.
        /// A missing file is not an error: every consumer below null-checks, and
        /// the layer simply does not sound.
        /// </summary>
        void LoadMissingClips()
        {
            if (music == null) music = Resources.Load<AudioClip>("Audio/music");
            if (crowdSwell == null) crowdSwell = Resources.Load<AudioClip>("Audio/crowd_swell");
            if (crowdRoar == null) crowdRoar = Resources.Load<AudioClip>("Audio/crowd_roar");
            if (crowdBoo == null) crowdBoo = Resources.Load<AudioClip>("Audio/crowd_boo");
            if (ballRoll == null) ballRoll = Resources.Load<AudioClip>("Audio/ball_roll");
            if (breath == null) breath = Resources.Load<AudioClip>("Audio/breath");
            if (post == null) post = Resources.Load<AudioClip>("Audio/post");
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
            _swell = NewBus("Audio_CrowdSwell", out _swellFilter);

            var poolRoot = new GameObject("Audio_Voices");
            poolRoot.transform.SetParent(transform, false);

            _pool = new AudioSource[Mathf.Max(1, _voices)];
            for (int i = 0; i < _pool.Length; i++)
            {
                var go = new GameObject($"Voice_{i}");
                go.transform.SetParent(poolRoot.transform, false);
                _pool[i] = NewPositionalVoice(go);
            }

            BuildBroadcastBus();
            BuildPhysicsVoices();
        }

        AudioSource NewPositionalVoice(GameObject go)
        {
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = _spatialBlend;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = _minDistance;
            source.maxDistance = _maxDistance;
            source.dopplerLevel = 0f;   // a top-down pitch has no fly-bys to sell
            return source;
        }

        /// <summary>
        /// Broadcast cues get their own small pool on ONE GameObject, because an
        /// AudioReverbFilter - like the low-pass filters above - applies to every
        /// source on the object it sits on. That is exactly what is wanted here
        /// (one shared stadium tail across whistle, horn and boo) and exactly what
        /// must not happen to the positional impacts, which would smear into mush.
        ///
        /// Still a pool of three rather than one source with PlayOneShot: defect
        /// 2 in the header - pitch belongs to the SOURCE - applies just as much to
        /// a boo overlapping a whistle as it did to two ball impacts.
        /// </summary>
        void BuildBroadcastBus()
        {
            var go = new GameObject("Audio_Broadcast");
            go.transform.SetParent(transform, false);

            _broadcast = new AudioSource[3];
            for (int i = 0; i < _broadcast.Length; i++)
            {
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                _broadcast[i] = source;
            }

            if (_reverbMix <= 0f) return;
            var reverb = go.AddComponent<AudioReverbFilter>();
            // User, not Off. Off zeroes the filter; User is the preset that means
            // "the values I set below are the ones to use".
            reverb.reverbPreset = AudioReverbPreset.User;
            reverb.dryLevel = 0f;
            reverb.room = Mathf.Lerp(-2000f, 0f, _reverbMix);
            reverb.roomHF = -300f;                          // a stadium is not bright
            reverb.decayTime = _reverbDecay;
            reverb.decayHFRatio = 0.7f;
            reverb.reflectionsLevel = -900f;
            reverb.reverbLevel = Mathf.Lerp(-2000f, 200f, _reverbMix);
            reverb.reverbDelay = 0.03f;
            reverb.diffusion = 100f;
            reverb.density = 100f;
        }

        /// <summary>
        /// The two continuous physics voices. Each needs its own GameObject: the
        /// rolling loop carries a low-pass whose cutoff is the whole effect, and
        /// the breath must be able to move to whichever player is most tired
        /// without dragging the ball's filter along with it.
        /// </summary>
        void BuildPhysicsVoices()
        {
            var rollGo = new GameObject("Audio_BallRoll");
            rollGo.transform.SetParent(transform, false);
            _roll = NewPositionalVoice(rollGo);
            _roll.loop = true;
            _rollFilter = rollGo.AddComponent<AudioLowPassFilter>();
            _rollFilter.cutoffFrequency = 400f;

            var breathGo = new GameObject("Audio_Breath");
            breathGo.transform.SetParent(transform, false);
            _breath = NewPositionalVoice(breathGo);
            _breath.loop = true;
            // Tighter than the impacts: breathing is intimate, so it should fall
            // away fast rather than being audible from the far touchline.
            _breath.maxDistance = _maxDistance * 0.4f;
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

        /// <summary>
        /// Non-positional cue on the broadcast bus (whistle, horn, boo, UI), with
        /// the shared stadium reverb tail. Deliberately a different pool from the
        /// positional impacts, not just a positional voice with spatialBlend
        /// forced to 0 - which is what this used to be, and which meant the
        /// reverb could never be applied to one without applying it to all.
        /// </summary>
        void Play2D(AudioClip clip, float volume, float pitch = 1f)
        {
            var source = NextBroadcast();
            if (clip == null || source == null || Muted) return;
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

        /// <summary>Same round-robin discipline over the reverberant broadcast pool.</summary>
        AudioSource NextBroadcast()
        {
            if (_broadcast == null) return null;
            for (int i = 0; i < _broadcast.Length; i++)
            {
                var candidate = _broadcast[(_nextBroadcast + i) % _broadcast.Length];
                if (candidate != null && !candidate.isPlaying)
                {
                    _nextBroadcast = (_nextBroadcast + i + 1) % _broadcast.Length;
                    return candidate;
                }
            }
            var source = _broadcast[_nextBroadcast];
            _nextBroadcast = (_nextBroadcast + 1) % _broadcast.Length;
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

            if (!collision.collider.CompareTag("Wall"))
            {
                PlayAt(kick, at, volume, pitch);
                return;
            }

            // A "post" is a wall contact near a goal mouth. There is no post
            // collider to hit - Agent_GoalFrame draws the mouth as a LineRenderer
            // with no physics at all - so position is the only thing that can tell
            // the two apart, and it is the thing a listener is using anyway.
            if (post != null && IsNearGoalMouth(at))
            {
                // Higher impacts ring the post higher, which is how a struck bar
                // actually behaves and reads as "off the woodwork".
                PlayAt(post, at, volume, Mathf.Lerp(0.9f, 1.15f, Mathf.Clamp01(impact / 18f)));
                Cheer(0.55f);   // the crowd reacts to woodwork whether or not it goes in
                return;
            }

            PlayAt(wall, at, volume * 0.8f, pitch);
        }

        /// <summary>
        /// True when a contact happened close enough to a goal centre to count as
        /// woodwork. The zone scales with the live goal width, so it stays correct
        /// through a curriculum step and through Agent_PitchSizing's squad-size
        /// rescale rather than being a magic distance in metres.
        /// </summary>
        bool IsNearGoalMouth(Vector3 worldPosition)
        {
            float zone = Mathf.Max(0.4f, _env.CurrentGoalWidth * _postZone);
            var point = (Vector2)worldPosition;

            if (_env.blueGoal != null &&
                Vector2.Distance(point, _env.blueGoal.position) <= zone) return true;
            if (_env.redGoal != null &&
                Vector2.Distance(point, _env.redGoal.position) <= zone) return true;
            return false;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner != null)
            {
                Play2D(goalHorn, 0.85f);
                if (crowdRoar != null) Play2D(crowdRoar, 0.9f);
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

            float pressure = Pressure();
            UpdateCrowd(dt, pressure);
            UpdateMusic(dt);
            UpdateFilters(dt);
            UpdateRoll(dt);
            UpdateBreath(dt);
        }

        /// <summary>
        /// How interesting the current moment is, 0..1: how close the ball is to
        /// either goal, plus whatever spike a goal or a near miss left behind.
        /// Computed once per frame and handed to every layer, so the bed, the
        /// swell and any future tier can never disagree about what is happening.
        /// </summary>
        float Pressure()
        {
            if (Muted || _env.Ball == null) return 0f;
            float distToGoal = Mathf.Min(
                Vector2.Distance(_env.Ball.position, _env.GetGoalPosition(Agent_Soccer.Team.Blue)),
                Vector2.Distance(_env.Ball.position, _env.GetGoalPosition(Agent_Soccer.Team.Red)));
            float tension = Mathf.Clamp01(1f - distToGoal / 9f);
            return Mathf.Clamp01(tension + _goalSpike);
        }

        void UpdateCrowd(float dt, float pressure)
        {
            if (_crowd == null || _crowd.clip == null) return;

            float duck = 1f - _duck * _duckAmount;
            float bus = _crowdVolume * _masterVolume;

            float target = Muted
                ? 0f
                : (crowdBase + (crowdSwellMax - crowdBase) * pressure) * bus * duck;
            _crowd.volume = Mathf.MoveTowards(_crowd.volume, target, dt * 0.6f);

            // The swell rides ON TOP of the bed rather than replacing it, and only
            // starts once the bed is already up - a crowd does not switch from
            // murmuring to roaring, it thickens.
            if (_swell != null && _swell.clip != null)
            {
                float blend = Mathf.Clamp01(pressure / Mathf.Max(0.01f, _swellFullAt));
                float swellTarget = Muted ? 0f : blend * blend * _swellMaxLevel * bus * duck;
                _swell.volume = Mathf.MoveTowards(_swell.volume, swellTarget, dt * 0.7f);
            }

            // A boo is the crowd bed dropped a fourth; it resolves back to unity
            // pitch so a stalled passage does not leave the stadium detuned.
            float wantPitch = _booTime > 0f ? 0.78f : 1f;
            _crowd.pitch = Mathf.MoveTowards(_crowd.pitch, wantPitch, dt * 1.5f);
        }

        /// <summary>
        /// The ball's own voice. Volume AND cutoff both track speed: level alone
        /// reads as a fade, but opening the filter as the ball accelerates is what
        /// makes it sound like it is travelling. Follows the ball so it pans and
        /// attenuates with distance like any other positional source.
        /// </summary>
        void UpdateRoll(float dt)
        {
            if (_roll == null || _roll.clip == null || _env.Ball == null) return;

            _roll.transform.position = _env.Ball.transform.position;

            float speed01 = Mathf.Clamp01(
                _env.Ball.linearVelocity.magnitude / Mathf.Max(0.1f, _rollFullSpeed));
            float target = Muted ? 0f : speed01 * _rollVolume * _sfxVolume * _masterVolume;
            _roll.volume = Mathf.MoveTowards(_roll.volume, target, dt * 2.5f);

            if (_rollFilter != null)
            {
                float cutoff = Mathf.Lerp(220f, 2400f, speed01);
                _rollFilter.cutoffFrequency =
                    Mathf.MoveTowards(_rollFilter.cutoffFrequency, cutoff, 6000f * dt);
            }
        }

        /// <summary>
        /// One breathing voice, moved to whichever player is currently most tired,
        /// rather than one loop per player. At 1v1 that is a distinction without a
        /// difference; at 2v2 it is the difference between a stadium and four
        /// people panting in unison.
        /// </summary>
        void UpdateBreath(float dt)
        {
            if (_breath == null || _breath.clip == null || _players == null) return;

            Agent_Soccer tired = null;
            float lowest = 1f;
            for (int i = 0; i < _players.Length; i++)
            {
                var player = _players[i];
                if (player == null || player.Stamina == null) continue;
                float ratio = player.Stamina.Ratio;
                if (ratio >= lowest) continue;
                lowest = ratio;
                tired = player;
            }

            float target = 0f;
            if (!Muted && tired != null && lowest < _breathBelow)
            {
                _breath.transform.position = tired.transform.position;
                // Loudest when empty, silent at the threshold.
                float exhaustion = 1f - lowest / Mathf.Max(0.01f, _breathBelow);
                target = exhaustion * _breathVolume * _sfxVolume * _masterVolume;
                // Breathing speeds up as it gets harder, which is a cue nothing
                // else in the mix carries.
                _breath.pitch = Mathf.Lerp(0.9f, 1.25f, exhaustion);
            }
            _breath.volume = Mathf.MoveTowards(_breath.volume, target, dt * 1.2f);
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
            if (_swellFilter != null)
                _swellFilter.cutoffFrequency =
                    Mathf.MoveTowards(_swellFilter.cutoffFrequency, target, speed);
        }
    }
}
