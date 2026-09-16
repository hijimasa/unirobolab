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
    Process m_Process;
    readonly ConcurrentQueue<string> m_Lines = new ConcurrentQueue<string>();
    string m_LastStatus = "";
    float m_NextEntityRefresh;

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
            runner = k_DefaultRunner;
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
        SetStatus("stopped");
    }

    void OnApplicationQuit() { StopRunner(); }

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    void SetStatus(string text)
    {
        m_LastStatus = text;
        if (m_Status != null) m_Status.text = text;
    }

    void Update()
    {
        string line = null;
        while (m_Lines.TryDequeue(out string l)) line = l;   // 最新行だけ表示
        if (line != null)
        {
            SetStatus(line);
            if (Application.isBatchMode) Debug.Log("[PolicyPanel] " + line);
        }
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

        Label(rt, "Policy (unirobolab live)", 18f, HeaderColor);
        Label(rt, "Contract JSON", 12f, TextColor); m_Contract = Input(rt, "/path/to/contract.json");
        Label(rt, "ONNX (blank = contract's)", 12f, TextColor); m_Onnx = Input(rt, "");
        Label(rt, "Entity", 12f, TextColor); m_Entity = Input(rt, "");
        Label(rt, "Goal (space separated)", 12f, TextColor); m_Goal = Input(rt, "0.5 0.5");

        var row = new GameObject("Buttons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(rt, false);
        row.GetComponent<LayoutElement>().preferredHeight = 30f;
        var hl = row.GetComponent<HorizontalLayoutGroup>(); hl.spacing = 6f; hl.childForceExpandWidth = true;
        m_Run = Btn(row.transform, "Run", () => StartRunner(m_Contract.text, m_Onnx.text, m_Entity.text, m_Goal.text, ""));
        m_SetGoal = Btn(row.transform, "Set goal", () => SendGoal(m_Goal.text));
        m_Stop = Btn(row.transform, "Stop", StopRunner);

        m_Status = Label(rt, LearningPort() > 0 ? "idle" : "learning server disabled (settings.learning_port)", 12f, TextColor);
        m_Status.enableWordWrapping = true;
        m_Status.GetComponent<LayoutElement>().preferredHeight = 48f;
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
