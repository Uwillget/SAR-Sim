# External assets

Most of this repository is self-contained: the Unity project, the Unreal host project,
the Python bridge, the documentation and the packaged Windows application build from
what is committed here. Two large authoring assets cannot live in Git, so they are
published separately.

## What ships in this repository

| Asset | Path | Notes |
|---|---|---|
| Study scene (Xinjiekou) | `Assets/Resources/Scenes/1.unity` | The scenario used in the paper. Self-contained: two altitude layers (75 m, 125 m), 102 axis nodes, 160 axis edges, 2 interlayer corridors, 7 vertiports, 126 helipads, 978-building city model. This is the only enabled build scene. |
| City-model sources of the study scene | `Assets/Resources/CityModels/1.skp`, `2.skp`, `3.skp` | The three CADMapper SketchUp tiles that the 978 buildings in `1.unity` are instanced from. Referenced by the scene directly. |
| Earlier single-tile city | `Assets/Resources/CityModels/Xinjiekou.skp` and `Xinjiekou.prefab` | The one-tile import used by the retired `SampleScene`. No shipped scene references it; it is kept as source material and can be deleted without affecting the build. |
| Unreal host project | `UnrealHost/SZ_TestArea/` | UE 4.27 project used by the joint simulation. Needs the AirSim 1.8.1 plugin, which is not redistributed here. |
| Python bridge | `JointSimulation/airsim_bridge/` | UDP receiver, AirSim pose writer, camera driver, coordinate tests. |

## What is published outside this repository

The joint simulation was developed against a Shenzhen whitebox scene that is far too
large for a Git repository: `ShenzhenSKPWhitebox.unity` alone is roughly 628 MB, and the
imported SketchUp cache it references (Nanshan, Futian, Longhua, Bao'an) adds another
1.1 GB.

| Asset | Approximate size | Location |
|---|---|---|
| `ShenzhenSKPWhitebox.unity` | 628 MB | Hugging Face (link below) |
| `Assets/ImportedCityModels/` | 1.1 GB | Hugging Face (link below) |
| Exported OBJ meshes (`ue_import/`) | 300 MB | Hugging Face (link below) |

Hugging Face repository: <https://huggingface.co/datasets/Uwillget/SAR-Sim-external-assets>

The dataset keeps the repository layout, so each file can be dropped straight into
the path shown above without renaming. All `.meta` files are included: the scene
references the city models by GUID, and a folder copied without them loses every
building reference.

## Reproducing the joint simulation

The bridge itself is scene-agnostic: it streams whatever aircraft Unity is simulating.
Reproducing the published joint-simulation demonstration, however, needs the Shenzhen
scene, because that is the city the Unreal host was built from.

1. Download the Shenzhen scene from the Hugging Face repository above and place it at
   `Assets/Resources/Scenes/ShenzhenSKPWhitebox.unity`, with the imported city cache at
   `Assets/ImportedCityModels/`.
2. Open `UnrealHost/SZ_TestArea/SZ_TestArea.uproject` in Unreal Engine 4.27 with the
   AirSim 1.8.1 plugin installed under `Plugins/AirSim`.
3. Copy `JointSimulation/settings.example.json` to `Documents/AirSim/settings.json`.
4. Follow the joint-simulation section of the top-level `README.md`.

Running the bridge against the shipped Xinjiekou scene instead also works as a pipeline
check. The aircraft move correctly in Unity, but the Shenzhen Unreal map will not match
the Xinjiekou geometry, so treat that combination as a connectivity test only.