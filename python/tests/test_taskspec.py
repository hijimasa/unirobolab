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
