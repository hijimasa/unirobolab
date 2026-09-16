"""Build a policy contract from an Isaac Lab (manager-based) environment config.

Input is the ``params/env.yaml`` that Isaac Lab / rsl_rl write into every run
directory (``logs/rsl_rl/<task>/<run>/params/env.yaml``): a dump of the
``ManagerBasedRLEnvCfg`` with the observation terms in order, their scale / clip /
history, the action term (scale, offset, use_default_offset), the command
manager, the sim dt and decimation, and the articulation's default joint state.

What it cannot know is the joint *order* the policy was trained with (Isaac Sim
orders joints by USD traversal, not by the URDF), so the caller passes it
explicitly (``--joints``). The default joint positions are resolved from the
``init_state.joint_pos`` regex table against that order.

Supported observation funcs: base_lin_vel, base_ang_vel, projected_gravity,
generated_commands (command), joint_pos_rel / joint_pos (joint_position),
joint_vel_rel / joint_vel (joint_velocity), last_action. Anything else (height
scan, images) raises: the contract cannot represent it yet.
"""

from __future__ import annotations

import re
from typing import Any

import yaml

OBS_FUNC_MAP = {
    "base_lin_vel": "base_lin_vel",
    "base_ang_vel": "base_ang_vel",
    "projected_gravity": "projected_gravity",
    "generated_commands": "command",
    "joint_pos_rel": "joint_position",
    "joint_pos": "joint_position",
    "joint_vel_rel": "joint_velocity",
    "joint_vel": "joint_velocity",
    "last_action": "last_action",
}


class ImportError_(ValueError):
    pass


def _func_name(term: dict) -> str:
    f = term.get("func", "")
    if not isinstance(f, str):
        f = str(f)
    # forms seen: "isaaclab.envs.mdp.observations:base_lin_vel", "<function base_lin_vel at 0x...>"
    m = re.search(r"[:\s]([A-Za-z_][A-Za-z0-9_]*)(?: at 0x[0-9a-f]+)?>?$", f)
    return m.group(1) if m else f.rsplit(".", 1)[-1]


def _resolve_regex_table(table: dict[str, float] | float | None, joints: list[str], default: float = 0.0) -> list[float]:
    """Isaac Lab keys many per-joint tables by regex; resolve them for our joint order.
    Later entries override earlier ones, matching Isaac Lab's resolve_matching_names_values."""
    out = [float(default)] * len(joints)
    if table is None:
        return out
    if isinstance(table, (int, float)):
        return [float(table)] * len(joints)
    for pattern, value in table.items():
        rx = re.compile(f"^{pattern}$")
        for i, j in enumerate(joints):
            if rx.match(j):
                out[i] = float(value)
    return out


def _scalar_or_list(values: list[float]) -> float | list[float]:
    return values[0] if all(abs(v - values[0]) < 1e-12 for v in values) else values


def build_contract(env: dict[str, Any], joints: list[str], name: str, onnx: str, urdf: str = "",
                   namespace: str = "", obs_group: str = "policy", asset: str = "robot",
                   action_name: str | None = None) -> dict[str, Any]:
    sim_dt = float(env["sim"]["dt"])
    decimation = int(env.get("decimation", 1))
    rate = 1.0 / (sim_dt * decimation)

    scene = env.get("scene", {})
    robot = scene.get(asset) or {}
    init = (robot.get("init_state") or {})
    default_pos = _resolve_regex_table(init.get("joint_pos"), joints, 0.0)
    default_vel = _resolve_regex_table(init.get("joint_vel"), joints, 0.0)

    # ---- actions (one joint action term)
    actions_cfg = env.get("actions") or {}
    if not actions_cfg:
        raise ImportError_("env.yaml has no actions")
    aname = action_name or next(iter(actions_cfg))
    a = actions_cfg[aname]
    cls = str(a.get("class_type", ""))
    if "JointPosition" in cls:
        mode = "position"
    elif "JointVelocity" in cls:
        mode = "velocity"
    elif "JointEffort" in cls:
        mode = "effort"
    else:
        raise ImportError_(f"unsupported action class {cls!r} (joint position/velocity/effort only)")
    act_joints = _select_joints(a.get("joint_names", [".*"]), joints)
    scale = _resolve_regex_table(a.get("scale", 1.0), act_joints, 1.0)
    offset = _resolve_regex_table(a.get("offset", 0.0), act_joints, 0.0)
    if a.get("use_default_offset", False):
        idx = [joints.index(j) for j in act_joints]
        offset = [default_pos[i] for i in idx] if mode == "position" else [default_vel[i] for i in idx]
    # term names are unique across observations and actions in a contract; Isaac Lab often
    # calls both the action and an observation "joint_pos"
    obs_names = set((env.get("observations") or {}).get(obs_group, {}).keys())
    term_name = aname if aname not in obs_names else f"{aname}_action"
    action_term: dict[str, Any] = {"name": term_name, "target": "joints", "mode": mode,
                                   "unit": "rad" if mode == "position" else ("rad/s" if mode == "velocity" else "N*m"),
                                   "scale": _scalar_or_list(scale), "offset": _scalar_or_list(offset)}
    if act_joints != joints:
        action_term["joints"] = act_joints
    clip = a.get("clip")
    if isinstance(clip, dict) and clip:
        lo = min(v[0] for v in clip.values()); hi = max(v[1] for v in clip.values())
        action_term["clip"] = [float(lo), float(hi)]
    n_act = len(act_joints)

    # ---- observations (one group, in yaml order)
    obs_cfg = (env.get("observations") or {}).get(obs_group)
    if not obs_cfg:
        raise ImportError_(f"no observation group {obs_group!r}")
    if obs_cfg.get("concatenate_terms", True) is False:
        raise ImportError_("observation group does not concatenate terms; the policy input is not a flat vector")
    group_history = int(obs_cfg.get("history_length") or 0)
    histories = set()
    terms = []
    commands_cfg = env.get("commands") or {}
    for tname, t in obs_cfg.items():
        if not isinstance(t, dict) or "func" not in t:
            continue  # group-level settings
        fn = _func_name(t)
        src = OBS_FUNC_MAP.get(fn)
        if src is None:
            raise ImportError_(f"observation {tname!r} uses {fn!r}, which the contract cannot represent")
        term: dict[str, Any] = {"name": tname, "source": src}
        params = t.get("params") or {}
        if src == "command":
            cname = params.get("command_name", "")
            ccls = str((commands_cfg.get(cname) or {}).get("class_type", ""))
            if "Velocity" in ccls:
                term.update({"size": 3, "unit": "unitless", "ros_type": "twist"})
                term["_isaaclab_command"] = f"{cname}: [lin_vel_x, lin_vel_y, ang_vel_z] (heading command not supported)"
            elif "Pose" in ccls:
                term.update({"size": 4, "unit": "unitless"})
                term["_isaaclab_command"] = f"{cname}: pose command (x, y, z, heading)"
            else:
                raise ImportError_(f"command {cname!r} of class {ccls!r} is not supported")
        elif src in ("joint_position", "joint_velocity"):
            asset_cfg = params.get("asset_cfg") or {}
            jn = asset_cfg.get("joint_names") if isinstance(asset_cfg, dict) else None
            sel = _select_joints(jn, joints) if jn else joints
            if sel != joints:
                term["joints"] = sel
            idx = [joints.index(j) for j in sel]
            if fn.endswith("_rel"):
                base = default_pos if src == "joint_position" else default_vel
                term["offset"] = _scalar_or_list([-base[i] for i in idx])
            term["unit"] = "rad" if src == "joint_position" else "rad/s"
        # scale / clip
        sc = t.get("scale")
        if sc is not None:
            if isinstance(sc, (list, tuple)):
                term["scale"] = [float(v) for v in sc]
            else:
                term["scale"] = float(sc)
            if "offset" in term and isinstance(term["offset"], list):
                # Isaac Lab applies scale after the subtraction: (x - x0) * s = x*s - x0*s
                s_list = term["scale"] if isinstance(term["scale"], list) else [term["scale"]] * len(term["offset"])
                term["offset"] = [o * sv for o, sv in zip(term["offset"], s_list)]
        cl = t.get("clip")
        if cl:
            term["clip"] = [float(cl[0]), float(cl[1])]
        h = int(t.get("history_length") or 0)
        histories.add(h)
        terms.append(term)
    if len(histories) > 1:
        raise ImportError_(f"terms use different history lengths {sorted(histories)}; the contract has one global history")
    history = max(histories.pop() if histories else 0, group_history, 1)

    contract = {
        "contract_version": 0,
        "name": name,
        "_imported_from": "isaaclab env.yaml",
        "robot": {"urdf": urdf, "joints": list(joints)},
        "control": {"policy_rate_hz": round(rate, 6), "sim_dt_s": sim_dt, "action_latency_steps": 0},
        "observations": terms,
        "actions": [action_term],
        "policy": {"onnx": onnx, "input_name": "obs", "output_name": "actions", "history_length": history},
    }
    if namespace:
        contract["ros"] = {"namespace": namespace, "command_mode": "joint_state_topic"}
    return contract


def _select_joints(patterns: list[str] | None, joints: list[str]) -> list[str]:
    if not patterns:
        return list(joints)
    out = []
    for j in joints:
        if any(re.match(f"^{p}$", j) for p in patterns):
            out.append(j)
    if not out:
        raise ImportError_(f"joint patterns {patterns} match none of {joints}")
    return out


def load_env_yaml(path: str) -> dict[str, Any]:
    with open(path, encoding="utf-8") as f:
        return yaml.safe_load(f)
