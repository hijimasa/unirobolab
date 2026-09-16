"""Observation/action processing shared by the training side.

This is a verbatim copy of the two functions in ``ros2/policy_node.py``. The node
must stay free of ``unirobolab`` imports (it is shipped inside generated ROS 2
packages), so the copy lives here and ``tests/test_obs_math.py`` checks that
both copies are identical.
"""

from __future__ import annotations

import numpy as np


def process_obs_term(v: np.ndarray, spec: dict) -> np.ndarray:
    """Contract order for observations: deadband -> *scale + offset -> clip."""
    v = np.asarray(v, dtype=np.float32).copy()
    db = float(spec.get("deadband", 0.0))
    if db > 0:
        v[np.abs(v) < db] = 0.0
    v = v * np.asarray(spec.get("scale", 1.0), dtype=np.float32) + np.asarray(spec.get("offset", 0.0), dtype=np.float32)
    clip = spec.get("clip")
    if clip:
        v = np.clip(v, clip[0], clip[1])
    return v


def process_action_term(raw: np.ndarray, spec: dict) -> tuple[np.ndarray, np.ndarray]:
    """Contract order for actions: clip -> *scale + offset. Returns (clipped_raw, target)."""
    a = np.asarray(raw, dtype=np.float32).copy()
    clip = spec.get("clip")
    if clip:
        a = np.clip(a, clip[0], clip[1])
    target = a * np.asarray(spec.get("scale", 1.0), dtype=np.float32) + np.asarray(spec.get("offset", 0.0), dtype=np.float32)
    return a, target
