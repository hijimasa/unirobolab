using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// UniRoboLab のプレイヤーをバッチモードで作る。
///   Unity -batchmode -nographics -quit -projectPath . -executeMethod BuildPlayer.Build -buildOutput <path> [-playerTarget linux|windows]
/// シーンが無ければ LabSceneBuilder で作ってから組む。
/// </summary>
public static class BuildPlayer
{
    public static void Build()
    {
        string output = Arg("-buildOutput") ?? "../../generated/player/UniRoboLab.x86_64";
        string targetArg = (Arg("-playerTarget") ?? "linux").ToLowerInvariant();
        BuildTarget target = targetArg.StartsWith("win") ? BuildTarget.StandaloneWindows64 : BuildTarget.StandaloneLinux64;
        LabFontBuilder.Build();     // 日本語のアトラスを焼いて Resources に置く (実行時生成は player で描画されない)
        if (!File.Exists(LabSceneBuilder.ScenePath)) LabSceneBuilder.Build();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        var opts = new BuildPlayerOptions
        {
            scenes = new[] { LabSceneBuilder.ScenePath },
            locationPathName = output,
            target = target,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(opts);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            throw new Exception("build failed: " + report.summary.result);
        Debug.Log($"[BuildPlayer] {output} ({report.summary.totalSize / 1e6:F0} MB)");
    }

    static string Arg(string name)
    {
        string[] a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return null;
    }
}
