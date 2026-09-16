"""unirobolab command line."""

from __future__ import annotations

import argparse
import os
import sys

from unirobolab import contract as contract_mod


def _load(path: str, schema: str | None):
    c = contract_mod.load(path)
    if schema:
        try:
            contract_mod.validate_against_schema(c.raw, schema)
        except ImportError:
            print("note: jsonschema not installed, skipping schema validation", file=sys.stderr)
    return c


def _default_schema(contract_path: str) -> str | None:
    """Find contract/policy_contract.schema.json relative to the repo, if present."""
    here = os.path.dirname(os.path.abspath(contract_path))
    for base in (here, os.path.dirname(here), os.path.dirname(os.path.dirname(here))):
        p = os.path.join(base, "policy_contract.schema.json")
        if os.path.isfile(p):
            return p
        p = os.path.join(base, "contract", "policy_contract.schema.json")
        if os.path.isfile(p):
            return p
    return None


def cmd_show(a) -> int:
    c = _load(a.contract, a.schema)
    print(contract_mod.describe(c))
    return 0


def cmd_make_test_policy(a) -> int:
    from unirobolab import test_policy
    c = _load(a.contract, a.schema)
    out = a.out or c.onnx_path
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    test_policy.write_onnx(c, out)
    print(f"wrote {out}")
    return 0 if test_policy.self_check(c, out) else 1


def cmd_gen(a) -> int:
    from unirobolab import generator
    c = _load(a.contract, a.schema)
    root = generator.generate(c, a.out, package_name=a.package, onnx_path=a.onnx, overwrite=a.overwrite)
    print(f"generated {root}")
    return 0


def cmd_sim2sim(a) -> int:
    from unirobolab.ros2 import sim2sim
    c = _load(a.contract, a.schema)
    if a.scenario:
        sc = sim2sim.Scenario.load(a.scenario, c)
    else:
        sc = sim2sim.Scenario.default(c, a.amplitude, a.hold, a.tolerance)
    return sim2sim.run(c, sc, a.pkg, a.ns, a.out, launch_timeout_s=a.launch_timeout)


def cmd_import_isaaclab(a) -> int:
    import json as _json
    from unirobolab.importers import isaaclab
    env = isaaclab.load_env_yaml(a.env_yaml)
    joints = [j.strip() for j in a.joints.split(",")] if "," in a.joints or not os.path.isfile(a.joints) \
        else [line.strip() for line in open(a.joints, encoding="utf-8") if line.strip()]
    try:
        raw = isaaclab.build_contract(env, joints, a.name, a.onnx, urdf=a.urdf, namespace=a.namespace,
                                      obs_group=a.obs_group, asset=a.asset, action_name=a.action)
    except isaaclab.ImportError_ as e:
        print(f"cannot import: {e}", file=sys.stderr)
        return 2
    contract_mod.from_dict(raw, a.out)  # layout check
    with open(a.out, "w", encoding="utf-8") as f:
        _json.dump(raw, f, indent=2, ensure_ascii=False); f.write("\n")
    print(f"wrote {a.out}")
    print(contract_mod.describe(contract_mod.load(a.out)))
    return 0


def cmd_draft(a) -> int:
    import json as _json
    from unirobolab import draft as draft_mod
    try:
        contract, train = draft_mod.draft(a.urdf, name=a.name, namespace=a.namespace, onnx=a.onnx)
    except draft_mod.DraftError as e:
        print(f"cannot draft: {e}", file=sys.stderr)
        return 2
    contract_mod.from_dict(contract, a.out)  # layout check
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        _json.dump(contract, f, indent=2, ensure_ascii=False); f.write("\n")
    train_path = a.train_out or os.path.splitext(a.out)[0] + ".train.json"
    with open(train_path, "w", encoding="utf-8") as f:
        _json.dump(train, f, indent=2, ensure_ascii=False); f.write("\n")
    print(f"wrote {a.out} and {train_path}")
    for n in contract["_notes"]:
        print("  " + n)
    print(contract_mod.describe(contract_mod.load(a.out)))
    return 0


def cmd_task_set(a) -> int:
    """Update the light-user fields of a task/train config in physical units."""
    import json as _json
    c = _load(a.contract, a.schema)
    with open(a.train, encoding="utf-8") as f:
        d = _json.load(f)
    task = d.setdefault("task", {})
    ttype = task.get("type", "joint_target")
    if a.time is not None:
        task["episode_steps"] = max(1, int(round(a.time * c.policy_rate_hz)))
    train = d.setdefault("train", {})
    es = train.setdefault("early_stop", {})
    if ttype == "joint_target":
        if a.goal_range is not None:
            task["goal_range"] = [-abs(a.goal_range), abs(a.goal_range)]
        if a.tolerance is not None:
            es.update({"metric": "final_abs_err", "threshold": a.tolerance})
            es.setdefault("window", 200); es.setdefault("min_timesteps", 50000)
    else:
        if a.goal_min is not None or a.goal_max is not None:
            lo, hi = task.get("goal_radius", [1.0, 2.5])
            task["goal_radius"] = [a.goal_min if a.goal_min is not None else lo, a.goal_max if a.goal_max is not None else hi]
        if a.tolerance is not None:
            task["reach_radius"] = a.tolerance
            es.update({"metric": "success_rate", "threshold": es.get("threshold", 0.9)})
            es.setdefault("window", 200); es.setdefault("min_timesteps", 40000)
    with open(a.train, "w", encoding="utf-8") as f:
        _json.dump(d, f, indent=2, ensure_ascii=False); f.write("\n")
    print(f"updated {a.train}: type={ttype} episode_steps={task.get('episode_steps')} "
          + (f"goal_range={task.get('goal_range')}" if ttype == "joint_target" else f"goal_radius={task.get('goal_radius')} reach_radius={task.get('reach_radius')}")
          + f" early_stop={es}")
    return 0


def cmd_explain_report(a) -> int:
    import json as _json
    from .explain import explain, format_text
    ex = explain(_json.load(open(a.report)), a.lang)
    print(_json.dumps(ex, ensure_ascii=False) if a.json else format_text(ex, a.lang))
    return 0 if ex["pass"] else 1


def cmd_train_status(a) -> int:
    """status.json を平易な 1〜2 行にする (GUI とターミナル用)。"""
    import json as _json
    from .status import format_text
    st = _json.load(open(a.status))
    print(_json.dumps(st, ensure_ascii=False) if a.json else format_text(st, a.lang))
    return 0


def cmd_ros_set(a) -> int:
    import json as _json
    from .deploy import ros_set
    raw = _json.load(open(a.contract))
    ros_set(raw, a.namespace, a.command_mode, a.controller, a.joint_states_topic, a.command_topic, a.estop_topic)
    tmp = a.contract + ".tmp"
    with open(tmp, "w") as f:
        _json.dump(raw, f, indent=2, ensure_ascii=False); f.write("\n")
    try:
        _load(tmp, a.schema)      # validate before replacing the contract
    except Exception:
        os.remove(tmp)
        raise
    os.replace(tmp, a.contract)
    print(f"updated {a.contract}: ros={_json.dumps(raw.get('ros'), ensure_ascii=False)} estop={raw.get('safety', {}).get('estop_topic')}")
    return 0


def cmd_deploy_guide(a) -> int:
    import json as _json
    from .deploy import deploy_guide, deploy_summary
    raw = _json.load(open(a.contract))
    text = deploy_summary(raw, a.package, a.lang) if a.summary else deploy_guide(raw, a.package, a.lang)
    if a.out:
        os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
        with open(a.out, "w") as f:
            f.write(text)
        print(f"wrote {a.out}")
    else:
        print(text)
    return 0


def cmd_task_gen(a) -> int:
    """task.json (開始条件と終了条件) → contract.json + train.json。"""
    import json as _json
    from .taskspec import estimate_time_s, generate
    spec = _json.load(open(a.spec))
    if a.estimate_only:
        secs, why = estimate_time_s(spec)
        print(_json.dumps({"estimate_s": round(secs), "estimate_why": why}, ensure_ascii=False))
        return 0
    contract, train = generate(spec, os.path.dirname(os.path.abspath(a.spec)))
    out = a.out or os.path.dirname(os.path.abspath(a.spec))
    os.makedirs(out, exist_ok=True)
    stem = a.name or contract["name"]
    cpath, tpath = os.path.join(out, f"{stem}.json"), os.path.join(out, f"{stem}.train.json")
    with open(cpath, "w") as f:
        _json.dump(contract, f, indent=2, ensure_ascii=False); f.write("\n")
    with open(tpath, "w") as f:
        _json.dump(train, f, indent=2, ensure_ascii=False); f.write("\n")
    _load(cpath, a.schema)
    secs, why = estimate_time_s(spec)
    print(_json.dumps({"contract": cpath, "train": tpath, "type": train["task"]["type"],
                       "episode_steps": train["task"]["episode_steps"], "estimate_s": round(secs), "estimate_why": why},
                      ensure_ascii=False))
    return 0


def cmd_task_preset(a) -> int:
    import json as _json
    from .taskspec import preset
    # URDF は仕様ファイルからの相対パスで持つ (プロジェクトのフォルダごと動かせるように)
    urdf = os.path.relpath(os.path.abspath(a.urdf), os.path.dirname(os.path.abspath(a.out)))
    spec = preset(a.kind, urdf, a.name, a.namespace)
    with open(a.out, "w") as f:
        _json.dump(spec, f, indent=2, ensure_ascii=False); f.write("\n")
    print(f"wrote {a.out}")
    return 0


def cmd_scenario_default(a) -> int:
    import json as _json
    from .scenario import default_scenario
    spec = _json.load(open(a.task)) if a.task else None
    sc = default_scenario(_json.load(open(a.contract)), spec)
    with open(a.out, "w") as f:
        _json.dump(sc, f, indent=2); f.write("\n")
    print(f"wrote {a.out}: {sc['_note']}")
    return 0


def cmd_task_show(a) -> int:
    """Print the light-user fields of a task config (for the GUI to read back)."""
    import json as _json
    c = _load(a.contract, a.schema)
    with open(a.train, encoding="utf-8") as f:
        d = _json.load(f)
    task = d.get("task", {}); es = d.get("train", {}).get("early_stop", {})
    ttype = task.get("type", "joint_target")
    steps = task.get("episode_steps", 100)
    out = {"type": ttype, "time_s": round(steps / c.policy_rate_hz, 2), "rate_hz": c.policy_rate_hz}
    # 学習結果の置き場所: 契約が宣言する policy.onnx (契約ファイルからの相対) — GUI はこの隣に出力する
    onnx = c.raw.get("policy", {}).get("onnx")
    if onnx:
        out["onnx"] = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(a.contract)), onnx))
    if ttype == "joint_target":
        gr = task.get("goal_range", [-0.5, 0.5]); out["goal_range"] = max(abs(gr[0]), abs(gr[1]))
        out["tolerance"] = es.get("threshold", 0.05)
    else:
        out["goal_min"], out["goal_max"] = task.get("goal_radius", [1.0, 2.5])
        out["tolerance"] = task.get("reach_radius", 0.15)
    print(_json.dumps(out))
    return 0


def cmd_live(a) -> int:
    from unirobolab.direct import live
    c = _load(a.contract, a.schema)
    goal = [float(v) for v in a.goal.split(",")] if a.goal else None
    entity = a.entity or (c.ros.namespace if c.ros else "robot")
    return live.run(c, entity, a.onnx or c.onnx_path, a.host, a.port, goal, a.duration, a.log, play=not a.no_play)


def cmd_train(a) -> int:
    from unirobolab import train
    ents = a.entities.split(",") if a.entities else None
    return train.run(a.contract, a.config, a.out, a.backend, transport=a.transport,
                     host=a.host, port=a.port, n_envs=a.n_envs, entities=ents, instances=a.instances,
                     spawn_urdf=a.spawn_urdf, spawn_spacing=a.spawn_spacing, spawn_yaw=a.spawn_yaw, spawn_layout=a.spawn_layout, spawn_origin=tuple(a.spawn_origin))


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="unirobolab")
    sub = p.add_subparsers(dest="cmd", required=True)

    def add_contract(sp):
        sp.add_argument("contract", help="policy contract JSON")
        sp.add_argument("--schema", help="JSON schema to validate against (auto-detected if omitted)")

    s = sub.add_parser("show", help="print the contract layout"); add_contract(s); s.set_defaults(fn=cmd_show)

    s = sub.add_parser("make-test-policy", help="write a wiring-check ONNX (goal -> target)")
    add_contract(s)
    s.add_argument("--out", help="output .onnx (default: contract's policy.onnx path)")
    s.set_defaults(fn=cmd_make_test_policy)

    s = sub.add_parser("gen", help="generate a ROS 2 package for the contract")
    add_contract(s)
    s.add_argument("--out", required=True, help="directory to create the package in")
    s.add_argument("--package", help="package name (default <name>_policy)")
    s.add_argument("--onnx", help="ONNX file (default: contract's policy.onnx)")
    s.add_argument("--overwrite", action="store_true")
    s.set_defaults(fn=cmd_gen)

    s = sub.add_parser("sim2sim", help="run the policy against the simulator and judge (needs ROS 2)")
    add_contract(s)
    s.add_argument("--pkg", help="generated package to launch; omit if the node is already running")
    s.add_argument("--ns", help="override the robot namespace")
    s.add_argument("--scenario", help="scenario JSON (goals, hold_s, tolerance)")
    s.add_argument("--amplitude", type=float, default=0.5, help="default scenario: step goal [unit of the command term]")
    s.add_argument("--hold", type=float, default=4.0, help="default scenario: hold time per goal [s]")
    s.add_argument("--tolerance", type=float, default=0.1, help="default scenario: |q-goal| tolerance")
    s.add_argument("--launch-timeout", type=float, default=15.0)
    s.add_argument("--out", default="sim2sim_out", help="report directory")
    s.set_defaults(fn=cmd_sim2sim)

    s = sub.add_parser("import-isaaclab", help="build a contract from an Isaac Lab run's params/env.yaml")
    s.add_argument("env_yaml")
    s.add_argument("--joints", required=True, help="joint order the policy was trained with: comma list or a file with one name per line")
    s.add_argument("--onnx", required=True, help="exported policy (path relative to the contract file)")
    s.add_argument("--out", required=True)
    s.add_argument("--name", default="imported")
    s.add_argument("--urdf", default="")
    s.add_argument("--namespace", default="")
    s.add_argument("--obs-group", default="policy")
    s.add_argument("--asset", default="robot")
    s.add_argument("--action", help="action term name (default: the first)")
    s.set_defaults(fn=cmd_import_isaaclab)

    s = sub.add_parser("draft-contract", help="draft a contract and a task config from a robot's URDF")
    s.add_argument("urdf")
    s.add_argument("--out", required=True, help="contract JSON to write (task config goes next to it as *.train.json)")
    s.add_argument("--train-out", help="task/train config path (default <out>.train.json)")
    s.add_argument("--name"); s.add_argument("--namespace", help="ROS namespace (default: from the ros2_control topics)")
    s.add_argument("--onnx", help="policy path to record in the contract")
    s.set_defaults(fn=cmd_draft)

    s = sub.add_parser("task-set", help="set the light-user fields of a task config (goal range, tolerance, time)")
    add_contract(s)
    s.add_argument("--train", required=True, help="task/train JSON to update")
    s.add_argument("--goal-range", type=float, help="joint targets: +- range [rad]")
    s.add_argument("--goal-min", type=float, help="base targets: nearest goal distance [m]")
    s.add_argument("--goal-max", type=float, help="base targets: farthest goal distance [m]")
    s.add_argument("--tolerance", type=float, help="success tolerance [rad or m]")
    s.add_argument("--time", type=float, help="time limit per attempt [s]")
    s.set_defaults(fn=cmd_task_set)

    s = sub.add_parser("explain-report", help="plain-language checklist + next steps from a sim2sim report.json")
    s.add_argument("report")
    s.add_argument("--lang", default="ja", choices=["ja", "en"])
    s.add_argument("--json", action="store_true")
    s.set_defaults(fn=cmd_explain_report)

    s = sub.add_parser("train-status", help="plain-language training progress from a run's status.json")
    s.add_argument("status")
    s.add_argument("--lang", default="ja", choices=["ja", "en"])
    s.add_argument("--json", action="store_true")
    s.set_defaults(fn=cmd_train_status)

    s = sub.add_parser("ros-set", help="edit the contract's ros section (namespace, command mode, controller, topics, e-stop)")
    add_contract(s)
    s.add_argument("--namespace"); s.add_argument("--command-mode", choices=["joint_state_topic", "ros2_control_commands"])
    s.add_argument("--controller"); s.add_argument("--joint-states-topic"); s.add_argument("--command-topic")
    s.add_argument("--estop-topic")
    s.set_defaults(fn=cmd_ros_set)

    s = sub.add_parser("deploy-guide", help="plain-language procedure for the real robot (Markdown)")
    s.add_argument("contract")
    s.add_argument("--package", help="generated package name (default <name>_policy)")
    s.add_argument("--lang", default="ja", choices=["ja", "en"])
    s.add_argument("--out", help="write to this file instead of stdout")
    s.add_argument("--summary", action="store_true", help="short plain-text summary for the panel instead of the Markdown guide")
    s.set_defaults(fn=cmd_deploy_guide)

    s = sub.add_parser("task-gen", help="task.json (start/goal conditions) -> contract.json + train.json")
    s.add_argument("spec")
    s.add_argument("--out", help="output directory (default: next to the spec)")
    s.add_argument("--name", help="file stem for the contract (default: contract name)")
    s.add_argument("--schema")
    s.add_argument("--estimate-only", action="store_true", help="print the training-time estimate only")
    s.set_defaults(fn=cmd_task_gen)

    s = sub.add_parser("robot-info", help="joints, limits, base type of a URDF as JSON (for the GUI)")
    s.add_argument("urdf")
    s.set_defaults(fn=lambda a: (print(__import__("json").dumps(__import__("unirobolab.draft", fromlist=["robot_info"]).robot_info(a.urdf))), 0)[1])

    s = sub.add_parser("task-preset", help="write an example task.json (joint_target | base_target) for a URDF")
    s.add_argument("kind", choices=["joint_target", "base_target"])
    s.add_argument("--urdf", required=True)
    s.add_argument("--out", required=True)
    s.add_argument("--name"); s.add_argument("--namespace")
    s.set_defaults(fn=cmd_task_preset)

    s = sub.add_parser("scenario-default", help="write the default sim2sim scenario (goals + e-stop test) for a contract")
    s.add_argument("contract"); s.add_argument("--task", help="task.json to derive goals and tolerance from"); s.add_argument("--out", required=True)
    s.set_defaults(fn=cmd_scenario_default)

    s = sub.add_parser("task-show", help="print the light-user fields of a task config as JSON")
    add_contract(s)
    s.add_argument("--train", required=True)
    s.set_defaults(fn=cmd_task_show)

    s = sub.add_parser("live", help="run a policy in real time through the simulator's learning server (no ROS)")
    add_contract(s)
    s.add_argument("--onnx", help="policy file (default: the contract's)")
    s.add_argument("--entity", help="entity name (default: ros.namespace)")
    s.add_argument("--goal", help="comma-separated initial goal; later: 'goal v1 v2 ...' lines on stdin")
    s.add_argument("--duration", type=float, help="seconds to run (default: until 'stop' on stdin)")
    s.add_argument("--log", help="JSONL of every observation/action")
    s.add_argument("--host", default="127.0.0.1")
    s.add_argument("--port", type=int, default=10100)
    s.add_argument("--no-play", action="store_true", help="do not switch the simulation to PLAYING")
    s.set_defaults(fn=cmd_live)

    s = sub.add_parser("train", help="train a policy for the contract (needs ROS 2 + the simulator)")
    add_contract(s)
    s.add_argument("--config", required=True, help="task/train JSON (goal range, reward terms, PPO settings)")
    s.add_argument("--out", required=True, help="run directory (policy.onnx, progress.csv, ...)")
    s.add_argument("--backend", default="unity", choices=["unity"])
    s.add_argument("--transport", default="direct", choices=["direct", "ros"],
                   help="direct: the simulator's learning server (SIM_LEARNING_PORT); ros: services/topics")
    s.add_argument("--host", default="127.0.0.1")
    s.add_argument("--port", type=int, default=10100, help="learning server port (direct)")
    s.add_argument("--n-envs", type=int, default=1, help="direct: entities <ns>_0..<n-1> in one scene")
    s.add_argument("--entities", help="direct: explicit comma-separated entity names")
    s.add_argument("--instances", type=int, default=1,
                   help="direct: simulator processes on ports port..port+K-1, each with --n-envs robots")
    s.add_argument("--spawn-urdf", help="direct: spawn missing entities from this URDF (path as seen by the simulator)")
    s.add_argument("--spawn-spacing", type=float, default=1.0, help="metres between spawned robots (along y)")
    s.add_argument("--spawn-yaw", type=float, default=0.0)
    s.add_argument("--spawn-layout", choices=["line", "grid"], default="line", help="how --n-envs robots are placed (grid = ceil(sqrt(n)) columns)")
    s.add_argument("--spawn-origin", type=float, nargs=2, default=[0.0, 0.0], metavar=("X", "Y"), help="ROS x y of the first robot [m]")
    s.set_defaults(fn=cmd_train)

    a = p.parse_args(argv)
    if hasattr(a, "contract") and getattr(a, "schema", None) is None:
        a.schema = _default_schema(a.contract)
    try:
        return a.fn(a)
    except contract_mod.ContractError as e:
        print(f"contract error: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
