using System;
using System.IO;
using UnityEngine;

/// <summary>
/// UniRoboLab のプレイヤー固有の設定。シミュレータ核の simulation_resources.json
/// (SIMULATION_RESOURCES_CONFIG) と同じファイルの "unirobolab" 節から読む。
/// 核 (SimulationCore) の設定 (physics_hz, learning_port ...) は "settings" 節のまま。
/// </summary>
[Serializable]
public class UniRoboLabConfigData
{
    /// <summary>unirobolab を持つ Python インタプリタ (既定 "python3")。パネルは "<python> -m unirobolab ..." を起動する。</summary>
    public string python;
    /// <summary>Policy パネルが起動するライブ実行器 (既定 "<python> -m unirobolab live")。環境変数 SIM_POLICY_RUNNER が優先。</summary>
    public string policy_runner_command;
    /// <summary>子プロセスに渡す環境変数 ("KEY=VALUE;KEY2=VALUE2"、例: PYTHONPATH=...)。</summary>
    public string policy_runner_env;
    /// <summary>Check タブの「Run check」が使うコマンド。空なら実行できずレポート表示のみ。引数はパネルが付け足す。</summary>
    public string sim2sim_command;
}

[Serializable]
class UniRoboLabConfigFile
{
    public UniRoboLabConfigData unirobolab;
}

public static class UniRoboLabConfig
{
    public const string ConfigPathEnvVar = "UNIROBOLAB_CONFIG";
    static UniRoboLabConfigData s_Data;
    static bool s_Loaded;

    public static UniRoboLabConfigData Data
    {
        get
        {
            if (!s_Loaded) Load();
            return s_Data;
        }
    }

    /// <summary>UNIROBOLAB_CONFIG → 核が読んだ simulation_resources.json の順で探す。無ければ既定値。</summary>
    static void Load()
    {
        s_Loaded = true;
        s_Data = new UniRoboLabConfigData();
        string path = Environment.GetEnvironmentVariable(ConfigPathEnvVar);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) path = SimulationResources.LoadedPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            var f = JsonUtility.FromJson<UniRoboLabConfigFile>(File.ReadAllText(path));
            if (f?.unirobolab != null) s_Data = f.unirobolab;
            Debug.Log("[UniRoboLabConfig] " + path);
        }
        catch (Exception e) { Debug.LogWarning("[UniRoboLabConfig] " + path + ": " + e.Message); }
    }
}
