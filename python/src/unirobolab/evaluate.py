"""学習済み方策 (ONNX) を学習環境で評価する: 成功率と最終誤差。学習時と違う条件 (物理のばらつき、
開始姿勢) を task の上書きで与えて、ずれに対する強さを比べる。"""
from __future__ import annotations

import json
from typing import Any

import numpy as np

from unirobolab.contract import Contract
from unirobolab.obs_math import process_action_term


def run(c: Contract, train_cfg: dict[str, Any], onnx_path: str, rounds: int, port: int, entities: list[str],
        spawn_urdf: str | None, task_override: dict[str, Any] | None = None, seed: int = 12345,
        spawn_spacing: float = 1.5, spawn_layout: str = "grid") -> dict[str, Any]:
    import onnxruntime as ort
    from unirobolab.direct.vec_env import DirectVecEnv
    task = dict(train_cfg["task"])
    for k, v in (task_override or {}).items():
        if v is None:
            task.pop(k, None)
        else:
            task[k] = v
    env = DirectVecEnv(c, task, entities, port=port, seed=seed, spawn_urdf=spawn_urdf,
                       spawn_spacing=spawn_spacing, spawn_layout=spawn_layout)
    sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
    n = env.num_envs
    episodes: list[dict] = []
    obs = env.reset()
    done_count = 0
    while done_count < rounds * n:
        # the exported actor has batch size 1: run it per entity
        y = np.stack([sess.run([c.output_name], {c.input_name: obs[i:i + 1].astype(np.float32)})[0].reshape(-1) for i in range(n)])
        obs, _, dones, infos = env.step(y.astype(np.float32))
        for i in range(n):
            if dones[i]:
                episodes.append({"success": bool(infos[i].get("success")), "final_abs_err": float(infos[i]["abs_err"]),
                                 "dynamics": infos[i].get("dynamics")})
                done_count += 1
    env.close()
    errs = np.array([e["final_abs_err"] for e in episodes])
    return {"episodes": len(episodes), "success_rate": float(np.mean([e["success"] for e in episodes])),
            "final_abs_err_mean": float(errs.mean()), "final_abs_err_median": float(np.median(errs)),
            "task": {k: task.get(k) for k in ("start_joints", "randomize")}, "per_episode": episodes}
