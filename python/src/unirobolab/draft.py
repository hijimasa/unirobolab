"""Draft a contract (and a task config) from a robot's URDF.

This is the light user's entry point: nothing about observations, scales or
rewards has to be written by hand. The draft reads

  - the movable joints in URDF order, with their limits,
  - the <ros2_control> block: command interfaces (position / velocity / effort)
    per joint and the joint_commands_topic / joint_states_topic /
    ground_truth_topic params (what the simulator and topic_based_ros2_control use),
  - whether the base is fixed (a link named "world" that a fixed joint attaches
    the robot to) or free (mobile robot),

and picks a template: joint targets for a fixed-base robot, a goal on the ground
for a mobile robot. Safety limits come from the URDF limits with a margin. The
output is meant to be edited, but it runs as is.
"""

from __future__ import annotations

import os
import re
import xml.etree.ElementTree as ET
from typing import Any


class DraftError(ValueError):
    pass


def _joint_info(root: ET.Element) -> list[dict[str, Any]]:
    out = []
    for j in root.findall("joint"):
        jt = j.get("type", "fixed")
        if jt in ("fixed", "floating", "planar"):
            continue
        lim = j.find("limit")
        info = {"name": j.get("name"), "type": jt,
                "parent": (j.find("parent").get("link") if j.find("parent") is not None else None),
                "child": (j.find("child").get("link") if j.find("child") is not None else None),
                "lower": float(lim.get("lower")) if lim is not None and lim.get("lower") else None,
                "upper": float(lim.get("upper")) if lim is not None and lim.get("upper") else None,
                "velocity": float(lim.get("velocity")) if lim is not None and lim.get("velocity") else None,
                "effort": float(lim.get("effort")) if lim is not None and lim.get("effort") else None}
        out.append(info)
    return out


def _ros2_control(root: ET.Element) -> tuple[dict[str, str], dict[str, str]]:
    """(joint -> command interface name, hardware params)."""
    modes: dict[str, str] = {}
    params: dict[str, str] = {}
    for rc in root.findall("ros2_control"):
        hw = rc.find("hardware")
        if hw is not None:
            for p in hw.findall("param"):
                params[p.get("name", "")] = (p.text or "").strip()
        for j in rc.findall("joint"):
            cmds = [c.get("name") for c in j.findall("command_interface")]
            if cmds:
                # prefer position > velocity > effort when several are declared
                for pref in ("position", "velocity", "effort"):
                    if pref in cmds:
                        modes[j.get("name")] = pref
                        break
    return modes, params


def _fixed_base(root: ET.Element) -> bool:
    links = {l.get("name") for l in root.findall("link")}
    if "world" not in links:
        return False
    for j in root.findall("joint"):
        p = j.find("parent")
        if j.get("type") == "fixed" and p is not None and p.get("link") == "world":
            return True
    return False


def _namespace_from(params: dict[str, str], robot_name: str) -> str:
    for key in ("joint_states_topic", "joint_commands_topic"):
        t = params.get(key, "")
        m = re.match(r"^/([^/]+)/", t)
        if m:
            return m.group(1)
    return robot_name


def draft(urdf_path: str, name: str | None = None, namespace: str | None = None,
          onnx: str | None = None) -> tuple[dict[str, Any], dict[str, Any]]:
    """Return (contract, task_config) dicts for the robot in urdf_path."""
    tree = ET.parse(urdf_path)
    root = tree.getroot()
    robot_name = root.get("name", "robot")
    joints = _joint_info(root)
    modes, params = _ros2_control(root)
    controlled = [j for j in joints if j["name"] in modes] or joints
    if not controlled:
        raise DraftError("no movable joints in the URDF")
    names = [j["name"] for j in controlled]
    fixed = _fixed_base(root)
    ns = namespace or _namespace_from(params, robot_name)
    name = name or f"{robot_name}_draft"
    mode_set = {modes.get(j["name"], "position") for j in controlled}
    mode = "position" if "position" in mode_set else ("velocity" if "velocity" in mode_set else "effort")
    mixed = len(mode_set) > 1

    # ---- limits -> safety and action scale
    safety: dict[str, Any] = {"_note": "from the URDF limits (90 % of the range); edit for the real robot"}
    joint_limits = {}
    speed = {}
    effort = {}
    for j in controlled:
        if j["lower"] is not None and j["upper"] is not None and j["type"] != "continuous":
            c = 0.5 * (j["lower"] + j["upper"]); h = 0.45 * (j["upper"] - j["lower"])
            joint_limits[j["name"]] = [round(c - h, 4), round(c + h, 4)]
        if j["velocity"]:
            speed[j["name"]] = j["velocity"]
        if j["effort"]:
            effort[j["name"]] = j["effort"]
    if joint_limits:
        safety["joint_limits"] = joint_limits
    if speed:
        safety["max_joint_speed"] = speed
    if effort and mode == "effort":
        safety["max_joint_effort"] = effort
    safety["ramp_in_s"] = 1.0
    safety["stop_action"] = "zero" if mode in ("velocity", "effort") else "hold"

    if mode == "position":
        # action = target angle: scale to the half range so a [-1, 1] output spans the joint
        half = [0.45 * (j["upper"] - j["lower"]) if j["lower"] is not None and j["upper"] is not None else 1.0 for j in controlled]
        centre = [0.5 * (j["lower"] + j["upper"]) if j["lower"] is not None and j["upper"] is not None else 0.0 for j in controlled]
        act_scale: Any = [round(h, 4) for h in half]
        act_offset: Any = [round(c, 4) for c in centre]
        unit = "rad"
    elif mode == "velocity":
        act_scale = [round(min(j["velocity"] or 10.0, 10.0), 4) for j in controlled]
        act_offset = 0.0
        unit = "rad/s"
    else:
        act_scale = [round(j["effort"] or 1.0, 4) for j in controlled]
        act_offset = 0.0
        unit = "N*m"
    if all(abs(v - act_scale[0]) < 1e-9 for v in act_scale):
        act_scale = act_scale[0]
    if isinstance(act_offset, list) and all(abs(v - act_offset[0]) < 1e-9 for v in act_offset):
        act_offset = act_offset[0]

    # ---- template
    if fixed:
        rate = 25
        steps = 2
        observations = [
            {"name": "q", "source": "joint_position", "unit": "rad", "scale": 1.0},
            {"name": "qd", "source": "joint_velocity", "unit": "rad/s", "scale": 0.1, "deadband": 0.02},
            {"name": "goal", "source": "command", "unit": "rad", "size": len(names)},
            {"name": "prev_a", "source": "last_action"},
        ]
        lo = [joint_limits.get(n, [-1.0, 1.0])[0] for n in names]
        hi = [joint_limits.get(n, [-1.0, 1.0])[1] for n in names]
        task = {
            "_note": "joint targets inside 80 % of the safety range; success = mean |q - goal| within 0.05 rad at the end",
            "type": "joint_target",
            "physics_steps_per_action": steps,
            "episode_steps": 50,
            "goal_range": [round(0.8 * min(lo), 3), round(0.8 * max(hi), 3)],
            "reward": {"tracking_l1": -1.0, "action_rate": -0.05},
        }
        early = {"metric": "final_abs_err", "threshold": 0.05, "window": 200, "min_timesteps": 50000}
        total = 400000
    else:
        rate = 10
        steps = 5
        observations = [
            {"name": "v_body", "source": "base_lin_vel", "unit": "m/s", "scale": 1.0},
            {"name": "w_body", "source": "base_ang_vel", "unit": "rad/s", "scale": 0.5},
            {"name": "goal", "source": "base_goal_xy", "unit": "m", "scale": 0.5, "clip": [-3.0, 3.0]},
            {"name": "wheel_w", "source": "joint_velocity", "unit": "rad/s", "scale": 0.1},
            {"name": "prev_a", "source": "last_action"},
        ]
        task = {
            "_note": "reach a point 1-2.5 m away and stay there; success = within 0.15 m at the end of the episode",
            "type": "base_target",
            "physics_steps_per_action": steps,
            "episode_steps": 100,
            "goal_radius": [1.0, 2.5],
            "goal_angle_deg": [-180, 180],
            "reach_radius": 0.15,
            "terminate_on_reach": False,
            "reward": {"base_progress": 5.0, "base_reached": 1.0, "action_rate": -0.02},
            "obs_noise": {"v_body": 0.02, "w_body": 0.05, "goal": 0.02, "wheel_w": 0.2},
        }
        early = {"metric": "success_rate", "threshold": 0.9, "window": 200, "min_timesteps": 40000}
        total = 400000
        safety["max_obs_age_s"] = 0.3

    action = {"name": "cmd", "target": "joints", "mode": mode, "unit": unit, "scale": act_scale,
              "clip": [-1.0, 1.0]}
    if act_offset != 0.0:
        action["offset"] = act_offset
    contract = {
        "contract_version": 0,
        "name": name,
        "_drafted_from": os.path.basename(urdf_path),
        "_notes": [
            f"base: {'fixed (joint targets)' if fixed else 'mobile (goal on the ground)'}",
            f"joints from the URDF in file order; command interfaces: {modes or 'none declared (position assumed)'}",
        ] + (["command interfaces differ between joints; the draft uses one mode for all"] if mixed else []),
        "robot": {"urdf": os.path.basename(urdf_path), "joints": names},
        "control": {"policy_rate_hz": rate, "sim_dt_s": 0.02, "action_latency_steps": 0},
        "observations": observations,
        "actions": [action],
        "policy": {"onnx": onnx or f"runs/{name}/policy.onnx", "input_name": "obs", "output_name": "actions",
                   "history_length": 1},
        "ros": {"namespace": ns, "command_mode": "joint_state_topic",
                **({"ground_truth_topic": params["ground_truth_topic"]} if params.get("ground_truth_topic") else {})},
        "safety": safety,
    }
    train = {"task": task, "train": {"algo": "ppo", "total_timesteps": total, "seed": 0, "n_steps": 50,
                                     "batch_size": 200, "n_epochs": 10, "learning_rate": 0.0003, "gamma": 0.97,
                                     "net_arch": [64, 64], "log_std_init": -1.5, "eval_episodes": 3,
                                     "early_stop": early}}
    return contract, train
