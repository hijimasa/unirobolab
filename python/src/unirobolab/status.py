"""学習の進み具合をライトユーザー向けの数字にまとめる (status.json の中身)。

progress.csv の行 (1 エピソード 1 行) から、成功率・誤差の推移・残り時間の見込み・
「次の一手」を計算する。GUI は数値と hint のキーだけを受け取り、文言はここで持つ。
"""
from __future__ import annotations

import numpy as np

# 報酬の内訳の呼び方。学習器の列名 (task.py の TERMS) をそのまま出しても意味が取れないので、
# 「何に対する報酬/罰か」を書く。罰 (重みが負) は penalty=True。
TERM_LABELS = {
    # 関節目標
    "tracking_l1": {"ja": "目標角からのずれ", "en": "distance from the target angle", "penalty": True},
    "tracking_l2": {"ja": "目標角からのずれ (二乗)", "en": "squared distance from the target angle", "penalty": True},
    # 地点到達
    "base_distance": {"ja": "目標地点からの距離", "en": "distance from the goal", "penalty": True},
    "base_progress": {"ja": "目標地点へ近づいた分", "en": "progress toward the goal", "penalty": False},
    "base_reached": {"ja": "到達できた回数", "en": "goal reached", "penalty": False},
    # 条件 (手先・物体)
    "progress": {"ja": "目標へ近づいた分", "en": "progress toward the goal", "penalty": False},
    "distance": {"ja": "目標からの距離", "en": "distance from the goal", "penalty": True},
    "reached": {"ja": "条件を満たせた回数", "en": "condition met", "penalty": False},
    "reach_progress": {"ja": "手先が対象へ近づいた分", "en": "hand approaching the object", "penalty": False},
    "reach_distance": {"ja": "手先と対象の距離", "en": "distance from hand to object", "penalty": True},
    # 共通
    "action_rate": {"ja": "動きの荒さ", "en": "jerky commands", "penalty": True},
    "velocity": {"ja": "関節の速さ", "en": "joint speed", "penalty": True},
}


def term_breakdown(rows: list[dict], window: int) -> list[dict]:
    """報酬の内訳: 直近 window 回の平均と、その前の window 回との差 (伸びているか)。
    成功率だけでは「なぜ上がらないか」が見えないので、どの項が効いているかを出す。"""
    if not rows:
        return []
    keys = [k[5:] for k in rows[-1] if k.startswith("term_")]
    win = rows[-window:]
    prev = rows[-2 * window:-window] if len(rows) >= 2 * window else []
    out = []
    for k in keys:
        mean = float(np.mean([float(r.get("term_" + k, 0.0) or 0.0) for r in win]))
        before = float(np.mean([float(r.get("term_" + k, 0.0) or 0.0) for r in prev])) if prev else None
        label = TERM_LABELS.get(k, {"ja": k, "en": k, "penalty": mean < 0})
        out.append({"key": k, "ja": label["ja"], "en": label["en"], "penalty": bool(label.get("penalty", mean < 0)),
                    "mean": round(mean, 4), "has_prev": before is not None,
                    "prev": round(before if before is not None else mean, 4)})
    total = sum(abs(t["mean"]) for t in out) or 1.0
    for t in out:
        t["share"] = round(abs(t["mean"]) / total, 4)
    out.sort(key=lambda t: -t["share"])
    return out


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
    "penalty_dominant": {
        "ja": "報酬より罰が大きい状態です。内訳の一番大きい罰の項を見て、② の制限時間を延ばすか許容誤差を広げてください",
        "en": "penalties outweigh the reward: look at the largest penalty in the breakdown, then extend the time or widen the tolerance in step 2",
    },
    "exploration_collapsed": {
        "ja": "方策のばらつきが早く縮んでいます (探索をやめています)。詳細 > Train の学習率を下げるか、ent_coef を上げてやり直してください",
        "en": "the policy's spread collapsed early (it stopped exploring): lower the learning rate or raise ent_coef under Expert > Train and retrain",
    },
    "value_not_learning": {
        "ja": "価値関数が当たっていません (説明率が低い)。1 回の制限時間が長すぎるか、報酬が疎すぎます (Task タブ)",
        "en": "the value function is not fitting (low explained variance): the attempt may be too long or the reward too sparse (Task tab)",
    },
    "updates_too_large": {
        "ja": "1 回の更新が大きすぎます (KL が大きい)。詳細 > Train の学習率を下げてやり直してください",
        "en": "the updates are too large (high KL): lower the learning rate under Expert > Train and retrain",
    },
}


def train_status(rows: list[dict], total_timesteps: int, n_envs: int, wall_s: float, unit: str = "",
                 tolerance: float | None = None, window: int = 200, early_stop: dict | None = None,
                 phase: str = "training", learner: dict | None = None) -> dict:
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

    # 報酬の内訳と学習器の内部。成功率だけでは「何が起きているか」が読めないので、
    # どの項が効いているか (terms) と、学習自体が健全か (learner) を出す。
    terms = term_breakdown(rows, window)
    learner = dict(learner or {})
    if phase == "training" and terms and progress >= 0.3:
        gain = sum(t["mean"] for t in terms if not t["penalty"])
        loss = -sum(t["mean"] for t in terms if t["penalty"])
        if loss > 0 and gain < loss and success_rate < 0.5:
            hints.append("penalty_dominant")
    if phase == "training" and progress >= 0.3:
        std = learner.get("policy_std")
        if std is not None and std < 0.08 and success_rate < 0.5:
            hints.append("exploration_collapsed")
        ev = learner.get("explained_variance")
        if ev is not None and ev < 0.1 and progress >= 0.5:
            hints.append("value_not_learning")
        kl = learner.get("approx_kl")
        if kl is not None and kl > 0.05:
            hints.append("updates_too_large")

    st = {
        "phase": phase,
        "timesteps": t, "total_timesteps": total, "progress": round(progress, 4),
        "wall_s": round(float(wall_s), 1), "eta_s": None if eta is None else round(float(eta), 0),
        "env_steps_per_s": round(rate, 1), "n_envs": int(n_envs),
        "episodes": n, "window": min(window, n),
        "success_rate": round(success_rate, 4),
        "final_abs_err_mean": None if err_mean is None else round(err_mean, 4),
        "tolerance": tolerance,
        "unit": unit,            # 誤差と許容の単位 ("rad" | "m")。空なら単位を出さない
        "early_stop": early_stop,
        "terms": terms,
        "learner": learner,
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
            u = f" {st['unit']}" if st.get("unit") else ""
            tol = f" / 目標 {st['tolerance']:g}{u}" if (ja and st["tolerance"] is not None) else (
                f" / target {st['tolerance']:g}{u}" if st["tolerance"] is not None else "")
            parts.append((f"誤差 {st['final_abs_err_mean']:.3f}{u}{tol}" if ja
                          else f"error {st['final_abs_err_mean']:.3f}{u}{tol}"))
    if st["phase"] == "training" and st["eta_s"] is not None:
        m = int(st["eta_s"] // 60)
        parts.append((f"残り およそ {m} 分" if m >= 1 else "残り 1 分未満") if ja else (f"about {m} min left" if m >= 1 else "under a minute left"))
        if st.get("early_stop"):
            parts.append("(目標に達すれば早く終わります)" if ja else "(ends early once the target is met)")
    parts.append(f"{st['n_envs']} 体並列" if ja else f"{st['n_envs']} robots in parallel")
    text = " / ".join(parts)
    # 報酬の内訳: 効いている順に 3 つ。矢印は前の窓との比較 (伸びている / 減っている)
    terms = st.get("terms") or []
    if terms:
        def one(t: dict) -> str:
            arrow = "" if not t.get("has_prev") else (" ↑" if t["mean"] > t["prev"] else (" ↓" if t["mean"] < t["prev"] else ""))
            return f"{t['ja' if ja else 'en']} {t['mean']:+.2f}{arrow}"
        text += "\n" + ("内訳: " if ja else "breakdown: ") + " / ".join(one(t) for t in terms[:3])
    lr = st.get("learner") or {}
    if lr:
        bits = []
        if lr.get("policy_std") is not None:
            bits.append((f"ばらつき {lr['policy_std']:.2f}" if ja else f"spread {lr['policy_std']:.2f}"))
        if lr.get("explained_variance") is not None:
            bits.append((f"価値の説明率 {lr['explained_variance'] * 100:.0f}%" if ja else f"value fit {lr['explained_variance'] * 100:.0f}%"))
        if lr.get("approx_kl") is not None:
            bits.append((f"更新の大きさ {lr['approx_kl']:.4f}" if ja else f"update size {lr['approx_kl']:.4f}"))
        if bits:
            text += "\n" + ("学習器: " if ja else "learner: ") + " / ".join(bits)
    for h in st.get("hints", []):
        text += "\n" + ("次の一手: " if ja else "next: ") + h["ja" if ja else "en"]
    return text
