import json
import pathlib

import pytest

from unirobolab import contract as contract_mod
from unirobolab.taskspec import TaskSpecError, estimate_time_s, generate, preset, validate

FIX = pathlib.Path(__file__).parent / "fixtures"


def test_joint_preset_generates_joint_target_contract(tmp_path):
    spec = preset("joint_target", str(FIX / "servo_demo.urdf"), name="servo")
    spec["goal"][0]["tolerance"] = 0.03
    spec["episode"]["time_s"] = 3.0
    assert validate(spec) == []
    c, t = generate(spec, str(tmp_path))
    assert t["task"]["type"] == "joint_target" and t["task"]["episode_steps"] == 75   # 3 s at 25 Hz
    assert t["train"]["early_stop"] == {"metric": "final_abs_err", "threshold": 0.03, "window": 200, "min_timesteps": 50000}
    assert c["_task_spec"]["goal"][0]["type"] == "joints_near"
    p = tmp_path / "c.json"; p.write_text(json.dumps(c))
    contract_mod.load(str(p))   # the generated contract validates


def test_explicit_joint_range_and_per_joint_dict(tmp_path):
    spec = preset("joint_target", str(FIX / "servo_demo.urdf"))
    spec["goal"][0]["range"] = [-0.8, 0.8]
    _, t = generate(spec, str(tmp_path))
    assert t["task"]["goal_range"] == [-0.8, 0.8]
    spec["goal"][0]["range"] = {"ideal_joint": [-1.0, 1.0], "cheap_joint": [-0.5, 0.7]}
    _, t = generate(spec, str(tmp_path))
    assert t["task"]["goal_range"] == [-0.5, 0.7]   # conservative: the common range


def test_base_region_ring_and_box(tmp_path):
    spec = preset("base_target", str(FIX / "diffbot_nosensors.urdf"), name="diffbot", namespace="diffbot")
    c, t = generate(spec, str(tmp_path))
    assert t["task"]["type"] == "base_target" and t["task"]["goal_radius"] == [1.0, 2.5] and t["task"]["reach_radius"] == 0.15
    assert t["train"]["early_stop"]["metric"] == "success_rate" and t["train"]["early_stop"]["threshold"] == 0.9
    spec["goal"][0]["region"] = {"shape": "box", "center": [2.0, 0.0], "size": [1.0, 1.0]}
    _, t = generate(spec, str(tmp_path))
    lo, hi = t["task"]["goal_radius"]
    assert 1.2 < lo < 1.4 and 2.6 < hi < 2.8   # 2 m away, half-diagonal 0.71


def test_mismatched_condition_and_robot_is_rejected(tmp_path):
    spec = preset("base_target", str(FIX / "servo_demo.urdf"))
    with pytest.raises(TaskSpecError):
        generate(spec, str(tmp_path))
    bad = preset("joint_target", str(FIX / "servo_demo.urdf")); bad["goal"] = []
    assert any("goal" in p for p in validate(bad))


def test_estimate_grows_with_tighter_tolerance():
    a = preset("joint_target", "x.urdf"); b = preset("joint_target", "x.urdf"); b["goal"][0]["tolerance"] = 0.025
    ta, why = estimate_time_s(a); tb, _ = estimate_time_s(b)
    assert tb > 2 * ta and "許容" in why


def test_connection_settings_from_spec_land_in_contract(tmp_path):
    spec = preset("joint_target", str(FIX / "servo_demo.urdf"), namespace="arm1")
    spec["robot"].update({"command_mode": "ros2_control_commands", "controller": "pos_ctrl", "estop_topic": "/arm1/stop"})
    spec["goal"][0]["range"] = []   # [] = auto
    c, t = generate(spec, str(tmp_path))
    assert c["ros"]["namespace"] == "arm1" and c["ros"]["controller_name"] == "pos_ctrl" and c["safety"]["estop_topic"] == "/arm1/stop"
    assert t["task"]["goal_range"][1] > 0


def test_robot_info_shape():
    from unirobolab.draft import robot_info
    info = robot_info(str(FIX / "servo_demo.urdf"))
    assert info["fixed_base"] and [j["name"] for j in info["joints"]] == ["ideal_joint", "cheap_joint"]
    assert info["joints"][0]["mode"] == "position" and info["joints"][0]["upper"] > 0


def test_random_start_joints_reach_train_config(tmp_path):
    from unirobolab.taskspec import generate, preset, validate
    from pathlib import Path
    fix = Path(__file__).parent / "fixtures" / "arm2_planar.urdf"
    spec = preset("push_object", str(fix), name="arm2")
    spec["start"]["joints"] = "random"; spec["start"]["joints_fraction"] = 0.6
    assert validate(spec) == []
    _, t = generate(spec, str(tmp_path))
    assert t["task"]["start_joints"] == {"mode": "random", "fraction": 0.6}
    spec["start"]["joints"] = "sometimes"
    assert any("start.joints" in p for p in validate(spec))


def test_randomize_and_history_reach_configs(tmp_path):
    from unirobolab.taskspec import generate, preset, validate
    from pathlib import Path
    fix = Path(__file__).parent / "fixtures" / "arm2_planar.urdf"
    spec = preset("push_object", str(fix), name="arm2")
    spec["training"]["randomize"] = {"object_mass": [0.5, 2.0], "object_friction": [0.2, 1.0], "drive_gain": [0.7, 1.3]}
    spec["training"]["history_length"] = 8
    assert validate(spec) == []
    c, t = generate(spec, str(tmp_path))
    assert t["task"]["randomize"]["object_mass"] == [0.5, 2.0] and c["policy"]["history_length"] == 8
    spec["training"]["randomize"] = {"gravity": [1, 2]}
    assert any("randomize.gravity" in p for p in validate(spec))


def test_estimate_uses_measured_rate_per_task_kind():
    from unirobolab.taskspec import estimate_time_s
    base = {"goal": [{"type": "base_in_region", "tolerance": 0.15}], "training": {"n_envs": 8}}
    joints = {"goal": [{"type": "joints_near", "tolerance": 0.05}], "training": {"n_envs": 8}}
    s_base, why = estimate_time_s(base)
    s_joints, _ = estimate_time_s(joints)
    # 差動二輪は 1 往復の物理が重い: 同じ並列数でサーボより桁で長くかかる
    assert s_base > 10 * 60 and s_joints < 5 * 60
    assert "③" in why


def test_spawn_spacing_covers_travel_and_objects():
    from unirobolab.taskspec import spawn_spacing
    from pathlib import Path
    fix = Path(__file__).parent / "fixtures"
    # 移動基体: 開始から r_max まで動くので、隣とは往復分 + 車体の余裕が要る
    base = {"goal": [{"type": "base_in_region", "region": {"shape": "ring", "r_min": 1.0, "r_max": 2.5}}]}
    assert spawn_spacing(base, str(fix / "diffbot_nosensors.urdf")) >= 2 * 2.5
    # 物体タスク: 目標の枠と物体の置き場所まで含める
    obj = {"goal": [{"type": "object_in_region", "region": {"shape": "box", "center": [0.3, 0.45, 0.0], "size": [0.12, 0.12, 0.0]}}],
           "objects": [{"name": "cube", "size": [0.05, 0.05, 0.05], "start": {"center": [0.55, 0.15, 0.0], "size": [0.06, 0.06, 0.0]}}]}
    arm = spawn_spacing(obj, str(fix / "arm2_planar.urdf"))
    assert arm > spawn_spacing({"goal": [{"type": "joints_near"}]}, str(fix / "arm2_planar.urdf")) - 1e-9
    assert 1.5 < arm < 3.0
