"""Sanity checks for Unity ENU <-> AirSim NED. Run: python -m airsim_bridge.test_coords"""
from __future__ import annotations

import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from airsim_bridge.coords import (  # noqa: E402
    LOCKERS_UNITY,
    locker_ned,
    ned_quat_to_unity,
    roundtrip_ok,
    unity_quat_to_ned,
    unity_to_ned,
    unity_to_unreal_cm,
)


def test_roundtrip_lockers() -> None:
    for name, east, north in LOCKERS_UNITY:
        assert roundtrip_ok(east, 8.0, north), name


def test_haifeng() -> None:
    n, e, d = unity_to_ned(206.80, 8.0, 148.90)
    assert abs(n - 148.90) < 1e-6
    assert abs(e - 206.80) < 1e-6
    assert abs(d + 8.0) < 1e-6
    ux, uy, uz = unity_to_unreal_cm(206.80, 8.0, 148.90)
    assert abs(ux - 14890.0) < 1e-3
    assert abs(uy - 20680.0) < 1e-3
    assert abs(uz - 800.0) < 1e-3


def test_quat_identity() -> None:
    qx, qy, qz, qw = unity_quat_to_ned(0.0, 0.0, 0.0, 1.0)
    assert (qx, qy, qz, qw) == (0.0, 0.0, 0.0, 1.0)
    back = ned_quat_to_unity(qx, qy, qz, qw)
    assert back == (0.0, 0.0, 0.0, 1.0)


def test_quat_yaw90_roundtrip() -> None:
    # Unity yaw +90 deg about +Y: (0, sin45, 0, cos45)
    s = math.sin(math.pi / 4.0)
    c = math.cos(math.pi / 4.0)
    nq = unity_quat_to_ned(0.0, s, 0.0, c)
    assert abs(nq[0]) < 1e-9
    assert abs(nq[1]) < 1e-9
    assert abs(nq[2] - s) < 1e-9
    assert abs(nq[3] - c) < 1e-9
    uq = ned_quat_to_unity(*nq)
    assert abs(uq[0]) < 1e-9
    assert abs(uq[1] - s) < 1e-9
    assert abs(uq[2]) < 1e-9
    assert abs(uq[3] - c) < 1e-9


def main() -> int:
    test_roundtrip_lockers()
    test_haifeng()
    test_quat_identity()
    test_quat_yaw90_roundtrip()
    rows = list(locker_ned(8.0))
    print(f"passed 4 checks, {len(rows)} lockers")
    print("09_Haifeng NED", unity_to_ned(206.80, 8.0, 148.90))
    print("11_Lizhi   NED", unity_to_ned(16129.30, 8.0, 7308.10))
    print("17_GuiguT1 NED", unity_to_ned(8715.70, 8.0, 20196.90))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
