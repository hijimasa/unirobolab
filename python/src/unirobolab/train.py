"""Training backend: PPO (stable-baselines3) on the Unity contract environment.

Outputs in ``--out``:
  policy.onnx        deterministic actor (mean action), input/output names from the contract
  model.zip          the SB3 model
  progress.csv       per-episode return, length, final tracking error and per-term reward sums
  learning_curve.png return and per-term contributions over episodes
  eval.json          deterministic rollouts after training
  train_config.json  the task/train config actually used
"""

from __future__ import annotations

import csv
import json
import os
import time
from typing import Any

import numpy as np

from unirobolab.contract import Contract


DEFAULT_TRAIN = {
    "algo": "ppo",
    "total_timesteps": 20000,
    "seed": 0,
    "n_steps": 400,
    "batch_size": 100,
    "n_epochs": 10,
    "learning_rate": 3e-4,
    "gamma": 0.97,
    "gae_lambda": 0.95,
    "ent_coef": 0.0,
    "net_arch": [64, 64],
    # PPO's Gaussian starts at std = exp(log_std_init). SB3's default 0 (std 1) is far
    # too wide for position targets in radians and wastes most of the budget.
    "log_std_init": -1.5,
    "eval_episodes": 3,
    # Early stopping, checked at the end of every rollout over the last `window` episodes:
    #   metric "success_rate": fraction of episodes ending with info["success"] >= threshold
    #   metric "final_abs_err": mean final |error| <= threshold
    # None disables it; total_timesteps stays the cap.
    "early_stop": None,
}


def load_config(path: str) -> tuple[dict[str, Any], dict[str, Any]]:
    with open(path, encoding="utf-8") as f:
        d = json.load(f)
    task = d.get("task", {})
    train = {**DEFAULT_TRAIN, **d.get("train", {})}
    return task, train


class _EpisodeLogger:
    """SB3 callback: one CSV row per finished episode (any env), with per-term reward sums."""

    def __init__(self, path: str, weights: dict[str, float], n_envs: int,
                 status_path: str | None = None, status_ctx: dict | None = None):
        from stable_baselines3.common.callbacks import BaseCallback

        outer = self
        self.status_path = status_path
        self.status_ctx = status_ctx or {}
        self._status_t = 0.0

        class CB(BaseCallback):
            def __init__(self):
                super().__init__()
                self.ret = np.zeros(n_envs)
                self.len = np.zeros(n_envs, int)
                self.episode = 0
                self.t0 = time.monotonic()

            def _on_step(self) -> bool:
                rewards = self.locals["rewards"]
                infos = self.locals["infos"]
                for i in range(len(infos)):
                    self.ret[i] += float(rewards[i])
                    self.len[i] += 1
                    info = infos[i]
                    if "episode_terms" in info:
                        self.episode += 1
                        row = {"episode": self.episode, "timesteps": self.num_timesteps,
                               "wall_s": round(time.monotonic() - self.t0, 1),
                               "return": round(float(self.ret[i]), 4), "length": int(self.len[i]),
                               "final_abs_err": round(info["abs_err"], 4),
                               "success": int(bool(info.get("success", False)))}
                        for k in weights:
                            row[f"term_{k}"] = round(info["episode_terms"][k], 4)
                        outer.rows.append(row)
                        outer.writer.writerow(row)
                        outer.f.flush()
                        if self.episode % max(10, n_envs * 5) == 0:
                            print(f"ep {self.episode:5d} t={self.num_timesteps:7d} "
                                  f"return={self.ret[i]:8.3f} final|err|={info['abs_err']:.4f} "
                                  f"({row['wall_s']:.0f}s)", flush=True)
                        self.ret[i], self.len[i] = 0.0, 0
                now = time.monotonic()
                if outer.status_path and now - outer._status_t >= 1.0:
                    outer._status_t = now
                    outer.write_status("training", now - self.t0)
                return True

        self.rows: list[dict] = []
        self.f = open(path, "w", newline="")
        self.writer = csv.DictWriter(self.f, fieldnames=["episode", "timesteps", "wall_s", "return", "length",
                                                         "final_abs_err", "success"] + [f"term_{k}" for k in weights])
        self.writer.writeheader()
        self.callback = CB()

    def write_status(self, phase: str, wall_s: float, extra: dict | None = None) -> None:
        """ライトユーザー向けの進捗 (status.json)。GUI が 1 秒ごとに読む。"""
        if not self.status_path:
            return
        from unirobolab.status import train_status
        ctx = self.status_ctx
        st = train_status(self.rows, ctx.get("total_timesteps", 0), ctx.get("n_envs", 1), wall_s,
                          tolerance=ctx.get("tolerance"), window=ctx.get("window", 200),
                          early_stop=ctx.get("early_stop"), phase=phase)
        if ctx.get("extra"):
            st.update(ctx["extra"])
        if extra:
            st.update(extra)
        tmp = self.status_path + ".tmp"
        with open(tmp, "w") as f:
            json.dump(st, f)
        os.replace(tmp, self.status_path)

    def close(self):
        self.f.close()


class _StartCurriculum:
    """開始姿勢の幅のカリキュラム。ロールアウトごとに直近 window 回の成功率を見て、閾値以上なら幅を step 広げる。
    いきなり広い幅で始めると (25 % でも) 学習が立ち上がらないことがあるので、ゼロ姿勢で覚えてから広げる。"""

    def __init__(self, env, logger, fraction_max: float, window: int, success_to_grow: float, step: float):
        from stable_baselines3.common.callbacks import BaseCallback
        outer = self
        self.env = env
        self.fraction = 0.0
        self.fraction_max = fraction_max
        self.window = max(window, 8 * getattr(env, "num_envs", 1))
        self.success_to_grow = success_to_grow
        self.step = step
        env.set_start_fraction(0.0)
        logger.status_ctx["extra"] = {"start_fraction": 0.0, "start_fraction_max": fraction_max}

        class CB(BaseCallback):
            def _on_step(self) -> bool:
                return True

            def _on_rollout_end(self) -> None:
                if outer.fraction >= outer.fraction_max or len(logger.rows) < outer.grown_at + outer.window:
                    return
                recent = logger.rows[-outer.window:]
                if float(np.mean([r.get("success", 0.0) for r in recent])) >= outer.success_to_grow:
                    outer.fraction = min(outer.fraction_max, outer.fraction + outer.step)
                    outer.env.set_start_fraction(outer.fraction)
                    logger.status_ctx["extra"]["start_fraction"] = round(outer.fraction, 3)
                    # 広げた直後の成功率で続けて広げないよう、窓を新しいエピソードで埋め直す
                    outer.grown_at = len(logger.rows)
                    print(f"curriculum: start fraction -> {outer.fraction:.2f} at {self.num_timesteps} steps", flush=True)

        self.grown_at = 0
        self.callback = CB()


class _EarlyStop:
    """SB3 callback: stop once a rolling metric over recent episodes meets the target."""

    def __init__(self, cfg: dict, rows: list[dict], n_envs: int = 1):
        from stable_baselines3.common.callbacks import BaseCallback

        outer = self
        self.cfg = dict(cfg)
        # with many envs a fixed window is only a few episodes per env: widen it so the
        # criterion sees at least `episodes_per_env` (default 8) episodes from every env
        self.cfg["window"] = max(int(cfg.get("window", 200)), int(cfg.get("episodes_per_env", 8)) * n_envs)
        self.rows = rows
        self.triggered_at: int | None = None

        class CB(BaseCallback):
            def _on_step(self) -> bool:
                return True

            def _on_rollout_end(self) -> None:
                if outer.triggered_at is not None:
                    return
                window = int(outer.cfg.get("window", 200))
                if self.num_timesteps < int(outer.cfg.get("min_timesteps", 0)) or len(outer.rows) < window:
                    return
                recent = outer.rows[-window:]
                metric = outer.cfg.get("metric", "success_rate")
                thr = float(outer.cfg["threshold"])
                if metric == "success_rate":
                    value = float(np.mean([r.get("success", 0.0) for r in recent]))
                    ok = value >= thr
                elif metric == "final_abs_err":
                    value = float(np.mean([r["final_abs_err"] for r in recent]))
                    ok = value <= thr
                else:
                    raise ValueError(f"unknown early_stop metric {metric!r}")
                if ok:
                    outer.triggered_at = self.num_timesteps
                    print(f"early stop: {metric}={value:.3f} over last {window} episodes at {self.num_timesteps} steps", flush=True)
                    self.model.env.reset_infos = None  # no-op; keeps mypy quiet
                    raise _StopTraining()

        self.callback = CB()


class _StopTraining(Exception):
    pass


def export_onnx(model, c: Contract, path: str) -> None:
    import torch

    policy = model.policy

    class Actor(torch.nn.Module):
        def __init__(self):
            super().__init__()
            self.p = policy

        def forward(self, obs):
            feats = self.p.extract_features(obs, self.p.pi_features_extractor)
            latent = self.p.mlp_extractor.forward_actor(feats)
            return self.p.action_net(latent)  # mean action = deterministic policy

    actor = Actor().eval()
    dummy = torch.zeros(1, c.policy_input_dim, dtype=torch.float32)
    torch.onnx.export(actor, dummy, path, opset_version=17,
                      input_names=[c.input_name], output_names=[c.output_name], dynamo=False)


def plot_curve(rows: list[dict], weights: dict[str, float], path: str) -> None:
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        return
    ep = [r["episode"] for r in rows]
    fig, ax = plt.subplots(2, 1, figsize=(8, 7), sharex=True)
    ax[0].plot(ep, [r["return"] for r in rows], label="return")
    for k in weights:
        ax[0].plot(ep, [r[f"term_{k}"] for r in rows], alpha=0.7, label=f"term {k}")
    ax[0].legend(); ax[0].set_ylabel("episode sum"); ax[0].grid(alpha=0.3)
    ax[1].plot(ep, [r["final_abs_err"] for r in rows])
    ax[1].set_ylabel("final |q - goal| [rad]"); ax[1].set_xlabel("episode"); ax[1].grid(alpha=0.3)
    fig.tight_layout(); fig.savefig(path, dpi=110); plt.close(fig)


def evaluate(model, env, episodes: int) -> dict:
    """Deterministic rollouts. Works for a single gym env and for a VecEnv (one episode per env)."""
    from stable_baselines3.common.vec_env.base_vec_env import VecEnv
    out = []
    if isinstance(env, VecEnv):
        obs = env.reset()
        n = env.num_envs
        errs = [[] for _ in range(n)]
        goals = [None] * n
        done_mask = np.zeros(n, bool)
        while not done_mask.all():
            a, _ = model.predict(obs, deterministic=True)
            obs, r, dones, infos = env.step(a)
            for i in range(n):
                if done_mask[i]:
                    continue
                errs[i].append(infos[i]["abs_err"])
                goals[i] = infos[i]["goal"]
                if dones[i]:
                    done_mask[i] = True
        for i in range(n):
            out.append({"goal": [float(g) for g in goals[i]], "final_abs_err": errs[i][-1],
                        "settled_abs_err": float(np.mean(errs[i][-10:]))})
    else:
        for _ in range(episodes):
            obs, info = env.reset()
            done = False
            errs = []
            while not done:
                a, _ = model.predict(obs, deterministic=True)
                obs, r, term, trunc, info = env.step(a)
                errs.append(info["abs_err"])
                done = term or trunc
            out.append({"goal": [float(g) for g in info["goal"]], "final_abs_err": errs[-1],
                        "settled_abs_err": float(np.mean(errs[-10:]))})
    return {"episodes": out,
            "mean_final_abs_err": float(np.mean([e["final_abs_err"] for e in out])),
            "mean_settled_abs_err": float(np.mean([e["settled_abs_err"] for e in out]))}


def run(contract_path: str, config_path: str, out_dir: str, backend: str = "unity",
        transport: str = "direct", host: str = "127.0.0.1", port: int = 10100,
        n_envs: int = 1, entities: list[str] | None = None, instances: int = 1,
        spawn_urdf: str | None = None, spawn_spacing: float = 1.0, spawn_yaw: float = 0.0,
        spawn_layout: str = "line", spawn_origin: tuple[float, float] = (0.0, 0.0)) -> int:
    from unirobolab import contract as contract_mod
    c = contract_mod.load(contract_path)
    task, train = load_config(config_path)
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "train_config.json"), "w") as f:
        json.dump({"task": task, "train": train, "contract": contract_path,
                   "transport": transport, "n_envs": n_envs, "entities": entities}, f, indent=2)

    if backend != "unity":
        print(f"backend {backend!r} not implemented"); return 2
    from stable_baselines3 import PPO

    if transport == "direct":
        from unirobolab.direct.vec_env import DirectVecEnv
        ns = c.ros.namespace if c.ros else "robot"
        if not entities:
            entities = [ns] if n_envs == 1 else [f"{ns}_{i}" for i in range(n_envs)]
        envs = [DirectVecEnv(c, task, entities, host=host, port=port + k, seed=train["seed"] + 1000 * k,
                             spawn_urdf=spawn_urdf, spawn_spacing=spawn_spacing, spawn_yaw=spawn_yaw, spawn_layout=spawn_layout, spawn_origin=spawn_origin)
                for k in range(instances)]
        if instances == 1:
            env = envs[0]
        else:
            from unirobolab.direct.pool import PoolVecEnv
            env = PoolVecEnv(envs)
        weights = env.task.weights
        n_envs = env.num_envs
    else:
        from unirobolab.ros2.unity_env import UnityContractEnv
        env = UnityContractEnv(c, task)
        weights = env.reward_weights
        n_envs = 1
    print(f"env ({transport}, {n_envs} envs): obs {env.observation_space.shape} act {env.action_space.shape}, "
          f"{task.get('physics_steps_per_action', 1)} physics steps/action, "
          f"{task.get('episode_steps', 100)} steps/episode, reward {weights}", flush=True)
    # 成功の定義 (status.json 用): 関節目標は early_stop の final_abs_err 閾値、地点到達は reach_radius
    es = train.get("early_stop") or {}
    tolerance = None
    if task.get("type", "joint_target") == "base_target":
        tolerance = float(task.get("reach_radius", 0.15))
    elif task.get("type") == "conditions" and task.get("conditions"):
        tolerance = float(task["conditions"][0].get("tolerance", 0.05))   # 成功 = 全条件が許容内 (info の success が優先)
    elif es.get("metric") == "final_abs_err":
        tolerance = float(es["threshold"])
    logger = _EpisodeLogger(os.path.join(out_dir, "progress.csv"), weights, n_envs,
                            status_path=os.path.join(out_dir, "status.json"),
                            status_ctx={"total_timesteps": int(train["total_timesteps"]), "n_envs": n_envs,
                                        "tolerance": tolerance, "window": int(es.get("window", 200)) if es else 200,
                                        "early_stop": es or None})
    callbacks = [logger.callback]
    curriculum = None
    sj = task.get("start_joints") if isinstance(task, dict) else None
    if sj and sj.get("mode") == "random" and sj.get("curriculum", True) and hasattr(env, "set_start_fraction"):
        # 開始姿勢のカリキュラム: 幅 0 から始め、直近の成功率が閾値を超えるたびに広げる (最終的に設定の fraction まで)
        curriculum = _StartCurriculum(env, logger, float(sj.get("fraction", 0.5)), int(es.get("window", 200)) if es else 200,
                                      float(sj.get("curriculum_success", 0.5)), float(sj.get("curriculum_step", 0.05)))
        callbacks.append(curriculum.callback)
    early = None
    if train.get("early_stop"):
        early = _EarlyStop(train["early_stop"], logger.rows, n_envs)
        callbacks.append(early.callback)
    model = PPO("MlpPolicy", env, seed=train["seed"], n_steps=train["n_steps"], batch_size=train["batch_size"],
                n_epochs=train["n_epochs"], learning_rate=train["learning_rate"], gamma=train["gamma"],
                gae_lambda=train["gae_lambda"], ent_coef=train["ent_coef"],
                policy_kwargs={"net_arch": list(train["net_arch"]),
                               "log_std_init": float(train["log_std_init"])},
                verbose=0, device="cpu")
    t0 = time.monotonic()
    stopped_early = False
    try:
        model.learn(total_timesteps=int(train["total_timesteps"]), callback=callbacks)
    except _StopTraining:
        stopped_early = True
    finally:
        logger.close()
    wall = time.monotonic() - t0
    steps_done = int(model.num_timesteps)
    print(f"trained {steps_done} steps in {wall:.0f}s ({steps_done/wall:.1f} env steps/s)"
          + (" [early stop]" if stopped_early else ""), flush=True)
    logger.write_status("evaluating", wall, {"stopped_early": stopped_early})

    model.save(os.path.join(out_dir, "model.zip"))
    onnx_path = os.path.join(out_dir, "policy.onnx")
    export_onnx(model, c, onnx_path)
    plot_curve(logger.rows, weights, os.path.join(out_dir, "learning_curve.png"))

    ev = evaluate(model, env, int(train["eval_episodes"]))
    with open(os.path.join(out_dir, "eval.json"), "w") as f:
        json.dump(ev, f, indent=2)
    print(f"eval: mean final |err| {ev['mean_final_abs_err']:.4f} rad, settled {ev['mean_settled_abs_err']:.4f} rad")
    print(f"wrote {onnx_path}")
    logger.write_status("done", wall, {"stopped_early": stopped_early, "eval_final_abs_err": ev["mean_final_abs_err"],
                                       "onnx": onnx_path})
    env.close()
    return 0
