# Unreal host project (SZ_TestArea)

Unreal Engine 4.27 project used as the rendering host by the joint simulation. Unity
remains the motion master: this map only receives poses and draws the aircraft.

This project is part of the experimental joint-simulation pilot. See the status table
in the top-level `README.md` for what is and is not implemented.

## What is here

| Path | Contents |
|---|---|
| `SZ_TestArea.uproject` | Project descriptor; enables `AirSim`, `EditorScriptingUtilities`, `PythonScriptPlugin` |
| `Config/DefaultEngine.ini` | Startup map `TestArea`, `AirSimGameMode`, no auto-exposure / bloom / motion blur, zero gravity |
| `Content/Maps/TestArea.umap` | The map: TestArea mesh, ground plane, sun, sky, `PlayerStart` at the Unreal origin |
| `Content/TestArea/TestArea.uasset` | Baked static mesh of the Shenzhen TestArea, 476k vertices / 302k triangles |
| `Content/Materials/` | `M_BuildingGray` and `M_Ground`, the flat-shaded whitebox materials |
| `Content/Python/import_testarea.py` | Regenerates the map from an exported OBJ |

## What is not here

- **The AirSim plugin.** Install AirSim 1.8.1 and place it at `Plugins/AirSim` (link or
  copy) before opening the project. The plugin is not redistributed in this repository.
- **The Shenzhen whitebox Unity scene** the mesh was exported from. It is 628 MB plus a
  1.1 GB city cache, so it is published separately — see `EXTERNAL_ASSETS.md` in the
  repository root.

## Opening the project

1. Install Unreal Engine 4.27 and build the AirSim 1.8.1 plugin for it.
2. Put the plugin at `UnrealHost/SZ_TestArea/Plugins/AirSim`.
3. Open `SZ_TestArea.uproject`. The default map is `/Game/Maps/TestArea` and the game
   mode is `AirSimGameMode`.
4. Copy `JointSimulation/settings.example.json` to `Documents/AirSim/settings.json`.
   `ViewMode` must stay `Manual` and `OriginGeopoint` must stay unchanged: it is the
   origin compiled into `airsim_bridge/coords.py` and into the Unity scene.

`PlayerStart` sits on the Unreal origin on purpose. Moving it shifts the AirSim NED
origin, which offsets every mirrored aircraft.

## Regenerating the map

`Content/Python/import_testarea.py` rebuilds the map from an OBJ exported by the Unity
`ITSAC > Export TestArea OBJ for AirSim` menu item. Run it from the editor's Python
console:

```text
py "<repo>/UnrealHost/SZ_TestArea/Content/Python/import_testarea.py"
```

It reads `<repo>/ue_import/testarea/TestArea.obj` by default; set `SAR_UE_IMPORT_DIR`
or pass a path as the first argument to point it elsewhere.

The script rebuilds the materials, imports the mesh, creates the ground plane, sun,
sky, sky light and exposure volume, and saves the level. The OBJ importer may mirror
the Y axis, so the script flips the actor's Y scale when the mesh centre lands on the
negative side; that flip puts east back on Unreal +Y.

## Coordinates

| Space | Axes | Units |
|---|---|---|
| Unity world | x = east, y = up, z = north | metres |
| AirSim NED | x = north, y = east, z = down | metres |
| Unreal world | X = north, Y = east, Z = up | centimetres |

The Unity-to-Unreal mapping is `NED = (Unity.z, Unity.x, −Unity.y)`.