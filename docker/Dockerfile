# UniRoboLab sim2sim / training image.
# Derives from the Unity_ROS2_sample container (ROS 2 + the simulator binary) and adds
# what the generated policy node and the training backend need. CPU-only torch keeps the
# image small; the policies here are tiny MLPs and the simulator is the bottleneck.
#
#   docker build --network=host -t $(id -un)/unirobolab-jazzy --build-arg BASE=$(id -un)/ros-jazzy-noble-unity-sample docker/
# (--network=host: the build sandbox has no DNS on this machine)
ARG BASE
FROM ${BASE}
USER root
# numpy/setuptools come from Debian (no RECORD file, pip cannot upgrade them) and ROS
# tooling depends on that setuptools, so pin both to what the base image has.
RUN pip3 install --no-cache-dir --break-system-packages \
      "numpy<2" "setuptools<69" \
      onnxruntime onnx jsonschema \
      gymnasium "stable-baselines3>=2.3" matplotlib \
      --extra-index-url https://download.pytorch.org/whl/cpu torch \
  && rm -rf /root/.cache/pip
USER unity
