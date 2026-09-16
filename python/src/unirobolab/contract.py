"""Policy contract: the single source of truth for how a policy observes and acts.

The schema lives in ``contract/policy_contract.schema.json``. This module loads a
contract, fills defaults, and derives the layouts (term sizes and offsets) that the
generated ROS 2 node, the sim2sim evaluator and the training side all share. No
ROS imports here so it can run anywhere.
"""

from __future__ import annotations

import json
import os

import numpy as np
from dataclasses import dataclass, field
from typing import Any

JOINT_OBS_SOURCES = ("joint_position", "joint_velocity", "joint_effort")
SIZED_OBS_SOURCES = ("command", "custom")
FIXED_SIZE_OBS_SOURCES = {
    "base_lin_vel": 3,
    "base_ang_vel": 3,
    "projected_gravity": 3,
    "imu_orientation": 4,
    "base_goal_xy": 2,
}


class ContractError(ValueError):
    pass


@dataclass
class Term:
    name: str
    size: int
    offset: int  # start index inside the concatenated vector
    spec: dict[str, Any]

    @property
    def end(self) -> int:
        return self.offset + self.size

    @property
    def source(self) -> str:
        return self.spec.get("source", self.spec.get("target", ""))

    @property
    def scale(self) -> np.ndarray:
        """Per-element scale (broadcast from a scalar)."""
        return np.broadcast_to(np.asarray(self.spec.get("scale", 1.0), dtype=np.float32), (self.size,)).copy()

    @property
    def shift(self) -> np.ndarray:
        """Per-element offset (broadcast from a scalar)."""
        return np.broadcast_to(np.asarray(self.spec.get("offset", 0.0), dtype=np.float32), (self.size,)).copy()

    @property
    def clip(self) -> tuple[float, float] | None:
        c = self.spec.get("clip")
        return (float(c[0]), float(c[1])) if c else None

    @property
    def deadband(self) -> float:
        return float(self.spec.get("deadband", 0.0))

    @property
    def joints(self) -> list[str]:
        return list(self.spec.get("joints") or [])


@dataclass
class RosConfig:
    namespace: str
    joint_states_topic: str
    command_topic: str
    command_mode: str  # joint_state_topic | ros2_control_commands
    goal_topic: str
    controller_name: str | None = None
    ground_truth_topic: str | None = None
    imu_topic: str | None = None
    odom_topic: str | None = None
    cmd_vel_topic: str | None = None


@dataclass
class Contract:
    raw: dict[str, Any]
    path: str
    name: str
    joints: list[str]
    urdf: str
    policy_rate_hz: float
    sim_dt_s: float | None
    action_latency_steps: int
    observations: list[Term] = field(default_factory=list)
    actions: list[Term] = field(default_factory=list)
    history_length: int = 1
    onnx: str = ""
    input_name: str = "obs"
    output_name: str = "actions"
    ros: RosConfig | None = None

    # ---- derived -------------------------------------------------------
    @property
    def obs_dim(self) -> int:
        """Size of one observation frame (before history stacking)."""
        return sum(t.size for t in self.observations)

    @property
    def policy_input_dim(self) -> int:
        return self.obs_dim * self.history_length

    @property
    def action_dim(self) -> int:
        return sum(t.size for t in self.actions)

    @property
    def period_s(self) -> float:
        return 1.0 / self.policy_rate_hz

    def obs_term(self, name: str) -> Term:
        for t in self.observations:
            if t.name == name:
                return t
        raise ContractError(f"no observation term named {name!r}")

    def obs_terms_by_source(self, source: str) -> list[Term]:
        return [t for t in self.observations if t.source == source]

    def resolve_path(self, p: str) -> str:
        """Paths inside the contract are relative to the contract file."""
        if os.path.isabs(p):
            return p
        return os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(self.path)), p))

    @property
    def onnx_path(self) -> str:
        return self.resolve_path(self.onnx)


def _term_size(spec: dict[str, Any], joints: list[str], action_dim: int | None) -> int:
    src = spec.get("source")
    if src in JOINT_OBS_SOURCES:
        return len(spec.get("joints") or joints)
    if src == "last_action":
        if action_dim is None:
            raise ContractError("last_action needs the action layout first")
        return action_dim
    if src in FIXED_SIZE_OBS_SOURCES:
        return FIXED_SIZE_OBS_SOURCES[src]
    if src in SIZED_OBS_SOURCES:
        if "size" not in spec:
            raise ContractError(f"observation {spec.get('name')!r} ({src}) needs 'size'")
        return int(spec["size"])
    raise ContractError(f"unknown observation source {src!r}")


def _action_size(spec: dict[str, Any], joints: list[str]) -> int:
    tgt = spec.get("target")
    if tgt == "joints":
        return len(spec.get("joints") or joints)
    if tgt == "base_twist":
        return int(spec.get("size", 2))
    if tgt == "custom":
        if "size" not in spec:
            raise ContractError(f"action {spec.get('name')!r} (custom) needs 'size'")
        return int(spec["size"])
    raise ContractError(f"unknown action target {tgt!r}")


def _layout(specs: list[dict[str, Any]], sizes: list[int]) -> list[Term]:
    out, off = [], 0
    for spec, n in zip(specs, sizes):
        out.append(Term(name=spec["name"], size=n, offset=off, spec=spec))
        off += n
    return out


def load(path: str) -> Contract:
    with open(path, encoding="utf-8") as f:
        raw = json.load(f)
    return from_dict(raw, path)


def from_dict(raw: dict[str, Any], path: str = "<memory>") -> Contract:
    if raw.get("contract_version") != 0:
        raise ContractError("contract_version must be 0")
    robot = raw["robot"]
    joints = list(robot["joints"])
    if len(set(joints)) != len(joints):
        raise ContractError("robot.joints has duplicates")
    ctrl = raw["control"]

    for a in raw["actions"]:
        for j in a.get("joints") or []:
            if j not in joints:
                raise ContractError(f"action {a['name']!r} refers to unknown joint {j!r}")
    for o in raw["observations"]:
        for j in o.get("joints") or []:
            if j not in joints:
                raise ContractError(f"observation {o['name']!r} refers to unknown joint {j!r}")

    action_sizes = [_action_size(a, joints) for a in raw["actions"]]
    actions = _layout(raw["actions"], action_sizes)
    action_dim = sum(action_sizes)
    obs_sizes = [_term_size(o, joints, action_dim) for o in raw["observations"]]
    observations = _layout(raw["observations"], obs_sizes)

    for t in observations + actions:
        for key in ("scale", "offset"):
            v = t.spec.get(key)
            if isinstance(v, list) and len(v) != t.size:
                raise ContractError(f"{t.name}: {key} has {len(v)} values, term has {t.size}")
        if t.source == "command" and t.spec.get("ros_type") == "twist" and t.size != 3:
            raise ContractError(f"{t.name}: ros_type twist needs size 3 ([vx, vy, wz])")
        if t.source == "base_twist" and t.size not in (2, 3):
            raise ContractError(f"{t.name}: base_twist size must be 2 ([vx, wz]) or 3 ([vx, vy, wz])")
    names = [t.name for t in observations] + [t.name for t in actions]
    if len(set(names)) != len(names):
        raise ContractError("observation/action term names must be unique")

    pol = raw["policy"]
    ros = None
    r = raw.get("ros")
    if r:
        ns = r.get("namespace", "")
        prefix = f"/{ns}" if ns else ""
        ros = RosConfig(
            namespace=ns,
            joint_states_topic=r.get("joint_states_topic", f"{prefix}/joint_states"),
            command_topic=r.get("command_topic", f"{prefix}/joint_command"),
            command_mode=r.get("command_mode", "joint_state_topic"),
            goal_topic=r.get("goal_topic", f"{prefix}/policy/command"),
            controller_name=r.get("controller_name"),
            ground_truth_topic=r.get("ground_truth_topic", f"{prefix}/ground_truth"),
            imu_topic=r.get("imu_topic"),
            odom_topic=r.get("odom_topic"),
            cmd_vel_topic=r.get("cmd_vel_topic", f"{prefix}/cmd_vel"),
        )
        if ros.command_mode == "ros2_control_commands" and not ros.controller_name:
            raise ContractError("ros.command_mode=ros2_control_commands needs ros.controller_name")

    return Contract(
        raw=raw,
        path=path,
        name=raw.get("name", "policy"),
        joints=joints,
        urdf=robot["urdf"],
        policy_rate_hz=float(ctrl["policy_rate_hz"]),
        sim_dt_s=float(ctrl["sim_dt_s"]) if "sim_dt_s" in ctrl else None,
        action_latency_steps=int(ctrl.get("action_latency_steps", 0)),
        observations=observations,
        actions=actions,
        history_length=int(pol.get("history_length", 1)),
        onnx=pol["onnx"],
        input_name=pol.get("input_name", "obs"),
        output_name=pol.get("output_name", "actions"),
        ros=ros,
    )


def validate_against_schema(raw: dict[str, Any], schema_path: str) -> None:
    """Optional strict validation; needs the jsonschema package."""
    import jsonschema  # local import: optional dependency

    with open(schema_path, encoding="utf-8") as f:
        schema = json.load(f)
    jsonschema.Draft202012Validator(schema).validate(raw)


def describe(c: Contract) -> str:
    lines = [f"contract {c.name}: {len(c.joints)} joints {c.joints}",
             f"  policy {c.policy_rate_hz:g} Hz, history {c.history_length}, "
             f"input {c.policy_input_dim} -> output {c.action_dim}"]
    lines.append("  observations:")
    for t in c.observations:
        lines.append(f"    [{t.offset:3d}:{t.end:3d}) {t.name:12s} {t.source}")
    lines.append("  actions:")
    for t in c.actions:
        lines.append(f"    [{t.offset:3d}:{t.end:3d}) {t.name:12s} {t.source} "
                     f"mode={t.spec.get('mode')}")
    if c.ros:
        lines.append(f"  ros: ns={c.ros.namespace!r} states={c.ros.joint_states_topic} "
                     f"cmd={c.ros.command_topic} ({c.ros.command_mode}) goal={c.ros.goal_topic}")
    return "\n".join(lines)
