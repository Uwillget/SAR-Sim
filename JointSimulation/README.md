# Joint Simulation (Unity ↔ Unreal Engine / AirSim)

Unity is the motion master. Every aircraft pose produced by SAR-Sim's microscopic
simulation is streamed to a Python bridge, which teleports the matching AirSim
vehicle and the Unreal camera. AirSim contributes geometry and rendering only: it
does not integrate flight dynamics, and its collision response is not part of any
simulation result.

This is an experimental pilot rather than a finished product. What works today is the
one-way path: poses leave Unity, the bridge creates vehicles, and the camera follows.
What does not exist yet is anything that depends on AirSim reporting state back —
pose read-back, closed-loop coordination, sensor-driven behaviour, or collision
feedback into the traffic model. The last three columns of `bridge_diag.csv` are
reserved for that read-back and are currently written as zero. Treat the module as a
visualisation bridge, not as two coupled simulators.

```text
Unity Editor                                airsim_bridge                  Unreal Engine 4.27 + AirSim 1.8.1
SimulationManager / AirSimJointBridge   ->  joint_bridge.py            ->  one pawn per Unity aircraft
ITS1 frame, 30 Hz, UDP 127.0.0.1:49001      simSetVehiclePose              ExternalCamera
                                            simSetObjectPose
```

## Contents

| Path | Role |
|---|---|
| `airsim_bridge/joint_bridge.py` | Main bridge: UDP receiver, per-aircraft vehicle management, camera driver, diagnostics |
| `airsim_bridge/coords.py` | ENU ↔ NED conversion, quaternion mapping, shared geographic origin, locker reference table |
| `airsim_bridge/kinematic_slave.py` | Legacy single-stream alternative: consumes newline-free JSON `{"frame":"unity_enu","vehicles":[...]}` from `AirSimKinematicPublisher.cs` instead of the binary ITS1 frames, and can plot a locker demo and probe a locker without Unity |
| `airsim_bridge/test_coords.py` | Conversion round-trips and locker table checks |
| `airsim_bridge/test_pipeline.py` | Frame parsing, smoother convergence, jitter detection — no Unity, AirSim or socket needed |
| `settings.example.json` | AirSim host template: `Manual` view mode, shared `OriginGeopoint`, camera director defaults |

`joint_bridge.py` and `kinematic_slave.py` are alternatives, not components that run together: both bind UDP 49001 by default and both teleport the same AirSim vehicles. Run one at a time, or give the second one a different `--port` and matching publisher port.

## Quick start

```text
pip install airsim==1.8.1
copy settings.example.json %USERPROFILE%\Documents\AirSim\settings.json
cd JointSimulation
python -m airsim_bridge.joint_bridge --launch-airsim
```

Omit `--launch-airsim` to attach to an AirSim instance that is already running, or
start Unreal yourself and then run `python -m airsim_bridge.joint_bridge`. Use
`--bind` / `--port` when 49001 is already taken.

Then in Unity: select `SimulationManager`, expand `Play Control`, tick
`Start Joint AirSim Simulation`, and start the simulation. The bridge process is
started by the run, not by Play Mode, and is terminated when the run stops.

## Requirements

| Component | Version | Notes |
|---|---|---|
| Unreal Engine | 4.27 | The host project ships at `UnrealHost/SZ_TestArea` |
| AirSim | 1.8.1 | Plugin plus the matching Python client; the plugin is not redistributed |
| Python | 3.10+ | Must be able to `import airsim` |

The Unreal host project lives in this repository at `UnrealHost/SZ_TestArea/`. It does
not contain the AirSim plugin: copy or link AirSim 1.8.1 into `Plugins/AirSim` before
opening it. The Shenzhen whitebox scene that the map's mesh was exported from is too
large for Git and is published separately — see `EXTERNAL_ASSETS.md`.

`settings.json` drives the host. Two fields are load-bearing:

- `"ViewMode": "Manual"` — in every other view mode `ACameraDirector::Tick`
  interpolates the camera toward the aircraft on every frame and never converges,
  which appears as on-screen shake. `Manual` only applies keyboard deltas, so the
  camera stays where the bridge puts it.
- `OriginGeopoint` — must match `ORIGIN_LAT` / `ORIGIN_LON` in
  `airsim_bridge/coords.py` and the origin baked into the Unity scene, or the two
  worlds are offset by a constant.

## Environment variables

| Variable | Purpose | Default |
|---|---|---|
| `UE4EDITOR_EXE` | `UE4Editor.exe`, used only by `--launch-airsim` | standard UE 4.27 install path |
| `UE4_PROJECT` | `.uproject` to launch, used only by `--launch-airsim` | `SZ_TestArea.uproject` |
| `SAR_FOLLOW_CAMERA` | Where the camera-follow state is saved | `Documents/AirSim/follow_camera.json` |

The Unity side reads `SAR_BRIDGE_PYTHON` (interpreter) and `SAR_BRIDGE_WORKDIR`
(this folder) from the environment, or from the `AirSimJointBridge` component's
Inspector overrides.

## Hotkeys

Registered globally, so they act while the Unreal window has focus:

| Key | Action |
|---|---|
| `H` | Toggle follow mode / free-flight mode |
| `N` | Cycle the followed aircraft |
| `J` / `L` | Camera closer / farther |
| `U` / `O` | Camera higher / lower |

In free-flight mode the bridge stops touching the camera and Unreal's own
`Manual`-mode keys take over: arrows move, `PageUp`/`PageDown` change altitude,
`A`/`D` yaw, `W`/`S` pitch, `Q`/`E` roll, `Shift`/`Ctrl` change speed. AirSim's own
camera hotkeys (`F`, `B`, `/`, `M`, `K`, `-`, `I`) leave `Manual` view mode and
bring the shake back; press `H` to return to bridge control.

## Frame conventions

| Space | Axes | Units |
|---|---|---|
| Unity world | x = east, y = up, z = north; yaw 0 faces north, positive yaw turns east | metres |
| AirSim / AirLib NED | x = north, y = east, z = down; yaw 0 faces north, positive yaw turns east | metres |
| Unreal world | X = north, Y = east, Z = up (city meshes use scale Y = −1 to undo OBJ import mirroring) | centimetres |

Vehicles are teleported with `simSetVehiclePose` and the camera with
`simSetObjectPose`. The bridge applies no spawn-offset correction, so keep the
vehicle spawn in `settings.json` at the Unreal origin (the example file does);
moving it shifts every mirrored aircraft by the same offset.

## Diagnostics

- The console reports the connected AirSim instance, a frame counter, the current
  mode, and the followed aircraft every two seconds.
- `bridge_diag.csv` (written next to `joint_bridge.py`, ignored by Git) samples every
  fifth frame: the raw Unity pose, the converted NED pose, and the desired and
  smoothed camera poses. The last three columns are reserved for the set/get delta
  and are currently written as zero.
- If the vehicles do not move: check whether the console printed a hotkey or API
  warning. AirSim API control must stay disabled — `SimpleFlight` enters hover mode
  when offboard goals stop arriving and then applies forces on every physics step,
  which fights the teleports. The bridge teleports and then zeroes the kinematics.
- If the camera does not move: the console says whether `ExternalCamera` exists.
  A warning there means the Unreal map lacks the actor that `Manual` view mode
  renders through.

## Tests

```text
python airsim_bridge/test_coords.py
python airsim_bridge/test_pipeline.py
```

`test_pipeline.py` exercises the same thresholds the bridge uses and prints a
per-test verdict. A failure there means the problem is in the conversion or camera
math rather than in the Unity → UDP → AirSim runtime path.

A full run currently ends with two `PASS` verdicts and one `WARN`: the camera
converges and stays still, and the frame parsing / jitter detection passes, but the
smoother amplifies ±5 mm of vehicle noise into about 17 mm of camera-position spread,
against a 15 mm budget. It does not fail and it does not affect the recorded
simulation results; it is a known property of the follow-camera smoothing constants,
left as-is rather than retuned during the pilot.