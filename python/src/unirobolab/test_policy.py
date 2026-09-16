"""Build a wiring-check policy as ONNX.

The policy copies the ``command`` observation term straight into the joint action
term, undoing the action scale/offset so that the published target equals the
commanded goal. It contains no learning; its only purpose is to prove that the
contract, the generated node, ONNX Runtime, the simulator and the evaluator agree
on joint order, units, scaling and rate. A real trained policy replaces the file
without touching anything else.
"""

from __future__ import annotations

import numpy as np

from unirobolab.contract import Contract, ContractError


def build_identity_weights(c: Contract) -> tuple[np.ndarray, np.ndarray]:
    """Return (W [D, A], b [A]) so that obs @ W + b reproduces the goal as raw action."""
    d, a = c.policy_input_dim, c.action_dim
    w = np.zeros((d, a), dtype=np.float32)
    b = np.zeros((a,), dtype=np.float32)

    cmds = c.obs_terms_by_source("command")
    if not cmds:
        raise ContractError("test policy needs an observation with source 'command'")
    cmd = cmds[0]
    joint_actions = [t for t in c.actions if t.source == "joints"]
    if not joint_actions:
        raise ContractError("test policy needs an action with target 'joints'")
    act = joint_actions[0]
    if cmd.size != act.size:
        raise ContractError(
            f"command term {cmd.name!r} has size {cmd.size} but action {act.name!r} has {act.size}")

    # Only the newest history frame matters; it is the last obs_dim block.
    frame_off = c.obs_dim * (c.history_length - 1)
    # The node applies: value -> deadband -> *scale + offset -> clip on observations,
    # and raw -> clip -> *scale + offset on actions. Undo both so target == goal.
    cs, csh, as_, ash = cmd.scale, cmd.shift, act.scale, act.shift
    for i in range(cmd.size):
        row = frame_off + cmd.offset + i
        col = act.offset + i
        # obs value = goal*cmd.scale + cmd.shift  ->  goal = (obs - cmd.shift)/cmd.scale
        # raw action needed = (goal - act.shift)/act.scale
        w[row, col] = 1.0 / (cs[i] * as_[i])
        b[col] = (-csh[i] / cs[i] - ash[i]) / as_[i]
    clip = act.clip
    if clip is not None:
        lo, hi = clip
        print(f"note: action {act.name!r} clips raw output to [{lo}, {hi}] (per-element scale/offset apply after)")
    return w, b


def write_onnx(c: Contract, out_path: str) -> None:
    import onnx
    from onnx import TensorProto, helper, numpy_helper

    w, b = build_identity_weights(c)
    inp = helper.make_tensor_value_info(c.input_name, TensorProto.FLOAT, [1, c.policy_input_dim])
    out = helper.make_tensor_value_info(c.output_name, TensorProto.FLOAT, [1, c.action_dim])
    w_init = numpy_helper.from_array(w, name="W")
    b_init = numpy_helper.from_array(b, name="b")
    nodes = [
        helper.make_node("MatMul", [c.input_name, "W"], ["mm"]),
        helper.make_node("Add", ["mm", "b"], [c.output_name]),
    ]
    graph = helper.make_graph(nodes, f"{c.name}_wiring_check", [inp], [out], [w_init, b_init])
    model = helper.make_model(graph, producer_name="unirobolab",
                              opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 8
    onnx.checker.check_model(model)
    onnx.save(model, out_path)


def self_check(c: Contract, onnx_path: str) -> bool:
    """Run the file through onnxruntime with a random goal; True if it round-trips."""
    try:
        import onnxruntime as ort
    except ImportError:
        print("onnxruntime not installed; skipping self-check")
        return True
    sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
    cmd = c.obs_terms_by_source("command")[0]
    act = [t for t in c.actions if t.source == "joints"][0]
    rng = np.random.default_rng(0)
    goal = rng.uniform(-0.5, 0.5, size=cmd.size).astype(np.float32)
    x = np.zeros((1, c.policy_input_dim), dtype=np.float32)
    frame_off = c.obs_dim * (c.history_length - 1)
    x[0, frame_off + cmd.offset: frame_off + cmd.end] = goal * cmd.scale + cmd.shift
    y = sess.run([c.output_name], {c.input_name: x})[0][0]
    raw = y[act.offset: act.end]
    target = raw * act.scale + act.shift
    ok = np.allclose(target, goal, atol=1e-5)
    print(f"self-check goal={goal} -> target={target} {'OK' if ok else 'MISMATCH'}")
    return bool(ok)
