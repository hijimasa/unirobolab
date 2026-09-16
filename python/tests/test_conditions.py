import json
import pathlib

import numpy as np
import pytest

from unirobolab import contract as contract_mod
from unirobolab.fk import chain_to, fk_point, load_tree
from unirobolab.task import Condition, ConditionTask, make_task
from unirobolab.taskspec import TaskSpecError, generate, preset

FIX = pathlib.Path(__file__).parent / "fixtures"
URDF = str(FIX / "servo_demo.urdf")


def test_fk_servo_tip():
    tree = load_tree(URDF)
    ch = chain_to(tree, "ideal_arm_link")
    assert [s.get("joint") for s in ch] == [None, None, "ideal_joint"]
    tip = [0.0, 0.0, -0.12]
    p0 = fk_point(ch, {"ideal_joint": 0.0}, tip)
    p90 = fk_point(ch, {"ideal_joint": np.pi / 2}, tip)
    assert np.allclose(p0, [0.045, -0.09, 0.13], atol=1e-6)
    assert np.allclose(p90, [0.045, 0.03, 0.25], atol=1e-6)    # rotates about x: y and z swap


def test_condition_task_reward_and_success():
    tree = load_tree(URDF); ch = chain_to(tree, "ideal_arm_link")
    cfg = {"type": "conditions", "episode_steps": 50,
           "conditions": [{"kind": "link_near", "link": "ideal_arm_link", "point": [0, 0, -0.12], "chain": ch,
                           "region": {"shape": "box", "center": [0.045, -0.03, 0.146], "size": [0.01, 0.04, 0.04]}, "tolerance": 0.03}],
           "reward": {"progress": 5.0, "distance": -0.5, "reached": 1.0, "action_rate": -0.05}}
    t = make_task(cfg, 2, ["ideal_joint", "cheap_joint"])
    assert isinstance(t, ConditionTask) and t.conditions[0].kind == "link_near"
    rng = np.random.default_rng(0)
    g = t.conditions[0].sample(rng, np.zeros(2))
    assert np.all(np.abs(g - np.array([0.045, -0.03, 0.146])) <= np.array([0.005, 0.02, 0.02]) + 1e-6)
    e_far = t.conditions[0].error(g, {"ideal_joint": 0.0}, np.zeros(2), np.zeros(3))
    e_near = t.conditions[0].error(g, {"ideal_joint": np.deg2rad(30)}, np.zeros(2), np.zeros(3))
    assert e_near < e_far
    r, contrib, stop = t.reward([e_near], [e_far], np.zeros(2), np.zeros(2), np.zeros(2))
    assert contrib["progress"] > 0 and r > 0 and stop is False
    r2, contrib2, _ = t.reward([0.01], [0.02], np.zeros(2), np.zeros(2), np.zeros(2))
    assert contrib2["reached"] == 1.0


def test_joints_near_condition_matches_legacy_error():
    c = Condition({"kind": "joints_near", "range": [-1, 1], "tolerance": 0.05}, ["a", "b"])
    g = np.array([0.5, -0.5], np.float32)
    assert abs(c.error(g, {}, np.array([0.6, -0.4]), np.zeros(3)) - 0.1) < 1e-6


def test_taskspec_link_target_generates_conditions_contract(tmp_path):
    spec = preset("link_target", URDF, name="servo")
    spec["goal"][0]["link"] = "ideal_arm_link"
    spec["goal"][0]["region"] = {"shape": "box", "center": [0.045, -0.03, 0.146], "size": [0.01, 0.04, 0.04]}
    c, t = generate(spec, str(tmp_path))
    srcs = [o["source"] for o in c["observations"]]
    assert srcs == ["joint_position", "joint_velocity", "link_position", "link_goal", "last_action"]
    assert c["observations"][2]["point"] == [0.0, 0.0, -0.06]     # visual origin of the arm link by default
    assert t["task"]["type"] == "conditions" and t["task"]["conditions"][0]["kind"] == "link_near"
    assert t["train"]["early_stop"]["metric"] == "success_rate"
    p = tmp_path / "c.json"; p.write_text(json.dumps(c))
    loaded = contract_mod.load(str(p))
    assert loaded.obs_dim == 2 + 2 + 3 + 3 + 2
    spec["goal"][0]["link"] = "nope"
    with pytest.raises(TaskSpecError):
        generate(spec, str(tmp_path))
