from unirobolab.status import format_text, train_status


def _rows(n, err, success=None, steps_per_ep=100):
    out = []
    for i in range(n):
        e = err(i) if callable(err) else err
        r = {"episode": i + 1, "timesteps": (i + 1) * steps_per_ep, "wall_s": (i + 1) * 0.5, "final_abs_err": e}
        if success is not None:
            r["success"] = int(success(i))
        out.append(r)
    return out


def test_progress_eta_and_success_from_tolerance():
    rows = _rows(100, lambda i: 0.5 - 0.004 * i)          # 0.5 -> 0.104
    st = train_status(rows, total_timesteps=20000, n_envs=8, wall_s=50.0, tolerance=0.3, window=50)
    assert st["timesteps"] == 10000 and st["progress"] == 0.5
    assert st["eta_s"] == 50.0 and st["env_steps_per_s"] == 200.0
    assert st["window"] == 50 and st["success_rate"] == 1.0   # last 50 episodes (err <= 0.3) all succeed
    assert st["hints"] == []


def test_success_flag_wins_over_tolerance():
    rows = _rows(20, 1.0, success=lambda i: i % 2 == 0)
    st = train_status(rows, 2000, 1, 10.0, tolerance=None, window=20)
    assert st["success_rate"] == 0.5


def test_no_success_hint_after_30_percent():
    rows = _rows(40, 1.0, success=lambda i: False)
    st = train_status(rows, 10000, 1, 10.0, window=20)   # 4000/10000 = 40 %
    assert [h["key"] for h in st["hints"]] == ["no_success"]
    assert "目標範囲" in format_text(st) and "goal range" in format_text(st, "en")


def test_getting_worse_hint():
    rows = _rows(80, lambda i: 0.1 if i < 40 else 0.3)
    st = train_status(rows, 10000, 1, 10.0, tolerance=0.05, window=40)
    assert "getting_worse" in [h["key"] for h in st["hints"]]


def test_format_text_phases():
    st = train_status(_rows(10, 0.01), 1000, 4, 5.0, tolerance=0.05, window=10, phase="done")
    text = format_text(st)
    assert text.startswith("学習が終わりました") and "4 体並列" in text and "残り" not in text
