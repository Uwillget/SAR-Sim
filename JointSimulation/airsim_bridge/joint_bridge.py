"""Unity SAR kinematic bridge for the TestArea AirSim map.

Unity is the motion master. This process:

* listens for binary pose frames on UDP 49001 (latest frame wins)
* creates one AirSim simpleFlight vehicle per Unity aircraft
* writes poses with simSetVehiclePose so the vehicles track Unity
* drives the view camera by teleporting the "ExternalCamera" actor with
  simSetObjectPose (world-NED)

AirSim must start with "ViewMode": "Manual" in settings.json.  In every other
view mode ACameraDirector::Tick lerps the camera towards the aircraft on every
frame (position and rotation never converge) — that is what produced the
on-screen shake.  In Manual mode CameraDirector only applies key deltas, which
are zero when nobody touches the keyboard, so the camera stays exactly where
this bridge puts it.  The ChaseCam vehicle is teleported to the same pose so
the FPV view (F key) shows the same viewpoint.

Executable hotkeys (RegisterHotKey — they act while the UE window has focus):

    H          toggle free-flight <-> follow
    N          cycle the followed aircraft
    J / L      follow distance closer / farther
    U / O      follow height higher / lower

In free mode the bridge stops touching the camera and the operator flies it
with UE's native Manual-mode keys (arrows = move, PageUp/PageDown = up/down,
A/D = yaw, W/S = pitch, Q/E = roll, Shift/Ctrl = speed).

API control is deliberately never enabled: SimpleFlight's offboard API enters
hover mode when it stops receiving goals and then applies forces on every
physics step, which fights the teleports.

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
    """Global-hotkey thread for follow-mode adjustments and mode switching.

    Keys are registered with the Win32 RegisterHotKey API, so they act while
    the UE window has focus (msvcrt only worked when this console was focused,
    which is why the old keys appeared dead).  None of these keys is bound
    inside AirSim, so there is no clash with UE's own bindings.

        H          toggle free-flight <-> follow
        N          cycle the followed aircraft
        J / L      follow distance closer / farther
        U / O      follow height higher / lower

    Free-flight itself is flown with UE's native Manual-mode keys (arrows,
    PageUp/PageDown, A/D, W/S, Q/E, Shift/Ctrl).
    """

    HOTKEYS = {"H": 0x48, "N": 0x4E, "J": 0x4A, "L": 0x4C, "U": 0x55, "O": 0x4F}
    MOD_NOREPEAT = 0x4000
    WM_HOTKEY = 0x0312
    QS_ALLINPUT = 0x04FF
    PM_REMOVE = 0x0001

    def __init__(self, follow: dict, names: List[str]):
        super().__init__(daemon=True)
        self.follow = follow
        self.names = names
        self._mtime = FOLLOW_PATH.stat().st_mtime if FOLLOW_PATH.exists() else 0

    def run(self) -> None:
        try:
            import ctypes
            from ctypes import wintypes
        except ImportError:
            return
        user32 = ctypes.windll.user32
        for name, vk in self.HOTKEYS.items():
            if not user32.RegisterHotKey(None, vk, self.MOD_NOREPEAT, vk):
                print(f"warning: hotkey {name} is already taken by another application")
        print("H=free/follow  N=next target  J/L=distance  U/O=height   (global hotkeys)")
        msg = wintypes.MSG()
        while True:
            # wake on a hotkey, or every 500 ms just to re-check follow_camera.json
            user32.MsgWaitForMultipleObjects(0, None, False, 500, self.QS_ALLINPUT)
            while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, self.PM_REMOVE):
                if msg.message == self.WM_HOTKEY:
                    self._on_hotkey(int(msg.wParam))
            self._reload_if_edited()

    def _on_hotkey(self, vk: int) -> None:
        if vk == 0x48:  # H — toggle follow / free-flight
            self.follow["free_mode"] = not self.follow.get("free_mode", False)
            mode = "FREE-FLIGHT (fly the camera with UE's manual keys)" \
                if self.follow["free_mode"] else "FOLLOW"
            print(f"\n>>> SWITCHED TO {mode} MODE <<<")
            save_follow(self.follow)
            return
        if vk == 0x4E:  # N — cycle target
            self._cycle(1)
        elif vk == 0x4A:  # J — closer
            self.follow["offset_forward_m"] = float(self.follow["offset_forward_m"]) - 2
        elif vk == 0x4C:  # L — farther
            self.follow["offset_forward_m"] = min(-2.0, float(self.follow["offset_forward_m"]) + 2)
        elif vk == 0x55:  # U — higher
            self.follow["offset_up_m"] = float(self.follow["offset_up_m"]) + 1
        elif vk == 0x4F:  # O — lower
            self.follow["offset_up_m"] = max(0.5, float(self.follow["offset_up_m"]) - 1)
        else:
            return
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
        self.follow.clear()
        self.follow.update(fresh)
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

def run(bind: str, port: int, launch: bool) -> None:
    follow = load_follow()
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((bind, port))
    sock.setblocking(False)
    print(f"UDP {bind}:{port}  binary ITS1 frames  (diagnostic dump every {DIAG_EVERY_N} frames)")
    client = connect(launch)
    print("AirSim connected")
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
    smoother = PoseSmoother()
    detector = JitterDetector()
    applied = 0
    last_status = 0.0

    while True:
        # drain UDP — latest frame wins
        packet = None
        while True:
            try:
                packet = sock.recv(65535)
            except BlockingIOError:
                break
        if packet is None:
            time.sleep(0.005)
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
            ned_pose, actual_pose = apply_vehicle(client, name, vehicle)
            poses[name] = ned_pose

            # --- feed diagnostics ---
            detector.feed_vehicle(name, (upx, upy, upz), (uqx, uqy, uqz, uqw),
                                  (ned_pose.position.x_val, ned_pose.position.y_val, ned_pose.position.z_val),
                                  (ned_pose.orientation.x_val, ned_pose.orientation.y_val,
                                   ned_pose.orientation.z_val, ned_pose.orientation.w_val))

        names[:] = frame_names

        # --- camera ---
        actual_cam: Optional[airsim.Pose] = None
        desired: Optional[airsim.Pose] = None
        smoothed: Optional[airsim.Pose] = None
        target_pose: Optional[airsim.Pose] = None
        if follow.get("free_mode", False):
            # Free-flight: the bridge leaves the camera alone.  AirSim's Manual
            # view mode flies ExternalCamera with its own keys and the motion
            # never fights this bridge.
            pass
        else:
            # Follow mode: track the selected vehicle
            target_pose = choose_target(follow, names, poses)
            if target_pose is not None:
                desired = look_pose(target_pose, follow)
                smoothed = smoother.step(str(follow.get("target") or ""), desired, time.time())
                actual_cam = apply_follow(client, smoothed)
                # --- feed camera diagnostics ---
                detector.feed_camera(
                    str(follow.get("target") or ""),
                    (target_pose.position.x_val, target_pose.position.y_val, target_pose.position.z_val),
                    (desired.position.x_val, desired.position.y_val, desired.position.z_val),
                    (smoothed.position.x_val, smoothed.position.y_val, smoothed.position.z_val),
                    (actual_cam.position.x_val, actual_cam.position.y_val, actual_cam.position.z_val)
                    if actual_cam else None,
                )

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
        now = time.time()
        if now - last_status > 2.0:
            last_status = now
            print(f"frames={applied} vehicles={len(frame_names)} "
                  f"mode={'FREE' if follow.get('free_mode') else 'FOLLOW'} "
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
