# Structured Air Route Simulation System

[![Unity](https://img.shields.io/badge/Unity-2022.3.62f1c1-black?logo=unity)](https://unity.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows)](#ways-to-run)
[![AirSim](https://img.shields.io/badge/AirSim-1.8.1%20%2B%20UE%204.27-3949AB)](#joint-simulation-with-unreal-engine-and-airsim)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Download](https://img.shields.io/badge/Download-v1.0.0-2E7D32?logo=github)](https://github.com/Uwillget/SAR-Sim/releases/latest)
[![Demo](https://img.shields.io/badge/Demo-YouTube-FF0000?logo=youtube)](https://youtu.be/wICTW1oSdc0)

**English** | [简体中文](README.zh-CN.md)

> **Download the ready-to-run Windows application** from the [Releases page](https://github.com/Uwillget/SAR-Sim/releases/latest) — English and 简体中文 portable archives, no Unity installation required. The source tree is for development; the executables are published only as release assets. See [Releases](#releases) for what each archive contains.

> **Watch the demo first:** [SAR-Sim feature walkthrough](https://youtu.be/wICTW1oSdc0) on YouTube — a screen recording of the English portable Windows build, showing the workbench features in use. It is the fastest way to see what the application does before downloading it or building it from source.

The Structured Air Route Simulation System (SAR-Sim) is a Unity-based research platform for urban low-altitude public-route systems. It combines city geometry, multilayer air-route networks, vertiport ground infrastructure, holding structures, connection corridors, traffic-demand authoring, microscopic simulation, and result inspection in one editable environment.

The project provides two equivalent operational workflows:

- the Unity Editor project for source development, scene authoring, testing, and builds; and
- a portable Windows workbench for users who do not need Unity.

Both workflows can author routes and traffic flows, edit simulation parameters, run single or batch experiments, and export result tables. The Unity workflow additionally exposes source-level inspectors and build tools; the packaged workbench provides a guided desktop UI for the same simulation model.

The Unity project can also stream every simulated aircraft into an Unreal Engine host running the AirSim plugin, so the same traffic can be inspected inside a photorealistic city model while Unity remains the sole source of motion. This joint simulation is a Unity-only capability and is not part of the portable Windows application. It is an experimental pilot rather than a finished product: the one-way pose stream works, while pose read-back, closed-loop coordination, and control that reacts to what AirSim reports are left for future work — see [Joint simulation status](#joint-simulation-status).

> SAR-Sim is a research prototype. It is not an airworthiness-certified or operational safety system. Collision, reservation, capacity, and performance results must be interpreted together with the selected parameters, input-data quality, and repeated experiments.

## Main capabilities

### Airspace and facility authoring

- Edit axis-network nodes, edges, and waypoints, including add, move, delete, undo, and detail inspection.
- Rename, duplicate, delete, reorder, and vertically reposition altitude layers while preserving plan coordinates.
- Combine the global `Air Network` display switch with each layer's eye control: a layer is rendered only when both switches are on.
- Freeze a layer with its snowflake control. A frozen layer cannot be edited, renamed, moved, deleted, or reordered, and other layers cannot be reordered across it.
- Regenerate derived lanes after axis-geometry changes. An edge receives no lanes when building clearance cannot support at least two lanes.
- Highlight buildings above a configurable height; runtime-imported city meshes also participate in lane-clearance checks.
- Edit vertiport boundaries, rectangular/circular/freehand helipads, helipad rotation, ground-approach nodes, and directed approach edges.
- Build holding structures from anchors, ring height/radius/angle/node count, and directed ramps.
- Edit vertiport corridors and lane-to-lane interlayer corridors. Interlayer connections use editable cubic Bezier geometry.
- Use orthographic top view for the four main editing workspaces while retaining free three-dimensional inspection.

### Routes and traffic flow

- Select and focus origin and destination helipads.
- Generate the shortest connected lane-level route through vertiport corridors, ordinary lanes, and interlayer corridors.
- Author a manual route as an ordered axis-edge sequence. The departure edge is locked, and every appended edge is checked for lane-level connectivity.
- Resolve an axis-edge sequence into an executable lane sequence, automatically inserting adjacent lane changes and required interlayer connectors.
- Add individual missions or periodic traffic flows defined by start time, interval, and aircraft count.
- Inspect, edit, remove, import, and export pending missions in JSON or XML.
- Use the same route resolver and ordered centerline-selection rules in the Unity `SimulationManager` Inspector/Scene view and the packaged desktop UI.

### Simulation and experiment control

- Run file-driven JSON/XML demand or continuous random demand.
- Configure aircraft motion, following distance, lane-change behavior, reservation buffers, wait thresholds, signal timing, information delay, and positioning error.
- Configure random fleet size, turnaround time, and maximum run duration.
- Start, stop, pause, resume, and run at `0.25x-4x` time scale.
- Optionally pause and focus on collision events.
- Count each continuous unordered aircraft-pair overlap once and export an auditable collision breakdown. The lane, approach, ramp, corridor, helipad, and unclassified/mixed-resource subtotals are exhaustive and a zero residual verifies their sum against `Total Collisions`.
- Display bounded aircraft trails and switch between overview and aircraft-follow cameras.
- Choose one of three mutually exclusive runtime lane-color modes: authored lane-type colors with no live recoloring; live aircraft load, where empty lanes are gray and occupied lanes progress from green to red as real-aircraft count rises; or passability, where open lanes are green and blocked lanes are red. Synthetic signal/reservation sentinel entries are excluded from the live-load count, and intersection-signal markers remain a separate visual layer.
- Temporarily show or hide any authored altitude layer from the Simulation page while observing a run. Airspace Structure, Routes & Flow, Simulation, and Data Visualization keep independent view masks, and every control displays the layer's authored name verbatim rather than a hard-coded lower/upper label.
- Define any number of parameter-sweep variables from the shared parameter catalog. The Cartesian grid is run automatically and written incrementally to a CSV experiment table.
- Restore the pre-sweep configuration when a batch experiment stops or completes.

### Analytics and visualization

- Archive complete per-mission trajectories, including repeated trips by reused random-demand aircraft.
- Select and overlay multiple trajectories.
- Display 3D traffic heatmaps, route previews, and experiment statistics; generate publication-quality, paged Python traffic figures with one full-size square road-network overview per selected altitude layer followed by one independently framed full-size overview per archived vertiport, without lane-to-lane connector clutter.
- Retain a scene-scoped Simulation History for single runs and parameter-sweep batches instead of overwriting the previous experiment's results.
- Recycle one standalone history record or one complete batch from Data Visualization. Records are moved to the active scene's `SimulationHistory/.Trash/` folder for manual recovery; an individual batch member cannot be removed while leaving its parent summary inconsistent.
- Reload one concrete historical run into the 3D lane-traversal/resource-reservation heatmap and trajectory view, inspect its archived `MetricsTable.csv`, or inspect a batch parent's original `BatchExperimentRunSummaryTable.csv` one experiment at a time in a paged two-column view.
- Select several completed runs (or a batch parent) for comparison. Output metrics (Y) are grouped into collapsible categories. High-resolution Python bar-comparison figures accept multiple Y metrics and use the selected experiment records directly on the X axis; they do not require a sensitivity input parameter. Parameter-response figures accept one to three input parameters (X) and multiple Y metrics: one X uses response curves, two X use square annotated heatmaps, and three X use semi-transparent square heatmap planes stacked in three-dimensional depth by the tested levels of the third parameter. Cumulative metrics are normalized per second only when simulation horizons differ materially.
- Zoom, pan, reset, and export displayed charts and result tables.
- Toggle city, network, approach, holding, facility, vertiport-corridor, interlayer-corridor, and signal layers independently.
- Export metrics, occupancy records, simulation recordings, and trajectory files.

### Scene and display management

- Keep each scenario in an independent tab with its own city, network, and vertiport data.
- Keep each scenario's Simulation History inside that scenario folder; changing the active tab changes the available history list as well as the editable topology.
- Create, duplicate, rename, save, close, and reopen scenarios.
- Import `.sarcity`, `.obj`, and supported `.dxf` city geometry and position it in the current view.
- Use the orientation cube for perspective/orthographic and six-direction view changes.
- Run in a resizable window or borderless fullscreen mode.
- Hide the complete runtime workbench with the on-screen `Hide UI` button or `F10`. A small `Show UI` button remains available, so the Unity Scene/Game view can be used without desktop-UI obstruction.

### Joint simulation with Unreal Engine and AirSim (experimental pilot)

- Mirror every simulated aircraft into an Unreal Engine 4.27 host running AirSim 1.8.1, using Unity as the motion master.
- Stream poses over a compact binary UDP frame at 30 Hz, so a run with dozens of aircraft needs no per-aircraft RPC call from Unity.
- Create one AirSim vehicle per Unity aircraft automatically; no manual `settings.json` entry is required for each mission.
- Drive the Unreal camera from the bridge: follow the selected aircraft with adjustable distance and height, or hand control back to Unreal's own manual-mode camera keys.
- Convert Unity ENU metres to AirSim NED metres and rotate quaternions with a shared, unit-tested convention, so Unreal geometry and Unity geometry stay aligned at the same WGS84 origin.
- Write a per-frame diagnostic CSV (`bridge_diag.csv`) with the raw Unity pose, the converted NED pose, and the desired and smoothed camera poses for offline jitter analysis.

This module is a pilot integration, not a finished product. Motion and camera are one-way today; reading AirSim state back and closing a coordination loop over it are explicitly out of scope for this release.

## Ways to run

For an illustrated walkthrough of both equivalent workflows, see the [English Quick Start](OperationGuide/QuickStart/README.md).

### Portable Windows application

Download the complete Windows x64 archive from the [**Releases page**](https://github.com/Uwillget/SAR-Sim/releases/latest). Extract the archive and keep the executable together with its `_Data` folder and all supplied directories.

The [feature walkthrough](https://youtu.be/wICTW1oSdc0) on YouTube was recorded on the English build of this archive, so it shows exactly what you get after extracting it.

The local build command produces two clean release directories from one validated
Windows player binary:

```text
Builds/Releases/en-US/
Builds/Releases/zh-CN/
```

Each directory contains an executable-adjacent `SAR.locale.json`. The English
package selects `en-US`; the Chinese package selects `zh-CN`. Source code, scene
object names, and Inspector labels remain English in both cases.

Do not copy only the executable. The portable application reads and writes application-owned data beside the executable, so the extracted directory must be writable.

### Unity Editor project

1. Open the repository root in Unity Hub.
2. Use the locked project version `2022.3.62f1c1`.
3. Open `Assets/Resources/Scenes/1.unity`.
4. Select the relevant manager object in the Hierarchy and operate through its Inspector and the Scene view.
5. Select `SimulationManager` to author routes/flows and control simulation; enter Play Mode only when execution is required.

The packaged runtime workbench is disabled by default in Unity Play Mode. In the
`SimulationManager` Inspector, use `Editor Play Mode Runtime UI` to enable it when
you need to test exact Windows-application behavior. Disabling it while playing
removes the generated Canvas, EventSystem, navigation, editing, routing, analytics,
and file-drop objects instead of merely making them invisible. This Editor-only
preference is persistent and does not disable the UI in Windows builds.

### Scenes in this repository

The repository ships one Unity scene, `Assets/Resources/Scenes/1.unity`, and it is the
scenario used for the benchmark experiments: the Xinjiekou study area, with two altitude
layers (75 m and 125 m), 102 axis nodes, 160 axis edges, 2 interlayer corridors, 7
vertiports and 126 helipads over a 978-building city model. It is self-contained and is
the only enabled build scene, so the portable application and the paper's experiments
use exactly this data.

Two other scenes exist in the authors' working copy and are deliberately not shipped:

| Scene | Why it is not here |
|---|---|
| `SampleScene.unity` | An early single-tile CADMapper experiment, superseded by `1.unity` and kept only as a development scratch scene. |
| `ShenzhenSKPWhitebox.unity` | A 628 MB authoring scene for the joint-simulation demonstration. Too large for Git; published separately — see [EXTERNAL_ASSETS.md](EXTERNAL_ASSETS.md). |

If you want to run the bridge against the Shenzhen demonstration, download that scene
first. Running the bridge with `1.unity` instead exercises the same code path but the
Unreal map will not match the Xinjiekou geometry.

## Unity Editor workflow

The primary Unity workflow uses manager Inspectors and the Scene view. `Tools > SAR Workbench` is an optional overview/shortcut window with five tabs:

1. `Overview` — current topology, facilities, and demand counts.
2. `Simulation` — demand import, playback controls, links to the complete `SimulationConfig`, and batch controls.
3. `Network` — shortcut to the detailed network Inspector, lane generation, schema validation, and export.
4. `Facilities` — shortcut to the detailed vertiport/approach/holding Inspector and export.
5. `Build` — packaged-data checks and Windows x64 build.

Route and demand authoring is intentionally located in the `SimulationManager` Inspector:

1. Expand `Routes & Traffic Flow (Inspector + Scene view)` and refresh the active topology.
2. Choose origin and destination helipads.
3. Generate the automatic shortest route, or choose `Manual: click centerlines`.
4. In manual mode, click axis centerlines directly in the Scene view. The selected route is highlighted red/orange and numbered in traversal order; the departure edge is locked, and clicking an already selected edge rolls the route back to that edge.
5. Confirm the route, then add one mission or a periodic traffic flow. Pending missions can be edited, removed, imported, and exported in the same Inspector.

Recommended order:

```text
Edit topology
  -> generate derived lanes
  -> validate and export topology
  -> author/import routes and flows
  -> configure parameters
  -> run a single or batch experiment
  -> inspect outputs
  -> validate and build
```

Stop the simulation before editing topology or applying a new parameter set.

## Joint simulation with Unreal Engine and AirSim

Unity remains the motion master. Every aircraft pose produced by the microscopic simulation is forwarded to a small Python process, which teleports the matching AirSim vehicle and the Unreal camera. No flight dynamics, collision response, or sensing is delegated to AirSim: the Unreal scene is a high-fidelity viewer for traffic that Unity has already resolved.

```text
Unity (UnityEditor)                         Python bridge                    Unreal Engine 4.27
AircraftAgent / FlightController            airsim_bridge.joint_bridge       AirSim 1.8.1 plugin
        |                                           |                                |
        | ITS1 binary frame, 30 Hz                  | simSetVehiclePose              |
        +------ UDP 127.0.0.1:49001 -------------->|------------------------------->| aircraft pawns
        |                                           | simSetObjectPose               |
        |                                           +------------------------------->| ExternalCamera
```

### Joint simulation status

This module is an experimental pilot shipped with the source tree so the integration can be inspected and extended. Be explicit about what it does and does not do when you use or cite it:

| Capability | Status |
|---|---|
| Unity → AirSim pose streaming (one per simulated aircraft) | Working |
| Automatic AirSim vehicle creation per Unity aircraft | Working |
| Bridge-driven follow camera with keyboard control | Working |
| ENU → NED conversion and quaternion mapping, with unit tests | Working |
| Diagnostic CSV capture | Working, partially reserved columns (see below) |
| AirSim → Unity pose read-back driving the simulation | **Not implemented** |
| Closed-loop coordination, conflict resolution or control over AirSim state | **Not implemented** |
| Collision or sensing feedback from Unreal feeding back into the traffic model | **Not implemented** |

The readback columns in `bridge_diag.csv` exist as placeholders and are currently written as zero. Anything that would depend on AirSim reporting state back to Unity — feedback control, sensor-driven behaviour, coordinated multi-host operation — is deliberately left to future work. Treat the module as a visualisation bridge, not as a co-simulation of two coupled simulators.

The bridge is also scene-agnostic but was developed against the Shenzhen whitebox scene, which is too large for Git. See [EXTERNAL_ASSETS.md](EXTERNAL_ASSETS.md) for where to get that scene and how the pieces fit together.

### What is streamed

One UDP datagram per frame carries every aircraft:

| Field | Encoding |
|---|---|
| Header | `ITS1` magic, version byte, `uint16` vehicle count, `uint32` sequence number |
| Per vehicle | 1-byte name length, UTF-8 name, then ten little-endian `float32` values |
| Values | Unity position `x y z`, Unity rotation `x y z w`, Unity velocity `x y z` |

Unity aircraft names are filtered to `A-Z a-z 0-9 _` before transmission, because AirSim vehicle names must be valid identifiers; `M-235753-0001` therefore arrives as `M2357530001`. The bridge keys its vehicles by the sanitised name.

### Prerequisites

| Component | Version used here | Notes |
|---|---|---|
| Unity | 2022.3.62f1c1 | Project version is locked |
| Unreal Engine | 4.27 | Any project with the AirSim plugin compiled in |
| AirSim | 1.8.1 | Plugin plus the matching Python client |
| Python | 3.10 or newer | Needs the `airsim` package: `pip install airsim==1.8.1` |

### Configure the AirSim host

1. Copy `JointSimulation/settings.example.json` to `Documents/AirSim/settings.json` on the machine that runs Unreal.
2. Keep `"ViewMode": "Manual"`. In every other view mode `ACameraDirector::Tick` continuously interpolates the camera toward the aircraft and never converges, which shows up as on-screen shake. In `Manual` mode the director only applies keyboard deltas, so the camera stays exactly where the bridge puts it.
3. Keep the `OriginGeopoint` in that file unchanged. It must match the origin compiled into `airsim_bridge/coords.py` and into the Unity scene; otherwise the two worlds are offset.
4. Leave the `Vehicles` spawn coordinates alone as well. `simSetVehiclePose` is anchored at the spawn point and the bridge does not compensate, so a moved spawn displaces that vehicle by the same amount. The example spawns `ChaseCam` two metres up on purpose.
5. Start the AirSim project (Play-In-Editor or a packaged build) before or after Unity — the bridge waits for the RPC port and can launch Unreal itself.

### Configure the Unity side

1. Select `SimulationManager` and expand `Play Control`.
2. Enable `Start Joint AirSim Simulation`.
3. If the bridge folder or interpreter is not on `PATH`, fill in the `AirSimJointBridge` component. You can instead set the environment variables, which is convenient when the paths differ per machine:

| Variable | Purpose | Default |
|---|---|---|
| `SAR_BRIDGE_PYTHON` | Python interpreter that has the `airsim` package | `python.exe` resolved from `PATH` |
| `SAR_BRIDGE_WORKDIR` | Folder that contains the `airsim_bridge` package | current directory |
| `UE4EDITOR_EXE` | `UE4Editor.exe`, used only by `--launch-airsim` | standard UE 4.27 install path |
| `UE4_PROJECT` | `.uproject` to launch, used only by `--launch-airsim` | `SZ_TestArea.uproject` |
| `SAR_FOLLOW_CAMERA` | Where camera-follow state is saved | `Documents/AirSim/follow_camera.json` |
| `SAR_UE_IMPORT_DIR` | Output folder for `ITSAC/Export *` mesh exporters | `<project>/ue_import` |

4. Enter Play Mode and start the simulation. The bridge process starts with the run, not with Play Mode, and is terminated when the run stops.

### Run the bridge by hand

The bridge is a normal Python entry point, so you can watch its console, or point it at an AirSim instance that Unity did not start:

```text
cd JointSimulation
python -m airsim_bridge.joint_bridge --launch-airsim
```

Omit `--launch-airsim` to attach to an AirSim that is already running. `--bind` and `--port` override the UDP endpoint if 49001 is taken.

### Camera controls

The bridge registers global hotkeys, so they act while the Unreal window has focus:

| Key | Action |
|---|---|
| `H` | Toggle follow mode and free-flight mode |
| `N` | Cycle the followed aircraft |
| `J` / `L` | Move the camera closer / farther |
| `U` / `O` | Raise / lower the camera |

In free-flight mode the bridge stops touching the camera, and Unreal's own `Manual`-mode keys take over: arrow keys move, `PageUp`/`PageDown` change altitude, `A`/`D` yaw, `W`/`S` pitch, `Q`/`E` roll, `Shift`/`Ctrl` change speed. AirSim's own camera hotkeys (`F`, `B`, `/`, `M`, `K`, `-`, `I`) leave `Manual` view mode and bring the shake back, so press `H` to return to bridge control afterwards.

### Behaviour worth knowing

- AirSim API control is deliberately never enabled. `SimpleFlight` enters hover mode when offboard goals stop arriving and then applies forces on every physics step, which fights the teleports. The bridge instead teleports and then zeroes the AirSim kinematics, so nothing accumulates between frames.
- Vehicles are created on demand with `simAddVehicle`, one per Unity aircraft. Runs that create new aircraft mid-simulation therefore keep working.
- Vehicle teleports go through `simSetVehiclePose`, which is anchored at the pawn's spawn point, and the camera is moved with `simSetObjectPose` in world-NED coordinates. The bridge applies no offset correction, so a vehicle spawned away from the Unreal origin is displaced by exactly that distance; the example settings spawn `ChaseCam` two metres up, which is deliberate.
- `bridge_diag.csv` is written next to the bridge source and sampled every fifth frame. It records the raw Unity pose, the converted NED pose, and the desired and smoothed camera poses; the final three columns are reserved for the set/get delta and are currently written as zero.
- Joint simulation is Editor-only, and the code is compiled out of the Windows player (`#if UNITY_EDITOR`). The portable application has no joint-simulation toggle at all, because it ships no Python interpreter and no Unreal host.

### Bridge tests

Two standalone scripts check the parts that do not need Unreal:

```text
cd JointSimulation
python airsim_bridge/test_coords.py      # ENU/NED round-trips, quaternion conversion, locker table
python airsim_bridge/test_pipeline.py    # frame parsing, smoother convergence, jitter detection
```

`test_pipeline.py` needs neither Unity, AirSim, nor a network socket. A full run currently prints two `PASS` verdicts and one `WARN`: the smoother turns ±5 mm of vehicle noise into about 17 mm of camera-position spread against a 15 mm budget. That warning is a known property of the follow-camera smoothing constants and does not affect simulation results. `JointSimulation/README.md` in the source repository is the bridge-level reference; that folder is not part of the Windows archives.

## Packaged workbench workflow

The packaged UI contains four modules:

- `Airspace Structure` — scenario, network layers, facilities, holding structures, and corridors.
- `Routes & Traffic Flow` — route design, mission/flow authoring, queue editing, and import/export.
- `Simulation` — demand source, all operator-facing parameters, batch grids, playback, trails, collision pause, and aircraft following.
- `Data Visualization` — current and historical trajectories/heatmaps, archived metrics, multi-run comparisons, parameter sensitivity, and output access.

During a single run or parameter sweep, the workbench remains on `Simulation` and locks the other modules, scene tabs, scene creation, and scene-data replacement. This run-time interlock prevents authoring, archived-analysis, and scene state from replacing simulation-owned data. `Runtime Lane Coloring` offers three mutually exclusive choices: lane-type colors, live aircraft load (gray when empty, then green-to-red by real-aircraft count), and passability (green open/red blocked). Signal and reservation sentinels do not count as aircraft, and signal markers remain independent. The archived Data Visualization heatmap is a separate state and is cleared when leaving that module, so it cannot affect a later simulation.

In the packaged application, use the aligned `Hide UI` control or `F10` for an unobstructed simulation view. A compact restore control remains beside `Settings` in the upper-right title row. This is a temporary display toggle: it does not stop the simulation or delete the current workspace.

### Simulation History and comparison

Every run receives a versioned manifest under the active scenario rather than in a global history list. The `Data Visualization` page can select one concrete run for historical 3D heatmap/trajectory playback or `MetricsTable` review, and can select a batch parent for its original summary table or statistical analysis. Expanding a batch exposes each member and its factor values. A batch parent has no unique 3D state and cannot be loaded into the 3D view; expand it and select exactly one concrete member instead. Loading another single run or member commits only after all of its artifacts are ready, atomically replacing the previous heatmaps, trajectories, and trajectory list so results from two runs cannot be mixed. The archive is streamed in the background and retains compact lane-entry totals and downsampled trajectories from completed missions only. Aircraft still in flight when a timed run ends are excluded. Archived topology is visual-only and never replaces the currently edited scene.

For a concrete run, `Traffic-Heatmap Altitude Layers` independently controls the archived 3D overlay and the Python 2D traffic figure. The Python renderer produces an ordered page set: one full-size square network overview for each visible authored altitude layer, followed by one full-size local overview for every archived vertiport. Each vertiport page uses padded square bounds around its approach edges, holding ramps, and reservable facility-to-network corridors. Nodes, helipad outlines, facility boundaries, and lane-to-lane connector curves are deliberately omitted. Facility corridors do participate in reservation windows, so they remain in the plot and are reconstructed as the same altitude-aware cubic Bezier curves used by the 3D scene. Use `Previous Figure` and `Next Figure` to review the pages; zoom, pan, and export apply to the current page. Road pages use lane traversal counts and vertiport pages use ground-path reservation time. Selecting another concrete run automatically replaces the old heatmap, trajectories, and completed-trajectory selector after the new archive is ready.

The available chart modes are derived from completed runs with valid archived artifacts. Stopped, interrupted, cancelled, or failed runs remain available for diagnostic 3D/table review but are excluded from comparison figures. Output metrics (Y) must be finite and shared, and the selector groups them into collapsible categories. A bar comparison needs no input parameter and places each selected experiment record on its X axis; multiple Y metrics produce independently scaled, labelled panels. For parameter response, input parameters (X) must be numeric and vary: one selected X produces response curves, two produce large square annotated heatmaps, and three place square, semi-transparent annotated heatmap planes at the observed levels of the third X parameter in a true three-dimensional stack. The planes preserve the measured factorial combinations without implying interpolation between untested values. Multiple Y metrics produce separate, consistently arranged panel groups. Python figures are rendered at high resolution, and both chart and table viewers provide zoom, pan, reset, and export controls. A batch summary is presented one experiment per page in two columns, with previous/next navigation and direct page-number jumps. Exact repeated combinations use real means, sample standard deviations, and sample counts only. Unselected varying parameters remain explicit contexts rather than being silently averaged, and the figures are described as empirical tested-grid responses rather than formal Sobol/Morris indices.

## Basic scene controls

| Input | Action |
|---|---|
| Mouse wheel | Zoom |
| Middle-button drag | Pan |
| Right-button drag | Orbit and enter free perspective |
| `F` | Set the view center to the cursor's horizontal-plane position |
| Orientation-cube center | Toggle perspective/orthographic projection |
| Orientation-cube cones | Select one of six orthographic directions |
| `Tab` / `Shift+Tab` | Cycle followed aircraft forward/backward |
| `F10` | Hide or restore the runtime workbench |

## Portable data layout

A Windows player stores application-owned files beside the executable:

```text
Structured-Air-Route-Simulator.exe
SARScenes/
  scenes.json
  default/
    AxisNetwork.json
    VertiportData.json
    CityModel.sarcity
    SimulationHistory/
      run-<UTC timestamp>-<id>/
        manifest.json
        MetricsTable.csv
        SimulationRecording.json.gz
        AxisNetworkSnapshot.json
        FullNetworkSnapshot.json
        VertiportsSnapshot.json
        OccupancyLog.json
      batch-<UTC timestamp>-<id>/
        manifest.json
        BatchExperimentRunSummaryTable.csv
Network/
SARData/
Output/
  MetricsTable.csv
  BatchExperimentRunSummaryTable.csv
  OccupancyLog.json
  SimulationRecording.json
  RouteWorkspace.json
  Trajectories/
ImportCache/
Settings/
  display.json
```

Each `manifest.json` stores portable relative file names, scene identity, run status and duration, parameters, metrics, and any batch/member relationship. Recordings are archived as gzip-compressed JSON and decompressed transparently when loaded. The ordinary `Output/` files remain the latest-run export surface; the copies in `SARScenes/<scene-id>/SimulationHistory/` are the persistent record associated with that scene tab.

Existing data from older LocalLow-based releases is copied once without deleting the legacy source; existing portable files take precedence. In the Unity Editor, runtime scenario data continues to use `persistentDataPath` so tests and development do not modify a distributed Windows package.

Portable path prefixes:

| Prefix | Resolves to |
|---|---|
| `scene://` | Active scenario directory |
| `app://` | Project/application root |
| `user://` | Runtime data root; equal to the application root in the Windows player |
| `streaming://` | Unity `StreamingAssets` directory |

## Input formats

### Scenario files

| File | Contents |
|---|---|
| `AxisNetwork.json` | Multilayer axis nodes, edges, waypoints, and layer metadata |
| `VertiportData.json` | Vertiports, helipads, approaches, holding structures, and facility corridors |
| `CityModel.sarcity` | Portable baked city mesh and base material colors |
| `FullNetwork.json` | Derived lane-level network generated from axis geometry and building clearance |

### City models

Runtime import supports:

- `.sarcity` — recommended portable city package;
- `.obj` — triangular meshes; and
- `.dxf` — 3D faces/solids or closed `LWPOLYLINE` entities with non-zero thickness.

Purely two-dimensional DXF outlines do not produce building volumes. In the Unity Editor, `Tools > SAR > CADMapper SketchUp Converter` can convert supported SketchUp imports to `.sarcity`. The repository does not include a licensed SketchUp C SDK runtime converter, so `.sarcity` or `.obj` is recommended for distribution.

### Route demand

JSON and XML files may contain individual trips, named routes, and periodic `flow` records. A mission may provide only endpoints for automatic routing or an explicit directed lane sequence. Invalid records are reported individually without discarding other valid records.

Examples:

- `Assets/StreamingAssets/SARData/RouteFile.example.json`
- `Assets/StreamingAssets/SARData/RouteFile.example.xml`

See [Route File Format](OperationGuide/RouteFileFormat.md) and [Traffic Control](OperationGuide/TrafficControl.md).

## Repository structure

```text
Assets/
|- DataBase/                 Default topology inputs and SimulationConfig
|- DataStructrue/            Static/dynamic data structures (legacy directory spelling)
|- Editor/                   Inspectors, SAR Workbench, export/conversion, and build tools
|- Resources/                Main scene, prefabs, materials, and built-in city assets
|- Scripts/
|  |- Network/               Geometry, connectivity, lane change, multilayer, and signal utilities
|  |- Reservation/           Reservation windows and helipad occupancy
|  |- Routes/                Shared authoring, JSON/XML import/export, and explicit-route resolution
|  |- RuntimeUI/             Desktop workbench, scenario tools, visualization, and portable paths
|  |- Traffic/               Following, entry safety, and facility-node separation
|  |- AirSimJointBridge.cs   Unity side of the joint simulation (UDP pose stream)
|  |- AirSimKinematicPublisher.cs   Minimal ENU publisher for the standalone kinematic slave
|  |- SimulationManager.cs   Simulation lifecycle, demand, collisions, and metrics
|  |- PathPlanner.cs         Facility reservation and lane-level route search
|  `- AircraftAgent.cs       Aircraft mission state machine
|- StreamingAssets/
|  |- SARAnalytics/          Python plotting worker sources, build inputs, and the packaged worker
|  |- SARData/               Packaged route examples
|  `- SARLocalization/       Runtime UI catalog (zh-CN)
`- Tests/Editor/             EditMode automated tests

JointSimulation/
|- airsim_bridge/            Python bridge: UDP receiver, AirSim pose writer, camera driver
|- README.md                 Bridge-level reference: protocol, coordinates, hotkeys, diagnostics
`- settings.example.json     AirSim settings template (Manual view mode, shared origin)

UnrealHost/SZ_TestArea/      Unreal Engine 4.27 host project for the joint simulation
                             (needs the AirSim 1.8.1 plugin, which is not redistributed)

OperationGuide/              Quick Start, formats, traffic rules, and build guide
Packages/                    Reproducible Unity package dependencies
ProjectSettings/             Unity 2022.3 project configuration
Tools/SARPlotWorker/         Plotting-worker build script, license collector, and tests
```

Generated builds, experiment outputs, plotting/visualization scripts stored with those outputs, imported city caches, and local QA captures are intentionally excluded from Git. Publish the Windows portable ZIP as a GitHub Release asset instead of committing build products to the source branch.

### Cloning on Windows

The collected third-party licence texts under `Assets/StreamingAssets/SARAnalytics/Worker/THIRD_PARTY_LICENSES/` include 21 paths longer than the legacy 260-character limit. Enable long paths before cloning, or those licence files will be skipped:

```text
git config --global core.longpaths true
```

Also enable `LongPathsEnabled` in the Windows registry, or clone into a short root such as `C:\src\`. The licence texts matter: the build validator checks them, and `THIRD_PARTY_NOTICES.md` points at them for redistribution compliance.

Two large authoring assets are excluded from Git because they would dominate the history. Both are published separately; see [EXTERNAL_ASSETS.md](EXTERNAL_ASSETS.md):

- `Assets/Resources/Scenes/ShenzhenSKPWhitebox.unity` — a 628 MB authoring scene built by importing the Shenzhen SketchUp/OBJ city model into Unity and adding colliders. The `ITSAC >` menu items that consume it (`Add Whitebox Colliders`, `Split Whitebox Into Buildings`, `Build Shenzhen Demo Airspace`, `Export Whitebox To UE`) require it locally, and the mesh exporters write to the directory named by `SAR_UE_IMPORT_DIR`.
- `Assets/ImportedCityModels/` — the imported city mesh cache (`ITSAC > Import Hull OSM Whitebox` writes `OSM_Hull` here, `Split Whitebox Into Buildings` writes `ShenzhenSplit`).

Neither is needed to open, run, or build the shipped application: the distributed scene is `Assets/Resources/Scenes/1.unity` (the Xinjiekou study scene), which is self-contained and is the only enabled build scene.

The Shenzhen scene is nevertheless required to reproduce the joint simulation, because that is the city the Unreal host was built from. `EXTERNAL_ASSETS.md` records where to download it and how it is laid out.

## Build and verification

Exit Play Mode, then run:

```text
Tools > SAR > Validate Build
Tools > SAR > Build Windows x64
```

The SAR build command recreates only the controlled `Builds/Windows` staging
directory, embeds the current route examples and runtime localization catalog,
validates the plotting worker and third-party licenses, and then publishes clean
English and Chinese copies under `Builds/Releases`. The release validator rejects
leaked runtime data and confirms that both packages use identical player binaries.

If packaged route examples changed, first run:

```text
Tools > SAR > Sync Packaged Route Data
```

Automated tests are under `Assets/Tests/Editor`. Run the full EditMode suite from Unity Test Runner before publishing. The current suite covers route files, explicit routes, multilayer connectivity, geometry, reservation behavior, random-demand lifecycle, runtime editing, scenario storage, analytics, parameter validation, batch-grid startup, and runtime-UI layout.

### Rebuilding the plotting worker

`Assets/StreamingAssets/SARAnalytics/Worker/SarSensitivityRenderer-win-x64-v1.zip` is a PyInstaller build of the two scripts beside it. Build validation checks its SHA-256 against `Worker/manifest.json`, so a change to either script requires a rebuild:

```text
Tools/SARPlotWorker/build_worker.ps1 -PythonExecutable <path-to-python.exe>
```

The script freezes `SensitivityAnalysis.py` and `TrafficHeatmapAnalysis.py`, collects the third-party licenses into `THIRD_PARTY_LICENSES/`, writes the archive, and updates the manifest hashes. The worker's own tests live in `Tools/SARPlotWorker/tests/`. `build_worker.ps1` regenerates `SarSensitivityRenderer.spec`; that file is a build product and is not tracked.

## Documentation

- [Video walkthrough of the English portable build](https://youtu.be/wICTW1oSdc0) (YouTube)
- [English Quick Start: Unity Editor and Windows Workflows](OperationGuide/QuickStart/README.md)
- [Workbench and Windows Build Guide](OperationGuide/WorkbenchAndBuild.md)
- [Route File Format](OperationGuide/RouteFileFormat.md)
- [Traffic Control, Lane Change, and Deadlock Rules](OperationGuide/TrafficControl.md)
- [External assets and the Shenzhen scene](EXTERNAL_ASSETS.md)
- [简体中文说明](README.zh-CN.md)

## Releases

The portable Windows application is distributed as two ZIP archives, not as source:

| Archive | Interface language | Selector |
|---|---|---|
| `SAR-Sim-Windows-x64-en-US-<version>.zip` | English | `SAR.locale.json` → `en-US` |
| `SAR-Sim-Windows-x64-zh-CN-<version>.zip` | 简体中文 | `SAR.locale.json` → `zh-CN` |
| `SAR-Sim-UnrealHost-SZ_TestArea-<version>.zip` | — | Optional Unreal host project for the joint simulation; no AirSim plugin inside |

Both Windows archives contain the same player binaries; only the locale selector and the bundled documentation differ. Unity source, scene object names, and Inspector labels stay English in both. Each archive already contains the Quick Start and the three reference guides under `OperationGuide/`, plus `README.md` and `README.zh-CN.md`.

Extract an archive to a writable folder and run `Structured-Air-Route-Simulator.exe` from there. Do not separate the executable from its `_Data` folder, `MonoBleedingEdge/`, or `UnityPlayer.dll`. The application also reads and writes `SARScenes/`, `Network/`, `Output/`, `SARData/`, `Settings/`, and `ImportCache/` beside the executable; those folders are created on first launch if they are absent, so the extracted directory must be writable.

Every archive is a clean build and ships no runtime state at all: no `SARScenes/`, `Output/`, `Network/`, `Settings/` or `ImportCache/`. On first launch the application seeds exactly one scenario, `SARScenes/default`, from the compiled scene — the Xinjiekou study area — and creates the other folders empty. So a fresh extraction always starts with one scenario and zero simulation history, and any result that appears under `Output/` afterwards is your own run.

The joint simulation is not part of the Windows archives. It needs the Unity Editor project, Unreal Engine, AirSim, and a Python interpreter, so it is documented in the source section above instead. The Unreal host project is released separately as the third archive above.

### Release checklist

Steps for publishing a release from this repository:

1. Confirm the source tree is clean and `Tools > SAR > Validate Build` passes in the Unity Editor.
2. Run `Tools > SAR > Build Windows x64`. It writes `Builds/Releases/en-US/` and `Builds/Releases/zh-CN/`, each with a `SAR.locale.json` selector. The release validator refuses to publish if `SARScenes/`, `Output/`, `Network/`, `Settings/` or `ImportCache/` leaked in.
3. Launch each release once from a scratch folder and confirm the seeded state: `SARScenes/scenes.json` lists one scene (`default`), `SARScenes/default/` holds `AxisNetwork.json`, `VertiportData.json` and `CityModel.sarcity`, there is no `SimulationHistory/`, and `Output/` is empty. Delete that scratch folder afterwards; do not ship the seeded copy.
4. Zip the two folders as `SAR-Sim-Windows-x64-en-US-<version>.zip` and `SAR-Sim-Windows-x64-zh-CN-<version>.zip`.
5. Zip `UnrealHost/SZ_TestArea/` as `SAR-Sim-UnrealHost-SZ_TestArea-<version>.zip`. Do not add the AirSim plugin: users install AirSim 1.8.1 themselves.
6. Confirm the Hugging Face dataset linked in [EXTERNAL_ASSETS.md](EXTERNAL_ASSETS.md) still resolves and carries the Shenzhen scene and the imported city cache. It is already published; re-upload only if either asset changed.
7. Create the GitHub release with the tag `v<version>`, attach the three archives, and paste the release notes below.

Suggested release notes body:

```text
SAR-Sim <version>

Windows portable application (no Unity required):
  SAR-Sim-Windows-x64-en-US-<version>.zip   English interface
  SAR-Sim-Windows-x64-zh-CN-<version>.zip   简体中文界面

Both archives contain the same player binaries and differ only in SAR.locale.json
and the bundled documentation. Each ships one scenario, the Xinjiekou study area
(two altitude layers, 102 nodes, 160 edges, 7 vertiports, 978 buildings), and no
simulation history or previous results.

Optional Unreal host for the experimental joint simulation:
  SAR-Sim-UnrealHost-SZ_TestArea-<version>.zip
Requires Unreal Engine 4.27 and the AirSim 1.8.1 plugin. The Shenzhen whitebox
scene that this map was built from is on Hugging Face — see EXTERNAL_ASSETS.md.

Source: the git tag for this release. The joint simulation is a pilot; pose
read-back and closed-loop coordination are future work.
```

## Research context

This repository accompanies:

> **A Benchmark-Oriented Framework for Design, Simulation, and Evaluation of Urban Low-Altitude Structured Air Route Systems**
>
> Zichun Zhao, Yuejie Lin, Tao Ma

Author affiliations: Zichun Zhao is with the School of Transportation, Southeast University, Nanjing 211189, China, and also with Zhongguancun Academy, Beijing 100094, China; Yuejie Lin is with the School of Mechanical Engineering, Shanghai Jiao Tong University, Shanghai 200240, China; Tao Ma is with the School of Transportation, Southeast University, Nanjing 211189, China.

The system separates component authoring, operational mechanisms, and experiment configuration so alternative network, facility, routing, or control methods can be evaluated through consistent data and recording interfaces.

### Notation: paper model vs. implementation

Section III-A of the paper specifies the aircraft operational instance as a tuple with a mission phase, an execution mode, and a lifecycle status. The code base carries the same design, but records what an agent is doing at the granularity the mechanisms need, so neither the identifiers nor the boundaries between values match one to one.

`AircraftState` is defined in `Assets/DataStructrue/DynamicDataStructure.cs`. `MissionPhase` and `State` are plain `string` fields, not enumerations, so the value sets below are conventions enforced by the assignments in `AircraftAgent.HandlePhaseTransition` and `SimulationManager` rather than by the type system.

| Paper | Implementation |
|---|---|
| `S_ph`, mission phase | `AircraftState.MissionPhase` |
| `S_md`, execution mode | no single field; see below |
| `S_st`, lifecycle status | `AircraftState.State` |

**Mission phase.** The entry and exit corridors are handled inside the adjacent motion phase rather than as phases of their own, so six paper values become four code values plus two bookkeeping values:

| Paper `S_ph` | `MissionPhase` | Comment |
|---|---|---|
| `Idle` | `START` | Parked at the origin helipad, before the first ramp point is issued. |
| `Departure` | `TAKEOFF` | Ramp trajectory through the approach network and holding structure. |
| `Entry` | — | Not a separate value: the entry corridor is part of the `TAKEOFF` ramp trajectory, gated by its `EntryMerge` checkpoint. |
| `Cruise` | `CRUISE` | Lane-sequence traversal. |
| `Exit` | — | Not a separate value: `ExitCorridorId` is claimed as the lane sequence ends and is the first leg of the `LANDING` trajectory. |
| `Arrival` | `LANDING` | Landing trajectory to the destination helipad. |
| — | `WAITING` | No paper counterpart: the aircraft is held at a helipad while a destination or a chained mission is retried. |
| — | `END` | No paper counterpart: terminal bookkeeping, always paired with `SUCCESS` or `FAIL`. |

**Execution mode.** `Proceed` is the absence of any hold condition rather than a stored value. `Hold` is spread over the wait and blocked-access flags: `FlightController.HasPendingHold` for a scheduled release, plus `AircraftAgent.isWaitingForEntry`, `isWaitingForLaneChange`, `isHoldingForLandingPlan`, and `isWaitingForVertiportResource`. `Replan` is realised by the retry paths — lane-change rerouting in `SimulationManager.TryRerouteCruise` gated by `laneChangeRerouteTimeout`, deferred landing planning up to `maxCorridorWaitTime`, and helipad requeue up to `maxHelipadWaitTime`.

**Lifecycle status.** `State` maps directly: `ACTIVE`, `SUCCESS`, and `FAIL` stand for the paper's `Active`, `Success`, and `Failure`. The two terminal values release the aircraft's resource registrations.

Nothing reported in the paper depends on these identifiers or on where a value boundary falls: the tuple describes the specification, and the fields record the same information at the granularity the mechanisms consume.

## License

Released under the [MIT License](LICENSE).
