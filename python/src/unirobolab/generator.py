"""Generate a ROS 2 (ament_python) package that runs a policy under a contract.

The package contains no generated logic: it ships the fixed ``policy_node.py``
from ``unirobolab/ros2``, the contract JSON and the ONNX file, and a launch file.
"""

from __future__ import annotations

import json
import os
import shutil
import stat
from importlib import resources

from unirobolab.contract import Contract


def _write(path: str, text: str, executable: bool = False) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    if executable:
        os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def generate(c: Contract, out_dir: str, package_name: str | None = None,
             onnx_path: str | None = None, overwrite: bool = False) -> str:
    pkg = package_name or f"{c.name}_policy"
    if not pkg.isidentifier():
        raise ValueError(f"package name {pkg!r} is not a valid identifier")
    root = os.path.join(out_dir, pkg)
    if os.path.exists(root):
        if not overwrite:
            raise FileExistsError(f"{root} exists (use --overwrite)")
        shutil.rmtree(root)

    onnx_src = onnx_path or c.onnx_path
    if not os.path.isfile(onnx_src):
        raise FileNotFoundError(f"ONNX file not found: {onnx_src}")
    onnx_name = os.path.basename(onnx_src)

    # contract copy: onnx path rewritten relative to the contract's own directory in share/
    raw = json.loads(json.dumps(c.raw))
    raw["policy"]["onnx"] = f"../policy/{onnx_name}"
    contract_name = f"{c.name}.json"
    _write(os.path.join(root, "contract", contract_name), json.dumps(raw, indent=2, ensure_ascii=False) + "\n")
    os.makedirs(os.path.join(root, "policy"), exist_ok=True)
    shutil.copyfile(onnx_src, os.path.join(root, "policy", onnx_name))

    node_src = resources.files("unirobolab.ros2").joinpath("policy_node.py").read_text(encoding="utf-8")
    _write(os.path.join(root, pkg, "__init__.py"), "")
    _write(os.path.join(root, pkg, "policy_node.py"), node_src, executable=True)
    _write(os.path.join(root, "resource", pkg), "")

    ns = c.ros.namespace if c.ros else ""
    _write(os.path.join(root, "package.xml"), f'''<?xml version="1.0"?>
<?xml-model href="http://download.ros.org/schema/package_format3.xsd" schematypens="http://www.w3.org/2001/XMLSchema"?>
<package format="3">
  <name>{pkg}</name>
  <version>0.0.1</version>
  <description>Policy "{c.name}" packaged by UniRoboLab from its contract. Runs the ONNX policy at {c.policy_rate_hz:g} Hz.</description>
  <maintainer email="user@example.com">unirobolab</maintainer>
  <license>Apache-2.0</license>

  <exec_depend>rclpy</exec_depend>
  <exec_depend>sensor_msgs</exec_depend>
  <exec_depend>std_msgs</exec_depend>
  <exec_depend>python3-numpy</exec_depend>
  <!-- onnxruntime has no rosdep key on every distro; install with pip if missing -->

  <export>
    <build_type>ament_python</build_type>
  </export>
</package>
''')
    _write(os.path.join(root, "setup.cfg"), f'''[develop]
script_dir=$base/lib/{pkg}
[install]
install_scripts=$base/lib/{pkg}
''')
    _write(os.path.join(root, "setup.py"), f'''from setuptools import find_packages, setup

package_name = "{pkg}"

setup(
    name=package_name,
    version="0.0.1",
    packages=find_packages(exclude=["test"]),
    data_files=[
        ("share/ament_index/resource_index/packages", ["resource/" + package_name]),
        ("share/" + package_name, ["package.xml"]),
        ("share/" + package_name + "/launch", ["launch/policy.launch.py"]),
        ("share/" + package_name + "/contract", ["contract/{contract_name}"]),
        ("share/" + package_name + "/policy", ["policy/{onnx_name}"]),
    ],
    install_requires=["setuptools", "numpy", "onnxruntime"],
    zip_safe=True,
    maintainer="unirobolab",
    maintainer_email="user@example.com",
    description="Policy {c.name} packaged by UniRoboLab",
    license="Apache-2.0",
    entry_points={{
        "console_scripts": [
            "policy_node = {pkg}.policy_node:main",
        ],
    }},
)
''')
    _write(os.path.join(root, "launch", "policy.launch.py"), f'''import os

from ament_index_python.packages import get_package_share_directory
from launch import LaunchDescription
from launch.actions import DeclareLaunchArgument
from launch.substitutions import LaunchConfiguration
from launch_ros.actions import Node


def generate_launch_description():
    share = get_package_share_directory("{pkg}")
    contract = os.path.join(share, "contract", "{contract_name}")
    return LaunchDescription([
        DeclareLaunchArgument("ns", default_value="{ns}",
                              description="robot namespace; overrides the contract's ros.namespace"),
        DeclareLaunchArgument("publish_debug", default_value="true"),
        Node(
            package="{pkg}",
            executable="policy_node",
            name="policy_node",
            output="screen",
            parameters=[{{
                "contract_path": contract,
                "namespace_override": LaunchConfiguration("ns"),
                "publish_debug": LaunchConfiguration("publish_debug"),
            }}],
        ),
    ])
''')
    _write(os.path.join(root, "README.md"), f'''# {pkg}

Generated by UniRoboLab from contract `{c.name}`. Do not edit by hand; regenerate with

```
unirobolab gen <contract.json> --out <dir>
```

- `contract/{contract_name}`: observation/action layout, joint order, rate, topics
- `policy/{onnx_name}`: the ONNX policy
- `{pkg}/policy_node.py`: the fixed runtime (same file for sim2sim and hardware)

```
ros2 launch {pkg} policy.launch.py ns:={ns or "<robot>"}
```

Needs `onnxruntime` in the ROS 2 Python environment (`pip install onnxruntime`).
''')
    return root
