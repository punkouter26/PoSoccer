// Agent_BuildAabCommand.cs
// Editor-only entry point for a Play-uploadable Android App Bundle.
//
// Separate from Agent_BuildPlayerCommand.Build (which produces the side-load APK)
// because the Play artifact differs in three ways that all matter:
//   1. .aab, not .apk        - Play requires App Bundles for apps created
//                              after August 2021, internal testing included.
//   2. Target API 36         - new apps must target Android 16 from
//                              2026-08-31; the APK path leaves this at 34.
//   3. Real upload keystore  - the APK path uses Unity's debug keystore, which
//                              Play rejects.
//
// Invoke from CLI:
//   Unity.exe -batchmode -quit -nographics -projectPath <root> \
//             -buildTarget Android -executeMethod Agent_BuildAabCommand.Build \
//             -logFile <log>
//
// Signing credentials are read from the ENVIRONMENT, never from this file or
// any tracked config, so the keystore password never enters the repo:
//   POSOCCER_KEYSTORE        full path to the .keystore
//   POSOCCER_KEYSTORE_PASS   keystore password
//   POSOCCER_KEYALIAS        key alias
//   POSOCCER_KEYALIAS_PASS   key alias password
//   POSOCCER_VERSION_CODE    optional integer; overrides bundleVersionCode
//   POSOCCER_VERSION_NAME    optional string;  overrides bundleVersion
//
// Output: <projectRoot>/Builds/PoSoccer/PoSoccer.aab
//
// Exit codes: 0 success | 1 build failed | 2 scene missing | 3 signing config missing

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class Agent_BuildAabCommand
{
    // Play's requirement for new apps from 2026-08-31. Written as a cast rather
    // than AndroidSdkVersions.AndroidApiLevel36 so this still compiles on an
    // editor whose enum predates API 36 - the underlying value is just the int.
    private const int TargetApiLevel = 36;

    // Index 0 is what the app boots into, so SCN_Menu MUST come first (UNITY_RULES:
    // the game always starts from the menu - Agent_MatchLoader reads squad sizes and
    // per-slot profiles from Agent_MatchSetup statics that only the menu sets).
    //
    // SCN_Training is deliberately EXCLUDED from Android builds. It is the headless
    // training/eval scene, is never reachable from the game's UI, and shipping it only
    // bloats the download. The "SCN_Training must stay index 0" rule applies to the
    // Windows player that mlagents-learn and evaluate.ps1 boot - not to the store build.
    private static readonly string[] Scenes =
    {
        "Assets/Scenes/SCN_Menu.unity",
        "Assets/Scenes/SCN_Exhibition.unity",
    };

    public static void Build()
    {
        foreach (string scene in Scenes)
        {
            if (!File.Exists(scene))
            {
                Debug.LogError($"[Agent_BuildAabCommand] Scene missing on disk: {scene}");
                EditorApplication.Exit(2);
                return;
            }
        }

        // ---- signing -------------------------------------------------------
        string keystore = Environment.GetEnvironmentVariable("POSOCCER_KEYSTORE");
        string keystorePass = Environment.GetEnvironmentVariable("POSOCCER_KEYSTORE_PASS");
        string keyAlias = Environment.GetEnvironmentVariable("POSOCCER_KEYALIAS");
        string keyAliasPass = Environment.GetEnvironmentVariable("POSOCCER_KEYALIAS_PASS");

        if (string.IsNullOrEmpty(keystore) || string.IsNullOrEmpty(keystorePass) ||
            string.IsNullOrEmpty(keyAlias) || string.IsNullOrEmpty(keyAliasPass))
        {
            Debug.LogError("[Agent_BuildAabCommand] Signing env vars missing. Set POSOCCER_KEYSTORE, " +
                           "POSOCCER_KEYSTORE_PASS, POSOCCER_KEYALIAS, POSOCCER_KEYALIAS_PASS. " +
                           "Refusing to fall back to the debug keystore - Play would reject it.");
            EditorApplication.Exit(3);
            return;
        }
        if (!File.Exists(keystore))
        {
            Debug.LogError($"[Agent_BuildAabCommand] No keystore at {keystore}");
            EditorApplication.Exit(3);
            return;
        }

        PlayerSettings.Android.useCustomKeystore = true;
        PlayerSettings.Android.keystoreName = keystore;
        PlayerSettings.Android.keystorePass = keystorePass;
        PlayerSettings.Android.keyaliasName = keyAlias;
        PlayerSettings.Android.keyaliasPass = keyAliasPass;

        // ---- Play requirements ---------------------------------------------
        EditorUserBuildSettings.buildAppBundle = true;
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android,
                                           ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)TargetApiLevel;

        string versionName = Environment.GetEnvironmentVariable("POSOCCER_VERSION_NAME");
        if (!string.IsNullOrEmpty(versionName)) { PlayerSettings.bundleVersion = versionName; }

        string versionCode = Environment.GetEnvironmentVariable("POSOCCER_VERSION_CODE");
        if (!string.IsNullOrEmpty(versionCode) && int.TryParse(versionCode, out int vc))
        {
            PlayerSettings.Android.bundleVersionCode = vc;
        }
        else
        {
            // Play rejects a versionCode it has already seen, and an internal
            // test burns one per upload, so auto-increment when not pinned.
            PlayerSettings.Android.bundleVersionCode += 1;
        }

        EditorBuildSettings.scenes = Array.ConvertAll(
            Scenes, s => new EditorBuildSettingsScene(s, true));

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputPath = Path.Combine(projectRoot, "Builds", "PoSoccer", "PoSoccer.aab");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var options = new BuildPlayerOptions
        {
            scenes = Scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };

        Debug.Log($"[Agent_BuildAabCommand] appId={PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android)} " +
                  $"version={PlayerSettings.bundleVersion} " +
                  $"versionCode={PlayerSettings.Android.bundleVersionCode} " +
                  $"targetSdk={(int)PlayerSettings.Android.targetSdkVersion} " +
                  $"minSdk={(int)PlayerSettings.Android.minSdkVersion}");
        Debug.Log($"[Agent_BuildAabCommand] Building AAB -> {outputPath}");

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        Debug.Log($"[Agent_BuildAabCommand] Result:  {summary.result}");
        Debug.Log($"[Agent_BuildAabCommand] Output:  {summary.outputPath}");
        Debug.Log($"[Agent_BuildAabCommand] Size:    {summary.totalSize} bytes");
        Debug.Log($"[Agent_BuildAabCommand] Time:    {summary.totalTime}");
        Debug.Log($"[Agent_BuildAabCommand] Errors:  {summary.totalErrors}");

        // Do not leave the password sitting in ProjectSettings.asset, which is
        // tracked in git. Unity persists these fields on write otherwise.
        PlayerSettings.Android.keystorePass = "";
        PlayerSettings.Android.keyaliasPass = "";

        EditorApplication.Exit(
            summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }
}
