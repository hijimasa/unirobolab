"""ROS 2 side: the runtime policy node (copied into generated packages), sim2sim, the env."""

from importlib import resources


def policy_node_source() -> str:
    """Text of policy_node.py without importing it (it needs rclpy)."""
    return resources.files("unirobolab.ros2").joinpath("policy_node.py").read_text(encoding="utf-8")
