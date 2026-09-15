"""Entry point used by the Unity binary. Placeholder until the first backend lands."""

from __future__ import annotations

import argparse
import sys


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="unirobolab-train")
    parser.add_argument("contract", help="path to a policy contract JSON")
    parser.add_argument("--backend", default="unity", choices=["unity"])
    args = parser.parse_args(argv)
    print(f"unirobolab-train: backend={args.backend} contract={args.contract} (not implemented)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
