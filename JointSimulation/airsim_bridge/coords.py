"""Unity ENU (m) <-> AirSim NED (m) <-> Unreal world (cm).

Shared geographic origin (WGS84), identical in Unity whitebox and
Documents/AirSim/settings.json OriginGeopoint:

    lon = 113.94358909787222
    lat = 22.48486839226679
    alt = 0

Frames
------
Unity world, metres, Y-up left-handed:
    x = East, y = Up, z = North
    yaw 0 looks +Z (north); +yaw turns toward +X (east)

AirSim / AirLib NED, metres, right-handed, relative to PlayerStart.
With PlayerStart at Unreal origin this equals world NED:
    x = North, y = East, z = Down
    yaw 0 looks +X (north); +yaw turns toward +Y (east)

Unreal world, centimetres:
    X = North, Y = East, Z = Up
    (city static meshes use scale Y = -1 to undo OBJ import mirroring;
    AirSim NED still uses Unreal +Y as East after that correction.)

Vector mapping (AirSim Unity plugin DataManager):
    NED = (Unity.z, Unity.x, -Unity.y)
    Unity = (NED.y, -NED.z, NED.x)

Quaternion mapping. Unity +Y yaw (nose from north toward east) must stay
+yaw in NED. The AirSim Unity plugin's SetToAirSim negates that yaw, so the
z component keeps Unity's y instead:

    NED quat (x,y,z,w) = (Unity.z, -Unity.x, Unity.y, Unity.w)
    Unity = (-NED.y, NED.z, NED.x, NED.w)
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable, Sequence, Tuple

ORIGIN_LON = 113.94358909787222
ORIGIN_LAT = 22.48486839226679
ORIGIN_ALT_M = 0.0

# Unity world metres of the 26 lockers (ground, y=0), from ShenzhenSKPWhitebox.
LOCKERS_UNITY = (
    ("01_Nanshan iPark", 6263.40, 12457.30),
    ("02_SIGS", 3733.30, 11308.60),
    ("03_Dashahe", 1662.60, 8066.90),
    ("04_HappyValley", 3801.20, 6424.10),
    ("05_EnergyTower", -15.40, 5891.40),
    ("06_Shuguang", 1297.40, 5903.10),
    ("07_WindowWorld", 2654.00, 5468.80),
    ("08_TalentPark", 236.20, 2919.30),
    ("09_Haifeng", 206.80, 148.90),
    ("10_Lianhuashan", 11283.30, 7424.10),
    ("11_Lizhi", 16129.30, 7308.10),
    ("12_Library", 11630.30, 7042.60),
    ("13_Xiangmi", 7971.60, 6610.90),
    ("14_Kerry", 11524.60, 5789.90),
    ("15_CentralPark", 13562.80, 5610.10),
    ("16_Mangrove", 4968.80, 4036.00),
    ("17_GuiguT1", 8715.70, 20196.90),
    ("18_Qingyuehui", 7811.60, 19565.90),
    ("19_Xinying", 7168.60, 19309.00),
    ("20_Shouchuang", 9417.60, 19215.10),
    ("21_BookCity", 9610.00, 19121.40),
    ("22_Chunjiang", 9937.30, 17349.10),
    ("23_Hongrongyuan", 8764.10, 14952.90),
    ("24_Huilong", 8902.20, 14188.10),
    ("25_Beizhan", 8000.10, 13648.90),
    ("26_Baishilong", 9423.30, 12574.20),
)


@dataclass(frozen=True)
class UnityPose:
    east: float
    up: float
    north: float
    qx: float = 0.0
    qy: float = 0.0
    qz: float = 0.0
    qw: float = 1.0


@dataclass(frozen=True)
class NedPose:
    north: float
    east: float
    down: float
    qx: float = 0.0
    qy: float = 0.0
    qz: float = 0.0
    qw: float = 1.0


def unity_to_ned(east: float, up: float, north: float) -> Tuple[float, float, float]:
    return north, east, -up


def ned_to_unity(north: float, east: float, down: float) -> Tuple[float, float, float]:
    return east, -down, north


def unity_quat_to_ned(qx: float, qy: float, qz: float, qw: float) -> Tuple[float, float, float, float]:
    # Plugin SetToAirSim uses (z, -x, -y, w), which mirrors yaw.
    # Keeping Unity y gives east for a +90 deg Unity yaw.
    return qz, -qx, qy, qw


def ned_quat_to_unity(qx: float, qy: float, qz: float, qw: float) -> Tuple[float, float, float, float]:
    return -qy, qz, qx, qw


def unity_pose_to_ned(p: UnityPose) -> NedPose:
    n, e, d = unity_to_ned(p.east, p.up, p.north)
    qx, qy, qz, qw = unity_quat_to_ned(p.qx, p.qy, p.qz, p.qw)
    return NedPose(n, e, d, qx, qy, qz, qw)


def ned_to_unreal_cm(north: float, east: float, down: float) -> Tuple[float, float, float]:
    return north * 100.0, east * 100.0, -down * 100.0


def unity_to_unreal_cm(east: float, up: float, north: float) -> Tuple[float, float, float]:
    n, e, d = unity_to_ned(east, up, north)
    return ned_to_unreal_cm(n, e, d)


def roundtrip_ok(east: float, up: float, north: float, eps: float = 1e-9) -> bool:
    n, e, d = unity_to_ned(east, up, north)
    e2, u2, n2 = ned_to_unity(n, e, d)
    return abs(e2 - east) < eps and abs(u2 - up) < eps and abs(n2 - north) < eps


def locker_ned(up: float = 8.0) -> Iterable[Tuple[str, NedPose]]:
    for name, east, north in LOCKERS_UNITY:
        n, e, d = unity_to_ned(east, up, north)
        yield name, NedPose(n, e, d)


def format_table(rows: Sequence[Tuple[str, NedPose]]) -> str:
    lines = ["id                         Unity E,N           AirSim NED N,E,D"]
    for name, ned in rows:
        east = ned.east
        north = ned.north
        lines.append(
            f"{name:<24} ({east:8.1f},{north:8.1f})  "
            f"({ned.north:8.1f},{ned.east:8.1f},{ned.down:6.1f})"
        )
    return "\n".join(lines)
