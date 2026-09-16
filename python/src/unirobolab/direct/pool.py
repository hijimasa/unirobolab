"""Several simulator instances stepped in parallel from one trainer.

Each instance is one DirectVecEnv (own port, own robots). Physics inside one Unity
process is single-threaded, so K processes on K cores give close to K times the
throughput of physics-bound robots. The instances are stepped concurrently from a
thread pool; the socket calls release the GIL.
"""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor

import numpy as np
from stable_baselines3.common.vec_env.base_vec_env import VecEnv

from unirobolab.direct.vec_env import DirectVecEnv


class PoolVecEnv(VecEnv):
    def __init__(self, envs: list[DirectVecEnv]) -> None:
        self.envs = envs
        self.pool = ThreadPoolExecutor(max_workers=len(envs))
        n = sum(e.num_envs for e in envs)
        super().__init__(n, envs[0].observation_space, envs[0].action_space)
        self.offsets = np.cumsum([0] + [e.num_envs for e in envs])
        self._actions = None

    @property
    def task(self):
        return self.envs[0].task

    @property
    def rpc_time(self) -> float:
        return max(e.rpc_time for e in self.envs)

    def reset(self) -> np.ndarray:
        parts = list(self.pool.map(lambda e: e.reset(), self.envs))
        return np.concatenate(parts)

    def step_async(self, actions: np.ndarray) -> None:
        self._actions = np.asarray(actions)

    def step_wait(self):
        def run(i):
            e = self.envs[i]
            e.step_async(self._actions[self.offsets[i]:self.offsets[i + 1]])
            return e.step_wait()
        results = list(self.pool.map(run, range(len(self.envs))))
        obs = np.concatenate([r[0] for r in results])
        rew = np.concatenate([r[1] for r in results])
        done = np.concatenate([r[2] for r in results])
        infos = [info for r in results for info in r[3]]
        return obs, rew, done, infos

    def close(self) -> None:
        for e in self.envs:
            e.close()
        self.pool.shutdown()

    def get_attr(self, attr_name, indices=None):
        return [getattr(self, attr_name)] * self.num_envs

    def set_attr(self, attr_name, value, indices=None):
        setattr(self, attr_name, value)

    def env_method(self, method_name, *args, indices=None, **kwargs):
        return [getattr(self, method_name)(*args, **kwargs)] * self.num_envs

    def env_is_wrapped(self, wrapper_class, indices=None):
        return [False] * self.num_envs

    def seed(self, seed=None):
        for i, e in enumerate(self.envs):
            e.seed(None if seed is None else seed + i)
        return [seed] * self.num_envs
