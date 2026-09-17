import json
import pathlib

from unirobolab.scenario import default_scenario

EX = pathlib.Path(__file__).parents[2] / "contract" / "examples"


def test_joint_scenario_has_three_goals_and_estop():
    c = json.load(open(EX / "servo_demo_rl.json"))
    sc = default_scenario(c, {"goal": [{"type": "joints_near", "tolerance": 0.05, "range": [-1.2, 1.2]}], "episode": {"time_s": 3.0}})
    assert len(sc["goals"]) == 3 and sc["goals"][0] == [0.72, 0.72] and sc["goals"][2] == [0.0, 0.0]
    assert sc["default_tolerance"] == 0.1 and sc["hold_s"] == 4.0 and sc["estop_test"]["at_s"] == 1.0
    assert abs(sc["max_obs_age_s"] - 0.12) < 1e-6   # 3 periods at 25 Hz


def test_base_scenario_points_on_ring():
    c = json.load(open(EX / "diffbot_rl.json"))
    sc = default_scenario(c, {"goal": [{"type": "base_in_region", "tolerance": 0.2, "region": {"r_min": 1.0, "r_max": 3.0}}], "episode": {"time_s": 10.0}})
    assert len(sc["goals"]) == 3 and abs((sc["goals"][0][0] ** 2 + sc["goals"][0][1] ** 2) ** 0.5 - 2.0) < 1e-3
    assert abs(sc["default_tolerance"] - 0.3) < 1e-9 and sc["hold_s"] == 12.0


def test_link_scenario_points_inside_region():
    c = {"control": {"policy_rate_hz": 25}, "observations": [{"source": "link_goal"}], "robot": {"joints": ["a"]}}
    sc = default_scenario(c, {"goal": [{"type": "link_near", "tolerance": 0.03, "region": {"shape": "box", "center": [0.045, -0.03, 0.146], "size": [0.01, 0.04, 0.04]}}], "episode": {"time_s": 3.0}})
    assert len(sc["goals"]) == 3 and sc["goals"][0] == [0.045, -0.03, 0.146] and abs(sc["goals"][1][2] - 0.156) < 1e-6
    assert abs(sc["default_tolerance"] - 0.045) < 1e-9 and sc["hold_s"] == 4.0


def test_default_scenario_object_task():
    from unirobolab.scenario import default_scenario
    contract = {"control": {"policy_rate_hz": 25.0},
                "observations": [{"name": "obj", "source": "object_position", "object": "cube"},
                                 {"name": "og", "source": "object_goal", "object": "cube"}]}
    spec = {"goal": [{"type": "object_in_region", "object": "cube", "tolerance": 0.06,
                      "region": {"shape": "box", "center": [0.42, 0.55, 0.0], "size": [0.12, 0.12, 0.0]}}],
            "episode": {"time_s": 4.0},
            "objects": [{"name": "cube", "shape": "box", "size": [0.05, 0.05, 0.05], "start": {"center": [0.55, 0.15, 0.0]}}]}
    sc = default_scenario(contract, spec)
    assert sc["object"] == "cube" and sc["objects"][0]["name"] == "cube"
    assert len(sc["goals"]) == 3 and sc["goals"][0] == [0.42, 0.55, 0.0]
    assert all(abs(g[0] - 0.42) <= 0.06 + 1e-9 and abs(g[1] - 0.55) <= 0.06 + 1e-9 for g in sc["goals"])
    assert abs(sc["default_tolerance"] - 0.09) < 1e-9 and sc["hold_s"] == 6.0 and sc["estop_test"]
