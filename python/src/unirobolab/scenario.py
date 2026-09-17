"""配備前チェック (sim2sim) の既定シナリオを契約とタスク仕様から作る。非常停止の試験を必ず含める。"""
from __future__ import annotations

import math
from typing import Any

import numpy as np


def default_scenario(contract: dict[str, Any], spec: dict[str, Any] | None = None) -> dict[str, Any]:
    spec = spec or {}
    goal = (spec.get("goal") or [{}])[0]
    ep = spec.get("episode", {})
    rate = float(contract.get("control", {}).get("policy_rate_hz", 25.0))
    obs_src = [t.get("source") for t in (contract.get("observations") or [])]
    base = "base_goal_xy" in obs_src
    time_s = float(ep.get("time_s", 10.0 if base else 2.0) or (10.0 if base else 2.0))
    if goal.get("type") == "object_in_region" or any(t == "object_goal" for t in obs_src):
        # 物体を領域へ: 目標は物体の到達位置 (根リンク座標系)。物体は各目標の前に開始条件の中心へ置き直す。
        region = goal.get("region") or {}
        c0 = np.asarray(region.get("center", [0.5, 0.0, 0.0]), float)
        half = 0.5 * np.asarray(region.get("size", [0.2, 0.2, 0.0]), float) if region.get("shape", "box") == "box" \
            else np.array([float(region.get("radius", 0.1))] * 2 + [0.0])
        goals = [[round(float(v), 4) for v in c0 + f * half] for f in (np.array([0, 0, 0]), np.array([0.5, 0.5, 0]), np.array([-0.5, -0.5, 0]))]
        tol = round(float(goal.get("tolerance", 0.05)) * 1.5, 4)
        hold = max(4.0, time_s + 2.0)
        objects = spec.get("objects") or []
        obj = goal.get("object") or next((t.get("object") for t in (contract.get("observations") or []) if t.get("source") == "object_goal"), None)
        note = f"auto: 3 goal points inside the task's region for object '{obj}' (root-link frame), tolerance {tol:g} m = 1.5 x the training tolerance"
        return {"_note": note, "goals": goals, "hold_s": hold, "settle_window_s": 1.0, "warmup_s": 3.0,
                "default_tolerance": tol, "rate_tolerance": 0.15, "max_obs_age_s": round(3.0 / rate, 3),
                "objects": objects, "object": obj, "reset_between_goals": True,
                "estop_test": {"at_s": 1.0, "release_at_s": 3.0, "hold_s": 6.0}}
    if goal.get("type") == "link_near" or any(t == "link_goal" for t in obs_src):
        region = goal.get("region") or {}
        c0 = np.asarray(region.get("center", [0.3, 0.0, 0.3]), float)
        half = 0.5 * np.asarray(region.get("size", [0.2, 0.2, 0.2]), float) if region.get("shape", "box") == "box" \
            else np.full(3, float(region.get("radius", 0.1)))
        goals = [[round(float(v), 4) for v in c0 + f * half] for f in (np.array([0, 0, 0]), np.array([0.5, 0.5, 0.5]), np.array([-0.5, -0.5, -0.5]))]
        tol = round(float(goal.get("tolerance", 0.03)) * 1.5, 4)
        hold = max(3.0, time_s + 1.0)
        note = f"auto: 3 points inside the task's region (root-link frame), tolerance {tol:g} m = 1.5 x the training tolerance"
        return {"_note": note, "goals": goals, "hold_s": hold, "settle_window_s": 1.0, "warmup_s": 3.0,
                "default_tolerance": tol, "rate_tolerance": 0.15, "max_obs_age_s": round(3.0 / rate, 3),
                "estop_test": {"at_s": 1.0, "release_at_s": 3.0, "hold_s": 6.0}}
    if base:
        region = goal.get("region", {})
        r = 0.5 * (float(region.get("r_min", 1.0)) + float(region.get("r_max", 2.5)))
        angles = [0.0, 90.0, -135.0]
        goals = [[round(r * math.cos(math.radians(a)), 3), round(r * math.sin(math.radians(a)), 3)] for a in angles]
        tol = float(goal.get("tolerance", 0.15)) * 1.5
        hold = max(6.0, time_s + 2.0)
        note = f"auto: 3 points at {r:g} m from the start (from the task's region), tolerance {tol:g} m = 1.5 x the training tolerance"
    else:
        n = len(contract.get("robot", {}).get("joints", []))
        rng = goal.get("range")
        if isinstance(rng, list) and len(rng) == 2:
            amp = 0.6 * max(abs(float(rng[0])), abs(float(rng[1])))
        else:
            lim = contract.get("safety", {}).get("joint_limits", {})
            spans = [0.5 * (float(v[1]) - float(v[0])) for v in lim.values()] or [0.6]
            amp = 0.5 * min(spans)
        amp = round(min(amp, 1.0), 3)
        goals = [[amp] * n, [-amp] * n, [0.0] * n]
        tol = round(max(2.0 * float(goal.get("tolerance", 0.05)), 0.03), 4)
        hold = max(3.0, time_s + 1.0)
        note = f"auto: step goals +-{amp:g} rad and 0, tolerance {tol:g} rad = 2 x the training tolerance"
    return {"_note": note, "goals": goals, "hold_s": hold, "settle_window_s": 1.0, "warmup_s": 3.0,
            "default_tolerance": tol, "rate_tolerance": 0.15, "max_obs_age_s": round(3.0 / rate, 3),
            "estop_test": {"at_s": 1.0, "release_at_s": 3.0, "hold_s": 6.0}}
