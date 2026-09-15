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


def cmd_train(a) -> int:
    from unirobolab import train
    return train.run(a.contract, a.backend)


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

    s = sub.add_parser("train", help="training backend (not implemented)")
    s.add_argument("contract"); s.add_argument("--backend", default="unity", choices=["unity"])
    s.set_defaults(fn=cmd_train)

    a = p.parse_args(argv)
    if hasattr(a, "contract") and getattr(a, "schema", None) is None and a.cmd != "train":
        a.schema = _default_schema(a.contract)
    try:
        return a.fn(a)
    except contract_mod.ContractError as e:
        print(f"contract error: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
