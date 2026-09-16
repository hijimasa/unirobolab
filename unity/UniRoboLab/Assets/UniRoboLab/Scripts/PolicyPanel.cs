using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

/// <summary>
/// Policy パネル: 契約 (contract JSON) と ONNX を指定して、スポーン済みのエンティティで
/// 方策をその場で動かす。ROS 2 は使わない。
/// </summary>
/// <remarks>
/// 推論は Unity の中では行わない。Inference Engine は ONNX をエディタでしか変換できず、
/// 配布バイナリでは変換済みファイルしか読めないためで、任意の ONNX を動かすには
/// unirobolab のライブ実行器 (`unirobolab live`: onnxruntime + 学習サーバ) を子プロセスで
/// 起動するのが、配線の実装を増やさない最短経路になる。パネルはそのプロセスを起動し、
/// 標準出力の状態行 (JSON) を表示し、標準入力で目標を送る。
///
/// 有効化: settings.learning_port (学習サーバ) が必要。コマンドは settings.policy_runner_command
/// か環境変数 SIM_POLICY_RUNNER。ヘッドレス検証用に SIM_POLICY_AUTORUN
/// ("契約|ONNX|エンティティ|目標|秒数") で起動時に自動実行できる。
/// </remarks>
public class PolicyPanel : MonoBehaviour
{
    public const string RunnerEnvVar = "SIM_POLICY_RUNNER";
    public const string AutorunEnvVar = "SIM_POLICY_AUTORUN";
    const string k_DefaultRunner = "python3 -m unirobolab live";

    static readonly Color HeaderColor = new Color(0.95f, 0.95f, 0.95f, 1f);
    static readonly Color TextColor = new Color(0.85f, 0.85f, 0.85f, 1f);
    static readonly Color PanelColor = new Color(0.12f, 0.12f, 0.14f, 0.92f);

    SimulationControl m_Control;
    TMP_InputField m_Contract, m_Onnx, m_Entity, m_Goal;
    TMP_Text m_Status;
    Button m_Run, m_Stop, m_SetGoal;
    GameObject m_ExpertBox;
    bool m_ExpertWas;
    Process m_Process;
    readonly ConcurrentQueue<string> m_Lines = new ConcurrentQueue<string>();
    string m_LastStatus = "";
    float m_NextEntityRefresh;

    // ---- ライトユーザー向け「試す」: 3D で目標を置く、軌跡、誤差と経過時間、関節スライダ
    TMP_Text m_Plain;                 // 誤差 / 経過 / 到達 (生の JSON は「詳細」のときだけ m_Status に)
    Button m_Pick;
    bool m_Picking;                   // 地面をクリックすると目標を置く (地点到達の契約のとき)
    GameObject m_GoalMarker;
    LineRenderer m_Trail;
    readonly List<Vector3> m_TrailPts = new List<Vector3>();
    float m_TrailNextAt;
    GameObject m_SliderBox;
    readonly List<Slider> m_JointSliders = new List<Slider>();
    float m_SliderSendAt = -1f;
    bool m_Running;
    int m_GoalSize = -1;
    int m_IsBase = -1;                // -1 不明, 0 関節, 1 地点 (ライブ実行器の状態行 "base" から)
    bool IsBase => m_IsBase >= 0 ? m_IsBase == 1 : LabPanel.TaskIsBase;
    static readonly Color GoalColor = new Color(0.3f, 0.9f, 0.4f, 0.9f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        SimulationControl control = FindFirstObjectByType<SimulationControl>(FindObjectsInactive.Include);
        if (control == null || control.GetComponent<PolicyPanel>() != null)
        {
            return;
        }
        control.gameObject.AddComponent<PolicyPanel>();
    }

    void Start()
    {
        m_Control = GetComponent<SimulationControl>();
        if (!Application.isBatchMode)
        {
            BuildUi();
        }
        string autorun = Environment.GetEnvironmentVariable(AutorunEnvVar);
        if (!string.IsNullOrEmpty(autorun))
        {
            StartCoroutine(AutorunAfterSettle(autorun));
        }
    }

    System.Collections.IEnumerator AutorunAfterSettle(string spec)
    {
        string[] parts = spec.Split('|');
        if (parts.Length < 3)
        {
            Debug.LogError($"[PolicyPanel] {AutorunEnvVar} は '契約|ONNX|エンティティ|目標|秒数' の形");
            yield break;
        }
        // エンティティが (サービスや学習サーバ経由で) 現れるまで待つ。最大 120 s。
        var buf = new List<GameObject>();
        float deadline = Time.realtimeSinceStartup + 120f;
        while (Time.realtimeSinceStartup < deadline)
        {
            m_Control.GetEntitiesSnapshot(buf);
            bool found = false;
            foreach (GameObject e in buf) if (e != null && e.name == parts[2]) found = true;
            if (found) break;
            yield return new WaitForSecondsRealtime(1f);
        }
        yield return new WaitForSecondsRealtime(1f);
        string goal = parts.Length > 3 ? parts[3] : "";
        string duration = parts.Length > 4 ? parts[4] : "";
        StartRunner(parts[0], parts[1], parts[2], goal, duration);
    }

    // ------------------------------------------------------------------ process
    static int LearningPort()
    {
        string fromEnv = Environment.GetEnvironmentVariable(SimulationControl.LearningPortEnvVar);
        int port = 0;
        if (!string.IsNullOrEmpty(fromEnv))
        {
            int.TryParse(fromEnv, out port);
        }
        else if (SimulationResources.Settings != null)
        {
            port = SimulationResources.Settings.learning_port;
        }
        return port;
    }

    public void StartRunner(string contract, string onnx, string entity, string goal, string duration)
    {
        if (m_Process != null && !m_Process.HasExited)
        {
            SetStatus("already running");
            return;
        }
        // ライトユーザーは Task タブの契約と直近の学習出力をそのまま使う
        if (string.IsNullOrEmpty(contract)) { contract = LabPanel.TaskContract; if (m_Contract != null) m_Contract.text = contract; }
        if (string.IsNullOrEmpty(onnx) && !string.IsNullOrEmpty(LabPanel.LastRunDir))
        {
            string cand = System.IO.Path.Combine(LabPanel.LastRunDir, "policy.onnx");
            if (System.IO.File.Exists(cand)) { onnx = cand; if (m_Onnx != null) m_Onnx.text = onnx; }
        }
        if (string.IsNullOrEmpty(contract)) { SetStatus("no contract: pick one in the Task tab"); return; }
        ClearTrail();
        m_GoalSize = -1; m_IsBase = -1;
        if (m_GoalMarker != null) m_GoalMarker.SetActive(false);
        if (!string.IsNullOrEmpty(goal)) PlaceGoalFromText(goal);
        int port = LearningPort();
        if (port <= 0)
        {
            SetStatus("learning server disabled: set settings.learning_port or SIM_LEARNING_PORT");
            return;
        }
        string runner = Environment.GetEnvironmentVariable(RunnerEnvVar);
        if (string.IsNullOrEmpty(runner))
        {
            runner = UniRoboLabConfig.Data.policy_runner_command;
        }
        if (string.IsNullOrEmpty(runner))
        {
            // 既定は他のパネルと同じインタプリタ (unirobolab.python) で "-m unirobolab live"
            runner = ExternalProcess.UnirobolabPython() + " -m unirobolab live";
        }
        var args = new StringBuilder();
        args.Append(runner).Append(' ').Append(Quote(contract));
        if (!string.IsNullOrEmpty(onnx)) args.Append(" --onnx ").Append(Quote(onnx));
        if (!string.IsNullOrEmpty(entity)) args.Append(" --entity ").Append(Quote(entity));
        if (!string.IsNullOrEmpty(goal)) args.Append(" --goal ").Append(Quote(goal.Trim().Replace(' ', ',')));
        if (!string.IsNullOrEmpty(duration)) args.Append(" --duration ").Append(duration);
        args.Append(" --port ").Append(port);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c " + Quote(args.ToString()),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string envSpec = UniRoboLabConfig.Data.policy_runner_env;
        if (!string.IsNullOrEmpty(envSpec))
        {
            foreach (string kv in envSpec.Split(';'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0) psi.Environment[kv.Substring(0, eq).Trim()] = kv.Substring(eq + 1).Trim();
            }
        }
        try
        {
            m_Process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            m_Process.OutputDataReceived += (_, e) => { if (e.Data != null) m_Lines.Enqueue(e.Data); };
            m_Process.ErrorDataReceived += (_, e) => { if (e.Data != null) m_Lines.Enqueue("! " + e.Data); };
            m_Process.Start();
            m_Process.BeginOutputReadLine();
            m_Process.BeginErrorReadLine();
            SetStatus("started: " + args);
            Debug.Log("[PolicyPanel] started: " + args);
            m_Running = true;
            if (m_Plain != null) m_Plain.text = LabPanel.JapaneseFont ? "動かしています..." : "running...";
        }
        catch (Exception e)
        {
            m_Process = null;
            SetStatus("failed to start: " + e.Message);
            Debug.LogError("[PolicyPanel] " + e);
        }
    }

    public void SendGoal(string goal)
    {
        if (m_Process == null || m_Process.HasExited) { SetStatus("not running"); return; }
        try
        {
            m_Process.StandardInput.WriteLine("goal " + goal.Trim().Replace(',', ' '));
            m_Process.StandardInput.Flush();
            PlaceGoalFromText(goal);
        }
        catch (Exception e) { SetStatus("goal failed: " + e.Message); }
    }

    public void StopRunner()
    {
        if (m_Process == null) return;
        try
        {
            if (!m_Process.HasExited)
            {
                m_Process.StandardInput.WriteLine("stop");
                m_Process.StandardInput.Flush();
                if (!m_Process.WaitForExit(3000)) m_Process.Kill();
            }
        }
        catch (Exception) { }
        m_Process = null;
        m_Running = false;
        SetStatus("stopped");
    }

    void OnApplicationQuit() { StopRunner(); }

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    void SetStatus(string text)
    {
        m_LastStatus = text;
        if (m_Status == null) return;
        // ライト層には言い換え (m_Plain) と失敗だけを見せ、状態行の JSON や起動コマンドは専門家層だけ
        if (!LabPanel.ExpertShown && (text.StartsWith("{") || text.StartsWith("started:"))) { m_Status.text = ""; return; }
        m_Status.text = text;
    }

    static float Num(string json, string key, float fallback)
    {
        int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return fallback;
        int c = json.IndexOf(':', i); int e = c + 1;
        while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '.' || json[e] == '-' || json[e] == 'e' || json[e] == ' ')) e++;
        return float.TryParse(json.Substring(c + 1, e - c - 1).Trim(), out float v) ? v : fallback;
    }

    /// <summary>ライブ実行器の状態行 {"t","rate_hz","err","goal":[..]} / {"done","steps","final_err"} を平易に。</summary>
    void ApplyStatusJson(string json)
    {
        bool ja = LabPanel.JapaneseFont;
        if (json.Contains("\"done\""))
        {
            float fe = Num(json, "final_err", -1f); float steps = Num(json, "steps", 0f);
            if (m_Plain != null) m_Plain.text = ja ? $"終了: 最終誤差 {fe:F3} ({steps:F0} ステップ)" : $"finished: final error {fe:F3} ({steps:F0} steps)";
            m_Running = false;
            return;
        }
        float t = Num(json, "t", 0f), err = Num(json, "err", -1f);
        if (json.Contains("\"base\": true")) m_IsBase = 1; else if (json.Contains("\"base\": false")) m_IsBase = 0;
        int gi = json.IndexOf("\"goal\"", StringComparison.Ordinal);
        int n = -1; string goalText = null;
        if (gi >= 0)
        {
            int a = json.IndexOf('[', gi), b = json.IndexOf(']', a);
            if (a > 0 && b > a) { goalText = json.Substring(a + 1, b - a - 1).Replace(',', ' '); n = goalText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length; }
        }
        if (n > 0 && n != m_GoalSize) { m_GoalSize = n; BuildJointSliders(n, goalText); if (goalText != null) PlaceGoalFromText(goalText); }
        string unit = IsBase ? "m" : "rad";
        if (m_Plain != null) m_Plain.text = ja ? $"誤差 {err:F3} {unit} / 経過 {t:F1} s" : $"error {err:F3} {unit} / {t:F1} s elapsed";
    }

    // ------------------------------------------------------------ 3D goal / trail
    Transform EntityRoot()
    {
        if (m_Control == null || m_Entity == null || string.IsNullOrEmpty(m_Entity.text)) return null;
        var buf = new List<GameObject>();
        m_Control.GetEntitiesSnapshot(buf);
        foreach (GameObject e in buf)
        {
            if (e == null || e.name != m_Entity.text) continue;
            // 動くのはルートの ArticulationBody (エンティティの GameObject 自体はスポーン位置に留まる)
            foreach (ArticulationBody ab in e.GetComponentsInChildren<ArticulationBody>()) if (ab.isRoot) return ab.transform;
            return e.transform;
        }
        return null;
    }

    void UpdateTrail()
    {
        if (!m_Running || Time.unscaledTime < m_TrailNextAt) return;
        m_TrailNextAt = Time.unscaledTime + 0.1f;
        Transform root = EntityRoot();
        if (root == null) return;
        Vector3 p = root.position + Vector3.up * 0.02f;
        if (m_TrailPts.Count > 0 && (m_TrailPts[m_TrailPts.Count - 1] - p).sqrMagnitude < 1e-4f) return;
        if (m_TrailPts.Count >= 2000) m_TrailPts.RemoveAt(0);
        m_TrailPts.Add(p);
        if (m_Trail == null)
        {
            var go = new GameObject("PolicyTrail", typeof(LineRenderer));
            m_Trail = go.GetComponent<LineRenderer>();
            m_Trail.material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default"));
            m_Trail.material.color = new Color(0.35f, 0.6f, 1f, 1f);
            if (m_Trail.material.HasProperty("_BaseColor")) m_Trail.material.SetColor("_BaseColor", new Color(0.35f, 0.6f, 1f, 1f));
            m_Trail.startColor = m_Trail.endColor = new Color(0.35f, 0.6f, 1f, 1f);
            m_Trail.startWidth = m_Trail.endWidth = 0.02f;
            m_Trail.useWorldSpace = true;
        }
        m_Trail.positionCount = m_TrailPts.Count;
        m_Trail.SetPositions(m_TrailPts.ToArray());
    }

    void ClearTrail()
    {
        m_TrailPts.Clear();
        if (m_Trail != null) m_Trail.positionCount = 0;
    }

    /// <summary>地面のクリックで目標を置く。ROS 座標 (x 前, y 左) = Unity (z, -x)。</summary>
    void UpdatePicking()
    {
        if (!m_Picking || !UnityEngine.Input.GetMouseButtonDown(0) || Camera.main == null) return;
        if (UnityEngine.EventSystems.EventSystem.current != null && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject()) return;
        Ray ray = Camera.main.ScreenPointToRay(UnityEngine.Input.mousePosition);
        Vector3 hit;
        if (Physics.Raycast(ray, out RaycastHit h, 500f)) hit = h.point;
        else { var plane = new Plane(Vector3.up, Vector3.zero); if (!plane.Raycast(ray, out float d)) return; hit = ray.GetPoint(d); }
        PlaceGoal(hit);
        float rx = hit.z, ry = -hit.x;
        if (m_Goal != null) m_Goal.text = $"{rx:F2} {ry:F2}";
        if (m_Process != null && !m_Process.HasExited) SendGoal($"{rx:F3} {ry:F3}");
    }

    void PlaceGoal(Vector3 world)
    {
        if (m_GoalMarker == null)
        {
            m_GoalMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            m_GoalMarker.name = "PolicyGoal";
            UnityEngine.Object.Destroy(m_GoalMarker.GetComponent<Collider>());
            m_GoalMarker.transform.localScale = new Vector3(0.3f, 0.01f, 0.3f);
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default")) { color = GoalColor };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", GoalColor);
            m_GoalMarker.GetComponent<Renderer>().material = mat;
        }
        m_GoalMarker.transform.position = new Vector3(world.x, 0.011f, world.z);
        m_GoalMarker.SetActive(true);
    }

    /// <summary>目標文字列 (ROS x y) からマーカを置く。地点到達の契約のときだけ意味を持つ。</summary>
    void PlaceGoalFromText(string goal)
    {
        if (!IsBase) return;
        string[] p = goal.Trim().Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 2 && float.TryParse(p[0], out float rx) && float.TryParse(p[1], out float ry)) PlaceGoal(new Vector3(-ry, 0f, rx));
    }

    // ------------------------------------------------------------ joint sliders
    void BuildJointSliders(int n, string initial = null)
    {
        if (m_SliderBox == null) return;
        foreach (Transform ch in m_SliderBox.transform) UnityEngine.Object.Destroy(ch.gameObject);
        m_JointSliders.Clear();
        bool show = !IsBase && n > 0 && n <= 8;
        m_SliderBox.SetActive(show);
        if (!show) return;
        float range = Mathf.Max(0.1f, LabPanel.TaskGoalRange);
        string[] cur = (initial ?? (m_Goal != null ? m_Goal.text : "")).Trim().Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (initial != null && m_Goal != null)
        {
            var parts = new List<string>();
            foreach (string v in cur) parts.Add(float.TryParse(v, out float f) ? f.ToString("F3") : v);
            m_Goal.text = string.Join(" ", parts);
        }
        for (int i = 0; i < n; i++)
        {
            GameObject go = DefaultControls.CreateSlider(new DefaultControls.Resources());
            go.transform.SetParent(m_SliderBox.transform, false);
            go.AddComponent<LayoutElement>().preferredHeight = 16f;
            var sl = go.GetComponent<Slider>();
            sl.minValue = -range; sl.maxValue = range;
            sl.value = i < cur.Length && float.TryParse(cur[i], out float v) ? Mathf.Clamp(v, -range, range) : 0f;
            sl.onValueChanged.AddListener(_ => { if (m_Goal != null) m_Goal.text = SliderGoal(); m_SliderSendAt = Time.unscaledTime + 0.15f; });
            m_JointSliders.Add(sl);
        }
        m_SliderBox.GetComponent<LayoutElement>().preferredHeight = 16f * n + 4f;
    }

    string SliderGoal()
    {
        var sb = new StringBuilder();
        foreach (Slider sl in m_JointSliders) { if (sb.Length > 0) sb.Append(' '); sb.Append(sl.value.ToString("F3")); }
        return sb.ToString();
    }

    void Update()
    {
        string line = null;
        while (m_Lines.TryDequeue(out string l)) line = l;   // 最新行だけ表示
        if (line != null)
        {
            SetStatus(line);
            if (Application.isBatchMode) Debug.Log("[PolicyPanel] " + line);
            if (line.StartsWith("{")) ApplyStatusJson(line);
        }
        UpdateTrail();
        UpdatePicking();
        if (m_ExpertBox != null && LabPanel.ExpertShown != m_ExpertWas) { m_ExpertWas = LabPanel.ExpertShown; m_ExpertBox.SetActive(m_ExpertWas); }
        if (m_SliderSendAt >= 0f && Time.unscaledTime >= m_SliderSendAt) { m_SliderSendAt = -1f; SendGoal(SliderGoal()); }
        if (m_Process != null && m_Process.HasExited && !m_LastStatus.StartsWith("exited"))
        {
            SetStatus("exited (" + m_Process.ExitCode + "): " + m_LastStatus);
            m_Process = null;
        }
        if (m_Entity != null && Time.unscaledTime > m_NextEntityRefresh && string.IsNullOrEmpty(m_Entity.text))
        {
            m_NextEntityRefresh = Time.unscaledTime + 1f;
            var buf = new List<GameObject>();
            m_Control.GetEntitiesSnapshot(buf);
            if (buf.Count > 0 && buf[0] != null) m_Entity.text = buf[0].name;
        }
    }

    // ---------------------------------------------------------------------- ui
    void BuildUi()
    {
        var canvasGo = new GameObject("PolicyPanelCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5;
        RectTransform canvasRt = canvasGo.GetComponent<RectTransform>();

        var panel = new GameObject("PolicyPanel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        var rt = panel.GetComponent<RectTransform>();
        rt.SetParent(canvasRt, false);
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(0f, 0f); rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(12f, 12f);
        rt.sizeDelta = new Vector2(380f, 285f);
        panel.GetComponent<Image>().color = PanelColor;
        var layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 8, 8); layout.spacing = 4f;
        layout.childForceExpandHeight = false; layout.childControlHeight = true; layout.childControlWidth = true;

        Label(rt, "Try the policy (unirobolab live)", 18f, HeaderColor);
        m_ExpertBox = new GameObject("Expert", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        m_ExpertBox.transform.SetParent(rt, false);
        var el = m_ExpertBox.GetComponent<VerticalLayoutGroup>(); el.spacing = 4f; el.childForceExpandHeight = false; el.childControlHeight = true; el.childControlWidth = true;
        m_ExpertBox.GetComponent<LayoutElement>().preferredHeight = 92f;
        Label(m_ExpertBox.transform, "Contract JSON (blank = Task tab)", 12f, TextColor); m_Contract = Input(m_ExpertBox.transform, "/path/to/contract.json");
        Label(m_ExpertBox.transform, "ONNX (blank = last training run)", 12f, TextColor); m_Onnx = Input(m_ExpertBox.transform, "");
        m_ExpertBox.SetActive(false);
        Label(rt, "Entity", 12f, TextColor); m_Entity = Input(rt, "");
        Label(rt, "Goal (rad per joint, or x y in metres)", 12f, TextColor); m_Goal = Input(rt, "0.5 0.5");
        m_SliderBox = new GameObject("JointSliders", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        m_SliderBox.transform.SetParent(rt, false);
        var sl = m_SliderBox.GetComponent<VerticalLayoutGroup>(); sl.spacing = 2f; sl.childForceExpandHeight = false; sl.childControlHeight = true; sl.childControlWidth = true;
        m_SliderBox.SetActive(false);

        var row = new GameObject("Buttons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(rt, false);
        row.GetComponent<LayoutElement>().preferredHeight = 30f; row.GetComponent<LayoutElement>().flexibleHeight = 0f;
        var hl = row.GetComponent<HorizontalLayoutGroup>(); hl.spacing = 6f; hl.childForceExpandWidth = true;
        m_Run = Btn(row.transform, "Run", () => StartRunner(m_Contract.text, m_Onnx.text, m_Entity.text, m_Goal.text, ""));
        m_SetGoal = Btn(row.transform, "Set goal", () => SendGoal(m_Goal.text));
        m_Pick = Btn(row.transform, "Pick in 3D", () => { m_Picking = !m_Picking; m_Pick.GetComponentInChildren<Text>().text = m_Picking ? "Picking..." : "Pick in 3D"; });
        m_Stop = Btn(row.transform, "Stop", StopRunner);

        m_Plain = Label(rt, LabPanel.JapaneseFont ? "Run で直近の学習結果を動かす。地点到達なら Pick in 3D で地面をクリック" : "Run uses the last training run; for base tasks, Pick in 3D and click the ground", 12f, TextColor);
        m_Plain.enableWordWrapping = true; m_Plain.GetComponent<LayoutElement>().preferredHeight = 34f;
        m_Status = Label(rt, LearningPort() > 0 ? "idle" : "learning server disabled (settings.learning_port)", 11f, TextColor);
        m_Status.enableWordWrapping = true;
        m_Status.GetComponent<LayoutElement>().preferredHeight = 30f;
        var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));   // 余った高さはここへ
        spacer.transform.SetParent(rt, false); spacer.GetComponent<LayoutElement>().flexibleHeight = 1f;
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

    static TMP_InputField Input(Transform parent, string placeholder)
    {
        GameObject go = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 26f;
        var field = go.GetComponent<TMP_InputField>();
        field.pointSize = 12f;
        if (field.placeholder is TMP_Text ph) { ph.text = placeholder; ph.fontSize = 12f; }
        return field;
    }

    static Button Btn(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
    {
        GameObject go = DefaultControls.CreateButton(new DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        var text = go.GetComponentInChildren<Text>();
        if (text != null) { text.text = label; text.fontSize = 13; }
        var button = go.GetComponent<Button>();
        button.onClick.AddListener(onClick);
        return button;
    }
}
