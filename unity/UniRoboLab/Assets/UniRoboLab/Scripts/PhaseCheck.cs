using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>⑤ チェック (配備前): 平易なチェックリストと次の一手。実行は sim2sim (ROS 2 が要る)。</summary>
public class PhaseCheck : Phase
{
    public override string Key => "check";
    public override string Title => Ui.T("⑤ チェック", "5 Check");
    public override LabWizard.PreviewMode Preview => LabWizard.PreviewMode.Hidden;

    TMP_Text m_Env, m_List, m_Raw;
    Button m_Run, m_Load;
    ExternalProcess m_Check, m_Explain;
    string m_ReportPath;

    public override void Build(RectTransform main, RectTransform details)
    {
        Root = Ui.Column(main, "Check", 6f);
        Ui.Label(Root.transform, Ui.T("実機に持っていく前に、ROS 2 の経路で方策を動かして確かめます。", "Before the real robot, run the policy through the ROS 2 path and check it."), 12f, Ui.Muted, true, 30f);
        m_Env = Ui.Label(Root.transform, "", 12f, Ui.Text, true, 40f);
        var r = Ui.Row(Root.transform, 32f);
        m_Run = Ui.Btn(r.transform, Ui.T("▶ チェックを実行", "▶ Run the check"), RunCheck, 0f, 30f); Ui.SetBtn(m_Run, null, Ui.BtnActive);
        m_Load = Ui.Btn(r.transform, Ui.T("結果を読み直す", "Reload result"), () => LoadReport(m_ReportPath), 130f, 30f);
        m_List = Ui.Label(Root.transform, "", 13f, Ui.Text, true, 260f); m_List.alignment = TextAlignmentOptions.TopLeft;
        Ui.Spacer(Root.transform);
        DetailsRoot = Ui.Column(details, "CheckDetails", 4f);
        Ui.Label(DetailsRoot.transform, "report.json", 12f, Ui.Muted);
        m_Raw = Ui.Label(DetailsRoot.transform, "", 10f, Ui.Text, true, 0f); m_Raw.GetComponent<LayoutElement>().flexibleHeight = 1f; m_Raw.alignment = TextAlignmentOptions.TopLeft;
    }

    public override bool CanEnter(out string reason) { reason = Ui.T("先に ③ で学習してください", "train in step 3 first"); return P.Exists("run"); }

    public override void Enter()
    {
        m_ReportPath = string.IsNullOrEmpty(P.D.report) ? Path.Combine(P.Dir, "sim2sim_out", "report.json") : P.Abs(P.D.report);
        string ros = Env.Ros2 switch
        {
            "host" => Ui.T("ROS 2: この PC で使えます", "ROS 2: available on this machine"),
            "container" => Ui.T("ROS 2: コンテナ (unirobolab-sim2sim) で代用します", "ROS 2: via the unirobolab-sim2sim container"),
            "docker-only" => Ui.T("ROS 2: 無し。コンテナは未起動 (scripts/sim2sim_container.sh start)", "ROS 2: none; container not running (scripts/sim2sim_container.sh start)"),
            _ => Ui.T("ROS 2: 見つかりません。チェックは ROS 2 のある環境で実行してください", "ROS 2: not found; run the check where ROS 2 is available"),
        };
        string cmd = UniRoboLabConfig.Data.sim2sim_command;
        m_Env.text = ros + (string.IsNullOrEmpty(cmd) ? Ui.T("\n実行コマンド未設定 (unirobolab.sim2sim_command)。結果ファイルの読み込みはできます", "\nsim2sim_command not set; results can still be loaded") : "");
        m_Run.interactable = !string.IsNullOrEmpty(cmd);
        if (File.Exists(m_ReportPath)) LoadReport(m_ReportPath); else m_List.text = Ui.T("(まだ結果がありません)", "(no result yet)");
        if (P.IsStale("report")) m_List.text = Ui.T("! 契約か方策が変わっています。チェックし直してください\n", "! the contract or the policy changed; check again\n") + m_List.text;
        W.Status(Ui.T("チェックを実行するか、結果を読み込んでください", "Run the check or load a result"));
    }

    void RunCheck()
    {
        string cmd = UniRoboLabConfig.Data.sim2sim_command;
        if (string.IsNullOrEmpty(cmd) || (m_Check != null && !m_Check.HasExited)) return;
        string pkg = Path.GetFileName(P.Abs(P.D.deploy_dir).TrimEnd('/'));
        var full = new StringBuilder(cmd).Append(' ').Append(ExternalProcess.Quote(P.Abs(P.D.contract)));
        if (!string.IsNullOrEmpty(pkg)) full.Append(" --pkg ").Append(ExternalProcess.Quote(pkg));
        full.Append(" --out ").Append(ExternalProcess.Quote(Path.GetDirectoryName(m_ReportPath))).Append(" 2>&1");
        m_Check = W.Launch(full.ToString());
        W.Status(Ui.T("チェックを実行中 (数分かかります)...", "running the check (takes a few minutes)..."));
    }

    void LoadReport(string path)
    {
        if (!File.Exists(path)) return;
        m_Raw.text = File.ReadAllText(path);
        m_Explain?.Stop();
        m_Explain = W.Launch($"{W.Py} -m unirobolab explain-report {ExternalProcess.Quote(path)} --lang {(Ui.Japanese ? "ja" : "en")} 2>&1");
    }

    public override void Tick()
    {
        if (m_Check != null) { string last = null; while (m_Check.TryDequeue(out string l)) last = l; if (last != null) W.Status(last); if (m_Check.HasExited) { m_Check = null; LoadReport(m_ReportPath); } }
        if (m_Explain != null && m_Explain.HasExited)
        {
            m_Explain.WaitForExit(); var sb = new StringBuilder(); while (m_Explain.TryDequeue(out string l)) sb.AppendLine(l);
            bool pass = m_Explain.ExitCode == 0; m_Explain = null;
            m_List.text = sb.ToString().Trim();
            P.D.report = P.Rel(m_ReportPath); if (pass) P.Stamp("report"); P.Save(); W.RefreshStepper();
            if (Application.isBatchMode) foreach (string l in m_List.text.Split('\n')) Debug.Log("[Wizard/check] " + l);
            W.Status(pass ? Ui.T("合格です。⑥ で実機用のパッケージを作ります", "Passed. Build the package for the real robot in step 6") : Ui.T("不合格。次の一手を見てください", "Failed; see the next steps"), pass ? Ui.Accent : Ui.Warn);
        }
    }

    public override bool CanProceed(out string reason) { reason = ""; return true; }   // 不合格でも警告つきで ⑥ へ行ける
    public override bool Done() => P.Exists("report") && !P.IsStale("report") && P.D.stamp_keys.Contains("report");
    public override bool Stale() => P.IsStale("report");
    public override void Action(string name) { if (name == "check") RunCheck(); else if (name == "load") LoadReport(m_ReportPath); }
}
