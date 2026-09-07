using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Builds a DEBUG-SIGNED APK for sideloading to a developer's own device.
    ///
    /// WHY THIS EXISTS SEPARATELY FROM <see cref="Editor_BuildAndroid"/> (2026-09-07).
    /// That builder signs with the Play UPLOAD key and aborts without it. The upload
    /// keystore (`PoSoccer-Release/posoccer-upload.jks`) is not on this machine, and
    /// generating a replacement would be actively harmful: Play identifies an app by its
    /// upload key, so a new one permanently forecloses updating the listing if the app
    /// has ever been published. Losing that key is the one unrecoverable mistake in the
    /// Android pipeline, and quietly minting a new one to make a build succeed would hide
    /// it behind a green result.
    ///
    /// Sideloading does not need the upload key. Gradle's debug keystore is sufficient
    /// for `adb install`, and Unity generates `~/.android/debug.keystore` on demand when
    /// useCustomKeystore is false. What you cannot do with the artifact this produces is
    /// ship it to Play or install it over a Play-signed build - the signatures differ, so
    /// Android refuses the upgrade. That is a real constraint, not a footnote:
    /// uninstall the store build first, or use the release builder once the key is back.
    ///
    /// THE TWO FLAGS. This project is configured for AAB releases, and both settings
    /// break a sideload independently (see the landmine in CLAUDE.md):
    ///   - `buildAppBundle = true` emits an AAB even when the path ends in .apk. The file
    ///     looks plausible and `adb install` fails with a misleading manifest parse error.
    ///   - `useCustomKeystore = true` points at the absent release key and aborts.
    /// Both are forced off for the build and RESTORED afterwards in a finally, so the
    /// release configuration survives a sideload.
    /// </summary>
    public static class Editor_BuildAndroidDebugApk
    {
        private const string OUTPUT_PATH = "Builds/Android/PoSoccer-debug.apk";

        [MenuItem("PoSoccer/Build Android APK (debug-signed, sideload)")]
        public static void Build()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("DEBUG APK RESULT: Aborted - exit Play mode first.");
                return;
            }

            List<string> scenes = Editor_BuildAndroidAAB.ResolveShipScenes();
            if (scenes == null)
            {
                Debug.LogError("DEBUG APK RESULT: Aborted - ship scene list did not resolve.");
                return;
            }

            // Remember the release configuration so a sideload cannot leave the project
            // in a state where the next AAB is unsigned or emitted as the wrong format.
            bool prevAppBundle = EditorUserBuildSettings.buildAppBundle;
            bool prevCustomKeystore = PlayerSettings.Android.useCustomKeystore;
            string prevKeystoreName = PlayerSettings.Android.keystoreName;
            string prevKeyalias = PlayerSettings.Android.keyaliasName;

            try
            {
                PlayerSettings.SetApplicationIdentifier(
                    NamedBuildTarget.Android, Editor_BuildAndroidAAB.APP_ID);

                // Debug signing: Unity/Gradle creates ~/.android/debug.keystore if absent.
                PlayerSettings.Android.useCustomKeystore = false;
                PlayerSettings.Android.keystoreName = string.Empty;
                PlayerSettings.Android.keyaliasName = string.Empty;
                EditorUserBuildSettings.buildAppBundle = false;

                // Auto-increment so each sideload is distinguishable on the device and in
                // logcat. Play rejects a reused code, and a human reading a bug report
                // cannot tell two builds apart if the code never moves.
                PlayerSettings.Android.bundleVersionCode += 1;
                int versionCode = PlayerSettings.Android.bundleVersionCode;

                // Portrait is locked project-wide; assert rather than assume, because a
                // rotated sideload is the kind of thing that gets blamed on the device.
                PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;

                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(OUTPUT_PATH));

                var options = new BuildPlayerOptions
                {
                    scenes = scenes.ToArray(),
                    locationPathName = OUTPUT_PATH,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = BuildOptions.None,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                if (summary.result == BuildResult.Succeeded)
                {
                    Debug.Log($"DEBUG APK RESULT: Success - {OUTPUT_PATH} " +
                              $"({summary.totalSize / (1024f * 1024f):0.0} MB), " +
                              $"versionCode {versionCode}, " +
                              $"appId {Editor_BuildAndroidAAB.APP_ID}, " +
                              $"scenes {scenes.Count}, " +
                              $"{summary.totalTime.TotalSeconds:0} s");
                }
                else
                {
                    Debug.LogError($"DEBUG APK RESULT: {summary.result} - " +
                                   $"{summary.totalErrors} error(s).");
                }
            }
            finally
            {
                // Restore the release configuration unconditionally.
                EditorUserBuildSettings.buildAppBundle = prevAppBundle;
                PlayerSettings.Android.useCustomKeystore = prevCustomKeystore;
                PlayerSettings.Android.keystoreName = prevKeystoreName;
                PlayerSettings.Android.keyaliasName = prevKeyalias;
                AssetDatabase.SaveAssets();
            }
        }
    }
}
