"""Unity SAR kinematic bridge for the TestArea AirSim map.

Unity is the motion master. This process:

* listens for binary pose frames on UDP 49001 (latest frame wins)
* creates one AirSim simpleFlight vehicle per Unity aircraft
* glides each mesh along the Unity pose and writes that glide velocity, so
  the airframe does not jump between 30 Hz packets or fall between them
* drives ExternalCamera in world-NED: B chase, H nose, "/" a closer chase
* moves SensorCam with the aircraft currently in view, so sub-windows 1/2/3
  show that aircraft

AirSim must start with "ViewMode": "Manual" in settings.json.  In every other
view mode ACameraDirector::Tick lerps the camera towards the aircraft on every
frame (position and rotation never converge) — that is what produced the
on-screen shake.  In Manual mode CameraDirector only applies key deltas, which
are zero when nobody touches the keyboard, so the camera stays exactly where
this bridge puts it.  Pressing B, H or "/" also taps M so the director returns
to Manual after AirSim handles the same key.

Keys while the UE window has focus:

    B          chase view, aircraft centred
    H          nose view, horizon level
    /          closer chase
    N          cycle the followed aircraft
    J / L      distance closer / farther
    U / O      height higher / lower
    M          free flight (UE manual keys)
    1 / 2 / 3  depth, segmentation and scene of the followed aircraft

In free mode the bridge stops touching the camera and the operator flies it
with UE's native Manual-mode keys (arrows = move, PageUp/PageDown = up/down,
A/D = yaw, W/S = pitch, Q/E = roll, Shift/Ctrl = speed).

API control is deliberately never enabled: SimpleFlight's offboard API enters
hover mode when it stops receiving goals and then applies forces on every
physics step, which fights the pose writes.

Every frame is written to bridge_diag.csv (raw Unity pose, NED conversion,
computed camera pose, AirSim readback) for offline jitter analysis.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import socket
import struct
import subprocess
import sys
import threading
import time
from collections import deque
from pathlib import Path
from typing import Deque, Dict, List, Optional, Tuple

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import airsim  # noqa: E402

from airsim_bridge.coords import unity_quat_to_ned, unity_to_ned  # noqa: E402

MAGIC = b"ITS1"
HEADER = struct.Struct("<4sBHI")  # magic, version, count, seq
FLOATS = struct.Struct("<10f")

DEFAULT_BIND = "127.0.0.1"
DEFAULT_PORT = 49001
CHASE_CAM = "ChaseCam"            # FPV vehicle (IsFpvVehicle in settings.json)
EXTERNAL_CAM = "ExternalCamera"   # the actor shown in Manual view mode
SENSOR_CAM_ACTORS = ("external_SensorCam", "SensorCam")


def _env_path(name: str, default: str) -> Path:
    """Read a path from the environment, falling back to a documented default.

    The Unreal host is machine-specific, so nothing here is hard-coded to one
    installation: set UE4EDITOR_EXE and UE4_PROJECT to point at your own copy.
    With neither variable set, the bridge still connects to an AirSim that is
    already running; only --launch-airsim needs them.
    """
    return Path(os.environ.get(name, default)).expanduser()


UE4_EDITOR = _env_path("UE4EDITOR_EXE",
                       r"C:\Program Files\Epic Games\UE_4.27\Engine\Binaries\Win64\UE4Editor.exe")
UE4_PROJECT = _env_path("UE4_PROJECT",
                        r"C:\Users\Public\Documents\Unreal Projects\SZ_TestArea\SZ_TestArea.uproject")
FOLLOW_PATH = _env_path("SAR_FOLLOW_CAMERA",
                        str(Path.home() / "Documents" / "AirSim" / "follow_camera.json"))

DEFAULT_FOLLOW = {
    "target": "",
    "offset_right_m": 0.0,
    "offset_up_m": 8.0,
    "offset_forward_m": -22.0,
    "extra_pitch_deg": -8.0,
    "extra_yaw_deg": 0.0,
    "free_mode": False,
    "view": "chase",
    "chase_back": 8.0,
    "chase_up": 1.8,
    "spring_back": 6.0,
    "spring_up": 3.5,
    "nose_ahead": 1.8,
    "nose_up": 0.35,
}

DIAG_EVERY_N = 30       # log detailed diagnostics every N frames
DIAG_WINDOW = 60        # keep this many frames of per-vehicle history
JITTER_THRESHOLD_MM = 5  # flag frame-to-frame deltas above this as suspicious


def _pose(north: float, east: float, down: float, qx=0.0, qy=0.0, qz=0.0, qw=1.0) -> airsim.Pose:
    return airsim.Pose(airsim.Vector3r(north, east, down), airsim.Quaternionr(qx, qy, qz, qw))


def _rotate_ned(qx: float, qy: float, qz: float, qw: float, x: float, y: float, z: float) -> Tuple[float, float, float]:
    """Rotate vector (x,y,z) by NED quaternion (qx,qy,qz,qw).  Verified correct."""
    tx = 2.0 * (qy * z - qz * y)
    ty = 2.0 * (qz * x - qx * z)
    tz = 2.0 * (qx * y - qy * x)
    return (
        x + qw * tx + (qy * tz - qz * ty),
        y + qw * ty + (qz * tx - qx * tz),
        z + qw * tz + (qx * ty - qy * tx),
    )


# ---------------------------------------------------------------------------
# follow-camera persistence
# ---------------------------------------------------------------------------

def load_follow() -> dict:
    if not FOLLOW_PATH.exists():
        FOLLOW_PATH.parent.mkdir(parents=True, exist_ok=True)
        FOLLOW_PATH.write_text(json.dumps(DEFAULT_FOLLOW, indent=2), encoding="utf-8")
        return dict(DEFAULT_FOLLOW)
    try:
        data = json.loads(FOLLOW_PATH.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return dict(DEFAULT_FOLLOW)
    merged = dict(DEFAULT_FOLLOW)
    merged.update({k: data[k] for k in DEFAULT_FOLLOW if k in data})
    return merged


def save_follow(follow: dict) -> None:
    FOLLOW_PATH.write_text(json.dumps(follow, indent=2), encoding="utf-8")


# ---------------------------------------------------------------------------
# AirSim lifecycle
# ---------------------------------------------------------------------------

def rpc_open(timeout: float = 0.4) -> bool:
    try:
        with socket.create_connection(("127.0.0.1", 41451), timeout):
            return True
    except OSError:
        return False


def launch_airsim() -> None:
    if not UE4_EDITOR.exists() or not UE4_PROJECT.exists():
        print("AirSim host is not ready.")
        print("  UE4 editor : %s%s" % (UE4_EDITOR, "" if UE4_EDITOR.exists() else "  (not found)"))
        print("  UE4 project: %s%s" % (UE4_PROJECT, "" if UE4_PROJECT.exists() else "  (not found)"))
        print("Set UE4EDITOR_EXE and UE4_PROJECT, or start the packaged AirSim host yourself.")
        return
    print(f"Launching {UE4_PROJECT.name}")
    subprocess.Popen(
        [str(UE4_EDITOR), str(UE4_PROJECT), "/Game/Maps/TestArea", "-game", "-windowed", "-ResX=1600", "-ResY=900", "-log"],
        cwd=str(UE4_PROJECT.parent),
    )


def connect(launch: bool) -> airsim.MultirotorClient:
    launched = False
    announced = False
    while True:
        if rpc_open():
            try:
                client = airsim.MultirotorClient()
                client.confirmConnection()
                return client
            except Exception:
                pass
        elif launch and not launched:
            launch_airsim()
            launched = True
        if not announced:
            print("waiting for AirSim RPC 41451")
            announced = True
        time.sleep(1.0)


# ---------------------------------------------------------------------------
# UDP frame parsing
# ---------------------------------------------------------------------------

def parse_frame(packet: bytes) -> Optional[Tuple[int, List[dict]]]:
    if len(packet) < HEADER.size:
        return None
    magic, version, count, seq = HEADER.unpack_from(packet, 0)
    if magic != MAGIC or version != 1:
        return None
    offset = HEADER.size
    vehicles = []
    for _ in range(count):
        if offset >= len(packet):
            return None
        name_len = packet[offset]
        offset += 1
        if offset + name_len + FLOATS.size > len(packet):
            return None
        name = packet[offset : offset + name_len].decode("utf-8", "replace")
        offset += name_len
        px, py, pz, qx, qy, qz, qw, vx, vy, vz = FLOATS.unpack_from(packet, offset)
        offset += FLOATS.size
        vehicles.append({
            "id": name,
            "pos": (px, py, pz),
            "quat": (qx, qy, qz, qw),
            "vel": (vx, vy, vz),
        })
    return seq, vehicles


# ---------------------------------------------------------------------------
# vehicle management  (no arming, no API control — flight controllers stay off)
# ---------------------------------------------------------------------------

def ensure_vehicle(client: airsim.MultirotorClient, name: str, pose: airsim.Pose, known: Dict[str, bool]) -> None:
    if name in known:
        return
    try:
        existing = set(client.listVehicles())
    except Exception:
        existing = set()
    if name not in existing:
        ok = False
        try:
            ok = bool(client.simAddVehicle(name, "simpleflight", pose, ""))
        except Exception as exc:
            print(f"simAddVehicle {name} failed: {exc}")
        if not ok and name not in existing:
            print(f"could not create AirSim vehicle {name}")
            return
        print(f"created AirSim vehicle {name}")
    # enableApiControl is deliberately never called: SimpleFlight's offboard
    # API starts applying hover forces to the vehicle once goals stop arriving,
    # and those forces fight the per-frame teleports (that is the residual
    # shake the CSV showed even with gravity off).
    known[name] = True


# ---------------------------------------------------------------------------
# pose pinning  (teleport + kinematic mode to defeat gravity)
# ---------------------------------------------------------------------------

def pin_pose(client: airsim.MultirotorClient, name: str, pose: airsim.Pose) -> Optional[airsim.Pose]:
    """Teleport the pawn to a world-NED pose and zero its kinematics.

    simSetVehiclePose places the pawn (ETeleportType::TeleportPhysics);
    simSetKinematics then overwrites the AirLib physics state with zero
    velocity/acceleration so nothing accumulates between the 30 Hz teleports.

    Returns the actual pose read back from AirSim.
    """
    try:
        client.simSetVehiclePose(pose, True, name)
        state = client.simGetGroundTruthKinematics(name)
        state.linear_velocity = airsim.Vector3r(0.0, 0.0, 0.0)
        state.angular_velocity = airsim.Vector3r(0.0, 0.0, 0.0)
        state.linear_acceleration = airsim.Vector3r(0.0, 0.0, 0.0)
        state.angular_acceleration = airsim.Vector3r(0.0, 0.0, 0.0)
        client.simSetKinematics(state, True, name)
        actual = client.simGetVehiclePose(name)
        return actual
    except Exception:
        return None


def yaw_from_ned_quat(qx: float, qy: float, qz: float, qw: float) -> float:
    """Yaw in radians, NED, from north toward east."""
    siny = 2.0 * (qw * qz + qx * qy)
    cosy = 1.0 - 2.0 * (qy * qy + qz * qz)
    return math.atan2(siny, cosy)


class PoseTrack:
    """Follow the latest Unity pose through a short low-pass.

    A new packet used to move the mesh by the whole distance flown since the
    previous one. Waiting has no distance, so it looked steady. Moving made
    the mesh jump, and a closer or higher chase camera turns that jump into
    a visible shake.
    """

    def __init__(self, tau: float = 0.06) -> None:
        self.target: Optional[airsim.Pose] = None
        self.shown: Optional[airsim.Pose] = None
        self.last = 0.0
        self.tau = tau
        self.velocity = (0.0, 0.0, 0.0)

    def push(self, t: float, pose: airsim.Pose) -> None:
        del t
        self.target = pose

    def sample(self, t: float) -> Optional[airsim.Pose]:
        if self.target is None:
            return None
        if self.shown is None:
            self.shown = self.target
            self.last = t
            return self.shown
        if t <= self.last:
            return self.shown
        dt = min(0.05, max(1e-3, t - self.last))
        self.last = t
        alpha = 1.0 - math.exp(-dt / self.tau)
        a, b = self.shown, self.target
        orientation = _slerp(a.orientation, b.orientation, alpha)
        shown = _pose(
            _lerp(a.position.x_val, b.position.x_val, alpha),
            _lerp(a.position.y_val, b.position.y_val, alpha),
            _lerp(a.position.z_val, b.position.z_val, alpha),
            orientation.x_val, orientation.y_val, orientation.z_val, orientation.w_val,
        )
        self.velocity = (
            (shown.position.x_val - a.position.x_val) / dt,
            (shown.position.y_val - a.position.y_val) / dt,
            (shown.position.z_val - a.position.z_val) / dt,
        )
        horiz = math.hypot(self.velocity[0], self.velocity[1])
        if horiz > 40.0:
            scale = 40.0 / horiz
            self.velocity = (self.velocity[0] * scale, self.velocity[1] * scale, self.velocity[2] * scale)
        self.shown = shown
        return self.shown


def pin_pose_fast(client: airsim.MultirotorClient, name: str, pose: airsim.Pose,
                  velocity: Tuple[float, float, float] = (0.0, 0.0, 0.0)) -> None:
    """Place the pawn and keep the glide velocity.

    Zero velocity holds the mesh still until the next pin, then it jumps by the
    distance flown. That jump is the shake. The velocity is the glide itself,
    so the physics step continues the same motion instead of being yanked back.
    """
    state = airsim.KinematicsState()
    state.position = pose.position
    state.orientation = pose.orientation
    state.linear_velocity = airsim.Vector3r(velocity[0], velocity[1], velocity[2])
    state.angular_velocity = airsim.Vector3r()
    state.linear_acceleration = airsim.Vector3r()
    state.angular_acceleration = airsim.Vector3r()
    client.simSetKinematics(state, True, name)
    client.simSetVehiclePose(pose, True, name)


def place_sensor_camera(client: airsim.MultirotorClient, vehicle: airsim.Pose) -> None:
    """Put the sub-window camera on the aircraft currently in view.

    Keys 1, 2 and 3 read this one camera. Moving it with the selected drone
    makes those pictures follow whichever aircraft is on screen.
    """
    yaw = yaw_from_ned_quat(
        vehicle.orientation.x_val, vehicle.orientation.y_val,
        vehicle.orientation.z_val, vehicle.orientation.w_val)
    quat = airsim.to_quaternion(0.0, 0.0, yaw)
    pose = _pose(
        vehicle.position.x_val + math.cos(yaw) * 1.6,
        vehicle.position.y_val + math.sin(yaw) * 1.6,
        vehicle.position.z_val - 0.25,
        quat.x_val, quat.y_val, quat.z_val, quat.w_val,
    )
    for name in SENSOR_CAM_ACTORS:
        try:
            if client.simSetObjectPose(name, pose, True):
                return
        except Exception as exc:
            if not place_sensor_camera.warned:
                place_sensor_camera.warned = True
                print(f"sensor camera {name} failed: {exc}")
            return
    if not place_sensor_camera.warned:
        place_sensor_camera.warned = True
        print("sensor camera was not found; sub-windows stay on the startup drone")


place_sensor_camera.warned = False


def apply_vehicle(client: airsim.MultirotorClient, name: str, msg: dict) -> Tuple[airsim.Pose, Optional[airsim.Pose]]:
    px, py, pz = msg["pos"]
    qx, qy, qz, qw = msg["quat"]
    n, e, d = unity_to_ned(px, py, pz)
    nqx, nqy, nqz, nqw = unity_quat_to_ned(qx, qy, qz, qw)
    pose = _pose(n, e, d, nqx, nqy, nqz, nqw)
    actual = pin_pose(client, name, pose)
    return pose, actual


def apply_follow(client: airsim.MultirotorClient, pose: airsim.Pose) -> Optional[airsim.Pose]:
    """Teleport the view camera to a world-NED pose.

    In Manual view mode AirSim renders through the "ExternalCamera" actor, a
    plain scene actor with no physics, so simSetObjectPose moves it exactly.
    ChaseCam (the FPV vehicle) is placed at the same pose so pressing F shows
    the same viewpoint.  Returns the ExternalCamera pose read back from AirSim.
    """
    try:
        ok = client.simSetObjectPose(EXTERNAL_CAM, pose, True)
        if not ok and not apply_follow.warned:
            apply_follow.warned = True
            print(f"WARNING: AirSim does not know an object named '{EXTERNAL_CAM}' — "
                  "camera will not move. Check simListSceneObjects.")
    except Exception as exc:
        print(f"simSetObjectPose({EXTERNAL_CAM}) failed: {exc}")
        return None
    pin_pose(client, CHASE_CAM, pose)
    try:
        return client.simGetObjectPose(EXTERNAL_CAM)
    except Exception:
        return None


apply_follow.warned = False  # one-shot warning flag


# ---------------------------------------------------------------------------
# camera math
# ---------------------------------------------------------------------------

def look_pose(vehicle: airsim.Pose, follow: dict) -> airsim.Pose:
    qx = vehicle.orientation.x_val
    qy = vehicle.orientation.y_val
    qz = vehicle.orientation.z_val
    qw = vehicle.orientation.w_val
    forward = float(follow["offset_forward_m"])
    right = float(follow["offset_right_m"])
    up = float(follow["offset_up_m"])
    ox, oy, oz = _rotate_ned(qx, qy, qz, qw, forward, right, -up)
    cx = vehicle.position.x_val + ox
    cy = vehicle.position.y_val + oy
    cz = vehicle.position.z_val + oz
    dx = vehicle.position.x_val - cx
    dy = vehicle.position.y_val - cy
    dz = vehicle.position.z_val - cz
    horiz = math.hypot(dx, dy)
    yaw = math.atan2(dy, dx) + math.radians(float(follow["extra_yaw_deg"]))
    pitch = math.atan2(-dz, max(horiz, 1e-4)) + math.radians(float(follow["extra_pitch_deg"]))
    return _pose(cx, cy, cz, *quat_xyzw(airsim.to_quaternion(pitch, 0.0, yaw)))


def nose_pose(vehicle: airsim.Pose, ahead: float = 2.2, above: float = 0.35) -> airsim.Pose:
    """Onboard view just ahead of the nose. Roll and pitch of the airframe are not copied.

    An actor pitch of 0 looks above the horizon on this camera, so the pitch
    here holds the horizon near the middle of the picture.
    """
    yaw = yaw_from_ned_quat(
        vehicle.orientation.x_val, vehicle.orientation.y_val,
        vehicle.orientation.z_val, vehicle.orientation.w_val)
    quat = airsim.to_quaternion(-0.12, 0.0, yaw)
    return _pose(
        vehicle.position.x_val + math.cos(yaw) * ahead,
        vehicle.position.y_val + math.sin(yaw) * ahead,
        vehicle.position.z_val - above,
        quat.x_val, quat.y_val, quat.z_val, quat.w_val)


def chase_pose(vehicle: airsim.Pose, back: float, above: float) -> airsim.Pose:
    """Behind and above the airframe, with the lens pitched onto the model.

    The extra pitch and yaw put the aircraft in the middle of the picture.
    The camera actor's lens looks above its forward axis, so a pure look-at
    left the airframe in the lower third.
    """
    yaw = yaw_from_ned_quat(
        vehicle.orientation.x_val, vehicle.orientation.y_val,
        vehicle.orientation.z_val, vehicle.orientation.w_val)
    aim = math.atan2(max(above, 0.4), max(back, 1.0))
    quat = airsim.to_quaternion(-aim - 0.22, 0.0, yaw + 0.32)
    return _pose(
        vehicle.position.x_val - math.cos(yaw) * back,
        vehicle.position.y_val - math.sin(yaw) * back,
        vehicle.position.z_val - above,
        quat.x_val, quat.y_val, quat.z_val, quat.w_val)


def place_window_camera(client: airsim.MultirotorClient, pose: airsim.Pose) -> None:
    try:
        client.simSetObjectPose(EXTERNAL_CAM, pose, True)
    except Exception as exc:
        if not place_window_camera.warned:
            place_window_camera.warned = True
            print(f"simSetObjectPose({EXTERNAL_CAM}) failed: {exc}")


place_window_camera.warned = False


def quat_xyzw(q: airsim.Quaternionr) -> Tuple[float, float, float, float]:
    return q.x_val, q.y_val, q.z_val, q.w_val


# ---------------------------------------------------------------------------
# exponential smoother  (matches Unity DroneFollowCamera time constants)
# ---------------------------------------------------------------------------

class PoseSmoother:
    """Same speeds as Unity DroneFollowCamera: position 5, rotation 10."""

    def __init__(self) -> None:
        self.pose: Optional[airsim.Pose] = None
        self.target_name = ""
        self.last_time = 0.0

    def reset(self, name: str) -> None:
        self.pose = None
        self.target_name = name

    def step(self, name: str, target: airsim.Pose, now: float) -> airsim.Pose:
        if name != self.target_name:
            self.reset(name)
        if self.pose is None:
            self.pose = target
            self.last_time = now
            return target
        dt = min(0.1, max(0.0, now - self.last_time))
        self.last_time = now
        pos_alpha = 1.0 - math.exp(-5.0 * dt)
        rot_alpha = 1.0 - math.exp(-10.0 * dt)
        position = airsim.Vector3r(
            _lerp(self.pose.position.x_val, target.position.x_val, pos_alpha),
            _lerp(self.pose.position.y_val, target.position.y_val, pos_alpha),
            _lerp(self.pose.position.z_val, target.position.z_val, pos_alpha),
        )
        orientation = _slerp(self.pose.orientation, target.orientation, rot_alpha)
        self.pose = airsim.Pose(position, orientation)
        return self.pose


def _lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def _slerp(a: airsim.Quaternionr, b: airsim.Quaternionr, t: float) -> airsim.Quaternionr:
    dot = a.x_val * b.x_val + a.y_val * b.y_val + a.z_val * b.z_val + a.w_val * b.w_val
    bx, by, bz, bw = b.x_val, b.y_val, b.z_val, b.w_val
    if dot < 0.0:
        bx, by, bz, bw = -bx, -by, -bz, -bw
        dot = -dot
    if dot > 0.9995:
        x = _lerp(a.x_val, bx, t)
        y = _lerp(a.y_val, by, t)
        z = _lerp(a.z_val, bz, t)
        w = _lerp(a.w_val, bw, t)
    else:
        theta = math.acos(max(-1.0, min(1.0, dot)))
        sin_theta = math.sin(theta)
        w0 = math.sin((1.0 - t) * theta) / sin_theta
        w1 = math.sin(t * theta) / sin_theta
        x = w0 * a.x_val + w1 * bx
        y = w0 * a.y_val + w1 * by
        z = w0 * a.z_val + w1 * bz
        w = w0 * a.w_val + w1 * bw
    length = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    return airsim.Quaternionr(x / length, y / length, z / length, w / length)


# ---------------------------------------------------------------------------
# jitter detector — tracks position / orientation deltas per vehicle
# ---------------------------------------------------------------------------

class JitterDetector:
    """Accumulates frame-to-frame position / orientation deltas per vehicle
    and periodically prints a summary so you can see WHERE jitter appears."""

    def __init__(self) -> None:
        self.history: Dict[str, Deque[dict]] = {}   # vehicle name -> last N frames
        self.cam_history: Deque[dict] = deque(maxlen=DIAG_WINDOW)
        self.frame_seq: List[int] = []

    def feed_vehicle(self, name: str, unity_pos: Tuple[float, float, float],
                     unity_quat: Tuple[float, float, float, float],
                     ned_pos: Tuple[float, float, float],
                     ned_quat: Tuple[float, float, float, float]) -> None:
        if name not in self.history:
            self.history[name] = deque(maxlen=DIAG_WINDOW)
        self.history[name].append({
            "u_pos": unity_pos, "u_quat": unity_quat,
            "n_pos": ned_pos, "n_quat": ned_quat,
        })

    def feed_camera(self, target_name: str,
                    tgt_pos: Tuple[float, float, float],
                    desired_pos: Tuple[float, float, float],
                    smoothed_pos: Tuple[float, float, float],
                    applied_pos: Optional[Tuple[float, float, float]]) -> None:
        self.cam_history.append({
            "target": target_name,
            "tgt": tgt_pos, "des": desired_pos,
            "smo": smoothed_pos, "app": applied_pos,
        })

    def dump(self, frame_no: int) -> None:
        """Print a diagnostic snapshot for every tracked vehicle and the camera."""
        if not self.history:
            return
        print(f"\n{'='*70}")
        print(f"DIAGNOSTIC  frame #{frame_no}  vehicles={len(self.history)}")
        print(f"{'='*70}")

        for name, hist in sorted(self.history.items()):
            if len(hist) < 3:
                continue
            # --- vehicle position deltas (mm) ---
            deltas_mm = []
            for i in range(1, len(hist)):
                prev = hist[i - 1]["n_pos"]
                cur = hist[i]["n_pos"]
                d = math.sqrt((cur[0] - prev[0]) ** 2 + (cur[1] - prev[1]) ** 2 + (cur[2] - prev[2]) ** 2) * 1000.0
                deltas_mm.append(d)

            avg_d = sum(deltas_mm) / len(deltas_mm)
            max_d = max(deltas_mm)
            min_d = min(deltas_mm)
            osc = max_d - min_d  # oscillation amplitude

            # detect high-frequency oscillation: alternating large/small deltas
            alternating = 0
            for i in range(2, len(deltas_mm)):
                if abs(deltas_mm[i] - deltas_mm[i - 2]) < 1.0 and abs(deltas_mm[i] - deltas_mm[i - 1]) > JITTER_THRESHOLD_MM:
                    alternating += 1

            flag = " *** JITTER ***" if (osc > JITTER_THRESHOLD_MM * 3 and alternating > 3) else ""
            print(f"  [{name}] pos-delta mm: avg={avg_d:.1f}  min={min_d:.1f}  max={max_d:.1f}  "
                  f"osc={osc:.1f}  alt={alternating}{flag}")

            # --- unity raw pos ---
            last = hist[-1]
            u_pos = last["u_pos"]
            u_quat = last["u_quat"]
            print(f"          unity raw: pos=({u_pos[0]:.3f},{u_pos[1]:.3f},{u_pos[2]:.3f})  "
                  f"quat=({u_quat[0]:.4f},{u_quat[1]:.4f},{u_quat[2]:.4f},{u_quat[3]:.4f})")
            # --- NED converted ---
            n_pos = last["n_pos"]
            n_quat = last["n_quat"]
            print(f"          NED conv:   pos=({n_pos[0]:.3f},{n_pos[1]:.3f},{n_pos[2]:.3f})  "
                  f"quat=({n_quat[0]:.4f},{n_quat[1]:.4f},{n_quat[2]:.4f},{n_quat[3]:.4f})")

            # --- yaw in degrees ---
            yaw = math.degrees(math.atan2(
                2.0 * (n_quat[3] * n_quat[2] + n_quat[0] * n_quat[1]),
                1.0 - 2.0 * (n_quat[1] * n_quat[1] + n_quat[2] * n_quat[2])))
            print(f"          NED yaw (deg): {yaw:.2f}")

        # --- camera pipeline ---
        if len(self.cam_history) >= 3:
            print(f"  --- camera pipeline ---")
            for i in range(max(0, len(self.cam_history) - 5), len(self.cam_history)):
                c = self.cam_history[i]
                tgt = c["tgt"]; des = c["des"]; smo = c["smo"]; app = c["app"]
                d_smo = math.sqrt((smo[0] - des[0]) ** 2 + (smo[1] - des[1]) ** 2 + (smo[2] - des[2]) ** 2) * 1000.0
                d_app = "N/A"
                if app is not None:
                    d_app = f"{math.sqrt((app[0] - smo[0])**2 + (app[1] - smo[1])**2 + (app[2] - smo[2])**2) * 1000.0:.1f}"
                print(f"    tgt={c['target']:<12s}  "
                      f"tgt_pos=({tgt[0]:.3f},{tgt[1]:.3f},{tgt[2]:.3f})  "
                      f"des→smo={d_smo:.1f}mm  smo→app={d_app}mm")

        print(f"{'='*70}\n")


# ---------------------------------------------------------------------------
# console follow keys
# ---------------------------------------------------------------------------

class FollowKeys(threading.Thread):
    """View keys while the Unreal window is in front.

    B is registered so AirSim's own chase camera does not also take it.
    H, /, J, L, U and O are polled only while the game window is focused.
    """

    HOTKEYS = {"N": 0x4E, "B": 0x42}
    MODE_KEYS = {
        0xBF: "spring",
        0x46: "nose",
        0x48: "nose",
        0x4D: "free",
    }
    ADJUST_KEYS = {0x4A: "J", 0x4C: "L", 0x55: "U", 0x4F: "O"}
    MOD_NOREPEAT = 0x4000
    WM_HOTKEY = 0x0312
    QS_ALLINPUT = 0x04FF
    PM_REMOVE = 0x0001

    def __init__(self, follow: dict, names: List[str]):
        super().__init__(daemon=True)
        self.follow = follow
        self.names = names
        self._mtime = FOLLOW_PATH.stat().st_mtime if FOLLOW_PATH.exists() else 0
        self._held: set = set()
        self.user32 = None

    def run(self) -> None:
        try:
            import ctypes
            from ctypes import wintypes
        except ImportError:
            return
        user32 = ctypes.windll.user32
        self.user32 = user32
        for name, vk in self.HOTKEYS.items():
            if not user32.RegisterHotKey(None, vk, self.MOD_NOREPEAT, vk):
                print(f"warning: hotkey {name} is already taken by another application")
        print("H=nose camera  N=next aircraft  J/L=closer/farther  U/O=higher/lower")
        print("B=chase  /=closer chase  M=manual keys  1/2/3=sensor windows")
        msg = wintypes.MSG()
        while True:
            user32.MsgWaitForMultipleObjects(0, None, False, 50, self.QS_ALLINPUT)
            while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, self.PM_REMOVE):
                if msg.message == self.WM_HOTKEY:
                    self._on_hotkey(int(msg.wParam))
            self._poll_view_keys()
            self._reload_if_edited()

    def _game_focused(self) -> bool:
        if self.user32 is None:
            return False
        import ctypes
        hwnd = self.user32.GetForegroundWindow()
        title = ctypes.create_unicode_buffer(512)
        self.user32.GetWindowTextW(hwnd, title, 512)
        return "TestArea" in title.value

    def _poll_view_keys(self) -> None:
        if self.user32 is None or not self._game_focused():
            return
        for vk, mode in self.MODE_KEYS.items():
            down = bool(self.user32.GetAsyncKeyState(vk) & 0x8000)
            if down and vk not in self._held:
                if vk == 0x4D and time.time() < float(self.follow.get("suppress_m_until", 0.0)):
                    pass
                else:
                    self._set_view(mode)
            if down:
                self._held.add(vk)
            else:
                self._held.discard(vk)
        for numpad, row in ((0x61, 0x31), (0x62, 0x32), (0x63, 0x33)):
            down = bool(self.user32.GetAsyncKeyState(numpad) & 0x8000)
            if down and numpad not in self._held:
                self.user32.keybd_event(row, 0, 0, 0)
                self.user32.keybd_event(row, 0, 2, 0)
            if down:
                self._held.add(numpad)
            else:
                self._held.discard(numpad)
        for vk, name in self.ADJUST_KEYS.items():
            down = bool(self.user32.GetAsyncKeyState(vk) & 0x8000)
            if down and vk not in self._held:
                self._nudge(name)
            if down:
                self._held.add(vk)
            else:
                self._held.discard(vk)

    def _set_view(self, mode: str, release: bool = True) -> None:
        self.follow["view"] = mode
        self.follow["free_mode"] = mode == "free"
        if release and mode != "free":
            self.follow["release_pending"] = True
            self.follow["release_at"] = time.time() + 0.2
            self.follow["suppress_m_until"] = self.follow["release_at"] + 0.6
        print(f"\n>>> view {mode} <<<")

    def _nudge(self, name: str) -> None:
        view = str(self.follow.get("view") or "chase")
        if view == "spring":
            back_key, up_key, back0, up0, lo, hi = "spring_back", "spring_up", 6.0, 3.5, 2.0, 20.0
        elif view == "nose":
            back_key, up_key, back0, up0, lo, hi = "nose_ahead", "nose_up", 1.8, 0.35, 0.8, 8.0
        else:
            back_key, up_key, back0, up0, lo, hi = "chase_back", "chase_up", 8.0, 1.8, 4.0, 40.0
        back = float(self.follow.get(back_key, back0))
        up = float(self.follow.get(up_key, up0))
        if name == "J":
            back = max(lo, back - (0.4 if view == "nose" else 2.0))
        elif name == "L":
            back = min(hi, back + (0.4 if view == "nose" else 2.0))
        elif name == "U":
            up = min(30.0, up + (0.3 if view == "nose" else 1.5))
        elif name == "O":
            up = max(-1.0, up - (0.3 if view == "nose" else 1.5))
        self.follow[back_key] = back
        self.follow[up_key] = up
        print(f"{view}: distance {back:.1f} m, height {up:.1f} m")

    def _on_hotkey(self, vk: int) -> None:
        if vk == 0x42:
            self._set_view("chase", release=True)
            return
        if vk == 0x4E:
            self._cycle(1)
            self._announce()
            save_follow(self.follow)
            self._mtime = FOLLOW_PATH.stat().st_mtime

    def _cycle(self, step: int) -> None:
        if not self.names:
            return
        current = str(self.follow.get("target") or "")
        if current not in self.names:
            self.follow["target"] = self.names[0]
        else:
            index = (self.names.index(current) + step) % len(self.names)
            self.follow["target"] = self.names[index]

    def _announce(self) -> None:
        print(
            "follow {target} right={offset_right_m} up={offset_up_m} "
            "forward={offset_forward_m} pitch={extra_pitch_deg} yaw={extra_yaw_deg}".format(**self.follow)
        )

    def _reload_if_edited(self) -> None:
        if not FOLLOW_PATH.exists():
            return
        mtime = FOLLOW_PATH.stat().st_mtime
        if mtime <= self._mtime + 0.05:
            return
        self._mtime = mtime
        fresh = load_follow()
        keep = {key: self.follow[key] for key in self.follow if key not in fresh}
        self.follow.clear()
        self.follow.update(fresh)
        self.follow.update(keep)
        print("reloaded follow_camera.json")


def choose_target(follow: dict, names: List[str], poses: Dict[str, airsim.Pose]) -> Optional[airsim.Pose]:
    target = str(follow.get("target") or "")
    if target in poses:
        return poses[target]
    if names:
        follow["target"] = names[0]
        return poses.get(names[0])
    return None


# ---------------------------------------------------------------------------
# main loop
# ---------------------------------------------------------------------------

def _quiet_picture(client: airsim.MultirotorClient) -> None:
    """Motion blur and temporal AA smear a mesh that is being written every frame."""
    for command in (
        "r.MotionBlurQuality 0",
        "r.MotionBlur.Amount 0",
        "r.DefaultFeature.MotionBlur 0",
        "ShowFlag.MotionBlur 0",
        "r.DefaultFeature.AntiAliasing 0",
        "r.PostProcessAAQuality 0",
        "r.TemporalAACurrentFrameWeight 1",
    ):
        try:
            client.simRunConsoleCommand(command)
        except Exception:
            pass


def _release_airsim_camera(follow: dict) -> bool:
    """Manual mode detaches AirSim's spring arm so only this bridge moves the lens."""
    import ctypes
    user32 = ctypes.windll.user32
    hwnd = user32.GetForegroundWindow()
    title = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, title, 512)
    if "TestArea" not in title.value:
        return False
    follow["suppress_m_until"] = time.time() + 1.0
    user32.keybd_event(0x4D, 0, 0, 0)
    user32.keybd_event(0x4D, 0, 2, 0)
    return True


def _view_pose(follow: dict, subject: airsim.Pose, ease: dict, now: float) -> Optional[airsim.Pose]:
    view = str(follow.get("view") or "chase")
    if view == "free" or follow.get("free_mode"):
        return None
    if view in ("chase", "spring"):
        if view == "chase":
            back = float(follow.get("chase_back", 8.0))
            up = float(follow.get("chase_up", 1.8))
        else:
            back = float(follow.get("spring_back", 6.0))
            up = float(follow.get("spring_up", 3.5))
        if ease["view"] != view:
            ease["view"] = view
            ease["back"] = back
            ease["up"] = up
            ease["t"] = now
        else:
            dt = min(0.05, max(0.0, now - ease["t"]))
            ease["t"] = now
            blend = 1.0 - math.exp(-dt / 0.12)
            ease["back"] += (back - ease["back"]) * blend
            ease["up"] += (up - ease["up"]) * blend
        return chase_pose(subject, ease["back"], ease["up"])
    if view == "nose":
        return nose_pose(subject, float(follow.get("nose_ahead", 1.8)), float(follow.get("nose_up", 0.35)))
    return look_pose(subject, follow)


def run(bind: str, port: int, launch: bool) -> None:
    follow = load_follow()
    follow["view"] = str(follow.get("view") or "chase")
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((bind, port))
    sock.setblocking(False)
    print(f"UDP {bind}:{port}  binary ITS1 frames  (diagnostic dump every {DIAG_EVERY_N} frames)")
    client = connect(launch)
    print("AirSim connected")
    _quiet_picture(client)
    try:
        objects = client.simListSceneObjects("ExternalCamera.*")
        print(f"ExternalCamera lookup: {objects or 'NOT FOUND — camera cannot be driven'}")
    except Exception as exc:
        print(f"scene object lookup failed: {exc}")
    # per-frame CSV log for offline jitter analysis
    diag_log = Path(__file__).resolve().parent / "bridge_diag.csv"
    diag_fh = open(diag_log, "w", encoding="utf-8")
    diag_fh.write("frame,seq,vehicle,ux,uy,uz,uqx,uqy,uqz,uqw,"
                  "nx,ny,nz,nqx,nqy,nqz,nqw,"
                  "cam_tgt_name,cam_des_x,cam_des_y,cam_des_z,"
                  "cam_smo_x,cam_smo_y,cam_smo_z,"
                  "set_get_dx_mm,set_get_dy_mm,set_get_dz_mm\n")
    diag_fh.flush()
    known: Dict[str, bool] = {}
    names: List[str] = []
    keys = FollowKeys(follow, names)
    keys.start()
    detector = JitterDetector()
    tracks: Dict[str, PoseTrack] = {}
    offset_ease = {"view": "", "back": 0.0, "up": 0.0, "t": 0.0}
    applied = 0
    last_status = 0.0
    last_pin = 0.0
    picture_settled = False

    def advance_bodies(now: float, force: bool = False) -> None:
        """Write the gliding pose often enough that gravity cannot accumulate."""
        nonlocal last_pin
        if not force and now - last_pin < 0.008:
            return
        last_pin = now
        for tracked_name, track in tracks.items():
            pose = track.sample(now)
            if pose is None:
                continue
            try:
                pin_pose_fast(client, tracked_name, pose, track.velocity)
            except Exception as exc:
                if not advance_bodies.warned:
                    advance_bodies.warned = True
                    print(f"pin {tracked_name} failed: {exc}")

    advance_bodies.warned = False

    def refresh_view(now: float) -> Optional[airsim.Pose]:
        if follow.get("release_pending") and now >= float(follow.get("release_at", 0.0)):
            if _release_airsim_camera(follow) or now > float(follow.get("release_at", 0.0)) + 1.0:
                follow["release_pending"] = False
        if not names:
            return None
        target_name = str(follow.get("target") or "")
        if target_name not in tracks:
            target_name = names[0]
            follow["target"] = target_name
        track = tracks.get(target_name)
        if track is None:
            return None
        pose = track.sample(now)
        if pose is None:
            return None
        place_sensor_camera(client, pose)
        desired_pose = _view_pose(follow, pose, offset_ease, now)
        if desired_pose is not None:
            place_window_camera(client, desired_pose)
        return desired_pose

    while True:
        # drain UDP — latest frame wins
        packet = None
        while True:
            try:
                packet = sock.recv(65535)
            except BlockingIOError:
                break
        now = time.time()
        if packet is None:
            advance_bodies(now)
            refresh_view(now)
            time.sleep(0.008)
            continue
        parsed = parse_frame(packet)
        if parsed is None:
            continue
        _seq, vehicles = parsed

        poses: Dict[str, airsim.Pose] = {}
        frame_names: List[str] = []

        for vehicle in vehicles:
            name = vehicle["id"] or f"A{len(frame_names)+1}"
            frame_names.append(name)

            # --- raw Unity data (for diagnostics) ---
            upx, upy, upz = vehicle["pos"]
            uqx, uqy, uqz, uqw = vehicle["quat"]

            # --- coordinate conversion ---
            n, e, d = unity_to_ned(upx, upy, upz)
            nqx, nqy, nqz, nqw = unity_quat_to_ned(uqx, uqy, uqz, uqw)
            init_pose = _pose(n, e, d)

            ensure_vehicle(client, name, init_pose, known)
            if name not in known:
                continue
            ned_pose = _pose(n, e, d, nqx, nqy, nqz, nqw)
            track = tracks.get(name)
            if track is None:
                track = PoseTrack()
                tracks[name] = track
            track.push(now, ned_pose)
            poses[name] = ned_pose

            # --- feed diagnostics ---
            detector.feed_vehicle(name, (upx, upy, upz), (uqx, uqy, uqz, uqw),
                                  (ned_pose.position.x_val, ned_pose.position.y_val, ned_pose.position.z_val),
                                  (ned_pose.orientation.x_val, ned_pose.orientation.y_val,
                                   ned_pose.orientation.z_val, ned_pose.orientation.w_val))

        names[:] = frame_names
        advance_bodies(now, force=True)
        desired = refresh_view(now)
        smoothed = desired

        applied += 1

        # --- per-frame CSV log (compact, for offline jitter analysis) ---
        if applied % 5 == 0:  # every 5th frame to keep file manageable
            cam_tgt = "free" if follow.get("free_mode", False) else str(follow.get("target") or "")
            cam_des = (0.0, 0.0, 0.0); cam_smo = (0.0, 0.0, 0.0)
            if desired is not None:
                cam_des = (desired.position.x_val, desired.position.y_val, desired.position.z_val)
            if smoothed is not None:
                cam_smo = (smoothed.position.x_val, smoothed.position.y_val, smoothed.position.z_val)
            for vehicle in vehicles:
                name = vehicle["id"]
                ned_pose = poses.get(name)
                if ned_pose is None:
                    continue
                upx, upy, upz = vehicle["pos"]
                uqx, uqy, uqz, uqw = vehicle["quat"]
                n, e, d = unity_to_ned(upx, upy, upz)
                nqx, nqy, nqz, nqw = unity_quat_to_ned(uqx, uqy, uqz, uqw)
                diag_fh.write(
                    f"{applied},{_seq},{name},{upx:.4f},{upy:.4f},{upz:.4f},"
                    f"{uqx:.6f},{uqy:.6f},{uqz:.6f},{uqw:.6f},"
                    f"{n:.4f},{e:.4f},{d:.4f},"
                    f"{nqx:.6f},{nqy:.6f},{nqz:.6f},{nqw:.6f},"
                    f"{cam_tgt},{cam_des[0]:.4f},{cam_des[1]:.4f},{cam_des[2]:.4f},"
                    f"{cam_smo[0]:.4f},{cam_smo[1]:.4f},{cam_smo[2]:.4f},"
                    f"0.000,0.000,0.000\n"
                )
        if applied % 30 == 0:
            diag_fh.flush()

        # --- periodic diagnostic dump ---
        if applied % DIAG_EVERY_N == 0 and applied > 0:
            detector.dump(applied)

        # --- periodic status line ---
        if not picture_settled and applied > 40:
            picture_settled = True
            _quiet_picture(client)
        if now - last_status > 2.0:
            last_status = now
            print(f"frames={applied} vehicles={len(frame_names)} "
                  f"view={follow.get('view', 'chase')} "
                  f"target={follow.get('target')}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Unity to AirSim joint simulation bridge")
    parser.add_argument("--bind", default=DEFAULT_BIND)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--launch-airsim", action="store_true")
    args = parser.parse_args()
    run(args.bind, args.port, args.launch_airsim)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
