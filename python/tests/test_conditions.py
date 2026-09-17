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
    e_far = t.conditions[0].error(g, {"q": np.zeros(2), "q_by_name": {"ideal_joint": 0.0}, "base_pos": np.zeros(3), "objects": {}})
    e_near = t.conditions[0].error(g, {"q": np.zeros(2), "q_by_name": {"ideal_joint": np.deg2rad(30)}, "base_pos": np.zeros(3), "objects": {}})
    assert e_near < e_far
    r, contrib, stop = t.reward([e_near], [e_far], np.zeros(2), np.zeros(2), np.zeros(2))
    assert contrib["progress"] > 0 and r > 0 and stop is False
    r2, contrib2, _ = t.reward([0.01], [0.02], np.zeros(2), np.zeros(2), np.zeros(2))
    assert contrib2["reached"] == 1.0


def test_joints_near_condition_matches_legacy_error():
    c = Condition({"kind": "joints_near", "range": [-1, 1], "tolerance": 0.05}, ["a", "b"])
    g = np.array([0.5, -0.5], np.float32)
    assert abs(c.error(g, {"q": np.array([0.6, -0.4]), "q_by_name": {}, "base_pos": np.zeros(3), "objects": {}}) - 0.1) < 1e-6


def test_taskspec_link_target_generates_conditions_contract(tmp_path):
    spec = preset("link_target", URDF, name="servo")
    spec["goal"][0]["link"] = "ideal_arm_link"
    spec["goal"][0]["region"] = {"shape": "box", "center": [0.045, -0.03, 0.146], "size": [0.01, 0.04, 0.04]}
    c, t = generate(spec, str(tmp_path))
    srcs = [o["source"] for o in c["observations"]]
    assert srcs == ["joint_position", "joint_velocity", "link_position", "link_goal", "last_action"]
    assert c["observations"][2]["point"] == [0.0, 0.0, -0.12]     # far end of the arm's visual by default
    assert t["task"]["type"] == "conditions" and t["task"]["conditions"][0]["kind"] == "link_near"
    assert t["train"]["early_stop"]["metric"] == "success_rate"
    p = tmp_path / "c.json"; p.write_text(json.dumps(c))
    loaded = contract_mod.load(str(p))
    assert loaded.obs_dim == 2 + 2 + 3 + 3 + 2
    spec["goal"][0]["link"] = "nope"
    with pytest.raises(TaskSpecError):
        generate(spec, str(tmp_path))


def test_object_condition_and_reach_shaping():
    tree = load_tree(URDF); ch = chain_to(tree, "ideal_arm_link")
    cfg = {"type": "conditions", "episode_steps": 50,
           "objects": [{"name": "cube", "shape": "box", "size": [0.05, 0.05, 0.05], "start": {"center": [0.3, 0.0, 0.0], "size": [0.1, 0.1, 0.0]}}],
           "conditions": [{"kind": "object_in_region", "object": "cube", "region": {"shape": "box", "center": [0.6, 0.0, 0.0], "size": [0.1, 0.1, 0.0]}, "tolerance": 0.05}],
           "reach": {"link": "ideal_arm_link", "chain": ch, "point": [0, 0, -0.12], "object": "cube"},
           "reward": {"progress": 5.0, "reached": 1.0, "reach_progress": 2.0, "action_rate": -0.05}}
    t = make_task(cfg, 2, ["ideal_joint", "cheap_joint"])
    c = t.conditions[0]
    g = c.sample(np.random.default_rng(1), np.zeros(2))
    assert 0.55 <= g[0] <= 0.65 and abs(g[2]) < 1e-6
    ctx = {"q": np.zeros(2), "q_by_name": {"ideal_joint": 0.0}, "base_pos": np.zeros(3), "objects": {"cube": np.array([0.3, 0.0, 0.02])}}
    e = c.error(g, ctx)
    assert abs(e - np.linalg.norm(g[:2] - np.array([0.3, 0.0]))) < 1e-6       # planar: height ignored
    reach = t.reach_distance(ctx)
    assert reach is not None and reach > 0
    r, contrib, _ = t.reward([e], [e + 0.05], np.zeros(2), np.zeros(2), np.zeros(2), reach, reach + 0.02)
    assert contrib["progress"] > 0 and abs(contrib["reach_progress"] - 0.04) < 1e-6


def test_taskspec_push_object_generates_object_contract(tmp_path):
    spec = preset("push_object", str(FIX / "arm2_planar.urdf"), name="arm2")
    c, t = generate(spec, str(tmp_path))
    srcs = [o["source"] for o in c["observations"]]
    assert srcs == ["joint_position", "joint_velocity", "link_position", "object_position", "object_goal", "last_action"]
    assert c["observations"][2]["link"] == "paddle_link" or c["observations"][2]["link"] == "fore_link"
    assert t["task"]["type"] == "conditions" and t["task"]["objects"][0]["name"] == "cube" and t["task"]["reach"]["object"] == "cube"
    assert c["ros"]["object_topics"]["cube"].endswith("/objects/cube/pose")
    p = tmp_path / "c.json"; p.write_text(json.dumps(c))
    assert contract_mod.load(str(p)).obs_dim == 2 + 2 + 3 + 3 + 3 + 2


def test_push_object_uses_relative_actions_and_integration():
    from unirobolab.obs_math import integrate_relative
    spec = preset("push_object", str(FIX / "arm2_planar.urdf"), name="arm2")
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        c, _ = generate(spec, d)
    a = c["actions"][0]
    assert a["relative"] is True and abs(a["scale"] - 0.06) < 1e-6 and a["clip"] == [-1.0, 1.0]
    t = integrate_relative(np.array([0.0, 2.45]), np.array([0.06, 0.06]), [[-2.5, 2.5], [-2.5, 2.5]])
    assert abs(t[0] - 0.06) < 1e-6 and abs(t[1] - 2.5) < 1e-6
