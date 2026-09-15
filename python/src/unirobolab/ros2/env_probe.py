"""Throughput / sanity probe for UnityContractEnv: random actions, prints steps/s.

    python3 -m unirobolab.ros2.env_probe <contract.json> <train.json> [steps]
"""

from __future__ import annotations

import json
import sys
import time

from unirobolab import contract
from unirobolab.ros2.unity_env import UnityContractEnv


def main() -> int:
    c = contract.load(sys.argv[1])
    task = json.load(open(sys.argv[2], encoding="utf-8"))["task"]
    n = int(sys.argv[3]) if len(sys.argv) > 3 else 100
    env = UnityContractEnv(c, task)
    t0 = time.monotonic()
    env.reset(seed=0)
    print(f"reset {time.monotonic() - t0:.3f}s")
    t0 = time.monotonic()
    info = {}
    for _ in range(n):
        _, _, _, trunc, info = env.step(env.action_space.sample())
        if trunc:
            env.reset()
    dt = time.monotonic() - t0
    print(f"{n} steps in {dt:.2f}s -> {n / dt:.1f} steps/s, stale steps {env.stale_steps}")
    print(f"last q={info.get('q')} goal={info.get('goal')} |err|={info.get('abs_err')}")
    env.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
