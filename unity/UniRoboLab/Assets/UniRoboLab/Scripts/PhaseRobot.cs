using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>① ロボット: URDF を選ぶ → 構造の表と 3D プレビュー → 実機の接続先。契約 JSON は詳細。</summary>
public class PhaseRobot : Phase
{
    public override string Key => "robot";
    public override string Title => Ui.T("① ロボット", "1 Robot");

    TMP_InputField m_Urdf, m_Name, m_Ns, m_Controller, m_Estop;
    Ui.Choice m_Mode;
    TMP_Text m_Base, m_Table, m_Validation, m_ContractText;
    ExternalProcess m_Info;
    RobotInfo m_RobotInfo;
    string m_SpawnedName;
    TaskSpec m_Spec;

    public override void Build(RectTransform main, RectTransform details)
    {
        Root = Ui.Column(main, "Robot", 6f);
        Ui.Label(Root.transform, Ui.T("ロボットの URDF を選ぶと、構造を読み取って右に表示します。", "Pick the robot's URDF; its structure is read and shown on the right."), 12f, Ui.Muted, true, 34f);
        var r = Ui.Row(Root.transform, 28f);
        Ui.Label(r.transform, "URDF", 12f, Ui.Muted, false, 0f, 60f);
        m_Urdf = Ui.Input(r.transform, "/path/to/robot.urdf", 26f);
        Ui.Btn(r.transform, Ui.T("選ぶ...", "Browse..."), PickUrdf, 90f, 26f);
        Ui.Btn(r.transform, Ui.T("読み込む", "Load"), () => LoadUrdf(m_Urdf.text), 90f, 26f);
        var r2 = Ui.Row(Root.transform, 26f);
        Ui.Label(r2.transform, Ui.T("ロボット名", "Name"), 12f, Ui.Muted, false, 0f, 60f);
        m_Name = Ui.Input(r2.transform, "robot", 24f);
        m_Base = Ui.Label(r2.transform, "", 12f, Ui.Text, false, 0f, 220f);
        Ui.Label(Root.transform, Ui.T("関節 (指令 / 可動範囲 / 速度上限)", "Joints (command / range / speed cap)"), 12f, Ui.Muted);
        m_Table = Ui.Label(Root.transform, Ui.T("(URDF を読み込むと出ます)", "(load a URDF)"), 12f, Ui.Text, true, 150f);
        m_Table.alignment = TextAlignmentOptions.TopLeft;
        Ui.Label(Root.transform, Ui.T("実機の接続先 (ここで決めたものをそのまま配備します)", "Real-robot connection (used as-is at deploy time)"), 12f, Ui.Muted);
        m_Ns = Ui.Field(Root.transform, Ui.T("名前空間", "Namespace"), "robot");
        var mr = Ui.Row(Root.transform, 26f);
        Ui.Label(mr.transform, Ui.T("指令の方式", "Command"), 12f, Ui.Muted, false, 0f, 110f);
        m_Mode = new Ui.Choice(mr.transform, new[] { Ui.T("JointState トピック", "JointState topic"), "ros2_control" }, 0, 24f);
        m_Controller = Ui.Field(Root.transform, Ui.T("コントローラ", "Controller"), "joint_group_position_controller");
        m_Estop = Ui.Field(Root.transform, Ui.T("非常停止トピック", "E-stop topic"), Ui.T("(自動) /<名前空間>/estop", "(auto) /<namespace>/estop"));
        m_Mode.OnChange = i => m_Controller.interactable = i == 1;
        m_Controller.interactable = false;
        m_Validation = Ui.Label(Root.transform, "", 12f, Ui.Text, true, 40f);
        Ui.Spacer(Root.transform);

        DetailsRoot = Ui.Column(details, "RobotDetails", 4f);
        Ui.Label(DetailsRoot.transform, Ui.T("契約 (JSON、自動生成。② で作り直されます)", "Contract JSON (generated; rebuilt in step 2)"), 12f, Ui.Muted);
        m_ContractText = Ui.Label(DetailsRoot.transform, "", 10f, Ui.Text, true, 0f);
        m_ContractText.GetComponent<LayoutElement>().flexibleHeight = 1f; m_ContractText.alignment = TextAlignmentOptions.TopLeft;
    }

    public override void Enter()
    {
        m_Spec = TaskSpec.Load(P.Abs(P.D.task));
        m_Urdf.text = P.Abs(P.D.urdf); m_Name.text = P.D.name; m_Ns.text = P.D.ns;
        m_Mode.Set(P.D.command_mode == "ros2_control_commands" ? 1 : 0); m_Controller.text = P.D.controller; m_Estop.text = P.D.estop_topic;
        if (P.Has(P.D.urdf) && m_RobotInfo == null) LoadUrdf(P.Abs(P.D.urdf));
        RefreshDetails();
        W.Status(P.Has(P.D.urdf) ? Ui.T("関節名と接続先が実機と合っているか確かめて「次へ」", "Check the joint names and the connection, then Next")
                                 : Ui.T("まず URDF を選んでください", "Pick a URDF to begin"));
    }

    void PickUrdf()
    {
        string[] sel = SFB.StandaloneFileBrowser.OpenFilePanel(Ui.T("ロボットの URDF", "Robot URDF"), P.Dir, "urdf", false);
        if (sel != null && sel.Length > 0 && !string.IsNullOrEmpty(sel[0])) { m_Urdf.text = sel[0]; LoadUrdf(sel[0]); }
    }

    void LoadUrdf(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { W.Status(Ui.T("URDF が見つかりません: ", "URDF not found: ") + path, Ui.Bad); return; }
        P.D.urdf = P.Rel(path); P.Save();
        m_Info = W.Launch($"{W.Py} -m unirobolab robot-info {ExternalProcess.Quote(path)} 2>&1");
        W.Status(Ui.T("URDF を読んでいます...", "reading the URDF..."));
        SpawnPreview(path);
    }

    void SpawnPreview(string urdf)
    {
        if (W.Sim == null || !W.Sim.CanSpawnFromGui) return;
        if (!string.IsNullOrEmpty(m_SpawnedName)) W.Sim.TryDeleteEntityByName(m_SpawnedName);
        if (W.Sim.TrySpawnRobotFromUrdf(urdf, out string name, out string err)) { m_SpawnedName = name; W.PlaceSpawned(name); FrameCamera(); }
        else Debug.LogWarning("[PhaseRobot] spawn: " + err);
    }

    /// <summary>スポーンしたロボットの外接球を狙う (スポーン位置は核が空いた場所を選ぶ)。</summary>
    void FrameCamera()
    {
        var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        if (cam == null) return;
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        foreach (GameObject e in buf)
        {
            if (e == null || e.name != m_SpawnedName) continue;
            var rs = e.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) break;
            Bounds b = rs[0].bounds; foreach (Renderer r in rs) b.Encapsulate(r.bounds);
            cam.target = b.center; cam.distance = Mathf.Max(0.8f, b.extents.magnitude * 3f);
            return;
        }
        cam.target = new Vector3(0f, 0.15f, 0f); cam.distance = 1.6f;
    }

    public override void Tick()
    {
        if (m_Info == null || !m_Info.HasExited) return;
        m_Info.WaitForExit();
        var sb = new StringBuilder(); while (m_Info.TryDequeue(out string l)) sb.AppendLine(l);
        string text = sb.ToString().Trim(); bool ok = m_Info.ExitCode == 0 && text.StartsWith("{");
        m_Info = null;
        if (!ok) { m_RobotInfo = null; m_Validation.text = Ui.T("読み取り失敗: ", "read failed: ") + text; m_Validation.color = Ui.Bad; W.RefreshStepper(); return; }
        m_RobotInfo = JsonUtility.FromJson<RobotInfo>(text);
        if (string.IsNullOrEmpty(m_Name.text)) m_Name.text = m_RobotInfo.name;
        if (string.IsNullOrEmpty(m_Ns.text)) m_Ns.text = m_RobotInfo.name;
        m_Base.text = (m_RobotInfo.fixed_base ? Ui.T("基体: 固定 (関節目標のタスク)", "base: fixed (joint-target tasks)") : Ui.T("基体: 移動 (地点到達のタスク)", "base: mobile (reach-a-point tasks)"))
                      + (m_RobotInfo.imu ? " / IMU" : "") + (m_RobotInfo.odom ? " / odom" : "");
        var t = new StringBuilder();
        foreach (RobotJoint j in m_RobotInfo.joints)
            t.AppendLine($"{j.name,-24} {Mode(j.mode),-6} {j.lower,6:F2} .. {j.upper,-6:F2} {(j.type == "prismatic" ? "m" : "rad"),-4} {j.velocity,5:F1} {(j.type == "prismatic" ? "m/s" : "rad/s")}");
        m_Table.text = t.ToString();
        m_Validation.text = Ui.T($"✓ 関節 {m_RobotInfo.joints.Count} 個を読み取りました", $"✓ {m_RobotInfo.joints.Count} joints read"); m_Validation.color = Ui.Accent;
        W.Status(Ui.T("関節名と接続先が実機と合っているか確かめて「次へ」", "Check the joint names and the connection, then Next"));
        W.RefreshStepper();
    }

    static string Mode(string m) => m == "position" ? Ui.T("位置", "pos") : m == "velocity" ? Ui.T("速度", "vel") : Ui.T("トルク", "eff");

    public override bool CanProceed(out string reason)
    {
        reason = Ui.T("URDF を読み込んでください", "load a URDF first");
        return m_RobotInfo != null;
    }

    public override bool OnNext()
    {
        P.D.name = m_Name.text.Trim(); P.D.ns = m_Ns.text.Trim();
        P.D.command_mode = m_Mode.Index == 1 ? "ros2_control_commands" : "joint_state_topic";
        P.D.controller = m_Controller.text.Trim(); P.D.estop_topic = m_Estop.text.Trim();
        m_Spec.robot.urdf = P.D.urdf; m_Spec.robot.name = P.D.name; m_Spec.robot.@namespace = P.D.ns;
        m_Spec.robot.command_mode = P.D.command_mode; m_Spec.robot.controller = P.D.controller; m_Spec.robot.estop_topic = P.D.estop_topic;
        if (m_Spec.goal.Count == 0) m_Spec.goal.Add(new SpecGoal { type = m_RobotInfo != null && !m_RobotInfo.fixed_base ? "base_in_region" : "joints_near", tolerance = m_RobotInfo != null && !m_RobotInfo.fixed_base ? 0.15f : 0.05f });
        if (m_Spec.episode.time_s <= 0f) m_Spec.episode.time_s = m_RobotInfo != null && !m_RobotInfo.fixed_base ? 10f : 2f;
        m_Spec.Save(P.Abs(P.D.task)); P.Save();
        return true;
    }

    public RobotInfo Info => m_RobotInfo;
    public override bool Done() => P.Has(P.D.urdf) && P.Has(P.D.task);

    void RefreshDetails()
    {
        if (m_ContractText == null) return;
        string c = P.Abs(P.D.contract);
        m_ContractText.text = File.Exists(c) ? File.ReadAllText(c) : Ui.T("(まだありません。② で生成されます)", "(none yet; generated in step 2)");
    }

    public override void OnProjectChanged() { m_RobotInfo = null; }
    public override void Action(string name) { if (name == "load") LoadUrdf(m_Urdf.text); }
}
