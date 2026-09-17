"""環境中の物体 (押す・運ぶ対象) の URDF を作る。物体は「基体が自由な 1 リンクの URDF」として
シミュレータにスポーンし、学習サーバがロボットと同じ経路で位置・姿勢を返す。"""
from __future__ import annotations

import os
from typing import Any


def object_urdf(name: str, shape: str = "box", size: list[float] | None = None, mass: float = 0.2,
                color: list[float] | None = None) -> str:
    size = list(size or [0.05, 0.05, 0.05])
    color = list(color or [0.9, 0.5, 0.2, 1.0])
    if shape == "box":
        geom = f'<box size="{size[0]} {size[1]} {size[2]}"/>'
        ixx = mass / 12 * (size[1] ** 2 + size[2] ** 2); iyy = mass / 12 * (size[0] ** 2 + size[2] ** 2); izz = mass / 12 * (size[0] ** 2 + size[1] ** 2)
        half_h = size[2] / 2
    elif shape == "sphere":
        r = size[0]
        geom = f'<sphere radius="{r}"/>'
        ixx = iyy = izz = 0.4 * mass * r * r
        half_h = r
    elif shape == "cylinder":
        r, h = size[0], size[1] if len(size) > 1 else size[0]
        geom = f'<cylinder radius="{r}" length="{h}"/>'
        ixx = iyy = mass / 12 * (3 * r * r + h * h); izz = 0.5 * mass * r * r
        half_h = h / 2
    else:
        raise ValueError(f"unknown shape {shape!r}")
    return f'''<?xml version="1.0"?>
<robot name="{name}">
  <material name="{name}_color"><color rgba="{color[0]} {color[1]} {color[2]} {color[3]}"/></material>
  <link name="{name}_link">
    <inertial>
      <origin xyz="0 0 {half_h}" rpy="0 0 0"/>
      <mass value="{mass}"/>
      <inertia ixx="{ixx:.6g}" ixy="0" ixz="0" iyy="{iyy:.6g}" iyz="0" izz="{izz:.6g}"/>
    </inertial>
    <visual>
      <origin xyz="0 0 {half_h}" rpy="0 0 0"/>
      <geometry>{geom}</geometry>
      <material name="{name}_color"/>
    </visual>
    <collision>
      <origin xyz="0 0 {half_h}" rpy="0 0 0"/>
      <geometry>{geom}</geometry>
    </collision>
  </link>
</robot>
'''


def write_object_urdfs(objects: list[dict[str, Any]], out_dir: str) -> dict[str, str]:
    """task.json の objects[] から URDF を書き、名前 → パスを返す。"""
    os.makedirs(out_dir, exist_ok=True)
    paths = {}
    for o in objects:
        name = o["name"]
        path = os.path.join(out_dir, f"{name}.urdf")
        with open(path, "w") as f:
            f.write(object_urdf(name, o.get("shape", "box"), o.get("size"), float(o.get("mass", 0.2)), o.get("color")))
        paths[name] = path
    return paths
