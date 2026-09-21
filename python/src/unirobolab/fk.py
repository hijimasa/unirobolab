"""URDF の運動学チェーン。手先などのリンクの位置を関節角から求める (順運動学)。

契約には chain (根リンクから対象リンクまでの各段: 固定の並進・回転と、可動なら回転軸と関節名) を
そのまま書く。計算 (fk_point) は obs_math.py と policy_node.py に同じ写しを置き、生成した
ROS 2 パッケージが unirobolab 無しで同じ観測を作れるようにする。
"""
from __future__ import annotations

import xml.etree.ElementTree as ET
from typing import Any

import numpy as np


def _floats(s: str | None, n: int) -> list[float]:
    if not s:
        return [0.0] * n
    v = [float(x) for x in s.split()]
    return (v + [0.0] * n)[:n]


def reach_radius(tree: dict[str, Any]) -> float:
    """根リンクから届きうる最大距離の上限 [m]。各リンクへの経路の並進の長さを足し、先端の張り出しを加える。
    姿勢によらない上限なので、並列学習で隣のロボットと重ならない間隔を決めるのに使う。"""
    joints = tree["joints"]
    best = 0.0
    for link in tree["links"]:
        d, cur, guard = 0.0, link, 0
        while cur in joints and guard < 64:
            j = joints[cur]
            d += float(np.linalg.norm(np.asarray(j["xyz"], float)))
            cur = j["parent"]; guard += 1
        tip = tree["tips"].get(link)
        if tip:
            d += float(np.linalg.norm(np.asarray(tip, float)))
        best = max(best, d)
    return best


def load_tree(urdf_path: str) -> dict[str, Any]:
    """{"root": link, "joints": {child_link: joint dict}, "links": [names], "tips": {link: xyz}}"""
    root = ET.parse(urdf_path).getroot()
    joints: dict[str, dict[str, Any]] = {}
    children = set()
    links = [l.get("name") for l in root.findall("link")]
    for j in root.findall("joint"):
        parent = j.find("parent").get("link"); child = j.find("child").get("link")
        origin = j.find("origin"); axis = j.find("axis")
        joints[child] = {"name": j.get("name"), "type": j.get("type", "fixed"), "parent": parent, "child": child,
                         "xyz": _floats(origin.get("xyz") if origin is not None else None, 3),
                         "rpy": _floats(origin.get("rpy") if origin is not None else None, 3),
                         "axis": _floats(axis.get("xyz") if axis is not None else "1 0 0", 3)}
        children.add(child)
    roots = [l for l in links if l not in children]
    # 先端の目安: リンクの visual (無ければ collision) の形状の、原点から見て一番遠い端。
    # 箱・円柱は最長軸 (原点のずれの向き) の端、球や不明な形状は原点。手先を「リンクの点」で指すときの既定
    tips: dict[str, list[float]] = {}
    for l in root.findall("link"):
        for tag in ("visual", "collision"):
            g = l.find(tag)
            if g is None:
                continue
            o = g.find("origin")
            origin = np.asarray(_floats(o.get("xyz") if o is not None else None, 3))
            geom = g.find("geometry")
            ext = np.zeros(3)
            if geom is not None:
                box = geom.find("box"); cyl = geom.find("cylinder")
                if box is not None:
                    ext = 0.5 * np.asarray(_floats(box.get("size"), 3))
                elif cyl is not None:
                    ext = np.array([0.0, 0.0, 0.5 * float(cyl.get("length", 0.0))])
            tip = origin.copy()
            if np.any(ext > 0):
                axis = int(np.argmax(np.where(ext > 0, np.abs(origin) + ext, -1.0)))   # 原点のずれが大きく伸びている軸
                sign = np.sign(origin[axis]) if abs(origin[axis]) > 1e-6 else 1.0
                tip[axis] = origin[axis] + sign * ext[axis]
            tips[l.get("name")] = [round(float(v), 4) for v in tip]
            break
    return {"root": roots[0] if roots else (links[0] if links else ""), "joints": joints, "links": links, "tips": tips}


def chain_to(tree: dict[str, Any], link: str) -> list[dict[str, Any]]:
    """根から link までの段の列 (契約の chain)。可動段は joint 名と axis を持つ。"""
    steps: list[dict[str, Any]] = []
    cur = link
    while cur in tree["joints"]:
        j = tree["joints"][cur]
        step: dict[str, Any] = {"xyz": j["xyz"], "rpy": j["rpy"]}
        if j["type"] in ("revolute", "continuous", "prismatic"):
            step["joint"] = j["name"]; step["axis"] = j["axis"]; step["type"] = j["type"]
        steps.append(step)
        cur = j["parent"]
    steps.reverse()
    return steps


def fk_point(chain: list, q: dict, point=None) -> np.ndarray:
    """chain の段を根から順に適用し、最後のリンク座標系の point (既定: 原点) を根の座標系で返す。"""
    def rpy_rot(r: float, p: float, y: float) -> np.ndarray:
        cr, sr, cp, sp, cy, sy = np.cos(r), np.sin(r), np.cos(p), np.sin(p), np.cos(y), np.sin(y)
        return np.array([[cy * cp, cy * sp * sr - sy * cr, cy * sp * cr + sy * sr],
                         [sy * cp, sy * sp * sr + cy * cr, sy * sp * cr - cy * sr],
                         [-sp, cp * sr, cp * cr]])

    def axis_rot(axis: np.ndarray, a: float) -> np.ndarray:
        k = axis / (np.linalg.norm(axis) or 1.0)
        K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
        return np.eye(3) + np.sin(a) * K + (1 - np.cos(a)) * (K @ K)

    R = np.eye(3); t = np.zeros(3)
    for s in chain:
        t = t + R @ np.asarray(s.get("xyz", [0, 0, 0]), float)
        R = R @ rpy_rot(*[float(v) for v in s.get("rpy", [0, 0, 0])])
        jn = s.get("joint")
        if jn is not None:
            a = float(q.get(jn, 0.0)); axis = np.asarray(s.get("axis", [1, 0, 0]), float)
            if s.get("type") == "prismatic":
                t = t + R @ (axis / (np.linalg.norm(axis) or 1.0) * a)
            else:
                R = R @ axis_rot(axis, a)
    return (t + R @ np.asarray(point if point is not None else [0.0, 0.0, 0.0], float)).astype(np.float32)
