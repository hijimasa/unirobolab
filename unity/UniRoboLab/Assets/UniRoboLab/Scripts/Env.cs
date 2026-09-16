using System;
using System.IO;
using UnityEngine;

/// <summary>
/// 環境の検出 (初回セットアップ画面用): Python 環境、リポジトリの場所、ROS 2 の有無、学習サーバ。
/// 設定ファイルの手書きを無くすための既定値もここ。
/// </summary>
public static class Env
{
    public static string RepoRoot { get; private set; } = "";
    public static string Python { get; private set; } = "";
    public static string PythonStatus { get; set; } = "";     // "ok" | 理由
    public static string Ros2 { get; set; } = "unknown";      // host | container | none | unknown
    public static string Ros2Detail { get; set; } = "";

    /// <summary>プレイヤーの場所から上へ辿って python/src/unirobolab を持つフォルダ (リポジトリ) を探す。</summary>
    public static void Detect()
    {
        string start = Path.GetDirectoryName(Application.dataPath) ?? ".";
        RepoRoot = FindRepo(start) ?? FindRepo(Directory.GetCurrentDirectory()) ?? "";
        string cfg = UniRoboLabConfig.Data.python;
        string venv = string.IsNullOrEmpty(RepoRoot) ? "" : Path.Combine(RepoRoot, ".venv", "bin", "python");
        Python = !string.IsNullOrEmpty(cfg) && cfg != "python3" ? cfg : (File.Exists(venv) ? venv : (string.IsNullOrEmpty(cfg) ? "python3" : cfg));
        if (string.IsNullOrEmpty(UniRoboLabConfig.Data.policy_runner_env) && !string.IsNullOrEmpty(RepoRoot))
            UniRoboLabConfig.Data.policy_runner_env = "PYTHONPATH=" + Path.Combine(RepoRoot, "python", "src");
        UniRoboLabConfig.Data.python = Python;
        Debug.Log($"[Env] repo={RepoRoot} python={Python}");
    }

    static string FindRepo(string from)
    {
        try
        {
            var d = new DirectoryInfo(from);
            for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "python", "src", "unirobolab"))) return d.FullName;
        }
        catch (Exception) { }
        return null;
    }

    public static int LearningPort()
    {
        string fromEnv = Environment.GetEnvironmentVariable(SimulationControl.LearningPortEnvVar);
        if (!string.IsNullOrEmpty(fromEnv) && int.TryParse(fromEnv, out int p)) return p;
        return SimulationResources.Settings != null ? SimulationResources.Settings.learning_port : 0;
    }

    /// <summary>Python 環境の確認コマンド (ExternalProcess で回す)。</summary>
    public static string PythonCheckCommand() => ExternalProcess.Quote(Python) + " -c 'import unirobolab, numpy, onnxruntime; import stable_baselines3; print(\"ok\")' 2>&1";

    /// <summary>ROS 2 の確認: ホストに ros2 があるか、コンテナが動いているか。</summary>
    public static string Ros2CheckCommand() =>
        "if command -v ros2 >/dev/null 2>&1; then echo host; elif docker ps --format '{{.Names}}' 2>/dev/null | grep -q '^unirobolab-sim2sim'; then echo container; elif command -v docker >/dev/null 2>&1; then echo docker-only; else echo none; fi";
}
