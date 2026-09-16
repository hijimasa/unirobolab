using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>② タスク: 開始条件と終了条件を決める (v1: 関節が目標角に / 基体が領域内)。契約と学習設定はここで生成。</summary>
public class PhaseTask : Phase
{
    public override string Key => "task";
    public override string Title => Ui.T("② タスク", "2 Task");

    TaskSpec m_Spec;
    bool m_IsBase, m_IsLink;
    Ui.Choice m_KindChoice;           // 固定基体: 関節目標 / 手先を領域へ
    GameObject m_LinkBox;
    Ui.Choice m_LinkChoice; readonly List<string> m_LinkNames = new List<string>();
    TMP_InputField m_Cx, m_Cy, m_Cz, m_Sx, m_Sy, m_Sz, m_Px, m_Py, m_Pz;
    LineRenderer m_BoxLine;
    GameObject m_PointMarker;
    readonly Dictionary<string, float[]> m_LinkTips = new Dictionary<string, float[]>();
    TMP_Text m_Kind, m_RangeL, m_TolL, m_TimeL, m_RMinL, m_RMaxL, m_Estimate, m_TrainText;
    Slider m_Range, m_Tol, m_Time, m_RMin, m_RMax, m_Yaw;
    TMP_InputField m_StartX, m_StartY; TMP_Text m_YawL;
    GameObject m_JointBox, m_BaseBox;
    Ui.Choice m_RangeMode;
    ExternalProcess m_Gen, m_Est;
    float m_EstAt = -1f;
    bool m_Generating;
    LineRenderer m_RingIn, m_RingOut;
    readonly List<LineRenderer> m_Arcs = new List<LineRenderer>();   // 関節目標: 各関節の目標範囲を弧で

    public override void Build(RectTransform main, RectTransform details)
    {
        Root = Ui.Column(main, "Task", 6f);
        m_Kind = Ui.Label(Root.transform, "", 13f, Ui.Header);
        var kr = Ui.Row(Root.transform, 26f);
        Ui.Label(kr.transform, Ui.T("何をさせるか", "Task kind"), 12f, Ui.Muted, false, 0f, 110f);
        m_KindChoice = new Ui.Choice(kr.transform, new[] { Ui.T("関節を目標角へ", "joints to target angles"), Ui.T("手先を所定の場所へ", "a link to a target region") }, 0, 24f);
        m_KindChoice.OnChange = i => { if (m_Spec != null && !m_IsBase) SetKind(i == 1); };
        Ui.Label(Root.transform, Ui.T("開始の位置と向き (ロボットはここからスタート。学習・チェックでも同じ)", "Start pose (used for training and the check as well)"), 12f, Ui.Muted);
        var sp = Ui.Row(Root.transform, 26f);
        Ui.Label(sp.transform, "x [m]", 12f, Ui.Text, false, 0f, 44f); m_StartX = Ui.Input(sp.transform, "0", 24f);
        Ui.Label(sp.transform, "y [m]", 12f, Ui.Text, false, 0f, 44f); m_StartY = Ui.Input(sp.transform, "0", 24f);
        m_YawL = Ui.Label(sp.transform, "", 12f, Ui.Text, false, 0f, 130f);
        m_Yaw = Ui.Slider(Root.transform, -180f, 180f, 180f, v => { m_YawL.text = Ui.T($"向き {v:F0}°", $"yaw {v:F0}°"); TouchStart(); }, true);
        m_StartX.onEndEdit.AddListener(_ => TouchStart()); m_StartY.onEndEdit.AddListener(_ => TouchStart());
        Ui.Label(Root.transform, Ui.T("終了 (成功) の条件", "Goal (success) condition"), 12f, Ui.Muted);
        // joints_near
        m_JointBox = Ui.Column(Root.transform, "Joint", 4f, 0, false);
        var rm = Ui.Row(m_JointBox.transform, 26f);
        Ui.Label(rm.transform, Ui.T("目標角の範囲", "Goal range"), 12f, Ui.Text, false, 0f, 110f);
        m_RangeMode = new Ui.Choice(rm.transform, new[] { Ui.T("自動 (可動範囲の 80 %)", "auto (80 % of limits)"), Ui.T("手で決める", "manual") }, 0, 24f);
        m_RangeL = Ui.Label(m_JointBox.transform, "", 12f, Ui.Text);
        m_Range = Ui.Slider(m_JointBox.transform, 0.05f, 3.0f, 0.5f, v => { m_RangeL.text = Ui.T($"目標は ±{v:F2} rad から抽選", $"goal sampled from ±{v:F2} rad"); Touch(); });
        m_RangeMode.OnChange = i => { m_Range.interactable = i == 1; m_RangeL.text = i == 0 ? Ui.T("目標は可動範囲の 80 % から抽選", "goal sampled from 80 % of the joint limits") : m_RangeL.text; Touch(); };
        // link_near (手先を領域へ): リンク、領域の中心と大きさ (根リンク座標系 [m])
        m_LinkBox = Ui.Column(Root.transform, "Link", 4f, 0, false);
        var lr = Ui.Row(m_LinkBox.transform, 26f);
        Ui.Label(lr.transform, Ui.T("動かすリンク", "Link"), 12f, Ui.Text, false, 0f, 110f);
        m_LinkChoice = new Ui.Choice(lr.transform, new[] { "-" }, 0, 24f);
        var pr = Ui.Row(m_LinkBox.transform, 26f);
        Ui.Label(pr.transform, Ui.T("リンク上の点 [m]", "Point on link [m]"), 12f, Ui.Text, false, 0f, 110f);
        m_Px = Ui.Input(pr.transform, "x", 24f); m_Py = Ui.Input(pr.transform, "y", 24f); m_Pz = Ui.Input(pr.transform, "z", 24f);
        foreach (var f in new[] { m_Px, m_Py, m_Pz }) f.onEndEdit.AddListener(_ => Touch());
        var cr = Ui.Row(m_LinkBox.transform, 26f);
        Ui.Label(cr.transform, Ui.T("領域の中心 [m]", "Region center [m]"), 12f, Ui.Text, false, 0f, 110f);
        m_Cx = Ui.Input(cr.transform, "x", 24f); m_Cy = Ui.Input(cr.transform, "y", 24f); m_Cz = Ui.Input(cr.transform, "z", 24f);
        var sr = Ui.Row(m_LinkBox.transform, 26f);
        Ui.Label(sr.transform, Ui.T("領域の大きさ [m]", "Region size [m]"), 12f, Ui.Text, false, 0f, 110f);
        m_Sx = Ui.Input(sr.transform, "x", 24f); m_Sy = Ui.Input(sr.transform, "y", 24f); m_Sz = Ui.Input(sr.transform, "z", 24f);
        foreach (var f in new[] { m_Cx, m_Cy, m_Cz, m_Sx, m_Sy, m_Sz }) f.onEndEdit.AddListener(_ => Touch());
        Ui.Label(m_LinkBox.transform, Ui.T("点 (緑の球) を箱 (緑) の中へ動かすのが目標。目標は箱の中から抽選するので、点が届く範囲に置いてください", "The goal is to bring the point (green sphere) into the box; goals are sampled inside it, so keep it reachable"), 11f, Ui.Muted, true, 30f);
        // base_in_region
        m_BaseBox = Ui.Column(Root.transform, "Base", 4f, 0, false);
        m_RMinL = Ui.Label(m_BaseBox.transform, "", 12f, Ui.Text);
        m_RMin = Ui.Slider(m_BaseBox.transform, 0.2f, 5f, 1f, v => { if (m_RMax != null && m_RMax.value < v + 0.2f) m_RMax.value = v + 0.2f; m_RMinL.text = Ui.T($"目標地点: 開始位置から {v:F1} m 以上", $"goal: at least {v:F1} m from the start"); Touch(); });
        m_RMaxL = Ui.Label(m_BaseBox.transform, "", 12f, Ui.Text);
        m_RMax = Ui.Slider(m_BaseBox.transform, 0.4f, 6f, 2.5f, v => { if (m_RMin != null && m_RMin.value > v - 0.2f) m_RMin.value = v - 0.2f; m_RMaxL.text = Ui.T($"目標地点: 開始位置から {v:F1} m 以内 (環の中から抽選)", $"goal: within {v:F1} m of the start (sampled in the ring)"); Touch(); });
        // common
        m_TolL = Ui.Label(Root.transform, "", 12f, Ui.Text);
        m_Tol = Ui.Slider(Root.transform, 0.01f, 0.5f, 0.05f, v => { m_TolL.text = m_IsBase || m_IsLink ? Ui.T($"成功: 目標から {v:F3} m 以内", $"success: within {v:F3} m") : Ui.T($"成功: 目標角から {v:F3} rad 以内", $"success: within {v:F3} rad"); Touch(); });
        m_TimeL = Ui.Label(Root.transform, "", 12f, Ui.Text);
        m_Time = Ui.Slider(Root.transform, 0.5f, 30f, 2f, v => { m_TimeL.text = Ui.T($"1 回の制限時間: {v:F1} 秒", $"time per attempt: {v:F1} s"); Touch(); });
        m_Estimate = Ui.Label(Root.transform, "", 12f, Ui.Accent, true, 40f);
        Ui.Label(Root.transform, Ui.T("「次へ」で契約と学習設定を生成します (学習はまだ始めません)。", "Next generates the contract and the training config (training does not start yet)."), 11f, Ui.Muted, true, 30f);
        Ui.Spacer(Root.transform);

        DetailsRoot = Ui.Column(details, "TaskDetails", 4f);
        Ui.Label(DetailsRoot.transform, Ui.T("生成される学習設定 (train.json)", "Generated training config (train.json)"), 12f, Ui.Muted);
        m_TrainText = Ui.Label(DetailsRoot.transform, "", 10f, Ui.Text, true, 0f);
        m_TrainText.GetComponent<LayoutElement>().flexibleHeight = 1f; m_TrainText.alignment = TextAlignmentOptions.TopLeft;
    }

    void Touch() { m_EstAt = Time.realtimeSinceStartup + 0.6f; DrawGoal(); }

    /// <summary>開始の位置・向きを仕様に書き、プレビューのロボットをそこへ置く。</summary>
    void TouchStart()
    {
        if (m_Spec == null) return;
        float.TryParse(m_StartX.text, out float x); float.TryParse(m_StartY.text, out float y);
        m_Spec.start.@base.xy = new[] { x, y }; m_Spec.start.@base.yaw_deg = new[] { m_Yaw.value, m_Yaw.value };
        m_Spec.Save(P.Abs(P.D.task));
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        foreach (GameObject e in buf) if (e != null) { W.PlaceSpawned(e.name, x, y, m_Yaw.value); break; }
        DrawGoal();
    }

    public override bool CanEnter(out string reason)
    {
        reason = Ui.T("先に ① でロボットを読み込んでください", "load the robot in step 1 first");
        return P.Has(P.D.urdf) && P.Has(P.D.task);
    }

    public override void Enter()
    {
        m_Spec = TaskSpec.Load(P.Abs(P.D.task));
        SpecGoal g = m_Spec.Goal0;
        m_IsBase = g.type == "base_in_region";
        m_IsLink = g.type == "link_near";
        m_Kind.text = m_IsBase ? Ui.T("このロボットは移動基体: 「所定の場所へ動く」タスク", "Mobile base: a reach-a-point task")
                               : Ui.T("このロボットは固定基体", "Fixed base");
        m_KindChoice.Buttons[0].transform.parent.gameObject.SetActive(!m_IsBase);
        LoadLinks();
        m_KindChoice.Set(m_IsLink ? 1 : 0);
        m_JointBox.SetActive(!m_IsBase && !m_IsLink); m_LinkBox.SetActive(m_IsLink); m_BaseBox.SetActive(m_IsBase);
        if (m_IsLink)
        {
            int li = m_LinkNames.IndexOf(g.link); if (li >= 0) m_LinkChoice.Set(li);
            float[] c = g.region.center != null && g.region.center.Length == 3 ? g.region.center : new[] { 0.3f, 0f, 0.3f };
            float[] sz = g.region.size != null && g.region.size.Length == 3 ? g.region.size : new[] { 0.2f, 0.2f, 0.2f };
            m_Cx.text = c[0].ToString("F3"); m_Cy.text = c[1].ToString("F3"); m_Cz.text = c[2].ToString("F3");
            float[] pt = g.point != null && g.point.Length == 3 ? g.point : TipOf(g.link);
            m_Px.text = pt[0].ToString("F3"); m_Py.text = pt[1].ToString("F3"); m_Pz.text = pt[2].ToString("F3");
            m_Sx.text = sz[0].ToString("F3"); m_Sy.text = sz[1].ToString("F3"); m_Sz.text = sz[2].ToString("F3");
        }
        m_Tol.minValue = m_IsBase ? 0.05f : 0.005f; m_Tol.maxValue = m_IsBase || m_IsLink ? 1f : 0.5f;
        m_Tol.value = g.tolerance > 0f ? g.tolerance : (m_IsBase ? 0.15f : 0.05f);
        m_Time.value = m_Spec.episode.time_s > 0f ? m_Spec.episode.time_s : (m_IsBase ? 10f : 2f);
        if (m_IsBase) { m_RMin.value = g.region.r_min; m_RMax.value = g.region.r_max; }
        else { bool manual = g.range != null && g.range.Length == 2; m_RangeMode.Set(manual ? 1 : 0); if (manual) m_Range.value = Mathf.Max(Mathf.Abs(g.range[0]), Mathf.Abs(g.range[1])); }
        m_Tol.onValueChanged.Invoke(m_Tol.value); m_Time.onValueChanged.Invoke(m_Time.value);
        var sb = m_Spec.start.@base;
        m_StartX.text = (sb.xy != null && sb.xy.Length > 0 ? sb.xy[0] : 0f).ToString("F2"); m_StartY.text = (sb.xy != null && sb.xy.Length > 1 ? sb.xy[1] : 0f).ToString("F2");
        m_Yaw.SetValueWithoutNotify(sb.yaw_deg != null && sb.yaw_deg.Length > 0 ? sb.yaw_deg[0] : 180f); m_YawL.text = Ui.T($"向き {m_Yaw.value:F0}°", $"yaw {m_Yaw.value:F0}°");
        DrawGoal();
        RefreshDetails();
        W.Status(Ui.T("成功の条件と制限時間を決めて「次へ」", "Set the success condition and the time limit, then Next"));
    }

    void Apply()
    {
        SpecGoal g = m_Spec.Goal0;
        g.tolerance = m_Tol.value; m_Spec.episode.time_s = m_Time.value;
        if (m_IsBase) { g.type = "base_in_region"; g.region.shape = "ring"; g.region.r_min = m_RMin.value; g.region.r_max = m_RMax.value; }
        else if (m_IsLink)
        {
            g.type = "link_near"; g.link = m_LinkNames.Count > 0 ? m_LinkNames[m_LinkChoice.Index] : "";
            g.region.shape = "box"; g.region.center = new[] { F(m_Cx, 0.3f), F(m_Cy, 0f), F(m_Cz, 0.3f) }; g.region.size = new[] { F(m_Sx, 0.2f), F(m_Sy, 0.2f), F(m_Sz, 0.2f) };
            g.point = new[] { F(m_Px, 0f), F(m_Py, 0f), F(m_Pz, 0f) };
        }
        else { g.type = "joints_near"; g.range = m_RangeMode.Index == 1 ? new[] { -m_Range.value, m_Range.value } : new float[0]; }
        m_Spec.Save(P.Abs(P.D.task));
    }

    static float F(TMP_InputField f, float d) => float.TryParse(f.text, out float v) ? v : d;

    /// <summary>種類の切替 (固定基体): 関節目標 ↔ 手先を領域へ。</summary>
    void SetKind(bool link)
    {
        m_IsLink = link;
        m_JointBox.SetActive(!link); m_LinkBox.SetActive(link);
        m_Tol.maxValue = link ? 1f : 0.5f;
        if (link && m_Spec.Goal0.type != "link_near") { m_Tol.value = 0.03f; m_Time.value = 3f; }
        else if (!link && m_Spec.Goal0.type != "joints_near") { m_Tol.value = 0.05f; m_Time.value = 2f; }
        m_Tol.onValueChanged.Invoke(m_Tol.value);
        Touch();
    }

    /// <summary>① が保存した robot_info.json から可動リンクの一覧を作る。</summary>
    void LoadLinks()
    {
        m_LinkNames.Clear();
        string p = System.IO.Path.Combine(P.Dir, "robot_info.json");
        if (System.IO.File.Exists(p))
        {
            var info = JsonUtility.FromJson<RobotInfo>(System.IO.File.ReadAllText(p));
            if (info != null) foreach (RobotLink l in info.links) { m_LinkTips[l.name] = l.tip; if (l.movable) m_LinkNames.Add(l.name); }
        }
        if (m_LinkNames.Count == 0) m_LinkNames.Add("-");
        // ボタン列を作り直す
        Transform row = m_LinkChoice.Buttons[0].transform.parent;
        foreach (Transform ch in row) UnityEngine.Object.Destroy(ch.gameObject);
        m_LinkChoice = new Ui.Choice(row.parent, m_LinkNames.ToArray(), 0, 24f);
        m_LinkChoice.Buttons[0].transform.parent.SetSiblingIndex(row.GetSiblingIndex());
        UnityEngine.Object.Destroy(row.gameObject);
        m_LinkChoice.OnChange = i => { if (m_LinkNames.Count > 0 && m_Px != null) { float[] t = TipOf(m_LinkNames[i]); m_Px.text = t[0].ToString("F3"); m_Py.text = t[1].ToString("F3"); m_Pz.text = t[2].ToString("F3"); } Touch(); };
    }

    float[] TipOf(string link) => link != null && m_LinkTips.TryGetValue(link, out float[] t) && t != null && t.Length == 3 ? t : new[] { 0f, 0f, 0f };

    public override void Tick()
    {
        if (m_EstAt > 0f && Time.realtimeSinceStartup >= m_EstAt && m_Est == null && !m_Generating)
        {
            m_EstAt = -1f; Apply();
            m_Est = W.Launch($"{W.Py} -m unirobolab task-gen {ExternalProcess.Quote(P.Abs(P.D.task))} --estimate-only 2>&1");
        }
        if (m_Est != null && m_Est.HasExited)
        {
            m_Est.WaitForExit(); string last = ""; while (m_Est.TryDequeue(out string l)) last = l; m_Est = null;
            if (last.StartsWith("{")) { float s = Ui.Num(last, "estimate_s", 0f); string why = Ui.Str(last, "estimate_why") ?? ""; m_Estimate.text = Ui.T($"学習の目安: 約 {Mathf.Max(1f, s / 60f):F0} 分 ({why}; 目標に達すれば早く終わります)", $"training estimate: about {Mathf.Max(1f, s / 60f):F0} min ({why}; ends early once the target is met)"); }
        }
        if (m_Gen != null && m_Gen.HasExited)
        {
            m_Gen.WaitForExit(); var sb = new StringBuilder(); string last = ""; while (m_Gen.TryDequeue(out string l)) { sb.AppendLine(l); last = l; }
            bool ok = m_Gen.ExitCode == 0 && last.StartsWith("{"); m_Gen = null; m_Generating = false;
            if (!ok) { W.Status(Ui.T("生成に失敗: ", "generation failed: ") + sb.ToString().Trim(), Ui.Bad); return; }
            P.D.contract = P.Rel(Ui.Str(last, "contract")); P.D.train = P.Rel(Ui.Str(last, "train"));
            P.Stamp("contract"); P.Stamp("train"); P.Save();
            RefreshDetails(); W.RefreshStepper();
            W.Status(Ui.T("契約と学習設定を作りました。③ で学習を始めます", "Contract and training config generated. Start training in step 3"), Ui.Accent);
            W.GoTo(2, false);
        }
    }

    public override bool CanProceed(out string reason) { reason = ""; return !m_Generating; }

    /// <summary>「次へ」= 仕様を保存して task-gen。完了したら ③ へ (Tick で遷移)。</summary>
    public override bool OnNext()
    {
        if (m_Generating) return false;
        Apply();
        string outDir = System.IO.Path.Combine(P.Dir, "generated");
        m_Gen = W.Launch($"{W.Py} -m unirobolab task-gen {ExternalProcess.Quote(P.Abs(P.D.task))} --out {ExternalProcess.Quote(outDir)} --name {ExternalProcess.Quote(string.IsNullOrEmpty(P.D.name) ? "robot" : P.D.name)} 2>&1");
        m_Generating = true;
        W.Status(Ui.T("契約と学習設定を生成しています...", "generating the contract and the training config..."));
        return false;
    }

    public override bool Done() => P.Exists("train") && !P.IsStale("train");
    public override bool Stale() => P.IsStale("contract") || P.IsStale("train");

    void RefreshDetails()
    {
        if (m_TrainText == null) return;
        string t = P.Abs(P.D.train);
        m_TrainText.text = System.IO.File.Exists(t) ? System.IO.File.ReadAllText(t) : Ui.T("(まだありません)", "(none yet)");
    }

    /// <summary>3D: 地点到達なら環 (緑)、関節目標なら各関節の目標範囲を弧 (緑) で描く。</summary>
    void DrawGoal()
    {
        if (m_BoxLine != null) m_BoxLine.enabled = false;
        if (m_PointMarker != null) m_PointMarker.SetActive(false);
        if (m_IsLink) { if (m_RingIn != null) { m_RingIn.enabled = false; m_RingOut.enabled = false; } foreach (LineRenderer a in m_Arcs) a.enabled = false; DrawLinkBox(); return; }
        if (!m_IsBase) { if (m_RingIn != null) { m_RingIn.enabled = false; m_RingOut.enabled = false; } DrawJointArcs(); return; }
        foreach (LineRenderer a in m_Arcs) a.enabled = false;
        if (m_RingIn == null) { m_RingIn = MakeRing("GoalRingIn"); m_RingOut = MakeRing("GoalRingOut"); }
        Vector3 c0 = new Vector3(-W.StartY, 0f, W.StartX);
        Ring(m_RingIn, m_RMin.value, c0); Ring(m_RingOut, m_RMax.value, c0);
        m_RingIn.enabled = m_RingOut.enabled = true;
        var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        if (cam != null) { cam.target = c0; cam.distance = Mathf.Max(3f, m_RMax.value * 2.2f); }
    }

    static LineRenderer MakeRing(string name)
    {
        var go = new GameObject(name, typeof(LineRenderer));
        var lr = go.GetComponent<LineRenderer>();
        lr.material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default"));
        var c = new Color(0.35f, 0.9f, 0.45f, 0.9f); lr.material.color = c; if (lr.material.HasProperty("_BaseColor")) lr.material.SetColor("_BaseColor", c);
        lr.startWidth = lr.endWidth = 0.03f; lr.loop = true; lr.useWorldSpace = true;
        return lr;
    }

    static void Ring(LineRenderer lr, float r, Vector3 c)
    {
        const int n = 64; lr.positionCount = n;
        for (int i = 0; i < n; i++) { float a = i * Mathf.PI * 2f / n; lr.SetPosition(i, c + new Vector3(Mathf.Cos(a) * r, 0.02f, Mathf.Sin(a) * r)); }
    }

    /// <summary>プレビューのロボット (① でスポーン) の回転関節ごとに、目標角の範囲を弧で描く。</summary>
    void DrawJointArcs()
    {
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        GameObject ent = null; foreach (GameObject e in buf) if (e != null) { ent = e; break; }
        int k = 0;
        if (ent != null)
        {
            float range = m_RangeMode.Index == 1 ? m_Range.value : AutoRange();
            foreach (ArticulationBody ab in ent.GetComponentsInChildren<ArticulationBody>())
            {
                if (ab.isRoot || ab.jointType != ArticulationJointType.RevoluteJoint) continue;
                if (k >= m_Arcs.Count) m_Arcs.Add(MakeRing("JointArc" + k));
                LineRenderer lr = m_Arcs[k++]; lr.loop = false; lr.startWidth = lr.endWidth = 0.012f;
                Vector3 center = ab.transform.TransformPoint(ab.anchorPosition);
                Vector3 axis = ab.transform.TransformDirection(ab.anchorRotation * Vector3.right).normalized;   // 関節の回転軸
                Vector3 radial = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up).normalized;
                float r = 0.12f; const int n = 40;
                lr.positionCount = n + 1;
                for (int i = 0; i <= n; i++)
                {
                    float a = -range + 2f * range * i / n;   // 現在角 0 を中心に ±range
                    lr.SetPosition(i, center + Quaternion.AngleAxis(a * Mathf.Rad2Deg, axis) * radial * r);
                }
                lr.enabled = true;
            }
            var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
            if (cam != null && k > 0) { var rs = ent.GetComponentsInChildren<Renderer>(); if (rs.Length > 0) { Bounds b = rs[0].bounds; foreach (Renderer rr in rs) b.Encapsulate(rr.bounds); cam.target = b.center; cam.distance = Mathf.Max(0.8f, b.extents.magnitude * 3f); } }
        }
        for (int i = k; i < m_Arcs.Count; i++) m_Arcs[i].enabled = false;
    }

    /// <summary>手先の目標領域 (根リンク座標系の箱) をロボットの根の姿勢で描く。ROS (x, y, z) → Unity (-y, z, x)。</summary>
    void DrawLinkBox()
    {
        if (m_BoxLine == null) { m_BoxLine = MakeRing("LinkGoalBox"); m_BoxLine.loop = false; m_BoxLine.startWidth = m_BoxLine.endWidth = 0.008f; }
        Vector3 c = new Vector3(F(m_Cx, 0.3f), F(m_Cy, 0f), F(m_Cz, 0.3f)), h = 0.5f * new Vector3(F(m_Sx, 0.2f), F(m_Sy, 0.2f), F(m_Sz, 0.2f));
        // ロボットの根 (① でスポーンしたエンティティ) の姿勢
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        Vector3 origin = new Vector3(-W.StartY, 0f, W.StartX); Quaternion rot = Quaternion.Euler(0f, -W.StartYawDeg, 0f);
        Vector3 U(float x, float y, float z) => origin + rot * new Vector3(-y, z, x);
        var pts = new List<Vector3>();
        Vector3[] k = { new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1), new Vector3(-1, -1, -1),
                        new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1), new Vector3(-1, -1, 1),
                        new Vector3(1, -1, 1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1), new Vector3(-1, 1, -1) };
        foreach (Vector3 s in k) pts.Add(U(c.x + s.x * h.x, c.y + s.y * h.y, c.z + s.z * h.z));
        m_BoxLine.positionCount = pts.Count; m_BoxLine.SetPositions(pts.ToArray()); m_BoxLine.enabled = true;
        // リンク上の点: プレビューのロボットのリンク (URDF 名の GameObject) から、ROS のローカル座標 (x, y, z) → Unity (-y, z, x)
        string link = m_LinkNames.Count > 0 ? m_LinkNames[m_LinkChoice.Index] : null;
        Transform lt = null;
        foreach (GameObject e in buf) { if (e == null) continue; foreach (Transform tr in e.GetComponentsInChildren<Transform>()) if (tr.name == link) { lt = tr; break; } if (lt != null) break; }
        if (lt != null)
        {
            if (m_PointMarker == null)
            {
                m_PointMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere); m_PointMarker.name = "LinkPoint";
                UnityEngine.Object.Destroy(m_PointMarker.GetComponent<Collider>());
                m_PointMarker.transform.localScale = Vector3.one * 0.02f;
                var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default")); var gc = new Color(0.3f, 0.9f, 0.4f, 1f);
                mat.color = gc; if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", gc); m_PointMarker.GetComponent<Renderer>().material = mat;
            }
            m_PointMarker.transform.position = lt.TransformPoint(new Vector3(-F(m_Py, 0f), F(m_Pz, 0f), F(m_Px, 0f)));
            m_PointMarker.SetActive(true);
        }
        var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        if (cam != null) { cam.target = U(c.x, c.y, c.z); cam.distance = Mathf.Max(0.8f, h.magnitude * 6f); }
    }

    /// <summary>「自動」のときの目標範囲 (可動範囲の 80 %) を表示用に見積もる: 生成された学習設定があればそれ、無ければ 1.0。</summary>
    float AutoRange()
    {
        string t = P.Abs(P.D.train);
        if (System.IO.File.Exists(t))
        {
            string json = System.IO.File.ReadAllText(t);
            int i = json.IndexOf("\"goal_range\"", System.StringComparison.Ordinal);
            if (i >= 0) { int a = json.IndexOf('[', i), b = json.IndexOf(']', a); if (a > 0 && b > a) { string[] p = json.Substring(a + 1, b - a - 1).Split(','); if (p.Length == 2 && float.TryParse(p[1], out float hi)) return Mathf.Abs(hi); } }
        }
        return 1.0f;
    }

    public override void Leave()
    {
        if (m_RingIn != null) { m_RingIn.enabled = false; m_RingOut.enabled = false; }
        foreach (LineRenderer a in m_Arcs) a.enabled = false;
        if (m_BoxLine != null) m_BoxLine.enabled = false;
        if (m_PointMarker != null) m_PointMarker.SetActive(false);
    }
    public override void Action(string name) { if (name == "next") OnNext(); }
}
