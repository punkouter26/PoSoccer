using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Vibration, scaled by the physics that caused it.
    ///
    /// This is a portrait mobile game that shipped with no haptic call anywhere,
    /// which means the one output channel a phone has that a monitor does not was
    /// simply unused. The events are already there and already carry magnitudes -
    /// Agent_Contact.PlayerContact reports a normal impulse in N*s, and
    /// Agent_EnvController.EpisodeEnded says who scored - so a body check can
    /// feel different from a goal without anybody inventing a number.
    ///
    /// WHY NOT Handheld.Vibrate(). It takes no arguments: one fixed ~500 ms buzz,
    /// the same for a graze and for a goal, and on most devices it is long enough
    /// to be unpleasant. Android has had amplitude-controlled haptics since API 26
    /// (VibrationEffect.createOneShot), and this project's minSdk is exactly 26 -
    /// so the good path is available on every device that can install the game.
    /// The JNI handles are resolved ONCE and cached; doing that per buzz would
    /// allocate and cross the JNI boundary four extra times on every contact.
    ///
    /// Everywhere that is not an Android player, this compiles to a no-op that
    /// still counts calls, so the PlayMode test can prove the wiring without a
    /// phone in the loop.
    ///
    /// It respects the sound mute. A player who has silenced the game in a public
    /// place has told you what they want, and a buzzing phone is louder than a
    /// quiet speaker.
    /// </summary>
    [DefaultExecutionOrder(70)]
    public sealed class Agent_Haptics : MonoBehaviour
    {
        const string ENABLED_KEY = "posoccer.haptics";

        [Tooltip("Contact impulse (N*s) below which no buzz is played.")]
        [SerializeField] private float _contactThreshold = 14f;
        [Tooltip("Contact impulse at which the buzz is at full amplitude.")]
        [SerializeField] private float _contactSaturation = 48f;
        [Tooltip("Milliseconds for a contact buzz at full strength.")]
        [SerializeField] private int _contactMs = 28;
        [Tooltip("Milliseconds for the goal buzz.")]
        [SerializeField] private int _goalMs = 90;
        [Tooltip("Shortest gap between two buzzes. A scrum fires contacts every few frames.")]
        [SerializeField] private float _minInterval = 0.12f;

        Agent_EnvController _env;
        Agent_Contact[] _contacts;
        float _nextAllowed;

        /// <summary>
        /// Buzzes requested since load, whatever the platform did with them.
        /// Exists so the wiring is testable off-device - a haptic that fires on
        /// a phone and nowhere else is a feature nobody can regression-test.
        /// </summary>
        public static int RequestCount { get; private set; }

        /// <summary>Player preference, persisted. Defaults on.</summary>
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(ENABLED_KEY, 1) != 0;
            set
            {
                PlayerPrefs.SetInt(ENABLED_KEY, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        void Start()
        {
            var hud = FindFirstObjectByType<Agent_HUD>();
            if (!Agent_Presentation.IsMatchScene(hud))
            {
                enabled = false;
                return;
            }

            _env = GetComponent<Agent_EnvController>();
            if (_env == null) _env = FindFirstObjectByType<Agent_EnvController>();
            if (_env == null)
            {
                enabled = false;
                return;
            }

            _env.EpisodeEnded += OnEpisodeEnded;

            _contacts = _env.GetComponentsInChildren<Agent_Contact>();
            for (int i = 0; i < _contacts.Length; i++)
            {
                if (_contacts[i] != null) _contacts[i].PlayerContact += OnPlayerContact;
            }
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            if (_contacts == null) return;
            for (int i = 0; i < _contacts.Length; i++)
            {
                if (_contacts[i] != null) _contacts[i].PlayerContact -= OnPlayerContact;
            }
        }

        void OnPlayerContact(Vector2 point, Vector2 normal, float impulse)
        {
            if (impulse < _contactThreshold) return;
            float amount = Mathf.InverseLerp(_contactThreshold, _contactSaturation, impulse);
            Play(Mathf.RoundToInt(Mathf.Lerp(_contactMs * 0.5f, _contactMs, amount)),
                 Mathf.Lerp(0.35f, 1f, amount));
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null) return;
            // A goal always buzzes, whoever scored: the phone is reporting an
            // event, not taking a side, and a silent concede would read as a bug.
            Play(_goalMs, 1f);
        }

        /// <summary>
        /// Requests a buzz of <paramref name="milliseconds"/> at
        /// <paramref name="amplitude"/> (0..1). Rate-limited, mute-aware, and
        /// counted for the tests.
        /// </summary>
        public void Play(int milliseconds, float amplitude)
        {
            if (!Enabled || Agent_Audio.Muted) return;
            if (Time.unscaledTime < _nextAllowed) return;
            _nextAllowed = Time.unscaledTime + _minInterval;

            RequestCount++;
            Agent_HapticsDevice.Play(milliseconds, Mathf.Clamp01(amplitude));
        }
    }

    /// <summary>
    /// The platform half, kept separate so the component above stays testable and
    /// every #if in this feature lives in one file.
    /// </summary>
    static class Agent_HapticsDevice
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject _vibrator;
        static bool _resolved;
        static bool _supportsAmplitude;

        static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    // VIBRATOR_SERVICE is deprecated on API 31+ in favour of
                    // VibratorManager, but it still resolves, and the fallback
                    // path here is a plain vibrate() rather than nothing - so a
                    // future removal degrades instead of throwing.
                    _vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }

                if (_vibrator != null)
                {
                    _supportsAmplitude = _vibrator.Call<bool>("hasAmplitudeControl");
                }
            }
            catch (System.Exception e)
            {
                // A device that refuses to hand over a vibrator is not a reason
                // to stop the match; it is a reason to stop asking.
                Debug.LogWarning($"Agent_Haptics: no vibrator available ({e.Message}).");
                _vibrator = null;
            }
        }

        internal static void Play(int milliseconds, float amplitude)
        {
            Resolve();
            if (_vibrator == null) return;

            try
            {
                if (_supportsAmplitude)
                {
                    // 1..255; 0 means "off" to the platform, so the floor is 1.
                    int strength = Mathf.Clamp(Mathf.RoundToInt(amplitude * 255f), 1, 255);
                    using (var effectClass = new AndroidJavaClass("android.os.VibrationEffect"))
                    using (var effect = effectClass.CallStatic<AndroidJavaObject>(
                               "createOneShot", (long)milliseconds, strength))
                    {
                        _vibrator.Call("vibrate", effect);
                    }
                }
                else
                {
                    // No amplitude control: carry the strength in the DURATION
                    // instead, which is the only axis the device has left.
                    _vibrator.Call("vibrate", (long)Mathf.RoundToInt(milliseconds * amplitude));
                }
            }
            catch (System.Exception)
            {
                // Swallowed on purpose: a haptic failure mid-match must never
                // interrupt the match, and Resolve already logged the useful case.
            }
        }
#else
        internal static void Play(int milliseconds, float amplitude)
        {
            // Desktop and editor have no haptics. Deliberately not a warning:
            // this runs on every contact, and a log line per body check would
            // bury the console during a match.
        }
#endif
    }
}
