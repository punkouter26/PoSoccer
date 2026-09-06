using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Generates the three adaptive music stems into Assets/Resources/Audio.
    ///
    /// WHY A GENERATOR AND NOT THREE FILES. The project's existing WAVs were
    /// synthesised in a session and committed with no way to reproduce them, so
    /// "make the swell a little brighter" meant "start again from nothing". These
    /// three have to line up SAMPLE FOR SAMPLE - they are crossfaded live, and a
    /// stem a few milliseconds adrift phases against its neighbours - which is
    /// exactly the property a re-run of one file by hand would break. Keeping the
    /// generator in the repo makes the stems data, not artefacts.
    ///
    /// THE THREE LAYERS ARE ONE PIECE OF MUSIC, NOT THREE.
    ///   bed     - a slow pad and a soft pulse. What a nothing-happening moment
    ///             sounds like; it never stops.
    ///   tension - a rising arpeggio on the same harmony, entering as the ball
    ///             works into a final third.
    ///   drive   - kick, snare-ish noise and a bass line, for a genuine chance.
    ///
    /// They share one tempo grid (112 bpm), one key (A minor) and one length
    /// (24 s = 44.8 bars, chosen to match the music.wav already in the project so
    /// the old file and the new bed can be swapped without re-timing anything).
    /// A stem crossfaded in at any moment therefore lands in time and in key,
    /// which is the whole reason to build them together.
    ///
    /// Run: PoSoccer -> Generate Music Stems.
    /// </summary>
    public static class Editor_MakeAudioStems
    {
        const int RATE = 44100;
        const int CHANNELS = 2;
        const float SECONDS = 24f;
        const float BPM = 112f;
        const string FOLDER = "Assets/Resources/Audio";

        // A minor: the pad holds Am - F - C - G, one chord every two bars.
        static readonly float[] Root = { 220.00f, 174.61f, 261.63f, 196.00f };            // A3 F3 C4 G3
        static readonly float[] Third = { 261.63f, 220.00f, 329.63f, 246.94f };           // C4 A3 E4 B3
        static readonly float[] Fifth = { 329.63f, 261.63f, 392.00f, 293.66f };           // E4 C4 G4 D4

        [MenuItem("PoSoccer/Generate Music Stems")]
        public static void Generate()
        {
            Directory.CreateDirectory(FOLDER);

            int frames = Mathf.RoundToInt(RATE * SECONDS);
            Write("music_bed", RenderBed(frames));
            Write("music_tension", RenderTension(frames));
            Write("music_drive", RenderDrive(frames));

            AssetDatabase.Refresh();
            Debug.Log($"[PoSoccer] Wrote 3 music stems ({SECONDS:0}s, {RATE} Hz stereo) to {FOLDER}. " +
                      "Agent_Audio loads them by name; no scene wiring needed.");
        }

        // -- Layers ----------------------------------------------------------

        /// <summary>
        /// Pad plus a heartbeat pulse. Deliberately dull on its own: this plays
        /// under every quiet moment of every match, and anything with a hook in
        /// it becomes unbearable by the fifth listen.
        /// </summary>
        static float[] RenderBed(int frames)
        {
            var buffer = new float[frames * CHANNELS];
            float beat = 60f / BPM;

            for (int i = 0; i < frames; i++)
            {
                float t = i / (float)RATE;
                int chord = ChordAt(t);

                // Three detuned saw-ish voices. Two slightly offset copies per
                // note give the slow beating that makes a synth pad sound wide
                // without a chorus effect.
                float pad =
                    Voice(Root[chord], t, 0.30f) +
                    Voice(Third[chord], t, 0.22f) +
                    Voice(Fifth[chord], t, 0.18f);

                // Heartbeat on every other beat, low and short.
                float phase = Mathf.Repeat(t, beat * 2f);
                float pulse = Mathf.Exp(-phase * 9f) * Mathf.Sin(2f * Mathf.PI * 55f * t) * 0.35f;

                float sample = (pad * 0.5f + pulse) * Fade(t) * 0.5f;
                Stereo(buffer, i, sample, 0.12f, t);
            }
            return buffer;
        }

        /// <summary>
        /// A sixteenth-note arpeggio through the same chord, filtered dark. It
        /// carries no new harmony - it is the bed, moving - so it can enter at
        /// any moment without a key change.
        /// </summary>
        static float[] RenderTension(int frames)
        {
            var buffer = new float[frames * CHANNELS];
            float step = 60f / BPM / 4f;                 // sixteenths

            for (int i = 0; i < frames; i++)
            {
                float t = i / (float)RATE;
                int chord = ChordAt(t);

                int index = Mathf.FloorToInt(t / step);
                float within = t - index * step;

                // Up-down through root/third/fifth, an octave up.
                int tone = index % 6;
                float freq = tone switch
                {
                    0 => Root[chord] * 2f,
                    1 => Third[chord] * 2f,
                    2 => Fifth[chord] * 2f,
                    3 => Root[chord] * 4f,
                    4 => Fifth[chord] * 2f,
                    _ => Third[chord] * 2f,
                };

                // Plucked envelope: fast attack, short decay, so the line reads
                // as movement rather than as a second pad.
                float envelope = Mathf.Exp(-within * 22f);
                float note = Mathf.Sin(2f * Mathf.PI * freq * t) * envelope;
                // A touch of the octave below fills it out without muddying.
                note += Mathf.Sin(2f * Mathf.PI * freq * 0.5f * t) * envelope * 0.3f;

                float sample = note * 0.30f * Fade(t);
                Stereo(buffer, i, sample, 0.22f, t);
            }
            return buffer;
        }

        /// <summary>
        /// Kick, noise backbeat and a walking bass. This is the only layer with a
        /// groove, which is why it is reserved for a real chance - if it played
        /// through a goalless midfield stalemate it would be lying about the game.
        /// </summary>
        static float[] RenderDrive(int frames)
        {
            var buffer = new float[frames * CHANNELS];
            float beat = 60f / BPM;
            uint noise = 0x13579BDFu;

            for (int i = 0; i < frames; i++)
            {
                float t = i / (float)RATE;
                int chord = ChordAt(t);

                // Kick on every beat: pitch sweep from 110 Hz down to 45 Hz.
                float kickPhase = Mathf.Repeat(t, beat);
                float kickEnv = Mathf.Exp(-kickPhase * 16f);
                float kickFreq = Mathf.Lerp(110f, 45f, Mathf.Clamp01(kickPhase * 12f));
                float kick = Mathf.Sin(2f * Mathf.PI * kickFreq * t) * kickEnv * 0.7f;

                // Backbeat on 2 and 4, made of filtered noise.
                float barPhase = Mathf.Repeat(t, beat * 2f);
                float snareEnv = Mathf.Exp(-Mathf.Repeat(barPhase - beat, beat * 2f) * 30f);
                noise = noise * 1664525u + 1013904223u;
                float white = ((noise >> 9) & 0x7FFFFF) / (float)0x400000 - 1f;
                float snare = white * snareEnv * 0.22f;

                // Eighth-note bass on the chord root, an octave below the pad.
                float bassStep = beat * 0.5f;
                float bassWithin = Mathf.Repeat(t, bassStep);
                float bassEnv = Mathf.Exp(-bassWithin * 7f);
                float bass = Mathf.Sin(2f * Mathf.PI * Root[chord] * 0.5f * t) * bassEnv * 0.45f;

                float sample = (kick + snare + bass) * 0.55f * Fade(t);
                Stereo(buffer, i, sample, 0.08f, t);
            }
            return buffer;
        }

        // -- Shared helpers --------------------------------------------------

        /// <summary>Two-bar chords, wrapping over the loop.</summary>
        static int ChordAt(float t)
        {
            float barSeconds = 60f / BPM * 4f;
            return Mathf.FloorToInt(t / (barSeconds * 2f)) % Root.Length;
        }

        /// <summary>
        /// One pad voice: a soft saw approximated by three harmonics, plus a
        /// detuned twin 0.6 Hz away so the pair beats slowly.
        /// </summary>
        static float Voice(float freq, float t, float gain)
        {
            float a = Mathf.Sin(2f * Mathf.PI * freq * t)
                    + Mathf.Sin(2f * Mathf.PI * freq * 2f * t) * 0.32f
                    + Mathf.Sin(2f * Mathf.PI * freq * 3f * t) * 0.14f;
            float b = Mathf.Sin(2f * Mathf.PI * (freq + 0.6f) * t);
            return (a * 0.7f + b * 0.5f) * gain;
        }

        /// <summary>
        /// A short fade at both ends so the loop point does not click.
        ///
        /// This is why the stems are generated rather than trimmed by ear: all
        /// three have to reach silence at the SAME sample, or a crossfade at the
        /// wrap point exposes one layer's click through the others.
        /// </summary>
        static float Fade(float t)
        {
            const float EDGE = 0.06f;
            float head = Mathf.Clamp01(t / EDGE);
            float tail = Mathf.Clamp01((SECONDS - t) / EDGE);
            return head * tail;
        }

        /// <summary>
        /// Writes one mono sample into a stereo frame with a slow auto-pan, so
        /// three layers stacked in the mix do not all sit in the same spot.
        /// </summary>
        static void Stereo(float[] buffer, int frame, float sample, float width, float t)
        {
            float pan = Mathf.Sin(2f * Mathf.PI * 0.07f * t) * width;
            buffer[frame * 2] = sample * (1f - pan);
            buffer[frame * 2 + 1] = sample * (1f + pan);
        }

        // -- WAV -------------------------------------------------------------

        static void Write(string name, float[] samples)
        {
            string path = Path.Combine(FOLDER, name + ".wav");

            // Normalise to a fixed headroom rather than to peak: the three stems
            // are summed at playback, so normalising each to full scale would
            // clip the moment two of them are up together.
            float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float magnitude = Mathf.Abs(samples[i]);
                if (magnitude > peak) peak = magnitude;
            }
            float scale = peak > 0.0001f ? 0.72f / peak : 1f;

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            int dataBytes = samples.Length * 2;
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataBytes);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);                                   // PCM chunk size
            writer.Write((short)1);                             // PCM
            writer.Write((short)CHANNELS);
            writer.Write(RATE);
            writer.Write(RATE * CHANNELS * 2);                  // byte rate
            writer.Write((short)(CHANNELS * 2));                // block align
            writer.Write((short)16);                            // bits
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(dataBytes);

            for (int i = 0; i < samples.Length; i++)
            {
                float value = Mathf.Clamp(samples[i] * scale, -1f, 1f);
                writer.Write((short)Math.Round(value * short.MaxValue));
            }
        }
    }
}
