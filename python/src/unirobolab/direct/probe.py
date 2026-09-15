"""Throughput probe for the direct channel: K entities, random actions.

    python3 -m unirobolab.direct.probe <contract.json> <train.json> <entity,entity,...> [steps] [port]
"""

from __future__ import annotations

import json
import sys
import time

import numpy as np

from unirobolab import contract
from unirobolab.direct.vec_env import DirectVecEnv


def main() -> int:
    c = contract.load(sys.argv[1])
    task = json.load(open(sys.argv[2], encoding="utf-8"))["task"]
    entities = sys.argv[3].split(",")
    n = int(sys.argv[4]) if len(sys.argv) > 4 else 200
    port = int(sys.argv[5]) if len(sys.argv) > 5 else 10100
    env = DirectVecEnv(c, task, entities, port=port)
    t0 = time.perf_counter()
    env.reset()
    print(f"reset {time.perf_counter() - t0 :.4f}s for {env.num_envs} envs")
    t0 = time.perf_counter()
    infos = []
    for _ in range(n):
        env.step_async(np.stack([env.action_space.sample() for _ in range(env.num_envs)]))
        _, _, _, infos = env.step_wait()
    dt = time.perf_counter() - t0
    print(f"{n} vec steps x {env.num_envs} envs in {dt:.2f}s -> {n / dt:.1f} rounds/s, "
          f"{n * env.num_envs / dt:.0f} env steps/s (rpc {env.rpc_time / n * 1e3:.2f} ms/round)")
    print(f"env0 q={infos[0]['q']} goal={infos[0]['goal']}")
    env.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
