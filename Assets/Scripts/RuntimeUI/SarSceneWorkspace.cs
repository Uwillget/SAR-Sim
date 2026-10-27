using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace RoadNetworkEditor.RuntimeUI
{
    [Serializable]
    public sealed class SarSceneProfile
    {
        public string Id;
        public string DisplayName;
        [JsonIgnore]
        public string AxisNetworkPath;
        [JsonIgnore]
        public string VertiportPath;
        [JsonIgnore]
        public string CityModelPath;
        public string NetworkSourcePath;
        public string VertiportSourcePath;
        public string CitySourcePath;
        public Vector3 CityPosition;
        public Vector3 CityEulerAngles;
        public bool IsClosed;
        public string CreatedUtc;
        public string ModifiedUtc;
    }

    [Serializable]
    internal sealed class SarSceneRegistry
    {
        public string ActiveSceneId;
        public List<SarSceneProfile> Scenes = new List<SarSceneProfile>();
    }

    /// <summary>
    /// Persistent multi-scene workspace. Each tab owns a complete copy of its city,
    /// axis network and facility data so editing one scene cannot mutate another.
    /// </summary>
    public sealed class SarSceneWorkspace : MonoBehaviour
    {
        private const string RegistryFileName = "scenes.json";
        private SimulationManager manager;
        private RuntimeAirspaceVisualizer visualizer;
        private ParameterSweepRunner sweepRunner;
        private SarSceneRegistry registry;
        private Transform cityRoot;
        private bool persistenceEnabled = true;
        private string transientWorkspaceRoot;
        private readonly HashSet<string> dirtySceneIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> savedFingerprints =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public event Action ProfilesChanged;
        public event Action ActiveSceneChanged;
        public event Action<string> StatusChanged;

        public IReadOnlyList<SarSceneProfile> Profiles => registry?.Scenes ?? (IReadOnlyList<SarSceneProfile>)Array.Empty<SarSceneProfile>();
        public IReadOnlyList<SarSceneProfile> OpenProfiles => registry?.Scenes?
            .Where(scene => scene != null && !scene.IsClosed).ToList()
            ?? (IReadOnlyList<SarSceneProfile>)Array.Empty<SarSceneProfile>();
        public IReadOnlyList<SarSceneProfile> ClosedProfiles => registry?.Scenes?
            .Where(scene => scene != null && scene.IsClosed).ToList()
            ?? (IReadOnlyList<SarSceneProfile>)Array.Empty<SarSceneProfile>();
        public SarSceneProfile ActiveProfile => registry?.Scenes.FirstOrDefault(scene => scene.Id == registry.ActiveSceneId);
        public string WorkspaceRoot => persistenceEnabled
            ? SarRuntimePaths.SceneRoot
            : transientWorkspaceRoot ?? Path.Combine(Application.temporaryCachePath, "SARWorkbenchPreview");
        public bool PersistenceEnabled => persistenceEnabled;
        /// <summary>
        /// Editor play uses the open Unity scene. The saved workspace profile is not
        /// loaded on startup and is not written back when play mode ends.
        /// </summary>
        public bool PreserveOpenUnityScene { get; private set; }
        public string ActiveSceneFolder => ActiveProfile == null ? null : SceneFolder(ActiveProfile);
        public string ActiveHistoryFolder => ActiveSceneFolder == null
            ? null : Path.Combine(ActiveSceneFolder, SarSimulationHistory.HistoryFolderName);
        public bool ActiveSceneIsDirty => ActiveProfile != null && dirtySceneIds.Contains(ActiveProfile.Id);

        public void Initialize(SimulationManager simulationManager,
            RuntimeAirspaceVisualizer airspaceVisualizer,
            bool enablePersistence = true)
        {
            manager = simulationManager;
            visualizer = airspaceVisualizer;
            sweepRunner = FindObjectOfType<ParameterSweepRunner>(true);
            cityRoot = EnsureCityRoot();
            persistenceEnabled = enablePersistence;
            if (!persistenceEnabled)
            {
                transientWorkspaceRoot = Path.Combine(Application.temporaryCachePath,
                    "SARWorkbenchPreview-" + GetInstanceID());
                registry = new SarSceneRegistry();
                SarSceneProfile preview = NewProfile("editor-layout-preview", "Editor Layout Preview");
                registry.Scenes.Add(preview);
                registry.ActiveSceneId = preview.Id;
                return;
            }
            Directory.CreateDirectory(WorkspaceRoot);
            LoadRegistry();
            EnsureDefaultScene();
#if UNITY_EDITOR
            // The Unity scene already holds the authored network and vertiports.
            // Loading the saved profile here replaces both, and the axis import
            // regenerates Assets/DataBase/FullNetwork.json.
            PreserveOpenUnityScene = true;
            RecordBaseline(ActiveProfile);
            Debug.Log("[SarSceneWorkspace] Editor play keeps the open Unity scene. " +
                      "The saved workspace profile was not loaded.");
#else
            if (!TryLoadProfile(ActiveProfile, out string error))
                Debug.LogWarning($"[SarSceneWorkspace] Saved scene could not be restored: {error}");
#endif
        }

        public SarSceneProfile CreateScene(string displayName)
        {
            if (!PrepareForSceneLifecycleMutation(out string lifecycleError))
            {
                StatusChanged?.Invoke(lifecycleError);
                return null;
            }
            SarSceneProfile previous = ActiveProfile;
            SaveActiveScene();
            string id = "scene-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var profile = NewProfile(id, UniqueName(string.IsNullOrWhiteSpace(displayName) ? "New Scene" : displayName.Trim()));
            registry.Scenes.Add(profile);
            CreateEmptySceneFiles(profile);
            if (!TryLoadProfileWithRollback(profile, previous, out string error))
            {
                registry.Scenes.Remove(profile);
                SaveRegistry();
                StatusChanged?.Invoke(error);
                return null;
            }
            registry.ActiveSceneId = profile.Id;
            SaveRegistry();
            ProfilesChanged?.Invoke();
            ActiveSceneChanged?.Invoke();
            StatusChanged?.Invoke($"Created independent scene \"{profile.DisplayName}\". ");
            return profile;
        }

        public SarSceneProfile DuplicateActiveScene()
        {
            SarSceneProfile source = ActiveProfile;
            if (source == null) return null;
            if (!PrepareForSceneLifecycleMutation(out string lifecycleError))
            {
                StatusChanged?.Invoke(lifecycleError);
                return null;
            }
            SaveActiveScene();
            string id = "scene-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var copy = NewProfile(id, UniqueName(source.DisplayName + " Copy"));
            registry.Scenes.Add(copy);
            Directory.CreateDirectory(SceneFolder(copy));
            CopyIfPresent(source.AxisNetworkPath, copy.AxisNetworkPath);
            CopyIfPresent(source.VertiportPath, copy.VertiportPath);
            CopyIfPresent(source.CityModelPath, copy.CityModelPath);
            SetOwnedSourcePaths(copy);
            copy.CityPosition = source.CityPosition;
            copy.CityEulerAngles = source.CityEulerAngles;
            if (!TryLoadProfileWithRollback(copy, source, out string error))
            {
                registry.Scenes.Remove(copy);
                SaveRegistry();
                StatusChanged?.Invoke(error);
                return null;
            }
            registry.ActiveSceneId = copy.Id;
            SaveRegistry();
            ProfilesChanged?.Invoke();
            ActiveSceneChanged?.Invoke();
            StatusChanged?.Invoke($"Duplicated as independent scene \"{copy.DisplayName}\". ");
            return copy;
        }

        public bool RecycleActiveScene(out string error)
        {
            error = null;
            SarSceneProfile active = ActiveProfile;
            if (active == null) { error = "No active scene is available."; return false; }
            if (OpenProfiles.Count <= 1) { error = "At least one scene must remain open."; return false; }
            if (!PrepareForSceneLifecycleMutation(out error)) return false;
            SaveActiveScene();
            SarSceneProfile fallback = OpenProfiles.First(scene => scene != active);
            string source = SceneFolder(active);
            string recycleRoot = Path.Combine(WorkspaceRoot, "_Recycle");
            Directory.CreateDirectory(recycleRoot);
            string destination = Path.Combine(recycleRoot,
                active.Id + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try
            {
                if (!TryLoadProfileWithRollback(fallback, active, out error)) return false;
                if (Directory.Exists(source)) Directory.Move(source, destination);
                registry.Scenes.Remove(active);
                dirtySceneIds.Remove(active.Id);
                registry.ActiveSceneId = fallback.Id;
                SaveRegistry();
                ProfilesChanged?.Invoke();
                ActiveSceneChanged?.Invoke();
                StatusChanged?.Invoke($"Scene \"{active.DisplayName}\" moved to the scene recycle bin. ");
                return true;
            }
            catch (Exception ex)
            {
                TryLoadProfile(active, out _);
                error = $"Failed to move the scene to the recycle bin: {ex.Message}";
                return false;
            }
        }

        public bool RenameActiveScene(string displayName, out string error)
        {
            error = null;
            SarSceneProfile active = ActiveProfile;
            if (active == null) { error = "No active scene is available."; return false; }
            string name = displayName?.Trim();
            if (string.IsNullOrWhiteSpace(name)) { error = "Scene name cannot be empty."; return false; }
            if (registry.Scenes.Any(scene => scene != active &&
                string.Equals(scene.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
            {
                error = "That scene name already exists.";
                return false;
            }
            active.DisplayName = name;
            Touch(active);
            SaveRegistry();
            ProfilesChanged?.Invoke();
            return true;
        }

        public bool Activate(string sceneId, out string error)
        {
            error = null;
            SarSceneProfile target = registry.Scenes.FirstOrDefault(scene => scene.Id == sceneId);
            if (target == null) { error = "The target scene does not exist."; return false; }
            if (target.IsClosed) { error = "The target scene tab is closed. Reopen it first."; return false; }
            if (target == ActiveProfile) return true;
            if (!PrepareForSceneLifecycleMutation(out error)) return false;
            SarSceneProfile previous = ActiveProfile;
            SaveActiveScene();
            if (!TryLoadProfileWithRollback(target, previous, out error)) return false;
            registry.ActiveSceneId = target.Id;
            SaveRegistry();
            ActiveSceneChanged?.Invoke();
            StatusChanged?.Invoke($"Switched to scene \"{target.DisplayName}\". ");
            return true;
        }

        public bool ImportAxisNetwork(string sourcePath, out string error)
        {
            error = null;
            SarSceneProfile active = ActiveProfile;
            if (active == null) { error = "No active scene is available."; return false; }
            string resolvedPath = ResolveActivePath(sourcePath);
            Debug.Log($"[SarSceneWorkspace] Importing axis network: '{resolvedPath}' -> '{active.AxisNetworkPath}'.");
            if (!manager.networkManager.TryImportAxisNetwork(resolvedPath, out error)) return false;
            manager.networkManager.ExportAxisNetworkToJSON(active.AxisNetworkPath);
            active.NetworkSourcePath = SarPortablePath.ToPortable(active.AxisNetworkPath, SceneFolder(active));
            Touch(active);
            RefreshTopology();
            SaveRegistry();
            RecordBaseline(active);
            StatusChanged?.Invoke($"Air-route network imported and saved to the current scene: {active.NetworkSourcePath}");
            return true;
        }

        public bool ImportVertiports(string sourcePath, out string error)
        {
            error = null;
            SarSceneProfile active = ActiveProfile;
            if (active == null) { error = "No active scene is available."; return false; }
            string resolvedPath = ResolveActivePath(sourcePath);
            Debug.Log($"[SarSceneWorkspace] Importing vertiport data: '{resolvedPath}' -> '{active.VertiportPath}'.");
            if (!manager.vertiportManager.TryImportFromJSON(resolvedPath, out error)) return false;
            manager.vertiportManager.ExportToJSON(active.VertiportPath);
            active.VertiportSourcePath = SarPortablePath.ToPortable(active.VertiportPath, SceneFolder(active));
            Touch(active);
            RefreshTopology();
            SaveRegistry();
            RecordBaseline(active);
            StatusChanged?.Invoke($"Vertiport structure imported and saved to the current scene: {active.VertiportSourcePath}");
            return true;
        }

        public bool ImportCityModel(string sourcePath, out string error)
        {
            return ImportCityModel(sourcePath, Vector3.zero, out error, out _);
        }

        public bool ImportCityModel(string sourcePath, Vector3 viewCenter, out string error, out Bounds cityBounds)
        {
            error = null;
            cityBounds = default;
            SarSceneProfile active = ActiveProfile;
            if (active == null) { error = "No active scene is available."; return false; }
            string resolvedPath = ResolveActivePath(sourcePath);
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            {
                error = $"City-model file not found: {sourcePath}";
                return false;
            }

            string extension = Path.GetExtension(resolvedPath).ToLowerInvariant();
            string loadPath = resolvedPath;
            if (extension == ".skp")
            {
#if UNITY_EDITOR
                if (!TryConvertSketchUp(resolvedPath, active.CityModelPath, out error)) return false;
                loadPath = active.CityModelPath;
#else
                if (!SarSketchUpRuntimeConverter.TryConvertToObj(resolvedPath, out loadPath, out error)) return false;
#endif
            }

            if (!SarCityModelPackage.TryLoad(loadPath, cityRoot, out error, out int meshCount)) return false;
            if (!TryGetCityBounds(out cityBounds))
            {
                error = "The city model contains no renderable meshes.";
                return false;
            }

            // Imported CAD coordinates commonly carry a large map-origin offset.
            // Align the horizontal geometry centre with the current view pivot and
            // place the lowest geometry point on Y=0, making the model immediately
            // visible without destroying its internal dimensions.
            cityRoot.position += ComputeCityPlacementDelta(cityBounds, viewCenter);
            active.CityPosition = cityRoot.position;
            if (!TryGetCityBounds(out cityBounds)) cityBounds = default;
            if (!string.Equals(Path.GetFullPath(loadPath), Path.GetFullPath(active.CityModelPath),
                    StringComparison.OrdinalIgnoreCase))
                SarCityModelPackage.Export(cityRoot, active.CityModelPath);
            active.CitySourcePath = SarPortablePath.ToPortable(active.CityModelPath, SceneFolder(active));
            Touch(active);
            visualizer?.RebindCityModel();
            SaveRegistry();
            RecordBaseline(active);
            StatusChanged?.Invoke($"City model loaded and centered ({meshCount} meshes): {active.CitySourcePath}");
            return true;
        }

        public static Vector3 ComputeCityPlacementDelta(Bounds bounds, Vector3 viewCenter)
        {
            return new Vector3(viewCenter.x - bounds.center.x, -bounds.min.y,
                viewCenter.z - bounds.center.z);
        }

        private bool TryGetCityBounds(out Bounds bounds)
        {
            bounds = default;
            if (cityRoot == null) return false;
            Renderer[] renderers = cityRoot.GetComponentsInChildren<Renderer>(true);
            bool found = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return found;
        }

        public void SetActiveCityTransform(Vector3 position, Vector3 eulerAngles)
        {
            SarSceneProfile active = ActiveProfile;
            if (active == null || cityRoot == null) return;
            if (!IsFinite(position) || !IsFinite(eulerAngles)) return;
            active.CityPosition = position;
            active.CityEulerAngles = NormalizeEuler(eulerAngles);
            ApplyCityTransform(active);
            Touch(active);
            dirtySceneIds.Add(active.Id);
            SaveRegistry();
            visualizer?.RebindCityModel();
            StatusChanged?.Invoke("City-model position and rotation updated. ");
        }

        public string ResolveActivePath(string path)
        {
            SarSceneProfile active = ActiveProfile;
            return SarPortablePath.Resolve(path, active == null ? null : SceneFolder(active));
        }

        public string ToActivePortablePath(string path)
        {
            SarSceneProfile active = ActiveProfile;
            return SarPortablePath.ToPortable(path, active == null ? null : SceneFolder(active));
        }

        public void MarkActiveDirty()
        {
            SarSceneProfile active = ActiveProfile;
            if (active != null) dirtySceneIds.Add(active.Id);
        }

        public bool IsDirty(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId)) return false;
            if (dirtySceneIds.Contains(sceneId)) return true;
            if (ActiveProfile?.Id != sceneId) return false;
            string current = ComputeActiveFingerprint();
            return !savedFingerprints.TryGetValue(sceneId, out string saved) || saved != current;
        }

        public bool CloseScene(string sceneId, bool saveChanges, out string error)
        {
            error = null;
            SarSceneProfile target = registry?.Scenes.FirstOrDefault(scene => scene.Id == sceneId);
            if (target == null) { error = "The target scene does not exist."; return false; }
            if (target.IsClosed) return true;
            if (OpenProfiles.Count <= 1) { error = "At least one scene must remain open."; return false; }
            if (!PrepareForSceneLifecycleMutation(out error)) return false;

            bool isActive = target == ActiveProfile;
            if (isActive)
            {
                if (saveChanges && !SaveActiveScene())
                {
                    error = "The current scene could not be saved, so its tab remains open.";
                    return false;
                }
                SarSceneProfile fallback = OpenProfiles.First(scene => scene != target);
                if (!TryLoadProfileWithRollback(fallback, target, out error)) return false;
                registry.ActiveSceneId = fallback.Id;
            }

            target.IsClosed = true;
            dirtySceneIds.Remove(target.Id);
            SaveRegistry();
            ProfilesChanged?.Invoke();
            if (isActive) ActiveSceneChanged?.Invoke();
            StatusChanged?.Invoke($"Scene tab \"{target.DisplayName}\" closed; its scene files were retained. ");
            return true;
        }

        public bool ReopenScene(string sceneId, bool activate, out string error)
        {
            error = null;
            SarSceneProfile target = registry?.Scenes.FirstOrDefault(scene => scene.Id == sceneId);
            if (target == null) { error = "The target scene does not exist."; return false; }
            if (!target.IsClosed && (!activate || target == ActiveProfile)) return true;
            if (!PrepareForSceneLifecycleMutation(out error)) return false;
            target.IsClosed = false;
            SaveRegistry();
            ProfilesChanged?.Invoke();
            if (!activate) return true;
            return Activate(target.Id, out error);
        }

        public bool SaveActiveScene()
        {
            if (!persistenceEnabled) return false;
            SarSceneProfile active = ActiveProfile;
            if (active == null || manager == null) return false;
            try
            {
                Directory.CreateDirectory(SceneFolder(active));
                manager.networkManager.ExportAxisNetworkToJSON(active.AxisNetworkPath);
                manager.vertiportManager.ExportToJSON(active.VertiportPath);
                SarCityModelPackage.Export(cityRoot, active.CityModelPath);
                Touch(active);
                SaveRegistry();
                RecordBaseline(active);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SarSceneWorkspace] Failed to save active scene: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Scene tabs are simulation ownership boundaries. A parameter sweep must be
        /// finalized before any operation can create, reopen, close, or replace the
        /// active profile; otherwise a delayed member could be archived under the new
        /// scene. StopSweepInternal also stops the current member and restores every
        /// swept parameter before the workspace saves or loads scene data.
        /// </summary>
        private bool PrepareForSceneLifecycleMutation(out string error)
        {
            error = null;
            try
            {
                if (sweepRunner == null)
                    sweepRunner = FindObjectOfType<ParameterSweepRunner>(true);
                if (sweepRunner != null && sweepRunner.isRunning)
                {
                    sweepRunner.StopSweepInternal();
                    if (sweepRunner.isRunning)
                    {
                        error = "The batch experiment could not be cancelled cleanly; the scene operation was aborted.";
                        return false;
                    }
                }

                // A standalone run is also scoped to the current scene. This second
                // check is normally a no-op after cancelling a sweep, but guarantees
                // the same ordering when no ParameterSweepRunner is present.
                if (manager != null && manager.IsRunning()) manager.StopSimulation();
                return true;
            }
            catch (Exception exception)
            {
                error = $"Stop the current simulation before changing scenes: {exception.Message}";
                Debug.LogError($"[SarSceneWorkspace] {error}\n{exception}");
                return false;
            }
        }

        public void OpenCadmapper()
        {
            Application.OpenURL("https://cadmapper.com/pro/home");
            StatusChanged?.Invoke("CADMapper opened. After downloading the SketchUp model, return to Scene Data to import it. ");
        }

        private bool TryLoadProfile(SarSceneProfile profile, out string error)
        {
            error = null;
            if (profile == null) { error = "The scene profile does not exist."; return false; }
            if (manager == null || manager.networkManager == null || manager.vertiportManager == null)
            {
                error = "The scene profile cannot be loaded until NetworkManager and VertiportManager are assigned.";
                return false;
            }
            if (!manager.networkManager.TryImportAxisNetwork(profile.AxisNetworkPath, out error)) return false;
            if (!manager.vertiportManager.TryImportFromJSON(profile.VertiportPath, out error)) return false;
            ApplyCityTransform(profile);
            if (!SarCityModelPackage.TryLoad(profile.CityModelPath, cityRoot, out error, out _)) return false;
            RefreshTopology();
            visualizer?.RebindCityModel();
            RecordBaseline(profile);
            return true;
        }

        private bool TryLoadProfileWithRollback(
            SarSceneProfile target,
            SarSceneProfile rollback,
            out string error)
        {
            if (TryLoadProfile(target, out error)) return true;
            string targetError = error;
            if (rollback != null && rollback != target &&
                !TryLoadProfile(rollback, out string rollbackError))
            {
                error = $"The target scene failed to load: {targetError}; rollback to the original scene also failed: {rollbackError}";
                Debug.LogError($"[SarSceneWorkspace] {error}");
                return false;
            }
            error = $"The target scene failed to load; the original scene was restored: {targetError}";
            return false;
        }

        private void RefreshTopology()
        {
            manager.RefreshRuntimeTopology();
        }

        private void LoadRegistry()
        {
            string path = Path.Combine(WorkspaceRoot, RegistryFileName);
            try
            {
                registry = File.Exists(path)
                    ? JsonConvert.DeserializeObject<SarSceneRegistry>(File.ReadAllText(path),
                        new Vector3Converter())
                    : new SarSceneRegistry();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarSceneWorkspace] Scene registry was reset: {ex.Message}");
                registry = new SarSceneRegistry();
            }
            if (registry == null) registry = new SarSceneRegistry();
            if (registry.Scenes == null) registry.Scenes = new List<SarSceneProfile>();
        }

        private void EnsureDefaultScene()
        {
            registry.Scenes.RemoveAll(scene => scene == null || string.IsNullOrWhiteSpace(scene.Id));
            if (registry.Scenes.Count == 0)
            {
                SarSceneProfile profile = NewProfile("default", "Default Research Scene");
                registry.Scenes.Add(profile);
                registry.ActiveSceneId = profile.Id;
                Directory.CreateDirectory(SceneFolder(profile));
                manager.networkManager.ExportAxisNetworkToJSON(profile.AxisNetworkPath);
                manager.vertiportManager.ExportToJSON(profile.VertiportPath);
                SarCityModelPackage.Export(cityRoot, profile.CityModelPath);
                SaveRegistry();
            }
            if (registry.Scenes.All(scene => scene.Id != registry.ActiveSceneId || scene.IsClosed))
            {
                SarSceneProfile firstOpen = registry.Scenes.FirstOrDefault(scene => !scene.IsClosed);
                if (firstOpen == null)
                {
                    firstOpen = registry.Scenes[0];
                    firstOpen.IsClosed = false;
                }
                registry.ActiveSceneId = firstOpen.Id;
            }
            foreach (SarSceneProfile profile in registry.Scenes) RepairPaths(profile);
            SarSceneProfile active = ActiveProfile;
            if (active != null && active.Id == "default")
            {
                Directory.CreateDirectory(SceneFolder(active));
                // Repair only the missing artifact. Replacing all three would discard
                // valid edits in the surviving scene files after a partial disk failure.
                if (!File.Exists(active.AxisNetworkPath))
                    manager.networkManager.ExportAxisNetworkToJSON(active.AxisNetworkPath);
                if (!File.Exists(active.VertiportPath))
                    manager.vertiportManager.ExportToJSON(active.VertiportPath);
                if (!File.Exists(active.CityModelPath))
                    SarCityModelPackage.Export(cityRoot, active.CityModelPath);
            }
            SaveRegistry();
        }

        private SarSceneProfile NewProfile(string id, string displayName)
        {
            string now = DateTime.UtcNow.ToString("o");
            var profile = new SarSceneProfile { Id = id, DisplayName = displayName, CreatedUtc = now, ModifiedUtc = now };
            RepairPaths(profile);
            return profile;
        }

        private void RepairPaths(SarSceneProfile profile)
        {
            string folder = SceneFolder(profile);
            profile.AxisNetworkPath = Path.Combine(folder, "AxisNetwork.json");
            profile.VertiportPath = Path.Combine(folder, "VertiportData.json");
            profile.CityModelPath = Path.Combine(folder, "CityModel" + SarCityModelPackage.Extension);
            SetOwnedSourcePaths(profile);
        }

        private void SetOwnedSourcePaths(SarSceneProfile profile)
        {
            if (profile == null) return;
            string folder = SceneFolder(profile);
            profile.NetworkSourcePath = SarPortablePath.ToPortable(profile.AxisNetworkPath, folder);
            profile.VertiportSourcePath = SarPortablePath.ToPortable(profile.VertiportPath, folder);
            profile.CitySourcePath = SarPortablePath.ToPortable(profile.CityModelPath, folder);
        }

        private void CreateEmptySceneFiles(SarSceneProfile profile)
        {
            Directory.CreateDirectory(SceneFolder(profile));
            var emptyNetwork = new Network { NetworkId = "AxisNetwork" };
            File.WriteAllText(profile.AxisNetworkPath, JsonConvert.SerializeObject(emptyNetwork, Formatting.Indented,
                new Vector3Converter()));
            File.WriteAllText(profile.VertiportPath, "[]");
            var temp = new GameObject("EmptyCity");
            try { SarCityModelPackage.Export(temp.transform, profile.CityModelPath); }
            finally
            {
                if (Application.isPlaying) Destroy(temp);
                else DestroyImmediate(temp);
            }
        }

        private Transform EnsureCityRoot()
        {
            CityModelController controller = FindObjectOfType<CityModelController>(true);
            if (controller != null) return controller.transform;
            var root = new GameObject("CityModel");
            root.AddComponent<CityModelController>();
            return root.transform;
        }

        private void ApplyCityTransform(SarSceneProfile profile)
        {
            if (cityRoot == null || profile == null) return;
            cityRoot.position = profile.CityPosition;
            cityRoot.rotation = Quaternion.Euler(profile.CityEulerAngles);
            cityRoot.localScale = Vector3.one;
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        private static Vector3 NormalizeEuler(Vector3 value) => new Vector3(
            Mathf.Repeat(value.x, 360f), Mathf.Repeat(value.y, 360f), Mathf.Repeat(value.z, 360f));

        private void RecordBaseline(SarSceneProfile profile)
        {
            if (profile == null) return;
            savedFingerprints[profile.Id] = ComputeFingerprint(profile);
            dirtySceneIds.Remove(profile.Id);
        }

        private string ComputeActiveFingerprint() => ComputeFingerprint(ActiveProfile);

        private string ComputeFingerprint(SarSceneProfile profile)
        {
            if (manager == null || profile == null) return string.Empty;
            string network = JsonConvert.SerializeObject(manager.networkManager?.AxisNetwork,
                Formatting.None, new Vector3Converter());
            string facilities = JsonConvert.SerializeObject(manager.vertiportManager?.Vertiports,
                Formatting.None, new Vector3Converter());
            string transform = JsonConvert.SerializeObject(new
            {
                profile.CityPosition,
                profile.CityEulerAngles
            }, Formatting.None, new Vector3Converter());
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(network + "\n" + facilities + "\n" + transform));
                return Convert.ToBase64String(bytes);
            }
        }

        private string SceneFolder(SarSceneProfile profile) => Path.Combine(WorkspaceRoot, profile.Id);

        private string UniqueName(string candidate)
        {
            string value = candidate;
            int suffix = 2;
            while (registry.Scenes.Any(scene => string.Equals(scene.DisplayName, value, StringComparison.OrdinalIgnoreCase)))
                value = candidate + " " + suffix++;
            return value;
        }

        private static void CopyIfPresent(string source, string destination)
        {
            if (File.Exists(source)) File.Copy(source, destination, true);
        }

        private static void Touch(SarSceneProfile profile) => profile.ModifiedUtc = DateTime.UtcNow.ToString("o");

        private void SaveRegistry()
        {
            Directory.CreateDirectory(WorkspaceRoot);
            string path = Path.Combine(WorkspaceRoot, RegistryFileName);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(registry, Formatting.Indented,
                new Vector3Converter()));
            File.Copy(temporary, path, true);
            File.Delete(temporary);
        }

#if UNITY_EDITOR
        private static bool TryConvertSketchUp(string sourcePath, string outputPath, out string error)
        {
            error = null;
            Type converter = Type.GetType("RoadNetworkEditor.EditorTools.SarCadmapperSketchUpConverter, Assembly-CSharp-Editor");
            var method = converter?.GetMethod("TryConvert", System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static);
            if (method == null)
            {
                error = "The CADMapper SketchUp converter was not found.";
                return false;
            }
            object[] arguments = { sourcePath, outputPath, null, 0 };
            bool success = (bool)method.Invoke(null, arguments);
            error = arguments[2] as string;
            return success;
        }
#endif
    }
}
