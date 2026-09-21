using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>② タスク: 開始条件と終了条件を決める (関節が目標角に / 手先が領域内 / 物体が領域内 / 基体が領域内)。契約と学習設定はここで生成。</summary>
public class PhaseTask : Phase
{
    public override string Key => "task";
    public override string Title => Ui.T("② タスク", "2 Task");

    TaskSpec m_Spec;
    bool m_IsBase, m_IsLink, m_IsObject;
    Ui.Choice m_KindChoice;           // 固定基体: 関節目標 / 手先を領域へ / 物体を領域へ
    // object_in_region: 物体 (形・大きさ・質量)、押すリンク、開始位置 (中心とばらつき)、目標領域 (中心と大きさ)。z は地面 (0)
    GameObject m_ObjectBox;
    Ui.Choice m_ShapeChoice, m_HandChoice;
    TMP_InputField m_OSx, m_OSy, m_OSz, m_OMass, m_OStartX, m_OStartY, m_OVarX, m_OVarY, m_OGx, m_OGy, m_OGsx, m_OGsy;
    LineRenderer m_StartLine;
    GameObject m_ObjPreview;
    GameObject m_LinkBox;
    Ui.Choice m_LinkChoice; readonly List<string> m_LinkNames = new List<string>();
    TMP_InputField m_Cx, m_Cy, m_Cz, m_Sx, m_Sy, m_Sz, m_Px, m_Py, m_Pz;
    LineRenderer m_BoxLine;
    GameObject m_PointMarker;
    readonly Dictionary<string, float[]> m_LinkTips = new Dictionary<string, float[]>();
    TMP_Text m_Kind, m_KindL, m_RangeL, m_TolL, m_TimeL, m_RMinL, m_RMaxL, m_Estimate, m_TrainText, m_Pending;
    Slider m_Range, m_Tol, m_Time, m_RMin, m_RMax, m_Yaw;
    TMP_InputField m_StartX, m_StartY; TMP_Text m_YawL;
    Ui.Choice m_StartJoints; Slider m_StartFrac; TMP_Text m_StartFracL; GameObject m_StartFracRow;   // 開始姿勢: ゼロ / ばらつかせる
    Ui.Choice m_Randomize, m_History;   // 物理のばらつき (質量・摩擦・駆動) と履歴窓
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
        m_KindL = Ui.Label(kr.transform, Ui.T("何をさせるか", "Task kind"), 12f, Ui.Muted, false, 0f, 110f);
        m_KindChoice = new Ui.Choice(kr.transform, new[] { Ui.T("関節を目標角へ", "joints to target angles"), Ui.T("手先を所定の場所へ", "a link to a target region"), Ui.T("物体を所定の場所へ", "an object to a target region") }, 0, 24f);
        m_KindChoice.OnChange = i => { if (m_Spec != null && !m_IsBase) SetKind(i); };
        Ui.Label(Root.transform, Ui.T("開始の位置と向き (ロボットはここからスタート。学習・チェックでも同じ)", "Start pose (used for training and the check as well)"), 12f, Ui.Muted);
        var sp = Ui.Row(Root.transform, 26f);
        Ui.Label(sp.transform, "x [m]", 12f, Ui.Text, false, 0f, 44f); m_StartX = Ui.Input(sp.transform, "0", 24f);
        Ui.Label(sp.transform, "y [m]", 12f, Ui.Text, false, 0f, 44f); m_StartY = Ui.Input(sp.transform, "0", 24f);
        m_YawL = Ui.Label(sp.transform, "", 12f, Ui.Text, false, 0f, 130f);
        m_Yaw = Ui.Slider(Root.transform, -180f, 180f, 180f, v => { m_YawL.text = Ui.T($"向き {v:F0}°", $"yaw {v:F0}°"); TouchStart(); }, true);
        m_StartX.onEndEdit.AddListener(_ => TouchStart()); m_StartY.onEndEdit.AddListener(_ => TouchStart());
        var sj = Ui.Row(Root.transform, 26f);
        Ui.Label(sj.transform, Ui.T("開始の関節姿勢", "Start joint pose"), 12f, Ui.Text, false, 0f, 110f);
        m_StartJoints = new Ui.Choice(sj.transform, new[] { Ui.T("ゼロ (URDF の原点)", "zero (URDF origin)"), Ui.T("毎回ばらつかせる", "randomized every attempt") }, 0, 24f);
        m_StartJoints.OnChange = i => { if (m_StartFracRow != null) m_StartFracRow.SetActive(i == 1); TouchStart(); };
        m_StartFracRow = Ui.Row(Root.transform, 26f);
        m_StartFracL = Ui.Label(m_StartFracRow.transform, "", 12f, Ui.Text, false, 0f, 220f);
        m_StartFrac = Ui.Slider(m_StartFracRow.transform, 0.05f, 1f, 0.15f, v => { m_StartFracL.text = Ui.T($"可動範囲の {v * 100f:F0} % の中から抽選", $"sampled inside {v * 100f:F0} % of the joint range"); TouchStart(); });
        m_StartFracRow.SetActive(false);
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
        // object_in_region (物体を領域へ): 物体、押すリンク、開始位置、目標領域 (根リンク座標系 [m]、地面上)
        m_ObjectBox = Ui.Column(Root.transform, "Object", 4f, 0, false);
        var orow = Ui.Row(m_ObjectBox.transform, 26f);
        Ui.Label(orow.transform, Ui.T("物体", "Object"), 12f, Ui.Text, false, 0f, 110f);
        m_ShapeChoice = new Ui.Choice(orow.transform, new[] { Ui.T("箱", "box"), Ui.T("球", "sphere"), Ui.T("円柱", "cylinder") }, 0, 24f);
        m_ShapeChoice.OnChange = _ => Touch();
        var osz = Ui.Row(m_ObjectBox.transform, 26f);
        Ui.Label(osz.transform, Ui.T("大きさ [m] / 質量 [kg]", "Size [m] / mass [kg]"), 12f, Ui.Text, false, 0f, 110f);
        m_OSx = Ui.Input(osz.transform, "x", 24f); m_OSy = Ui.Input(osz.transform, "y", 24f); m_OSz = Ui.Input(osz.transform, "z", 24f); m_OMass = Ui.Input(osz.transform, "kg", 24f);
        var hrow = Ui.Row(m_ObjectBox.transform, 26f);
        Ui.Label(hrow.transform, Ui.T("押すリンク", "Pushing link"), 12f, Ui.Text, false, 0f, 110f);
        m_HandChoice = new Ui.Choice(hrow.transform, new[] { "-" }, 0, 24f);
        var ost = Ui.Row(m_ObjectBox.transform, 26f);
        Ui.Label(ost.transform, Ui.T("開始位置 x, y [m] ± ばらつき", "Start x, y [m] ± spread"), 12f, Ui.Text, false, 0f, 110f);
        m_OStartX = Ui.Input(ost.transform, "x", 24f); m_OStartY = Ui.Input(ost.transform, "y", 24f); m_OVarX = Ui.Input(ost.transform, "±x", 24f); m_OVarY = Ui.Input(ost.transform, "±y", 24f);
        var ogl = Ui.Row(m_ObjectBox.transform, 26f);
        Ui.Label(ogl.transform, Ui.T("目標領域 中心 x, y / 大きさ [m]", "Goal region center x, y / size [m]"), 12f, Ui.Text, false, 0f, 110f);
        m_OGx = Ui.Input(ogl.transform, "x", 24f); m_OGy = Ui.Input(ogl.transform, "y", 24f); m_OGsx = Ui.Input(ogl.transform, "w", 24f); m_OGsy = Ui.Input(ogl.transform, "d", 24f);
        foreach (var f in new[] { m_OSx, m_OSy, m_OSz, m_OMass, m_OStartX, m_OStartY, m_OVarX, m_OVarY, m_OGx, m_OGy, m_OGsx, m_OGsy }) f.onEndEdit.AddListener(_ => Touch());
        Ui.Label(m_ObjectBox.transform, Ui.T("物体 (橙) を緑の枠へ押し込めば成功。手先の姿勢や関節角は問いません (座標はロボットの根リンク基準)",
                                              "Push the object (orange) into the green frame; hand pose and joint angles do not matter (root-link frame)"), 11f, Ui.Muted, false, 22f);
        // base_in_region
        m_BaseBox = Ui.Column(Root.transform, "Base", 4f, 0, false);
        m_RMinL = Ui.Label(m_BaseBox.transform, "", 12f, Ui.Text);
        m_RMin = Ui.Slider(m_BaseBox.transform, 0.2f, 5f, 1f, v => { if (m_RMax != null && m_RMax.value < v + 0.2f) m_RMax.value = v + 0.2f; m_RMinL.text = Ui.T($"目標地点: 開始位置から {v:F1} m 以上", $"goal: at least {v:F1} m from the start"); Touch(); });
        m_RMaxL = Ui.Label(m_BaseBox.transform, "", 12f, Ui.Text);
        m_RMax = Ui.Slider(m_BaseBox.transform, 0.4f, 6f, 2.5f, v => { if (m_RMin != null && m_RMin.value > v - 0.2f) m_RMin.value = v - 0.2f; m_RMaxL.text = Ui.T($"目標地点: 開始位置から {v:F1} m 以内 (環の中から抽選)", $"goal: within {v:F1} m of the start (sampled in the ring)"); Touch(); });
        // common
        m_TolL = Ui.Label(Root.transform, "", 12f, Ui.Text);
        m_Tol = Ui.Slider(Root.transform, 0.01f, 0.5f, 0.05f, v => { m_TolL.text = m_IsBase || m_IsLink || m_IsObject ? Ui.T($"成功: 目標から {v:F3} m 以内", $"success: within {v:F3} m") : Ui.T($"成功: 目標角から {v:F3} rad 以内", $"success: within {v:F3} rad"); Touch(); });
        m_TimeL = Ui.Label(Root.transform, "", 12f, Ui.Text);
        m_Time = Ui.Slider(Root.transform, 0.5f, 30f, 2f, v => { m_TimeL.text = Ui.T($"1 回の制限時間: {v:F1} 秒", $"time per attempt: {v:F1} s"); Touch(); });
        var rz = Ui.Row(Root.transform, 26f);
        Ui.Label(rz.transform, Ui.T("物理のばらつき", "Physics randomization"), 12f, Ui.Text, false, 0f, 110f);
        m_Randomize = new Ui.Choice(rz.transform, new[] { Ui.T("なし", "off"), Ui.T("あり (質量・摩擦・駆動)", "on (mass, friction, drive)") }, 0, 24f);
        m_Randomize.OnChange = _ => Touch();
        var hr = Ui.Row(Root.transform, 26f);
        Ui.Label(hr.transform, Ui.T("方策の履歴窓", "Policy history window"), 12f, Ui.Text, false, 0f, 110f);
        m_History = new Ui.Choice(hr.transform, new[] { Ui.T("今の観測だけ", "current observation only"), Ui.T("直近 8 回 (状況を推定できる)", "last 8 frames (infers the situation)") }, 0, 24f);
        m_History.OnChange = _ => Touch();
        Ui.Label(Root.transform, Ui.T("実機とのずれ (物体の重さ・滑りやすさ、モータの応答) に強くするなら両方を入れます。質量 ×0.5〜2、摩擦 0.2〜1.0、駆動 ×0.7〜1.3 で毎回変わります。学習は少し長くなります", "Turn both on for robustness to sim-to-real gaps: mass x0.5-2, friction 0.2-1.0 and drive gain x0.7-1.3 change every attempt. Training takes a little longer"), 11f, Ui.Muted, true, 30f);
        m_Estimate = Ui.Label(Root.transform, "", 12f, Ui.Accent, true, 40f);
        m_Pending = Ui.Label(Root.transform, "", 12f, Ui.Warn, true, 34f);
        Ui.Label(Root.transform, Ui.T("「次へ」で契約と学習設定を生成します (学習はまだ始めません)。", "Next generates the contract and the training config (training does not start yet)."), 11f, Ui.Muted, true, 30f);
        Ui.Spacer(Root.transform);

        DetailsRoot = Ui.Column(details, "TaskDetails", 4f);
        Ui.Label(DetailsRoot.transform, Ui.T("生成される学習設定 (train.json)", "Generated training config (train.json)"), 12f, Ui.Muted);
        m_TrainText = Ui.Label(DetailsRoot.transform, "", 10f, Ui.Text, true, 0f);
        m_TrainText.GetComponent<LayoutElement>().flexibleHeight = 1f; m_TrainText.alignment = TextAlignmentOptions.TopLeft;
    }

    void Touch() { m_EstAt = Time.realtimeSinceStartup + 0.6f; DrawGoal(); }

    /// <summary>ここで変えた値は task.json にすぐ入るが、契約と学習設定は「次へ」で作り直すまで古いままなので、それを出す。</summary>
    void RefreshPending()
    {
        if (m_Pending == null) return;
        bool pending = P.Exists("train") && P.IsStale("train");
        m_Pending.text = pending
            ? Ui.T("変更はまだ ③ 以降に反映されていません。「次へ」で契約と学習設定を作り直します (学習・チェックもやり直しになります)",
                   "The change has not reached step 3 yet. Next regenerates the contract and the training config (training and the check have to be redone)")
            : "";
    }

    /// <summary>開始の位置・向きを仕様に書き、プレビューのロボットをそこへ置く。</summary>
    void TouchStart()
    {
        if (m_Spec == null) return;
        float.TryParse(m_StartX.text, out float x); float.TryParse(m_StartY.text, out float y);
        m_Spec.start.@base.xy = new[] { x, y }; m_Spec.start.@base.yaw_deg = new[] { m_Yaw.value, m_Yaw.value };
        m_Spec.start.joints = m_StartJoints.Index == 1 ? "random" : "zero"; m_Spec.start.joints_fraction = m_StartFrac.value;
        m_EstAt = Time.realtimeSinceStartup + 0.6f;
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

    /// <summary>② のプレビュー用のロボット。⑤ はエンティティを消すので、戻ってきたときに居ないことがある。</summary>
    void EnsurePreviewRobot()
    {
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        foreach (GameObject e in buf) if (e != null && !e.name.Contains("__")) return;   // "__" は物体
        if (!P.Has(P.D.urdf) || !W.Sim.CanSpawnFromGui) return;
        if (W.Sim.TrySpawnRobotFromUrdf(P.Abs(P.D.urdf), out string name, out _)) W.PlaceSpawned(name);
    }

    /// <summary>ロボットと目標をまとめて画角に入れる。目標だけを見ると「届く範囲か」が判断できない。</summary>
    void FrameRobotAndGoal(Bounds goal)
    {
        var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        if (cam == null) return;
        Bounds b = goal;
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        foreach (GameObject e in buf)
        {
            if (e == null) continue;
            foreach (Renderer r in e.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
        }
        cam.Frame(b);
    }

    public override void Enter()
    {
        EnsurePreviewRobot();
        // 斜め上から見る。真横だと台座や板でロボットの構造 (関節の並び) が隠れて分からない
        var cam0 = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        if (cam0 != null) cam0.SetAngle(35f, 30f);
        m_Spec = TaskSpec.Load(P.Abs(P.D.task));
        SpecGoal g = m_Spec.Goal0;
        m_IsBase = g.type == "base_in_region";
        m_IsLink = g.type == "link_near";
        m_IsObject = g.type == "object_in_region";
        m_Kind.text = m_IsBase ? Ui.T("このロボットは移動基体: 「所定の場所へ動く」タスク", "Mobile base: a reach-a-point task")
                               : Ui.T("このロボットは固定基体", "Fixed base");
        m_KindChoice.Buttons[0].transform.parent.gameObject.SetActive(!m_IsBase);
        if (m_KindL != null) m_KindL.gameObject.SetActive(!m_IsBase);
        LoadLinks();
        m_KindChoice.Set(m_IsObject ? 2 : (m_IsLink ? 1 : 0));
        m_JointBox.SetActive(!m_IsBase && !m_IsLink && !m_IsObject); m_LinkBox.SetActive(m_IsLink); m_ObjectBox.SetActive(m_IsObject); m_BaseBox.SetActive(m_IsBase);
        if (m_IsObject)
        {
            SpecObject o = m_Spec.Object0;
            m_ShapeChoice.Set(o.shape == "sphere" ? 1 : (o.shape == "cylinder" ? 2 : 0));
            float[] os = o.size != null && o.size.Length == 3 ? o.size : new[] { 0.05f, 0.05f, 0.05f };
            m_OSx.text = os[0].ToString("F3"); m_OSy.text = os[1].ToString("F3"); m_OSz.text = os[2].ToString("F3"); m_OMass.text = o.mass.ToString("F2");
            int hi = m_LinkNames.IndexOf(g.hand); m_HandChoice.Set(hi >= 0 ? hi : Mathf.Max(0, m_LinkNames.Count - 1));
            float[] sc = o.start != null && o.start.center != null && o.start.center.Length == 3 ? o.start.center : new[] { 0.3f, 0f, 0f };
            float[] sv = o.start != null && o.start.size != null && o.start.size.Length == 3 ? o.start.size : new[] { 0.1f, 0.1f, 0f };
            m_OStartX.text = sc[0].ToString("F3"); m_OStartY.text = sc[1].ToString("F3"); m_OVarX.text = (0.5f * sv[0]).ToString("F3"); m_OVarY.text = (0.5f * sv[1]).ToString("F3");
            float[] gc = g.region.center != null && g.region.center.Length == 3 ? g.region.center : new[] { 0.5f, 0f, 0f };
            float[] gs = g.region.size != null && g.region.size.Length == 3 ? g.region.size : new[] { 0.2f, 0.2f, 0f };
            m_OGx.text = gc[0].ToString("F3"); m_OGy.text = gc[1].ToString("F3"); m_OGsx.text = gs[0].ToString("F3"); m_OGsy.text = gs[1].ToString("F3");
        }
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
        m_Tol.minValue = m_IsBase ? 0.05f : 0.005f; m_Tol.maxValue = m_IsBase || m_IsLink || m_IsObject ? 1f : 0.5f;
        m_Tol.value = g.tolerance > 0f ? g.tolerance : (m_IsBase ? 0.15f : 0.05f);
        m_Time.value = m_Spec.episode.time_s > 0f ? m_Spec.episode.time_s : (m_IsBase ? 10f : 2f);
        var rzs = m_Spec.training.randomize;
        m_Randomize.Set(rzs != null && ((rzs.object_mass != null && rzs.object_mass.Length == 2) || (rzs.drive_gain != null && rzs.drive_gain.Length == 2)) ? 1 : 0);
        m_History.Set(m_Spec.training.history_length > 1 ? 1 : 0);
        if (m_IsBase)
        {
            m_RMin.value = g.region.r_min > 0f ? g.region.r_min : m_RMin.value;
            m_RMax.value = g.region.r_max > 0f ? g.region.r_max : m_RMax.value;
            m_RMin.onValueChanged.Invoke(m_RMin.value); m_RMax.onValueChanged.Invoke(m_RMax.value);
        }
        else { bool manual = g.range != null && g.range.Length == 2; m_RangeMode.Set(manual ? 1 : 0); if (manual) m_Range.value = Mathf.Max(Mathf.Abs(g.range[0]), Mathf.Abs(g.range[1])); }
        m_Tol.onValueChanged.Invoke(m_Tol.value); m_Time.onValueChanged.Invoke(m_Time.value);
        var sb = m_Spec.start.@base;
        m_StartX.text = (sb.xy != null && sb.xy.Length > 0 ? sb.xy[0] : 0f).ToString("F2"); m_StartY.text = (sb.xy != null && sb.xy.Length > 1 ? sb.xy[1] : 0f).ToString("F2");
        m_Yaw.SetValueWithoutNotify(sb.yaw_deg != null && sb.yaw_deg.Length > 0 ? sb.yaw_deg[0] : 180f); m_YawL.text = Ui.T($"向き {m_Yaw.value:F0}°", $"yaw {m_Yaw.value:F0}°");
        m_StartJoints.Set(m_Spec.start.joints == "random" ? 1 : 0); m_StartFracRow.SetActive(m_Spec.start.joints == "random");
        m_StartFrac.SetValueWithoutNotify(m_Spec.start.joints_fraction > 0f ? m_Spec.start.joints_fraction : 0.15f); m_StartFracL.text = Ui.T($"可動範囲の {m_StartFrac.value * 100f:F0} % の中から抽選", $"sampled inside {m_StartFrac.value * 100f:F0} % of the joint range");
        DrawGoal();
        RefreshPending();
        RefreshDetails();
        W.Status(Ui.T("成功の条件と制限時間を決めて「次へ」", "Set the success condition and the time limit, then Next"));
    }

    void Apply()
    {
        SpecGoal g = m_Spec.Goal0;
        g.tolerance = m_Tol.value; m_Spec.episode.time_s = m_Time.value;
        if (m_Spec.training.randomize == null) m_Spec.training.randomize = new SpecRandomize();
        if (m_Randomize.Index == 1)
        {
            m_Spec.training.randomize.drive_gain = new[] { 0.7f, 1.3f };
            bool obj = m_IsObject; m_Spec.training.randomize.object_mass = obj ? new[] { 0.5f, 2f } : new float[0]; m_Spec.training.randomize.object_friction = obj ? new[] { 0.2f, 1f } : new float[0];
        }
        else { m_Spec.training.randomize.drive_gain = new float[0]; m_Spec.training.randomize.object_mass = new float[0]; m_Spec.training.randomize.object_friction = new float[0]; }
        m_Spec.training.history_length = m_History.Index == 1 ? 8 : 1;
        if (m_IsBase) { g.type = "base_in_region"; g.region.shape = "ring"; g.region.r_min = m_RMin.value; g.region.r_max = m_RMax.value; }
        else if (m_IsLink)
        {
            g.type = "link_near"; g.link = m_LinkNames.Count > 0 ? m_LinkNames[m_LinkChoice.Index] : "";
            g.region.shape = "box"; g.region.center = new[] { F(m_Cx, 0.3f), F(m_Cy, 0f), F(m_Cz, 0.3f) }; g.region.size = new[] { F(m_Sx, 0.2f), F(m_Sy, 0.2f), F(m_Sz, 0.2f) };
            g.point = new[] { F(m_Px, 0f), F(m_Py, 0f), F(m_Pz, 0f) };
        }
        else if (m_IsObject)
        {
            SpecObject o = m_Spec.Object0;
            o.shape = m_ShapeChoice.Index == 1 ? "sphere" : (m_ShapeChoice.Index == 2 ? "cylinder" : "box");
            o.size = new[] { F(m_OSx, 0.05f), F(m_OSy, 0.05f), F(m_OSz, 0.05f) }; o.mass = Mathf.Max(0.001f, F(m_OMass, 0.1f));
            if (o.start == null) o.start = new SpecObjectStart();
            o.start.center = new[] { F(m_OStartX, 0.3f), F(m_OStartY, 0f), 0f }; o.start.size = new[] { 2f * Mathf.Abs(F(m_OVarX, 0.05f)), 2f * Mathf.Abs(F(m_OVarY, 0.05f)), 0f };
            g.type = "object_in_region"; g.@object = o.name; g.hand = m_LinkNames.Count > 0 && m_LinkNames[0] != "-" ? m_LinkNames[m_HandChoice.Index] : "";
            g.region.shape = "box"; g.region.center = new[] { F(m_OGx, 0.5f), F(m_OGy, 0f), 0f }; g.region.size = new[] { Mathf.Abs(F(m_OGsx, 0.2f)), Mathf.Abs(F(m_OGsy, 0.2f)), 0f };
        }
        else { g.type = "joints_near"; g.range = m_RangeMode.Index == 1 ? new[] { -m_Range.value, m_Range.value } : new float[0]; }
        m_Spec.Save(P.Abs(P.D.task));
    }

    static float F(TMP_InputField f, float d) => float.TryParse(f.text, out float v) ? v : d;

    /// <summary>種類の切替 (固定基体): 0 関節目標 / 1 手先を領域へ / 2 物体を領域へ。</summary>
    void SetKind(int kind)
    {
        m_IsLink = kind == 1; m_IsObject = kind == 2;
        m_JointBox.SetActive(kind == 0); m_LinkBox.SetActive(m_IsLink); m_ObjectBox.SetActive(m_IsObject);
        m_Tol.maxValue = kind == 0 ? 0.5f : 1f;
        string t = m_Spec.Goal0.type;
        if (m_IsLink && t != "link_near") { m_Tol.value = 0.03f; m_Time.value = 3f; }
        else if (m_IsObject && t != "object_in_region")
        {
            m_Tol.value = 0.05f; m_Time.value = 6f;
            if (m_Spec.objects.Count == 0)
            {
                // 初期値: 物体は手先の届く範囲の手前、目標はその先 (押すリンクの先端の距離を目安に)
                float[] tip = TipOf(m_LinkNames.Count > 0 ? m_LinkNames[Mathf.Max(0, m_LinkNames.Count - 1)] : null);
                float r = Mathf.Max(0.2f, 0.8f * Mathf.Sqrt(tip[0] * tip[0] + tip[1] * tip[1] + tip[2] * tip[2]));
                m_OStartX.text = (0.6f * r).ToString("F3"); m_OStartY.text = "0.000"; m_OVarX.text = "0.030"; m_OVarY.text = "0.030";
                m_OGx.text = r.ToString("F3"); m_OGy.text = "0.000"; m_OGsx.text = "0.120"; m_OGsy.text = "0.120";
                m_OSx.text = m_OSy.text = m_OSz.text = "0.050"; m_OMass.text = "0.10"; m_HandChoice.Set(Mathf.Max(0, m_LinkNames.Count - 1));
            }
        }
        else if (kind == 0 && t != "joints_near") { m_Tol.value = 0.05f; m_Time.value = 2f; }
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
        Transform hrow = m_HandChoice.Buttons[0].transform.parent;
        int hidx = hrow.GetSiblingIndex(); Transform hparent = hrow.parent;
        UnityEngine.Object.Destroy(hrow.gameObject);
        m_HandChoice = new Ui.Choice(hparent, m_LinkNames.ToArray(), Mathf.Max(0, m_LinkNames.Count - 1), 24f);
        m_HandChoice.Buttons[0].transform.parent.SetSiblingIndex(hidx);
        m_HandChoice.OnChange = _ => Touch();
    }

    float[] TipOf(string link) => link != null && m_LinkTips.TryGetValue(link, out float[] t) && t != null && t.Length == 3 ? t : new[] { 0f, 0f, 0f };

    public override void Tick()
    {
        if (m_EstAt > 0f && Time.realtimeSinceStartup >= m_EstAt && m_Est == null && !m_Generating)
        {
            m_EstAt = -1f; Apply();
            RefreshPending(); W.RefreshStepper();
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
        if (m_StartLine != null) m_StartLine.enabled = false;
        if (m_ObjPreview != null) m_ObjPreview.SetActive(false);
        if (m_IsObject) { if (m_RingIn != null) { m_RingIn.enabled = false; m_RingOut.enabled = false; } foreach (LineRenderer a in m_Arcs) a.enabled = false; DrawObjectScene(); return; }
        if (m_IsLink) { if (m_RingIn != null) { m_RingIn.enabled = false; m_RingOut.enabled = false; } foreach (LineRenderer a in m_Arcs) a.enabled = false; DrawLinkBox(); return; }
        if (!m_IsBase) { if (m_RingIn != null) { m_RingIn.enabled = false; m_RingOut.enabled = false; } DrawJointArcs(); return; }
        foreach (LineRenderer a in m_Arcs) a.enabled = false;
        if (m_RingIn == null) { m_RingIn = MakeRing("GoalRingIn"); m_RingOut = MakeRing("GoalRingOut"); }
        Vector3 c0 = new Vector3(-W.StartY, 0f, W.StartX);
        Ring(m_RingIn, m_RMin.value, c0); Ring(m_RingOut, m_RMax.value, c0);
        m_RingIn.enabled = m_RingOut.enabled = true;
        FrameRobotAndGoal(new Bounds(c0, new Vector3(2f * m_RMax.value, 0.4f, 2f * m_RMax.value)));
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
            if (k > 0) FrameRobotAndGoal(new Bounds(ent.transform.position, Vector3.one * 0.2f));
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
        FrameRobotAndGoal(new Bounds(U(c.x, c.y, c.z), 2f * new Vector3(h.magnitude, h.magnitude, h.magnitude)));
    }

    /// <summary>物体タスク: 開始の枠 (橙)、物体のプレビュー (橙、開始位置の中心)、目標の枠 (緑) を地面に描く。根リンク座標系 → Unity は DrawLinkBox と同じ。</summary>
    void DrawObjectScene()
    {
        Vector3 origin = new Vector3(-W.StartY, 0f, W.StartX); Quaternion rot = Quaternion.Euler(0f, -W.StartYawDeg, 0f);
        Vector3 U(float x, float y, float z) => origin + rot * new Vector3(-y, z, x);
        void Rect(LineRenderer lr, float cx, float cy, float hx, float hy)
        {
            var pts = new[] { U(cx - hx, cy - hy, 0.005f), U(cx + hx, cy - hy, 0.005f), U(cx + hx, cy + hy, 0.005f), U(cx - hx, cy + hy, 0.005f) };
            lr.loop = true; lr.positionCount = 4; lr.SetPositions(pts); lr.enabled = true;
        }
        if (m_BoxLine == null) { m_BoxLine = MakeRing("LinkGoalBox"); m_BoxLine.startWidth = m_BoxLine.endWidth = 0.008f; }
        if (m_StartLine == null)
        {
            m_StartLine = MakeRing("ObjectStartBox"); m_StartLine.startWidth = m_StartLine.endWidth = 0.008f;
            var oc = new Color(1f, 0.6f, 0.2f, 0.9f); m_StartLine.material.color = oc; if (m_StartLine.material.HasProperty("_BaseColor")) m_StartLine.material.SetColor("_BaseColor", oc);
        }
        float gx = F(m_OGx, 0.5f), gy = F(m_OGy, 0f), ghx = 0.5f * Mathf.Abs(F(m_OGsx, 0.2f)), ghy = 0.5f * Mathf.Abs(F(m_OGsy, 0.2f));
        float sx = F(m_OStartX, 0.3f), sy = F(m_OStartY, 0f), shx = Mathf.Abs(F(m_OVarX, 0.05f)), shy = Mathf.Abs(F(m_OVarY, 0.05f));
        Rect(m_BoxLine, gx, gy, Mathf.Max(ghx, 0.01f), Mathf.Max(ghy, 0.01f));
        Rect(m_StartLine, sx, sy, Mathf.Max(shx, 0.01f), Mathf.Max(shy, 0.01f));
        // 物体のプレビュー: 形と大きさどおりの原始形状 (物理なし)
        PrimitiveType pt = m_ShapeChoice.Index == 1 ? PrimitiveType.Sphere : (m_ShapeChoice.Index == 2 ? PrimitiveType.Cylinder : PrimitiveType.Cube);
        if (m_ObjPreview == null || m_ObjPreview.name != "ObjectPreview" + pt)
        {
            if (m_ObjPreview != null) UnityEngine.Object.Destroy(m_ObjPreview);
            m_ObjPreview = GameObject.CreatePrimitive(pt); m_ObjPreview.name = "ObjectPreview" + pt;
            UnityEngine.Object.Destroy(m_ObjPreview.GetComponent<Collider>());
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default")); var oc = new Color(0.95f, 0.55f, 0.2f, 1f);
            mat.color = oc; if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", oc); m_ObjPreview.GetComponent<Renderer>().material = mat;
        }
        float ox = Mathf.Abs(F(m_OSx, 0.05f)), oy = Mathf.Abs(F(m_OSy, 0.05f)), oz = Mathf.Abs(F(m_OSz, 0.05f));
        float h = m_ShapeChoice.Index == 1 ? 2f * ox : (m_ShapeChoice.Index == 2 ? oy : oz);   // 球: size[0] は半径、円柱: (半径, 高さ)
        m_ObjPreview.transform.localScale = m_ShapeChoice.Index == 1 ? Vector3.one * 2f * ox : (m_ShapeChoice.Index == 2 ? new Vector3(2f * ox, 0.5f * oy, 2f * ox) : new Vector3(oy, oz, ox));
        m_ObjPreview.transform.position = U(sx, sy, 0.5f * h); m_ObjPreview.transform.rotation = rot;
        m_ObjPreview.SetActive(true);
        var span = new Bounds(U(sx, sy, 0f), Vector3.one * 0.1f);
        span.Encapsulate(U(gx, gy, 0f)); span.Expand(2f * Mathf.Max(ghx, shx) + 0.2f);
        FrameRobotAndGoal(span);
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
        if (m_StartLine != null) m_StartLine.enabled = false;
        if (m_ObjPreview != null) m_ObjPreview.SetActive(false);
    }
    public override void Action(string name) { if (name == "next") OnNext(); }
}
