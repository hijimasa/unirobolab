"""Throughput probe across K instances (ports port..port+K-1), spawning M robots each.

    python3 -m unirobolab.direct.pool_probe <contract.json> <train.json> <urdf> K M [rounds] [port] [spacing]
"""

from __future__ import annotations

import json
import sys
import time

import numpy as np

from unirobolab import contract
from unirobolab.direct.pool import PoolVecEnv
from unirobolab.direct.vec_env import DirectVecEnv


def main() -> int:
    c = contract.load(sys.argv[1])
    task = json.load(open(sys.argv[2], encoding="utf-8"))["task"]
    urdf = sys.argv[3]
    k, m = int(sys.argv[4]), int(sys.argv[5])
    rounds = int(sys.argv[6]) if len(sys.argv) > 6 else 100
    port = int(sys.argv[7]) if len(sys.argv) > 7 else 10100
    spacing = float(sys.argv[8]) if len(sys.argv) > 8 else 6.0
    ns = c.ros.namespace if c.ros else "robot"
    names = [f"{ns}_{i}" for i in range(m)] if m > 1 else [ns]
    envs = [DirectVecEnv(c, task, names, port=port + i, spawn_urdf=urdf, spawn_spacing=spacing) for i in range(k)]
    env = PoolVecEnv(envs) if k > 1 else envs[0]
    env.reset()
    t0 = time.perf_counter()
    for _ in range(rounds):
        env.step_async(np.stack([env.action_space.sample() for _ in range(env.num_envs)]))
        env.step_wait()
    dt = time.perf_counter() - t0
    print(f"K={k} x M={m}: {rounds} rounds in {dt:.2f}s -> {rounds / dt:.1f} rounds/s, "
          f"{rounds * env.num_envs / dt:.0f} env steps/s ({dt / rounds * 1e3:.1f} ms/round)")
    env.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
