# SAR Workbench and Windows Build Guide

## 1. Two equivalent simulation workflows

SAR-Sim supports both a source-level Unity workflow and a portable Windows workflow. They share the same topology data, route-demand service, `SimulationConfig`, simulation manager, parameter catalog, batch runner, and result formats.

| Capability | Unity Editor | Packaged Windows workbench |
|---|---|---|
| Edit air-route layers, facilities, holding structures, and corridors | Manager Inspectors plus Scene view | Airspace Structure module |
| Generate automatic or manual routes | `SimulationManager` Inspector plus Scene view | Routes & Traffic Flow module |
| Add single missions and periodic flows | `SimulationManager` Inspector | Routes & Traffic Flow module |
| Import/export JSON or XML demand | Yes | Yes |
| Edit aircraft, reservation, signal, and uncertainty parameters | `SimulationConfig` inspector | Simulation module |
| Define and run parameter sweeps | `ParameterSweepRunner` inspector | Simulation module |
| Export batch experiment table | CSV | CSV |
| Run, pause, stop, change time scale, and follow aircraft | Play Mode | Simulation module |
| Inspect current or historical trajectories, heatmaps, metrics, comparisons, and sensitivity | Runtime workbench in Play Mode | Data Visualization module |
| Mirror traffic into Unreal Engine / AirSim (experimental pilot) | `SimulationManager` + `AirSimJointBridge`, Editor only | Not applicable |
| Validate and build Windows x64 | Yes | Not applicable |

The interfaces are intentionally different where the host environment differs. Unity retains Scene view manipulation, inspectors, source debugging, tests, and build tools. The Windows application replaces those development-only surfaces with guided runtime controls. The simulation capabilities and generated data are otherwise aligned.

For an annotated, step-by-step introduction to both workflows, see the [English Quick Start](QuickStart/README.md).

## 2. Unity Editor workflow

Open `Assets/Resources/Scenes/1.unity`. The primary authoring workflow is:

```text
Hierarchy object -> Manager Inspector -> Scene view
```

Select `NetworkManager` for axis/lane work, `VertiportManager` for facility work, and `SimulationManager` for route demand and simulation controls. This keeps the workflow entirely inside the standard Unity Inspector and Scene view.

`Tools > SAR Workbench` remains an optional overview/shortcut window with five tabs:

1. `Overview` — topology, facility, and demand counts.
2. `Simulation` — route-file import, queue controls, Play Mode controls, and links to the complete configuration and batch inspector.
3. `Network` — shortcut to the network Inspector, lane generation, validation, and `FullNetwork.json` export.
4. `Facilities` — shortcut to the facility Inspector and `VertiportData.json` export.
5. `Build` — packaged-data checks and Windows build.

Recommended sequence:

```text
Edit -> generate lanes -> validate -> export topology
     -> author/import demand -> configure -> simulate
     -> inspect results -> validate build -> build
```

### 2.1 Routes and traffic flow in Unity

Select the `SimulationManager` object and expand `Routes & Traffic Flow (Inspector + Scene view)`:

1. Click `Refresh route-authoring topology` after topology changes.
2. Select origin and destination helipads.
3. Choose `Automatic shortest` or `Manual: click centerlines`.
4. In manual mode, click the desired axis centerlines directly in the Scene view. The departure edge is inserted and locked automatically. Every additional click is checked for lane-level connectivity.
5. Selected centerlines are highlighted red/orange and numbered in traversal order. Hovering a candidate uses a brighter orange highlight. Clicking an already selected centerline rolls the route back to that point; `Esc` pauses selection.
6. Once the highlighted route reaches the destination, click `Confirm manual route`.
7. Add a single mission or periodic flow, or select `Edit` beside a queued mission to update it.
8. Import or export the queue as JSON/XML from the same Inspector.

Automatic and manual generation call the same `RouteDemandAuthoring` service as the Windows workbench. Manual axis edges are resolved to a directed lane sequence, including required adjacent lane changes and interlayer corridors.

### 2.2 Parameters and batch experiments in Unity

Select the `SimulationConfig` asset to edit all operator-facing numeric fields. Select the scene's `ParameterSweepRunner` to define any number of enabled sweep variables, their start values, steps, counts, batch duration, and CSV destination.

Starting a sweep outside Play Mode automatically enters Play Mode and starts the requested experiment after the domain reload. The runner resolves the Cartesian grid itself, validates every range against the shared parameter catalog, restores original values when it stops, and writes:

```text
Output/BatchExperimentRunSummaryTable.csv
```

### 2.3 Joint simulation with Unreal Engine and AirSim

Unity is the motion master. `SimulationManager` starts a Python bridge when the run starts, streams every aircraft pose to it over UDP, and the bridge teleports the matching AirSim vehicle. AirSim contributes geometry and rendering only: it does not integrate flight dynamics and its collision response is not part of the simulation result.

This module is an experimental pilot. The one-way Unity-to-AirSim path works — poses, vehicle creation, camera. Anything that would need AirSim to report state back (pose read-back, closed-loop coordination, sensor or collision feedback) is not implemented; the read-back columns of `bridge_diag.csv` are placeholders written as zero. See the status table in the top-level `README.md`.

The host project is `UnrealHost/SZ_TestArea/` in this repository; it needs the AirSim 1.8.1 plugin placed at `Plugins/AirSim`, which is not redistributed. The Shenzhen whitebox scene behind the map is published separately — see `EXTERNAL_ASSETS.md`.

```text
SimulationManager / AirSimJointBridge      airsim_bridge.joint_bridge      Unreal Engine 4.27 + AirSim 1.8.1
  ITS1 frame, 30 Hz, UDP 127.0.0.1:49001  ->  simSetVehiclePose           ->  one pawn per Unity aircraft
                                             simSetObjectPose             ->  ExternalCamera
```

Requirements are Unreal Engine 4.27 with the AirSim plugin compiled in, the AirSim Python client (`pip install airsim==1.8.1`), and Python 3.10 or newer. Nothing is sent to AirSim until the simulation actually starts, so Play Mode alone does not launch the bridge.

Configuration is deliberately outside the project, because both the interpreter and the Unreal installation are machine-specific:

| Setting | Where | Default |
|---|---|---|
| `pythonPathOverride` | `AirSimJointBridge` component | `SAR_BRIDGE_PYTHON`, else `python.exe` from `PATH` |
| `workingDirectoryOverride` | `AirSimJointBridge` component | `SAR_BRIDGE_WORKDIR`, else the current directory |
| `UE4EDITOR_EXE` | environment | standard UE 4.27 install path |
| `UE4_PROJECT` | environment | `SZ_TestArea.uproject` |
| `SAR_FOLLOW_CAMERA` | environment | `Documents/AirSim/follow_camera.json` |
| `SAR_UE_IMPORT_DIR` | environment | `<project>/ue_import`, used by `ITSAC/Export *` |

The AirSim host settings come from `JointSimulation/settings.example.json`; these fields are load-bearing:

- `"ViewMode": "Manual"` — in any other mode `ACameraDirector::Tick` interpolates the camera toward the aircraft every frame and never converges, which appears as on-screen shake. `Manual` mode only applies keyboard deltas, so the bridge's camera pose survives.
- `OriginGeopoint` — must match the origin compiled into `airsim_bridge/coords.py` and into the Unity scene, or the Unreal and Unity worlds are offset by a constant.
- The vehicle spawn coordinates — `simSetVehiclePose` is anchored at the spawn point and the bridge applies no offset correction, so any non-zero spawn offsets that vehicle by the same amount. The example file spawns `ChaseCam` at `Z = -2` (two metres up), which is the intended FPV seat height rather than an error; if you retune it, remember the FPV view moves with it.

Frame conventions:

| Space | Axes | Units |
|---|---|---|
| Unity world | x = east, y = up, z = north; yaw 0 faces north, positive yaw turns east | metres |
| AirSim / AirLib NED | x = north, y = east, z = down; yaw 0 faces north, positive yaw turns east | metres |

Vehicles are teleported with `simSetVehiclePose` and the camera with `simSetObjectPose`; the conversion between the two spaces lives in `airsim_bridge/coords.py` and is covered by `test_coords.py`.

Diagnostics and troubleshooting:

- The bridge console prints the connected AirSim instance, the frame count, the current mode, and the followed aircraft every two seconds.
- `bridge_diag.csv`, written beside the bridge source, samples every fifth frame: the raw Unity pose, the converted NED pose, and the desired and smoothed camera poses. The last three columns are reserved for the set/get delta and are currently zero. `test_pipeline.py` exercises the same thresholds.
- If the camera does not move, the console reports whether `ExternalCamera` exists — a warning there means the Unreal map is missing the actor that Manual view mode renders through.
- If the vehicles do not move, check for a hotkey or API warning: AirSim API control must stay disabled, because `SimpleFlight` enters hover mode when offboard goals stop and then applies forces that fight the teleports.
- If the Console says the interpreter or the bridge folder was not found, the two messages are separate and name the resolved path; check which one is wrong before editing anything else.

`python -m airsim_bridge.joint_bridge` runs the bridge by hand, with `--launch-airsim` to start Unreal itself and `--bind`/`--port` to move the UDP endpoint. `python airsim_bridge/test_coords.py` and `python airsim_bridge/test_pipeline.py` check the conversion and pipeline math without Unity, AirSim, or a socket.

## 3. Using the runtime UI inside Unity

The runtime workbench is disabled by default in Unity Play Mode so the standard Inspector and Scene view remain the complete Editor workflow.

Select `SimulationManager` and find `Editor Play Mode Runtime UI` under `Play Control`:

- `Enable Runtime UI in Editor Play Mode` creates the same generated Canvas, EventSystem, navigation, editing, routing, analytics, and file-drop components used by the Windows application.
- `Disable Runtime UI in Editor Play Mode` destroys that complete generated object set. It can be used before entering Play Mode or while the simulation is running.
- The choice is persistent across Play Mode sessions and affects only the Unity Editor. A Windows build always starts with its packaged workbench enabled.

When the runtime workbench is enabled, `Hide UI` or `F10` performs a temporary visual hide without stopping or resetting the simulation. The compact restore button remains aligned beside `Settings` in the upper-right title row.

## 4. Packaged Windows workbench

The Windows application exposes four modules:

- `Airspace Structure` — scenario data, network layers, vertiports, holding structures, and both corridor types.
- `Routes & Traffic Flow` — endpoints, automatic/manual routing, single missions, flows, queue editing, and route-file import/export.
- `Simulation` — demand source, aircraft/reservation/signal/uncertainty parameters, batch experiment grid, playback, collisions, trails, and following.
- `Data Visualization` — current and historical trajectories/heatmaps, archived metrics, multi-run comparisons, parameter sensitivity, and output access.

Parameter changes are applied only while no single or batch simulation is running. The lane-change removal timeout must remain greater than the reroute timeout. Batch variables may be added or removed dynamically; invalid or out-of-range grids are rejected before the first run.

The four modules share one 3D viewport, but they do not share an unrestricted mutation window. While a single run or parameter sweep is active, the workbench remains on `Simulation` and disables Airspace Structure, Routes & Traffic Flow, Data Visualization, scene-tab switching, scene creation, and scene-data replacement. Stop the run to release this interlock. This prevents authoring data, archived analysis state, or another scene from replacing objects used by the running simulation.

`Runtime Lane Coloring` on `Simulation` provides exactly one active mode at a time: `Lane-type colors` keeps the authored connection/type palette; `Live aircraft load` makes empty lanes gray and maps increasing real-aircraft occupancy from green to red; and `Lane passability` renders open lanes green and blocked lanes red. Synthetic signal/reservation sentinel occupancy is excluded from the live-aircraft count, so traffic-signal state cannot contaminate the load heatmap. Intersection-signal markers remain a separate visual layer. The archived heatmap on `Data Visualization` is also independent and is cleared when leaving that module, so it cannot leak into a later simulation.

### 4.1 Collision-event accounting

One collision event is one continuous overlap episode of an unordered aircraft pair. Repeated overlap samples do not add events; separation, normal run completion, manual stop, and interrupted shutdown all finalize the same event through one accounting path. Active pairs and their start metadata are reset together before every batch member, so an overlap cannot leak into the next run.

Every aircraft-aircraft event is assigned to exactly one of `Lane Collisions`, `Approach Collisions`, `Ramp Collisions`, `Corridor Collisions`, `Helipad Collisions`, or `Unclassified / Mixed-Resource Collisions`. The last category preserves events whose start metadata is unavailable or whose two aircraft resolve to conflicting resource types. The exported identity is:

```text
Total Collisions = Lane + Approach + Ramp + Corridor + Helipad + Unclassified / Mixed-Resource
```

`Collision Classification Residual` must therefore be zero. Aircraft-building overlap messages remain frame-level diagnostics and are deliberately excluded from this aircraft-aircraft event total until they have equivalent episode tracking.

### 4.2 Network-layer visibility and freezing

Each altitude-layer row in the Airspace Structure workspace ends with an eye control and a snowflake control:

- The eye controls only that layer. Effective visibility is `Air Network global visibility AND page-local layer eye visibility`, so turning off either switch removes the layer from that module's 3D view and network hit testing without changing the authored JSON metadata.
- The snowflake locks the layer as an editing boundary. Nodes, edges, and waypoints on a frozen layer cannot be added, moved, connected, or deleted. The layer's name, altitude, and other layer properties cannot be changed, and the layer itself cannot be deleted or reordered.
- A different layer cannot be dragged across a frozen layer. This preserves both the frozen layer's position and the ordering boundary around it.

Visibility and freezing are independent: a layer may be visible and frozen for reference, or hidden and unlocked while preparing a later edit. `Routes & Flow`, `Simulation`, and `Data Visualization` provide their own altitude-layer visibility lists. Switching modules replaces the complete temporary view mask, so hiding a layer while editing cannot remove it from a later run or archived result; returning to a module restores only that module's session-local choice. Layer names are always the exact authored `DisplayName`, even in the Chinese release. Old network JSON files remain compatible; a missing freeze field loads as unlocked.

### 4.3 Scene-scoped Simulation History

Completed, stopped, interrupted, and batch-member runs are recorded under the currently active scenario tab. Switching tabs therefore switches both the topology and the history list; results from different scene folders are never silently mixed.

Use the Simulation History area on `Data Visualization` as follows:

1. Select one single run, or expand a batch and select one concrete member, then choose `Load 3D Results` to load its lane-traversal heatmap, ground-resource reservation heatmap, completed-mission trajectories, and archived topology without changing the editable scenario. Trajectories that were still active when a timed experiment ended are excluded. Loading a different concrete run stages all of its artifacts and then atomically replaces the old heatmap, trajectories, and completed-trajectory selector; no manual clearing or refresh is required. Choose `Review Result Table` for that run's archived `MetricsTable.csv`; select the batch parent to display its original `BatchExperimentRunSummaryTable.csv` one experiment per page in a two-column view. Use `Previous`, `Next`, or a page-number jump to navigate the batch.
2. Select several completed leaves, or select a batch parent to expand its completed members for statistical analysis. A Python bar comparison accepts one or more shared output metrics (Y), places the selected experiment records on its X axis, and does not use the sensitivity input-parameter selection. Each metric is rendered in its own labelled, independently scaled panel.
3. For empirical parameter-response analysis, select one to three varying numeric input parameters (X) and one or more shared output metrics (Y). One X produces response curves with fixed-context series kept in a separate legend band; two X produce large square, annotated heatmaps; three X preserve the first two parameters as square heatmap axes and stack semi-transparent annotated planes at the observed levels of the third parameter in true three-dimensional depth. Multiple Y metrics become separate panel groups.
4. To remove an obsolete record, select exactly one standalone run or one batch parent and choose `Move Selected Record to Recycle Bin`. A batch parent and all of its member folders move together; a member cannot be recycled independently. The operation is scene-scoped and recoverable from `SimulationHistory/.Trash/`. Move the saved run folders back under `SimulationHistory/`, then use `Refresh After External Restore`, if manual recovery is required.

The interface derives valid analysis choices from completed manifests with valid required artifacts. Stopped, interrupted, cancelled, or failed runs remain selectable for diagnostic 3D/table review but are excluded from comparison plots. Metrics that are absent or non-finite in any comparable run are not offered, and the output-metric (Y) selector groups the remaining choices into collapsible categories. A parameter is not offered as a sensitivity factor unless it is numeric and actually varies. If selected runs have unequal durations, cumulative metrics such as totals, counts, collisions, missions, or throughput are normalized by each run's duration and labelled per second. Average, ratio, utilization, occupancy, duration, and already rate-like metrics are not duration-normalized.

A batch parent has no unique time axis, trajectory set, or 3D traffic state, so it cannot be loaded into the 3D view and the application never deserializes or overlays every member recording. Expand the batch and select exactly one concrete member for 3D review; batch-wide work remains available through its table and statistical plots. When another single run or member is loaded, the application stages the complete result first and then atomically replaces the previous heatmaps, trajectories, and trajectory list. A failed or cancelled load therefore cannot leave artifacts from different runs mixed together.

For a selected concrete run, `Traffic-Heatmap Altitude Layers` controls which authored altitude layers appear in both the archived 3D overlay and `Generate / Refresh 2D Traffic Heatmap`. The bundled Python/Matplotlib renderer produces an ordered set of full-size pages: one square network overview per visible authored altitude layer, then one independently framed local overview per archived vertiport. Each facility page uses padded square bounds around that vertiport's approach edges, holding ramps, and facility-to-network corridors rather than shrinking every vertiport into one city-wide panel. Nodes, helipad outlines, facility boundaries, and lane-to-lane connector curves are not drawn. Facility corridors are retained because the planner books their reservation windows; their geometry is rebuilt with the same altitude-aware cubic Bezier construction used in the Unity 3D scene. Use `Previous Figure` and `Next Figure` to turn pages; zoom, pan, reset, and export operate on the current page. Lane heat uses archived traversal counts and vertiport paths use archived reservation time. Layer visibility is an analysis choice and does not modify the authored layer's enabled state.

Bar-comparison and parameter-response figures are generated headlessly at high resolution with pandas, NumPy, and Matplotlib rather than being drawn with Unity UI primitives. Multi-metric figures use separate labelled panels and distinguishable metric palettes. The chart and result-table viewers both support zoom, pan, reset, and export. Exact repeated parameter combinations use their real mean, sample standard deviation, and sample count only; no synthetic variance or smoothing is introduced. If additional input parameters vary but are not selected as X axes, their values remain separate fixed-context panels or curves rather than being collapsed into an unqualified marginal mean. Three-factor data is shown as semi-transparent observed heatmap planes stacked along the third X axis, not as a fitted surface or waterfall that could imply untested intermediate values. These plots describe empirical response over the tested parameter grid; they are not Sobol or Morris global-sensitivity indices.

## 5. Portable data and output paths

The Windows player uses the executable directory as its data root:

```text
Structured-Air-Route-Simulator.exe
SARScenes/
  scenes.json
  <scene-id>/
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
ImportCache/
Settings/
```

Each run directory is self-describing. Its versioned `manifest.json` stores scene identity, timestamps, status, duration, captured parameter values, parsed metrics, artifact warnings, a topology fingerprint, and relative archive file names. Batch manifests also record their child run IDs. The axis network, generated lane-level network, and vertiports are snapshotted at run start. `SimulationRecording.json` is stored as `SimulationRecording.json.gz` to reduce history size and is decompressed transparently when selected in Data Visualization. The latest-run files under `Output/` are retained for existing export workflows; the history service takes independent snapshots and does not move or delete them.

Keep the whole extracted Windows directory together and place it in a writable location. Do not distribute only the `.exe`.

The Unity Editor intentionally keeps runtime scenario data under Unity's `persistentDataPath` so development and tests do not overwrite a packaged distribution. Older Windows releases that stored data under LocalLow are migrated by copying missing files once; the legacy source is not deleted.

Supported portable path prefixes are `scene://`, `app://`, `user://`, and `streaming://`.

## 6. Synchronizing packaged route examples

Authoritative examples are kept in `Assets/DataBase/`; release copies are kept in `Assets/StreamingAssets/SARData/`.

After changing an example, run:

```text
Tools > SAR > Sync Packaged Route Data
```

Synchronization is explicit so every input change remains visible in Git. See [RouteFileFormat.md](RouteFileFormat.md) for the JSON/XML schema.

## 7. Building Windows x64

Exit Play Mode, then run:

```text
Tools > SAR > Validate Build
Tools > SAR > Build Windows x64
```

One validated player is published into two clean release directories:

```text
Builds/Releases/en-US/
Builds/Releases/zh-CN/
```

The pre-build check verifies the Build Settings scene, packaged demand files, Chinese runtime catalog, Python plotting worker, dependency locks, and third-party licenses. The command safely recreates only `Builds/Windows` as a staging directory, preventing stale `Output/`, `SARScenes/`, settings, or migration files from leaking into a release. After Unity finishes, it checks embedded hashes, copies the English README, license, notices, and operation guides, and publishes the two localized directories above. Their player binaries are identical; only the executable-adjacent `SAR.locale.json` selects `en-US` or `zh-CN`. Project source, scene object names, and Inspector labels remain English.

Create separate ZIP archives containing the entire `en-US` and `zh-CN` directories and publish both as GitHub Release assets. Keep the Unity source project in the repository branch; do not commit generated player binaries to the source tree. To regenerate only the localized copies from an already validated staging player, use `Tools > SAR > Publish Bilingual Releases from Windows Build`.

## 8. New-repository publication boundary

The source repository should include:

- `Assets/`, including source code, the main scene, required default inputs, prefabs, materials, tests, packaged route examples, the analytics worker sources, and the runtime localization catalog;
- `JointSimulation/`, the Python bridge, its tests, and the AirSim `settings.example.json`;
- `Tools/SARPlotWorker/`, the worker build script, its pinned build requirements, and the license collector;
- `Packages/` and `ProjectSettings/`;
- `OperationGuide/`, both READMEs, `LICENSE`, `THIRD_PARTY_NOTICES.md`, `.gitignore`, and `.gitattributes`.

The source repository should not include:

- `Library/`, `Temp/`, `Obj/`, `Logs/`, `.vs/`, or generated solution/project files;
- `Builds/` or unpacked Windows players;
- `Output/`, simulation recordings, trajectories, metrics tables, or parameter-sweep results;
- plotting/visualization scripts stored with experimental outputs or their derived figures;
- the Shenzhen whitebox scene and `Assets/ImportedCityModels/`, which are large machine-local authoring assets;
- `bridge_diag.csv`, `follow_camera.json`, the PyInstaller spec written by `build_worker.ps1`, or `__pycache__`;
- local QA screenshots, test results, imported-city caches, or machine-specific settings.

This separation keeps the Git history reviewable while still making the no-Unity application available through Releases.

## 9. Verification before publication

Before creating a release:

1. run all Unity EditMode tests;
2. check that the Console contains no project-code errors;
3. validate the build;
4. build the Windows player;
5. if the analytics worker sources changed, rebuild it with `Tools/SARPlotWorker/build_worker.ps1` and confirm `Worker/manifest.json` was updated;
6. copy the proposed release directory to a disposable smoke-test folder and launch that copy;
7. verify route generation, parameter application, one short batch run, CSV output, and `F10` hide/restore;
8. remove only the verified disposable smoke-test folder, then confirm the release directory itself still contains no runtime-state folders;
9. if the joint simulation is part of the delivery, run one short scenario against the AirSim host and confirm the vehicles follow and the camera holds steady;
10. inspect `git status` and confirm no generated data or local settings are staged.

Use `git revert` for traceable rollback of published changes. Avoid destructive resets when research data has not been backed up.
