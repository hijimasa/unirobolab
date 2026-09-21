using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// ⑤ チェック (配備前): この GUI のシミュレータ自身を被検体に、ROS 2 の経路で方策を動かして判定する。
/// ROS 側の手順 (scripts/check_runner.sh: パッケージ生成 → ビルド → スポーン → 方策ノード → 判定) は
/// ホストの ROS 2 か、コンテナ (docker exec) で走らせ、"##STEP n/5" 行を段階表示にする。
/// </summary>
public class PhaseCheck : Phase
{
    public override string Key => "check";
    public override string Title => Ui.T("⑤ チェック", "5 Check");
    public override LabWizard.PreviewMode Preview => LabWizard.PreviewMode.Side;

    TMP_Text m_Env, m_List, m_Raw, m_Log;
    Button m_Run, m_Stop, m_Container;
    readonly List<TMP_Text> m_Steps = new List<TMP_Text>();
    ExternalProcess m_Runner, m_Explain, m_ContainerProc, m_RosProbe;
    string m_OutDir, m_ReportPath;
    long m_ReportWriteTicksBeforeRun;
    bool m_StampReportOnExplain;
    float m_SavedScale = -1f;
    string m_EnvShown = "";
    readonly StringBuilder m_LogBuf = new StringBuilder();
    static readonly string[] k_StepsJa = { "ROS 2 パッケージを生成", "パッケージをビルド", "シミュレータに接続してロボットをスポーン", "ROS 2 経由で方策を動かして判定 (非常停止の試験を含む)", "完了" };
    static readonly string[] k_StepsEn = { "generate the ROS 2 package", "build the package", "connect to the simulator and spawn the robot", "run the policy through ROS 2 and judge (incl. e-stop test)", "done" };

    public override void Build(RectTransform main, RectTransform details)
    {
        Root = Ui.Column(main, "Check", 6f);
        Ui.Label(Root.transform, Ui.T("実機に持っていく前に、この画面のシミュレータを相手に ROS 2 の経路で方策を動かして確かめます。", "Before the real robot, the policy is run through the ROS 2 path against this simulator."), 12f, Ui.Muted, true, 34f);
        m_Env = Ui.Label(Root.transform, "", 12f, Ui.Text, true, 40f);
        var r = Ui.Row(Root.transform, 32f);
        m_Run = Ui.Btn(r.transform, Ui.T("▶ チェックを実行", "▶ Run the check"), RunCheck, 0f, 30f); Ui.SetBtn(m_Run, null, Ui.BtnActive);
        m_Stop = Ui.Btn(r.transform, Ui.T("■ 中止", "■ Abort"), Abort, 90f, 30f);
        m_Container = Ui.Btn(r.transform, Ui.T("コンテナを起動", "Start container"), StartContainer, 140f, 30f); m_Container.gameObject.SetActive(false);
        for (int i = 0; i < 5; i++) m_Steps.Add(Ui.Label(Root.transform, "", 12f, Ui.Muted, false, 20f));
        m_List = Ui.Label(Root.transform, "", 13f, Ui.Text, true, 230f); m_List.alignment = TextAlignmentOptions.TopLeft;
        Ui.Spacer(Root.transform);
        DetailsRoot = Ui.Column(details, "CheckDetails", 4f);
        Ui.Label(DetailsRoot.transform, Ui.T("実行ログ", "Runner log"), 12f, Ui.Muted);
        m_Log = Ui.Label(DetailsRoot.transform, "", 10f, Ui.Text, true, 150f); m_Log.alignment = TextAlignmentOptions.BottomLeft;
        Ui.Label(DetailsRoot.transform, "report.json", 12f, Ui.Muted);
        m_Raw = Ui.Label(DetailsRoot.transform, "", 10f, Ui.Text, true, 0f); m_Raw.GetComponent<LayoutElement>().flexibleHeight = 1f; m_Raw.alignment = TextAlignmentOptions.TopLeft;
    }

    public override bool CanEnter(out string reason) { reason = Ui.T("先に ③ で学習してください", "train in step 3 first"); return P.Exists("run"); }

    public override void Enter()
    {
        m_OutDir = Path.Combine(P.Dir, "sim2sim_out");
        m_ReportPath = Path.Combine(m_OutDir, "report.json");
        RefreshEnv();
        ResetSteps();
        if (File.Exists(m_ReportPath) && P.Exists("report")) LoadReport(false); else m_List.text = Ui.T("(まだ結果がありません)", "(no result yet)");
        if (P.IsStale("report")) m_List.text = Ui.T("! タスク、契約、方策、または結果が変わっています。チェックし直してください\n", "! the task, contract, policy, or result changed; check again\n") + m_List.text;
        W.Status(Ui.T("「チェックを実行」を押してください", "Press Run the check"));
    }

    void RefreshEnv()
    {
        bool canRun = Env.Ros2 == "host" || Env.Ros2 == "container";
        m_Env.text = Env.Ros2 switch
        {
            "host" => Ui.T("ROS 2: この PC で実行します", "ROS 2: runs on this machine"),
            "container" => Ui.T("ROS 2: コンテナ (unirobolab-sim2sim) で実行します。選択中のプロジェクトだけをコンテナと共有します", "ROS 2: runs in the unirobolab-sim2sim container (only the current project is shared with it)"),
            "docker-only" => Ui.T("ROS 2: 無し。コンテナを起動すると実行できます (初回はイメージの作成に 10 分ほどかかります)", "ROS 2: none; start the container to run the check (the first start builds the image, about 10 minutes)"),
            _ => Ui.T("ROS 2 も Docker も見つかりません。チェックは ROS 2 のある環境で実行してください", "Neither ROS 2 nor Docker found; run the check where ROS 2 is available"),
        };
        m_Env.color = canRun ? Ui.Text : Ui.Warn;
        m_Run.interactable = canRun && (m_Runner == null || m_Runner.HasExited);
        m_Container.gameObject.SetActive(Env.Ros2 == "docker-only");
    }

    void ResetSteps()
    {
        for (int i = 0; i < 5; i++) { m_Steps[i].text = $"   {i + 1}. " + (Ui.Japanese ? k_StepsJa[i] : k_StepsEn[i]); m_Steps[i].color = Ui.Muted; }
    }

    void RunCheck()
    {
        if (m_Runner != null && !m_Runner.HasExited) return;
        string contract = P.Abs(P.D.contract), onnx = P.OnnxPath, urdf = P.Abs(P.D.urdf), task = P.Abs(P.D.task), outDir = m_OutDir;
        // 被検体はこのシミュレータ: 学習/試す で置いたロボットを消し、実時間に戻す
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        foreach (GameObject e in buf) if (e != null) W.Sim.TryDeleteEntityByName(e.name);
        if (m_SavedScale < 0f) { m_SavedScale = SimulationControl.ConfiguredTimeScale; SimulationControl.ConfiguredTimeScale = 1f; if (Time.timeScale > 1f) Time.timeScale = 1f; }
        string ns = string.IsNullOrEmpty(P.D.ns) ? "robot" : P.D.ns;
        string envs = $"CONTRACT={ExternalProcess.Quote(contract)} ONNX={ExternalProcess.Quote(onnx)} URDF={ExternalProcess.Quote(urdf)} TASK={ExternalProcess.Quote(task ?? "")} OUT={ExternalProcess.Quote(outDir)} NS={ExternalProcess.Quote(ns)} SPAWN_YAW={W.StartYawRad:F5} SPAWN_X={W.StartX:F3} SPAWN_Y={W.StartY:F3}";
        string cmd = Env.Ros2 == "container"
            ? $"env {envs} bash {ExternalProcess.Quote(Path.Combine(Env.RepoRoot, "scripts", "sim2sim_container.sh"))} check jazzy {ExternalProcess.Quote(P.Dir)} 2>&1"
            : $"env {envs} bash {ExternalProcess.Quote(Path.Combine(Env.RepoRoot, "scripts", "check_runner.sh"))} 2>&1";
        m_ReportWriteTicksBeforeRun = File.Exists(m_ReportPath) ? File.GetLastWriteTimeUtc(m_ReportPath).Ticks : 0L;
        m_LogBuf.Clear(); ResetSteps(); m_List.text = "";
        m_Runner = W.Launch(cmd);
        m_Run.interactable = false;
        W.Status(Ui.T("チェックを実行しています (1〜2 分)...", "running the check (1-2 minutes)..."));
        Debug.Log("[Wizard/check] " + cmd);
    }

    void Abort()
    {
        if (m_Runner == null) return;
        m_Runner.Stop(null, 1000); m_Runner = null; RestoreScale(); m_Run.interactable = true;
        W.Status(Ui.T("中止しました", "aborted"), Ui.Warn);
    }

    /// <summary>ROS 経由でスポーンされたロボットにカメラを向ける。</summary>
    void FrameOnRobot()
    {
        var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        if (cam == null) return;
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        foreach (GameObject e in buf)
        {
            if (e == null) continue;
            var rs = e.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) continue;
            Bounds b = rs[0].bounds; foreach (Renderer r in rs) b.Encapsulate(r.bounds);
            cam.target = b.center; cam.distance = Mathf.Max(0.8f, b.extents.magnitude * 3f);
            return;
        }
    }

    void RestoreScale() { if (m_SavedScale >= 0f) { SimulationControl.ConfiguredTimeScale = m_SavedScale; m_SavedScale = -1f; } }

    void StartContainer()
    {
        if (m_ContainerProc != null && !m_ContainerProc.HasExited) return;
        m_ContainerProc = W.Launch($"bash {ExternalProcess.Quote(Path.Combine(Env.RepoRoot, "scripts", "sim2sim_container.sh"))} start jazzy {ExternalProcess.Quote(P.Dir)} 2>&1");
        W.Status(Ui.T("コンテナを起動しています...", "starting the container..."));
    }

    void LoadReport(bool stampOnPass)
    {
        if (!File.Exists(m_ReportPath)) return;
        m_Raw.text = File.ReadAllText(m_ReportPath);
        m_Explain?.Stop();
        m_StampReportOnExplain = stampOnPass;
        m_Explain = W.Launch($"{W.Py} -m unirobolab explain-report {ExternalProcess.Quote(m_ReportPath)} --lang {(Ui.Japanese ? "ja" : "en")} 2>&1");
    }

    public override void Tick()
    {
        if (Env.Ros2 != m_EnvShown) { m_EnvShown = Env.Ros2; RefreshEnv(); }   // 起動時の検出は非同期
        if (m_Runner != null)
        {
            while (m_Runner.TryDequeue(out string l))
            {
                m_LogBuf.AppendLine(l); if (m_LogBuf.Length > 5000) m_LogBuf.Remove(0, 2000);
                if (l.StartsWith("##STEP "))
                {
                    string rest = l.Substring(7); int n = rest.Length > 0 && char.IsDigit(rest[0]) ? rest[0] - '0' : 0;
                    for (int i = 0; i < 5; i++) { if (i < n - 1) { m_Steps[i].text = $"✓ {i + 1}. " + (Ui.Japanese ? k_StepsJa[i] : k_StepsEn[i]); m_Steps[i].color = Ui.Accent; } else if (i == n - 1) { m_Steps[i].text = $"● {i + 1}. " + (Ui.Japanese ? k_StepsJa[i] : k_StepsEn[i]); m_Steps[i].color = Ui.Header; } }
                    // 実行器の ##STEP は英語なので、画面には翻訳済みの段階名を出す
                    W.Status(n >= 1 && n <= 5 ? $"{n}/5 " + (Ui.Japanese ? k_StepsJa[n - 1] : k_StepsEn[n - 1]) : rest);
                    if (n == 4) FrameOnRobot();
                    if (Application.isBatchMode) Debug.Log("[Wizard/check] " + l);
                }
                else if (l.StartsWith("##FAIL ")) { W.Status(l.Substring(7), Ui.Bad); if (Application.isBatchMode) Debug.Log("[Wizard/check] " + l); }
            }
            if (m_Log != null) m_Log.text = m_LogBuf.ToString();
            if (m_Runner.HasExited)
            {
                int code = m_Runner.ExitCode; m_Runner = null; RestoreScale(); m_Run.interactable = true;
                bool newReport = File.Exists(m_ReportPath) && File.GetLastWriteTimeUtc(m_ReportPath).Ticks != m_ReportWriteTicksBeforeRun;
                if (newReport)
                {
                    for (int i = 0; i < 5; i++) { m_Steps[i].text = $"✓ {i + 1}. " + (Ui.Japanese ? k_StepsJa[i] : k_StepsEn[i]); m_Steps[i].color = Ui.Accent; }
                    LoadReport(true);
                }
                else if (code != 0) W.Status(Ui.T("チェックを実行できませんでした。「詳細」のログを見てください", "the check could not run; see the log under Details"), Ui.Bad);
                else W.Status(Ui.T("チェック結果が更新されませんでした。もう一度実行してください", "the check result was not updated; run the check again"), Ui.Bad);
            }
        }
        if (m_ContainerProc != null && m_ContainerProc.HasExited)
        {
            m_ContainerProc = null; m_RosProbe = W.Launch(Env.Ros2CheckCommand());
        }
        if (m_RosProbe != null && m_RosProbe.HasExited)
        {
            m_RosProbe.WaitForExit(); string last = "none"; while (m_RosProbe.TryDequeue(out string l)) last = l.Trim(); Env.Ros2 = last; m_RosProbe = null; RefreshEnv();
            W.Status(Env.Ros2 == "container" ? Ui.T("コンテナが動いています", "container is running") : Ui.T("コンテナを起動できませんでした", "could not start the container"), Env.Ros2 == "container" ? Ui.Accent : Ui.Bad);
        }
        if (m_Explain != null && m_Explain.HasExited)
        {
            m_Explain.WaitForExit(); var sb = new StringBuilder(); while (m_Explain.TryDequeue(out string l)) sb.AppendLine(l);
            bool pass = m_Explain.ExitCode == 0; m_Explain = null;
            m_List.text = sb.ToString().Trim();
            P.D.report = P.Rel(m_ReportPath); if (pass && m_StampReportOnExplain) P.Stamp("report"); P.Save();
            bool currentPass = pass && PhaseDeploy.CheckState(P) == PhaseDeploy.CheckGateState.Ready;
            W.RefreshStepper();
            m_StampReportOnExplain = false;
            if (Application.isBatchMode) foreach (string l in m_List.text.Split('\n')) Debug.Log("[Wizard/check] " + l);
            W.Status(currentPass ? Ui.T("合格です。⑥ で実機用のパッケージを作ります", "Passed. Build the package for the real robot in step 6")
                                 : pass ? Ui.T("以前の合格結果です。現在の内容でチェックを再実行してください", "This is an earlier PASS; rerun the check with the current files")
                                        : Ui.T("不合格。次の一手を見てください", "Failed; see the next steps"), currentPass ? Ui.Accent : Ui.Warn);
            if (currentPass) W.TriggerDoneShots();
        }
    }

    // ⑥ は ⑤ の合格が前提 (PhaseDeploy.CheckState)。未実施・不合格・古い結果のままでは進ませない
    public override bool CanProceed(out string reason) => PhaseDeploy.HasCurrentPass(P, out reason);
    public override bool Done() => P.Exists("report") && !P.IsStale("report") && P.D.stamp_keys.Contains("report");
    public override bool Stale() => P.IsStale("report");
    public override void Leave() { RestoreScale(); }
    public override void Action(string name) { if (name == "check") RunCheck(); else if (name == "load") LoadReport(false); }
}
