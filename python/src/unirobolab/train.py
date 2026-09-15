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
}


def load_config(path: str) -> tuple[dict[str, Any], dict[str, Any]]:
    with open(path, encoding="utf-8") as f:
        d = json.load(f)
    task = d.get("task", {})
    train = {**DEFAULT_TRAIN, **d.get("train", {})}
    return task, train


class _EpisodeLogger:
    """SB3 callback: one CSV row per finished episode, with per-term reward sums."""

    def __init__(self, path: str, weights: dict[str, float]):
        from stable_baselines3.common.callbacks import BaseCallback

        outer = self

        class CB(BaseCallback):
            def __init__(self):
                super().__init__()
                self.ret = 0.0
                self.len = 0
                self.episode = 0
                self.t0 = time.monotonic()

            def _on_step(self) -> bool:
                self.ret += float(self.locals["rewards"][0])
                self.len += 1
                info = self.locals["infos"][0]
                if "episode_terms" in info:
                    self.episode += 1
                    row = {"episode": self.episode, "timesteps": self.num_timesteps,
                           "wall_s": round(time.monotonic() - self.t0, 1),
                           "return": round(self.ret, 4), "length": self.len,
                           "final_abs_err": round(info["abs_err"], 4)}
                    for k in weights:
                        row[f"term_{k}"] = round(info["episode_terms"][k], 4)
                    outer.rows.append(row)
                    outer.writer.writerow(row)
                    outer.f.flush()
                    if self.episode % 10 == 0:
                        print(f"ep {self.episode:4d} t={self.num_timesteps:6d} "
                              f"return={self.ret:8.3f} final|err|={info['abs_err']:.4f} "
                              f"({row['wall_s']:.0f}s)")
                    self.ret, self.len = 0.0, 0
                return True

        self.rows: list[dict] = []
        self.f = open(path, "w", newline="")
        self.writer = csv.DictWriter(self.f, fieldnames=["episode", "timesteps", "wall_s", "return", "length",
                                                         "final_abs_err"] + [f"term_{k}" for k in weights])
        self.writer.writeheader()
        self.callback = CB()

    def close(self):
        self.f.close()


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
    out = []
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


def run(contract_path: str, config_path: str, out_dir: str, backend: str = "unity") -> int:
    from unirobolab import contract as contract_mod
    c = contract_mod.load(contract_path)
    task, train = load_config(config_path)
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "train_config.json"), "w") as f:
        json.dump({"task": task, "train": train, "contract": contract_path}, f, indent=2)

    if backend != "unity":
        print(f"backend {backend!r} not implemented"); return 2
    from stable_baselines3 import PPO
    from unirobolab.ros2.unity_env import UnityContractEnv

    env = UnityContractEnv(c, task)
    print(f"env: obs {env.observation_space.shape} act {env.action_space.shape}, "
          f"{task.get('physics_steps_per_action', 1)} physics steps/action, "
          f"{task.get('episode_steps', 100)} steps/episode, reward {env.reward_weights}")
    logger = _EpisodeLogger(os.path.join(out_dir, "progress.csv"), env.reward_weights)
    model = PPO("MlpPolicy", env, seed=train["seed"], n_steps=train["n_steps"], batch_size=train["batch_size"],
                n_epochs=train["n_epochs"], learning_rate=train["learning_rate"], gamma=train["gamma"],
                gae_lambda=train["gae_lambda"], ent_coef=train["ent_coef"],
                policy_kwargs={"net_arch": list(train["net_arch"]),
                               "log_std_init": float(train["log_std_init"])},
                verbose=0, device="cpu")
    t0 = time.monotonic()
    try:
        model.learn(total_timesteps=int(train["total_timesteps"]), callback=logger.callback)
    finally:
        logger.close()
    wall = time.monotonic() - t0
    print(f"trained {train['total_timesteps']} steps in {wall:.0f}s ({train['total_timesteps']/wall:.1f} steps/s)")

    model.save(os.path.join(out_dir, "model.zip"))
    onnx_path = os.path.join(out_dir, "policy.onnx")
    export_onnx(model, c, onnx_path)
    plot_curve(logger.rows, env.reward_weights, os.path.join(out_dir, "learning_curve.png"))

    ev = evaluate(model, env, int(train["eval_episodes"]))
    with open(os.path.join(out_dir, "eval.json"), "w") as f:
        json.dump(ev, f, indent=2)
    print(f"eval: mean final |err| {ev['mean_final_abs_err']:.4f} rad, settled {ev['mean_settled_abs_err']:.4f} rad")
    print(f"wrote {onnx_path}")
    env.close()
    return 0
