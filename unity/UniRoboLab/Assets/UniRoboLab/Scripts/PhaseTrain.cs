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
    TMP_Text m_Progress, m_Log, m_Hint;
    RawImage m_Curve; Texture2D m_CurveTex; int m_CurvePoints;
    ExternalProcess m_Train, m_StatusProc;
    string m_RunDir; float m_StatusNextAt; bool m_Finished;
    readonly StringBuilder m_LogBuf = new StringBuilder();

    public override void Build(RectTransform main, RectTransform details)
    {
        Root = Ui.Column(main, "Train", 6f);
        var r = Ui.Row(Root.transform, 32f);
        m_Start = Ui.Btn(r.transform, Ui.T("▶ 学習を始める", "▶ Start training"), StartTraining, 0f, 30f);
        m_Stop = Ui.Btn(r.transform, Ui.T("■ 止める", "■ Stop"), StopTraining, 110f, 30f);
        Ui.SetBtn(m_Start, null, Ui.BtnActive);
        m_Progress = Ui.Label(Root.transform, "", 13f, Ui.Text, true, 60f);
        m_CurveTex = Ui.DarkTexture(560, 150);
        m_Curve = Ui.Image(Root.transform, m_CurveTex, 150f);
        Ui.Label(Root.transform, Ui.T("青: 試行ごとの誤差   緑: 成功率 (移動平均)", "blue: error per attempt   green: success rate (moving average)"), 10f, Ui.Muted);
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
           .Append(" --spawn-urdf ").Append(ExternalProcess.Quote(P.Abs(P.D.urdf))).Append(" --spawn-spacing 0.6 2>&1");
        m_Train = W.Launch(cmd.ToString());
        m_Finished = false; m_CurvePoints = 0; m_StatusNextAt = 0f; m_LogBuf.Clear(); m_Hint.text = ""; m_Back2.gameObject.SetActive(false);
        m_Progress.text = Ui.T("学習を始めています...", "starting...");
        m_Start.interactable = false;
        W.Status(Ui.T("学習中", "training"));
        var cam = Camera.main != null ? Camera.main.GetComponent<LabCamera>() : null;
        // 学習器は ROS の y 方向 (= Unity の -x) に 0.6 m 間隔で並べる。列の中央を狙う
        if (cam != null) { cam.target = new Vector3(-0.6f * (spec.training.n_envs - 1) * 0.5f, 0.15f, 0f); cam.distance = 1.0f + 0.55f * spec.training.n_envs; }
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
            while (m_Train.TryDequeue(out string l)) { m_LogBuf.AppendLine(l); if (m_LogBuf.Length > 6000) m_LogBuf.Remove(0, 2000); }
            if (m_Log != null && m_Log.gameObject.activeInHierarchy) m_Log.text = m_LogBuf.ToString();
            if (m_Train.HasExited)
            {
                int code = m_Train.ExitCode; m_Train = null; m_Start.interactable = true;
                if (code == 0 && File.Exists(Path.Combine(m_RunDir, "policy.onnx")))
                {
                    P.D.run_dir = P.Rel(m_RunDir); P.Stamp("run"); P.Save(); m_Finished = true;
                    W.Status(Ui.T("学習が終わりました。④ で試すか、⑤ でチェックへ", "Training finished. Try it in step 4 or check it in step 5"), Ui.Accent);
                    W.RefreshStepper(); W.TriggerDoneShots();
                }
                else W.Status(Ui.T($"学習が異常終了しました (exit {code})。「詳細」のログを見てください", $"training failed (exit {code}); see the log under Details"), Ui.Bad);
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
        int win = Mathf.Max(10, err.Count / 10);
        var rate = new List<float>(err.Count); float acc = 0f;
        for (int i = 0; i < suc.Count; i++) { acc += suc[i]; if (i >= win) acc -= suc[i - win]; rate.Add(acc / Mathf.Min(win, i + 1)); }
        int w = m_CurveTex.width, h = m_CurveTex.height;
        var px = new Color32[w * h]; for (int i = 0; i < px.Length; i++) px[i] = new Color32(20, 20, 24, 255);
        void Plot(List<float> v, Color32 c, float lo, float hi)
        {
            if (hi - lo < 1e-6f) hi = lo + 1e-6f;
            for (int x = 0; x < w; x++)
            {
                int i0 = x * v.Count / w, i1 = Mathf.Min(v.Count, (x + 1) * v.Count / w); if (i1 <= i0) i1 = i0 + 1;
                float m = 0f; for (int i = i0; i < i1 && i < v.Count; i++) m += v[i]; m /= (i1 - i0);
                int y = Mathf.Clamp((int)((m - lo) / (hi - lo) * (h - 1)), 0, h - 1);
                px[y * w + x] = c; if (y > 0) px[(y - 1) * w + x] = c;
            }
        }
        float eHi = 0f; foreach (float e in err) eHi = Mathf.Max(eHi, e);
        Plot(rate, new Color32(90, 220, 120, 255), 0f, 1f); Plot(err, new Color32(90, 160, 255, 255), 0f, eHi);
        m_CurveTex.SetPixels32(px); m_CurveTex.Apply();
    }

    public override bool CanProceed(out string reason) { reason = ""; return true; }   // ④ は任意 (⑤ の前提は学習結果)
    public override bool Done() => P.Exists("run") && !P.IsStale("run");
    public override bool Stale() => P.IsStale("run");
    public override void Leave() { }
    public override void Action(string name) { if (name == "train") StartTraining(); }
    public bool IsTraining => m_Train != null && !m_Train.HasExited;
    public void Kill() { m_Train?.Stop(null, 1000); m_StatusProc?.Stop(); }
}
