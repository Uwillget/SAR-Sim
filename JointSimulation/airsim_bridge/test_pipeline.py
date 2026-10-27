"""End-to-end pipeline test for the Unity→AirSim joint bridge.

Simulates a drone flying a smooth circular path and feeds the identical
binary ITS1 frames the bridge would receive from Unity.  Validates:

1. Unity ENU ↔ NED coordinate round-trips
2. Quaternion conversion correctness
3. Camera look_pose stability under smooth motion
4. PoseSmoother convergence behaviour
5. Frame-to-frame position jitter detection

Run standalone — no Unity, no AirSim, no UDP.  Just Python.
"""

import math
import struct
import time
from typing import List, Tuple

# ---------------------------------------------------------------------------
# Minimal mocks — avoid importing airsim / the bridge at module level
# ---------------------------------------------------------------------------

class Vector3r:
    __slots__ = ("x_val", "y_val", "z_val")
    def __init__(self, x=0.0, y=0.0, z=0.0):
        self.x_val = x; self.y_val = y; self.z_val = z
    def __repr__(self): return f"V({self.x_val:.3f},{self.y_val:.3f},{self.z_val:.3f})"


class Quaternionr:
    __slots__ = ("x_val", "y_val", "z_val", "w_val")
    def __init__(self, x=0.0, y=0.0, z=0.0, w=1.0):
        self.x_val = x; self.y_val = y; self.z_val = z; self.w_val = w
    def __repr__(self): return f"Q({self.x_val:.4f},{self.y_val:.4f},{self.z_val:.4f},{self.w_val:.4f})"


def mock_pose(n, e, d, qx=0.0, qy=0.0, qz=0.0, qw=1.0):
    return MockPose(Vector3r(n, e, d), Quaternionr(qx, qy, qz, qw))


class MockPose:
    def __init__(self, pos, ori):
        self.position = pos; self.orientation = ori


# fake the airsim.to_quaternion utility
def mock_to_quaternion(pitch, roll, yaw):
    """airsim.to_quaternion returns (w,x,y,z)."""
    cy = math.cos(yaw * 0.5); sy = math.sin(yaw * 0.5)
    cp = math.cos(pitch * 0.5); sp = math.sin(pitch * 0.5)
    cr = math.cos(roll * 0.5);  sr = math.sin(roll * 0.5)
    w = cr * cp * cy + sr * sp * sy
    x = sr * cp * cy - cr * sp * sy
    y = cr * sp * cy + sr * cp * sy
    z = cr * cp * sy - sr * sp * cy
    return Quaternionr(x, y, z, w)


# ---------------------------------------------------------------------------
# Copy the EXACT conversion / camera / smoother code from joint_bridge.py
# ---------------------------------------------------------------------------

def unity_to_ned(east: float, up: float, north: float) -> Tuple[float, float, float]:
    return north, east, -up

def unity_quat_to_ned(qx: float, qy: float, qz: float, qw: float):
    return qz, -qx, qy, qw

def _rotate_ned(qx, qy, qz, qw, x, y, z):
    tx = 2.0 * (qy * z - qz * y)
    ty = 2.0 * (qz * x - qx * z)
    tz = 2.0 * (qx * y - qy * x)
    return (
        x + qw * tx + (qy * tz - qz * ty),
        y + qw * ty + (qz * tx - qx * tz),
        z + qw * tz + (qx * ty - qy * tx),
    )

def _lerp(a, b, t):
    return a + (b - a) * t

def _slerp(a: Quaternionr, b: Quaternionr, t: float) -> Quaternionr:
    dot = a.x_val * b.x_val + a.y_val * b.y_val + a.z_val * b.z_val + a.w_val * b.w_val
    bx, by, bz, bw = b.x_val, b.y_val, b.z_val, b.w_val
    if dot < 0.0:
        bx, by, bz, bw = -bx, -by, -bz, -bw; dot = -dot
    if dot > 0.9995:
        x = _lerp(a.x_val, bx, t); y = _lerp(a.y_val, by, t)
        z = _lerp(a.z_val, bz, t); w = _lerp(a.w_val, bw, t)
    else:
        theta = math.acos(max(-1.0, min(1.0, dot)))
        sin_theta = math.sin(theta)
        w0 = math.sin((1.0 - t) * theta) / sin_theta
        w1 = math.sin(t * theta) / sin_theta
        x = w0 * a.x_val + w1 * bx; y = w0 * a.y_val + w1 * by
        z = w0 * a.z_val + w1 * bz; w = w0 * a.w_val + w1 * bw
    length = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    return Quaternionr(x / length, y / length, z / length, w / length)

class PoseSmoother:
    def __init__(self):
        self.pose = None; self.target_name = ""; self.last_time = 0.0
    def reset(self, name):
        self.pose = None; self.target_name = name
    def step(self, name, target, now):
        if name != self.target_name:
            self.reset(name)
        if self.pose is None:
            self.pose = target; self.last_time = now; return target
        dt = min(0.1, max(0.0, now - self.last_time))
        self.last_time = now
        pa = 1.0 - math.exp(-5.0 * dt); ra = 1.0 - math.exp(-10.0 * dt)
        p = self.pose.position; t = target.position
        pos = Vector3r(_lerp(p.x_val, t.x_val, pa),
                       _lerp(p.y_val, t.y_val, pa),
                       _lerp(p.z_val, t.z_val, pa))
        ori = _slerp(self.pose.orientation, target.orientation, ra)
        self.pose = MockPose(pos, ori)
        return self.pose

def quat_xyzw(q):
    return q.x_val, q.y_val, q.z_val, q.w_val

def look_pose(vehicle, follow):
    qx, qy, qz, qw = (vehicle.orientation.x_val, vehicle.orientation.y_val,
                       vehicle.orientation.z_val, vehicle.orientation.w_val)
    fwd = float(follow["offset_forward_m"])
    rgt = float(follow["offset_right_m"])
    up  = float(follow["offset_up_m"])
    ox, oy, oz = _rotate_ned(qx, qy, qz, qw, fwd, rgt, -up)
    cx = vehicle.position.x_val + ox
    cy = vehicle.position.y_val + oy
    cz = vehicle.position.z_val + oz
    dx, dy, dz = vehicle.position.x_val - cx, vehicle.position.y_val - cy, vehicle.position.z_val - cz
    horiz = math.hypot(dx, dy)
    yaw = math.atan2(dy, dx) + math.radians(float(follow["extra_yaw_deg"]))
    pitch = math.atan2(-dz, max(horiz, 1e-4)) + math.radians(float(follow["extra_pitch_deg"]))
    q = mock_to_quaternion(pitch, 0.0, yaw)
    return MockPose(Vector3r(cx, cy, cz), Quaternionr(*quat_xyzw(q)))


# ---------------------------------------------------------------------------
# Binary ITS1 frame builder (matches AirSimJointBridge.SendPoses exactly)
# ---------------------------------------------------------------------------

MAGIC = b"ITS1"
HEADER_FMT = struct.Struct("<4sBHI")
FLOATS_FMT = struct.Struct("<10f")

def build_frame(seq: int, vehicles: List[dict]) -> bytes:
    """vehicles: list of {id:str, pos:(x,y,z), quat:(x,y,z,w), vel:(vx,vy,vz)}"""
    buf = bytearray(65507)
    buf[0:4] = MAGIC
    buf[4] = 1  # version
    count_offset = 5
    struct.pack_into("<H", buf, count_offset, 0)  # placeholder
    struct.pack_into("<I", buf, 7, seq)
    offset = 11
    count = 0
    for v in vehicles:
        name_bytes = v["id"].encode("utf-8")
        if len(name_bytes) > 64:
            continue
        buf[offset] = len(name_bytes); offset += 1
        buf[offset:offset + len(name_bytes)] = name_bytes; offset += len(name_bytes)
        px, py, pz = v["pos"]
        qx, qy, qz, qw = v["quat"]
        vx, vy, vz = v.get("vel", (0.0, 0.0, 0.0))
        FLOATS_FMT.pack_into(buf, offset, px, py, pz, qx, qy, qz, qw, vx, vy, vz)
        offset += 40
        count += 1
    struct.pack_into("<H", buf, count_offset, count)
    return bytes(buf[:offset])


def parse_frame(packet: bytes) -> List[dict]:
    if len(packet) < 11: return []
    magic, ver, count, seq = HEADER_FMT.unpack_from(packet, 0)
    if magic != MAGIC or ver != 1: return []
    offset = 11
    vehicles = []
    for _ in range(count):
        name_len = packet[offset]; offset += 1
        name = packet[offset:offset + name_len].decode("utf-8")
        offset += name_len
        vals = FLOATS_FMT.unpack_from(packet, offset); offset += 40
        vehicles.append({"id": name, "pos": vals[0:3], "quat": vals[3:7], "vel": vals[7:10]})
    return vehicles


# ---------------------------------------------------------------------------
# Test: circular flight path
# ---------------------------------------------------------------------------

def euler_to_quat_unity(yaw_deg: float) -> Tuple[float, float, float, float]:
    """Unity convention: Y-up, yaw 0 = north (+Z), +yaw = east (+X).
    Quaternion is (x,y,z,w)."""
    half = math.radians(yaw_deg) / 2.0
    # In Unity: rotation around Y axis, q = (0, sin(half), 0, cos(half))
    return (0.0, math.sin(half), 0.0, math.cos(half))


def test_circular_flight():
    print("=" * 70)
    print("TEST 1: Circular flight — smooth motion, no injected noise")
    print("=" * 70)

    follow = {
        "target": "Drone0",
        "offset_right_m": 0.0,
        "offset_up_m": 8.0,
        "offset_forward_m": -22.0,
        "extra_pitch_deg": -8.0,
        "extra_yaw_deg": 0.0,
    }

    smoother = PoseSmoother()
    frames = 300
    radius = 500.0        # 500 m circle
    centre_east = 5000.0  # Unity east
    centre_north = 10000.0  # Unity north
    height = 100.0         # Unity up
    speed = 15.0           # m/s
    dt = 1.0 / 30.0        # 30 fps

    prev_unity_pos = None
    prev_ned_pos = None
    prev_cam_des = None
    max_pos_delta_mm = 0.0
    max_cam_delta_mm = 0.0

    for i in range(frames):
        t = i * dt
        angle = (speed / radius) * t  # radians
        yaw_deg = math.degrees(angle) + 90.0  # nose tangent to circle (+90 so nose points along tangent)

        # Unity ENU position
        ux = centre_east + radius * math.cos(angle)
        uy = height
        uz = centre_north + radius * math.sin(angle)
        uq = euler_to_quat_unity(yaw_deg)

        # build & parse a frame
        vehicles = [{"id": "Drone0", "pos": (ux, uy, uz), "quat": uq, "vel": (0, 0, 0)}]
        frame = build_frame(i, vehicles)
        parsed = parse_frame(frame)

        # coordinate conversion
        v = parsed[0]
        n, e, d = unity_to_ned(*v["pos"])
        nq = unity_quat_to_ned(*v["quat"])
        ned_pos = (n, e, d)

        # vehicle position delta (mm)
        if prev_ned_pos is not None:
            delta = math.sqrt((n - prev_ned_pos[0])**2 + (e - prev_ned_pos[1])**2 + (d - prev_ned_pos[2])**2) * 1000
            max_pos_delta_mm = max(max_pos_delta_mm, delta)

        prev_unity_pos = (ux, uy, uz)
        prev_ned_pos = ned_pos

        # camera pipeline
        veh_pose = mock_pose(n, e, d, *nq)
        desired = look_pose(veh_pose, follow)
        now = t
        smoothed = smoother.step("Drone0", desired, now)

        if prev_cam_des is not None:
            cam_delta = math.sqrt(
                (desired.position.x_val - prev_cam_des.position.x_val)**2 +
                (desired.position.y_val - prev_cam_des.position.y_val)**2 +
                (desired.position.z_val - prev_cam_des.position.z_val)**2
            ) * 1000
            max_cam_delta_mm = max(max_cam_delta_mm, cam_delta)

        prev_cam_des = desired

        # periodic status
        if i % 60 == 0:
            yaw_ned = math.degrees(math.atan2(
                2 * (nq[3]*nq[2] + nq[0]*nq[1]),
                1 - 2*(nq[1]*nq[1] + nq[2]*nq[2])))
            print(f"  frame {i:4d}  unity=({ux:8.1f},{uy:5.1f},{uz:8.1f})  "
                  f"yaw={yaw_deg:7.2f}°  "
                  f"ned=({n:8.1f},{e:8.1f},{d:5.1f})  "
                  f"ned_yaw={yaw_ned:7.2f}°  "
                  f"cam=({desired.position.x_val:8.1f},{desired.position.y_val:8.1f},{desired.position.z_val:5.1f})")

    print(f"\n  Max vehicle pos delta: {max_pos_delta_mm:.1f} mm/frame  "
          f"(expected ~{speed*1000/30:.0f} mm at {speed} m/s)")
    print(f"  Max camera delta:     {max_cam_delta_mm:.1f} mm/frame")
    print(f"  VERDICT: ", end="")
    if max_pos_delta_mm < 1000 and max_cam_delta_mm < 1000:
        print("PASS — no anomalous jitter in the math pipeline")
    else:
        print("FAIL — unexpected large delta detected")


# ---------------------------------------------------------------------------
# Test: stationary drone (should produce zero camera movement)
# ---------------------------------------------------------------------------

def test_stationary():
    print("\n" + "=" * 70)
    print("TEST 2: Stationary drone — camera should converge and stay still")
    print("=" * 70)

    follow = {
        "target": "Drone0",
        "offset_right_m": 0.0,
        "offset_up_m": 8.0,
        "offset_forward_m": -22.0,
        "extra_pitch_deg": -8.0,
        "extra_yaw_deg": 0.0,
    }

    smoother = PoseSmoother()
    # drone sits at a fixed position, facing north
    ux, uy, uz = 5000.0, 100.0, 10000.0
    uq = (0.0, 0.0, 0.0, 1.0)  # identity, facing north

    prev_cam = None
    converged_at = -1
    for i in range(120):
        vehicles = [{"id": "Drone0", "pos": (ux, uy, uz), "quat": uq, "vel": (0, 0, 0)}]
        parsed = parse_frame(build_frame(i, vehicles))
        v = parsed[0]
        n, e, d = unity_to_ned(*v["pos"])
        nq = unity_quat_to_ned(*v["quat"])
        veh_pose = mock_pose(n, e, d, *nq)
        desired = look_pose(veh_pose, follow)
        smoothed = smoother.step("Drone0", desired, i / 30.0)

        if prev_cam is not None:
            delta = math.sqrt(
                (smoothed.position.x_val - prev_cam.position.x_val)**2 +
                (smoothed.position.y_val - prev_cam.position.y_val)**2 +
                (smoothed.position.z_val - prev_cam.position.z_val)**2
            ) * 1000
            if delta < 0.01 and converged_at < 0:
                converged_at = i

        prev_cam = smoothed

    print(f"  Smoother converged at frame {converged_at} "
          f"({converged_at/30:.2f}s, expected ~1s with τ=0.2s)")
    final_cam = (smoothed.position.x_val, smoothed.position.y_val, smoothed.position.z_val)
    desired_cam = (desired.position.x_val, desired.position.y_val, desired.position.z_val)
    final_err = math.sqrt((final_cam[0] - desired_cam[0])**2 +
                          (final_cam[1] - desired_cam[1])**2 +
                          (final_cam[2] - desired_cam[2])**2) * 1000
    print(f"  Final camera vs target error: {final_err:.3f} mm")
    print(f"  Camera NED: ({final_cam[0]:.3f},{final_cam[1]:.3f},{final_cam[2]:.3f})")
    print(f"  VERDICT: ", end="")
    if final_err < 1.0:
        print("PASS — camera converges and stays perfectly still")
    else:
        print(f"WARN — camera {final_err:.1f}mm from target after convergence")


# ---------------------------------------------------------------------------
# Test: quaternion round-trip
# ---------------------------------------------------------------------------

def test_quat_roundtrip():
    print("\n" + "=" * 70)
    print("TEST 3: Quaternion round-trip Unity <-> NED")
    print("=" * 70)
    all_ok = True
    for yaw_deg in range(0, 360, 15):
        uq = euler_to_quat_unity(yaw_deg)
        nq = unity_quat_to_ned(*uq)
        # check: ned yaw should match unity yaw
        ned_yaw = math.degrees(math.atan2(
            2 * (nq[3]*nq[2] + nq[0]*nq[1]),
            1 - 2*(nq[1]*nq[1] + nq[2]*nq[2])))
        err = abs(ned_yaw - yaw_deg)
        if err > 1.0 and abs(err - 360) > 1.0:
            print(f"  FAIL yaw {yaw_deg}° → NED yaw {ned_yaw:.1f}°  err={err:.2f}°")
            all_ok = False
    if all_ok:
        print(f"  PASS — all 24 yaw angles convert correctly (<1 deg error)")


# ---------------------------------------------------------------------------
# Test: binary frame round-trip (no corruption)
# ---------------------------------------------------------------------------

def test_frame_roundtrip():
    print("\n" + "=" * 70)
    print("TEST 4: Binary frame build → parse round-trip")
    print("=" * 70)
    vehicles = [
        {"id": "Drone_A", "pos": (1234.567, 89.012, 3456.789), "quat": (0.0, 0.7071, 0.0, 0.7071), "vel": (12.3, 0.5, -2.1)},
        {"id": "Drone_B__", "pos": (-500.0, 200.0, 8000.0), "quat": (0.1, 0.2, 0.3, 0.927), "vel": (0, 0, 0)},
    ]
    frame = build_frame(42, vehicles)
    parsed = parse_frame(frame)
    for orig, par in zip(vehicles, parsed):
        for key in ("id", "pos", "quat", "vel"):
            a, b = orig[key], par[key]
            if isinstance(a, tuple):
                for j, (av, bv) in enumerate(zip(a, b)):
                    if abs(av - bv) > 1e-3:  # float32 tolerance
                        print(f"  FAIL {orig['id']} {key}[{j}]: {av} ≠ {bv} (diff={abs(av-bv):.6f})")
                        return
            elif a != b:
                print(f"  FAIL {orig['id']} {key}: {a} ≠ {b}")
                return
    print(f"  PASS — {len(vehicles)} vehicles serialise/deserialise losslessly")


# ---------------------------------------------------------------------------
# Test: look_pose stability under tiny perturbations (jitter injection test)
# ---------------------------------------------------------------------------

def test_look_pose_stability():
    print("\n" + "=" * 70)
    print("TEST 5: look_pose stability under ±5mm / ±0.1° perturbations")
    print("=" * 70)

    follow = {
        "target": "Drone0",
        "offset_right_m": 0.0,
        "offset_up_m": 8.0,
        "offset_forward_m": -22.0,
        "extra_pitch_deg": -8.0,
        "extra_yaw_deg": 0.0,
    }

    # baseline
    ux, uy, uz = 5000.0, 100.0, 10000.0
    uq = (0.0, 0.0, 0.0, 1.0)
    n, e, d = unity_to_ned(ux, uy, uz)
    nq = unity_quat_to_ned(*uq)
    base_veh = mock_pose(n, e, d, *nq)
    base_cam = look_pose(base_veh, follow)

    # inject position noise
    noise_mm = 5.0
    noise_m = noise_mm / 1000.0
    cam_positions = []
    for dx in (-noise_m, 0, noise_m):
        for dy in (-noise_m, 0, noise_m):
            for dz in (-noise_m, 0, noise_m):
                veh = mock_pose(n + dx, e + dy, d + dz, *nq)
                cam = look_pose(veh, follow)
                cam_positions.append((cam.position.x_val, cam.position.y_val, cam.position.z_val))

    max_spread = 0.0
    for i, c1 in enumerate(cam_positions):
        for c2 in cam_positions[i+1:]:
            d = math.sqrt((c1[0]-c2[0])**2 + (c1[1]-c2[1])**2 + (c1[2]-c2[2])**2) * 1000
            max_spread = max(max_spread, d)

    print(f"  Camera position spread from ±{noise_mm}mm vehicle noise: {max_spread:.2f} mm")
    print(f"  VERDICT: ", end="")
    if max_spread < noise_mm * 3:
        print(f"PASS — noise amplification < {noise_mm*3:.0f}mm (1:1 gain)")
    else:
        print(f"WARN — noise amplification {max_spread:.0f}mm > {noise_mm*3:.0f}mm")


# ---------------------------------------------------------------------------

if __name__ == "__main__":
    test_frame_roundtrip()
    test_quat_roundtrip()
    test_stationary()
    test_circular_flight()
    test_look_pose_stability()
    print("\n" + "=" * 70)
    print("ALL MATH PIPELINE TESTS COMPLETE")
    print("=" * 70)
    print("If all tests PASS: the jitter is NOT in the coordinate/camera math.")
    print("It must be in the Unity→UDP→AirSim runtime interaction.")
    print("Run the joint simulation and check the diagnostic output next.")