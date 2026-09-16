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


def cmd_train(a) -> int:
    from unirobolab import train
    ents = a.entities.split(",") if a.entities else None
    return train.run(a.contract, a.config, a.out, a.backend, transport=a.transport,
                     host=a.host, port=a.port, n_envs=a.n_envs, entities=ents, instances=a.instances,
                     spawn_urdf=a.spawn_urdf, spawn_spacing=a.spawn_spacing, spawn_yaw=a.spawn_yaw)


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
