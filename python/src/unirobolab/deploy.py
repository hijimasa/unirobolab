"""実機へ持っていく前の「接続先フォーム」と「手順書」。

- ros_set: 契約の ros 節 (名前空間、指令方式、コントローラ名、トピック) と safety.estop_topic を
  書き換える。GUI (Deploy タブ) は JSON を触らず、これを呼ぶ。
- deploy_guide: 契約から、接続するトピック、実機で確かめること (非常停止、上限値、ランプイン)、
  起動コマンドを平易な Markdown にする。生成パッケージに DEPLOY.md として置く。
"""
from __future__ import annotations

import json
from typing import Any


def ros_set(raw: dict[str, Any], namespace: str | None = None, command_mode: str | None = None,
            controller: str | None = None, joint_states_topic: str | None = None,
            command_topic: str | None = None, estop_topic: str | None = None) -> dict[str, Any]:
    """raw (契約 JSON) を書き換えて返す。None の項目は触らない。空文字は既定に戻す (削除)。"""
    ros = raw.setdefault("ros", {})

    def put(key: str, value: str | None) -> None:
        if value is None:
            return
        if value == "":
            ros.pop(key, None)
        else:
            ros[key] = value

    put("namespace", namespace)
    if command_mode is not None:
        if command_mode not in ("joint_state_topic", "ros2_control_commands"):
            raise ValueError(f"command_mode must be joint_state_topic or ros2_control_commands, got {command_mode!r}")
        ros["command_mode"] = command_mode
    put("controller_name", controller)
    if ros.get("command_mode") == "ros2_control_commands" and not ros.get("controller_name"):
        ros["controller_name"] = "joint_group_position_controller"   # gen の既定と同じ
    put("joint_states_topic", joint_states_topic)
    put("command_topic", command_topic)
    if estop_topic is not None:
        safety = raw.setdefault("safety", {})
        if estop_topic == "":
            safety.pop("estop_topic", None)
        else:
            safety["estop_topic"] = estop_topic
    return raw


def _terms(raw: dict[str, Any], key: str) -> list[dict[str, Any]]:
    """observations / actions は項のリスト (古い形の {"terms": [...]} も受ける)。"""
    v = raw.get(key, [])
    return list(v.get("terms", [])) if isinstance(v, dict) else list(v)


def _topics(raw: dict[str, Any]) -> dict[str, str]:
    ros = raw.get("ros", {})
    ns = ros.get("namespace", "robot")
    pre = f"/{ns}" if ns else ""
    mode = ros.get("command_mode", "joint_state_topic")
    t = {"joint_states": ros.get("joint_states_topic") or f"{pre}/joint_states"}
    if mode == "ros2_control_commands":
        ctrl = ros.get("controller_name") or "joint_group_position_controller"
        t["command"] = f"{pre}/{ctrl}/commands"
        t["controller"] = ctrl
    else:
        t["command"] = ros.get("command_topic") or f"{pre}/joint_command"
    t["estop"] = raw.get("safety", {}).get("estop_topic") or f"{pre}/estop"
    for k in ("goal_topic", "imu_topic", "odom_topic", "cmd_vel_topic"):
        if ros.get(k):
            t[k.replace("_topic", "")] = ros[k]
    src = [term.get("source") for term in _terms(raw, "observations")]
    if "base_goal_xy" in src and "goal" not in t:
        t["goal"] = f"{pre}/goal"
    if any(s in ("imu_orientation",) for s in src) and "imu" not in t:
        t["imu"] = f"{pre}/imu"
    if any(s in ("base_lin_vel", "base_ang_vel", "base_goal_xy") for s in src) and "odom" not in t:
        t["odom"] = f"{pre}/odom"
    act = _terms(raw, "actions")
    if any(a.get("target") == "base_twist" for a in act) and "cmd_vel" not in t:
        t["cmd_vel"] = f"{pre}/cmd_vel"
    return t


def deploy_guide(raw: dict[str, Any], package: str | None = None, lang: str = "ja") -> str:
    ja = lang != "en"
    name = raw.get("name", "policy")
    pkg = package or f"{name}_policy"
    ros = raw.get("ros", {})
    ns = ros.get("namespace", "robot")
    joints = [j["name"] if isinstance(j, dict) else j for j in raw.get("robot", {}).get("joints", [])]
    rate = raw.get("control", {}).get("policy_rate_hz", 0)
    mode = ros.get("command_mode", "joint_state_topic")
    t = _topics(raw)
    safety = raw.get("safety", {})
    act = _terms(raw, "actions")
    act_kinds = sorted({(a.get("mode") or a.get("target") or "position") for a in act})

    L: list[str] = []
    if ja:
        L += [f"# {name} を実機で動かす手順", "",
              f"生成パッケージ: `{pkg}`。契約 `{name}` (制御周期 {rate:g} Hz、関節 {len(joints)} 個: {', '.join(joints)})。", "",
              "## 1. 接続するトピック", "",
              "| 役割 | トピック | 実機側で用意するもの |", "|---|---|---|",
              f"| 関節の状態 | `{t['joint_states']}` | sensor_msgs/JointState を {max(rate * 2, 20):g} Hz 以上で出す (name に上の関節名を含める) |"]
        if mode == "ros2_control_commands":
            L.append(f"| 指令 | `{t['command']}` | ros2_control のコントローラ `{t['controller']}` (Float64MultiArray、関節順は契約どおり) |")
        else:
            L.append(f"| 指令 | `{t['command']}` | sensor_msgs/JointState を受けて関節を動かすドライバ ({'/'.join(act_kinds)}) |")
        L.append(f"| 非常停止 | `{t['estop']}` | std_msgs/Bool。true で指令が止まり、false で再開 |")
        for k, label, what in (("goal", "目標位置", "geometry_msgs/Point など (契約の goal_topic 参照)"),
                               ("odom", "オドメトリ", "nav_msgs/Odometry"), ("imu", "IMU", "sensor_msgs/Imu"),
                               ("cmd_vel", "速度指令 (出力)", "geometry_msgs/Twist を受ける台車ドライバ")):
            if k in t:
                L.append(f"| {label} | `{t[k]}` | {what} |")
        L += ["", "## 2. 動かす前に確かめること", ""]
        L.append(f"- 非常停止: `{t['estop']}` に true を流して指令が止まることを、ロボットを浮かせた状態で確かめる。"
                 " 物理的な非常停止スイッチも別に用意する (このトピックはソフトウェアの停止でしかない)。")
        if safety.get("joint_limits"):
            lim = ", ".join(f"{k}: [{v[0]:g}, {v[1]:g}]" for k, v in safety["joint_limits"].items())
            L.append(f"- 可動範囲の上限 (契約 safety.joint_limits、これを超える指令は切り詰められる): {lim}")
        if safety.get("max_joint_speed") is not None:
            L.append(f"- 関節速度の上限: {json.dumps(safety['max_joint_speed'])} (rad/s または m/s)。初回は半分程度に下げて様子を見る。")
        if safety.get("max_joint_effort") is not None:
            L.append(f"- 関節トルク/力の上限: {json.dumps(safety['max_joint_effort'])}。")
        if safety.get("max_base_speed"):
            v = safety["max_base_speed"]
            L.append(f"- 台車速度の上限: 並進 {v[0]:g} m/s、旋回 {v[1]:g} rad/s。")
        L.append(f"- 観測の鮮度: joint_states が {safety.get('max_obs_age_s', 0.2):g} 秒より古くなると指令を止める。無線や USB の遅れがこれを超えないか確かめる。")
        L.append(f"- 立ち上がり: 起動後 {safety.get('ramp_in_s', 1.0):g} 秒かけて現在姿勢から方策の目標へ寄せる (急に動かない)。")
        stop = safety.get("stop_action", "hold")
        L.append("- 停止時の動作: " + ("指令の送信をやめる (ドライバがその場で保持)" if stop == "hold" else "ゼロ (停止) 指令を出す") + "。")
        L += ["", "## 3. 起動", "", "```bash",
              f"cd <ws> && colcon build --packages-select {pkg} && source install/setup.bash",
              "pip install onnxruntime   # ノードの Python 環境に無ければ",
              f"ros2 launch {pkg} policy.launch.py ns:={ns}",
              "```", "",
              "## 4. 初回の見方", "",
              f"1. まず sim2sim (Check タブ) が合格していること。",
              "2. ロボットを浮かせるか、人が届かない場所で、上限を下げた状態で起動する。",
              f"3. `ros2 topic echo {t['joint_states']}` と `ros2 topic hz {t['command']}` で、指令が {rate:g} Hz で出ていることを見る。",
              "4. 非常停止 → 再開 を 1 回試す。",
              "5. 上限を契約の値に戻し、本来の目標で動かす。うまく行かなければ Check タブの「次の一手」に戻る。", ""]
    else:
        L += [f"# Running {name} on the real robot", "",
              f"Generated package: `{pkg}`. Contract `{name}` ({rate:g} Hz, {len(joints)} joints: {', '.join(joints)}).", "",
              "## 1. Topics to connect", "", "| role | topic | what the robot side must provide |", "|---|---|---|",
              f"| joint states | `{t['joint_states']}` | sensor_msgs/JointState at >= {max(rate * 2, 20):g} Hz with the joint names above |"]
        if mode == "ros2_control_commands":
            L.append(f"| command | `{t['command']}` | ros2_control controller `{t['controller']}` (Float64MultiArray in contract joint order) |")
        else:
            L.append(f"| command | `{t['command']}` | a driver that accepts sensor_msgs/JointState ({'/'.join(act_kinds)}) |")
        L.append(f"| e-stop | `{t['estop']}` | std_msgs/Bool: true stops commanding, false resumes |")
        for k, label, what in (("goal", "goal", "geometry_msgs/Point (see goal_topic)"), ("odom", "odometry", "nav_msgs/Odometry"),
                               ("imu", "IMU", "sensor_msgs/Imu"), ("cmd_vel", "velocity command (output)", "a base driver taking geometry_msgs/Twist")):
            if k in t:
                L.append(f"| {label} | `{t[k]}` | {what} |")
        L += ["", "## 2. Check before moving", "",
              f"- E-stop: publish true on `{t['estop']}` with the robot lifted and confirm commands stop. Keep a physical e-stop too.",]
        if safety.get("joint_limits"):
            L.append("- Joint limits (safety.joint_limits): " + ", ".join(f"{k}: [{v[0]:g}, {v[1]:g}]" for k, v in safety["joint_limits"].items()))
        if safety.get("max_joint_speed") is not None:
            L.append(f"- Joint speed cap: {json.dumps(safety['max_joint_speed'])}. Halve it for the first run.")
        if safety.get("max_joint_effort") is not None:
            L.append(f"- Effort cap: {json.dumps(safety['max_joint_effort'])}.")
        if safety.get("max_base_speed"):
            v = safety["max_base_speed"]
            L.append(f"- Base speed cap: {v[0]:g} m/s, {v[1]:g} rad/s.")
        L.append(f"- Freshness: commanding stops when joint_states is older than {safety.get('max_obs_age_s', 0.2):g} s.")
        L.append(f"- Ramp-in: {safety.get('ramp_in_s', 1.0):g} s blend from the measured pose to the policy targets.")
        L.append("- Stop action: " + ("stop publishing (drives hold)" if safety.get("stop_action", "hold") == "hold" else "publish zero") + ".")
        L += ["", "## 3. Launch", "", "```bash",
              f"cd <ws> && colcon build --packages-select {pkg} && source install/setup.bash",
              "pip install onnxruntime", f"ros2 launch {pkg} policy.launch.py ns:={ns}", "```", "",
              "## 4. First run", "", "1. sim2sim (Check tab) must pass first.", "2. Lift the robot or clear the area; lower the caps.",
              f"3. `ros2 topic echo {t['joint_states']}` and `ros2 topic hz {t['command']}`: commands at {rate:g} Hz.",
              "4. Try e-stop and resume once.", "5. Restore the caps and run the real goals; if it fails, go back to the Check tab's next steps.", ""]
    return "\n".join(L)
