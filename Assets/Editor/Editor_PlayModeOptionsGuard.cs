using UnityEditor;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Forces "Enter Play Mode Options" back to None whenever it drifts.
    ///
    /// WHY THIS EXISTS. Running the PlayMode suite flips
    /// ProjectSettings/EditorSettings.asset's m_EnterPlayModeOptions from 0 to 1
    /// (DisableDomainReload) and leaves it flipped - observed on 2026-08-05 and
    /// again on 2026-09-06, once per run. That is not cosmetic. With domain
    /// reload disabled:
    ///
    ///   - ML-Agents' Academy needs the reload; without it a trained brain is
    ///     driven by stale statics.
    ///   - Agent_EnvController's spawn dictionaries come back EMPTY while the
    ///     serialized `agents` list comes back populated, so ResetPitch threw
    ///     KeyNotFoundException every FixedUpdate and the pitch froze with all
    ///     four actions at exactly 0.0000 and nothing in the console. It reads
    ///     as "the brain is broken".
    ///
    /// The documented mitigation was a manual `git diff ProjectSettings/` after
    /// every PlayMode run. That is a checklist item on a landmine that re-arms
    /// itself on every run, and it has already been missed twice. This makes the
    /// correction automatic.
    ///
    /// WHAT IT DOES NOT DO. It never touches the setting while play mode is
    /// running or entering - the test framework's flip has to survive its own
    /// run, and fighting it mid-run would be a worse bug than the one this
    /// fixes. The correction lands on the next return to edit mode (and on
    /// domain reload, which covers an editor restart mid-drift).
    ///
    /// The guard can be switched off from the PoSoccer menu when somebody
    /// deliberately wants fast enter-play-mode; the preference is per-user
    /// (EditorPrefs), so switching it off cannot be committed by accident.
    /// </summary>
    [InitializeOnLoad]
    internal static class Editor_PlayModeOptionsGuard
    {
        const string ENABLED_PREF = "PoSoccer.PlayModeOptionsGuard.Enabled";
        const string MENU_PATH = "PoSoccer/Guard Enter Play Mode Options";

        /// <summary>
        /// The only configuration this project runs on: neither reload disabled.
        /// Writing this also clears enterPlayModeOptionsEnabled, because the two
        /// are coupled - see Enforce.
        /// </summary>
        const EnterPlayModeOptions REQUIRED = EnterPlayModeOptions.None;

        static bool Enabled
        {
            get => EditorPrefs.GetBool(ENABLED_PREF, true);
            set => EditorPrefs.SetBool(ENABLED_PREF, value);
        }

        static Editor_PlayModeOptionsGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // Deferred: a domain reload is mid-flight when the static constructor
            // runs, and AssetDatabase writes are not safe there.
            EditorApplication.delayCall += () => Enforce(false);
        }

        static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // ExitingPlayMode still has the player loop up. EnteredEditMode is
            // the first moment the flip is both visible and safe to undo.
            if (change == PlayModeStateChange.EnteredEditMode) Enforce(false);
        }

        /// <summary>Path of the settings asset this guard keeps in sync with memory.</summary>
        const string SETTINGS_ASSET = "ProjectSettings/EditorSettings.asset";

        /// <summary>
        /// Returns true when the setting had drifted and was corrected.
        ///
        /// ONLY <c>enterPlayModeOptions</c> IS WRITTEN, NEVER
        /// <c>enterPlayModeOptionsEnabled</c>. The two look like independent
        /// fields and are not - measured in the live editor on 2026-09-06:
        ///
        ///   set enterPlayModeOptionsEnabled = true
        ///       -> options becomes DisableDomainReload | DisableSceneReload
        ///   set enterPlayModeOptions = None
        ///       -> enabled becomes false, on its own
        ///   set enterPlayModeOptions = DisableDomainReload
        ///       -> enabled becomes true, on its own
        ///
        /// They are one setting behind two properties, with the invariant
        /// `enabled == (options != None)`. So a version of this guard that tried
        /// to hold `enabled = true` alongside `options = None` was asking for a
        /// state Unity does not have: it turned BOTH reloads off - precisely the
        /// configuration this file exists to prevent - and then normalised itself
        /// back, so it would have re-fired and re-logged forever. Writing the
        /// options alone gets the toggle for free and converges in one pass.
        /// </summary>
        internal static bool Enforce(bool force)
        {
            if (!force && !Enabled) return false;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return false;

            EnterPlayModeOptions was = EditorSettings.enterPlayModeOptions;
            bool memoryDrifted = was != REQUIRED;
            if (memoryDrifted) EditorSettings.enterPlayModeOptions = REQUIRED;

            // MEMORY BEING RIGHT IS NOT THE SAME AS THE FILE BEING RIGHT, and the
            // file is what git sees and what the next editor loads. Measured on
            // 2026-09-06: after a run of PlayMode suites the live editor reported
            // `options=None` while ProjectSettings/EditorSettings.asset still read
            // `m_EnterPlayModeOptions: 1`. An earlier version of this method
            // returned early whenever memory looked correct, so that stale file
            // was never rewritten - the guard reported success and left exactly
            // the drift it exists to remove. Checking the file directly is the
            // only honest test, and it costs one small read.
            bool fileDrifted = FileSaysDrifted();
            if (!memoryDrifted && !fileDrifted) return false;

            Persist();

            Debug.Log(
                $"[PlayModeOptionsGuard] Enter Play Mode Options had drifted " +
                $"(memory '{was}', file {(fileDrifted ? "stale" : "clean")}) and was reset to " +
                $"'{REQUIRED}'. A PlayMode test run does this on every run; with " +
                "DisableDomainReload set, Agent_EnvController.ResetPitch throws and the " +
                $"pitch freezes silently. Turn the guard off under '{MENU_PATH}' if you meant it.");
            return true;
        }

        /// <summary>
        /// True when the serialized file carries a non-zero m_EnterPlayModeOptions,
        /// whatever the in-memory property currently says.
        /// </summary>
        static bool FileSaysDrifted()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.dataPath) ?? ".", SETTINGS_ASSET);
                if (!System.IO.File.Exists(path)) return false;

                foreach (string line in System.IO.File.ReadAllLines(path))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("m_EnterPlayModeOptions:")) continue;
                    string value = trimmed.Substring("m_EnterPlayModeOptions:".Length).Trim();
                    return value != "0";
                }
            }
            catch (System.Exception)
            {
                // A guard that throws on a locked or half-written file would be
                // worse than one that skips a cycle; the next reload retries.
            }
            return false;
        }

        /// <summary>
        /// Writes the corrected values back to ProjectSettings.
        ///
        /// `AssetDatabase.SaveAssets()` ALONE DOES NOT DO THIS, which is how the
        /// first version of this guard came to be silently useless: it set the
        /// in-memory property correctly - verified by reflection, the live editor
        /// reported `options=None` - while the file on disk still read
        /// `m_EnterPlayModeOptions: 1`, so `git status` stayed dirty and a fresh
        /// editor would have loaded the drifted value straight back. EditorSettings
        /// is a ProjectSettings singleton, not a tracked asset, so it has to be
        /// loaded from its path and marked dirty by hand before a save reaches it.
        /// </summary>
        static void Persist()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath(SETTINGS_ASSET);
            if (settings != null)
            {
                for (int i = 0; i < settings.Length; i++)
                {
                    if (settings[i] != null) EditorUtility.SetDirty(settings[i]);
                }
            }
            AssetDatabase.SaveAssets();
        }

        [MenuItem(MENU_PATH)]
        static void ToggleGuard()
        {
            Enabled = !Enabled;
            if (Enabled) Enforce(false);
        }

        [MenuItem(MENU_PATH, isValidateFunction: true)]
        static bool ToggleGuardValidate()
        {
            Menu.SetChecked(MENU_PATH, Enabled);
            return true;
        }
    }
}
