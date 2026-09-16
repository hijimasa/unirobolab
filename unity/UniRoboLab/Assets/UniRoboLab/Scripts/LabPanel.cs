using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UniRoboLab パネル: Contract (契約 JSON の編集・検証)、Train (学習の起動・停止と学習曲線)、
/// Check (sim2sim のレポート表示と、設定があれば実行) の 3 タブ。Policy パネルと同じく
/// 実行時生成の uGUI で、外部処理は unirobolab の子プロセス (ExternalProcess)。
/// </summary>
/// <remarks>
/// 最小限の機能に絞る: 見た目より「GUI から一通り回せる」ことを優先している。
/// ヘッドレス検証用に SIM_TRAIN_AUTORUN="契約|学習設定|URDF|体数|出力ディレクトリ" で
/// 起動時に学習を始められる (UI は作らない)。
/// </remarks>
public class LabPanel : MonoBehaviour
{
    public const string TrainAutorunEnvVar = "SIM_TRAIN_AUTORUN";
    /// <summary>ヘッドレス検証用: "契約|学習設定|URDF" で Task タブの Read → Save → 学習開始を通す。</summary>
    public const string TaskAutorunEnvVar = "SIM_TASK_AUTORUN";
    bool m_TaskAutorun;
    static readonly Color HeaderColor = new Color(0.95f, 0.95f, 0.95f, 1f);
    static readonly Color TextColor = new Color(0.85f, 0.85f, 0.85f, 1f);
    static readonly Color PanelColor = new Color(0.12f, 0.12f, 0.14f, 0.92f);
    static readonly Color TabColor = new Color(0.25f, 0.25f, 0.3f, 1f);

    SimulationControl m_Control;
    readonly Dictionary<string, GameObject> m_Tabs = new Dictionary<string, GameObject>();

    // Contract
    TMP_InputField m_ContractPath, m_ContractText;
    TMP_Text m_ContractStatus;
    ExternalProcess m_Validate;

    // Train
    TMP_InputField m_TrainContract, m_TrainConfig, m_TrainUrdf, m_TrainEnvs, m_TrainOut;
    TMP_Text m_TrainStatus;
    RawImage m_Curve;
    Texture2D m_CurveTex;
    ExternalProcess m_Train;
    float m_NextCurve;
    int m_CurvePoints;

    // Task (light-user layer)
    TMP_InputField m_TaskContract, m_TaskTrain;
    TMP_Text m_TaskType, m_TaskStatus, m_TaskGoalLabel, m_TaskGoalMaxLabel, m_TaskTolLabel, m_TaskTimeLabel;
    Slider m_TaskGoal, m_TaskGoalMax, m_TaskTol, m_TaskTime;
    GameObject m_TaskGoalMaxRow, m_TaskExpert;
    bool m_TaskIsBase;
    string m_TaskOnnxDir;             // 契約が宣言する policy.onnx の置き場所 (task-show の onnx)。学習の出力先
    ExternalProcess m_TaskProc;
    bool m_TaskReadPending;
    readonly List<GameObject> m_ExpertTabButtons = new List<GameObject>();
    bool m_ExpertShown;

    // Check
    TMP_InputField m_ReportPath, m_CheckContract, m_CheckPkg, m_CheckScenario;
    TMP_Text m_ReportText, m_CheckStatus, m_PlainText;
    GameObject m_CheckExpert;
    ExternalProcess m_Explain;
    public const string CheckAutorunEnvVar = "SIM_CHECK_AUTORUN";
    /// <summary>画面確認用: SIM_GUI_SCREENSHOT=<png> で起動 8 秒後 (SIM_GUI_SCREENSHOT_DELAY 秒後) に画面を保存する。</summary>
    public const string ScreenshotEnvVar = "SIM_GUI_SCREENSHOT";
    readonly List<(string path, float at)> m_Shots = new List<(string, float)>();   // "a.png@10,b.png@120" も可
    /// <summary>Policy パネルが使う: 専門家層の表示、Task タブの契約と目標範囲、直近の学習出力。</summary>
    public static bool ExpertShown => s_Instance != null && s_Instance.m_ExpertShown;
    public static bool JapaneseFont => s_JapaneseFont;
    public static string TaskContract => s_Instance != null && s_Instance.m_TaskContract != null ? s_Instance.m_TaskContract.text : "";
    public static string LastRunDir => s_Instance != null ? s_Instance.m_LastRunDir : "";
    public static float TaskGoalRange => s_Instance != null && s_Instance.m_TaskGoal != null ? s_Instance.m_TaskGoal.value : 1f;
    public static bool TaskIsBase => s_Instance != null && s_Instance.m_TaskIsBase;
    static LabPanel s_Instance;
    string m_LastRunDir = "";
    static bool s_JapaneseFont;   // OS の日本語フォントを TMP のフォールバックに登録できたか
    static bool s_FontProbed;

    /// <summary>プロジェクトは CJK フォントを同梱しないので、OS のフォントから動的フォントを作って
    /// TMP のフォールバックに足す。見つからなければ英語表示にする。</summary>
    static void EnsureJapaneseFont()
    {
        if (s_FontProbed) return;
        s_FontProbed = true;
        try
        {
            string[] installed = Font.GetOSInstalledFontNames();
            var candidates = new List<string>();
            foreach (string key in new[] { "noto sans cjk jp", "noto sans jp", "yu gothic ui", "meiryo", "hiragino sans", "noto sans cjk", "droid sans fallback", "droid sans japanese", "ipagothic", "ipaexgothic", "takao", "vl gothic" })
                foreach (string n in installed)
                    if (n.ToLowerInvariant().Contains(key) && !candidates.Contains(n)) candidates.Add(n);
            Debug.Log("[LabPanel] japanese font candidates: " + string.Join(", ", candidates));
            foreach (string name in candidates)
            {
                // OS フォントは family 名で読む (Font オブジェクト経由だとフォントデータに触れず失敗する)
                TMP_FontAsset fa = null;
                foreach (string style in new[] { "Regular", "" })
                {
                    try { fa = TMP_FontAsset.CreateFontAsset(name, style, 24); } catch (Exception) { fa = null; }
                    if (fa != null) break;
                }
                if (fa == null) { Debug.Log("[LabPanel] font asset creation failed: " + name); continue; }
                var fallbacks = TMP_Settings.fallbackFontAssets ?? new List<TMP_FontAsset>();
                fallbacks.Add(fa);
                TMP_Settings.fallbackFontAssets = fallbacks;
                s_JapaneseFont = true;
                Debug.Log("[LabPanel] japanese font: " + name);
                return;
            }
            Debug.Log("[LabPanel] no japanese OS font found (" + installed.Length + " fonts); using english text");
        }
        catch (Exception e) { Debug.LogWarning("[LabPanel] font probe failed: " + e.Message); }
    }
    ExternalProcess m_Check;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        SimulationControl control = FindFirstObjectByType<SimulationControl>(FindObjectsInactive.Include);
        if (control == null || control.GetComponent<LabPanel>() != null) return;
        control.gameObject.AddComponent<LabPanel>();
    }

    void Start()
    {
        s_Instance = this;
        m_Control = GetComponent<SimulationControl>();
        if (!Application.isBatchMode) BuildUi();
        string autorun = Environment.GetEnvironmentVariable(TrainAutorunEnvVar);
        if (!string.IsNullOrEmpty(autorun))
        {
            string[] p = autorun.Split('|');
            if (p.Length >= 5) { m_AutorunOut = p[4]; StartTraining(p[0], p[1], p[2], p[3], p[4]); }
            else Debug.LogError($"[LabPanel] {TrainAutorunEnvVar} は '契約|学習設定|URDF|体数|出力' の形");
        }
        string tabEnv = Environment.GetEnvironmentVariable("SIM_GUI_TAB");   // 画面確認用: 起動時に開くタブ
        if (!string.IsNullOrEmpty(tabEnv) && m_Tabs.ContainsKey(tabEnv))
        {
            if (m_ExpertTabButtons.Count > 0 && (tabEnv == "Contract" || tabEnv == "Train") && !m_ExpertShown) ToggleExpert();
            ShowTab(tabEnv);
        }
        string draftRun = Environment.GetEnvironmentVariable("SIM_DRAFT_AUTORUN");   // "URDF|契約の出力先"
        if (!string.IsNullOrEmpty(draftRun))
        {
            string[] p = draftRun.Split('|');
            if (m_DraftUrdf == null) BuildUi();
            if (!m_ExpertShown) ToggleExpert();
            ShowTab("Contract");
            m_DraftUrdf.text = p[0];
            if (p.Length > 1) m_ContractPath.text = p[1];
            DraftContract();
        }
        string taskRun = Environment.GetEnvironmentVariable(TaskAutorunEnvVar);
        if (!string.IsNullOrEmpty(taskRun))
        {
            string[] p = taskRun.Split('|');
            if (p.Length >= 3)
            {
                if (m_TaskContract == null) BuildUi();          // batchmode でも UI を組んで同じ経路を通す
                m_TaskContract.text = p[0]; m_TaskTrain.text = p[1]; m_DraftUrdf.text = p[2];
                m_TaskAutorun = !(p.Length > 3 && p[3] == "read");   // 4 つ目が read なら読むだけ (画面確認用)
                ReadTask();
            }
            else Debug.LogError($"[LabPanel] {TaskAutorunEnvVar} は '契約|学習設定|URDF' の形");
        }
        string shots = Environment.GetEnvironmentVariable(ScreenshotEnvVar);
        if (!string.IsNullOrEmpty(shots))
        {
            float delay = float.TryParse(Environment.GetEnvironmentVariable("SIM_GUI_SCREENSHOT_DELAY"), out float d) ? d : 8f;
            foreach (string item in shots.Split(','))
            {
                int at = item.LastIndexOf('@');
                if (at > 0 && item.Substring(at + 1) == "done") m_Shots.Add((item.Substring(0, at), float.PositiveInfinity));   // 学習完了の 3 秒後
                else if (at > 0 && float.TryParse(item.Substring(at + 1), out float t)) m_Shots.Add((item.Substring(0, at), Time.realtimeSinceStartup + t));
                else m_Shots.Add((item, Time.realtimeSinceStartup + delay));
            }
        }
        string deployRun = Environment.GetEnvironmentVariable(DeployAutorunEnvVar);
        if (!string.IsNullOrEmpty(deployRun))
        {
            string[] p = deployRun.Split('|');
            if (m_DepNs == null) BuildUi();
            ShowTab("Deploy");
            if (p.Length >= 3) m_DepNs.text = p[2];
            if (p.Length >= 4) SetDepMode(p[3] == "ros2_control_commands");
            StartDeploy(p[0], p.Length > 1 ? p[1] : null);
        }
        string checkRun = Environment.GetEnvironmentVariable(CheckAutorunEnvVar);
        if (!string.IsNullOrEmpty(checkRun))
        {
            if (m_ReportPath == null) BuildUi();
            m_ReportPath.text = checkRun;
            ShowTab("Check");
            LoadReport(checkRun);
        }
    }

    // ================================================================= Contract
    /// <summary>URDF から契約と学習設定の草案を作り、契約をエディタに読み込む。</summary>
    void DraftContract()
    {
        string urdf = m_DraftUrdf.text.Trim();
        if (urdf.Length == 0) { SetContractStatus("URDF path is empty"); return; }
        if (string.IsNullOrEmpty(m_ContractPath.text))
        {
            m_ContractPath.text = Path.Combine(Path.GetDirectoryName(urdf) ?? ".", Path.GetFileNameWithoutExtension(urdf) + "_contract.json");
        }
        m_Validate = Launch($"{ExternalProcess.UnirobolabPython()} -m unirobolab draft-contract {ExternalProcess.Quote(urdf)} --out {ExternalProcess.Quote(m_ContractPath.text)} 2>&1");
        m_LoadAfterValidate = true;
        SetContractStatus("drafting...");
    }

    bool m_LoadAfterValidate;
    TMP_InputField m_DraftUrdf;

    void LoadContract()
    {
        try { m_ContractText.text = File.ReadAllText(m_ContractPath.text); SetContractStatus("loaded"); }
        catch (Exception e) { SetContractStatus("load failed: " + e.Message); }
    }

    void SaveContract()
    {
        try { File.WriteAllText(m_ContractPath.text, m_ContractText.text); SetContractStatus("saved"); }
        catch (Exception e) { SetContractStatus("save failed: " + e.Message); }
    }

    void ValidateContract()
    {
        SaveContract();
        m_Validate = Launch($"{ExternalProcess.UnirobolabPython()} -m unirobolab show {ExternalProcess.Quote(m_ContractPath.text)} 2>&1");
        SetContractStatus("validating...");
    }

    void SetContractStatus(string s) { if (m_ContractStatus != null) m_ContractStatus.text = s; }

    // ==================================================================== Train
    public void StartTraining(string contract, string config, string urdf, string nEnvs, string outDir)
    {
        if (m_Train != null && !m_Train.HasExited) { SetTrainStatus("already training"); return; }
        int port = LearningPort();
        if (port <= 0) { SetTrainStatus("learning server disabled: set settings.learning_port"); return; }
        var cmd = new StringBuilder();
        cmd.Append(ExternalProcess.UnirobolabPython()).Append(" -u -m unirobolab train ")
           .Append(ExternalProcess.Quote(contract))
           .Append(" --config ").Append(ExternalProcess.Quote(config))
           .Append(" --out ").Append(ExternalProcess.Quote(outDir))
           .Append(" --transport direct --port ").Append(port)
           .Append(" --n-envs ").Append(string.IsNullOrEmpty(nEnvs) ? "1" : nEnvs.Trim());
        if (!string.IsNullOrEmpty(urdf)) cmd.Append(" --spawn-urdf ").Append(ExternalProcess.Quote(urdf)).Append(" --spawn-spacing 1.0");
        cmd.Append(" 2>&1");
        m_Train = Launch(cmd.ToString());
        m_CurvePoints = 0;
        m_StatusDir = outDir; m_TaskCurvePoints = 0; m_StatusNextAt = 0f;
        m_LastRunDir = outDir;
        SetTrainStatus("training started");
    }

    void StopTraining()
    {
        if (m_Train == null) return;
        m_Train.Stop(null, 2000);
        m_Train = null;
        SetTrainStatus("training stopped");
    }

    void SetTrainStatus(string s)
    {
        if (m_TrainStatus != null) m_TrainStatus.text = s;
        if (m_TaskStatus != null && s.StartsWith("training")) m_TaskStatus.text = s;   // 生のログ行はライト層に出さない
        if (Application.isBatchMode) Debug.Log("[LabPanel/train] " + s);
    }

    /// <summary>
    /// 学習中は 1 秒ごとに status.json を `unirobolab train-status` に通して平易な文にし、
    /// progress.csv から誤差 (青) と成功率 (緑、移動平均) の曲線を Task タブに描く。
    /// </summary>
    void PollTrainStatus()
    {
        if (string.IsNullOrEmpty(m_StatusDir)) return;
        if (m_StatusProc != null)
        {
            if (!m_StatusProc.HasExited) return;
            m_StatusProc.WaitForExit();
            var sb = new StringBuilder();
            while (m_StatusProc.TryDequeue(out string l)) sb.AppendLine(l);
            string text = sb.ToString().Trim();
            bool ok = m_StatusProc.ExitCode == 0;
            m_StatusProc = null;
            if (ok && text.Length > 0)
            {
                if (m_TaskProgress != null) m_TaskProgress.text = text;
                if (Application.isBatchMode) Debug.Log("[LabPanel/status] " + text.Replace('\n', ' '));
                if (text.StartsWith("学習が終わりました") || text.StartsWith("training finished"))
                {
                    m_StatusDir = null;
                    for (int i = 0; i < m_Shots.Count; i++) if (float.IsPositiveInfinity(m_Shots[i].at)) m_Shots[i] = (m_Shots[i].path, Time.realtimeSinceStartup + 3f);
                }
            }
            RedrawTaskCurve();
            return;
        }
        bool training = m_Train != null && !m_Train.HasExited;
        if (!training && m_StatusNextAt < 0f) return;          // 終了後に 1 回だけ読む
        if (Time.realtimeSinceStartup < m_StatusNextAt) return;
        m_StatusNextAt = training ? Time.realtimeSinceStartup + 1f : -1f;
        string path = Path.Combine(m_StatusDir, "status.json");
        if (!File.Exists(path)) return;
        m_StatusProc = Launch($"{ExternalProcess.UnirobolabPython()} -m unirobolab train-status {ExternalProcess.Quote(path)} --lang {(s_JapaneseFont ? "ja" : "en")} 2>&1");
    }

    void RedrawTaskCurve()
    {
        string dir = m_StatusDir ?? (m_TrainOut != null ? m_TrainOut.text : m_AutorunOut);
        if (string.IsNullOrEmpty(dir)) return;
        string path = Path.Combine(dir, "progress.csv");
        if (!File.Exists(path) || m_TaskCurveTex == null) return;
        var err = new List<float>(); var suc = new List<float>();
        try
        {
            using var r = new StreamReader(path);
            string header = r.ReadLine(); if (header == null) return;
            string[] cols = header.Split(',');
            int iErr = Array.IndexOf(cols, "final_abs_err"), iSuc = Array.IndexOf(cols, "success");
            string line;
            while ((line = r.ReadLine()) != null)
            {
                string[] f = line.Split(',');
                if (f.Length <= Math.Max(iErr, iSuc)) continue;
                if (float.TryParse(f[iErr], out float e)) { err.Add(e); suc.Add(iSuc >= 0 && float.TryParse(f[iSuc], out float sv) ? sv : 0f); }
            }
        }
        catch (IOException) { return; }
        if (err.Count == 0 || err.Count == m_TaskCurvePoints) return;
        m_TaskCurvePoints = err.Count;
        // 成功率は直近 (n/10, 最低 10) エピソードの移動平均
        int win = Mathf.Max(10, err.Count / 10);
        var rate = new List<float>(err.Count);
        float acc = 0f;
        for (int i = 0; i < suc.Count; i++) { acc += suc[i]; if (i >= win) acc -= suc[i - win]; rate.Add(acc / Mathf.Min(win, i + 1)); }
        int w = m_TaskCurveTex.width, h = m_TaskCurveTex.height;
        var px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(20, 20, 24, 255);
        void Plot(List<float> v, Color32 c, float lo, float hi)
        {
            if (hi - lo < 1e-6f) hi = lo + 1e-6f;
            for (int x = 0; x < w; x++)
            {
                int i0 = x * v.Count / w, i1 = Mathf.Min(v.Count, (x + 1) * v.Count / w);
                if (i1 <= i0) i1 = i0 + 1;
                float m = 0f; for (int i = i0; i < i1 && i < v.Count; i++) m += v[i]; m /= (i1 - i0);
                int y = Mathf.Clamp((int)((m - lo) / (hi - lo) * (h - 1)), 0, h - 1);
                px[y * w + x] = c; if (y > 0) px[(y - 1) * w + x] = c;
            }
        }
        float eHi = 0f; foreach (float e in err) eHi = Mathf.Max(eHi, e);
        Plot(rate, new Color32(90, 220, 120, 255), 0f, 1f);
        Plot(err, new Color32(90, 160, 255, 255), 0f, eHi);
        m_TaskCurveTex.SetPixels32(px); m_TaskCurveTex.Apply();
    }

    /// <summary>progress.csv の final_abs_err (青) と return (橙、正規化) をエピソード順に描く。</summary>
    void RedrawCurve(string outDir)
    {
        string path = Path.Combine(outDir, "progress.csv");
        if (!File.Exists(path)) return;
        var err = new List<float>(); var ret = new List<float>();
        try
        {
            using var r = new StreamReader(path);
            string header = r.ReadLine();
            if (header == null) return;
            string[] cols = header.Split(',');
            int iErr = Array.IndexOf(cols, "final_abs_err"), iRet = Array.IndexOf(cols, "return");
            string line;
            while ((line = r.ReadLine()) != null)
            {
                string[] f = line.Split(',');
                if (f.Length <= Math.Max(iErr, iRet)) continue;
                if (float.TryParse(f[iErr], out float e) && float.TryParse(f[iRet], out float rt)) { err.Add(e); ret.Add(rt); }
            }
        }
        catch (IOException) { return; }
        if (err.Count == 0 || err.Count == m_CurvePoints) return;
        m_CurvePoints = err.Count;
        if (Application.isBatchMode) Debug.Log($"[LabPanel/train] curve data: {err.Count} episodes, last final_abs_err {err[err.Count - 1]:F4}");
        if (m_CurveTex == null) return;
        int w = m_CurveTex.width, h = m_CurveTex.height;
        var px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(20, 20, 24, 255);
        void Plot(List<float> v, Color32 c)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (float x in v) { lo = Mathf.Min(lo, x); hi = Mathf.Max(hi, x); }
            if (hi - lo < 1e-6f) hi = lo + 1e-6f;
            for (int x = 0; x < w; x++)
            {
                int i0 = x * v.Count / w, i1 = Mathf.Min(v.Count, (x + 1) * v.Count / w);
                if (i1 <= i0) i1 = i0 + 1;
                float m = 0f; for (int i = i0; i < i1 && i < v.Count; i++) m += v[i]; m /= (i1 - i0);
                int y = Mathf.Clamp((int)((m - lo) / (hi - lo) * (h - 1)), 0, h - 1);
                px[y * w + x] = c;
                if (y > 0) px[(y - 1) * w + x] = c;
            }
        }
        Plot(ret, new Color32(255, 160, 60, 255));
        Plot(err, new Color32(90, 160, 255, 255));
        m_CurveTex.SetPixels32(px); m_CurveTex.Apply();
    }

    // ==================================================================== Check
    void LoadReport(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            m_ReportText.text = SummarizeReport(json);
            SetCheckStatus("loaded " + Path.GetFileName(Path.GetDirectoryName(path) ?? "") + "/" + Path.GetFileName(path));
            m_Explain?.Stop();
            m_Explain = Launch($"{ExternalProcess.UnirobolabPython()} -m unirobolab explain-report {ExternalProcess.Quote(path)} --lang {(s_JapaneseFont ? "ja" : "en")} 2>&1");
            if (m_PlainText != null) m_PlainText.text = "...";
        }
        catch (Exception e) { SetCheckStatus("load failed: " + e.Message); }
    }

    /// <summary>report.json の checks を "ok/FAIL 名前: detail" の行にする (最小限の JSON 走査)。</summary>
    static string SummarizeReport(string json)
    {
        var sb = new StringBuilder();
        int p = json.IndexOf("\"pass\"", StringComparison.Ordinal);
        sb.AppendLine(p >= 0 && json.Substring(p, Math.Min(20, json.Length - p)).Contains("true") ? "PASS" : "FAIL");
        int checks = json.IndexOf("\"checks\"", StringComparison.Ordinal);
        if (checks < 0) return sb.ToString();
        foreach (string name in new[] { "joints_present", "node_alive", "rate", "obs_fresh", "tracking", "finite", "estop" })
        {
            int i = json.IndexOf("\"" + name + "\"", checks, StringComparison.Ordinal);
            if (i < 0) continue;
            int pass = json.IndexOf("\"pass\"", i, StringComparison.Ordinal);
            bool ok = pass >= 0 && json.Substring(pass, Math.Min(16, json.Length - pass)).Contains("true");
            string detail = "";
            int d = json.IndexOf("\"detail\"", i, StringComparison.Ordinal);
            int nextName = json.IndexOf("\"pass\"", pass + 6, StringComparison.Ordinal);
            if (d >= 0 && (nextName < 0 || d < nextName))
            {
                int q1 = json.IndexOf('"', d + 8); int q2 = q1 >= 0 ? json.IndexOf('"', q1 + 1) : -1;
                if (q1 >= 0 && q2 > q1) detail = json.Substring(q1 + 1, q2 - q1 - 1);
            }
            sb.Append(ok ? "ok   " : "FAIL ").Append(name);
            if (detail.Length > 0) sb.Append(": ").Append(detail);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    void RunCheck()
    {
        string cmd = UniRoboLabConfig.Data.sim2sim_command;
        if (string.IsNullOrEmpty(cmd)) { SetCheckStatus("unirobolab.sim2sim_command is not set in the config"); return; }
        if (m_Check != null && !m_Check.HasExited) { SetCheckStatus("already running"); return; }
        FillCheckDefaults();
        if (string.IsNullOrEmpty(m_CheckContract.text)) { SetCheckStatus("set the contract (Task tab) first"); return; }
        string outDir = Path.GetDirectoryName(m_ReportPath.text);
        var full = new StringBuilder(cmd).Append(' ').Append(ExternalProcess.Quote(m_CheckContract.text))
            .Append(" --pkg ").Append(ExternalProcess.Quote(m_CheckPkg.text))
            .Append(string.IsNullOrEmpty(m_CheckScenario.text) ? "" : " --scenario " + ExternalProcess.Quote(m_CheckScenario.text))
            .Append(" --out ").Append(ExternalProcess.Quote(outDir)).Append(" 2>&1");
        m_Check = Launch(full.ToString());
        SetCheckStatus("sim2sim running...");
    }

    void SetCheckStatus(string s) { if (m_CheckStatus != null) m_CheckStatus.text = s; }

    /// <summary>ライトユーザーは契約だけ選ぶ: パッケージ名は gen の既定 (<name>_policy)、シナリオは契約の隣の
    /// <name>.sim2sim.json があれば使い、報告先は契約の隣の sim2sim_out/report.json。</summary>
    void FillCheckDefaults()
    {
        if (string.IsNullOrEmpty(m_CheckContract.text) && m_TaskContract != null) m_CheckContract.text = m_TaskContract.text;
        string c = m_CheckContract.text;
        if (string.IsNullOrEmpty(c)) return;
        string dir = Path.GetDirectoryName(c) ?? ".", stem = Path.GetFileNameWithoutExtension(c);
        if (string.IsNullOrEmpty(m_CheckPkg.text)) m_CheckPkg.text = stem + "_policy";
        if (string.IsNullOrEmpty(m_CheckScenario.text))
        {
            string sc = Path.Combine(dir, stem + ".sim2sim.json");
            if (File.Exists(sc)) m_CheckScenario.text = sc;
        }
        if (string.IsNullOrEmpty(m_ReportPath.text)) m_ReportPath.text = Path.Combine(dir, "sim2sim_out", "report.json");
    }

    // ================================================================= process
    ExternalProcess Launch(string cmd)
    {
        try { return new ExternalProcess(cmd, ExternalProcess.RunnerEnv()); }
        catch (Exception e) { Debug.LogError("[LabPanel] " + e.Message); return null; }
    }

    void Update()
    {
        string last;
        if (m_Validate != null)
        {
            var sb = new StringBuilder();
            while (m_Validate.TryDequeue(out last)) sb.AppendLine(last);
            if (sb.Length > 0) SetContractStatus(sb.ToString().TrimEnd());
            if (m_Validate.HasExited && m_Validate.ExitCode != 0 && m_ContractStatus != null && !m_ContractStatus.text.StartsWith("contract error"))
                SetContractStatus("invalid (exit " + m_Validate.ExitCode + "): " + m_ContractStatus.text);
            if (m_Validate.HasExited)
            {
                if (m_LoadAfterValidate && m_Validate.ExitCode == 0)
                {
                    LoadContract();
                    // 草案ができたら Task タブの契約欄にも入れる (次の一歩をすぐ踏めるように)
                    if (m_TaskContract != null && string.IsNullOrEmpty(m_TaskContract.text)) { m_TaskContract.text = m_ContractPath.text; m_TaskTrain.text = ""; }
                    SetContractStatus("drafted " + m_ContractPath.text + " (+ .train.json). Next: Task tab");
                }
                m_LoadAfterValidate = false;
                m_Validate = null;
            }
        }
        if (m_TaskProc != null)
        {
            var sb = new StringBuilder();
            while (m_TaskProc.TryDequeue(out last)) sb.AppendLine(last);
            string text = sb.ToString().Trim();
            if (text.Length > 0)
            {
                if (m_TaskReadPending && text.StartsWith("{")) ApplyTaskJson(text);
                else SetTaskStatus(text);
            }
            if (m_TaskProc.HasExited)
            {
                m_TaskProc.WaitForExit();                    // 非同期の残り行を確実に受け取る
                sb.Clear();
                while (m_TaskProc.TryDequeue(out last)) sb.AppendLine(last);
                text = sb.ToString().Trim();
                if (text.Length > 0) { if (m_TaskReadPending && text.StartsWith("{")) ApplyTaskJson(text); else SetTaskStatus(text); }
                bool ok = m_TaskProc.ExitCode == 0;
                bool wasRead = m_TaskReadPending;
                m_TaskReadPending = false;
                m_TaskProc = null;
                if (wasRead)
                {
                    if (m_TaskAutorun)
                    {
                        m_TaskAutorun = false;
                        if (ok) { m_TaskTol.value = 0.03f; m_TaskTime.value = 3f; SaveTask(); m_StartAfterSave = true; }
                        else Debug.LogError("[LabPanel/task] autorun: read failed");
                    }
                }
                else if (m_StartAfterSave)
                {
                    m_StartAfterSave = false;
                    if (ok) StartTrainingFromTask(); else SetTaskStatus("save failed; not training");
                }
            }
        }
        PollTrainStatus();
        PollDeploy();
        if (m_Train != null)
        {
            last = null;
            while (m_Train.TryDequeue(out string l)) last = l;
            if (last != null) SetTrainStatus(last);
            if (Time.unscaledTime > m_NextCurve)
            {
                m_NextCurve = Time.unscaledTime + 2f;
                RedrawCurve(m_TrainOut != null ? m_TrainOut.text : m_AutorunOut);
            }
            if (m_Train.HasExited) { SetTrainStatus("training finished (exit " + m_Train.ExitCode + ")"); RedrawCurve(m_TrainOut != null ? m_TrainOut.text : m_AutorunOut); m_Train = null; }
        }
        if (m_Check != null)
        {
            last = null;
            while (m_Check.TryDequeue(out string l)) last = l;
            if (last != null) SetCheckStatus(last);
            if (m_Check.HasExited) { LoadReport(m_ReportPath.text); m_Check = null; }
        }
        for (int i = m_Shots.Count - 1; i >= 0; i--)
        {
            if (Time.realtimeSinceStartup < m_Shots[i].at) continue;
            ScreenCapture.CaptureScreenshot(m_Shots[i].path);
            Debug.Log("[LabPanel] screenshot -> " + m_Shots[i].path);
            m_Shots.RemoveAt(i);
        }
        if (m_Explain != null && m_Explain.HasExited)
        {
            m_Explain.WaitForExit();
            var sb = new StringBuilder();
            while (m_Explain.TryDequeue(out string l)) sb.AppendLine(l);
            string text = sb.ToString().TrimEnd();
            if (m_PlainText != null) m_PlainText.text = text;
            if (Application.isBatchMode) foreach (string l in text.Split('\n')) Debug.Log("[LabPanel/check] " + l);
            m_Explain = null;
        }
    }

    string m_AutorunOut = "";

    static int LearningPort()
    {
        string fromEnv = Environment.GetEnvironmentVariable(SimulationControl.LearningPortEnvVar);
        int port = 0;
        if (!string.IsNullOrEmpty(fromEnv)) int.TryParse(fromEnv, out port);
        else if (SimulationResources.Settings != null) port = SimulationResources.Settings.learning_port;
        return port;
    }

    void OnApplicationQuit() { StopTraining(); m_Check?.Stop(); m_Validate?.Stop(); m_Explain?.Stop(); m_TaskProc?.Stop(); m_StatusProc?.Stop(); m_DepProc?.Stop(); }

    // ======================================================================= ui
    void BuildUi()
    {
        EnsureJapaneseFont();
        var canvasGo = new GameObject("LabPanelCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 5;
        RectTransform canvasRt = canvasGo.GetComponent<RectTransform>();

        var panel = new GameObject("LabPanel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        var rt = panel.GetComponent<RectTransform>();
        rt.SetParent(canvasRt, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f); rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(12f, 305f);
        rt.sizeDelta = new Vector2(440f, 455f);
        panel.GetComponent<Image>().color = PanelColor;
        var layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 8, 8); layout.spacing = 4f;
        layout.childForceExpandHeight = false; layout.childControlHeight = true; layout.childControlWidth = true;

        Label(rt, "UniRoboLab", 18f, HeaderColor);
        var tabRow = Row(rt, 28f);
        Btn(tabRow.transform, "Task", () => ShowTab("Task"));
        Btn(tabRow.transform, "Check", () => ShowTab("Check"));
        Btn(tabRow.transform, "Deploy", () => ShowTab("Deploy"));
        foreach (string name in new[] { "Contract", "Train" })
        {
            string n = name;
            Button b = Btn(tabRow.transform, n, () => ShowTab(n));
            b.gameObject.SetActive(false);              // expert layer: hidden until "詳細"
            m_ExpertTabButtons.Add(b.gameObject);
        }
        Btn(tabRow.transform, s_JapaneseFont ? "詳細" : "Expert", ToggleExpert);
        m_Tabs["Task"] = BuildTaskTab(rt);
        m_Tabs["Contract"] = BuildContractTab(rt);
        m_Tabs["Train"] = BuildTrainTab(rt);
        m_Tabs["Check"] = BuildCheckTab(rt);
        m_Tabs["Deploy"] = BuildDeployTab(rt);
        ShowTab("Task");
    }

    void ShowTab(string name)
    {
        foreach (var kv in m_Tabs) kv.Value.SetActive(kv.Key == name);
    }

    /// <summary>専門家向けのタブ (Contract / Train / Check) の表示を切り替える。隠すだけで消さない。</summary>
    void ToggleExpert()
    {
        m_ExpertShown = !m_ExpertShown;
        foreach (GameObject b in m_ExpertTabButtons) b.SetActive(m_ExpertShown);
        if (m_CheckExpert != null) m_CheckExpert.SetActive(m_ExpertShown);
        if (m_TaskExpert != null) m_TaskExpert.SetActive(m_ExpertShown);
        if (!m_ExpertShown && !m_Tabs["Check"].activeSelf && !m_Tabs["Deploy"].activeSelf) ShowTab("Task");
    }

    // =================================================================== Deploy
    TMP_InputField m_DepNs, m_DepController, m_DepEstop, m_DepOut;
    TMP_Text m_DepMode, m_DepGuide, m_DepStatus;
    bool m_DepRos2Control;
    ExternalProcess m_DepProc;
    int m_DepStep;            // 0 idle, 1 ros-set, 2 gen, 3 guide
    string m_DepPkgDir;
    public const string DeployAutorunEnvVar = "SIM_DEPLOY_AUTORUN";   // "契約|出力ディレクトリ" でヘッドレス確認

    /// <summary>実機へ: 接続先のフォーム → ROS 2 パッケージ生成 → 手順書。JSON の編集は ros-set に任せる。</summary>
    static TMP_Text FormLabel(Transform row, string text)
    {
        TMP_Text l = Label(row, text, 12f, TextColor);
        var le = l.GetComponent<LayoutElement>(); le.preferredWidth = 96f; le.flexibleWidth = 0f;
        return l;
    }

    GameObject BuildDeployTab(RectTransform parent)
    {
        GameObject tab = Column(parent, "DeployTab");
        Label(tab.transform, "Real robot: where to connect (contract from the Task tab)", 12f, TextColor);
        var r1 = Row(tab.transform, 26f);
        FormLabel(r1.transform, "namespace"); m_DepNs = Input(r1.transform, "robot", 24f);
        var r2 = Row(tab.transform, 26f);
        m_DepMode = Label(r2.transform, "command: joint_states topic", 12f, TextColor);
        Btn(r2.transform, "switch", () => SetDepMode(!m_DepRos2Control));
        var r3 = Row(tab.transform, 26f);
        FormLabel(r3.transform, "controller"); m_DepController = Input(r3.transform, "(ros2_control) joint_group_position_controller", 24f);
        var r4 = Row(tab.transform, 26f);
        FormLabel(r4.transform, "e-stop topic"); m_DepEstop = Input(r4.transform, "(auto) /<namespace>/estop", 24f);
        var r5 = Row(tab.transform, 26f);
        FormLabel(r5.transform, "package dir"); m_DepOut = Input(r5.transform, "(auto) <contract dir>/deploy", 24f);
        var r6 = Row(tab.transform, 28f);
        Btn(r6.transform, "Make ROS 2 package", () => StartDeploy(null, null));
        Btn(r6.transform, "Show guide", ShowGuideOnly);
        m_DepGuide = Label(tab.transform, "", 11f, TextColor);
        m_DepGuide.enableWordWrapping = true; m_DepGuide.GetComponent<LayoutElement>().preferredHeight = 150f;
        m_DepGuide.alignment = TextAlignmentOptions.TopLeft; m_DepGuide.overflowMode = TextOverflowModes.Truncate;
        m_DepStatus = Label(tab.transform, "fill in the namespace, then Make ROS 2 package", 12f, TextColor);
        m_DepStatus.enableWordWrapping = true; m_DepStatus.GetComponent<LayoutElement>().preferredHeight = 34f;
        SetDepMode(false);
        return tab;
    }

    void SetDepMode(bool ros2Control)
    {
        m_DepRos2Control = ros2Control;
        m_DepMode.text = ros2Control ? "command: ros2_control controller" : "command: joint_states topic";
        m_DepController.interactable = ros2Control;
    }

    string DepContract() => TaskContract;

    string DepOutDir()
    {
        if (!string.IsNullOrEmpty(m_DepOut.text)) return m_DepOut.text;
        string c = DepContract();
        return Path.Combine(Path.GetDirectoryName(c) ?? ".", "deploy");
    }

    /// <summary>ros-set → gen → deploy-guide を順に走らせる。contract/outDir を渡すと autorun。</summary>
    void StartDeploy(string contract, string outDir)
    {
        if (!string.IsNullOrEmpty(contract)) { if (m_TaskContract != null) m_TaskContract.text = contract; if (m_DepOut != null) m_DepOut.text = outDir; }
        string c = DepContract();
        if (string.IsNullOrEmpty(c)) { SetDepStatus("pick a contract in the Task tab first"); return; }
        if (m_DepProc != null && !m_DepProc.HasExited) { SetDepStatus("busy"); return; }
        var cmd = new StringBuilder();
        cmd.Append(ExternalProcess.UnirobolabPython()).Append(" -m unirobolab ros-set ").Append(ExternalProcess.Quote(c));
        if (!string.IsNullOrEmpty(m_DepNs.text)) cmd.Append(" --namespace ").Append(ExternalProcess.Quote(m_DepNs.text));
        cmd.Append(" --command-mode ").Append(m_DepRos2Control ? "ros2_control_commands" : "joint_state_topic");
        if (m_DepRos2Control && !string.IsNullOrEmpty(m_DepController.text)) cmd.Append(" --controller ").Append(ExternalProcess.Quote(m_DepController.text));
        if (!string.IsNullOrEmpty(m_DepEstop.text)) cmd.Append(" --estop-topic ").Append(ExternalProcess.Quote(m_DepEstop.text));
        cmd.Append(" 2>&1");
        m_DepStep = 1;
        m_DepProc = Launch(cmd.ToString());
        SetDepStatus("1/3 writing the connection settings into the contract...");
    }

    void ShowGuideOnly()
    {
        string c = DepContract();
        if (string.IsNullOrEmpty(c)) { SetDepStatus("pick a contract in the Task tab first"); return; }
        if (m_DepProc != null && !m_DepProc.HasExited) { SetDepStatus("busy"); return; }
        m_DepStep = 3;
        m_DepProc = Launch($"{ExternalProcess.UnirobolabPython()} -m unirobolab deploy-guide {ExternalProcess.Quote(c)} --lang {(s_JapaneseFont ? "ja" : "en")} --summary 2>&1");
    }

    void PollDeploy()
    {
        if (m_DepProc == null || !m_DepProc.HasExited) return;
        m_DepProc.WaitForExit();
        var sb = new StringBuilder();
        while (m_DepProc.TryDequeue(out string l)) sb.AppendLine(l);
        string text = sb.ToString().Trim();
        bool ok = m_DepProc.ExitCode == 0;
        m_DepProc = null;
        string c = DepContract();
        if (!ok) { SetDepStatus("failed: " + text); m_DepStep = 0; return; }
        if (m_DepStep == 1)
        {
            m_DepStep = 2;
            m_DepProc = Launch($"{ExternalProcess.UnirobolabPython()} -m unirobolab gen {ExternalProcess.Quote(c)} --out {ExternalProcess.Quote(DepOutDir())} --overwrite 2>&1");
            SetDepStatus("2/3 generating the ROS 2 package...");
        }
        else if (m_DepStep == 2)
        {
            // "generated <dir>" → 手順書を DEPLOY.md としてパッケージに置き、画面にも出す
            m_DepPkgDir = text.StartsWith("generated ") ? text.Substring(10).Trim() : DepOutDir();
            string pkg = Path.GetFileName(m_DepPkgDir.TrimEnd('/'));
            m_DepStep = 3;
            string py = ExternalProcess.UnirobolabPython(), lang = s_JapaneseFont ? "ja" : "en";
            m_DepProc = Launch($"{py} -m unirobolab deploy-guide {ExternalProcess.Quote(c)} --package {ExternalProcess.Quote(pkg)} --lang {lang} --out {ExternalProcess.Quote(Path.Combine(m_DepPkgDir, "DEPLOY.md"))} 2>&1 && {py} -m unirobolab deploy-guide {ExternalProcess.Quote(c)} --package {ExternalProcess.Quote(pkg)} --lang {lang} --summary 2>&1");
            SetDepStatus("3/3 writing the guide...");
        }
        else
        {
            m_DepStep = 0;
            string guide = text.StartsWith("wrote ") ? text.Substring(text.IndexOf('\n') + 1) : text;
            if (m_DepGuide != null) m_DepGuide.text = guide;
            SetDepStatus(string.IsNullOrEmpty(m_DepPkgDir) ? "guide shown" : "package ready: " + m_DepPkgDir + " (DEPLOY.md inside)");
            if (Application.isBatchMode) foreach (string l in guide.Split('\n')) Debug.Log("[LabPanel/deploy] " + l);
            m_DepPkgDir = null;
        }
    }

    void SetDepStatus(string s)
    {
        if (m_DepStatus != null) m_DepStatus.text = s;
        if (Application.isBatchMode) Debug.Log("[LabPanel/deploy] " + s);
    }

    // ===================================================================== Task
    GameObject BuildTaskTab(RectTransform parent)
    {
        GameObject tab = Column(parent, "TaskTab");
        Label(tab.transform, "1. Robot: contract (Draft from URDF in 詳細 > Contract)", 12f, TextColor);
        m_TaskContract = Input(tab.transform, "/path/to/contract.json", 24f);
        m_TaskExpert = Column(tab.transform, "TaskExpert");
        m_TaskExpert.GetComponent<LayoutElement>().flexibleHeight = 0f;
        Label(m_TaskExpert.transform, "2. Task settings (saved next to the contract as *.train.json)", 12f, TextColor);
        m_TaskTrain = Input(m_TaskExpert.transform, "(auto) /path/to/contract.train.json", 24f);
        m_TaskExpert.SetActive(false);
        var row = Row(tab.transform, 26f);
        Btn(row.transform, "Read", ReadTask);
        m_TaskType = Label(row.transform, "type: -", 12f, TextColor);
        m_TaskGoalLabel = Label(tab.transform, "Goal range", 12f, TextColor);
        m_TaskGoal = SliderRow(tab.transform, 0.05f, 3.0f, 0.5f, v => m_TaskGoalLabel.text = m_TaskIsBase ? $"Nearest goal: {v:F2} m" : $"Goal range: +-{v:F2} rad");
        m_TaskGoalMaxLabel = Label(tab.transform, "Farthest goal", 12f, TextColor);
        m_TaskGoalMax = SliderRow(tab.transform, 0.2f, 5.0f, 2.5f, v => m_TaskGoalMaxLabel.text = $"Farthest goal: {v:F2} m");
        m_TaskGoalMaxRow = m_TaskGoalMax.gameObject;
        m_TaskTolLabel = Label(tab.transform, "Success tolerance", 12f, TextColor);
        m_TaskTol = SliderRow(tab.transform, 0.01f, 0.5f, 0.05f, v => m_TaskTolLabel.text = m_TaskIsBase ? $"Success: within {v:F2} m" : $"Success: within {v:F3} rad");
        m_TaskTimeLabel = Label(tab.transform, "Time per attempt", 12f, TextColor);
        m_TaskTime = SliderRow(tab.transform, 0.5f, 20f, 2f, v => m_TaskTimeLabel.text = $"Time per attempt: {v:F1} s");
        var row2 = Row(tab.transform, 28f);
        Btn(row2.transform, "Save task", SaveTask);
        Btn(row2.transform, "Save & train", () => { SaveTask(); m_StartAfterSave = true; });
        Btn(row2.transform, "Stop", StopTraining);
        m_TaskStatus = Label(tab.transform, "Pick a contract, Read, adjust, Save & train", 12f, TextColor);
        m_TaskStatus.enableWordWrapping = true; m_TaskStatus.GetComponent<LayoutElement>().preferredHeight = 22f;
        m_TaskProgress = Label(tab.transform, "", 12f, TextColor);
        m_TaskProgress.enableWordWrapping = true; m_TaskProgress.GetComponent<LayoutElement>().preferredHeight = 46f;
        var img = new GameObject("TaskCurve", typeof(RectTransform), typeof(RawImage), typeof(LayoutElement));
        img.transform.SetParent(tab.transform, false);
        img.GetComponent<LayoutElement>().preferredHeight = 64f;
        m_TaskCurveTex = new Texture2D(400, 64, TextureFormat.RGBA32, false);
        { var bg = new Color32[400 * 64]; for (int i = 0; i < bg.Length; i++) bg[i] = new Color32(20, 20, 24, 255); m_TaskCurveTex.SetPixels32(bg); m_TaskCurveTex.Apply(); }
        m_TaskCurve = img.GetComponent<RawImage>(); m_TaskCurve.texture = m_TaskCurveTex;
        Label(tab.transform, "blue: error per attempt   green: success rate", 10f, TextColor);
        SetBaseTask(false);
        return tab;
    }

    bool m_StartAfterSave;

    // 学習の進み具合 (status.json): 成功率・誤差・残り時間・次の一手。Task タブに出す
    TMP_Text m_TaskProgress;
    RawImage m_TaskCurve;
    Texture2D m_TaskCurveTex;
    ExternalProcess m_StatusProc;
    float m_StatusNextAt;
    string m_StatusDir;
    int m_TaskCurvePoints;

    void SetBaseTask(bool isBase)
    {
        m_TaskIsBase = isBase;
        m_TaskGoalMaxRow.SetActive(isBase); m_TaskGoalMaxLabel.gameObject.SetActive(isBase);
        m_TaskGoal.minValue = isBase ? 0.2f : 0.05f; m_TaskGoal.maxValue = isBase ? 5f : 3f;
        m_TaskTol.minValue = isBase ? 0.05f : 0.005f; m_TaskTol.maxValue = isBase ? 1f : 0.5f;
        m_TaskGoal.onValueChanged.Invoke(m_TaskGoal.value); m_TaskTol.onValueChanged.Invoke(m_TaskTol.value);
        m_TaskType.text = isBase ? "type: reach a point (mobile base)" : "type: joint targets (fixed base)";
    }

    string TaskTrainPath()
    {
        if (!string.IsNullOrEmpty(m_TaskTrain.text)) return m_TaskTrain.text;
        string c = m_TaskContract.text;
        return Path.Combine(Path.GetDirectoryName(c) ?? ".", Path.GetFileNameWithoutExtension(c) + ".train.json");
    }

    void ReadTask()
    {
        if (string.IsNullOrEmpty(m_TaskContract.text)) { SetTaskStatus("contract path is empty"); return; }
        m_TaskTrain.text = TaskTrainPath();
        m_TaskProc = Launch($"{ExternalProcess.UnirobolabPython()} -m unirobolab task-show {ExternalProcess.Quote(m_TaskContract.text)} --train {ExternalProcess.Quote(m_TaskTrain.text)} 2>&1");
        m_TaskReadPending = true;
        SetTaskStatus("reading...");
    }

    /// <summary>task-show の 1 行 JSON から数値を拾う (最小限の走査)。</summary>
    void ApplyTaskJson(string json)
    {
        float Num(string key, float fallback)
        {
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return fallback;
            int c = json.IndexOf(':', i); int e = c + 1;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '.' || json[e] == '-' || json[e] == 'e' || json[e] == ' ')) e++;
            return float.TryParse(json.Substring(c + 1, e - c - 1).Trim(), out float v) ? v : fallback;
        }
        bool isBase = json.Contains("base_target");
        SetBaseTask(isBase);
        int oi = json.IndexOf("\"onnx\"", StringComparison.Ordinal);
        m_TaskOnnxDir = null;
        if (oi >= 0)
        {
            int q1 = json.IndexOf('"', json.IndexOf(':', oi)); int q2 = q1 >= 0 ? json.IndexOf('"', q1 + 1) : -1;
            if (q1 >= 0 && q2 > q1) m_TaskOnnxDir = Path.GetDirectoryName(json.Substring(q1 + 1, q2 - q1 - 1));
        }
        if (isBase) { m_TaskGoal.value = Num("goal_min", 1f); m_TaskGoalMax.value = Num("goal_max", 2.5f); }
        else m_TaskGoal.value = Num("goal_range", 0.5f);
        m_TaskTol.value = Num("tolerance", isBase ? 0.15f : 0.05f);
        m_TaskTime.value = Num("time_s", 2f);
        SetTaskStatus("read " + m_TaskTrain.text);
    }

    void SaveTask()
    {
        if (string.IsNullOrEmpty(m_TaskContract.text)) { SetTaskStatus("contract path is empty"); return; }
        m_TaskTrain.text = TaskTrainPath();
        var cmd = new StringBuilder();
        cmd.Append(ExternalProcess.UnirobolabPython()).Append(" -m unirobolab task-set ")
           .Append(ExternalProcess.Quote(m_TaskContract.text)).Append(" --train ").Append(ExternalProcess.Quote(m_TaskTrain.text))
           .Append(" --tolerance ").Append(m_TaskTol.value.ToString("F4")).Append(" --time ").Append(m_TaskTime.value.ToString("F2"));
        if (m_TaskIsBase) cmd.Append(" --goal-min ").Append(m_TaskGoal.value.ToString("F3")).Append(" --goal-max ").Append(m_TaskGoalMax.value.ToString("F3"));
        else cmd.Append(" --goal-range ").Append(m_TaskGoal.value.ToString("F3"));
        cmd.Append(" 2>&1");
        m_TaskProc = Launch(cmd.ToString());
        SetTaskStatus("saving...");
    }

    void StartTrainingFromTask()
    {
        string contract = m_TaskContract.text;
        // 契約が policy.onnx の場所を宣言していればそこへ (gen / sim2sim / Try がそのまま見つけられる)
        string outDir = !string.IsNullOrEmpty(m_TaskOnnxDir) ? m_TaskOnnxDir
            : Path.Combine(Path.GetDirectoryName(contract) ?? ".", "runs", Path.GetFileNameWithoutExtension(contract));
        string urdf = m_DraftUrdf != null ? m_DraftUrdf.text : "";
        StartTraining(contract, TaskTrainPath(), urdf, "8", outDir);
        if (m_TrainOut != null) m_TrainOut.text = outDir;
        m_StatusDir = outDir; m_TaskCurvePoints = 0; m_StatusNextAt = 0f;
        SetTaskStatus("training started (progress and curve under 詳細 > Train)");
    }

    void SetTaskStatus(string s)
    {
        if (m_TaskStatus != null) m_TaskStatus.text = s;
        if (Application.isBatchMode) Debug.Log("[LabPanel/task] " + s);
    }

    static Slider SliderRow(Transform parent, float min, float max, float value, UnityEngine.Events.UnityAction<float> onChange)
    {
        GameObject go = DefaultControls.CreateSlider(new DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.layoutPriority = 2; le.flexibleHeight = 0f; le.preferredHeight = 20f;
        var sl = go.GetComponent<Slider>();
        sl.minValue = min; sl.maxValue = max; sl.value = value;
        sl.onValueChanged.AddListener(onChange);
        onChange(value);
        return sl;
    }

    GameObject BuildContractTab(RectTransform parent)
    {
        GameObject tab = Column(parent, "ContractTab");
        Label(tab.transform, "URDF -> draft a contract and a task (the light-user entry point)", 12f, TextColor);
        m_DraftUrdf = Input(tab.transform, "/path/to/robot.urdf", 24f);
        var drow = Row(tab.transform, 28f);
        Btn(drow.transform, "Draft from URDF", DraftContract);
        Label(tab.transform, "Contract JSON path", 12f, TextColor);
        m_ContractPath = Input(tab.transform, "/path/to/contract.json", 26f);
        var row = Row(tab.transform, 28f);
        Btn(row.transform, "Load", LoadContract); Btn(row.transform, "Save", SaveContract); Btn(row.transform, "Validate", ValidateContract);
        m_ContractText = Input(tab.transform, "{ ... }", 170f);
        m_ContractText.lineType = TMP_InputField.LineType.MultiLineNewline;
        m_ContractText.textComponent.alignment = TextAlignmentOptions.TopLeft;
        m_ContractStatus = Label(tab.transform, "idle", 12f, TextColor);
        m_ContractStatus.enableWordWrapping = true; m_ContractStatus.GetComponent<LayoutElement>().preferredHeight = 60f;
        return tab;
    }

    GameObject BuildTrainTab(RectTransform parent)
    {
        GameObject tab = Column(parent, "TrainTab");
        Label(tab.transform, "Contract / train config / URDF to spawn", 12f, TextColor);
        m_TrainContract = Input(tab.transform, "/path/to/contract.json", 24f);
        m_TrainConfig = Input(tab.transform, "/path/to/train.json", 24f);
        m_TrainUrdf = Input(tab.transform, "/path/to/robot.urdf (blank = robots already spawned)", 24f);
        var row = Row(tab.transform, 24f);
        m_TrainEnvs = Input(row.transform, "robots (16)", 24f); m_TrainEnvs.text = "16";
        m_TrainOut = Input(row.transform, "/path/to/run dir", 24f);
        var row2 = Row(tab.transform, 28f);
        Btn(row2.transform, "Start", () => StartTraining(m_TrainContract.text, m_TrainConfig.text, m_TrainUrdf.text, m_TrainEnvs.text, m_TrainOut.text));
        Btn(row2.transform, "Stop", StopTraining);
        var img = new GameObject("Curve", typeof(RectTransform), typeof(RawImage), typeof(LayoutElement));
        img.transform.SetParent(tab.transform, false);
        img.GetComponent<LayoutElement>().preferredHeight = 130f;
        m_CurveTex = new Texture2D(400, 130, TextureFormat.RGBA32, false);
        m_Curve = img.GetComponent<RawImage>(); m_Curve.texture = m_CurveTex;
        Label(tab.transform, "blue: final |error| per episode, orange: return", 11f, TextColor);
        m_TrainStatus = Label(tab.transform, LearningPort() > 0 ? "idle" : "learning server disabled (settings.learning_port)", 12f, TextColor);
        m_TrainStatus.enableWordWrapping = true; m_TrainStatus.GetComponent<LayoutElement>().preferredHeight = 44f;
        return tab;
    }

    GameObject BuildCheckTab(RectTransform parent)
    {
        GameObject tab = Column(parent, "CheckTab");
        Label(tab.transform, "Deploy check (sim2sim): report.json", 12f, TextColor);
        m_ReportPath = Input(tab.transform, "(auto) <contract dir>/sim2sim_out/report.json", 24f);
        var row = Row(tab.transform, 28f);
        Btn(row.transform, "Run check", RunCheck);
        Btn(row.transform, "Load report", () => LoadReport(m_ReportPath.text));
        m_PlainText = Label(tab.transform, "", 12f, TextColor);
        m_PlainText.enableWordWrapping = true; m_PlainText.GetComponent<LayoutElement>().preferredHeight = 260f;
        m_PlainText.alignment = TextAlignmentOptions.TopLeft;
        m_CheckExpert = Column(tab.transform, "CheckExpert");
        Label(m_CheckExpert.transform, "Run: contract / package / scenario (needs settings.sim2sim_command)", 11f, TextColor);
        m_CheckContract = Input(m_CheckExpert.transform, "(auto) contract from the Task tab", 24f);
        m_CheckPkg = Input(m_CheckExpert.transform, "(auto) <contract name>_policy", 24f);
        m_CheckScenario = Input(m_CheckExpert.transform, "(auto) <contract name>.sim2sim.json if present", 24f);
        m_ReportText = Label(m_CheckExpert.transform, "", 11f, TextColor);
        m_ReportText.enableWordWrapping = true; m_ReportText.GetComponent<LayoutElement>().preferredHeight = 120f;
        m_ReportText.alignment = TextAlignmentOptions.TopLeft;
        m_CheckExpert.SetActive(false);
        m_CheckStatus = Label(tab.transform, "idle", 12f, TextColor);
        m_CheckStatus.enableWordWrapping = true; m_CheckStatus.GetComponent<LayoutElement>().preferredHeight = 40f;
        return tab;
    }

    static GameObject Column(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var l = go.GetComponent<VerticalLayoutGroup>(); l.spacing = 4f;
        l.childForceExpandHeight = false; l.childControlHeight = true; l.childControlWidth = true;
        go.GetComponent<LayoutElement>().flexibleHeight = 1f;
        return go;
    }

    static GameObject Row(Transform parent, float height)
    {
        var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
        var hl = go.GetComponent<HorizontalLayoutGroup>(); hl.spacing = 6f; hl.childForceExpandWidth = true;
        hl.childControlHeight = true; hl.childControlWidth = true;
        return go;
    }

    static TMP_Text Label(Transform parent, string text, float size, Color color)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = size + 8f;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text; tmp.fontSize = size; tmp.color = color; tmp.enableWordWrapping = false;
        return tmp;
    }

    static TMP_InputField Input(Transform parent, string placeholder, float height)
    {
        GameObject go = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.layoutPriority = 2; le.flexibleHeight = 0f; le.preferredHeight = height;
        var field = go.GetComponent<TMP_InputField>();
        field.pointSize = 12f;
        if (field.placeholder is TMP_Text ph) { ph.text = placeholder; ph.fontSize = 12f; }
        return field;
    }

    static Button Btn(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
    {
        GameObject go = DefaultControls.CreateButton(new DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = TabColor;
        var text = go.GetComponentInChildren<Text>();
        if (text != null) { text.text = label; text.fontSize = 13; text.color = Color.white; }
        var button = go.GetComponent<Button>();
        button.onClick.AddListener(onClick);
        return button;
    }
}
