import inspect

from unirobolab import obs_math
from unirobolab.ros2 import policy_node_source


def test_copies_identical():
    """policy_node.py ships without unirobolab; its copy of the math must match."""
    src = policy_node_source()
    for fn in ("process_obs_term", "process_action_term", "fk_point"):
        ours = inspect.getsource(getattr(obs_math, fn))
        assert ours in src, f"{fn} in ros2/policy_node.py differs from obs_math.py"
