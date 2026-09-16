"""sim2sim の report.json をライトユーザー向けの言葉に言い換え、失敗を「次の一手」に変換する。

判定そのものは ros2/sim2sim.py が行う。ここは結果の読み替えだけで、report.json の
checks/detail 文字列から数値を拾う (detail は sim2sim.py が書く固定書式)。
"""
from __future__ import annotations

import difflib
import re

_T = {
    "ja": {
        "pass": "合格: 実機に進めます",
        "fail": "不合格: 下の「次の一手」から直してください",
        "joints_present": "関節が見つかる",
        "node_alive": "方策ノードが動いている",
        "rate": "決めた周期で動いている",
        "obs_fresh": "センサの値が新しい",
        "tracking": "目標に到達する",
        "disturbance": "押されても戻る",
        "finite": "数値が壊れていない",
        "estop": "非常停止が効く",
        "next": "次の一手",
        "ok": "ok", "ng": "NG",
    },
    "en": {
        "pass": "PASS: ready for the real robot",
        "fail": "FAIL: see the next steps below",
        "joints_present": "joints are found",
        "node_alive": "policy node is running",
        "rate": "runs at the chosen rate",
        "obs_fresh": "sensor values are fresh",
        "tracking": "reaches the goals",
        "disturbance": "recovers when pushed",
        "finite": "numbers are sane",
        "estop": "emergency stop works",
        "next": "next steps",
        "ok": "ok", "ng": "NG",
    },
}


def _num(pattern: str, s: str, default: float | None = None) -> float | None:
    m = re.search(pattern, s or "")
    return float(m.group(1)) if m else default


def _list(pattern: str, s: str) -> list[str]:
    m = re.search(pattern, s or "")
    return re.findall(r"'([^']*)'", m.group(1)) if m else []


def explain(report: dict, lang: str = "ja") -> dict:
    """{"pass": bool, "items": [{"key","label","ok","reason"}], "next": [str]}"""
    t = _T.get(lang, _T["ja"])
    ja = lang != "en"
    checks = report.get("checks", {})
    items: list[dict] = []
    nxt: list[str] = []

    def add(key: str, ok: bool, reason: str) -> None:
        items.append({"key": key, "label": t[key], "ok": bool(ok), "reason": reason})

    def step(s: str) -> None:
        if s not in nxt:
            nxt.append(s)

    # --- joints
    jp = checks.get("joints_present")
    node_down = False
    if jp is not None:
        missing = _list(r"missing=(\[[^\]]*\])", jp.get("detail", ""))
        seen = _list(r"seen=(\[[^\]]*\])", jp.get("detail", ""))
        if jp["pass"]:
            add("joints_present", True, (f"{len(seen)} 関節" if ja else f"{len(seen)} joints"))
        else:
            node_down = True
            if not seen:
                add("joints_present", False, "joint_states が届いていない" if ja else "no joint_states received")
                step("シミュレータにロボットがスポーンされているか、契約の ros.namespace が合っているか確認してください"
                     if ja else "check that the robot is spawned and the contract's ros.namespace matches")
            else:
                hints = []
                for m in missing:
                    close = difflib.get_close_matches(m, seen, n=1)
                    hints.append(f"{m} → {close[0]}?" if close else m)
                add("joints_present", False, ("見つからない関節: " if ja else "missing joints: ") + ", ".join(hints))
                step(("契約の関節名を直してください (詳細 > Contract)。ロボット側の関節: " if ja
                      else "fix the joint names in the contract (Expert > Contract). Robot joints: ") + ", ".join(seen))

    # --- node
    na = checks.get("node_alive")
    if na is not None:
        errs = re.search(r"errors=(\[.*\])", na.get("detail", ""))
        n_status = _num(r"(\d+) status msgs", na.get("detail", ""), 0)
        if na["pass"]:
            add("node_alive", True, "")
        else:
            node_down = True
            if not n_status:
                add("node_alive", False, "ノードから応答がない" if ja else "no status from the node")
                step("方策ノードが起動していません。パッケージを作り直して (gen --overwrite) ログを確認してください"
                     if ja else "the policy node did not start; regenerate the package (gen --overwrite) and read its log")
            else:
                e = errs.group(1) if errs else ""
                add("node_alive", False, ("ノードがエラーを報告: " if ja else "node reported errors: ") + e[:120])
                if "joints missing" not in e:
                    step("ノードのエラーを直してください (上の理由を参照)" if ja else "fix the node error above")

    # --- rate
    r = checks.get("rate")
    if r is not None:
        med = _num(r"median ([\d.]+) Hz", r.get("detail", ""))
        tgt = _num(r"target ([\d.]+) Hz", r.get("detail", ""))
        if r["pass"]:
            add("rate", True, f"{med:g} Hz" if med is not None else "")
        elif med is None:
            add("rate", False, "計測できず (ノードが動いていない)" if ja else "not measured (node not running)")
        else:
            add("rate", False, (f"実測 {med:g} Hz / 目標 {tgt:g} Hz" if ja else f"measured {med:g} Hz vs target {tgt:g} Hz"))
            if tgt and med < tgt:
                step("周期が出ていません。契約の control.policy_rate_hz を下げるか、シミュレータの target_fps を上げてください"
                     if ja else "rate too low: lower control.policy_rate_hz in the contract or raise the simulator's target_fps")
            else:
                step("周期が速すぎます。ノードのタイマ設定を確認してください" if ja else "rate too high: check the node timer")

    # --- freshness
    of = checks.get("obs_fresh")
    if of is not None:
        worst = _num(r"worst joint_states age ([\d.]+) ms", of.get("detail", ""))
        limit = _num(r"limit ([\d.]+) ms", of.get("detail", ""))
        if of["pass"]:
            add("obs_fresh", True, (f"最悪 {worst:g} ms 遅れ" if ja else f"worst lag {worst:g} ms") if worst is not None else "")
        elif worst is None:
            add("obs_fresh", False, "計測できず (ノードが動いていない)" if ja else "not measured (node not running)")
        else:
            add("obs_fresh", False, (f"最悪 {worst:g} ms 遅れ (許容 {limit:g} ms)" if ja else f"worst lag {worst:g} ms (limit {limit:g} ms)"))
            step("センサ値が古いです。シミュレータ設定の target_fps を 60 以上にしてください (実機なら配信周期を上げる)"
                 if ja else "stale sensors: set the simulator's target_fps to 60 or more (on a robot, raise the publish rate)")

    # --- tracking (+ disturbance)
    tr = checks.get("tracking")
    wrenches = report.get("wrenches") or []
    if tr is not None:
        phases = tr.get("phases", [])
        n_ok = 0
        worst_ratio = 0.0
        worst_err = worst_tol = None
        no_samples = False
        unmoved = 0
        is_base = False
        for ph in phases:
            ph_ok = True
            for j, res in ph.get("joints", {}).items():
                is_base = is_base or j == "base"
                if "mean_abs_err" not in res:
                    no_samples = True
                    ph_ok = False
                    continue
                e, tol = float(res["mean_abs_err"]), float(res.get("tol", 0.0) or 0.0)
                ratio = e / tol if tol > 0 else 0.0
                if ratio > worst_ratio:
                    worst_ratio, worst_err, worst_tol = ratio, e, tol
                if not res.get("pass"):
                    ph_ok = False
                    goal = ph.get("goal", [])
                    gi = list(ph["joints"]).index(j)
                    g = abs(float(goal[gi])) if j != "base" and gi < len(goal) else None
                    if g is not None and g > 0 and abs(e - g) / g < 0.15:
                        unmoved += 1
            n_ok += int(ph_ok)
        unit = "m" if is_base else "rad"
        if tr["pass"]:
            add("tracking", True, (f"{n_ok}/{len(phases)} 目標, 最大誤差 {worst_err:.3f} {unit}" if ja
                                   else f"{n_ok}/{len(phases)} goals, worst error {worst_err:.3f} {unit}") if worst_err is not None else "")
        else:
            if worst_err is not None:
                add("tracking", False, (f"{n_ok}/{len(phases)} 目標に到達, 最大誤差 {worst_err:.3f} {unit} (許容 {worst_tol:g})" if ja
                                        else f"{n_ok}/{len(phases)} goals reached, worst error {worst_err:.3f} {unit} (tolerance {worst_tol:g})"))
            else:
                add("tracking", False, "計測できず" if ja else "not measured")
            if node_down or (no_samples and worst_err is None):
                pass  # the cause is above
            elif unmoved > 0:
                step("関節が目標へ動いていません (誤差 ≒ 目標)。行動の scale/offset と指令トピックが学習時と同じか確認してください (詳細 > Contract)"
                     if ja else "joints did not move toward the goal (error ≈ goal): check the action scale/offset and command topic (Expert > Contract)")
            elif worst_ratio <= 1.5:
                step((f"惜しい結果です。許容誤差を {worst_tol:g} → {worst_err * 1.2:.2f} {unit} に広げるか、学習をもう少し続けてください (Task タブ)" if ja
                      else f"close: widen the tolerance from {worst_tol:g} to {worst_err * 1.2:.2f} {unit}, or train a little longer (Task tab)"))
                if is_base:
                    step("到達後にその場で留まる学習 (terminate_on_reach=false) と観測ノイズ (obs_noise) を入れて再学習すると安定します"
                         if ja else "retrain with hold-at-goal (terminate_on_reach=false) and observation noise (obs_noise)")
            else:
                step("目標範囲を狭めるか制限時間を延ばして再学習してください (Task タブ)"
                     if ja else "narrow the goal range or extend the time limit and retrain (Task tab)")
                step("観測ノイズ (obs_noise) を入れて再学習すると sim2sim に強くなります"
                     if ja else "retraining with observation noise (obs_noise) makes the policy more robust")
        if wrenches:
            add("disturbance", bool(tr["pass"]), (f"外乱 {len(wrenches)} 回" if ja else f"{len(wrenches)} pushes"))
            if not tr["pass"]:
                step("外乱に弱いです。外乱を小さくして通るか確かめ、観測ノイズ入りで再学習してください"
                     if ja else "weak against pushes: try smaller pushes, then retrain with observation noise")

    # --- finite
    fi = checks.get("finite")
    if fi is not None:
        n_act = _num(r"over (\d+) actions", fi.get("detail", ""), 0)
        bad = _num(r"(\d+) non-finite", fi.get("detail", ""), 0)
        if fi["pass"]:
            add("finite", True, "")
        elif not n_act:
            add("finite", False, "行動が 1 つも出ていない" if ja else "no actions were produced")
        else:
            add("finite", False, (f"NaN/inf が {bad:g} 回" if ja else f"{bad:g} NaN/inf vectors"))
            step("方策が壊れた値を出しています。学習率を下げて学習をやり直してください (詳細 > Train)"
                 if ja else "the policy emits NaN/inf: retrain with a lower learning rate (Expert > Train)")

    # --- estop
    es = checks.get("estop")
    if es is not None:
        acts = _num(r"actions while stopped=(\d+)", es.get("detail", ""))
        if es["pass"]:
            add("estop", True, "")
        elif "never asserted" in es.get("detail", ""):
            add("estop", False, "停止指令が送られていない" if ja else "stop was never sent")
            step("シナリオの estop_test と契約の safety.estop_topic を確認してください"
                 if ja else "check the scenario's estop_test and the contract's safety.estop_topic")
        else:
            add("estop", False, (f"停止中に指令が {acts:g} 回出た" if ja else f"{acts:g} commands while stopped") if acts is not None else es.get("detail", ""))
            step("非常停止中に指令が出ています。契約の safety 設定と生成したノードの停止処理を確認してください"
                 if ja else "commands were sent during e-stop: check the contract's safety section and the node's stop handling")

    if node_down and not nxt:
        step("方策ノードを直してから再チェックしてください" if ja else "fix the policy node, then re-run the check")
    return {"pass": bool(report.get("pass")), "items": items, "next": nxt[:4]}


def format_text(ex: dict, lang: str = "ja") -> str:
    t = _T.get(lang, _T["ja"])
    lines = [t["pass"] if ex["pass"] else t["fail"]]
    for it in ex["items"]:
        mark = "[ok]" if it["ok"] else "[NG]"
        lines.append(f"{mark} {it['label']}" + (f" — {it['reason']}" if it["reason"] else ""))
    if ex["next"]:
        lines.append(t["next"] + ":")
        lines += [f" {i + 1}. {s}" for i, s in enumerate(ex["next"])]
    return "\n".join(lines)
