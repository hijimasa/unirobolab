using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>④ 試す: 学習した方策をその場で動かす。3D が主役。地点到達は地面クリック、関節目標はスライダ。</summary>
public class PhaseTry : Phase
{
    public override string Key => "try";
    public override string Title => Ui.T("④ 試す", "4 Try");
    public override LabWizard.PreviewMode Preview => LabWizard.PreviewMode.Large;

    Button m_Run, m_Stop, m_Pick;
    TMP_Text m_Plain, m_Raw, m_GoalL;
    GameObject m_SliderBox; readonly List<Slider> m_Sliders = new List<Slider>();
    ExternalProcess m_Proc;
    bool m_Picking, m_Running; int m_IsBase = -1, m_GoalSize = -1; float m_SendAt = -1f, m_SavedScale = -1f, m_TrailAt;
    GameObject m_Marker; LineRenderer m_Trail; readonly List<Vector3> m_TrailPts = new List<Vector3>();
    string m_Entity; readonly StringBuilder m_RawBuf = new StringBuilder();

    public override void Build(RectTransform main, RectTransform details)
    {
        Root = Ui.Column(main, "Try", 6f);
        Ui.Label(Root.transform, Ui.T("学習した方策をその場で動かします。", "Run the trained policy in place."), 12f, Ui.Muted, true, 30f);
        var r = Ui.Row(Root.transform, 32f);
        m_Run = Ui.Btn(r.transform, Ui.T("▶ 動かす", "▶ Run"), Run, 0f, 30f); Ui.SetBtn(m_Run, null, Ui.BtnActive);
        m_Stop = Ui.Btn(r.transform, Ui.T("■ 止める", "■ Stop"), Stop, 90f, 30f);
        m_Pick = Ui.Btn(Root.transform, Ui.T("地面をクリックして目標を置く", "Click the ground to place the goal"), () => { m_Picking = !m_Picking; Ui.SetBtn(m_Pick, null, m_Picking ? Ui.BtnActive : Ui.BtnNormal); }, 0f, 26f);
        m_GoalL = Ui.Label(Root.transform, Ui.T("目標 (関節ごと)", "Goal (per joint)"), 12f, Ui.Muted);
        m_SliderBox = Ui.Column(Root.transform, "Sliders", 3f, 0, false);
        m_Plain = Ui.Label(Root.transform, "", 13f, Ui.Text, true, 44f);
        Ui.Spacer(Root.transform);
        DetailsRoot = Ui.Column(details, "TryDetails", 4f);
        Ui.Label(DetailsRoot.transform, Ui.T("実行器の出力 (JSON)", "Runner output (JSON)"), 12f, Ui.Muted);
        m_Raw = Ui.Label(DetailsRoot.transform, "", 10f, Ui.Text, true, 0f);
        m_Raw.GetComponent<LayoutElement>().flexibleHeight = 1f; m_Raw.alignment = TextAlignmentOptions.BottomLeft;
    }

    public override bool CanEnter(out string reason) { reason = Ui.T("先に ③ で学習してください", "train in step 3 first"); return P.Exists("run"); }

    public override void Enter()
    {
        TaskSpec spec = TaskSpec.Load(P.Abs(P.D.task));
        m_IsBase = spec.Goal0.type == "base_in_region" ? 1 : 0;
        m_Pick.gameObject.SetActive(m_IsBase == 1); m_GoalL.gameObject.SetActive(m_IsBase == 0); m_SliderBox.SetActive(m_IsBase == 0);
        EnsureEntity();
        W.Status(Ui.T("「動かす」を押してください", "Press Run"));
    }

    /// <summary>試す用のロボットが無ければスポーンする (学習中の 8 体があればその 1 体目を使う)。</summary>
    void EnsureEntity()
    {
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        foreach (GameObject e in buf) if (e != null) { m_Entity = e.name; break; }
        if (string.IsNullOrEmpty(m_Entity) && W.Sim.CanSpawnFromGui)
        {
            if (W.Sim.TrySpawnRobotFromUrdf(P.Abs(P.D.urdf), out string name, out _)) m_Entity = name;
        }
        var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        Transform root = EntityRoot();
        if (cam != null && root != null) { cam.target = root.position; cam.distance = m_IsBase == 1 ? 4f : 1.6f; }
    }

    void Run()
    {
        if (m_Proc != null && !m_Proc.HasExited) return;
        if (Env.LearningPort() <= 0) { W.Status(Ui.T("学習サーバが無効です", "learning server is off"), Ui.Warn); return; }
        EnsureEntity();
        if (string.IsNullOrEmpty(m_Entity)) { W.Status(Ui.T("ロボットがスポーンできません", "could not spawn the robot"), Ui.Bad); return; }
        string runner = Environment.GetEnvironmentVariable("SIM_POLICY_RUNNER");
        if (string.IsNullOrEmpty(runner)) runner = UniRoboLabConfig.Data.policy_runner_command;
        if (string.IsNullOrEmpty(runner)) runner = W.Py + " -m unirobolab live";
        var cmd = new StringBuilder(runner).Append(' ').Append(ExternalProcess.Quote(P.Abs(P.D.contract)))
            .Append(" --onnx ").Append(ExternalProcess.Quote(P.OnnxPath)).Append(" --entity ").Append(ExternalProcess.Quote(m_Entity))
            .Append(" --port ").Append(Env.LearningPort()).Append(" 2>&1");
        m_Proc = W.Launch(cmd.ToString());
        m_Running = true; m_GoalSize = -1; ClearTrail(); if (m_Marker != null) m_Marker.SetActive(false);
        if (m_SavedScale < 0f) { m_SavedScale = SimulationControl.ConfiguredTimeScale; SimulationControl.ConfiguredTimeScale = 1f; if (Time.timeScale > 1f) Time.timeScale = 1f; }
        m_Plain.text = Ui.T("動かしています...", "running...");
        W.Status(Ui.T("実行中", "running"));
    }

    void Stop()
    {
        if (m_Proc != null) { m_Proc.WriteLine("stop"); m_Proc.Stop(null, 3000); m_Proc = null; }
        m_Running = false; RestoreScale(); W.Status(Ui.T("止めました", "stopped"));
    }

    void RestoreScale() { if (m_SavedScale >= 0f) { SimulationControl.ConfiguredTimeScale = m_SavedScale; m_SavedScale = -1f; } }

    public override void Tick()
    {
        if (m_Proc != null)
        {
            string last = null;
            while (m_Proc.TryDequeue(out string l)) { last = l; m_RawBuf.AppendLine(l); if (m_RawBuf.Length > 4000) m_RawBuf.Remove(0, 1500); }
            if (last != null)
            {
                if (m_Raw != null && m_Raw.gameObject.activeInHierarchy) m_Raw.text = m_RawBuf.ToString();
                if (last.StartsWith("{")) ApplyStatus(last);
                else if (last.StartsWith("!")) W.Status(last, Ui.Bad);
                if (Application.isBatchMode) Debug.Log("[Wizard/try] " + last);
            }
            if (m_Proc.HasExited) { int code = m_Proc.ExitCode; m_Proc = null; m_Running = false; RestoreScale(); if (code != 0) W.Status(Ui.T($"実行器が終了 (exit {code})。「詳細」を見てください", $"runner exited ({code}); see Details"), Ui.Bad); }
        }
        UpdateTrail(); UpdatePicking();
        if (m_SendAt >= 0f && Time.unscaledTime >= m_SendAt) { m_SendAt = -1f; SendGoal(SliderGoal()); }
    }

    void ApplyStatus(string json)
    {
        if (json.Contains("\"done\""))
        {
            m_Plain.text = Ui.T($"終了: 最終誤差 {Ui.Num(json, "final_err", -1f):F3}", $"finished: final error {Ui.Num(json, "final_err", -1f):F3}");
            return;
        }
        float t = Ui.Num(json, "t", 0f), err = Ui.Num(json, "err", -1f);
        int gi = json.IndexOf("\"goal\"", StringComparison.Ordinal); string goalText = null; int n = -1;
        if (gi >= 0) { int a = json.IndexOf('[', gi), b = json.IndexOf(']', a); if (a > 0 && b > a) { goalText = json.Substring(a + 1, b - a - 1).Replace(',', ' '); n = goalText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length; } }
        if (n > 0 && n != m_GoalSize) { m_GoalSize = n; BuildSliders(n, goalText); if (m_IsBase == 1 && goalText != null) PlaceGoalFromText(goalText); }
        m_Plain.text = m_IsBase == 1 ? Ui.T($"誤差 {err:F3} m / 経過 {t:F1} s", $"error {err:F3} m / {t:F1} s") : Ui.T($"誤差 {err:F3} rad / 経過 {t:F1} s", $"error {err:F3} rad / {t:F1} s");
    }

    void SendGoal(string goal)
    {
        if (m_Proc == null || m_Proc.HasExited) return;
        m_Proc.WriteLine("goal " + goal.Trim().Replace(',', ' '));
        if (m_IsBase == 1) PlaceGoalFromText(goal);
    }

    // ---- sliders (joint goals)
    void BuildSliders(int n, string initial)
    {
        foreach (Transform ch in m_SliderBox.transform) UnityEngine.Object.Destroy(ch.gameObject);
        m_Sliders.Clear();
        if (m_IsBase == 1 || n <= 0 || n > 12) return;
        TaskSpec spec = TaskSpec.Load(P.Abs(P.D.task));
        float range = spec.Goal0.range != null && spec.Goal0.range.Length == 2 ? Mathf.Max(Mathf.Abs(spec.Goal0.range[0]), Mathf.Abs(spec.Goal0.range[1])) : 1.2f;
        string[] cur = (initial ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < n; i++)
        {
            int k = i;
            var row = Ui.Row(m_SliderBox.transform, 22f);
            var lab = Ui.Label(row.transform, $"j{i + 1}: 0.00", 11f, Ui.Text, false, 0f, 90f);
            var sl = Ui.Slider(row.transform, -range, range, i < cur.Length && float.TryParse(cur[i], out float v) ? Mathf.Clamp(v, -range, range) : 0f,
                                val => { lab.text = $"j{k + 1}: {val:F2}"; m_SendAt = Time.unscaledTime + 0.15f; });
            lab.text = $"j{i + 1}: {sl.value:F2}";
            m_Sliders.Add(sl);
        }
    }

    string SliderGoal() { var sb = new StringBuilder(); foreach (Slider s in m_Sliders) { if (sb.Length > 0) sb.Append(' '); sb.Append(s.value.ToString("F3")); } return sb.ToString(); }

    // ---- 3D goal / trail
    Transform EntityRoot()
    {
        if (string.IsNullOrEmpty(m_Entity)) return null;
        var buf = new List<GameObject>(); W.Sim.GetEntitiesSnapshot(buf);
        foreach (GameObject e in buf)
        {
            if (e == null || e.name != m_Entity) continue;
            foreach (ArticulationBody ab in e.GetComponentsInChildren<ArticulationBody>()) if (ab.isRoot) return ab.transform;
            return e.transform;
        }
        return null;
    }

    void UpdateTrail()
    {
        if (!m_Running || m_IsBase != 1 || Time.unscaledTime < m_TrailAt) return;
        m_TrailAt = Time.unscaledTime + 0.1f;
        Transform root = EntityRoot(); if (root == null) return;
        Vector3 p = root.position + Vector3.up * 0.02f;
        if (m_TrailPts.Count > 0 && (m_TrailPts[m_TrailPts.Count - 1] - p).sqrMagnitude < 1e-4f) return;
        if (m_TrailPts.Count >= 2000) m_TrailPts.RemoveAt(0);
        m_TrailPts.Add(p);
        if (m_Trail == null)
        {
            m_Trail = new GameObject("PolicyTrail", typeof(LineRenderer)).GetComponent<LineRenderer>();
            m_Trail.material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default"));
            var c = new Color(0.35f, 0.6f, 1f, 1f); m_Trail.material.color = c; if (m_Trail.material.HasProperty("_BaseColor")) m_Trail.material.SetColor("_BaseColor", c);
            m_Trail.startWidth = m_Trail.endWidth = 0.02f; m_Trail.useWorldSpace = true;
        }
        m_Trail.positionCount = m_TrailPts.Count; m_Trail.SetPositions(m_TrailPts.ToArray());
    }

    void ClearTrail() { m_TrailPts.Clear(); if (m_Trail != null) m_Trail.positionCount = 0; }

    void UpdatePicking()
    {
        if (!m_Picking || !UnityEngine.Input.GetMouseButtonDown(0) || Camera.main == null || !Camera.main.enabled) return;
        if (UnityEngine.EventSystems.EventSystem.current != null && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject()) return;
        Ray ray = Camera.main.ScreenPointToRay(UnityEngine.Input.mousePosition);
        Vector3 hit;
        if (Physics.Raycast(ray, out RaycastHit h, 500f)) hit = h.point; else { var plane = new Plane(Vector3.up, Vector3.zero); if (!plane.Raycast(ray, out float d)) return; hit = ray.GetPoint(d); }
        PlaceGoal(hit);
        SendGoal($"{hit.z:F3} {-hit.x:F3}");
    }

    void PlaceGoalFromText(string goal)
    {
        string[] p = goal.Trim().Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 2 && float.TryParse(p[0], out float rx) && float.TryParse(p[1], out float ry)) PlaceGoal(new Vector3(-ry, 0f, rx));
    }

    void PlaceGoal(Vector3 world)
    {
        if (m_Marker == null)
        {
            m_Marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder); m_Marker.name = "PolicyGoal";
            UnityEngine.Object.Destroy(m_Marker.GetComponent<Collider>());
            m_Marker.transform.localScale = new Vector3(0.3f, 0.01f, 0.3f);
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default")); var c = new Color(0.3f, 0.9f, 0.4f, 0.9f);
            mat.color = c; if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c); m_Marker.GetComponent<Renderer>().material = mat;
        }
        m_Marker.transform.position = new Vector3(world.x, 0.011f, world.z); m_Marker.SetActive(true);
    }

    public override bool Done() => false;   // 任意のフェーズ
    public override void Leave() { Stop(); if (m_Marker != null) m_Marker.SetActive(false); ClearTrail(); }
    public override void Action(string name) { if (name == "run") Run(); }
}
