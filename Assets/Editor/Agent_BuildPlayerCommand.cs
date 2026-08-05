// Agent_BuildPlayerCommand.cs
// Editor-only entry point for headless Unity Android builds.
// Invoke from CLI:
//   Unity.exe -batchmode -quit -nographics -projectPath <root> \
//             -buildTarget Android -executeMethod Agent_BuildPlayerCommand.Build \
//             [-Development] -logFile <log>
//
// Output: <projectRoot>/Builds/PoSoccer/PoSoccer.apk
//
// This class lives in Assets/Editor/ so it gets compiled into an implicit
// editor-only assembly and is automatically stripped from runtime builds.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class Agent_BuildPlayerCommand
{
    // SCN_Menu first: index 0 is what the app boots into, and the game always starts
    // from the menu (Agent_MatchLoader depends on Agent_MatchSetup statics that only the
    // menu sets). SCN_Training is excluded - it is the headless training/eval scene and
    // is unreachable from the game's UI on device.
    private static readonly string[] Scenes =
    {
        "Assets/Scenes/SCN_Menu.unity",
        "Assets/Scenes/SCN_Exhibition.unity",
    };

    // Exit codes:
    //   0   = success
    //   1   = build completed but BuildResult != Succeeded
    //   2   = scene missing on disk
    public static void Build()
    {
        bool development = HasFlag("-Development") || HasFlag("-development");

        // Sanity: every scene file must exist before we invoke BuildPipeline.
        foreach (string scene in Scenes)
        {
            if (!File.Exists(scene))
            {
                Debug.LogError($"[Agent_BuildPlayerCommand] Scene missing on disk: {scene}");
                EditorApplication.Exit(2);
                return;
            }
        }

        // Register scenes into EditorBuildSettings, then drive BuildPipeline.
        EditorBuildSettings.scenes = Array.ConvertAll(
            Scenes,
            s => new EditorBuildSettingsScene(s, true));

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputPath = Path.Combine(projectRoot, "Builds", "PoSoccer", "PoSoccer.apk");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var buildPlayerOptions = new BuildPlayerOptions
        {
            scenes = Scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = development
                ? (BuildOptions.Development | BuildOptions.AllowDebugging)
                : BuildOptions.None,
        };

        Debug.Log($"[Agent_BuildPlayerCommand] Building " +
                  $"{(development ? "DEVELOPMENT" : "MASTER")} APK -> {outputPath}");

        // Use fully-qualified types so we don't depend on `using` resolving
        // UnityEditor.Build.Reporting (that sub-namespace is in a separate
        // assembly that the implicit Assembly-CSharp-Editor doesn't reference)
        var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
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
