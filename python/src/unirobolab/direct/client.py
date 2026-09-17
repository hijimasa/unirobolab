"""Client for the simulator's learning server (SimulationLearningServer.cs).

One TCP connection, one request in flight. Protocol (little-endian, one frame =
uint32 length + payload):

  request : uint8 op (1=INFO, 2=RESET, 3=STEP, 4=PING, 5=PAUSE, 6=SPAWN)
            SPAWN: string name, string urdf_path, f64 x, y, z, yaw -> status + string entity_name
            uint16 n, n x string (uint16 len + UTF-8)                 entity names
            STEP only: uint32 steps, n x { uint16 m, m x { string joint, f32 pos, vel, eff } }
                       (NaN = leave that component untouched)
  response: uint8 status (0 = OK), else string error
            OK: uint16 n, n x { uint16 k, k x { string joint, f64 pos, vel, eff },
                              f64 x 13 base state: pos xyz, quat xyzw, lin vel xyz, ang vel xyz
                              (world frame, ROS axes) },
                f64 sim_time (physics time)
  RESET puts joints at zero and the base back at its spawn pose.
"""

from __future__ import annotations

import socket
import struct
from dataclasses import dataclass

import numpy as np

OP_INFO, OP_RESET, OP_STEP, OP_PING, OP_PAUSE, OP_SPAWN, OP_PLAY, OP_SET_POSE = 1, 2, 3, 4, 5, 6, 7, 8


class LearningServerError(RuntimeError):
    pass


@dataclass
class EntityState:
    names: list[str]
    position: np.ndarray      # joint positions, contract-independent order (as reported)
    velocity: np.ndarray
    effort: np.ndarray
    base_pos: np.ndarray      # world, ROS axes (x forward, y left, z up)
    base_quat: np.ndarray     # x, y, z, w
    base_lin_vel: np.ndarray  # world frame
    base_ang_vel: np.ndarray  # world frame


class LearningClient:
    def __init__(self, host: str = "127.0.0.1", port: int = 10100, timeout_s: float = 30.0) -> None:
        self.sock = socket.create_connection((host, port), timeout=timeout_s)
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass

    # ------------------------------------------------------------ framing
    @staticmethod
    def _s(s: str) -> bytes:
        b = s.encode("utf-8")
        return struct.pack("<H", len(b)) + b

    def _rpc(self, payload: bytes) -> bytes:
        self.sock.sendall(struct.pack("<I", len(payload)) + payload)
        hdr = self._recv(4)
        (n,) = struct.unpack("<I", hdr)
        return self._recv(n)

    def _recv(self, n: int) -> bytes:
        buf = bytearray()
        while len(buf) < n:
            chunk = self.sock.recv(n - len(buf))
            if not chunk:
                raise LearningServerError("connection closed by simulator")
            buf += chunk
        return bytes(buf)

    @staticmethod
    def _parse_states(resp: bytes) -> tuple[list[EntityState], float]:
        off = 0
        status = resp[off]; off += 1
        if status != 0:
            (ln,) = struct.unpack_from("<H", resp, off); off += 2
            raise LearningServerError(resp[off:off + ln].decode("utf-8"))
        (n,) = struct.unpack_from("<H", resp, off); off += 2
        out = []
        for _ in range(n):
            (k,) = struct.unpack_from("<H", resp, off); off += 2
            names, pos, vel, eff = [], [], [], []
            for _ in range(k):
                (ln,) = struct.unpack_from("<H", resp, off); off += 2
                names.append(resp[off:off + ln].decode("utf-8")); off += ln
                p, v, e = struct.unpack_from("<ddd", resp, off); off += 24
                pos.append(p); vel.append(v); eff.append(e)
            base = np.asarray(struct.unpack_from("<13d", resp, off)); off += 13 * 8
            out.append(EntityState(names, np.asarray(pos), np.asarray(vel), np.asarray(eff),
                                   base[0:3], base[3:7], base[7:10], base[10:13]))
        (sim_time,) = struct.unpack_from("<d", resp, off)
        return out, sim_time

    # ----------------------------------------------------------------- ops
    def ping(self) -> None:
        r = self._rpc(bytes([OP_PING]))
        if r != b"\x00":
            raise LearningServerError("bad ping response")

    def pause(self) -> None:
        """Same transition as set_simulation_state(PAUSED); stepping needs it."""
        self._parse_states(self._rpc(bytes([OP_PAUSE]) + struct.pack("<H", 0)))

    def play(self) -> None:
        """Same transition as set_simulation_state(PLAYING): real time, for live policy runs."""
        self._parse_states(self._rpc(bytes([OP_PLAY]) + struct.pack("<H", 0)))

    def observe(self, entities: list[str], commands: list[dict | None] | None = None):
        """Apply commands (optional) and read states without stepping; allowed while playing."""
        return self.step(entities, 0, commands if commands is not None else [None] * len(entities))

    def spawn(self, name: str, urdf_path: str, x: float = 0.0, y: float = 0.0, z: float = 0.0,
              yaw: float = 0.0) -> str:
        """Spawn a URDF (path as seen by the simulator process). Returns the entity name."""
        payload = bytes([OP_SPAWN]) + self._s(name) + self._s(urdf_path) + struct.pack("<dddd", x, y, z, yaw)
        resp = self._rpc(payload)
        if resp[0] != 0:
            (ln,) = struct.unpack_from("<H", resp, 1)
            raise LearningServerError(resp[3:3 + ln].decode("utf-8"))
        (ln,) = struct.unpack_from("<H", resp, 1)
        return resp[3:3 + ln].decode("utf-8")

    def has_entities(self, entities: list[str]) -> list[bool]:
        """Which of the names exist (INFO one by one; a missing one raises)."""
        out = []
        for e in entities:
            try:
                self.info([e]); out.append(True)
            except LearningServerError:
                out.append(False)
        return out

    def info(self, entities: list[str]) -> list[EntityState]:
        payload = bytes([OP_INFO]) + struct.pack("<H", len(entities)) + b"".join(self._s(e) for e in entities)
        return self._parse_states(self._rpc(payload))[0]

    def set_pose(self, entity: str, x: float, y: float, z: float, yaw: float) -> EntityState:
        """エンティティの基体を (x, y, z, yaw) [ROS 座標, rad] へ置き直し、速度を 0 にする。以後の RESET もここへ戻る。"""
        payload = bytes([OP_SET_POSE]) + self._s(entity) + struct.pack("<dddd", x, y, z, yaw)
        states, _ = self._parse_states(self._rpc(payload))
        return states[0]

    def reset(self, entities: list[str]) -> tuple[list[EntityState], float]:
        payload = bytes([OP_RESET]) + struct.pack("<H", len(entities)) + b"".join(self._s(e) for e in entities)
        return self._parse_states(self._rpc(payload))

    def step(self, entities: list[str], steps: int,
             commands: list[dict[str, np.ndarray] | None]) -> tuple[list[EntityState], float]:
        """commands[i] = {"name": [...], "position"?: arr, "velocity"?: arr, "effort"?: arr} or None."""
        parts = [bytes([OP_STEP]), struct.pack("<H", len(entities))]
        parts += [self._s(e) for e in entities]
        parts.append(struct.pack("<I", int(steps)))
        nan = float("nan")
        for cmd in commands:
            if not cmd:
                parts.append(struct.pack("<H", 0))
                continue
            names = list(cmd["name"])
            parts.append(struct.pack("<H", len(names)))
            pos = cmd.get("position"); vel = cmd.get("velocity"); eff = cmd.get("effort")
            for j, nm in enumerate(names):
                parts.append(self._s(nm))
                parts.append(struct.pack("<fff",
                                         float(pos[j]) if pos is not None else nan,
                                         float(vel[j]) if vel is not None else nan,
                                         float(eff[j]) if eff is not None else nan))
        return self._parse_states(self._rpc(b"".join(parts)))
