"""Slave AirSim to Unity kinematic poses.

The SAR platform (Unity) is the motion master. This process listens for
Unity ENU poses on UDP and writes them into AirSim with simSetVehiclePose
(ignore_collision=True), so the UE drone follows the command exactly
instead of running SimpleFlight/PX4 dynamics.

Usage
-----
1. UE PIE on ShenzhenWhitebox (AirSim plugin, OriginGeopoint = shared origin,
   PlayerStart at Unreal origin so vehicle NED == world NED).
2. python -m airsim_bridge.kinematic_slave
3. Play the Unity sim with AirSimKinematicPublisher on the scene.

Without Unity, probe a locker:
    python -m airsim_bridge.kinematic_slave --probe 09
"""
from __future__ import annotations

import argparse
import json
import math
import socket
import sys
import time
from pathlib import Path
from typing import Dict, Optional

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import airsim  # noqa: E402

from airsim_bridge.coords import (  # noqa: E402
    LOCKERS_UNITY,
    unity_quat_to_ned,
    unity_to_ned,
)

# Matches Unity ITSAC/Build Shenzhen Demo Airspace (locker 09 Haifeng).
DEMO_EAST0 = 206.80
DEMO_NORTH0 = 148.90
DEMO_ALT = 75.0
DEMO_SPACING = 150.0
DEMO_HALF = 1

DEFAULT_BIND = "127.0.0.1"
DEFAULT_PORT = 49001


def _pose_ned(north: float, east: float, down: float, qx=0.0, qy=0.0, qz=0.0, qw=1.0) -> airsim.Pose:
    return airsim.Pose(
        airsim.Vector3r(north, east, down),
        airsim.Quaternionr(qx, qy, qz, qw),
    )


def connect(timeout: float = 10.0) -> airsim.MultirotorClient:
    client = airsim.MultirotorClient()
    deadline = time.time() + timeout
    last_err: Optional[Exception] = None
    while time.time() < deadline:
        try:
            client.confirmConnection()
            return client
        except Exception as exc:  # noqa: BLE001
            last_err = exc
            time.sleep(0.5)
    raise RuntimeError(f"AirSim not reachable on default RPC 41451: {last_err}")


def prepare_vehicle(client: airsim.MultirotorClient, name: str) -> None:
    try:
        client.enableApiControl(True, name)
        client.armDisarm(True, name)
    except Exception:
        # ComputerVision mode has no arming.
        pass


def apply_unity_vehicle(client: airsim.MultirotorClient, vehicle: str, msg: dict) -> None:
    pos = msg["pos"]
    quat = msg.get("quat") or [0.0, 0.0, 0.0, 1.0]
    n, e, d = unity_to_ned(float(pos[0]), float(pos[1]), float(pos[2]))
    qx, qy, qz, qw = unity_quat_to_ned(*[float(v) for v in quat])
    client.simSetVehiclePose(_pose_ned(n, e, d, qx, qy, qz, qw), True, vehicle)


def chase_camera_director(client: airsim.MultirotorClient, north: float, east: float, down: float) -> None:
    """Keep the possessed CameraDirector next to the drone.

    PIE looks through CameraDirector. It spawns at the origin, so a kinematic
    slave that only moves Drone1 leaves the editor/game view staring at origin
    PIP cameras while the vehicle is hundreds of metres away.
    """
    back = 22.0
    up = 8.0
    pose = airsim.Pose(
        airsim.Vector3r(north - back, east, down - up),
        airsim.to_quaternion(math.radians(-16.0), 0.0, 0.0),
    )
    try:
        client.simSetObjectPose("CameraDirector", pose, True)
    except Exception:
        pass
    try:
        client.simSetObjectPose("DroneBeacon", _pose_ned(north, east, down), True)
    except Exception:
        pass


def _v(east: float, up: float, north: float) -> airsim.Vector3r:
    n, e, d = unity_to_ned(east, up, north)
    return airsim.Vector3r(n, e, d)


def plot_demo_airspace(client: airsim.MultirotorClient) -> None:
    """Persistent AirSim debug lines for the 3x3 Haifeng corridor."""
    try:
        client.simFlushPersistentMarkers()
    except Exception:
        pass
    nodes = []
    n = 2 * DEMO_HALF + 1
    for iz in range(n):
        row = []
        for ix in range(n):
            east = DEMO_EAST0 + (ix - DEMO_HALF) * DEMO_SPACING
            north = DEMO_NORTH0 + (iz - DEMO_HALF) * DEMO_SPACING
            row.append(_v(east, DEMO_ALT, north))
        nodes.append(row)
    amber = [0.83, 0.62, 0.18, 1.0]
    green = [0.16, 0.62, 0.35, 1.0]
    teal = [0.10, 0.55, 0.47, 1.0]
    segs = []
    for iz in range(n):
        for ix in range(n):
            if ix + 1 < n:
                segs.extend([nodes[iz][ix], nodes[iz][ix + 1]])
            if iz + 1 < n:
                segs.extend([nodes[iz][ix], nodes[iz + 1][ix]])
    client.simPlotLineList(segs, amber, thickness=12.0, duration=-1.0, is_persistent=True)
    loop = [
        nodes[0][2],
        nodes[2][2],
        nodes[2][0],
        nodes[0][0],
        nodes[0][2],
    ]
    client.simPlotLineStrip(loop, green, thickness=18.0, duration=-1.0, is_persistent=True)
    pad = _v(DEMO_EAST0, 8.0, DEMO_NORTH0)
    top = _v(DEMO_EAST0, DEMO_ALT, DEMO_NORTH0)
    client.simPlotLineList([pad, top], [0.13, 0.48, 0.72, 1.0], thickness=10.0, duration=-1.0, is_persistent=True)
    client.simPlotPoints([pad, top], teal, size=20.0, duration=-1.0, is_persistent=True)
    print("plotted Haifeng 3x3 demo airspace markers")


def run_slave(bind: str, port: int, default_vehicle: str, plot_demo: bool = True) -> None:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((bind, port))
    sock.settimeout(1.0)
    print(f"UDP listening {bind}:{port}  (Unity ENU metres)")
    client = connect()
    print("AirSim connected")
    if plot_demo:
        plot_demo_airspace(client)
    prepared: Dict[str, str] = {}
    n_ok = 0
    while True:
        try:
            raw, _addr = sock.recvfrom(65535)
        except socket.timeout:
            continue
        try:
            frame = json.loads(raw.decode("utf-8"))
        except json.JSONDecodeError:
            continue
        vehicles = frame.get("vehicles") or []
        for i, v in enumerate(vehicles):
            airsim_name = v.get("airsim") or (default_vehicle if i == 0 else v.get("id") or default_vehicle)
            if airsim_name not in prepared:
                prepare_vehicle(client, airsim_name)
                prepared[airsim_name] = airsim_name
            apply_unity_vehicle(client, airsim_name, v)
            n_ok += 1
            if n_ok % 5 == 1:
                pos = v.get("pos") or [0, 0, 0]
                n, e, d = unity_to_ned(float(pos[0]), float(pos[1]), float(pos[2]))
                chase_camera_director(client, n, e, d)
        if n_ok and n_ok % 200 == 0:
            print(f"applied {n_ok} pose commands")


def probe_locker(client: airsim.MultirotorClient, key: str, vehicle: str, height: float) -> None:
    key = key.strip()
    hit = None
    for name, east, north in LOCKERS_UNITY:
        if name.startswith(key) or key in name:
            hit = (name, east, north)
            break
    if hit is None:
        raise SystemExit(f"no locker matching {key!r}")
    name, east, north = hit
    n, e, d = unity_to_ned(east, height, north)
    prepare_vehicle(client, vehicle)
    client.simSetVehiclePose(_pose_ned(n, e, d), True, vehicle)
    pose = client.simGetVehiclePose(vehicle)
    gps = None
    try:
        gps = client.getMultirotorState(vehicle).gps_location
    except Exception:
        pass
    print(f"teleport {name}")
    print(f"  Unity ENU  E={east:.2f} U={height:.2f} N={north:.2f}")
    print(f"  cmd NED    N={n:.2f} E={e:.2f} D={d:.2f}")
    print(
        f"  AirSim pose N={pose.position.x_val:.2f} E={pose.position.y_val:.2f} "
        f"D={pose.position.z_val:.2f}"
    )
    if gps is not None:
        print(f"  GPS        lat={gps.latitude:.6f} lon={gps.longitude:.6f} alt={gps.altitude:.2f}")


def main() -> int:
    p = argparse.ArgumentParser(description="AirSim kinematic slave for the Unity SAR platform")
    p.add_argument("--bind", default=DEFAULT_BIND)
    p.add_argument("--port", type=int, default=DEFAULT_PORT)
    p.add_argument("--vehicle", default="Drone1")
    p.add_argument("--probe", default="", help="locker id/name to teleport, e.g. 09 or Haifeng")
    p.add_argument("--height", type=float, default=8.0, help="Unity up (m) for --probe")
    args = p.parse_args()
    if args.probe:
        client = connect()
        probe_locker(client, args.probe, args.vehicle, args.height)
        return 0
    run_slave(args.bind, args.port, args.vehicle)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
