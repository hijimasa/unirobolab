using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>⑥ 実機へ: ⑤ で確かめた契約と方策から ROS 2 パッケージと手順書を作る。接続先は ① で決めたもの。</summary>
public class PhaseDeploy : Phase
{
    public override string Key => "deploy";
    public override string Title => Ui.T("⑥ 実機へ", "6 Deploy");
    public override LabWizard.PreviewMode Preview => LabWizard.PreviewMode.Hidden;

    TMP_Text m_Head, m_Summary, m_Guide;
    TMP_InputField m_Out;
    Button m_Make, m_Open;
    ExternalProcess m_Proc; int m_Step; string m_PkgDir;

    public override void Build(RectTransform main, RectTransform details)
    {
        Root = Ui.Column(main, "Deploy", 6f);
        m_Head = Ui.Label(Root.transform, "", 12f, Ui.Text, true, 44f);
        var r = Ui.Row(Root.transform, 28f);
        Ui.Label(r.transform, Ui.T("出力先", "Output"), 12f, Ui.Muted, false, 0f, 60f);
        m_Out = Ui.Input(r.transform, "(auto) <project>/deploy", 26f);
        Ui.Btn(r.transform, Ui.T("選ぶ...", "Browse..."), () => { string[] s = SFB.StandaloneFileBrowser.OpenFolderPanel(Ui.T("出力先", "Output folder"), P.Dir, false); if (s != null && s.Length > 0 && !string.IsNullOrEmpty(s[0])) m_Out.text = s[0]; }, 90f, 26f);
        var r2 = Ui.Row(Root.transform, 32f);
        m_Make = Ui.Btn(r2.transform, Ui.T("ROS 2 パッケージと手順書を作る", "Build the ROS 2 package and the guide"), Make, 0f, 30f); Ui.SetBtn(m_Make, null, Ui.BtnActive);
        m_Open = Ui.Btn(r2.transform, Ui.T("手順書を開く", "Open the guide"), OpenGuide, 130f, 30f); m_Open.interactable = false;
        m_Summary = Ui.Label(Root.transform, "", 12f, Ui.Text, true, 200f); m_Summary.alignment = TextAlignmentOptions.TopLeft;
        Ui.Spacer(Root.transform);
        DetailsRoot = Ui.Column(details, "DeployDetails", 4f);
        Ui.Label(DetailsRoot.transform, "DEPLOY.md", 12f, Ui.Muted);
        m_Guide = Ui.Label(DetailsRoot.transform, "", 10f, Ui.Text, true, 0f); m_Guide.GetComponent<LayoutElement>().flexibleHeight = 1f; m_Guide.alignment = TextAlignmentOptions.TopLeft;
    }

    public override bool CanEnter(out string reason) { reason = Ui.T("先に ③ で学習してください", "train in step 3 first"); return P.Exists("run"); }

    public override void Enter()
    {
        bool checked_ = P.Exists("report") && !P.IsStale("report");
        m_Head.text = (checked_ ? Ui.T("✓ ⑤ で確かめた契約と方策をそのまま使います。", "✓ Uses the contract and policy verified in step 5.")
                                 : Ui.T("! ⑤ のチェックが未実施か古いです。実機に持っていく前に確かめてください。", "! Step 5 has not been run (or is stale); check before the real robot."))
                      + "\n" + Ui.T("接続先 (① で決めたもの): ", "Connection (from step 1): ") + P.D.ns + " / " + (P.D.command_mode == "ros2_control_commands" ? "ros2_control" : "JointState");
        m_Head.color = checked_ ? Ui.Text : Ui.Warn;
        if (string.IsNullOrEmpty(m_Out.text)) m_Out.text = string.IsNullOrEmpty(P.D.deploy_dir) ? Path.Combine(P.Dir, "deploy") : P.Abs(P.D.deploy_dir);
        if (P.Exists("deploy")) ShowExisting();
        W.Status(Ui.T("「作る」を押すとパッケージと手順書ができます", "Press the build button to create the package and the guide"));
    }

    void ShowExisting()
    {
        m_PkgDir = FindPkg(P.Abs(P.D.deploy_dir));
        string md = m_PkgDir != null ? Path.Combine(m_PkgDir, "DEPLOY.md") : null;
        m_Open.interactable = md != null && File.Exists(md);
        if (m_Open.interactable) { m_Guide.text = File.ReadAllText(md); }
    }

    static string FindPkg(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (string d in Directory.GetDirectories(dir)) if (File.Exists(Path.Combine(d, "package.xml"))) return d;
        return null;
    }

    void Make()
    {
        if (m_Proc != null && !m_Proc.HasExited) return;
        P.D.deploy_dir = P.Rel(m_Out.text); P.Save();
        m_Step = 1;
        m_Proc = W.Launch($"{W.Py} -m unirobolab gen {ExternalProcess.Quote(P.Abs(P.D.contract))} --out {ExternalProcess.Quote(m_Out.text)} --onnx {ExternalProcess.Quote(P.OnnxPath)} --overwrite 2>&1");
        W.Status(Ui.T("1/2 パッケージを生成しています...", "1/2 generating the package..."));
    }

    public override void Tick()
    {
        if (m_Proc == null || !m_Proc.HasExited) return;
        m_Proc.WaitForExit(); var sb = new StringBuilder(); while (m_Proc.TryDequeue(out string l)) sb.AppendLine(l);
        string text = sb.ToString().Trim(); bool ok = m_Proc.ExitCode == 0; m_Proc = null;
        if (!ok) { W.Status(Ui.T("失敗: ", "failed: ") + text, Ui.Bad); m_Step = 0; return; }
        if (m_Step == 1)
        {
            m_PkgDir = text.StartsWith("generated ") ? text.Substring(10).Trim() : FindPkg(m_Out.text);
            string pkg = Path.GetFileName(m_PkgDir.TrimEnd('/')); string lang = Ui.Japanese ? "ja" : "en"; string c = ExternalProcess.Quote(P.Abs(P.D.contract));
            m_Step = 2;
            m_Proc = W.Launch($"{W.Py} -m unirobolab deploy-guide {c} --package {ExternalProcess.Quote(pkg)} --lang {lang} --out {ExternalProcess.Quote(Path.Combine(m_PkgDir, "DEPLOY.md"))} 2>&1 && {W.Py} -m unirobolab deploy-guide {c} --package {ExternalProcess.Quote(pkg)} --lang {lang} --summary 2>&1");
            W.Status(Ui.T("2/2 手順書を書いています...", "2/2 writing the guide..."));
        }
        else
        {
            m_Step = 0;
            m_Summary.text = text.StartsWith("wrote ") ? text.Substring(text.IndexOf('\n') + 1) : text;
            P.Stamp("deploy"); P.Save(); ShowExisting(); W.RefreshStepper();
            W.Status(Ui.T("できました: ", "done: ") + m_PkgDir, Ui.Accent);
            if (Application.isBatchMode) foreach (string l in m_Summary.text.Split('\n')) Debug.Log("[Wizard/deploy] " + l);
        }
    }

    void OpenGuide()
    {
        string md = m_PkgDir != null ? Path.Combine(m_PkgDir, "DEPLOY.md") : null;
        if (md == null || !File.Exists(md)) return;
        try { System.Diagnostics.Process.Start("xdg-open", md); } catch (System.Exception) { Application.OpenURL("file://" + md); }
    }

    public override bool Done() => P.Exists("deploy") && !P.IsStale("deploy");
    public override bool Stale() => P.IsStale("deploy");
    public override void Action(string name) { if (name == "deploy") Make(); }
}
