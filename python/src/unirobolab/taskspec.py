"""タスク仕様 (task.json): 「どこから始まって、どうなれば成功か」。GUI の ② タスク画面が書き、
ここから契約 (contract) と学習設定 (train) を生成する。ユーザーは契約 JSON を探さないし開かない。

仕様の形 (spec_version 0):

    {
      "spec_version": 0,
      "robot": {"urdf": "servo_demo.urdf", "name": "servo_demo", "namespace": "servo_demo",
                "command_mode": "joint_state_topic", "controller": "", "estop_topic": ""},
      "start": {"joints": "zero", "base": {"xy": [0, 0], "yaw_deg": [0, 0]}},
      "goal": [
        {"type": "joints_near", "range": [-1.2, 1.2], "tolerance": 0.05},
        {"type": "base_in_region", "region": {"shape": "ring", "center": [0, 0], "r_min": 1.0, "r_max": 2.5,
                                                "angle_deg": [-180, 180]}, "tolerance": 0.15}
      ],
      "episode": {"time_s": 3.0, "hold_s": 0.0},
      "training": {"n_envs": 8, "success_target": 0.9}
    }

v1 の原始条件は joints_near (関節が目標角に; 目標は範囲から抽選) と base_in_region (基体が領域内;
領域は ring / box / sphere) の 2 つ。既存の task.py (joint_target / base_target) にそのまま写像する。
将来の条件 (物体が領域内、手先が点の近く) は task.py 側の条件ベース環境と一緒に足す。
"""
from __future__ import annotations

import math
import os
from typing import Any

from . import draft as draft_mod

SPEC_VERSION = 0
CONDITION_TYPES = ("joints_near", "base_in_region", "link_near", "object_in_region")


class TaskSpecError(ValueError):
    pass


def preset(kind: str, urdf: str, name: str | None = None, namespace: str | None = None) -> dict[str, Any]:
    """プリセット (例として置くだけ): joint_target / base_target を仕様の形で返す。"""
    robot = {"urdf": urdf}
    if name:
        robot["name"] = name
    if namespace:
        robot["namespace"] = namespace
    if kind == "joint_target":
        goal = [{"type": "joints_near", "range": "auto", "tolerance": 0.05}]
        episode = {"time_s": 2.0, "hold_s": 0.0}
    elif kind == "link_target":
        goal = [{"type": "link_near", "link": "", "tolerance": 0.03,
                 "region": {"shape": "box", "center": [0.3, 0.0, 0.3], "size": [0.2, 0.2, 0.2]}}]
        episode = {"time_s": 3.0, "hold_s": 0.0}
    elif kind == "push_object":
        goal = [{"type": "object_in_region", "object": "cube", "tolerance": 0.05,
                 "region": {"shape": "box", "center": [0.5, 0.0, 0.0], "size": [0.2, 0.2, 0.0]}}]
        episode = {"time_s": 6.0, "hold_s": 0.0}
    elif kind == "base_target":
        goal = [{"type": "base_in_region", "tolerance": 0.15,
                 "region": {"shape": "ring", "center": [0.0, 0.0], "r_min": 1.0, "r_max": 2.5, "angle_deg": [-180.0, 180.0]}}]
        episode = {"time_s": 10.0, "hold_s": 0.0}
    else:
        raise TaskSpecError(f"unknown preset {kind!r} (joint_target | link_target | push_object | base_target)")
    spec = {"spec_version": SPEC_VERSION, "robot": robot,
            "start": {"joints": "zero", "base": {"xy": [0.0, 0.0], "yaw_deg": [0.0, 0.0]}},
            "goal": goal, "episode": episode, "training": {"n_envs": 8, "success_target": 0.9}}
    if kind == "push_object":
        spec["objects"] = [{"name": "cube", "shape": "box", "size": [0.05, 0.05, 0.05], "mass": 0.1,
                            "start": {"center": [0.3, 0.0, 0.0], "size": [0.1, 0.1, 0.0], "yaw_deg": [-30.0, 30.0]}}]
    return spec


def validate(spec: dict[str, Any]) -> list[str]:
    """人が読める問題の一覧 (空なら OK)。"""
    problems = []
    if spec.get("spec_version", 0) != SPEC_VERSION:
        problems.append(f"spec_version は {SPEC_VERSION} (got {spec.get('spec_version')})")
    if not spec.get("robot", {}).get("urdf"):
        problems.append("robot.urdf が無い")
    goal = spec.get("goal") or []
    if not goal:
        problems.append("終了条件 (goal) が 1 つも無い")
    for i, c in enumerate(goal):
        t = c.get("type")
        if t not in CONDITION_TYPES:
            problems.append(f"goal[{i}].type={t!r} は未対応 ({', '.join(CONDITION_TYPES)})")
            continue
        tol = c.get("tolerance")
        if tol is None or float(tol) <= 0:
            problems.append(f"goal[{i}].tolerance は正の数")
        if t == "base_in_region":
            r = c.get("region", {})
            if r.get("shape") not in ("ring", "box", "sphere"):
                problems.append(f"goal[{i}].region.shape は ring | box | sphere")
    names = {o.get("name") for o in spec.get("objects") or []}
    for i, c in enumerate(goal):
        if c.get("type") == "link_near" and not c.get("link"):
            problems.append(f"goal[{i}] (link_near) は link が要る")
        if c.get("type") == "object_in_region" and c.get("object") not in names:
            problems.append(f"goal[{i}] (object_in_region) の object {c.get('object')!r} が objects に無い")
    types = [c.get("type") for c in goal]
    if len(types) != len(set(types)):
        problems.append("同じ種類の終了条件は 1 つまで")
    if "joints_near" in types and "link_near" in types:
        problems.append("関節目標と手先の条件は同時に使えない (どちらかにする)")
    if float(spec.get("episode", {}).get("time_s", 0) or 0) <= 0:
        problems.append("episode.time_s は正の数")
    return problems


def _joint_range(c: dict[str, Any], contract: dict[str, Any], train: dict[str, Any]) -> list[float]:
    rng = c.get("range", "auto")
    if rng == "auto" or rng is None or (isinstance(rng, list) and len(rng) == 0):   # [] = auto (GUI は空配列で書く)
        return list(train["task"].get("goal_range", [-0.5, 0.5]))   # draft: 安全範囲の 80 %
    if isinstance(rng, dict):   # 関節ごと → task.py は対称のスカラ範囲なので最小の絶対値に丸める (保守的)
        lo = max(float(v[0]) for v in rng.values()); hi = min(float(v[1]) for v in rng.values())
        return [round(lo, 3), round(hi, 3)]
    return [round(float(rng[0]), 3), round(float(rng[1]), 3)]


def _region_to_radius(region: dict[str, Any], center_xy: list[float]) -> tuple[list[float], list[float]]:
    """領域を task.py の (goal_radius, goal_angle_deg) に写す。box/sphere は中心距離に丸める (v1)。"""
    shape = region.get("shape", "ring")
    if shape == "ring":
        return ([float(region.get("r_min", 1.0)), float(region.get("r_max", 2.5))],
                [float(a) for a in region.get("angle_deg", [-180.0, 180.0])])
    cx, cy = region.get("center", [0.0, 0.0])
    d = math.hypot(cx - center_xy[0], cy - center_xy[1])
    if shape == "sphere":
        r = float(region.get("radius", 0.5))
        return ([max(0.1, d - r), d + r], [-180.0, 180.0])
    size = region.get("size", [1.0, 1.0])
    half = 0.5 * math.hypot(float(size[0]), float(size[1]))
    return ([max(0.1, d - half), d + half], [-180.0, 180.0])


def estimate_time_s(spec: dict[str, Any], env_steps_per_s: float = 1500.0) -> tuple[float, str]:
    """学習時間の目安 (秒) と根拠の 1 行。経験則: servo (関節 2、±1.2 rad、許容 0.05) で 13 万ステップ、
    許容を半分にすると約 2.5 倍、diffbot (領域到達、許容 0.15 m) で 20 万ステップ。並列数は速度に効く。"""
    goal = spec.get("goal") or [{}]
    c = goal[0]
    n_envs = int(spec.get("training", {}).get("n_envs", 8))
    if c.get("type") == "base_in_region":
        tol = float(c.get("tolerance", 0.15))
        steps = 200000 * (0.15 / max(tol, 1e-3)) ** 1.0
        why = f"地点到達、許容 {tol:g} m、{n_envs} 体並列"
    elif c.get("type") == "link_near":
        tol = float(c.get("tolerance", 0.03))
        steps = 60000 * (0.03 / max(tol, 1e-3)) ** 1.2   # servo の手先 (許容 3 cm) で 5.2 万ステップ
        why = f"手先を領域へ、許容 {tol:g} m、{n_envs} 体並列"
    elif c.get("type") == "object_in_region":
        tol = float(c.get("tolerance", 0.05))
        steps = 250000 * (0.05 / max(tol, 1e-3)) ** 1.2   # 押し (平面腕、許容 5 cm) の目安; 到達より探索が要る
        why = f"物体を領域へ、許容 {tol:g} m、{n_envs} 体並列"
    else:
        tol = float(c.get("tolerance", 0.05))
        steps = 130000 * (0.05 / max(tol, 1e-3)) ** 1.3
        why = f"関節目標、許容 {tol:g} rad、{n_envs} 体並列"
    rate = env_steps_per_s * (n_envs / 8.0) ** 0.7
    return steps / rate, why


def generate(spec: dict[str, Any], spec_dir: str = ".") -> tuple[dict[str, Any], dict[str, Any]]:
    """仕様 → (契約, 学習設定)。契約の骨格は draft (URDF から) で、条件が task と早期終了を決める。"""
    problems = validate(spec)
    if problems:
        raise TaskSpecError("; ".join(problems))
    robot = spec["robot"]
    urdf = robot["urdf"]
    if not os.path.isabs(urdf):
        urdf = os.path.normpath(os.path.join(spec_dir, urdf))
    contract, train = draft_mod.draft(urdf, robot.get("name") or None, robot.get("namespace") or None, robot.get("onnx") or None)
    # 実機の接続先は ① で決めた値 (仕様の robot 節) を契約に写す。⑥ では変えない
    from .deploy import ros_set
    ros_set(contract, command_mode=robot.get("command_mode") or None, controller=robot.get("controller") or None,
            estop_topic=robot.get("estop_topic") or None)
    task = train["task"]
    es = train["train"].setdefault("early_stop", {})
    rate = float(contract["control"]["policy_rate_hz"])
    ep = spec.get("episode", {})
    c = spec["goal"][0]
    tol = float(c["tolerance"])
    task["episode_steps"] = max(1, int(round(float(ep.get("time_s", 2.0)) * rate)))
    if c["type"] == "object_in_region":
        _apply_object(spec, c, contract, task, es, urdf, tol)
        train["n_envs"] = int(spec.get("training", {}).get("n_envs", 8))
        contract["_task_spec"] = {"spec_version": SPEC_VERSION, "goal": spec["goal"], "episode": ep, "objects": spec.get("objects", [])}
        return contract, train
    if c["type"] == "link_near":
        _apply_link_near(spec, c, contract, task, es, urdf, tol)
        train["n_envs"] = int(spec.get("training", {}).get("n_envs", 8))
        contract["_task_spec"] = {"spec_version": SPEC_VERSION, "goal": spec["goal"], "episode": ep}
        return contract, train
    if c["type"] == "joints_near":
        if task.get("type") != "joint_target":
            raise TaskSpecError("関節目標の条件は固定基体のロボット向け (この URDF は移動基体と判定)")
        task["goal_range"] = _joint_range(c, contract, train)
        es.update({"metric": "final_abs_err", "threshold": tol})
        task["_note"] = f"task.json: joints_near, goal sampled in {task['goal_range']}, success |q-goal| <= {tol} rad"
    else:
        if task.get("type") != "base_target":
            raise TaskSpecError("基体の領域の条件は移動基体のロボット向け (この URDF は固定基体と判定)")
        start_xy = [float(v) for v in spec.get("start", {}).get("base", {}).get("xy", [0.0, 0.0])]
        task["goal_radius"], task["goal_angle_deg"] = _region_to_radius(c.get("region", {}), start_xy)
        task["reach_radius"] = tol
        task["terminate_on_reach"] = float(ep.get("hold_s", 0.0)) <= 0.0 and bool(c.get("stop_at_goal", False))
        es.update({"metric": "success_rate", "threshold": float(spec.get("training", {}).get("success_target", 0.9))})
        task["_note"] = f"task.json: base_in_region {c.get('region', {}).get('shape')}, success within {tol} m"
    train["n_envs"] = int(spec.get("training", {}).get("n_envs", 8))
    contract["_task_spec"] = {"spec_version": SPEC_VERSION, "goal": spec["goal"], "episode": ep}
    return contract, train


def _apply_link_near(spec: dict[str, Any], c: dict[str, Any], contract: dict[str, Any], task: dict[str, Any],
                     es: dict[str, Any], urdf: str, tol: float) -> None:
    """手先 (リンクの点) を領域へ: 契約の観測を q, qd, link_position, link_goal, prev_a にし、
    学習設定を conditions 型にする。固定基体・移動基体を問わない (v1 は固定基体の腕を想定)。"""
    from .fk import chain_to, load_tree
    if task.get("type") != "joint_target":
        raise TaskSpecError("手先の条件は v1 では固定基体のロボット向け")
    tree = load_tree(urdf)
    link = c["link"]
    if link not in tree["links"]:
        raise TaskSpecError(f"link {link!r} が URDF に無い")
    chain = chain_to(tree, link)
    point = list(c.get("point") or tree["tips"].get(link, [0.0, 0.0, 0.0]))
    region = c.get("region") or {"shape": "box", "center": [0.3, 0.0, 0.3], "size": [0.2, 0.2, 0.2]}
    obs = contract["observations"]
    keep = [t for t in obs if t.get("source") in ("joint_position", "joint_velocity")]
    contract["observations"] = keep + [
        {"name": "tip", "source": "link_position", "unit": "m", "scale": 1.0, "link": link, "point": point, "chain": chain},
        {"name": "tip_goal", "source": "link_goal", "unit": "m", "scale": 1.0, "link": link, "point": point, "chain": chain},
        {"name": "prev_a", "source": "last_action"},
    ]
    task.pop("goal_range", None)
    task["type"] = "conditions"
    task["conditions"] = [{"kind": "link_near", "link": link, "point": point, "chain": chain, "region": region, "tolerance": tol}]
    task["reward"] = {"progress": 5.0, "distance": -0.5, "reached": 1.0, "action_rate": -0.05}
    task["stop_at_goal"] = False
    task["_note"] = f"task.json: link_near {link} point {point} in {region.get('shape')} region, success within {tol} m"
    es.clear()
    es.update({"metric": "success_rate", "threshold": float(spec.get("training", {}).get("success_target", 0.9)), "window": 200, "min_timesteps": 50000})
    contract.setdefault("ros", {})["goal_topic"] = contract.get("ros", {}).get("goal_topic") or f"/{contract['ros'].get('namespace', 'robot')}/policy/command"


def _apply_object(spec: dict[str, Any], c: dict[str, Any], contract: dict[str, Any], task: dict[str, Any],
                  es: dict[str, Any], urdf: str, tol: float) -> None:
    """物体を領域へ (押す・運ぶ、把持なし): 観測 = q, qd, 手先の位置, 物体の位置, 物体の目標との差, prev_a。
    報酬は成功に加えて「手先が物体へ近づく」「物体が目標へ近づく」の整形。物体の位置は実機では
    ros.object_topics[名前] (PoseStamped、根リンク座標系) から取る。"""
    from .fk import chain_to, load_tree
    tree = load_tree(urdf)
    obj = c["object"]
    hand = spec.get("hand") or c.get("hand")
    if not hand:
        movable = [j["child"] for j in tree["joints"].values() if j["type"] in ("revolute", "continuous", "prismatic")]
        hand = movable[-1] if movable else None
    reach = None
    obs = [t for t in contract["observations"] if t.get("source") in ("joint_position", "joint_velocity")]
    if hand and hand in tree["links"]:
        chain = chain_to(tree, hand); point = list(c.get("hand_point") or tree["tips"].get(hand, [0.0, 0.0, 0.0]))
        obs.append({"name": "hand", "source": "link_position", "unit": "m", "scale": 1.0, "link": hand, "point": point, "chain": chain})
        reach = {"link": hand, "chain": chain, "point": point, "object": obj}
    obs += [{"name": "obj", "source": "object_position", "unit": "m", "scale": 1.0, "object": obj},
            {"name": "obj_goal", "source": "object_goal", "unit": "m", "scale": 1.0, "object": obj},
            {"name": "prev_a", "source": "last_action"}]
    contract["observations"] = obs
    # 行動は増分 (relative): 1 ステップ ±max_step [rad] を前回の目標に足す。絶対目標だと探索の乱雑な掃いで
    # 物体を弾いてしまうので、押す・運ぶは増分で滑らかに動かす
    rate = float(contract.get("control", {}).get("policy_rate_hz", 25.0))
    max_step = float(c.get("max_step_rad", 1.5 / rate))   # 既定 1.5 rad/s 相当
    for a in contract.get("actions", []):
        if a.get("target") == "joints" and a.get("mode", "position") == "position":
            a["relative"] = True; a["scale"] = round(max_step, 4); a.pop("offset", None); a["clip"] = [-1.0, 1.0]
            a["_note"] = f"relative: increment of up to {max_step:.3f} rad per step ({rate:g} Hz)"
    region = c.get("region") or {"shape": "box", "center": [0.5, 0.0, 0.0], "size": [0.2, 0.2, 0.0]}
    task.pop("goal_range", None)
    task["type"] = "conditions"
    task["objects"] = [dict(o) for o in spec.get("objects", [])]
    task["conditions"] = [{"kind": "object_in_region", "object": obj, "region": region, "tolerance": tol, "planar": True}]
    if reach:
        task["reach"] = reach
    task["reward"] = {"progress": 5.0, "distance": -0.2, "reached": 1.0, "reach_progress": 2.0, "reach_distance": -0.1, "action_rate": -0.02}
    task["stop_at_goal"] = False
    task["_note"] = f"task.json: object {obj} into {region.get('shape')} region (planar), success within {tol} m; reach shaping via {hand}"
    es.clear()
    es.update({"metric": "success_rate", "threshold": float(spec.get("training", {}).get("success_target", 0.9)), "window": 200, "min_timesteps": 50000})
    ros = contract.setdefault("ros", {})
    ros["goal_topic"] = ros.get("goal_topic") or f"/{ros.get('namespace', 'robot')}/policy/command"
    ros.setdefault("object_topics", {})[obj] = f"/{ros.get('namespace', 'robot')}/objects/{obj}/pose"
