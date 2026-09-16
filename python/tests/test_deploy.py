import copy
import json
import pathlib

import pytest

from unirobolab.deploy import deploy_guide, deploy_summary, ros_set

EX = pathlib.Path(__file__).parents[2] / "contract" / "examples"


def test_ros_set_edits_and_clears():
    raw = json.load(open(EX / "servo_demo_rl.json"))
    ros_set(raw, namespace="arm1", command_mode="ros2_control_commands", controller="pos_ctrl", estop_topic="/arm1/stop")
    assert raw["ros"]["namespace"] == "arm1" and raw["ros"]["controller_name"] == "pos_ctrl"
    assert raw["safety"]["estop_topic"] == "/arm1/stop"
    ros_set(raw, controller="", estop_topic="")
    assert raw["ros"]["controller_name"] == "joint_group_position_controller"   # ros2_control needs one
    assert "estop_topic" not in raw["safety"]
    ros_set(raw, command_mode="joint_state_topic", controller="")
    assert "controller_name" not in raw["ros"]
    with pytest.raises(ValueError):
        ros_set(raw, command_mode="bogus")


def test_guide_joint_state_mode_lists_topics_and_limits():
    raw = json.load(open(EX / "servo_demo_rl.json"))
    raw.setdefault("safety", {})["joint_limits"] = {"ideal_joint": [-1.0, 1.0]}
    text = deploy_guide(raw, lang="ja")
    assert "/ServoDemo/joint_states" in text and "/ServoDemo/joint_command" in text and "/ServoDemo/estop" in text
    assert "ideal_joint: [-1, 1]" in text and "ros2 launch servo_demo_rl_policy policy.launch.py ns:=ServoDemo" in text
    assert "非常停止" in text


def test_guide_ros2_control_and_base_task():
    raw = json.load(open(EX / "servo_demo_rl_ros2control.json"))
    text = deploy_guide(raw, package="mypkg", lang="en")
    assert "/ServoDemo/joint_group_position_controller/commands" in text and "ros2 launch mypkg" in text
    base = json.load(open(EX / "diffbot_rl.json"))
    text = deploy_guide(base, lang="en")
    assert "/diffbot/odom" in text and "/diffbot/goal" in text and "/diffbot/joint_command" in text
    assert "12" in text   # max_joint_speed from the example's safety section


def test_summary_is_short_and_names_topics():
    raw = json.load(open(EX / "diffbot_rl.json"))
    text = deploy_summary(raw, lang="ja")
    assert len(text.splitlines()) <= 8 and "/diffbot/joint_states" in text and "/diffbot/estop" in text and "ros2 launch" in text


def test_package_name_is_ros_safe():
    from unirobolab.generator import ros_package_name
    assert ros_package_name("ServoDemo_draft_policy") == "servodemo_draft_policy"
    assert ros_package_name("2 bots-policy") == "p_2_bots_policy"
    raw = json.load(open(EX / "servo_demo_rl.json")); raw["name"] = "ServoDemo_draft"
    assert "servodemo_draft_policy" in deploy_summary(raw, lang="en")
