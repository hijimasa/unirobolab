# UniRoboLab check container (step 5, sim2sim through ROS 2), self-contained.
#
# Starts from the official ros-base image and builds the few ROS 2 packages the check needs from
# source, pinned by commit: the ROS-TCP endpoint (hijimasa fork: __remove_subscriber and
# service fixes the simulator relies on), simulation_interfaces (the services the simulator
# implements), simulation_ros2_utils (spawn_entity / set_sim_state helpers plus
# simulation_extra_interfaces) and topic_based_ros2_control (deployment through ros2_control).
# The simulator itself is NOT in the image: the UniRoboLab player runs on the host and connects to
# the endpoint in this container (--net=host). No torch / stable-baselines3 either: training talks
# to the simulator directly and never goes through ROS 2.
#
#   docker build --network=host -t $(id -un)/unirobolab-jazzy --build-arg UID=$(id -u) --build-arg GID=$(id -g) docker/
# (scripts/sim2sim_container.sh build does this; start builds the image when it is missing)
ARG ROS_DISTRO_ARG=jazzy
FROM ros:${ROS_DISTRO_ARG}-ros-base

ARG USER=unity
ARG UID=1000
ARG GID=1000
# pinned sources (Unity_ROS2_sample's submodules at the time of writing)
ARG ROS_TCP_ENDPOINT_REV=6c683a32eb0454435391aa341b7cca2f4f5ae280
ARG SIMULATION_INTERFACES_REV=172a1b1c1b3ced62b99cddae9b4143e5bcddee8a
ARG SIMULATION_ROS2_UTILS_REV=34c2ba118f82998ebc9c756b205c13b1588a747f
ARG TOPIC_BASED_ROS2_CONTROL_REV=e273bce581800aa6c2022abe2eaaa76611c1fdc9

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
      git sudo python3-pip python3-colcon-common-extensions python3-rosdep \
      ros-${ROS_DISTRO}-xacro ros-${ROS_DISTRO}-ros2-control ros-${ROS_DISTRO}-ros2-controllers \
      ros-${ROS_DISTRO}-controller-manager ros-${ROS_DISTRO}-hardware-interface \
  && apt-get clean && rm -rf /var/lib/apt/lists/*

# the generated policy node and the sim2sim evaluator (python3 -m unirobolab, mounted from the host)
RUN pip3 install --no-cache-dir --break-system-packages "numpy<2" onnxruntime onnx jsonschema pyyaml

# a user with the host's uid/gid so files written into the mounted repository stay owned by the host user
RUN if getent passwd ${UID} >/dev/null && [ "$(getent passwd ${UID} | cut -d: -f1)" != "${USER}" ]; then userdel -r "$(getent passwd ${UID} | cut -d: -f1)" 2>/dev/null || true; fi \
  && if getent group ${GID} >/dev/null && [ "$(getent group ${GID} | cut -d: -f1)" != "${USER}" ]; then groupdel "$(getent group ${GID} | cut -d: -f1)" 2>/dev/null || true; fi \
  && groupadd --gid ${GID} ${USER} && useradd --gid ${GID} --uid ${UID} -ms /bin/bash ${USER} \
  && echo "${USER} ALL=(ALL) NOPASSWD: ALL" > /etc/sudoers.d/${USER} && chmod 0440 /etc/sudoers.d/${USER}
USER ${USER}
WORKDIR /home/${USER}/colcon_ws

RUN mkdir -p src && cd src \
  && git clone -q https://github.com/hijimasa/ROS-TCP-Endpoint.git && git -C ROS-TCP-Endpoint checkout -q ${ROS_TCP_ENDPOINT_REV} \
  && git clone -q https://github.com/ros-simulation/simulation_interfaces.git && git -C simulation_interfaces checkout -q ${SIMULATION_INTERFACES_REV} \
  && git clone -q https://github.com/REACT-ROBOT/simulation_ros2_utils.git && git -C simulation_ros2_utils checkout -q ${SIMULATION_ROS2_UTILS_REV} \
  && git clone -q https://github.com/hijimasa/topic_based_ros2_control.git && git -C topic_based_ros2_control checkout -q ${TOPIC_BASED_ROS2_CONTROL_REV} \
  && rm -rf */.git
RUN sudo apt-get update && (sudo rosdep init 2>/dev/null || true) && rosdep update --rosdistro ${ROS_DISTRO} \
  && rosdep install --from-paths src --ignore-src -r -y --rosdistro ${ROS_DISTRO} \
  && sudo apt-get clean && sudo rm -rf /var/lib/apt/lists/*
RUN bash -c "source /opt/ros/${ROS_DISTRO}/setup.bash && colcon build --cmake-args -DCMAKE_BUILD_TYPE=Release" \
  && rm -rf build log

# FastDDS over UDP only: killed nodes leak /dev/shm segments that break discovery in long check loops
COPY fastdds_udp_only.xml /home/${USER}/colcon_ws/scripts/fastdds_udp_only.xml
ENV FASTRTPS_DEFAULT_PROFILES_FILE=/home/${USER}/colcon_ws/scripts/fastdds_udp_only.xml
RUN echo "source /opt/ros/${ROS_DISTRO}/setup.bash" >> ~/.bashrc && echo "source ~/colcon_ws/install/setup.sh" >> ~/.bashrc
ENTRYPOINT ["/bin/bash"]
