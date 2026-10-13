using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.InferenceEngine;
using UnityEngine;
#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
using UnityEngine.Networking;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PoSoccer
{
    /// <summary>
    /// Community brains: trained policies that did not ship in the build, loaded from a
    /// file at runtime and fielded like any roster player.
    ///
    /// THREE PARTS.
    ///  - EXPORT writes a roster brain as a .sentis file plus one index.json describing
    ///    the set. That folder is what gets uploaded to a Hugging Face model repo.
    ///  - INSTALLED brains live in persistentDataPath/brains as name.sentis + name.json.
    ///    Anything in that folder that passes the checks below joins the menu's roster,
    ///    however it got there - downloaded by the menu, or copied in by hand.
    ///  - The HUB CLIENT fetches a repo's index.json and downloads one brain into that
    ///    folder.
    ///
    /// WHY .sentis AND NOT .onnx. The ONNX importer is editor-only; a player cannot read
    /// an .onnx. Inference Engine's own serialized format is what
    /// ModelWriter.Save(stream, ModelAsset) emits, and its bytes are exactly the
    /// ModelAsset's description block followed by its weight chunks, each with a 4-byte
    /// length prefix. <see cref="LoadModel"/> splits a file back into those blocks.
    /// ML-Agents takes a ModelAsset and nothing else, and the asset's two fields are
    /// internal, hence the reflection - pinned by Agent_EditMode_BrainHub, which round
    /// trips the shipped STANDARD brain, so a package update that renames a field fails
    /// a test rather than a download.
    ///
    /// WHAT A DOWNLOADED FILE CAN AND CANNOT DO. A .sentis file is a graph description
    /// and weights. It is data: nothing in it is executed as code. A brain is refused
    /// unless the file parses, its inputs sum to this runtime's observation contract
    /// (Agent_Soccer.ModelFitsContract, 173 inputs today) and it has a continuous action
    /// output and no discrete one. A refused brain is never fielded. Downloads are https
    /// only and capped in size while they arrive. What a file CAN still be is a bad
    /// player; nothing here grades it, and the eval figure in an index is the uploader's
    /// claim, labelled as such on the card.
    ///
    /// THE ANDROID BUILD DOES NOT INCLUDE THE HUB CLIENT. docs/privacy-policy.md states
    /// that the app requests no INTERNET permission and makes no network connections,
    /// and Unity adds that permission the moment a script references a networking API.
    /// So the client compiles only off Android, or on Android when POSOCCER_BRAIN_HUB is
    /// defined - which is a decision to change the policy and the Play data-safety form
    /// first. Loading brains already in the folder works everywhere.
    ///
    /// Colour and name come from the index, which the uploader writes. No texture is
    /// ever assigned (UNITY_RULES: custom brains get user-supplied textures only).
    /// </summary>
    public static class Agent_BrainHub
    {
        /// <summary>Hugging Face model repo the menu reads when none is given.</summary>
        public const string DEFAULT_REPO = "punkouter26/posoccer-brains";
        public const string INDEX_FILE = "index.json";
        public const string MODEL_EXTENSION = ".sentis";
        /// <summary>Largest brain file accepted. The shipped brains are under 0.5 MB.</summary>
        public const int MAX_MODEL_BYTES = 8 * 1024 * 1024;
        public const int MAX_INDEX_BYTES = 256 * 1024;
        /// <summary>Longest roster name kept; the menu's pick buttons are sized for the built-in names.</summary>
        public const int MAX_NAME_LENGTH = 10;
        const int REQUEST_TIMEOUT_SECONDS = 20;

        /// <summary>One brain in an index, and the sidecar stored next to an installed file.</summary>
        [Serializable]
        public sealed class Entry
        {
            public string name;
            public string author;
            public string file;
            public string runId;
            public string trainedOn;
            public int steps;
            /// <summary>The uploader's stated 1v1 win rate against the benchmark bot. -1 = not stated.</summary>
            public float evalWinRate = -1f;
            public int evalEpisodes;
            /// <summary>"#RRGGBB". Empty = white.</summary>
            public string colour;
            public string notes;
        }

        [Serializable]
        public sealed class Index
        {
            public int formatVersion = 1;
            /// <summary>Observation contract the brains were exported under. Informational; each file is checked itself.</summary>
            public int modelInputs;
            public Entry[] brains = Array.Empty<Entry>();
        }

        /// <summary>True when this build can reach a hub. False on the shipped Android build.</summary>
        public static bool NetworkCompiled =>
#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
            true;
#else
            false;
#endif

        public static string BrainFolder => Path.Combine(Application.persistentDataPath, "brains");

        public static string IndexUrl(string repo) =>
            $"https://huggingface.co/{repo}/resolve/main/{INDEX_FILE}";

        public static string FileUrl(string repo, string file) =>
            $"https://huggingface.co/{repo}/resolve/main/{file}";

        // -- Validation --------------------------------------------------------

        /// <summary>
        /// A model file name this project will write to disk: letters, digits, dash and
        /// underscore, then .sentis. No separators and no dots in the stem, so a name
        /// from an index can never address anything outside the brains folder.
        /// </summary>
        public static bool IsSafeFileName(string file)
        {
            if (string.IsNullOrEmpty(file) || !file.EndsWith(MODEL_EXTENSION, StringComparison.Ordinal))
                return false;
            int stemLength = file.Length - MODEL_EXTENSION.Length;
            if (stemLength < 1 || stemLength > 64) return false;
            for (int charIndex = 0; charIndex < stemLength; charIndex++)
            {
                char c = file[charIndex];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>"owner/name", each part letters, digits, dash, underscore or dot.</summary>
        public static bool IsSafeRepo(string repo)
        {
            if (string.IsNullOrEmpty(repo) || repo.Length > 96) return false;
            int slashes = 0;
            for (int charIndex = 0; charIndex < repo.Length; charIndex++)
            {
                char c = repo[charIndex];
                if (c == '/') { slashes++; continue; }
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                if (!ok) return false;
            }
            return slashes == 1 && repo[0] != '/' && repo[repo.Length - 1] != '/'
                   && !repo.Contains("..");
        }

        /// <summary>
        /// The roster name for an entry: upper case, letters and digits only, at most
        /// <see cref="MAX_NAME_LENGTH"/>. Never empty - Reward_Settings.Initial and three
        /// UI call sites take its first character.
        /// </summary>
        public static string RosterName(string raw)
        {
            var builder = new System.Text.StringBuilder(MAX_NAME_LENGTH);
            if (raw != null)
            {
                for (int charIndex = 0; charIndex < raw.Length && builder.Length < MAX_NAME_LENGTH; charIndex++)
                {
                    char c = char.ToUpperInvariant(raw[charIndex]);
                    if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) builder.Append(c);
                }
            }
            return builder.Length > 0 ? builder.ToString() : "BRAIN";
        }

        /// <summary>Parses an index. Null on anything that is not one.</summary>
        public static Index ParseIndex(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var index = JsonUtility.FromJson<Index>(json);
                if (index == null || index.brains == null) return null;
                return index;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // -- Loading -----------------------------------------------------------

        static readonly FieldInfo DescriptionField = typeof(ModelAsset).GetField(
            "modelAssetData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo WeightsField = typeof(ModelAsset).GetField(
            "modelWeightsChunks", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// Builds a ModelAsset from the bytes of a .sentis file, or returns null with a
        /// reason. Checks structure only; <see cref="TryBuildProfile"/> checks the contract.
        /// </summary>
        public static ModelAsset LoadModel(byte[] bytes, string assetName, out string error)
        {
            error = null;
            if (DescriptionField == null || WeightsField == null)
            {
                error = "this Inference Engine version stores models differently; the loader needs updating";
                return null;
            }
            if (bytes == null || bytes.Length < 8)
            {
                error = "file is empty or truncated";
                return null;
            }
            if (bytes.Length > MAX_MODEL_BYTES)
            {
                error = $"file is larger than {MAX_MODEL_BYTES / (1024 * 1024)} MB";
                return null;
            }

            // Layout: [int32 n][n bytes of description] then, per weight chunk,
            // [int32 m][m bytes]. Each block is kept WITH its prefix, which is how
            // ModelAsset stores it.
            var blocks = new List<byte[]>(2);
            int offset = 0;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < sizeof(int))
                {
                    error = "file ends inside a block header";
                    return null;
                }
                int size = BitConverter.ToInt32(bytes, offset);
                bool isDescription = blocks.Count == 0;
                if (size < 0 || (isDescription && size == 0)
                    || size > bytes.Length - offset - sizeof(int))
                {
                    error = "file has a block whose length does not fit the file";
                    return null;
                }
                var block = new byte[size + sizeof(int)];
                Buffer.BlockCopy(bytes, offset, block, 0, block.Length);
                blocks.Add(block);
                offset += block.Length;
            }

            ModelAsset asset = null;
            try
            {
                asset = ScriptableObject.CreateInstance<ModelAsset>();
                asset.name = assetName;
                asset.hideFlags = HideFlags.DontSave;

                var description = ScriptableObject.CreateInstance(DescriptionField.FieldType);
                description.hideFlags = HideFlags.DontSave;
                DescriptionField.FieldType.GetField("value").SetValue(description, blocks[0]);
                DescriptionField.SetValue(asset, description);

                Type chunkType = WeightsField.FieldType.GetElementType();
                Array chunks = Array.CreateInstance(chunkType, blocks.Count - 1);
                for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
                {
                    var chunk = ScriptableObject.CreateInstance(chunkType);
                    chunk.hideFlags = HideFlags.DontSave;
                    chunkType.GetField("value").SetValue(chunk, blocks[chunkIndex + 1]);
                    chunks.SetValue(chunk, chunkIndex);
                }
                WeightsField.SetValue(asset, chunks);

                // Parse it now. A file that is the right shape of bytes and the wrong
                // content fails here, not when a match tries to build a policy from it.
                var model = ModelLoader.Load(asset);
                if (!HasContinuousActionsOnly(model))
                {
                    error = "not a continuous-action ML-Agents policy";
                    Discard(asset);
                    return null;
                }
                return asset;
            }
            catch (Exception exception)
            {
                error = "not a readable model: " + exception.GetType().Name;
                Discard(asset);
                return null;
            }
        }

        static bool HasContinuousActionsOnly(Model model)
        {
            bool continuous = false;
            for (int outputIndex = 0; outputIndex < model.outputs.Count; outputIndex++)
            {
                string outputName = model.outputs[outputIndex].name;
                if (outputName == "continuous_actions") continuous = true;
                if (outputName == "discrete_actions") return false;
            }
            return continuous;
        }

        static void Discard(ModelAsset asset)
        {
            if (asset == null) return;
            if (DescriptionField.GetValue(asset) is UnityEngine.Object description) Kill(description);
            if (WeightsField.GetValue(asset) is Array chunks)
            {
                for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
                {
                    if (chunks.GetValue(chunkIndex) is UnityEngine.Object chunk) Kill(chunk);
                }
            }
            Kill(asset);
        }

        static void Kill(UnityEngine.Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }

        /// <summary>
        /// A roster profile for a community brain: <paramref name="template"/>'s body and
        /// reward DNA (inert in a match), the entry's name, colour and provenance, and the
        /// loaded model. False, with a reason, when the file is not a brain this runtime
        /// can field.
        /// </summary>
        public static bool TryBuildProfile(Entry entry, byte[] modelBytes, Reward_Settings template,
            out Reward_Settings profile, out string error)
        {
            profile = null;
            if (entry == null || template == null)
            {
                error = "no entry or no template profile";
                return false;
            }

            string rosterName = RosterName(entry.name);
            var model = LoadModel(modelBytes, rosterName, out error);
            if (model == null) return false;

            if (!Agent_Soccer.ModelFitsContract(model))
            {
                error = $"trained on a different observation contract (this build needs " +
                        $"{Agent_Soccer.ExpectedModelInputSize} inputs)";
                Discard(model);
                return false;
            }

            profile = UnityEngine.Object.Instantiate(template);
            profile.name = "Reward_" + rosterName;
            profile.hideFlags = HideFlags.DontSave;
            profile.playerName = rosterName;
            profile.personalityNotes = string.IsNullOrEmpty(entry.author)
                ? "Community brain."
                : $"Community brain by {entry.author}." +
                  (string.IsNullOrEmpty(entry.notes) ? string.Empty : " " + entry.notes);
            profile.playerColor = ColorUtility.TryParseHtmlString(entry.colour, out Color colour)
                ? new Color(colour.r, colour.g, colour.b, 1f) : Color.white;
            profile.kitPattern = Reward_Settings.KitPattern.None;
            profile.brainModel = model;
            profile.trainingSteps = Mathf.Max(0, entry.steps);
            profile.trainingRunId = entry.runId ?? string.Empty;
            profile.trainedOn = entry.trainedOn ?? string.Empty;
            // The uploader's claim, not a grade this install took.
            profile.evalWinRate = entry.evalWinRate >= 0f ? Mathf.Clamp01(entry.evalWinRate) : -1f;
            profile.evalEpisodes = Mathf.Max(0, entry.evalEpisodes);
            // Unknown: nothing in the file says what gain it was trained under.
            profile.trainedActionGain = -1f;
            return true;
        }

        // -- Installed brains --------------------------------------------------

        // One profile per file for the life of the process. The menu is rebuilt on every
        // visit, and a lineup chosen on one visit holds these instances across the scene
        // load, so they must not be rebuilt (or their models reloaded) each time.
        static readonly Dictionary<string, Reward_Settings> Loaded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="profile"/> came from the brains folder, not the build.</summary>
        public static bool IsCommunity(Reward_Settings profile)
        {
            if (profile == null) return false;
            foreach (var pair in Loaded)
            {
                if (pair.Value == profile) return true;
            }
            return false;
        }

        /// <summary>
        /// Every usable brain in the brains folder, as roster profiles. Names already in
        /// <paramref name="taken"/> (the built-in roster) or used by an earlier file are
        /// skipped, so a community brain can never shadow a shipped player.
        /// </summary>
        public static List<Reward_Settings> LoadInstalled(Reward_Settings template, IList<string> taken)
        {
            var result = new List<Reward_Settings>();
            if (template == null) return result;

            string folder = BrainFolder;
            if (!Directory.Exists(folder)) return result;

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*" + MODEL_EXTENSION);
            }
            catch (IOException)
            {
                return result;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            var names = new HashSet<string>(StringComparer.Ordinal);
            if (taken != null)
            {
                for (int takenIndex = 0; takenIndex < taken.Count; takenIndex++) names.Add(taken[takenIndex]);
            }

            for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
            {
                string path = files[fileIndex];
                if (!IsSafeFileName(Path.GetFileName(path))) continue;

                if (!Loaded.TryGetValue(path, out var profile) || profile == null)
                {
                    profile = LoadFile(path, template);
                    if (profile == null) continue;
                    Loaded[path] = profile;
                }

                if (!names.Add(profile.playerName)) continue;
                result.Add(profile);
            }
            return result;
        }

        static Reward_Settings LoadFile(string path, Reward_Settings template)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MAX_MODEL_BYTES) return null;

                Entry entry = null;
                string sidecar = Path.ChangeExtension(path, ".json");
                if (File.Exists(sidecar)) entry = JsonUtility.FromJson<Entry>(File.ReadAllText(sidecar));
                // A file copied in by hand has no sidecar: it is named after itself.
                entry ??= new Entry { name = Path.GetFileNameWithoutExtension(path) };
                if (string.IsNullOrEmpty(entry.name)) entry.name = Path.GetFileNameWithoutExtension(path);

                if (TryBuildProfile(entry, File.ReadAllBytes(path), template, out var profile, out string error))
                {
                    return profile;
                }
                Debug.LogWarning($"[BrainHub] {Path.GetFileName(path)} skipped: {error}.");
                return null;
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException || exception is ArgumentException)
            {
                Debug.LogWarning($"[BrainHub] {Path.GetFileName(path)} skipped: {exception.Message}");
                return null;
            }
        }

        /// <summary>
        /// Writes a brain and its sidecar into the brains folder. The bytes are checked
        /// against the contract FIRST, so a refused brain never reaches the disk.
        /// </summary>
        public static bool Install(Entry entry, byte[] modelBytes, Reward_Settings template, out string error)
        {
            if (entry == null || !IsSafeFileName(entry.file))
            {
                error = "the index names a file this app will not write";
                return false;
            }
            if (!TryBuildProfile(entry, modelBytes, template, out var probe, out error)) return false;

            try
            {
                Directory.CreateDirectory(BrainFolder);
                string path = Path.Combine(BrainFolder, entry.file);
                File.WriteAllBytes(path, modelBytes);
                File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonUtility.ToJson(entry, true));
                // Replaces whatever an earlier install of the same file left in the cache.
                if (Loaded.TryGetValue(path, out var previous) && previous != null && previous != probe)
                {
                    Discard(previous.brainModel);
                    Kill(previous);
                }
                Loaded[path] = probe;
                return true;
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException)
            {
                Discard(probe.brainModel);
                Kill(probe);
                error = "could not write to storage: " + exception.Message;
                return false;
            }
        }

        /// <summary>Removes an installed brain's files. The profile stays valid until the next launch.</summary>
        public static bool Uninstall(Reward_Settings profile)
        {
            string found = null;
            foreach (var pair in Loaded)
            {
                if (pair.Value == profile) { found = pair.Key; break; }
            }
            if (found == null) return false;

            try
            {
                File.Delete(found);
                string sidecar = Path.ChangeExtension(found, ".json");
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException)
            {
                return false;
            }
            Loaded.Remove(found);
            return true;
        }

        // -- Export ------------------------------------------------------------

        /// <summary>
        /// Writes each profile's brain as NAME.sentis into <paramref name="folder"/> and
        /// an index.json describing them. Profiles without a brain that fits the
        /// contract are left out. Returns the index it wrote.
        /// </summary>
        public static Index Export(IList<Reward_Settings> profiles, string folder, string author)
        {
            Directory.CreateDirectory(folder);
            var entries = new List<Entry>();

            for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
            {
                var profile = profiles[profileIndex];
                if (profile == null || profile.brainModel == null) continue;
                if (!Agent_Soccer.ModelFitsContract(profile.brainModel)) continue;

                string file = RosterName(profile.playerName) + MODEL_EXTENSION;
                ModelWriter.Save(Path.Combine(folder, file), profile.brainModel);

                entries.Add(new Entry
                {
                    name = profile.playerName,
                    author = author,
                    file = file,
                    runId = profile.trainingRunId,
                    trainedOn = profile.trainedOn,
                    steps = profile.trainingSteps,
                    evalWinRate = profile.evalWinRate,
                    evalEpisodes = profile.evalEpisodes,
                    colour = "#" + ColorUtility.ToHtmlStringRGB(profile.playerColor),
                    notes = string.Empty,
                });
            }

            var index = new Index
            {
                modelInputs = Agent_Soccer.ExpectedModelInputSize,
                brains = entries.ToArray(),
            };
            File.WriteAllText(Path.Combine(folder, INDEX_FILE), JsonUtility.ToJson(index, true));
            return index;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Exports every roster profile under Assets/Agents to Builds/BrainHub. Upload
        /// that folder to a Hugging Face model repo; see docs/brain-hub.md.
        /// </summary>
        [MenuItem("PoSoccer/Export Brains For Hub")]
        public static void ExportRosterToBuilds()
        {
            var profiles = new List<Reward_Settings>();
            string[] guids = AssetDatabase.FindAssets("t:Reward_Settings", new[] { "Assets/Agents" });
            for (int guidIndex = 0; guidIndex < guids.Length; guidIndex++)
            {
                profiles.Add(AssetDatabase.LoadAssetAtPath<Reward_Settings>(
                    AssetDatabase.GUIDToAssetPath(guids[guidIndex])));
            }

            string folder = Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                "Builds", "BrainHub");
            var index = Export(profiles, folder, author: string.Empty);
            Debug.Log($"[BrainHub] Exported {index.brains.Length} brains to {folder}");
        }
#endif

        // -- Hub client --------------------------------------------------------

#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
        /// <summary>Fetches a repo's index. <paramref name="done"/> gets the index, or null and a reason.</summary>
        public static void FetchIndex(string repo, Action<Index, string> done)
        {
            if (!IsSafeRepo(repo))
            {
                done(null, "not a repo name (expected owner/name)");
                return;
            }
            Get(IndexUrl(repo), MAX_INDEX_BYTES, (bytes, error) =>
            {
                if (bytes == null)
                {
                    done(null, error);
                    return;
                }
                var index = ParseIndex(System.Text.Encoding.UTF8.GetString(bytes));
                done(index, index == null ? "the repo's index.json is not a brain index" : null);
            });
        }

        /// <summary>Downloads one brain's bytes. Nothing is written; pass them to <see cref="Install"/>.</summary>
        public static void Download(string repo, Entry entry, Action<byte[], string> done)
        {
            if (!IsSafeRepo(repo) || entry == null || !IsSafeFileName(entry.file))
            {
                done(null, "the index names a file this app will not fetch");
                return;
            }
            Get(FileUrl(repo, entry.file), MAX_MODEL_BYTES, done);
        }

        static void Get(string url, int maxBytes, Action<byte[], string> done)
        {
            var handler = new CappedDownload(maxBytes);
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET, handler, null)
            {
                timeout = REQUEST_TIMEOUT_SECONDS,
            };
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                byte[] data = null;
                string error = null;
                if (handler.Overflowed) error = "the file is larger than this app accepts";
                else if (request.result != UnityWebRequest.Result.Success)
                    error = request.responseCode == 404 ? "not found on the hub" : request.error;
                else data = handler.Bytes;

                request.Dispose();
                done(data, error);
            };
        }

        /// <summary>Stops a download the moment it passes the cap, rather than after it has all arrived.</summary>
        sealed class CappedDownload : DownloadHandlerScript
        {
            readonly int _maxBytes;
            readonly MemoryStream _buffer = new();

            public CappedDownload(int maxBytes) : base(new byte[16 * 1024]) => _maxBytes = maxBytes;

            public bool Overflowed { get; private set; }
            public byte[] Bytes => _buffer.ToArray();

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (_buffer.Length + dataLength > _maxBytes)
                {
                    Overflowed = true;
                    return false;   // aborts the request
                }
                _buffer.Write(data, 0, dataLength);
                return true;
            }
        }
#else
        /// <summary>This build has no hub client. See the class docstring.</summary>
        public static void FetchIndex(string repo, Action<Index, string> done) =>
            done(null, "this build has no network access");

        /// <summary>This build has no hub client. See the class docstring.</summary>
        public static void Download(string repo, Entry entry, Action<byte[], string> done) =>
            done(null, "this build has no network access");
#endif
    }
}
