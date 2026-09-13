// Agent_BuildPlayerCommand.cs
// Editor-only entry point for headless Unity Android builds.
// Invoke from CLI:
//   Unity.exe -batchmode -quit -nographics -projectPath <root> \
//             -buildTarget Android -executeMethod Agent_BuildPlayerCommand.Build \
//             [-Development] -logFile <log>
//
// Output: <projectRoot>/Builds/PoSoccer/PoSoccer.apk
//
// This class lives in Assets/Editor/, which carries PoSoccer.Editor.Build.asmdef -
// so it compiles into THAT assembly, not into an implicit Assembly-CSharp-Editor as
// this header used to claim. That matters here: it is what lets this file call
// Editor_BuildAndroidAAB's internal shipping-scene resolver instead of keeping a
// second copy of the list.
//
// WHY THIS FILE STILL EXISTS ALONGSIDE Editor_BuildAndroid. That one is the menu
// item; this one is the batch-mode entry point, and the difference is
// EditorApplication.Exit - a CLI build has to report success through a process exit
// code, which a MenuItem has no way to do. Everything else is now shared.

using System;
using System.Collections.Generic;
using System.IO;
using PoSoccer.EditorTools;
using UnityEditor;
using UnityEngine;

public static class Agent_BuildPlayerCommand
{
    // Exit codes:
    //   0   = success
    //   1   = build completed but BuildResult != Succeeded
    //   2   = scene missing on disk
    public static void Build()
    {
        bool development = HasFlag("-Development") || HasFlag("-development");

        // THE SHIPPING SCENE LIST HAS ONE OWNER. This file used to carry its own
        // copy, as did Agent_BuildAabCommand, so three lists had to be kept in
        // agreement by hand and nothing would have reported it if they drifted -
        // the build would simply have shipped a different set of scenes depending
        // on which entry point was used. ResolveShipScenes also does the
        // exists-on-disk check and logs the missing path.
        List<string> shipScenes = Editor_BuildAndroidAAB.ResolveShipScenes();
        if (shipScenes == null)
        {
            EditorApplication.Exit(2);
            return;
        }
        string[] scenes = shipScenes.ToArray();

        // NOTE: EditorBuildSettings.scenes is deliberately NOT written here.
        // BuildPipeline uses BuildPlayerOptions.scenes, so assigning the global
        // list bought nothing and cost something real - it silently rewrote the
        // project's Build Settings as a side effect of running a build, and
        // SCN_Training has to stay at index 0 there for headless training and eval
        // to boot. A build command must not reorder the scene list for everything
        // else in the project.

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputPath = Path.Combine(projectRoot, "Builds", "PoSoccer", "PoSoccer.apk");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        // This command builds the side-load APK, so it owns both settings the AAB
        // release path leaves behind. Without forcing them a build here fails or,
        // worse, succeeds and produces something uninstallable:
        //
        //   useCustomKeystore  -> points at keys/posoccer-upload.keystore with the
        //                         password in POSOCCER_KEYSTORE_PASS. Absent those,
        //                         the build dies with "Unable to sign the Android
        //                         application". A side-load wants the debug keystore.
        //   buildAppBundle     -> emits an .aab even when the output path ends in
        //                         .apk. The file looks fine but carries
        //                         BundleConfig.pb and base/manifest/AndroidManifest.xml
        //                         instead of a root manifest, and adb rejects it with
        //                         the misleading INSTALL_PARSE_FAILED_UNEXPECTED_EXCEPTION.
        //
        // Both are restored in the finally block so Agent_BuildAabCommand and the
        // Play release path are unaffected.
        bool prevAppBundle = EditorUserBuildSettings.buildAppBundle;
        bool prevCustomKeystore = PlayerSettings.Android.useCustomKeystore;
        EditorUserBuildSettings.buildAppBundle = false;
        PlayerSettings.Android.useCustomKeystore = false;

        // Use fully-qualified types so we don't depend on `using` resolving
        // UnityEditor.Build.Reporting (that sub-namespace is in a separate
        // assembly that the implicit Assembly-CSharp-Editor doesn't reference)
        UnityEditor.Build.Reporting.BuildReport report;
        try
        {
            var buildPlayerOptions = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = development
                    ? (BuildOptions.Development | BuildOptions.AllowDebugging)
                    : BuildOptions.None,
            };

            Debug.Log($"[Agent_BuildPlayerCommand] Building " +
                      $"{(development ? "DEVELOPMENT" : "MASTER")} APK -> {outputPath}");

            report = BuildPipeline.BuildPlayer(buildPlayerOptions);
        }
        finally
        {
            // Restore here, not in a finally around the exits: EditorApplication.Exit
            // does not return, so anything after it would never run.
            EditorUserBuildSettings.buildAppBundle = prevAppBundle;
            PlayerSettings.Android.useCustomKeystore = prevCustomKeystore;
        }

        var summary = report.summary;

        Debug.Log($"[Agent_BuildPlayerCommand] Result:  {summary.result}");
        Debug.Log($"[Agent_BuildPlayerCommand] Output:  {summary.outputPath}");
        Debug.Log($"[Agent_BuildPlayerCommand] Size:    {summary.totalSize} bytes");
        Debug.Log($"[Agent_BuildPlayerCommand] Time:    {summary.totalTime}");
        Debug.Log($"[Agent_BuildPlayerCommand] Errors:  {summary.totalErrors}");
        Debug.Log($"[Agent_BuildPlayerCommand] Warnings:{summary.totalWarnings}");

        if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
            return;
        }

        EditorApplication.Exit(0);
    }

    private static bool HasFlag(string flag)
    {
        foreach (string a in Environment.GetCommandLineArgs())
        {
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
