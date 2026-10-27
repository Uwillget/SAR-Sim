using System;
using System.IO;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Central location for application-owned runtime data. A Windows player keeps
    /// every portable artifact beside the executable; the Editor retains its
    /// isolated LocalLow workspace so edit-mode tests cannot modify a distribution.
    /// </summary>
    public static class SarRuntimePaths
    {
        private const string MigrationMarkerName = ".legacy-local-low-migrated-v1";
        private static readonly object MigrationLock = new object();
        private static bool migrationChecked;

        public static string ApplicationRoot => Normalize(Path.Combine(Application.dataPath, ".."));

        public static string DataRoot => Application.isEditor
            ? Normalize(Application.persistentDataPath)
            : ApplicationRoot;

        public static string SceneRoot => Path.Combine(DataRoot, "SARScenes");
        public static string OutputRoot => Path.Combine(DataRoot, "Output");
        public static string NetworkRoot => Path.Combine(DataRoot, "Network");
        public static string SarDataRoot => Path.Combine(DataRoot, "SARData");
        public static string ImportCacheRoot => Path.Combine(DataRoot, "ImportCache");
        public static string ToolCacheRoot
        {
            get
            {
                // PyInstaller distributions contain deeply nested DLL names. Keep this
                // disposable cache under a deliberately short Windows path so first-run
                // extraction also works on systems without long-path support enabled.
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrWhiteSpace(local)
                    ? Path.Combine(DataRoot, "ToolCache")
                    : Normalize(Path.Combine(local, "SARSim", "ToolCache"));
            }
        }
        public static string SettingsRoot => Path.Combine(DataRoot, "Settings");
        public static string DisplaySettingsPath => Path.Combine(SettingsRoot, "display.json");

        /// <summary>
        /// Explicit compatibility operation for users who deliberately want to import
        /// data from a pre-portable LocalLow installation.  It is intentionally not a
        /// runtime initializer: a clean release must seed its own authored scene rather
        /// than silently inherit unrelated developer-machine scenes and history.
        /// Existing portable files always win and the legacy copy is never deleted.
        /// </summary>
        public static void EnsureLegacyDataMigrated()
        {
            if (Application.isEditor || migrationChecked) return;
            lock (MigrationLock)
            {
                if (migrationChecked) return;
                migrationChecked = true;
                string legacyRoot = Normalize(Application.persistentDataPath);
                string dataRoot = Normalize(DataRoot);
                if (string.Equals(legacyRoot, dataRoot, StringComparison.OrdinalIgnoreCase)) return;

                string marker = Path.Combine(dataRoot, MigrationMarkerName);
                if (File.Exists(marker)) return;
                try
                {
                    foreach (string folder in new[] { "SARScenes", "Output", "Network", "SARData", "ImportCache" })
                        CopyMissingDirectory(Path.Combine(legacyRoot, folder), Path.Combine(dataRoot, folder));
                    File.WriteAllText(marker,
                        "Legacy LocalLow application data was copied without deleting the source.\n" +
                        DateTime.UtcNow.ToString("o"));
                    Debug.Log($"[SarRuntimePaths] Portable data root: {dataRoot}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SarRuntimePaths] Legacy data migration was incomplete: {ex.Message}");
                }
            }
        }

        private static void CopyMissingDirectory(string source, string destination)
        {
            if (!Directory.Exists(source)) return;
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                string target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target, false);
            }
            foreach (string child in Directory.GetDirectories(source))
                CopyMissingDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path ?? string.Empty; }
        }
    }

    [Serializable]
    public sealed class SarDisplayPreferences
    {
        public bool Initialized;
        public bool FullScreen;
        public int WindowWidth;
        public int WindowHeight;
    }

    public static class SarDisplayPreferencesStore
    {
        public static SarDisplayPreferences Load()
        {
            try
            {
                if (File.Exists(SarRuntimePaths.DisplaySettingsPath))
                {
                    SarDisplayPreferences saved =
                        JsonUtility.FromJson<SarDisplayPreferences>(File.ReadAllText(SarRuntimePaths.DisplaySettingsPath));
                    if (saved != null) return saved;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarDisplayPreferences] Settings could not be read: {ex.Message}");
            }

            // One-time compatibility with releases that used the Windows registry.
            return new SarDisplayPreferences
            {
                Initialized = PlayerPrefs.HasKey("SAR.Display.FullScreen"),
                FullScreen = PlayerPrefs.GetInt("SAR.Display.FullScreen", 0) != 0,
                WindowWidth = PlayerPrefs.GetInt("SAR.Display.WindowWidth", 0),
                WindowHeight = PlayerPrefs.GetInt("SAR.Display.WindowHeight", 0)
            };
        }

        public static void Save(SarDisplayPreferences preferences)
        {
            if (preferences == null) return;
            try
            {
                Directory.CreateDirectory(SarRuntimePaths.SettingsRoot);
                File.WriteAllText(SarRuntimePaths.DisplaySettingsPath,
                    JsonUtility.ToJson(preferences, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarDisplayPreferences] Settings could not be saved: {ex.Message}");
            }
        }
    }
}
