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
    bool m_IsBase;
    TMP_Text m_Kind, m_RangeL, m_TolL, m_TimeL, m_RMinL, m_RMaxL, m_Estimate, m_TrainText;
    Slider m_Range, m_Tol, m_Time, m_RMin, m_RMax;
    GameObject m_JointBox, m_BaseBox;
    Ui.Choice m_RangeMode;
    ExternalProcess m_Gen, m_Est;
    float m_EstAt = -1f;
    bool m_Generating;
    LineRenderer m_RingIn, m_RingOut;

    public override void Build(RectTransform main, RectTransform details)
    {
        Root = Ui.Column(main, "Task", 6f);
        m_Kind = Ui.Label(Root.transform, "", 13f, Ui.Header);
        Ui.Label(Root.transform, Ui.T("開始: ロボットは今の姿勢・位置から (v1 では固定)", "Start: the robot's current pose and position (fixed in v1)"), 12f, Ui.Muted);
        Ui.Label(Root.transform, Ui.T("終了 (成功) の条件", "Goal (success) condition"), 12f, Ui.Muted);
        // joints_near
        m_JointBox = Ui.Column(Root.transform, "Joint", 4f, 0, false);
        var rm = Ui.Row(m_JointBox.transform, 26f);
        Ui.Label(rm.transform, Ui.T("目標角の範囲", "Goal range"), 12f, Ui.Text, false, 0f, 110f);
        m_RangeMode = new Ui.Choice(rm.transform, new[] { Ui.T("自動 (可動範囲の 80 %)", "auto (80 % of limits)"), Ui.T("手で決める", "manual") }, 0, 24f);
        m_RangeL = Ui.Label(m_JointBox.transform, "", 12f, Ui.Text);
        m_Range = Ui.Slider(m_JointBox.transform, 0.05f, 3.0f, 0.5f, v => { m_RangeL.text = Ui.T($"目標は ±{v:F2} rad から抽選", $"goal sampled from ±{v:F2} rad"); Touch(); });
        m_RangeMode.OnChange = i => { m_Range.interactable = i == 1; m_RangeL.text = i == 0 ? Ui.T("目標は可動範囲の 80 % から抽選", "goal sampled from 80 % of the joint limits") : m_RangeL.text; Touch(); };
        // base_in_region
        m_BaseBox = Ui.Column(Root.transform, "Base", 4f, 0, false);
        m_RMinL = Ui.Label(m_BaseBox.transform, "", 12f, Ui.Text);
        m_RMin = Ui.Slider(m_BaseBox.transform, 0.2f, 5f, 1f, v => { if (m_RMax != null && m_RMax.value < v + 0.2f) m_RMax.value = v + 0.2f; m_RMinL.text = Ui.T($"目標地点: 開始位置から {v:F1} m 以上", $"goal: at least {v:F1} m from the start"); Touch(); });
        m_RMaxL = Ui.Label(m_BaseBox.transform, "", 12f, Ui.Text);
        m_RMax = Ui.Slider(m_BaseBox.transform, 0.4f, 6f, 2.5f, v => { if (m_RMin != null && m_RMin.value > v - 0.2f) m_RMin.value = v - 0.2f; m_RMaxL.text = Ui.T($"目標地点: 開始位置から {v:F1} m 以内 (環の中から抽選)", $"goal: within {v:F1} m of the start (sampled in the ring)"); Touch(); });
        // common
        m_TolL = Ui.Label(Root.transform, "", 12f, Ui.Text);
        m_Tol = Ui.Slider(Root.transform, 0.01f, 0.5f, 0.05f, v => { m_TolL.text = m_IsBase ? Ui.T($"成功: 目標から {v:F2} m 以内", $"success: within {v:F2} m") : Ui.T($"成功: 目標角から {v:F3} rad 以内", $"success: within {v:F3} rad"); Touch(); });
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
        m_Kind.text = m_IsBase ? Ui.T("このロボットは移動基体: 「所定の場所へ動く」タスク", "Mobile base: a reach-a-point task")
                               : Ui.T("このロボットは固定基体: 「関節を目標角へ動かす」タスク", "Fixed base: a joint-target task");
        m_JointBox.SetActive(!m_IsBase); m_BaseBox.SetActive(m_IsBase);
        m_Tol.minValue = m_IsBase ? 0.05f : 0.005f; m_Tol.maxValue = m_IsBase ? 1f : 0.5f;
        m_Tol.value = g.tolerance > 0f ? g.tolerance : (m_IsBase ? 0.15f : 0.05f);
        m_Time.value = m_Spec.episode.time_s > 0f ? m_Spec.episode.time_s : (m_IsBase ? 10f : 2f);
        if (m_IsBase) { m_RMin.value = g.region.r_min; m_RMax.value = g.region.r_max; }
        else { bool manual = g.range != null && g.range.Length == 2; m_RangeMode.Set(manual ? 1 : 0); if (manual) m_Range.value = Mathf.Max(Mathf.Abs(g.range[0]), Mathf.Abs(g.range[1])); }
        m_Tol.onValueChanged.Invoke(m_Tol.value); m_Time.onValueChanged.Invoke(m_Time.value);
        RefreshDetails();
        W.Status(Ui.T("成功の条件と制限時間を決めて「次へ」", "Set the success condition and the time limit, then Next"));
    }

    void Apply()
    {
        SpecGoal g = m_Spec.Goal0;
        g.tolerance = m_Tol.value; m_Spec.episode.time_s = m_Time.value;
        if (m_IsBase) { g.type = "base_in_region"; g.region.shape = "ring"; g.region.r_min = m_RMin.value; g.region.r_max = m_RMax.value; }
        else { g.type = "joints_near"; g.range = m_RangeMode.Index == 1 ? new[] { -m_Range.value, m_Range.value } : new float[0]; }
        m_Spec.Save(P.Abs(P.D.task));
    }

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

    /// <summary>3D: 地点到達なら環 (緑) を描く。関節目標は v1 では描かない。</summary>
    void DrawGoal()
    {
        if (!m_IsBase) { if (m_RingIn != null) { m_RingIn.enabled = false; m_RingOut.enabled = false; } return; }
        if (m_RingIn == null) { m_RingIn = MakeRing("GoalRingIn"); m_RingOut = MakeRing("GoalRingOut"); }
        Ring(m_RingIn, m_RMin.value); Ring(m_RingOut, m_RMax.value);
        m_RingIn.enabled = m_RingOut.enabled = true;
        var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        if (cam != null) { cam.target = Vector3.zero; cam.distance = Mathf.Max(3f, m_RMax.value * 2.2f); }
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

    static void Ring(LineRenderer lr, float r)
    {
        const int n = 64; lr.positionCount = n;
        for (int i = 0; i < n; i++) { float a = i * Mathf.PI * 2f / n; lr.SetPosition(i, new Vector3(Mathf.Cos(a) * r, 0.02f, Mathf.Sin(a) * r)); }
    }

    public override void Leave() { if (m_RingIn != null) { m_RingIn.enabled = false; m_RingOut.enabled = false; } }
    public override void Action(string name) { if (name == "next") OnNext(); }
}
