using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Match audio: velocity-scaled ball impacts, kickoff whistle, goal horn,
    /// and a reactive crowd bed that swells as the ball nears either goal and
    /// erupts on goals. Clips are plain fields - generated placeholder WAVs are
    /// wired by default; drop in Asset Store SFX to upgrade the sound instantly.
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

        [Header("Crowd dynamics")]
        [Range(0f, 1f)] public float crowdBase = 0.16f;
        [Range(0f, 1f)] public float crowdSwellMax = 0.45f;

        Agent_EnvController _env;
        AudioSource _oneShot, _crowd, _music;
        float _goalSpike;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _env.EpisodeEnded += OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit += OnBallHit;

            _oneShot = NewSource(false);
            _crowd = NewSource(true);
            _music = NewSource(true);

            if (crowdLoop != null) { _crowd.clip = crowdLoop; _crowd.volume = 0f; _crowd.Play(); }
            if (music != null) { _music.clip = music; _music.volume = 0.25f; _music.Play(); }
            Play(whistle, 0.5f);
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit -= OnBallHit;
        }

        AudioSource NewSource(bool loop)
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.loop = loop;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            return source;
        }

        void Play(AudioClip clip, float volume, float pitch = 1f)
        {
            if (clip == null || Muted || _oneShot == null) return;
            _oneShot.pitch = pitch;
            _oneShot.PlayOneShot(clip, volume);
        }

        void OnBallHit(Collision2D collision)
        {
            float impact = collision.relativeVelocity.magnitude;
            if (impact < 1.5f) return;
            float volume = Mathf.Clamp01(impact / 14f);
            float pitch = Random.Range(0.92f, 1.08f);
            if (collision.collider.CompareTag("Wall")) Play(wall, volume * 0.8f, pitch);
            else Play(kick, volume, pitch);
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner != null)
            {
                Play(goalHorn, 0.85f);
                _goalSpike = 1f;
            }
            // The whistle marks the kickoff, not the goal - never stacked on the horn.
            KickoffWhistleAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        async UniTaskVoid KickoffWhistleAsync(CancellationToken token)
        {
            await UniTask.Delay(System.TimeSpan.FromSeconds(1.5), DelayType.UnscaledDeltaTime,
                cancellationToken: token);
            Play(whistle, 0.45f);
        }

        void Update()
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
            }
            _goalSpike = Mathf.Max(0f, _goalSpike - Time.unscaledDeltaTime * 0.7f);
            _crowd.volume = Mathf.MoveTowards(_crowd.volume, target, Time.unscaledDeltaTime * 0.6f);
            if (_music != null) _music.mute = Muted;
        }
    }
}
