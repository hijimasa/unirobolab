import json
import pathlib

from unirobolab.explain import explain, format_text

FIX = pathlib.Path(__file__).parent / "fixtures" / "reports"


def _ex(name, lang="ja"):
    return explain(json.load(open(FIX / f"{name}.json")), lang)


def test_pass_report_has_no_next_steps():
    ex = _ex("safety_servo")
    assert ex["pass"] and all(i["ok"] for i in ex["items"]) and ex["next"] == []
    assert [i["key"] for i in ex["items"]] == ["joints_present", "node_alive", "rate", "obs_fresh", "tracking", "finite", "estop"]


def test_bad_joint_name_suggests_closest_joint():
    ex = _ex("badjoint")
    jp = ex["items"][0]
    assert not jp["ok"] and "ideal_join → ideal_joint?" in jp["reason"]
    assert any("関節名" in s for s in ex["next"])
    # the downstream failures (rate, freshness, tracking) must not add unrelated advice
    assert not any("target_fps" in s or "目標範囲" in s for s in ex["next"])


def test_unmoved_joints_point_at_action_scaling():
    ex = _ex("badscale")
    tr = next(i for i in ex["items"] if i["key"] == "tracking")
    assert not tr["ok"] and "1/3" in tr["reason"]
    assert any("scale/offset" in s for s in ex["next"])


def test_marginal_base_miss_suggests_tolerance_and_hold():
    ex = _ex("diffbot_hold")
    tr = next(i for i in ex["items"] if i["key"] == "tracking")
    assert "2/3" in tr["reason"] and " m" in tr["reason"]
    assert any("許容誤差" in s for s in ex["next"]) and any("terminate_on_reach" in s for s in ex["next"])


def test_disturbance_row_and_english():
    ex = _ex("servo_robust", "en")
    d = next(i for i in ex["items"] if i["key"] == "disturbance")
    assert d["ok"] and d["reason"] == "7 pushes"
    text = format_text(ex, "en")
    assert text.startswith("PASS") and "[ok] recovers when pushed — 7 pushes" in text
