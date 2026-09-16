import os

import numpy as np

from unirobolab import contract as contract_mod
from unirobolab.importers.isaaclab import build_contract, load_env_yaml

HERE = os.path.dirname(__file__)
ANYMAL_JOINTS = ["LF_HAA", "LH_HAA", "RF_HAA", "RH_HAA", "LF_HFE", "LH_HFE", "RF_HFE", "RH_HFE",
                 "LF_KFE", "LH_KFE", "RF_KFE", "RH_KFE"]


def _import():
    env = load_env_yaml(os.path.join(HERE, "fixtures", "isaaclab_anymal_flat_env.yaml"))
    return build_contract(env, ANYMAL_JOINTS, "anymal_flat", "policy.onnx", namespace="anymal")


def test_layout_matches_isaaclab():
    raw = _import()
    c = contract_mod.from_dict(raw)
    assert c.policy_rate_hz == 50.0 and c.sim_dt_s == 0.005
    assert [t.name for t in c.observations] == ["base_lin_vel", "base_ang_vel", "projected_gravity",
                                                  "velocity_commands", "joint_pos", "joint_vel", "actions"]
    assert c.obs_dim == 3 + 3 + 3 + 3 + 12 + 12 + 12
    assert c.action_dim == 12
    assert c.history_length == 1


def test_default_offsets_resolved_by_regex():
    raw = _import()
    c = contract_mod.from_dict(raw)
    jp = c.obs_term("joint_pos")
    # joint_pos_rel = q - q_default -> offset = -q_default, in the caller's joint order
    expected = {"LF_HAA": 0.0, "LF_HFE": -0.4, "LH_HFE": 0.4, "LF_KFE": 0.8, "LH_KFE": -0.8}
    for j, v in expected.items():
        assert abs(jp.shift[ANYMAL_JOINTS.index(j)] - v) < 1e-9
    act = c.actions[0]
    assert act.spec["mode"] == "position"
    assert np.allclose(act.scale, 0.5)
    assert abs(act.shift[ANYMAL_JOINTS.index("RH_KFE")] - 0.8) < 1e-9  # use_default_offset


def test_scales_and_command_type():
    raw = _import()
    c = contract_mod.from_dict(raw)
    assert np.allclose(c.obs_term("base_ang_vel").scale, 0.25)
    assert np.allclose(c.obs_term("joint_vel").scale, 0.05)
    cmd = c.obs_term("velocity_commands")
    assert cmd.size == 3 and cmd.spec["ros_type"] == "twist"
