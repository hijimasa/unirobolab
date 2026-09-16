"""sim2sim evaluator: drive the generated policy node against the simulator and judge.

Runs inside the ROS 2 environment. It optionally launches the generated package,
publishes a goal sequence on the contract's goal topic, records joint states and
the node's status, and produces a pass/fail report with the reasons.

Checks (each is reported individually):
  joints_present   every contract joint appears on joint_states
  node_alive       status messages arrive and report no contract errors
  rate             measured loop rate within tolerance of policy_rate_hz
  obs_fresh        worst joint_states age seen by the loop stays below max_obs_age_s
                   (default 2 policy periods)
  tracking         after each hold, |q - goal| <= tolerance for every joint
  finite           no NaN/inf in observations or actions
"""

from __future__ import annotations

import csv
import json
import os
import signal
import subprocess
import sys
import time
from dataclasses import dataclass, field

import numpy as np
import rclpy
from rclpy.node import Node
from geometry_msgs.msg import PoseStamped
from sensor_msgs.msg import JointState
from std_msgs.msg import Bool, Float64MultiArray, String

from unirobolab.contract import Contract

try:
    from geometry_msgs.msg import Wrench
    from simulation_extra_interfaces.srv import ApplyLinkWrench
except ImportError:  # older interface package
    ApplyLinkWrench = None


@dataclass
class Scenario:
    goals: list[list[float]]
    hold_s: float
    settle_window_s: float
    tolerance: dict[str, float]
    default_tolerance: float
    rate_tolerance: float  # fraction
    warmup_s: float = 3.0
    max_obs_age_s: float | None = None  # default: 2 policy periods
    # disturbances applied during every hold, relative to its start:
    #   [{"at_s": 1.0, "link": "", "force": [fx, fy, fz], "torque": [tx, ty, tz], "duration_s": 0.2}]
    # (simulator apply_link_wrench: world frame, ROS axes, force at the link's centre of mass)
    disturbances: list[dict] = field(default_factory=list)
    entity: str | None = None  # entity name for apply_link_wrench (default: ros.namespace)
    # random goals appended to `goals`: {"n": 5, "low": [...], "high": [...], "seed": 0}
    random_goals: dict | None = None
    # e-stop test appended after the goals: assert the e-stop topic at `at_s` into an extra hold of
    # `hold_s`, release it at `release_at_s`; pass = the node reports stopped, publishes no actions
    # while stopped, and resumes afterwards. {"at_s": 1.0, "release_at_s": 3.0, "hold_s": 6.0}
    estop_test: dict | None = None

    @staticmethod
    def default(c: Contract, amplitude: float, hold_s: float, tolerance: float) -> "Scenario":
        n = c.obs_terms_by_source("command")[0].size
        goals = [[amplitude] * n, [-amplitude] * n, [0.0] * n]
        return Scenario(goals=goals, hold_s=hold_s, settle_window_s=1.0,
                        tolerance={}, default_tolerance=tolerance, rate_tolerance=0.15)

    @staticmethod
    def load(path: str, c: Contract) -> "Scenario":
        with open(path, encoding="utf-8") as f:
            d = json.load(f)
        goals = list(d["goals"])
        rg = d.get("random_goals")
        if rg:
            rng = np.random.default_rng(int(rg.get("seed", 0)))
            lo, hi = np.asarray(rg["low"], float), np.asarray(rg["high"], float)
            goals += [list(map(float, rng.uniform(lo, hi))) for _ in range(int(rg.get("n", 5)))]
        return Scenario(goals=goals, hold_s=float(d.get("hold_s", 4.0)),
                        settle_window_s=float(d.get("settle_window_s", 1.0)),
                        tolerance=dict(d.get("tolerance", {})),
                        default_tolerance=float(d.get("default_tolerance", 0.1)),
                        rate_tolerance=float(d.get("rate_tolerance", 0.15)),
                        warmup_s=float(d.get("warmup_s", 3.0)),
                        max_obs_age_s=d.get("max_obs_age_s"),
                        disturbances=list(d.get("disturbances", [])),
                        entity=d.get("entity"), random_goals=rg, estop_test=d.get("estop_test"))

    def tol(self, joint: str) -> float:
        return float(self.tolerance.get(joint, self.default_tolerance))


@dataclass
class Recorder:
    js_rows: list[tuple] = field(default_factory=list)      # (t, name..., pos...)
    status: list[dict] = field(default_factory=list)
    actions: list[tuple] = field(default_factory=list)
    observations: list[tuple] = field(default_factory=list)
    js_names: list[str] | None = None
    poses: list[tuple] = field(default_factory=list)          # (t, x, y, z)


class Evaluator(Node):
    def __init__(self, c: Contract, rec: Recorder) -> None:
        super().__init__("sim2sim_evaluator")
        self.c = c
        self.rec = rec
        self.t0 = time.monotonic()
        ros = c.ros
        base = ros.goal_topic.rsplit("/", 1)[0]
        self.create_subscription(JointState, ros.joint_states_topic, self._on_js, 50)
        self.create_subscription(String, f"{base}/status", self._on_status, 10)
        self.create_subscription(Float64MultiArray, f"{base}/action", self._on_act, 50)
        self.create_subscription(Float64MultiArray, f"{base}/observation", self._on_obs, 50)
        self.pub_goal = self.create_publisher(Float64MultiArray, ros.goal_topic, 10)
        estop_topic = (c.safety or {}).get("estop_topic", f"/{ros.namespace}/policy/estop")
        self.pub_estop = self.create_publisher(Bool, estop_topic, 10)
        self.base_task = bool(c.obs_terms_by_source("base_goal_xy"))
        if self.base_task:
            self.create_subscription(PoseStamped, ros.ground_truth_topic, self._on_pose, 50)
        self.cli_wrench = None
        if ApplyLinkWrench is not None:
            self.cli_wrench = self.create_client(ApplyLinkWrench, "/apply_link_wrench")
        self.wrench_log: list[dict] = []

    def apply_wrench(self, entity: str, d: dict) -> None:
        if self.cli_wrench is None or not self.cli_wrench.wait_for_service(timeout_sec=2.0):
            print("apply_link_wrench service not available; disturbance skipped", file=sys.stderr)
            return
        req = ApplyLinkWrench.Request()
        req.entity = entity
        req.link = d.get("link", "")
        f = d.get("force", [0, 0, 0]); tq = d.get("torque", [0, 0, 0])
        req.wrench = Wrench()
        req.wrench.force.x, req.wrench.force.y, req.wrench.force.z = map(float, f)
        req.wrench.torque.x, req.wrench.torque.y, req.wrench.torque.z = map(float, tq)
        req.duration = float(d.get("duration_s", 0.0))
        fut = self.cli_wrench.call_async(req)
        end = time.monotonic() + 5.0
        while rclpy.ok() and not fut.done() and time.monotonic() < end:
            rclpy.spin_once(self, timeout_sec=0.01)
        res = fut.result() if fut.done() else None
        ok = res is not None and res.result == ApplyLinkWrench.Response.RESULT_OK
        self.wrench_log.append({"t": self.now(), "entity": entity, "ok": ok, **d})
        if not ok:
            print(f"apply_link_wrench failed: {getattr(res, 'error_message', 'timeout')}", file=sys.stderr)

    def now(self) -> float:
        return time.monotonic() - self.t0

    def _on_js(self, m: JointState) -> None:
        if self.rec.js_names is None:
            self.rec.js_names = list(m.name)
        self.rec.js_rows.append((self.now(), list(m.name), list(m.position), list(m.velocity)))

    def _on_pose(self, m: PoseStamped) -> None:
        p = m.pose.position
        self.rec.poses.append((self.now(), p.x, p.y, p.z))

    def latest_xy(self) -> np.ndarray | None:
        return np.array(self.rec.poses[-1][1:3]) if self.rec.poses else None

    def _on_status(self, m: String) -> None:
        try:
            d = json.loads(m.data)
        except json.JSONDecodeError:
            return
        d["t"] = self.now()
        self.rec.status.append(d)

    def _on_act(self, m: Float64MultiArray) -> None:
        self.rec.actions.append((self.now(), list(m.data)))

    def _on_obs(self, m: Float64MultiArray) -> None:
        self.rec.observations.append((self.now(), list(m.data)))

    def send_goal(self, g: list[float]) -> None:
        self.pub_goal.publish(Float64MultiArray(data=[float(v) for v in g]))


def _spin_for(node: Node, seconds: float, on_tick=None) -> None:
    end = time.monotonic() + seconds
    while rclpy.ok() and time.monotonic() < end:
        rclpy.spin_once(node, timeout_sec=0.02)
        if on_tick:
            on_tick()


def _joint_positions(rec: Recorder, joints: list[str], t_from: float, t_to: float) -> dict[str, np.ndarray]:
    out = {j: [] for j in joints}
    for t, names, pos, _ in rec.js_rows:
        if t_from <= t <= t_to:
            for j in joints:
                if j in names:
                    out[j].append(pos[names.index(j)])
    return {j: np.asarray(v) for j, v in out.items()}


def evaluate(c: Contract, sc: Scenario, rec: Recorder, phases: list[tuple[float, float, list[float]]]) -> dict:
    checks: dict[str, dict] = {}
    joints = c.joints
    act_term = [t for t in c.actions if t.source == "joints"][0]
    act_joints = act_term.joints or joints

    # joints_present
    names = rec.js_names or []
    missing = [j for j in joints if j not in names]
    checks["joints_present"] = {"pass": not missing and bool(names),
                                "detail": f"missing={missing} seen={names}" if names else "no joint_states received"}

    # node_alive
    errs = sorted({e for s in rec.status for e in s.get("errors", [])})
    checks["node_alive"] = {"pass": bool(rec.status) and not errs,
                            "detail": f"{len(rec.status)} status msgs, errors={errs}" if rec.status else "no status from policy node"}

    # rate (ignore the first status which may be a partial second)
    rates = [s["rate_measured_hz"] for s in rec.status[1:] if s.get("steps", 0) > 0]
    if rates:
        med = float(np.median(rates))
        ok = abs(med - c.policy_rate_hz) <= sc.rate_tolerance * c.policy_rate_hz
        checks["rate"] = {"pass": ok, "detail": f"median {med:.1f} Hz vs target {c.policy_rate_hz:g} Hz (min {min(rates):.1f}, max {max(rates):.1f})"}
    else:
        checks["rate"] = {"pass": False, "detail": "no rate samples"}

    # obs_fresh: worst joint_states age the policy loop ever saw
    limit = sc.max_obs_age_s if sc.max_obs_age_s is not None else 2 * c.period_s
    ages = [s["obs_age_max_s"] for s in rec.status if s.get("obs_age_max_s") is not None]
    means = [s["obs_age_mean_s"] for s in rec.status if s.get("obs_age_mean_s") is not None]
    if ages:
        worst = max(ages)
        checks["obs_fresh"] = {"pass": worst <= limit + 0.005,
                               "detail": f"worst joint_states age {worst*1e3:.1f} ms, mean {np.mean(means)*1e3:.1f} ms "
                                         f"(limit {limit*1e3:.0f} ms)"}
    else:
        checks["obs_fresh"] = {"pass": False, "detail": "no observation age samples"}

    # tracking per phase
    track = []
    all_ok = True
    base_task = bool(c.obs_terms_by_source("base_goal_xy"))
    if base_task:
        # goal = world x, y; pass when the base ends the hold within tolerance (metres)
        for (t_start, t_end, goal) in phases:
            pts = np.array([[x, y] for (t, x, y, z) in rec.poses if t_end - sc.settle_window_s <= t <= t_end])
            row = {"goal": goal, "t_end": round(t_end, 2), "joints": {}}
            if pts.size == 0:
                row["joints"]["base"] = {"pass": False, "detail": "no ground truth in settle window"}
                all_ok = False
            else:
                d = float(np.mean(np.linalg.norm(pts - np.asarray(goal[:2]), axis=1)))
                ok = d <= sc.default_tolerance
                all_ok &= ok
                row["joints"]["base"] = {"pass": ok, "mean_abs_err": round(d, 4), "tol": sc.default_tolerance,
                                         "q_mean": [round(float(v), 3) for v in pts.mean(axis=0)], "n": int(len(pts))}
            track.append(row)
        checks["tracking"] = {"pass": all_ok and bool(phases), "phases": track}
        phases = []  # skip the joint loop below
    for (t_start, t_end, goal) in phases:
        win = _joint_positions(rec, act_joints, t_end - sc.settle_window_s, t_end)
        row = {"goal": goal, "t_end": round(t_end, 2), "joints": {}}
        for i, j in enumerate(act_joints):
            q = win[j]
            if q.size == 0:
                row["joints"][j] = {"pass": False, "detail": "no samples in settle window"}
                all_ok = False
                continue
            err = float(np.mean(np.abs(q - goal[i])))
            ok = err <= sc.tol(j)
            all_ok &= ok
            row["joints"][j] = {"pass": ok, "mean_abs_err": round(err, 4), "tol": sc.tol(j),
                                "q_mean": round(float(q.mean()), 4), "n": int(q.size)}
        track.append(row)
    if not base_task:
        checks["tracking"] = {"pass": all_ok and bool(phases), "phases": track}

    # finite
    bad = 0
    for _, v in rec.actions:
        bad += int(not np.all(np.isfinite(v)))
    for _, v in rec.observations:
        bad += int(not np.all(np.isfinite(v)))
    checks["finite"] = {"pass": bad == 0 and (rec.actions or rec.observations) != [],
                        "detail": f"{bad} non-finite vectors over {len(rec.actions)} actions / {len(rec.observations)} observations"}

    overall = all(v["pass"] for v in checks.values())
    return {"contract": c.name, "pass": overall, "checks": checks,
            "scenario": {"goals": len(phases), "hold_s": sc.hold_s, "disturbances": sc.disturbances},
            "samples": {"joint_states": len(rec.js_rows), "status": len(rec.status), "actions": len(rec.actions)}}


def _evaluate_estop(rec: Recorder, times: dict[str, float]) -> dict:
    """The node must report stopped while the e-stop is asserted, publish no actions then, and resume."""
    t_a, t_r = times.get("assert"), times.get("release")
    if t_a is None:
        return {"pass": False, "detail": "e-stop was never asserted"}
    stopped = [s for s in rec.status if s["t"] > t_a + 1.0 and (t_r is None or s["t"] < t_r) and s.get("safety")]
    reported = bool(stopped) and all(s["safety"]["stopped"] for s in stopped)
    acts_during = sum(1 for t, _ in rec.actions if t_a + 0.3 < t < (t_r if t_r else 1e9))
    resumed = t_r is None or any(t > t_r + 1.5 for t, _ in rec.actions)
    ok = reported and acts_during == 0 and resumed
    return {"pass": ok, "detail": f"stopped reported={reported}, actions while stopped={acts_during}, resumed={resumed}"}


def run(c: Contract, sc: Scenario, pkg: str | None, ns: str | None, out_dir: str,
        launch_timeout_s: float = 15.0) -> int:
    if c.ros is None:
        print("contract has no 'ros' section; sim2sim needs topics", file=sys.stderr)
        return 2
    if ns:
        # override namespace: rebuild the topic defaults the way the node does
        from unirobolab.contract import RosConfig
        prefix = f"/{ns}"
        c.ros = RosConfig(namespace=ns, joint_states_topic=f"{prefix}/joint_states",
                          command_topic=f"{prefix}/joint_command", command_mode=c.ros.command_mode,
                          goal_topic=f"{prefix}/policy/command", controller_name=c.ros.controller_name)
    os.makedirs(out_dir, exist_ok=True)

    proc = None
    if pkg:
        cmd = ["ros2", "launch", pkg, "policy.launch.py", f"ns:={c.ros.namespace}"]
        print("launching:", " ".join(cmd))
        proc = subprocess.Popen(cmd, stdout=open(os.path.join(out_dir, "policy_node.log"), "w"),
                                stderr=subprocess.STDOUT, start_new_session=True)

    rclpy.init()
    rec = Recorder()
    node = Evaluator(c, rec)
    phases: list[tuple[float, float, list[float]]] = []
    estop_times: dict[str, float] = {}
    try:
        # wait for the node to report
        t_wait = time.monotonic()
        while not rec.status and time.monotonic() - t_wait < launch_timeout_s:
            rclpy.spin_once(node, timeout_sec=0.1)
        if not rec.status:
            print(f"no status from policy node within {launch_timeout_s}s", file=sys.stderr)
        _spin_for(node, sc.warmup_s)
        origin = node.latest_xy() if node.base_task else None
        if node.base_task and origin is None:
            print("no ground truth pose received; base goals cannot be placed", file=sys.stderr)
        for g in sc.goals:
            if node.base_task and origin is not None:
                g = [float(origin[0] + g[0]), float(origin[1] + g[1])]  # scenario goals are offsets
            t_start = node.now()
            # re-publish the goal a few times: a late subscriber must not miss it
            for _ in range(3):
                node.send_goal(g)
                _spin_for(node, 0.05)
            print(f"goal {g} hold {sc.hold_s}s" + (f" with {len(sc.disturbances)} disturbances" if sc.disturbances else ""))
            if sc.disturbances:
                entity = sc.entity or c.ros.namespace
                pending = sorted(sc.disturbances, key=lambda d: float(d.get("at_s", 0.0)))
                hold_start = node.now()
                while node.now() - hold_start < sc.hold_s:
                    if pending and node.now() - hold_start >= float(pending[0].get("at_s", 0.0)):
                        node.apply_wrench(entity, pending.pop(0))
                    _spin_for(node, 0.05)
            else:
                _spin_for(node, sc.hold_s, on_tick=None)
            phases.append((t_start, node.now(), list(g)))
        if sc.estop_test:
            et = sc.estop_test
            hold = float(et.get("hold_s", 6.0)); at = float(et.get("at_s", 1.0)); rel = float(et.get("release_at_s", 3.0))
            print(f"e-stop test: assert at {at}s, release at {rel}s, hold {hold}s")
            t0e = node.now(); asserted = released = False
            while node.now() - t0e < hold:
                el = node.now() - t0e
                if not asserted and el >= at:
                    node.pub_estop.publish(Bool(data=True)); asserted = True; estop_times["assert"] = node.now()
                if asserted and not released and el >= rel:
                    node.pub_estop.publish(Bool(data=False)); released = True; estop_times["release"] = node.now()
                _spin_for(node, 0.05)
    finally:
        node_wrench_log = list(node.wrench_log)
        node.destroy_node()
        rclpy.try_shutdown()
        if proc is not None:
            proc.send_signal(signal.SIGINT)  # ros2 launch forwards it to the node once
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(os.getpgid(proc.pid), signal.SIGKILL)

    report = evaluate(c, sc, rec, phases)
    report["wrenches"] = node_wrench_log
    if sc.estop_test and estop_times:
        report["checks"]["estop"] = _evaluate_estop(rec, estop_times)
        report["pass"] = report["pass"] and report["checks"]["estop"]["pass"]
    with open(os.path.join(out_dir, "report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
    with open(os.path.join(out_dir, "joint_states.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["t"] + [f"q_{j}" for j in c.joints] + [f"qd_{j}" for j in c.joints])
        for t, names, pos, vel in rec.js_rows:
            idx = [names.index(j) if j in names else None for j in c.joints]
            w.writerow([f"{t:.4f}"] + [pos[i] if i is not None else "" for i in idx]
                       + [vel[i] if i is not None and i < len(vel) else "" for i in idx])
    with open(os.path.join(out_dir, "status.jsonl"), "w", encoding="utf-8") as f:
        for s in rec.status:
            f.write(json.dumps(s) + "\n")

    print()
    print(f"sim2sim {c.name}: {'PASS' if report['pass'] else 'FAIL'}")
    for k, v in report["checks"].items():
        mark = "ok  " if v["pass"] else "FAIL"
        if k == "tracking":
            print(f"  {mark} tracking")
            for ph in v["phases"]:
                for j, r in ph["joints"].items():
                    m = "ok  " if r["pass"] else "FAIL"
                    print(f"       {m} goal={ph['goal']} {j}: " + (
                        f"|err|={r['mean_abs_err']} tol={r['tol']} q={r['q_mean']}" if "mean_abs_err" in r else r["detail"]))
        else:
            print(f"  {mark} {k}: {v['detail']}")
    print(f"report: {os.path.join(out_dir, 'report.json')}")
    return 0 if report["pass"] else 1
