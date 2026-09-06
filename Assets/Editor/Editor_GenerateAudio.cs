using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Synthesises the audio layers Agent_Audio's adaptive mix needs and the
    /// project does not have: a music bed, the crowd swell and roar tiers, a boo,
    /// a rolling-ball loop, a breathing loop and a post ring.
    ///
    /// WHAT THIS IS AND IS NOT. It is not a replacement for licensed recordings -
    /// synthesis will not beat a real crowd, and Agent_Audio's fields take a Store
    /// pack the moment one is dropped in. It exists because the alternative was
    /// leaving those fields null, which meant the swell/roar tiering, the ducking
    /// and the physics-driven parameters could not be heard, tested or tuned at
    /// all. A synthesised layer you can hear beats a licensed layer you have not
    /// bought yet.
    ///
    /// IT DOES NOT TOUCH THE SIX EXISTING CLIPS. kick, wall, whistle, horn,
    /// crowd_loop and click are real recordings in Assets/Audio and are left
    /// exactly alone; everything written here is new and lands in
    /// Assets/Resources/Audio so Agent_Audio can load it WITHOUT a scene edit.
    /// (Scene authoring is MCP-only per UNITY_RULES, and a new serialized
    /// AudioClip field would need one scene edit per scene to fill in. Resources
    /// is the same escape hatch Agent_Surfaces uses for its normal maps.)
    ///
    /// Output matches the existing clips: 44100 Hz, 16-bit PCM.
    ///
    /// Every loop is genuinely seamless, by two different mechanisms depending on
    /// the material. Tonal content (the music bed) snaps every partial to an
    /// integer number of cycles across the loop, so the waveform meets itself
    /// exactly. Noise content cannot do that, so it is equal-power crossfaded head
    /// into tail. A loop that clicks once a bar is worse than no loop.
    /// </summary>
    public static class Editor_GenerateAudio
    {
        const int SAMPLE_RATE = 44100;
        const string OUTPUT_DIR = "Assets/Resources/Audio";

        [MenuItem("PoSoccer/Generate Missing Audio Clips")]
        public static void Generate()
        {
            Directory.CreateDirectory(OUTPUT_DIR);

            Write("music", MusicBed(24f), loop: true, stereo: true);
            Write("crowd_swell", CrowdSwell(8f), loop: true);
            Write("crowd_roar", CrowdRoar(4.5f), loop: false);
            Write("crowd_boo", CrowdBoo(3f), loop: false);
            Write("ball_roll", BallRoll(2f), loop: true);
            Write("breath", Breath(3.4f), loop: true);
            Write("post", PostRing(1.6f), loop: false);

            AssetDatabase.Refresh();
            Debug.Log($"Editor_GenerateAudio: wrote 7 clips to {OUTPUT_DIR}. " +
                      "The six recordings in Assets/Audio were not touched.");
        }

        // -- Voices ----------------------------------------------------------

        /// <summary>
        /// Slow ambient pad on a suspended chord: no third, so it sits under
        /// commentary without implying a mood the match has not earned yet.
        /// Every partial is snapped to a whole number of cycles across the loop,
        /// which is what makes the seam inaudible without a crossfade.
        /// </summary>
        static float[] MusicBed(float seconds)
        {
            int length = (int)(seconds * SAMPLE_RATE);
            var buffer = new float[length];

            // A2 - E3 - B3 - E4 - F#4: open fifths stacked, plus the ninth on top.
            float[] chord = { 110f, 164.81f, 246.94f, 329.63f, 369.99f };
            float[] gains = { 0.30f, 0.22f, 0.16f, 0.11f, 0.07f };

            var rng = new Lcg(0x50536F63);

            for (int voice = 0; voice < chord.Length; voice++)
            {
                float frequency = SnapToLoop(chord[voice], seconds);
                // A slow amplitude drift per voice, also loop-locked, so the pad
                // breathes instead of sitting as a static drone.
                float driftHz = SnapToLoop(0.05f + voice * 0.017f, seconds);
                float phase = (float)rng.NextDouble() * Mathf.PI * 2f;

                for (int i = 0; i < length; i++)
                {
                    float t = i / (float)SAMPLE_RATE;
                    float drift = 0.65f + 0.35f * Mathf.Sin(2f * Mathf.PI * driftHz * t + phase);
                    buffer[i] += Mathf.Sin(2f * Mathf.PI * frequency * t + phase) * gains[voice] * drift;
                    // A quiet octave adds air without another audible voice.
                    buffer[i] += Mathf.Sin(2f * Mathf.PI * frequency * 2f * t + phase)
                                 * gains[voice] * 0.18f * drift;
                }
            }

            SoftClip(buffer, 0.85f);
            Normalize(buffer, 0.55f);
            return buffer;
        }

        /// <summary>
        /// The middle crowd tier: the bed with more upper-mid energy and a faster
        /// wobble, so layering it over crowd_loop reads as the same crowd getting
        /// interested rather than as a second crowd arriving.
        /// </summary>
        static float[] CrowdSwell(float seconds)
        {
            var buffer = Noise(seconds, 0x1234ABCD);
            OnePoleLowPass(buffer, 2600f);
            OnePoleHighPass(buffer, 260f);
            Wobble(buffer, 0.9f, 0.35f, 0x77);
            CrossfadeLoop(ref buffer, 0.8f);
            Normalize(buffer, 0.7f);
            return buffer;
        }

        /// <summary>Goal roar: a fast rise, a held peak and a long decay tail.</summary>
        static float[] CrowdRoar(float seconds)
        {
            var buffer = Noise(seconds, 0x0FACADE);
            OnePoleLowPass(buffer, 4200f);
            OnePoleHighPass(buffer, 180f);
            Wobble(buffer, 2.1f, 0.28f, 0x91);

            int length = buffer.Length;
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)length;
                // 0.18 rise, 0.25 hold, decay for the rest.
                float envelope = t < 0.18f
                    ? Mathf.Pow(t / 0.18f, 0.6f)
                    : t < 0.43f
                        ? 1f
                        : Mathf.Pow(1f - (t - 0.43f) / 0.57f, 1.7f);
                buffer[i] *= envelope;
            }
            Normalize(buffer, 0.92f);
            return buffer;
        }

        /// <summary>
        /// Disapproval: the same noise dropped into the low mids with a slow
        /// vowel-ish sweep, which is most of what separates a boo from a rumble.
        /// </summary>
        static float[] CrowdBoo(float seconds)
        {
            var buffer = Noise(seconds, 0x0B00);
            OnePoleLowPass(buffer, 900f);
            OnePoleHighPass(buffer, 120f);
            Wobble(buffer, 5.5f, 0.45f, 0xB0);

            int length = buffer.Length;
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)length;
                float envelope = Mathf.Min(1f, t / 0.12f) * Mathf.Pow(1f - t, 0.9f);
                buffer[i] *= envelope;
            }
            Normalize(buffer, 0.8f);
            return buffer;
        }

        /// <summary>
        /// Rolling ball: a narrow low rumble. Deliberately dull and quiet - it is
        /// played as a continuous loop under everything else and filtered by
        /// speed at runtime, so anything characterful here becomes maddening
        /// within a minute.
        /// </summary>
        static float[] BallRoll(float seconds)
        {
            var buffer = Noise(seconds, 0x120112);
            OnePoleLowPass(buffer, 420f);
            OnePoleLowPass(buffer, 420f);      // 12 dB/oct: one pole leaves too much hiss
            OnePoleHighPass(buffer, 55f);
            CrossfadeLoop(ref buffer, 0.4f);
            Normalize(buffer, 0.5f);
            return buffer;
        }

        /// <summary>Two-phase breathing loop, driven at runtime by stamina.</summary>
        static float[] Breath(float seconds)
        {
            var buffer = Noise(seconds, 0x6EEA71);
            OnePoleLowPass(buffer, 1800f);
            OnePoleHighPass(buffer, 400f);

            int length = buffer.Length;
            // Two full breaths across the loop, asymmetric: the out-breath is
            // longer and quieter, which is what makes it read as a person.
            for (int i = 0; i < length; i++)
            {
                float cycle = (i / (float)length) * 2f % 1f;
                float envelope = cycle < 0.35f
                    ? Mathf.Sin(cycle / 0.35f * Mathf.PI) * 1f
                    : cycle < 0.85f
                        ? Mathf.Sin((cycle - 0.35f) / 0.5f * Mathf.PI) * 0.55f
                        : 0f;
                buffer[i] *= envelope;
            }
            CrossfadeLoop(ref buffer, 0.15f);
            Normalize(buffer, 0.6f);
            return buffer;
        }

        /// <summary>
        /// Goalpost ring. Inharmonic partials - a struck bar is not a struck
        /// string - with the higher ones decaying fastest, which is what makes
        /// metal sound like metal rather than like a bell.
        /// </summary>
        static float[] PostRing(float seconds)
        {
            int length = (int)(seconds * SAMPLE_RATE);
            var buffer = new float[length];

            float[] partials = { 1f, 2.76f, 5.40f, 8.93f, 13.34f };
            float[] gains = { 1f, 0.55f, 0.32f, 0.18f, 0.09f };
            const float ROOT = 430f;

            for (int p = 0; p < partials.Length; p++)
            {
                float frequency = ROOT * partials[p];
                float decay = 6f + p * 5f;      // higher partials die first
                for (int i = 0; i < length; i++)
                {
                    float t = i / (float)SAMPLE_RATE;
                    buffer[i] += Mathf.Sin(2f * Mathf.PI * frequency * t)
                                 * gains[p] * Mathf.Exp(-decay * t);
                }
            }

            // A couple of milliseconds of noise on the attack: the contact itself.
            var rng = new Lcg(0x9051);
            int attack = SAMPLE_RATE / 400;
            for (int i = 0; i < attack && i < length; i++)
            {
                float k = 1f - i / (float)attack;
                buffer[i] += (float)(rng.NextDouble() * 2.0 - 1.0) * k * 0.6f;
            }

            Normalize(buffer, 0.9f);
            return buffer;
        }

        // -- Building blocks ---------------------------------------------------

        static float[] Noise(float seconds, int seed)
        {
            int length = (int)(seconds * SAMPLE_RATE);
            var buffer = new float[length];
            var rng = new Lcg(seed);
            for (int i = 0; i < length; i++) buffer[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            return buffer;
        }

        static void OnePoleLowPass(float[] buffer, float cutoffHz)
        {
            float dt = 1f / SAMPLE_RATE;
            float rc = 1f / (2f * Mathf.PI * cutoffHz);
            float alpha = dt / (rc + dt);
            float previous = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                previous += alpha * (buffer[i] - previous);
                buffer[i] = previous;
            }
        }

        static void OnePoleHighPass(float[] buffer, float cutoffHz)
        {
            float dt = 1f / SAMPLE_RATE;
            float rc = 1f / (2f * Mathf.PI * cutoffHz);
            float alpha = rc / (rc + dt);
            float previousIn = buffer.Length > 0 ? buffer[0] : 0f;
            float previousOut = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float input = buffer[i];
                previousOut = alpha * (previousOut + input - previousIn);
                previousIn = input;
                buffer[i] = previousOut;
            }
        }

        /// <summary>Slow random amplitude modulation - the "many voices" cue.</summary>
        static void Wobble(float[] buffer, float rateHz, float depth, int seed)
        {
            var rng = new Lcg(seed);
            float phaseA = (float)rng.NextDouble() * Mathf.PI * 2f;
            float phaseB = (float)rng.NextDouble() * Mathf.PI * 2f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float lfo = 0.6f * Mathf.Sin(2f * Mathf.PI * rateHz * t + phaseA)
                          + 0.4f * Mathf.Sin(2f * Mathf.PI * rateHz * 0.37f * t + phaseB);
                buffer[i] *= 1f - depth + depth * (0.5f + 0.5f * lfo);
            }
        }

        /// <summary>
        /// Nearest frequency completing a whole number of cycles in the loop, so
        /// the waveform value and slope match at the seam.
        /// </summary>
        static float SnapToLoop(float frequency, float seconds)
        {
            float cycles = Mathf.Max(1f, Mathf.Round(frequency * seconds));
            return cycles / seconds;
        }

        /// <summary>
        /// Equal-power crossfade of the tail back over the head, then truncate.
        /// Noise cannot be loop-locked the way a sine can, so this is how a noise
        /// bed loops without a click.
        /// </summary>
        static void CrossfadeLoop(ref float[] buffer, float fadeSeconds)
        {
            int fade = Mathf.Min((int)(fadeSeconds * SAMPLE_RATE), buffer.Length / 3);
            if (fade <= 0) return;

            int tailStart = buffer.Length - fade;
            for (int i = 0; i < fade; i++)
            {
                float t = i / (float)fade;
                float head = Mathf.Sqrt(t);
                float tail = Mathf.Sqrt(1f - t);
                buffer[i] = buffer[i] * head + buffer[tailStart + i] * tail;
            }
            Array.Resize(ref buffer, tailStart);
        }

        static void SoftClip(float[] buffer, float drive)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Math.Tanh(buffer[i] * drive);
        }

        static void Normalize(float[] buffer, float peak)
        {
            float maximum = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float magnitude = Mathf.Abs(buffer[i]);
                if (magnitude > maximum) maximum = magnitude;
            }
            if (maximum < 1e-6f) return;

            float gain = peak / maximum;
            for (int i = 0; i < buffer.Length; i++) buffer[i] *= gain;
        }

        /// <summary>
        /// Deterministic PRNG. UnityEngine.Random would make every regeneration
        /// produce different clips, so a diff of Assets/Resources/Audio would show
        /// churn on every run and nobody could tell a real change from noise.
        /// </summary>
        struct Lcg
        {
            ulong _state;
            public Lcg(int seed) { _state = (ulong)(seed == 0 ? 1 : seed); }

            public double NextDouble()
            {
                _state = _state * 6364136223846793005UL + 1442695040888963407UL;
                return ((_state >> 11) & 0x1FFFFFFFFFFFFFUL) / (double)0x20000000000000UL;
            }
        }

        // -- Output ------------------------------------------------------------

        static void Write(string name, float[] mono, bool loop, bool stereo = false)
        {
            string path = $"{OUTPUT_DIR}/{name}.wav";
            using (var stream = new FileStream(path, FileMode.Create))
            using (var writer = new BinaryWriter(stream))
            {
                int channels = stereo ? 2 : 1;
                int frames = mono.Length;
                int dataBytes = frames * channels * 2;

                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);                        // PCM
                writer.Write((short)channels);
                writer.Write(SAMPLE_RATE);
                writer.Write(SAMPLE_RATE * channels * 2);      // byte rate
                writer.Write((short)(channels * 2));           // block align
                writer.Write((short)16);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataBytes);

                for (int i = 0; i < frames; i++)
                {
                    short sample = (short)(Mathf.Clamp(mono[i], -1f, 1f) * 32767f);
                    writer.Write(sample);
                    if (!stereo) continue;
                    // Widen to stereo by delaying the right channel a few samples:
                    // a Haas shift, so the pad has width without decorrelating into
                    // a phasey mess when a phone sums it back to mono.
                    int delayed = Mathf.Max(0, i - 480);
                    writer.Write((short)(Mathf.Clamp(mono[delayed], -1f, 1f) * 32767f));
                }
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null) return;

            var settings = importer.defaultSampleSettings;
            // Beds and loops stream or decompress on load; short one-shots stay
            // decompressed so a goal does not cost a decode on the frame it lands.
            settings.loadType = loop
                ? AudioClipLoadType.CompressedInMemory
                : AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = 0.7f;
            settings.preloadAudioData = !loop;
            importer.defaultSampleSettings = settings;

            // 3D on everything: Agent_Audio decides per cue whether to play a clip
            // positionally or as a broadcast, and a clip imported 2D can never be
            // panned even when it should be.
            importer.threeD = true;
            importer.SaveAndReimport();
        }
    }
}
