"""Check that STEP advances exactly the requested number of physics steps (via sim_time).

    python3 -m unirobolab.direct.step_check <entity> [port]
"""

from __future__ import annotations

import sys

from unirobolab.direct.client import LearningClient


def main() -> int:
    entity = sys.argv[1] if len(sys.argv) > 1 else "ServoDemo"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 10100
    c = LearningClient(port=port)
    c.pause()
    ok = True
    for n in (1, 2, 3, 5, 8, 9, 10, 16, 50, 100):
        _, t0 = c.step([entity], 0, [None])
        _, t1 = c.step([entity], n, [None])
        ran = (t1 - t0) / 0.02
        good = abs(ran - n) < 1e-3
        ok &= good
        print(f"steps={n:3d}: sim dt {(t1 - t0) * 1e3:8.3f} ms -> {ran:.3f} steps {'OK' if good else 'MISMATCH'}")
    c.close()
    print("exact" if ok else "NOT exact")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
