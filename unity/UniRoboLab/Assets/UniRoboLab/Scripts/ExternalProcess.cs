using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// GUI パネルが起動する子プロセス (unirobolab のコマンド) の薄い包み。標準出力・標準エラーを
/// 行単位で溜め、標準入力に行を書け、終了を待つ/殺す。メインスレッドから Drain で取り出す。
/// </summary>
public class ExternalProcess
{
    readonly Process m_Process;
    readonly ConcurrentQueue<string> m_Lines = new ConcurrentQueue<string>();
    public string CommandLine { get; }
    public bool HasExited => m_Process == null || m_Process.HasExited;
    public int ExitCode => m_Process != null && m_Process.HasExited ? m_Process.ExitCode : -1;

    /// <summary>コマンド文字列を /bin/sh -c で起動する。env は "KEY=VALUE;..."。
    /// Windows では Git for Windows の bash.exe (無ければ PATH の bash) を使う。コマンドは POSIX シェルの
    /// 書き方 (引用、2>&1、command -v) なので、cmd.exe では動かない。</summary>
    public ExternalProcess(string commandLine, string envSpec, string workingDir = null)
    {
        CommandLine = commandLine;
        var psi = new ProcessStartInfo
        {
            FileName = ShellPath(),
            Arguments = "-c " + Quote(commandLine),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,   // 日本語の出力を '?' にしない
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        if (!string.IsNullOrEmpty(envSpec))
        {
            foreach (string kv in envSpec.Split(';'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0) psi.Environment[kv.Substring(0, eq).Trim()] = kv.Substring(eq + 1).Trim();
            }
        }
        m_Process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        m_Process.OutputDataReceived += (_, e) => { if (e.Data != null) m_Lines.Enqueue(e.Data); };
        m_Process.ErrorDataReceived += (_, e) => { if (e.Data != null) m_Lines.Enqueue("! " + e.Data); };
        m_Process.Start();
        m_Process.BeginOutputReadLine();
        m_Process.BeginErrorReadLine();
        Debug.Log("[ExternalProcess] started: " + commandLine);
    }

    public bool TryDequeue(out string line) => m_Lines.TryDequeue(out line);

    /// <summary>終了済みのプロセスの非同期出力を読み切る (HasExited の直後に呼ぶ)。</summary>
    public void WaitForExit() { try { m_Process?.WaitForExit(); } catch (Exception) { } }

    public void WriteLine(string line)
    {
        if (HasExited) return;
        try { m_Process.StandardInput.WriteLine(line); m_Process.StandardInput.Flush(); }
        catch (Exception e) { Debug.LogWarning("[ExternalProcess] stdin: " + e.Message); }
    }

    /// <summary>行を送って待ち、終わらなければ殺す。</summary>
    public void Stop(string stopLine = null, int waitMs = 3000)
    {
        try
        {
            if (!HasExited)
            {
                if (stopLine != null) WriteLine(stopLine);
                if (!m_Process.WaitForExit(waitMs)) m_Process.Kill();
            }
        }
        catch (Exception) { }
    }

    public static string Quote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

    /// <summary>使うシェル: Linux/macOS は /bin/sh、Windows は Git for Windows の bash.exe。</summary>
    public static string ShellPath()
    {
        if (!Env.IsWindows) return "/bin/sh";
        foreach (string root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("LocalAppData") + "\\Programs" })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string p = System.IO.Path.Combine(root, "Git", "bin", "bash.exe");
            if (System.IO.File.Exists(p)) return p;
        }
        return "bash.exe";   // PATH 上の bash (Git for Windows / MSYS2)。無ければ起動に失敗し、状態行に出る
    }

    /// <summary>シェルが使えるか (Windows で Git for Windows が無いときの案内用)。</summary>
    public static bool ShellAvailable()
    {
        string sh = ShellPath();
        if (System.IO.Path.IsPathRooted(sh)) return System.IO.File.Exists(sh);
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string d in path.Split(System.IO.Path.PathSeparator))
            if (!string.IsNullOrEmpty(d) && System.IO.File.Exists(System.IO.Path.Combine(d, sh))) return true;
        return false;
    }

    /// <summary>設定と環境変数から unirobolab のインタプリタを決める。</summary>
    public static string UnirobolabPython()
    {
        string fromEnv = Environment.GetEnvironmentVariable("UNIROBOLAB_PYTHON");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        string s = UniRoboLabConfig.Data.python;
        return string.IsNullOrEmpty(s) ? "python3" : s;
    }

    public static string RunnerEnv() =>
        UniRoboLabConfig.Data.policy_runner_env;
}
