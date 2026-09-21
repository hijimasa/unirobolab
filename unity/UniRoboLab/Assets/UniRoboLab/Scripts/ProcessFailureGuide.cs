using System;

/// <summary>子プロセスのログ末尾を、状態行に出せる原因と復帰手順へ要約する。</summary>
public static class ProcessFailureGuide
{
    public enum Kind
    {
        Communication,
        PortConflict,
        PythonEnvironment,
        MissingArtifact,
        Configuration,
        Unknown,
    }

    public readonly struct Result
    {
        public readonly Kind FailureKind;
        public readonly string Message;
        public bool SuggestTaskReview => FailureKind == Kind.Communication || FailureKind == Kind.MissingArtifact ||
                                         FailureKind == Kind.Configuration || FailureKind == Kind.Unknown;

        public Result(Kind failureKind, string message)
        {
            FailureKind = failureKind;
            Message = message;
        }
    }

    public static Result ForTraining(string log, int exitCode, int port, bool japanese)
    {
        Kind kind = Classify(log);
        string detail = japanese ? " (詳細: train.log)" : " (details: train.log)";
        switch (kind)
        {
            case Kind.Communication:
                return R(kind,
                    $"学習サーバ応答なし: プレイヤーを1つだけ起動し、ポート {port} を確認して再試行。再発時は②で同時数を減らす{detail}",
                    $"Learning server did not respond: run only one player, check port {port}, and retry; if repeated, reduce parallel environments in step 2{detail}", japanese);
            case Kind.PortConflict:
                return R(kind,
                    $"ポート競合: 他のプレイヤーを終了し、学習ポート {port} を空けて再試行{detail}",
                    $"Port conflict: stop other players, free learning port {port}, then retry{detail}", japanese);
            case Kind.PythonEnvironment:
                return R(kind,
                    $"Python環境の失敗: scripts/setup_python.sh --training を実行して再試行{detail}",
                    $"Python environment failed: run scripts/setup_python.sh --training, then retry{detail}", japanese);
            case Kind.MissingArtifact:
                return R(kind,
                    $"学習入力が見つかりません: ②を確定してから再試行{detail}",
                    $"Training input is missing: confirm step 2, then retry{detail}", japanese);
            case Kind.Configuration:
                return R(kind,
                    $"学習設定が合いません: ②の設定を見直して再試行{detail}",
                    $"Training configuration does not match: review step 2, then retry{detail}", japanese);
            default:
                return R(kind,
                    $"学習失敗 (exit {exitCode}): プレイヤー/ポートを確認して再試行。続く場合は②と詳細ログを確認",
                    $"Training failed (exit {exitCode}): check the player/port and retry; if it repeats, review step 2 and Details", japanese);
        }
    }

    public static Result ForRunner(string log, int exitCode, int port, bool japanese)
    {
        Kind kind = Classify(log);
        string detail = japanese ? " (詳細にログ)" : " (log in Details)";
        switch (kind)
        {
            case Kind.Communication:
                return R(kind,
                    $"学習サーバ応答なし: プレイヤーを1つだけ起動し、ポート {port} を確認して「動かす」を再試行{detail}",
                    $"Learning server did not respond: run only one player, check port {port}, then press Run again{detail}", japanese);
            case Kind.PortConflict:
                return R(kind,
                    $"ポート競合: 他のプレイヤーを終了し、学習ポート {port} を空けて再試行{detail}",
                    $"Port conflict: stop other players, free learning port {port}, then retry{detail}", japanese);
            case Kind.PythonEnvironment:
                return R(kind,
                    $"Python環境の失敗: scripts/setup_python.sh --training を実行して再試行{detail}",
                    $"Python environment failed: run scripts/setup_python.sh --training, then retry{detail}", japanese);
            case Kind.MissingArtifact:
                return R(kind,
                    $"方策が見つかりません: ③で学習し直してから「動かす」{detail}",
                    $"Policy is missing: train again in step 3, then press Run{detail}", japanese);
            case Kind.Configuration:
                return R(kind,
                    $"方策と②の設定が一致しません: ②を確定し、③で学習し直す{detail}",
                    $"Policy and step 2 do not match: confirm step 2 and train again in step 3{detail}", japanese);
            default:
                return R(kind,
                    $"実行失敗 (exit {exitCode}): プレイヤー/ポートを確認して再試行。続く場合は②と詳細ログを確認",
                    $"Run failed (exit {exitCode}): check the player/port and retry; if it repeats, review step 2 and Details", japanese);
        }
    }

    public static Kind Classify(string log)
    {
        string s = (log ?? "").ToLowerInvariant();
        if (Has(s, "address already in use", "eaddrinuse", "only one usage of each socket address"))
            return Kind.PortConflict;
        if (Has(s, "timeouterror", "timed out", "connectionrefused", "connection refused", "connectionreset",
                  "connection reset", "brokenpipe", "broken pipe", "unexpected eof", "connection closed",
                  "failed to connect", "no connection could be made"))
            return Kind.Communication;
        if (Has(s, "modulenotfounderror", "no module named", "importerror", "command not found", "not recognized as an internal"))
            return Kind.PythonEnvironment;
        if (Has(s, "filenotfounderror", "no such file or directory", "could not find file", "policy.onnx was not found"))
            return Kind.MissingArtifact;
        if (Has(s, "shape mismatch", "invalidargument", "invalid argument", "dimension mismatch", "observation shape",
                  "action shape", "valueerror", "jsondecodeerror"))
            return Kind.Configuration;
        return Kind.Unknown;
    }

    static bool Has(string text, params string[] needles)
    {
        foreach (string needle in needles)
            if (text.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    static Result R(Kind kind, string ja, string en, bool japanese) => new Result(kind, japanese ? ja : en);
}
