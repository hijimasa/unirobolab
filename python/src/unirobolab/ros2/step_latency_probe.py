"""Time /step_and_observe for several step counts: the slope is the per-step cost
(one frame per physics step while stepping), the intercept is the round-trip overhead.

    python3 -m unirobolab.ros2.step_latency_probe <entity> [repeats]
"""

from __future__ import annotations

import sys
import time

import numpy as np
import rclpy
from simulation_extra_interfaces.srv import StepAndObserve
from simulation_interfaces.msg import SimulationState
from simulation_interfaces.srv import SetSimulationState


def call(node, cli, req, timeout_s=30.0):
    fut = cli.call_async(req)
    end = time.monotonic() + timeout_s
    while rclpy.ok() and not fut.done():
        rclpy.spin_once(node, timeout_sec=0.001)
        if time.monotonic() > end:
            raise TimeoutError(cli.srv_name)
    return fut.result()


def main() -> int:
    entity = sys.argv[1] if len(sys.argv) > 1 else "ServoDemo"
    repeats = int(sys.argv[2]) if len(sys.argv) > 2 else 20
    rclpy.init()
    node = rclpy.create_node("step_latency_probe")
    st = node.create_client(SetSimulationState, "/set_simulation_state")
    sao = node.create_client(StepAndObserve, "/step_and_observe")
    for c in (st, sao):
        if not c.wait_for_service(timeout_sec=10.0):
            print(f"{c.srv_name} not available"); return 1
    req = SetSimulationState.Request(); req.state = SimulationState(state=SimulationState.STATE_PAUSED)
    call(node, st, req)
    rows = []
    for steps in (0, 1, 2, 5, 10, 20, 50):
        ts = []
        for _ in range(repeats):
            r = StepAndObserve.Request(); r.entity = entity; r.steps = steps
            t0 = time.perf_counter()
            res = call(node, sao, r)
            ts.append((time.perf_counter() - t0) * 1e3)
            if res.result != StepAndObserve.Response.RESULT_OK:
                print("error:", res.error_message); return 1
        ts = np.array(ts)
        rows.append((steps, np.median(ts)))
        print(f"steps={steps:3d}: median {np.median(ts):6.1f} ms  p10 {np.percentile(ts,10):6.1f}  p90 {np.percentile(ts,90):6.1f}")
    x = np.array([r[0] for r in rows], float); y = np.array([r[1] for r in rows])
    slope, icpt = np.polyfit(x, y, 1)
    print(f"fit: {icpt:.1f} ms round trip + {slope:.2f} ms per physics step ({1e3/slope if slope>0 else float('inf'):.0f} steps/s while stepping)")
    node.destroy_node(); rclpy.try_shutdown()
    return 0


if __name__ == "__main__":
    sys.exit(main())
