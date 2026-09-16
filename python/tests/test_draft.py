import json
import os

from unirobolab import contract as contract_mod
from unirobolab.draft import draft

HERE = os.path.dirname(__file__)


def test_fixed_base_servo():
    c, t = draft(os.path.join(HERE, "fixtures", "servo_demo.urdf"))
    cc = contract_mod.from_dict(c)
    assert cc.joints == ["ideal_joint", "cheap_joint"]
    assert cc.ros.namespace == "ServoDemo"
    assert cc.actions[0].spec["mode"] == "position"
    assert [o.source for o in cc.observations] == ["joint_position", "joint_velocity", "command", "last_action"]
    assert t["task"]["type"] == "joint_target"
    assert c["safety"]["joint_limits"]["ideal_joint"] == [-1.53, 1.53]
    assert c["safety"]["max_joint_speed"]["cheap_joint"] == 8.0
    json.dumps(c); json.dumps(t)


def test_mobile_diffbot():
    c, t = draft(os.path.join(HERE, "fixtures", "diffbot_nosensors.urdf"))
    cc = contract_mod.from_dict(c)
    assert cc.joints == ["left_wheel_joint", "right_wheel_joint"]
    assert cc.actions[0].spec["mode"] == "velocity"
    assert cc.ros.namespace == "diffbot" and cc.ros.ground_truth_topic == "/diffbot/ground_truth"
    assert "base_goal_xy" in [o.source for o in cc.observations]
    assert t["task"]["type"] == "base_target"
    assert c["safety"]["stop_action"] == "zero"
