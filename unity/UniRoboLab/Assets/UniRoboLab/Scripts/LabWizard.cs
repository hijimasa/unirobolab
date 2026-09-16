using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UniRoboLab の画面全体: 上にステッパー (① ロボット → ⑥ 実機へ)、左にフェーズの主画面、右に 3D プレビュー、
/// 下に状態行と「詳細 / 戻る / 次へ」。docs/ux-flow.md v2.1。
/// ヘッドレス/撮影用: SIM_WIZARD_PROJECT=<dir>、SIM_WIZARD_PHASE=<1..6>、SIM_WIZARD_ACTION=<フェーズの主操作>、
/// SIM_GUI_SCREENSHOT="a.png@10,b.png@done"。
/// </summary>
public class LabWizard : MonoBehaviour
{
    public static LabWizard I { get; private set; }
    public Project P { get; private set; }
    public SimulationControl Sim { get; private set; }
    public bool ExpertShown { get; private set; }

    readonly List<Phase> m_Phases = new List<Phase>();
    readonly List<Button> m_StepButtons = new List<Button>();
    int m_Current = -1;
    RectTransform m_Root, m_Main, m_Details, m_PreviewFrame;
    Image m_PreviewCover;
    TMP_Text m_Status, m_Title, m_ProjectLabel;
    Button m_Back, m_Next, m_DetailsBtn;
    Camera m_Cam;
    ExternalProcess m_EnvPy, m_EnvRos;
    readonly List<(string path, float at)> m_Shots = new List<(string, float)>();
    string m_PendingAction;
    float m_ActionAt = -1f;

    public enum PreviewMode { Side, Large, Hidden }

    /// <summary>開始の位置と向き (task.json の start.base、ROS 座標: x 前, y 左, yaw 反時計回り [deg])。①〜⑤ で共通。</summary>
    public float StartX { get { var b = TaskSpec.Load(P.Abs(P.D.task)).start.@base; return b.xy != null && b.xy.Length > 0 ? b.xy[0] : 0f; } }
    public float StartY { get { var b = TaskSpec.Load(P.Abs(P.D.task)).start.@base; return b.xy != null && b.xy.Length > 1 ? b.xy[1] : 0f; } }
    public float StartYawDeg { get { var b = TaskSpec.Load(P.Abs(P.D.task)).start.@base; return b.yaw_deg != null && b.yaw_deg.Length > 0 ? b.yaw_deg[0] : 180f; } }
    public float StartYawRad => StartYawDeg * Mathf.Deg2Rad;

    /// <summary>GUI でスポーンしたロボットを開始の位置と向きへ置く (核の GUI スポーンは姿勢を取らない)。</summary>
    public void PlaceSpawned(string entityName) => PlaceSpawned(entityName, StartX, StartY, StartYawDeg);

    public void PlaceSpawned(string entityName, float rosX, float rosY, float yawDeg)
    {
        var buf = new List<GameObject>(); Sim.GetEntitiesSnapshot(buf);
        Vector3 pos = new Vector3(-rosY, 0f, rosX);                 // ROS (x, y) → Unity (-y, ·, x)
        Quaternion rot = Quaternion.Euler(0f, -yawDeg, 0f);        // ROS の yaw (反時計回り) → Unity の y 回転
        foreach (GameObject e in buf)
        {
            if (e == null || e.name != entityName) continue;
            foreach (ArticulationBody ab in e.GetComponentsInChildren<ArticulationBody>())
                if (ab.isRoot) { Vector3 p = ab.transform.position; ab.TeleportRoot(new Vector3(pos.x, p.y, pos.z), rot); return; }
            e.transform.SetPositionAndRotation(new Vector3(pos.x, e.transform.position.y, pos.z), rot);
            return;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        SimulationControl control = FindFirstObjectByType<SimulationControl>(FindObjectsInactive.Include);
        if (control == null || control.GetComponent<LabWizard>() != null) return;
        control.gameObject.AddComponent<LabWizard>();
    }

    void Start()
    {
        I = this;
        Sim = GetComponent<SimulationControl>();
        Ui.EnsureJapaneseFont();
        Env.Detect();
        m_Cam = Camera.main;
        string dir = Environment.GetEnvironmentVariable("SIM_WIZARD_PROJECT");
        if (string.IsNullOrEmpty(dir)) dir = Project.LastDir() ?? Project.DefaultDir();
        P = Project.Open(dir);
        m_Phases.Add(new PhaseRobot()); m_Phases.Add(new PhaseTask()); m_Phases.Add(new PhaseTrain());
        m_Phases.Add(new PhaseTry()); m_Phases.Add(new PhaseCheck()); m_Phases.Add(new PhaseDeploy());
        if (!Application.isBatchMode || Environment.GetEnvironmentVariable("SIM_WIZARD_PHASE") != null || Environment.GetEnvironmentVariable("SIM_GUI_SCREENSHOT") != null) BuildUi();
        m_EnvPy = Launch(Env.PythonCheckCommand());
        m_EnvRos = Launch(Env.Ros2CheckCommand());
        int start = ResumePhase();
        string ph = Environment.GetEnvironmentVariable("SIM_WIZARD_PHASE");
        if (!string.IsNullOrEmpty(ph) && int.TryParse(ph, out int n)) start = Mathf.Clamp(n - 1, 0, m_Phases.Count - 1);
        GoTo(start, true);
        string shots = Environment.GetEnvironmentVariable("SIM_GUI_SCREENSHOT");
        if (!string.IsNullOrEmpty(shots))
        {
            float delay = float.TryParse(Environment.GetEnvironmentVariable("SIM_GUI_SCREENSHOT_DELAY"), out float d) ? d : 8f;
            foreach (string item in shots.Split(','))
            {
                int at = item.LastIndexOf('@');
                if (at > 0 && item.Substring(at + 1) == "done") m_Shots.Add((item.Substring(0, at), float.PositiveInfinity));
                else if (at > 0 && float.TryParse(item.Substring(at + 1), out float t)) m_Shots.Add((item.Substring(0, at), Time.realtimeSinceStartup + t));
                else m_Shots.Add((item, Time.realtimeSinceStartup + delay));
            }
        }
        m_PendingAction = Environment.GetEnvironmentVariable("SIM_WIZARD_ACTION");
        if (!string.IsNullOrEmpty(m_PendingAction)) m_ActionAt = Time.realtimeSinceStartup + 2f;
        var ros = Unity.Robotics.ROSTCPConnector.ROSConnection.GetOrCreateInstance();
        if (ros != null) ros.ShowHud = false;   // 核の ROS HUD は ⑤ 以外では出さない
    }

    /// <summary>成果物の有無からフェーズを復元する (失効していれば手前に戻す)。</summary>
    int ResumePhase()
    {
        if (!P.Has(P.D.urdf)) return 0;
        if (!P.Exists("train") || P.IsStale("contract") || P.IsStale("train")) return 1;
        if (!P.Exists("run") || P.IsStale("run")) return 2;
        if (!P.Exists("report") || P.IsStale("report")) return 4;
        return 5;
    }

    // ------------------------------------------------------------------ ui
    void BuildUi()
    {
        var canvasGo = new GameObject("WizardCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 10;
        m_Root = canvasGo.GetComponent<RectTransform>();

        // header
        var header = Ui.Box(m_Root, Ui.Bg); Anchor(header.rectTransform, 0f, 1f, 1f, 1f, 0f, -44f, 0f, 0f);
        var hrow = Ui.Row(header.transform, 44f); Stretch(hrow.GetComponent<RectTransform>()); hrow.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(14, 14, 6, 6);
        m_Title = Ui.Label(hrow.transform, "UniRoboLab", 18f, Ui.Header, false, 0f, 130f);
        m_ProjectLabel = Ui.Label(hrow.transform, "", 12f, Ui.Muted);
        Ui.Btn(hrow.transform, Ui.T("プロジェクトを開く...", "Open project..."), OpenProjectDialog, 150f, 26f);
        // stepper
        var stepper = Ui.Box(m_Root, Ui.Panel); Anchor(stepper.rectTransform, 0f, 1f, 1f, 1f, 0f, -84f, 0f, -44f);
        var srow = Ui.Row(stepper.transform, 40f, 4f); Stretch(srow.GetComponent<RectTransform>()); srow.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(10, 10, 6, 6);
        for (int i = 0; i < m_Phases.Count; i++) { int k = i; m_StepButtons.Add(Ui.Btn(srow.transform, m_Phases[i].Title, () => GoTo(k, false), 0f, 28f)); }
        // main (left) and preview (right)
        var mainBg = Ui.Box(m_Root, Ui.Bg); Anchor(mainBg.rectTransform, 0f, 0f, 0.62f, 1f, 0f, 48f, 0f, -84f);
        var mainCol = Ui.Column(mainBg.transform, "Main", 6f, 14); Stretch(mainCol.GetComponent<RectTransform>());
        m_Main = mainCol.GetComponent<RectTransform>();
        m_PreviewFrame = new GameObject("PreviewFrame", typeof(RectTransform)).GetComponent<RectTransform>();
        m_PreviewFrame.SetParent(m_Root, false); Anchor(m_PreviewFrame, 0.62f, 0f, 1f, 1f, 0f, 48f, 0f, -84f);
        m_PreviewCover = Ui.Box(m_PreviewFrame, Ui.Bg); Stretch(m_PreviewCover.rectTransform); m_PreviewCover.gameObject.SetActive(false);
        // details (over the preview)
        var det = Ui.Box(m_Root, Ui.Panel2); Anchor(det.rectTransform, 0.62f, 0f, 1f, 1f, 0f, 48f, 0f, -84f);
        var detCol = Ui.Column(det.transform, "Details", 4f, 10); Stretch(detCol.GetComponent<RectTransform>());
        m_Details = detCol.GetComponent<RectTransform>(); det.gameObject.SetActive(false);
        // footer
        var footer = Ui.Box(m_Root, Ui.Panel); Anchor(footer.rectTransform, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 48f);
        var frow = Ui.Row(footer.transform, 48f); Stretch(frow.GetComponent<RectTransform>()); frow.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(14, 14, 10, 10);
        m_Status = Ui.Label(frow.transform, "", 12f, Ui.Text, false);
        m_DetailsBtn = Ui.Btn(frow.transform, Ui.T("詳細", "Details"), () => ShowDetails(!ExpertShown), 80f);
        m_Back = Ui.Btn(frow.transform, Ui.T("← 戻る", "← Back"), () => GoTo(m_Current - 1, false), 100f);
        m_Next = Ui.Btn(frow.transform, Ui.T("次へ →", "Next →"), () => { if (m_Phases[m_Current].OnNext()) GoTo(m_Current + 1, false); }, 110f);
        Ui.SetBtn(m_Next, null, Ui.BtnActive);
        foreach (Phase ph in m_Phases) { ph.W = this; ph.Build(m_Main, m_Details); ph.Root.SetActive(false); if (ph.DetailsRoot != null) ph.DetailsRoot.SetActive(false); }
    }

    static void Anchor(RectTransform rt, float ax0, float ay0, float ax1, float ay1, float ox0, float oy0, float ox1, float oy1)
    {
        rt.anchorMin = new Vector2(ax0, ay0); rt.anchorMax = new Vector2(ax1, ay1); rt.offsetMin = new Vector2(ox0, oy0); rt.offsetMax = new Vector2(ox1, oy1);
    }
    static void Stretch(RectTransform rt) => Anchor(rt, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);

    // ------------------------------------------------------------------ phases
    public void GoTo(int index, bool silent)
    {
        index = Mathf.Clamp(index, 0, m_Phases.Count - 1);
        if (index != m_Current && index > 0 && !m_Phases[index].CanEnter(out string why))
        {
            if (!silent) Status(why, Ui.Warn);
            if (m_Current >= 0) return;
            index = ResumePhase();   // 起動時に入れないフェーズを指定された: 前提の揃うところへ
            if (index > 0 && !m_Phases[index].CanEnter(out _)) index = 0;
        }
        if (m_Current >= 0 && m_Root != null) { m_Phases[m_Current].Leave(); m_Phases[m_Current].Root.SetActive(false); if (m_Phases[m_Current].DetailsRoot != null) m_Phases[m_Current].DetailsRoot.SetActive(false); }
        m_Current = index;
        P.D.phase_reached = Mathf.Max(P.D.phase_reached, index); P.Save();
        Phase ph = m_Phases[index];
        if (m_Root != null)
        {
            ph.Root.SetActive(true);
            if (ph.DetailsRoot != null) ph.DetailsRoot.SetActive(ExpertShown);
            SetPreview(ph.Preview);
        }
        ph.Enter();
        RefreshStepper();
        if (Application.isBatchMode) Debug.Log($"[Wizard] phase {index + 1}: {ph.Title}");
    }

    public void RefreshStepper()
    {
        if (m_Root == null || m_Current < 0) return;
        m_ProjectLabel.text = Ui.T("プロジェクト: ", "project: ") + P.Dir;
        for (int i = 0; i < m_Phases.Count; i++)
        {
            Phase ph = m_Phases[i];
            string mark = i == m_Current ? "● " : (ph.Stale() ? "! " : (ph.Done() ? "✓ " : ""));
            bool can = i == 0 || ph.CanEnter(out _);
            Ui.SetBtn(m_StepButtons[i], mark + ph.Title, i == m_Current ? Ui.BtnActive : (ph.Stale() ? new Color(0.55f, 0.40f, 0.20f) : (ph.Done() ? Ui.BtnDone : Ui.BtnNormal)), can);
        }
        m_Back.interactable = m_Current > 0;
        bool nextOk = m_Current < m_Phases.Count - 1 && m_Phases[m_Current].CanProceed(out string reason);
        m_Next.interactable = nextOk;
        if (!nextOk && m_Current < m_Phases.Count - 1) m_Next.GetComponentInChildren<TMP_Text>().text = Ui.T("次へ →", "Next →");
    }

    public void ShowDetails(bool on)
    {
        ExpertShown = on;
        if (m_Root == null) return;
        m_Details.parent.gameObject.SetActive(on);
        Phase ph = m_Phases[m_Current];
        if (ph.DetailsRoot != null) ph.DetailsRoot.SetActive(on);
        Ui.SetBtn(m_DetailsBtn, null, on ? Ui.BtnActive : Ui.BtnNormal);
        SetPreview(on ? PreviewMode.Hidden : ph.Preview);
    }

    /// <summary>3D の枠: 右側 (Side)、大きく (Large)、隠す (Hidden)。カメラの viewport rect を変える。</summary>
    public void SetPreview(PreviewMode mode)
    {
        if (m_Cam == null || m_Root == null) return;
        float top = 84f / Screen.height, bottom = 48f / Screen.height;
        switch (mode)
        {
            case PreviewMode.Side:
                m_Cam.enabled = true; m_Cam.rect = new Rect(0.62f, bottom, 0.38f, 1f - top - bottom);
                Anchor((RectTransform)m_Main.parent, 0f, 0f, 0.62f, 1f, 0f, 48f, 0f, -84f); m_PreviewCover.gameObject.SetActive(false);
                break;
            case PreviewMode.Large:
                m_Cam.enabled = true; m_Cam.rect = new Rect(0.34f, bottom, 0.66f, 1f - top - bottom);
                Anchor((RectTransform)m_Main.parent, 0f, 0f, 0.34f, 1f, 0f, 48f, 0f, -84f); m_PreviewCover.gameObject.SetActive(false);
                break;
            case PreviewMode.Hidden:
                m_Cam.enabled = false;
                Anchor((RectTransform)m_Main.parent, 0f, 0f, 0.62f, 1f, 0f, 48f, 0f, -84f); m_PreviewCover.gameObject.SetActive(true);
                break;
        }
        Anchor(m_PreviewFrame, mode == PreviewMode.Large ? 0.34f : 0.62f, 0f, 1f, 1f, 0f, 48f, 0f, -84f);
    }

    public void Status(string text, Color? color = null)
    {
        if (m_Status != null) { m_Status.text = text; m_Status.color = color ?? Ui.Text; }
        if (Application.isBatchMode) Debug.Log("[Wizard] " + text);
    }

    public ExternalProcess Launch(string cmd) => new ExternalProcess(cmd, UniRoboLabConfig.Data.policy_runner_env, Env.RepoRoot);

    public string Py => ExternalProcess.Quote(Env.Python);

    public Phase Current => m_Current >= 0 ? m_Phases[m_Current] : null;

    void OpenProjectDialog()
    {
        string[] sel = SFB.StandaloneFileBrowser.OpenFolderPanel(Ui.T("プロジェクトのフォルダ", "Project folder"), P.Dir, false);
        if (sel == null || sel.Length == 0 || string.IsNullOrEmpty(sel[0])) return;
        P = Project.Open(sel[0]);
        foreach (Phase ph in m_Phases) ph.OnProjectChanged();
        GoTo(ResumePhase(), true);
    }

    // ------------------------------------------------------------------ loop
    void Update()
    {
        if (m_EnvPy != null && m_EnvPy.HasExited)
        {
            m_EnvPy.WaitForExit(); var sb = new StringBuilder(); while (m_EnvPy.TryDequeue(out string l)) sb.AppendLine(l);
            Env.PythonStatus = m_EnvPy.ExitCode == 0 && sb.ToString().Contains("ok") ? "ok" : sb.ToString().Trim();
            m_EnvPy = null; RefreshStepper();
        }
        if (m_EnvRos != null && m_EnvRos.HasExited)
        {
            m_EnvRos.WaitForExit(); string last = "none"; while (m_EnvRos.TryDequeue(out string l)) last = l.Trim();
            Env.Ros2 = last; m_EnvRos = null;
        }
        Current?.Tick();
        if (m_ActionAt > 0f && Time.realtimeSinceStartup >= m_ActionAt)
        {
            m_ActionAt = -1f;
            if (m_PendingAction == "next") { if (Current != null && Current.OnNext()) GoTo(m_Current + 1, false); }
            else Current?.Action(m_PendingAction);
            m_PendingAction = null;
        }
        for (int i = m_Shots.Count - 1; i >= 0; i--)
        {
            if (Time.realtimeSinceStartup < m_Shots[i].at) continue;
            ScreenCapture.CaptureScreenshot(m_Shots[i].path);
            Debug.Log("[Wizard] screenshot -> " + m_Shots[i].path);
            m_Shots.RemoveAt(i);
        }
    }

    /// <summary>「done」指定の撮影を今から 3 秒後に予約する (学習完了などの区切りで呼ぶ)。</summary>
    public void TriggerDoneShots()
    {
        for (int i = 0; i < m_Shots.Count; i++) if (float.IsPositiveInfinity(m_Shots[i].at)) m_Shots[i] = (m_Shots[i].path, Time.realtimeSinceStartup + 3f);
    }

    void OnApplicationQuit() { foreach (Phase ph in m_Phases) ph.Leave(); }
}

/// <summary>フェーズ画面の共通部分。</summary>
public abstract class Phase
{
    public LabWizard W;
    public GameObject Root, DetailsRoot;
    public abstract string Key { get; }
    public abstract string Title { get; }
    public virtual LabWizard.PreviewMode Preview => LabWizard.PreviewMode.Side;
    public abstract void Build(RectTransform main, RectTransform details);
    public virtual void Enter() { }
    public virtual void Leave() { }
    public virtual void Tick() { }
    public virtual void OnProjectChanged() { }
    /// <summary>このフェーズに入れるか (前提)。</summary>
    public virtual bool CanEnter(out string reason) { reason = ""; return true; }
    /// <summary>「次へ」を押せるか。</summary>
    public virtual bool CanProceed(out string reason) { reason = ""; return true; }
    /// <summary>「次へ」で行う処理。false なら進まない。</summary>
    public virtual bool OnNext() => true;
    public abstract bool Done();
    public virtual bool Stale() => false;
    /// <summary>撮影/ヘッドレス用の主操作 (SIM_WIZARD_ACTION)。</summary>
    public virtual void Action(string name) { }
    protected Project P => W.P;
}
