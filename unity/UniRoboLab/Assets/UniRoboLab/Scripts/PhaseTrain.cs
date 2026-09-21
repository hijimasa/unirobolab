using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>③ 学習: 始める / 止める、進捗の文と曲線、次の一手。結果は runs/<時刻>/ に積む。</summary>
public class PhaseTrain : Phase
{
    public override string Key => "train";
    public override string Title => Ui.T("③ 学習", "3 Train");

    Button m_Start, m_Stop, m_Back2;
    TMP_Text m_Progress, m_Log, m_Hint, m_AxisTop, m_AxisBottom;
    RawImage m_Curve; Texture2D m_CurveTex; int m_CurvePoints;
    ExternalProcess m_Train, m_StatusProc;
    string m_RunDir; float m_StatusNextAt; bool m_Finished;
    readonly StringBuilder m_LogBuf = new StringBuilder();
    StreamWriter m_LogFile;   // <run>/train.log: 学習器の出力を残す (失敗の原因を後で追える)

    public override void Build(RectTransform main, RectTransform details)
    {
        Root = Ui.Column(main, "Train", 6f);
        var r = Ui.Row(Root.transform, 32f);
        m_Start = Ui.Btn(r.transform, Ui.T("▶ 学習を始める", "▶ Start training"), StartTraining, 0f, 30f);
        m_Stop = Ui.Btn(r.transform, Ui.T("■ 止める", "■ Stop"), StopTraining, 110f, 30f);
        Ui.SetBtn(m_Start, null, Ui.BtnActive);
        m_Progress = Ui.Label(Root.transform, "", 13f, Ui.Text, true, 60f);
        m_AxisTop = Ui.Label(Root.transform, "", 11f, Ui.Muted);
        m_CurveTex = Ui.DarkTexture(560, 150);
        m_Curve = Ui.Image(Root.transform, m_CurveTex, 150f);
        m_AxisBottom = Ui.Label(Root.transform, "", 11f, Ui.Muted, true, 46f);
        m_AxisBottom.text = Ui.T("横軸: 試行の順 (左が最初)。青の点: その試行の終わりに目標からどれだけ離れていたか。橙の線: 成功とみなす誤差 (この線より下なら成功)。緑の線: 直近の試行で成功した割合 (右端の目盛 0〜100 %)",
                                "x: attempts in order (first on the left). Blue dots: distance from the goal at the end of each attempt. Orange line: the error that counts as success (below it = success). Green line: share of recent attempts that succeeded (right scale 0-100 %)");
        m_Hint = Ui.Label(Root.transform, "", 12f, Ui.Warn, true, 44f);
        m_Back2 = Ui.Btn(Root.transform, Ui.T("② に戻って条件を変える", "Back to step 2 to change the task"), () => W.GoTo(1, false), 260f, 26f);
        m_Back2.gameObject.SetActive(false);
        Ui.Spacer(Root.transform);

        DetailsRoot = Ui.Column(details, "TrainDetails", 4f);
        Ui.Label(DetailsRoot.transform, Ui.T("学習器のログ", "Trainer log"), 12f, Ui.Muted);
        m_Log = Ui.Label(DetailsRoot.transform, "", 10f, Ui.Text, true, 0f);
        m_Log.GetComponent<LayoutElement>().flexibleHeight = 1f; m_Log.alignment = TextAlignmentOptions.BottomLeft;
    }

    public override bool CanEnter(out string reason)
    {
        reason = Ui.T("先に ② でタスクを決めてください", "define the task in step 2 first");
        return P.Exists("train");
    }

    public override void Enter()
    {
        m_Finished = P.Exists("run") && !P.IsStale("run");
        if (m_Finished && string.IsNullOrEmpty(m_RunDir)) { m_RunDir = P.Abs(P.D.run_dir); m_StatusNextAt = 0f; }
        string pre = Precheck();
        m_Start.interactable = pre == null && (m_Train == null || m_Train.HasExited);
        if (pre != null) W.Status(pre, Ui.Warn);
        else if (m_Train != null && !m_Train.HasExited) W.Status(Ui.T("学習中です", "training..."));
        else if (m_Finished) W.Status(Ui.T("学習結果があります。④ で試すか、⑤ でチェックへ", "A trained policy exists. Try it in step 4 or check it in step 5"), Ui.Accent);
        else W.Status(Ui.T("「学習を始める」を押してください", "Press Start training"));
        if (P.IsStale("run")) m_Hint.text = Ui.T("! タスクか契約が変わっています。学習し直してください", "! the task or the contract changed; train again");
    }

    string Precheck()
    {
        if (Env.LearningPort() <= 0) return Ui.T("シミュレータの学習サーバが無効です (SIM_LEARNING_PORT か settings.learning_port)", "the simulator's learning server is off (SIM_LEARNING_PORT or settings.learning_port)");
        if (Env.PythonStatus != "ok" && Env.PythonStatus != "") return Ui.T("Python 環境に問題: ", "Python environment problem: ") + Env.PythonStatus + Ui.T(" → scripts/setup_python.sh --training", " → scripts/setup_python.sh --training");
        return null;
    }

    public void StartTraining()
    {
        if (m_Train != null && !m_Train.HasExited) return;
        string pre = Precheck(); if (pre != null) { W.Status(pre, Ui.Warn); return; }
        TaskSpec spec = TaskSpec.Load(P.Abs(P.D.task));
        m_RunDir = Path.Combine(P.Dir, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var cmd = new StringBuilder();
        cmd.Append(W.Py).Append(" -u -m unirobolab train ").Append(ExternalProcess.Quote(P.Abs(P.D.contract)))
           .Append(" --config ").Append(ExternalProcess.Quote(P.Abs(P.D.train))).Append(" --out ").Append(ExternalProcess.Quote(m_RunDir))
           .Append(" --transport direct --port ").Append(Env.LearningPort()).Append(" --n-envs ").Append(Mathf.Max(1, spec.training.n_envs))
           .Append(" --spawn-urdf ").Append(ExternalProcess.Quote(P.Abs(P.D.urdf))).Append(" --spawn-spacing 0.6 --spawn-layout grid --spawn-yaw ").Append(W.StartYawRad.ToString("F5"))
           .Append(" --spawn-origin ").Append(W.StartX.ToString("F3")).Append(' ').Append(W.StartY.ToString("F3")).Append(" 2>&1");
        Directory.CreateDirectory(m_RunDir);
        try { m_LogFile?.Dispose(); m_LogFile = new StreamWriter(Path.Combine(m_RunDir, "train.log"), false) { AutoFlush = true }; m_LogFile.WriteLine("$ " + cmd); } catch (Exception) { m_LogFile = null; }
        m_Train = W.Launch(cmd.ToString());
        m_Finished = false; m_CurvePoints = 0; m_StatusNextAt = 0f; m_LogBuf.Clear(); m_Hint.text = ""; m_Back2.gameObject.SetActive(false);
        m_Progress.text = Ui.T("学習を始めています...", "starting...");
        m_Start.interactable = false;
        W.Status(Ui.T("学習中", "training"));
        var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        // 学習器は 0.6 m 間隔の格子 (ceil(sqrt(n)) 列; ROS x = Unity z, ROS y = Unity -x) に並べる。格子の中央を狙う
        if (cam != null)
        {
            int n = Mathf.Max(1, spec.training.n_envs); int cols = Mathf.CeilToInt(Mathf.Sqrt(n)); int rows = Mathf.CeilToInt(n / (float)cols);
            cam.target = new Vector3(-W.StartY - 0.6f * (cols - 1) * 0.5f, 0.15f, W.StartX + 0.6f * (rows - 1) * 0.5f);
            cam.distance = 1.2f + 0.7f * Mathf.Max(cols, rows);
        }
    }

    public void StopTraining()
    {
        if (m_Train == null) return;
        m_Train.Stop(null, 2000); m_Train = null; m_Start.interactable = true;
        W.Status(Ui.T("学習を止めました", "training stopped"));
    }

    public override void Tick()
    {
        if (m_Train != null)
        {
            while (m_Train.TryDequeue(out string l)) { m_LogBuf.AppendLine(l); m_LogFile?.WriteLine(l); if (m_LogBuf.Length > 6000) m_LogBuf.Remove(0, 2000); }
            if (m_Log != null && m_Log.gameObject.activeInHierarchy) m_Log.text = m_LogBuf.ToString();
            if (m_Train.HasExited)
            {
                m_Train.WaitForExit(); while (m_Train.TryDequeue(out string l2)) { m_LogBuf.AppendLine(l2); m_LogFile?.WriteLine(l2); }
                int code = m_Train.ExitCode; m_Train = null; m_Start.interactable = true;
                m_LogFile?.WriteLine($"[exit {code}]"); m_LogFile?.Dispose(); m_LogFile = null;
                if (code == 0 && File.Exists(Path.Combine(m_RunDir, "policy.onnx")))
                {
                    P.D.run_dir = P.Rel(m_RunDir); P.Stamp("run"); P.Save(); m_Finished = true;
                    // 目標の成功率に届いたかで文言を変える (届かなくても方策は出ている: ④ で試せる)
                    float rate = -1f; string sj = Path.Combine(m_RunDir, "status.json");
                    if (File.Exists(sj)) rate = Ui.Num(File.ReadAllText(sj), "success_rate", -1f);
                    float target = TaskSpec.Load(P.Abs(P.D.task)).training.success_target;
                    if (rate >= 0f && target > 0f && rate < target)
                        W.Status(Ui.T($"学習は終わりましたが成功率 {rate * 100f:F0} % で目標 {target * 100f:F0} % に届いていません。④ で様子を見られます。上げるには ② で許容誤差を広げる、制限時間を延ばす、開始姿勢の幅を狭める、または学習をもう一度",
                                      $"Training finished at {rate * 100f:F0} % success, below the {target * 100f:F0} % target. You can still try it in step 4; to improve, widen the tolerance, extend the time, narrow the start pose spread in step 2, or train again"), Ui.Warn);
                    else
                        W.Status(Ui.T("学習が終わりました。④ で試すか、⑤ でチェックへ", "Training finished. Try it in step 4 or check it in step 5"), Ui.Accent);
                    W.RefreshStepper(); W.TriggerDoneShots();
                }
                else
                {
                    var failure = ProcessFailureGuide.ForTraining(m_LogBuf.ToString(), code, Env.LearningPort(), Ui.Japanese);
                    m_Hint.text = failure.Message;
                    m_Back2.gameObject.SetActive(failure.SuggestTaskReview);
                    W.Status(failure.Message, Ui.Bad);
                }
                m_StatusNextAt = 0f;
            }
        }
        PollStatus();
    }

    void PollStatus()
    {
        if (string.IsNullOrEmpty(m_RunDir)) return;
        if (m_StatusProc != null)
        {
            if (!m_StatusProc.HasExited) return;
            m_StatusProc.WaitForExit(); var sb = new StringBuilder(); while (m_StatusProc.TryDequeue(out string l)) sb.AppendLine(l);
            string text = sb.ToString().Trim(); bool ok = m_StatusProc.ExitCode == 0; m_StatusProc = null;
            if (ok && text.Length > 0)
            {
                string[] lines = text.Split('\n');
                m_Progress.text = lines[0];
                m_Hint.text = lines.Length > 1 ? string.Join("\n", lines, 1, lines.Length - 1) : "";
                m_Back2.gameObject.SetActive(lines.Length > 1);
                if (Application.isBatchMode) Debug.Log("[Wizard/train] " + text.Replace('\n', ' '));
            }
            RedrawCurve();
            return;
        }
        bool training = m_Train != null && !m_Train.HasExited;
        if (!training && m_StatusNextAt < 0f) return;
        if (Time.realtimeSinceStartup < m_StatusNextAt) return;
        m_StatusNextAt = training ? Time.realtimeSinceStartup + 1f : -1f;
        string path = Path.Combine(m_RunDir, "status.json");
        if (!File.Exists(path)) return;
        m_StatusProc = W.Launch($"{W.Py} -m unirobolab train-status {ExternalProcess.Quote(path)} --lang {(Ui.Japanese ? "ja" : "en")} 2>&1");
    }

    void RedrawCurve()
    {
        string path = Path.Combine(m_RunDir ?? "", "progress.csv");
        if (!File.Exists(path) || m_CurveTex == null) return;
        var err = new List<float>(); var suc = new List<float>();
        try
        {
            using var r = new StreamReader(path);
            string header = r.ReadLine(); if (header == null) return;
            string[] cols = header.Split(',');
            int iErr = Array.IndexOf(cols, "final_abs_err"), iSuc = Array.IndexOf(cols, "success");
            string line;
            while ((line = r.ReadLine()) != null)
            {
                string[] f = line.Split(',');
                if (f.Length <= Math.Max(iErr, iSuc)) continue;
                if (float.TryParse(f[iErr], out float e)) { err.Add(e); suc.Add(iSuc >= 0 && float.TryParse(f[iSuc], out float sv) ? sv : 0f); }
            }
        }
        catch (IOException) { return; }
        if (err.Count == 0 || err.Count == m_CurvePoints) return;
        m_CurvePoints = err.Count;
        TaskSpec spec = TaskSpec.Load(P.Abs(P.D.task));
        float tol = spec.Goal0.tolerance > 0f ? spec.Goal0.tolerance : 0.05f;
        // 成功 = 許容誤差以内 (status.py と同じ定義。CSV の success 列は地点到達の学習器だけが立てる)
        bool anyFlag = false; foreach (float f in suc) if (f > 0f) { anyFlag = true; break; }
        if (!anyFlag) for (int i = 0; i < suc.Count; i++) suc[i] = err[i] <= tol ? 1f : 0f;
        int win = Mathf.Max(10, err.Count / 10);
        var rate = new List<float>(err.Count); float acc = 0f;
        for (int i = 0; i < suc.Count; i++) { acc += suc[i]; if (i >= win) acc -= suc[i - win]; rate.Add(acc / Mathf.Min(win, i + 1)); }
        int w = m_CurveTex.width, h = m_CurveTex.height;
        var px = new Color32[w * h]; for (int i = 0; i < px.Length; i++) px[i] = new Color32(20, 20, 24, 255);
        void Plot(List<float> v, Color32 c, float lo, float hi, int thick)
        {
            if (hi - lo < 1e-6f) hi = lo + 1e-6f;
            for (int x = 0; x < w; x++)
            {
                int i0 = x * v.Count / w, i1 = Mathf.Min(v.Count, (x + 1) * v.Count / w); if (i1 <= i0) i1 = i0 + 1;
                float m = 0f; for (int i = i0; i < i1 && i < v.Count; i++) m += v[i]; m /= (i1 - i0);
                int y = Mathf.Clamp((int)((m - lo) / (hi - lo) * (h - 1)), 0, h - 1);
                for (int t = 0; t < thick; t++) if (y - t >= 0) px[(y - t) * w + x] = c;
            }
        }
        // 縦軸: 誤差 0 〜 eHi。目標付近が見えるように上限は目標の 5 倍まで (それより大きい初期の誤差は上端に張り付く)
        float eMax = 0f; foreach (float e in err) eMax = Mathf.Max(eMax, e);
        float eHi = Mathf.Clamp(eMax, tol * 3f, tol * 5f);
        for (int gy = 1; gy < 4; gy++) { int y = gy * h / 4; for (int x = 0; x < w; x += 3) px[y * w + x] = new Color32(50, 50, 58, 255); }   // 薄い横罫線
        int yTol = Mathf.Clamp((int)(tol / eHi * (h - 1)), 0, h - 1);
        for (int x = 0; x < w; x++) { px[yTol * w + x] = new Color32(255, 170, 60, 255); if (yTol + 1 < h) px[(yTol + 1) * w + x] = new Color32(255, 170, 60, 255); }
        Plot(rate, new Color32(90, 220, 120, 255), 0f, 1f, 3); Plot(err, new Color32(90, 160, 255, 255), 0f, eHi, 2);
        m_CurveTex.SetPixels32(px); m_CurveTex.Apply();
        string unit = spec.Goal0.type == "joints_near" ? "rad" : "m";
        float last = rate.Count > 0 ? rate[rate.Count - 1] : 0f;
        m_AxisTop.text = Ui.T($"縦軸: 誤差 0 〜 {eHi:F2} {unit}   橙の線: 成功の目標 {tol:g} {unit}   緑: 成功率 (今 {last * 100f:F0} %)   試行 {err.Count} 回",
                              $"y: error 0 to {eHi:F2} {unit}   orange: success target {tol:g} {unit}   green: success rate (now {last * 100f:F0} %)   {err.Count} attempts");
    }

    public override bool CanProceed(out string reason) { reason = ""; return true; }   // ④ は任意 (⑤ の前提は学習結果)
    public override bool Done() => P.Exists("run") && !P.IsStale("run");
    public override bool Stale() => P.IsStale("run");
    public override void Leave() { }
    public override void Action(string name) { if (name == "train") StartTraining(); }
    public bool IsTraining => m_Train != null && !m_Train.HasExited;
    public void Kill() { m_Train?.Stop(null, 1000); m_StatusProc?.Stop(); }
}
