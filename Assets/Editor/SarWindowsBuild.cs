using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RoadNetworkEditor.EditorTools
{
    /// <summary>
    /// One reproducible Windows build path for the research demonstrator.
    /// Validation is deliberately read-only; packaged-data synchronization is
    /// a separate command so every data change remains visible to Git.
    /// </summary>
    public static class SarWindowsBuild
    {
        public const string ExecutableName = "Structured-Air-Route-Simulator.exe";
        public const string EnglishLocale = "en-US";
        public const string ChineseLocale = "zh-CN";
        public const string LocaleFileName = "SAR.locale.json";
        private const string ThirdPartyNoticesFile = "THIRD_PARTY_NOTICES.md";
        private const string ChineseReadmeFile = "README.zh-CN.md";
        private const string ExternalAssetsFile = "EXTERNAL_ASSETS.md";
        private const string LocalizationFolderName = "SARLocalization";
        private const string ChineseCatalogFileName = "zh-CN.json";

        private static readonly string[] RequiredPackagedFiles =
        {
            "Missions.json",
            "RouteFile.example.json",
            "RouteFile.example.xml"
        };

        private static readonly string[] ReleaseDocumentFiles =
        {
            "RouteFileFormat.md",
            "TrafficControl.md",
            "WorkbenchAndBuild.md"
        };

        private static readonly string[] ForbiddenReleaseEntries =
        {
            "Assets",
            "ImportCache",
            "Network",
            "Output",
            "SARData",
            "SARScenes",
            "Settings",
            ".legacy-local-low-migrated-v1",
            "cone-inward-exe.png"
        };

        public static string BuildDirectory => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Builds", "Windows"));

        public static string ReleaseRootDirectory => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Builds", "Releases"));

        public static string EnglishReleaseDirectory => Path.Combine(
            ReleaseRootDirectory, EnglishLocale);

        public static string ChineseReleaseDirectory => Path.Combine(
            ReleaseRootDirectory, ChineseLocale);

        [MenuItem("Tools/SAR/Validate Build")]
        public static void ValidateFromMenu()
        {
            List<string> issues = CollectValidationIssues();
            if (issues.Count == 0)
            {
                Debug.Log("[SAR Build] Validation passed.");
                EditorUtility.DisplayDialog("SAR Build", "Validation passed.", "OK");
                return;
            }

            string message = string.Join("\n", issues.Select(issue => "• " + issue));
            Debug.LogError("[SAR Build] Validation failed:\n" + message);
            EditorUtility.DisplayDialog("SAR Build Validation", message, "OK");
        }

        [MenuItem("Tools/SAR/Sync Packaged Route Data")]
        public static void SyncPackagedRouteData()
        {
            string sourceDirectory = Path.Combine(Application.dataPath, "DataBase");
            string targetDirectory = Path.Combine(Application.streamingAssetsPath, "SARData");
            Directory.CreateDirectory(targetDirectory);

            foreach (string fileName in RequiredPackagedFiles)
            {
                string source = Path.Combine(sourceDirectory, fileName);
                if (!File.Exists(source))
                    throw new FileNotFoundException("Source route data is missing.", source);
                File.Copy(source, Path.Combine(targetDirectory, fileName), true);
            }

            AssetDatabase.Refresh();
            Debug.Log("[SAR Build] Packaged route data synchronized. Review and commit the visible asset changes.");
        }

        [MenuItem("Tools/SAR/Build Windows x64")]
        public static void BuildWindows64()
        {
            BuildWindows64Core(true);
        }

        /// <summary>
        /// Non-interactive entry point for -batchmode -executeMethod. Validation failures
        /// throw instead of being hidden behind an editor dialog, so CI receives a non-zero exit.
        /// </summary>
        public static void BuildWindows64BatchMode()
        {
            BuildWindows64Core(false);
        }

        private static void BuildWindows64Core(bool interactive)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (interactive)
                {
                    EditorUtility.DisplayDialog("SAR Build", "Exit Play Mode before building.", "OK");
                    return;
                }
                throw new BuildFailedException("Exit Play Mode before building.");
            }

            List<string> issues = CollectValidationIssues();
            if (issues.Count > 0)
            {
                string message = string.Join("\n", issues.Select(issue => "• " + issue));
                if (interactive)
                {
                    EditorUtility.DisplayDialog("SAR Build Validation", message, "OK");
                    return;
                }
                throw new BuildFailedException("SAR build validation failed:\n" + message);
            }

            if (interactive && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            // The research workbench is responsive. Keep the native Windows frame
            // resizable so the maximize button and arbitrary 16:9 window sizes work.
            PlayerSettings.resizableWindow = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PrepareCleanBuildDirectory();
            string[] scenes = EnabledScenePaths();
            string executablePath = Path.Combine(BuildDirectory, ExecutableName);
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = executablePath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.CompressWithLz4HC
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
                throw new BuildFailedException(
                    $"SAR Windows build failed: {summary.result}, {summary.totalErrors} error(s).");

            CopyReleaseDocumentation(BuildDirectory);
            ValidateReleaseOutput(BuildDirectory, null);
            PublishBilingualReleases();

            Debug.Log($"[SAR Build] Windows x64 build completed: {executablePath} " +
                      $"({summary.totalSize / (1024f * 1024f):F1} MB, {summary.totalTime}). " +
                      $"English and Chinese releases: {ReleaseRootDirectory}");
            if (interactive) EditorUtility.RevealInFinder(ReleaseRootDirectory);
        }

        [MenuItem("Tools/SAR/Publish Bilingual Releases from Windows Build")]
        public static void PublishBilingualReleasesFromExistingBuild()
        {
            ValidateReleaseOutput(BuildDirectory, null);
            PublishBilingualReleases();
            Debug.Log($"[SAR Build] Bilingual releases assembled from the validated Windows build: " +
                      ReleaseRootDirectory);
            EditorUtility.RevealInFinder(ReleaseRootDirectory);
        }

        [MenuItem("Tools/SAR/Open Build Folder")]
        public static void OpenBuildFolder()
        {
            Directory.CreateDirectory(ReleaseRootDirectory);
            EditorUtility.RevealInFinder(ReleaseRootDirectory);
        }

        public static List<string> CollectValidationIssues()
        {
            var issues = new List<string>();
            string[] scenes = EnabledScenePaths();
            if (scenes.Length == 0)
                issues.Add("No enabled scene is configured in Build Settings.");
            foreach (string scene in scenes)
            {
                if (!File.Exists(Path.GetFullPath(Path.Combine(Application.dataPath, "..", scene))))
                    issues.Add($"Build scene does not exist: {scene}");
            }

            string databaseDirectory = Path.Combine(Application.dataPath, "DataBase");
            string packagedDirectory = Path.Combine(Application.streamingAssetsPath, "SARData");
            foreach (string fileName in RequiredPackagedFiles)
            {
                string source = Path.Combine(databaseDirectory, fileName);
                string packaged = Path.Combine(packagedDirectory, fileName);
                bool sourceExists = File.Exists(source);
                bool packagedExists = File.Exists(packaged);
                if (!sourceExists)
                    issues.Add($"Source data is missing: Assets/DataBase/{fileName}");
                if (!packagedExists)
                    issues.Add($"Packaged data is missing: Assets/StreamingAssets/SARData/{fileName}");
                if (sourceExists && packagedExists &&
                    !File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(packaged)))
                    issues.Add($"Packaged data is stale: {fileName}. Run Tools > SAR > Sync Packaged Route Data.");
            }

            ValidateDefaultResearchSceneData(databaseDirectory, issues);

            ValidateLocalizationCatalog(
                Path.Combine(Application.streamingAssetsPath, LocalizationFolderName), issues);
            ValidatePlotWorker(Path.Combine(Application.streamingAssetsPath, "SARAnalytics", "Worker"), issues);

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string thirdPartyNotices = Path.Combine(projectRoot, ThirdPartyNoticesFile);
            if (!File.Exists(thirdPartyNotices) || new FileInfo(thirdPartyNotices).Length == 0)
                issues.Add($"Release notice is missing or empty: {ThirdPartyNoticesFile}");

            return issues;
        }

        /// <summary>
        /// The bundled demonstrator is expected to ship with the authored two-layer
        /// research scene.  A scene/profile import must not silently reduce it to a
        /// single layer or strip either class of connector, because aircraft then have
        /// no facility entry/exit edges and every mission remains in route retry.
        /// </summary>
        private static void ValidateDefaultResearchSceneData(string databaseDirectory,
            List<string> issues)
        {
            // AxisNetwork.json is the reproducible single-layer skeleton-generator
            // output, not the authored default scene profile. Validate the generated
            // two-layer runtime network and the live build scene below instead.
            ValidateNetworkFile(Path.Combine(databaseDirectory, "FullNetwork.json"),
                "generated full network", issues);

            string vertiportPath = Path.Combine(databaseDirectory, "VertiportData.json");
            if (!File.Exists(vertiportPath))
            {
                issues.Add("Default facility data is missing: Assets/DataBase/VertiportData.json");
            }
            else
            {
                try
                {
                    JArray vertiports = JArray.Parse(File.ReadAllText(vertiportPath));
                    int groundCorridors = vertiports
                        .OfType<JObject>()
                        .Sum(item => (item["Corridors"] as JArray)?.Count ?? 0);
                    if (groundCorridors == 0)
                        issues.Add("Default facility data contains no ground-network corridors; " +
                                   "aircraft cannot enter or leave the air network.");
                }
                catch (Exception ex)
                {
                    issues.Add("Default facility data could not be validated: " + ex.Message);
                }
            }

            RoadNetworkEditor.NetworkManager networkManager =
                UnityEngine.Object.FindObjectOfType<RoadNetworkEditor.NetworkManager>(true);
            RoadNetworkEditor.VertiportManager vertiportManager =
                UnityEngine.Object.FindObjectOfType<RoadNetworkEditor.VertiportManager>(true);
            if (networkManager == null)
            {
                issues.Add("The active build scene has no NetworkManager.");
            }
            else
            {
                if ((networkManager.AxisNetwork?.AltitudeLayers?.Count ?? 0) < 2)
                    issues.Add("The active build scene lost the authored second network layer.");
                if ((networkManager.AxisNetwork?.LaneConnectionCorridors?.Count ?? 0) == 0)
                    issues.Add("The active build scene contains no interlayer corridor.");
            }
            if (vertiportManager == null)
            {
                issues.Add("The active build scene has no VertiportManager.");
            }
            else
            {
                int sceneCorridors = (vertiportManager.Vertiports ??
                                      new List<global::Vertiport>())
                    .Where(item => item != null)
                    .Sum(item => item.Corridors?.Count ?? 0);
                if (sceneCorridors == 0)
                    issues.Add("The active build scene contains no ground-network corridor; " +
                               "aircraft cannot enter or leave the air network.");
            }
        }

        private static void ValidateNetworkFile(string path, string label, List<string> issues)
        {
            if (!File.Exists(path))
            {
                issues.Add($"Default {label} is missing: {path}");
                return;
            }
            try
            {
                JObject network = JObject.Parse(File.ReadAllText(path));
                int layerCount = (network["AltitudeLayers"] as JArray)?.Count ?? 0;
                int connectorCount = (network["LaneConnectionCorridors"] as JArray)?.Count ?? 0;
                if (layerCount < 2)
                    issues.Add($"The default {label} contains {layerCount} altitude layer(s); " +
                               "the authored research scene requires two.");
                if (connectorCount == 0)
                    issues.Add($"The default {label} contains no interlayer corridor.");
            }
            catch (Exception ex)
            {
                issues.Add($"The default {label} could not be validated: {ex.Message}");
            }
        }

        private static void ValidateLocalizationCatalog(string localizationRoot, List<string> issues)
        {
            string catalogPath = Path.Combine(localizationRoot, ChineseCatalogFileName);
            if (!File.Exists(catalogPath))
            {
                issues.Add($"Packaged runtime localization catalog is missing: " +
                           $"{LocalizationFolderName}/{ChineseCatalogFileName}");
                return;
            }

            try
            {
                JObject catalog = JObject.Parse(File.ReadAllText(catalogPath));
                if (!string.Equals((string)catalog["locale"], ChineseLocale,
                        StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"Runtime localization catalog locale must be '{ChineseLocale}'.");
                    return;
                }

                JObject exact = catalog["exact"] as JObject;
                JObject phrases = catalog["phrases"] as JObject;
                if (exact == null || !exact.Properties().Any())
                    issues.Add("Runtime localization catalog has no exact UI translations.");
                if (phrases == null || !phrases.Properties().Any())
                    issues.Add("Runtime localization catalog has no dynamic phrase translations.");
            }
            catch (Exception ex)
            {
                issues.Add("Runtime localization catalog could not be validated: " + ex.Message);
            }
        }

        private static void ValidatePlotWorker(string workerRoot, List<string> issues)
        {
            string manifestPath = Path.Combine(workerRoot, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                issues.Add("Packaged Python plotting worker manifest is missing: SARAnalytics/Worker/manifest.json");
                return;
            }
            try
            {
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                if ((int?)manifest["SchemaVersion"] != 1)
                {
                    issues.Add("Packaged Python plotting worker uses an unsupported manifest schema.");
                    return;
                }
                string archiveName = (string)manifest["ArchiveFile"];
                string expectedHash = ((string)manifest["Sha256"] ?? string.Empty).Trim().ToLowerInvariant();
                string expectedSourceHash = ((string)manifest["SourceSha256"] ?? string.Empty)
                    .Trim().ToLowerInvariant();
                string expectedTrafficSourceHash = ((string)manifest["TrafficHeatmapSourceSha256"] ?? string.Empty)
                    .Trim().ToLowerInvariant();
                string executableRelativePath = (string)manifest["ExecutableRelativePath"];
                string componentsRelativePath = (string)manifest["ThirdPartyComponentsRelativePath"];
                string expectedComponentsHash = ((string)manifest["ThirdPartyComponentsSha256"] ?? string.Empty)
                    .Trim().ToLowerInvariant();
                string expectedRuntimeRequirementsHash = ((string)manifest["RuntimeRequirementsSha256"] ?? string.Empty)
                    .Trim().ToLowerInvariant();
                string expectedBuildRequirementsHash = ((string)manifest["BuildRequirementsSha256"] ?? string.Empty)
                    .Trim().ToLowerInvariant();
                string pythonVersion = ((string)manifest["PythonVersion"] ?? string.Empty).Trim();
                string pyInstallerVersion = ((string)manifest["PyInstallerVersion"] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(archiveName) || string.IsNullOrWhiteSpace(expectedHash) ||
                    string.IsNullOrWhiteSpace(expectedSourceHash) ||
                    string.IsNullOrWhiteSpace(expectedTrafficSourceHash) ||
                    string.IsNullOrWhiteSpace(executableRelativePath) ||
                    string.IsNullOrWhiteSpace(componentsRelativePath) ||
                    string.IsNullOrWhiteSpace(expectedComponentsHash) ||
                    string.IsNullOrWhiteSpace(expectedRuntimeRequirementsHash) ||
                    string.IsNullOrWhiteSpace(expectedBuildRequirementsHash) ||
                    string.IsNullOrWhiteSpace(pythonVersion) ||
                    string.IsNullOrWhiteSpace(pyInstallerVersion))
                {
                    issues.Add("Packaged Python plotting worker manifest is incomplete.");
                    return;
                }
                string root = Path.GetFullPath(workerRoot).TrimEnd(Path.DirectorySeparatorChar,
                                  Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string archivePath = Path.GetFullPath(Path.Combine(root, archiveName));
                if (!archivePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(archivePath))
                {
                    issues.Add($"Packaged Python plotting worker archive is missing or unsafe: {archiveName}");
                    return;
                }
                string actualHash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(archivePath))
                    actualHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                {
                    issues.Add("Packaged Python plotting worker archive failed SHA-256 validation.");
                    return;
                }

                string sourcePath = Path.Combine(Path.GetDirectoryName(workerRoot),
                    "SensitivityAnalysis.py");
                if (!File.Exists(sourcePath))
                {
                    issues.Add("Packaged Python plotting source is missing: SARAnalytics/SensitivityAnalysis.py");
                    return;
                }
                string actualSourceHash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(sourcePath))
                    actualSourceHash = BitConverter.ToString(sha.ComputeHash(stream))
                        .Replace("-", "").ToLowerInvariant();
                if (!string.Equals(expectedSourceHash, actualSourceHash, StringComparison.Ordinal))
                {
                    issues.Add("SensitivityAnalysis.py differs from the packaged worker. Rebuild Tools/SARPlotWorker/build_worker.ps1.");
                    return;
                }

                string trafficSourcePath = Path.Combine(Path.GetDirectoryName(workerRoot),
                    "TrafficHeatmapAnalysis.py");
                if (!File.Exists(trafficSourcePath))
                {
                    issues.Add("Packaged Python plotting source is missing: SARAnalytics/TrafficHeatmapAnalysis.py");
                    return;
                }
                string actualTrafficSourceHash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(trafficSourcePath))
                    actualTrafficSourceHash = BitConverter.ToString(sha.ComputeHash(stream))
                        .Replace("-", "").ToLowerInvariant();
                if (!string.Equals(expectedTrafficSourceHash, actualTrafficSourceHash, StringComparison.Ordinal))
                {
                    issues.Add("TrafficHeatmapAnalysis.py differs from the packaged worker. Rebuild Tools/SARPlotWorker/build_worker.ps1.");
                    return;
                }

                string analyticsRoot = Path.GetDirectoryName(workerRoot);
                foreach (var requirement in new[]
                         {
                             new { FileName = "requirements.txt", ExpectedHash = expectedRuntimeRequirementsHash },
                             new { FileName = "build-requirements.txt", ExpectedHash = expectedBuildRequirementsHash }
                         })
                {
                    string requirementPath = Path.Combine(analyticsRoot, requirement.FileName);
                    if (!File.Exists(requirementPath))
                    {
                        issues.Add($"Packaged Python plotting dependency lock is missing: {requirement.FileName}");
                        return;
                    }
                    string actualRequirementHash;
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream stream = File.OpenRead(requirementPath))
                        actualRequirementHash = BitConverter.ToString(sha.ComputeHash(stream))
                            .Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(requirement.ExpectedHash, actualRequirementHash, StringComparison.Ordinal))
                    {
                        issues.Add($"Packaged Python plotting dependency lock is stale: {requirement.FileName}");
                        return;
                    }
                }

                string normalizedComponents = componentsRelativePath.Replace('\\', '/').TrimStart('/');
                if (Path.IsPathRooted(componentsRelativePath) || normalizedComponents.Contains("../"))
                {
                    issues.Add("Packaged Python plotting worker third-party inventory path is unsafe.");
                    return;
                }
                string componentsPath = Path.GetFullPath(Path.Combine(root,
                    normalizedComponents.Replace('/', Path.DirectorySeparatorChar)));
                if (!componentsPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(componentsPath))
                {
                    issues.Add("Packaged Python plotting worker third-party inventory is missing.");
                    return;
                }
                string actualComponentsHash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(componentsPath))
                    actualComponentsHash = BitConverter.ToString(sha.ComputeHash(stream))
                        .Replace("-", "").ToLowerInvariant();
                if (!string.Equals(expectedComponentsHash, actualComponentsHash, StringComparison.Ordinal))
                {
                    issues.Add("Packaged Python plotting worker third-party inventory failed SHA-256 validation.");
                    return;
                }
                string licenseReadme = Path.Combine(Path.GetDirectoryName(componentsPath), "README.md");
                if (!File.Exists(licenseReadme))
                {
                    issues.Add("Packaged Python plotting worker third-party license README is missing.");
                    return;
                }
                JObject componentInventory = JObject.Parse(File.ReadAllText(componentsPath));
                if ((int?)componentInventory["schema_version"] != 1)
                {
                    issues.Add("Packaged Python plotting worker license inventory uses an unsupported schema.");
                    return;
                }
                JArray components = componentInventory["components"] as JArray;
                if (components == null || components.Count == 0)
                {
                    issues.Add("Packaged Python plotting worker license inventory has no components.");
                    return;
                }
                string licensesRoot = Path.GetDirectoryName(componentsPath);
                string guardedLicensesRoot = Path.GetFullPath(licensesRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                foreach (JToken component in components)
                {
                    string componentName = ((string)component["name"] ?? "unnamed component").Trim();
                    JArray licenseFiles = component["license_files"] as JArray;
                    if (licenseFiles == null || licenseFiles.Count == 0)
                    {
                        issues.Add($"Packaged Python plotting worker license inventory has no files for {componentName}.");
                        return;
                    }
                    foreach (JToken licenseFile in licenseFiles)
                    {
                        string relativePath = ((string)licenseFile["path"] ?? string.Empty).Trim();
                        string expectedLicenseHash = ((string)licenseFile["sha256"] ?? string.Empty)
                            .Trim().ToLowerInvariant();
                        string normalizedLicensePath = relativePath.Replace('\\', '/').TrimStart('/');
                        if (string.IsNullOrWhiteSpace(normalizedLicensePath) ||
                            string.IsNullOrWhiteSpace(expectedLicenseHash) ||
                            Path.IsPathRooted(relativePath) || normalizedLicensePath.Contains("../"))
                        {
                            issues.Add($"Packaged Python plotting worker has an unsafe license path for {componentName}.");
                            return;
                        }
                        string licensePath = Path.GetFullPath(Path.Combine(guardedLicensesRoot,
                            normalizedLicensePath.Replace('/', Path.DirectorySeparatorChar)));
                        if (!licensePath.StartsWith(guardedLicensesRoot, StringComparison.OrdinalIgnoreCase) ||
                            !File.Exists(licensePath))
                        {
                            issues.Add($"Packaged Python plotting worker license file is missing for {componentName}: {relativePath}");
                            return;
                        }
                        string actualLicenseHash;
                        using (SHA256 sha = SHA256.Create())
                        using (FileStream stream = File.OpenRead(licensePath))
                            actualLicenseHash = BitConverter.ToString(sha.ComputeHash(stream))
                                .Replace("-", "").ToLowerInvariant();
                        if (!string.Equals(expectedLicenseHash, actualLicenseHash, StringComparison.Ordinal))
                        {
                            issues.Add($"Packaged Python plotting worker license file failed SHA-256 validation: {relativePath}");
                            return;
                        }
                    }
                }
                string[] requiredComponents = { "python", "numpy", "pandas", "matplotlib", "pyinstaller" };
                foreach (string componentName in requiredComponents)
                {
                    bool present = components != null && components.Any(component =>
                        string.Equals((string)component["name"], componentName,
                            StringComparison.OrdinalIgnoreCase));
                    if (!present)
                    {
                        issues.Add($"Packaged Python plotting worker license inventory omits {componentName}.");
                        return;
                    }
                }

                string normalizedExecutable = executableRelativePath.Replace('\\', '/').TrimStart('/');
                if (Path.IsPathRooted(executableRelativePath) || normalizedExecutable.Contains("../"))
                {
                    issues.Add("Packaged Python plotting worker executable path is unsafe.");
                    return;
                }
                using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                {
                    bool containsExecutable = archive.Entries.Any(entry =>
                        string.Equals(entry.FullName.Replace('\\', '/'), normalizedExecutable,
                            StringComparison.OrdinalIgnoreCase));
                    if (!containsExecutable)
                        issues.Add("Packaged Python plotting worker archive does not contain its declared executable.");
                }
            }
            catch (Exception ex)
            {
                issues.Add("Packaged Python plotting worker could not be validated: " + ex.Message);
            }
        }

        private static void PrepareCleanBuildDirectory()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string buildRoot = Path.GetFullPath(Path.Combine(projectRoot, "Builds"));
            PrepareCleanDirectory(BuildDirectory, buildRoot);
        }

        private static void CopyReleaseDocumentation(string targetRoot)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (string fileName in new[] { "README.md", ChineseReadmeFile, ExternalAssetsFile, "LICENSE", ThirdPartyNoticesFile })
            {
                string source = Path.Combine(projectRoot, fileName);
                if (File.Exists(source)) File.Copy(source, Path.Combine(targetRoot, fileName), true);
            }

            string guideRoot = Path.Combine(projectRoot, "OperationGuide");
            string targetGuideRoot = Path.Combine(targetRoot, "OperationGuide");
            Directory.CreateDirectory(targetGuideRoot);
            foreach (string fileName in ReleaseDocumentFiles)
            {
                string source = Path.Combine(guideRoot, fileName);
                if (File.Exists(source)) File.Copy(source, Path.Combine(targetGuideRoot, fileName), true);
            }

            string quickStartRoot = Path.Combine(guideRoot, "QuickStart");
            string targetQuickStartRoot = Path.Combine(targetGuideRoot, "QuickStart");
            Directory.CreateDirectory(targetQuickStartRoot);
            string quickStartReadme = Path.Combine(quickStartRoot, "README.md");
            if (File.Exists(quickStartReadme))
                File.Copy(quickStartReadme, Path.Combine(targetQuickStartRoot, "README.md"), true);

            string sourceImages = Path.Combine(quickStartRoot, "Images", "annotated");
            string targetImages = Path.Combine(targetQuickStartRoot, "Images", "annotated");
            if (!Directory.Exists(sourceImages)) return;
            Directory.CreateDirectory(targetImages);
            foreach (string source in Directory.GetFiles(sourceImages, "*.png", SearchOption.TopDirectoryOnly))
                File.Copy(source, Path.Combine(targetImages, Path.GetFileName(source)), true);
        }

        private static void PublishBilingualReleases()
        {
            ValidateReleaseOutput(BuildDirectory, null);
            Directory.CreateDirectory(ReleaseRootDirectory);
            PublishLocalizedRelease(EnglishLocale, EnglishReleaseDirectory);
            PublishLocalizedRelease(ChineseLocale, ChineseReleaseDirectory);
            ValidateReleaseOutput(EnglishReleaseDirectory, EnglishLocale);
            ValidateReleaseOutput(ChineseReleaseDirectory, ChineseLocale);
            ValidateIdenticalPlayerBinaries(EnglishReleaseDirectory, ChineseReleaseDirectory);
        }

        private static void PublishLocalizedRelease(string locale, string targetRoot)
        {
            PrepareCleanDirectory(targetRoot, ReleaseRootDirectory);
            CopyDirectory(BuildDirectory, targetRoot);
            var selection = new JObject { ["locale"] = locale };
            File.WriteAllText(Path.Combine(targetRoot, LocaleFileName), selection.ToString(),
                new UTF8Encoding(false));
            // Both README.md and README.zh-CN.md are published in both archives. They
            // cross-link each other, so neither may be renamed or replaced here.
        }

        private static void PrepareCleanDirectory(string targetDirectory, string guardedParentDirectory)
        {
            string target = Path.GetFullPath(targetDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetFullPath(guardedParentDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string guardedRoot = parent + Path.DirectorySeparatorChar;
            if (string.Equals(target, parent, StringComparison.OrdinalIgnoreCase) ||
                !target.StartsWith(guardedRoot, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException($"Refusing to clean an unexpected build directory: {target}");

            // Recreating only an explicitly guarded child prevents stale runtime data,
            // personal scenes, or history from leaking into a distributable package.
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
        }

        private static void CopyDirectory(string sourceRoot, string targetRoot)
        {
            string source = Path.GetFullPath(sourceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(targetRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(source))
                throw new BuildFailedException($"Validated Windows build is missing: {source}");
            if (target.StartsWith(source + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("A localized release cannot be nested inside its source build.");

            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new BuildFailedException($"Refusing to copy a linked build directory: {directory}");
                string relative = directory.Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(target, relative));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new BuildFailedException($"Refusing to copy a linked build file: {file}");
                string relative = file.Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }

        private static void ValidateReleaseOutput(string releaseRoot, string expectedLocale)
        {
            string root = Path.GetFullPath(releaseRoot);
            string executable = Path.Combine(root, ExecutableName);
            if (!File.Exists(executable))
                throw new BuildFailedException($"Release executable is missing: {executable}");

            foreach (string entryName in ForbiddenReleaseEntries)
            {
                string path = Path.Combine(root, entryName);
                if (Directory.Exists(path) || File.Exists(path))
                    throw new BuildFailedException($"Generated runtime data leaked into the release: {entryName}");
            }

            string databaseDirectory = Path.Combine(Application.dataPath, "DataBase");
            string builtDataDirectory = Path.Combine(
                root,
                Path.GetFileNameWithoutExtension(ExecutableName) + "_Data",
                "StreamingAssets",
                "SARData");
            foreach (string fileName in RequiredPackagedFiles)
            {
                string source = Path.Combine(databaseDirectory, fileName);
                string packaged = Path.Combine(builtDataDirectory, fileName);
                if (!File.Exists(packaged))
                    throw new BuildFailedException($"Release data is missing: {fileName}");
                if (!File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(packaged)))
                    throw new BuildFailedException($"Release data is stale: {fileName}");
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string sourceNotices = Path.Combine(projectRoot, ThirdPartyNoticesFile);
            string releaseNotices = Path.Combine(root, ThirdPartyNoticesFile);
            if (!File.Exists(sourceNotices))
                throw new BuildFailedException($"Source release notice is missing: {ThirdPartyNoticesFile}");
            if (!File.Exists(releaseNotices))
                throw new BuildFailedException($"Release notice is missing: {ThirdPartyNoticesFile}");
            if (!File.ReadAllBytes(sourceNotices).SequenceEqual(File.ReadAllBytes(releaseNotices)))
                throw new BuildFailedException($"Release notice is stale: {ThirdPartyNoticesFile}");
            var workerIssues = new List<string>();
            ValidatePlotWorker(Path.Combine(
                root,
                Path.GetFileNameWithoutExtension(ExecutableName) + "_Data",
                "StreamingAssets", "SARAnalytics", "Worker"), workerIssues);
            if (workerIssues.Count > 0)
                throw new BuildFailedException("Release plotting worker validation failed:\n" +
                                               string.Join("\n", workerIssues));
            string sourceWorkerManifest = Path.Combine(Application.streamingAssetsPath,
                "SARAnalytics", "Worker", "manifest.json");
            string releaseWorkerManifest = Path.Combine(root,
                Path.GetFileNameWithoutExtension(ExecutableName) + "_Data",
                "StreamingAssets", "SARAnalytics", "Worker", "manifest.json");
            if (!File.Exists(sourceWorkerManifest) ||
                !File.ReadAllBytes(sourceWorkerManifest).SequenceEqual(
                    File.ReadAllBytes(releaseWorkerManifest)))
                throw new BuildFailedException("Release plotting worker manifest is stale.");

            string builtLocalizationRoot = Path.Combine(root,
                Path.GetFileNameWithoutExtension(ExecutableName) + "_Data",
                "StreamingAssets", LocalizationFolderName);
            var localizationIssues = new List<string>();
            ValidateLocalizationCatalog(builtLocalizationRoot, localizationIssues);
            if (localizationIssues.Count > 0)
                throw new BuildFailedException("Release localization validation failed:\n" +
                                               string.Join("\n", localizationIssues));
            string sourceCatalog = Path.Combine(Application.streamingAssetsPath,
                LocalizationFolderName, ChineseCatalogFileName);
            string releaseCatalog = Path.Combine(builtLocalizationRoot, ChineseCatalogFileName);
            if (!File.Exists(sourceCatalog) ||
                !File.ReadAllBytes(sourceCatalog).SequenceEqual(File.ReadAllBytes(releaseCatalog)))
                throw new BuildFailedException("Release localization catalog is stale.");

            foreach (string fileName in new[] { "README.md", ChineseReadmeFile, ExternalAssetsFile, "LICENSE", ThirdPartyNoticesFile })
                if (!File.Exists(Path.Combine(root, fileName)))
                    throw new BuildFailedException($"Release document is missing: {fileName}");
            foreach (string fileName in ReleaseDocumentFiles)
                if (!File.Exists(Path.Combine(root, "OperationGuide", fileName)))
                    throw new BuildFailedException($"Release operation guide is missing: {fileName}");
            if (!File.Exists(Path.Combine(root, "OperationGuide", "QuickStart", "README.md")))
                throw new BuildFailedException("Release QuickStart guide is missing.");

            if (string.IsNullOrWhiteSpace(expectedLocale)) return;
            string localePath = Path.Combine(root, LocaleFileName);
            if (!File.Exists(localePath))
                throw new BuildFailedException($"Release locale selector is missing: {LocaleFileName}");
            try
            {
                JObject selection = JObject.Parse(File.ReadAllText(localePath));
                if (!string.Equals((string)selection["locale"], expectedLocale,
                        StringComparison.OrdinalIgnoreCase))
                    throw new BuildFailedException(
                        $"Release locale must be '{expectedLocale}': {localePath}");
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BuildFailedException($"Release locale selector is invalid: {ex.Message}");
            }
        }

        private static void ValidateIdenticalPlayerBinaries(string englishRoot, string chineseRoot)
        {
            string dataFolder = Path.GetFileNameWithoutExtension(ExecutableName) + "_Data";
            string[] candidates =
            {
                ExecutableName,
                "UnityPlayer.dll",
                Path.Combine(dataFolder, "Managed", "Assembly-CSharp.dll")
            };
            foreach (string relativePath in candidates)
            {
                string englishPath = Path.Combine(englishRoot, relativePath);
                string chinesePath = Path.Combine(chineseRoot, relativePath);
                if (!File.Exists(englishPath) && !File.Exists(chinesePath)) continue;
                if (!File.Exists(englishPath) || !File.Exists(chinesePath))
                    throw new BuildFailedException($"Localized releases differ: {relativePath}");
                if (!string.Equals(ComputeSha256(englishPath), ComputeSha256(chinesePath),
                        StringComparison.Ordinal))
                    throw new BuildFailedException(
                        $"Localized releases do not use the same player binary: {relativePath}");
            }
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        private static string[] EnabledScenePaths()
        {
            return EditorBuildSettings.scenes
                .Where(scene => scene.enabled && !string.IsNullOrWhiteSpace(scene.path))
                .Select(scene => scene.path)
                .ToArray();
        }
    }

    public sealed class SarBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            List<string> issues = SarWindowsBuild.CollectValidationIssues();
            if (issues.Count > 0)
                throw new BuildFailedException("SAR build validation failed:\n" + string.Join("\n", issues));
        }
    }
}
