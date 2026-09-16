"""学習の進み具合をライトユーザー向けの数字にまとめる (status.json の中身)。

progress.csv の行 (1 エピソード 1 行) から、成功率・誤差の推移・残り時間の見込み・
「次の一手」を計算する。GUI は数値と hint のキーだけを受け取り、文言はここで持つ。
"""
from __future__ import annotations

import numpy as np

_HINTS = {
    "no_success": {
        "ja": "成功が出ていません。目標範囲を狭めるか、1 回の制限時間を延ばしてください (Task タブ)",
        "en": "no successes yet: narrow the goal range or extend the time per attempt (Task tab)",
    },
    "getting_worse": {
        "ja": "途中から誤差が悪化しています。学習率を下げてやり直してください (詳細 > Train)",
        "en": "error got worse mid-way: retrain with a lower learning rate (Expert > Train)",
    },
    "plateau": {
        "ja": "誤差が下がり止まっています。許容誤差を広げるか、制限時間を延ばしてください (Task タブ)",
        "en": "error stopped improving: widen the tolerance or extend the time per attempt (Task tab)",
    },
}


def train_status(rows: list[dict], total_timesteps: int, n_envs: int, wall_s: float,
                 tolerance: float | None = None, window: int = 200, early_stop: dict | None = None,
                 phase: str = "training") -> dict:
    """rows: progress.csv の行 (dict)。tolerance: 成功とみなす最終誤差 (関節: early_stop の閾値、
    地点: reach_radius)。行に success があればそれを優先し、無ければ final_abs_err <= tolerance。"""
    n = len(rows)
    t = int(rows[-1]["timesteps"]) if n else 0
    total = max(int(total_timesteps), 1)
    progress = min(1.0, t / total)
    rate = t / wall_s if wall_s > 0 and t > 0 else 0.0
    eta = (total - t) / rate if rate > 0 else None
    win = rows[-window:] if n else []

    def ok(r: dict) -> bool:
        if tolerance is not None:
            return float(r.get("final_abs_err", np.inf)) <= tolerance
        return bool(int(r.get("success", 0)))

    success_rate = float(np.mean([ok(r) for r in win])) if win else 0.0
    err_mean = float(np.mean([float(r["final_abs_err"]) for r in win])) if win else None
    prev = rows[-2 * window:-window] if n >= 2 * window else []
    prev_err = float(np.mean([float(r["final_abs_err"]) for r in prev])) if prev else None

    hints: list[str] = []
    if phase == "training" and progress >= 0.3 and win and success_rate == 0.0:
        hints.append("no_success")
    if phase == "training" and prev_err is not None and err_mean is not None and progress >= 0.5:
        if err_mean > prev_err * 1.2 and (tolerance is None or err_mean > tolerance):
            hints.append("getting_worse")
        elif tolerance is not None and abs(err_mean - prev_err) < 0.05 * max(prev_err, 1e-9) and err_mean > tolerance * 1.5 and progress >= 0.7:
            hints.append("plateau")

    st = {
        "phase": phase,
        "timesteps": t, "total_timesteps": total, "progress": round(progress, 4),
        "wall_s": round(float(wall_s), 1), "eta_s": None if eta is None else round(float(eta), 0),
        "env_steps_per_s": round(rate, 1), "n_envs": int(n_envs),
        "episodes": n, "window": min(window, n),
        "success_rate": round(success_rate, 4),
        "final_abs_err_mean": None if err_mean is None else round(err_mean, 4),
        "tolerance": tolerance,
        "early_stop": early_stop,
        "hints": [{"key": k, "ja": _HINTS[k]["ja"], "en": _HINTS[k]["en"]} for k in hints],
    }
    return st


def format_text(st: dict, lang: str = "ja") -> str:
    ja = lang != "en"
    parts = []
    if st["phase"] == "done":
        parts.append("学習が終わりました" if ja else "training finished")
    elif st["phase"] == "evaluating":
        parts.append("仕上げの評価中" if ja else "evaluating")
    elif st["phase"] == "stopped":
        parts.append("停止しました" if ja else "stopped")
    else:
        parts.append(f"学習中 {st['progress'] * 100:.0f}%" if ja else f"training {st['progress'] * 100:.0f}%")
    if st["episodes"]:
        parts.append((f"成功率 {st['success_rate'] * 100:.0f}% (直近 {st['window']} 回)" if ja
                      else f"success {st['success_rate'] * 100:.0f}% (last {st['window']})"))
        if st["final_abs_err_mean"] is not None:
            tol = f" / 目標 {st['tolerance']:g}" if (ja and st["tolerance"] is not None) else (
                f" / target {st['tolerance']:g}" if st["tolerance"] is not None else "")
            parts.append((f"誤差 {st['final_abs_err_mean']:.3f}{tol}" if ja else f"error {st['final_abs_err_mean']:.3f}{tol}"))
    if st["phase"] == "training" and st["eta_s"] is not None:
        m = int(st["eta_s"] // 60)
        parts.append((f"残り およそ {m} 分" if m >= 1 else "残り 1 分未満") if ja else (f"about {m} min left" if m >= 1 else "under a minute left"))
        if st.get("early_stop"):
            parts.append("(目標に達すれば早く終わります)" if ja else "(ends early once the target is met)")
    parts.append(f"{st['n_envs']} 体並列" if ja else f"{st['n_envs']} robots in parallel")
    text = " / ".join(parts)
    for h in st.get("hints", []):
        text += "\n" + ("次の一手: " if ja else "next: ") + h["ja" if ja else "en"]
    return text
