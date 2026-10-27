using System;
using System.IO;
using RoadNetworkEditor.RuntimeUI;
using UnityEditor;
using UnityEngine;

namespace RoadNetworkEditor.EditorTools
{
    /// <summary>
    /// Converts Unity-imported SketchUp geometry into a standalone-readable SAR city
    /// package. SketchUpImporter is editor-only by design, so this is the supported
    /// bridge for CADMapper downloads.
    /// </summary>
    public static class SarCadmapperSketchUpConverter
    {
        private const string ImportedRoot = "Assets/ImportedCityModels";

        [MenuItem("Tools/SAR/CADMapper SketchUp Converter")]
        public static void ConvertFromMenu()
        {
            string input = EditorUtility.OpenFilePanel("Select CADMapper SketchUp Model", string.Empty, "skp");
            if (string.IsNullOrWhiteSpace(input)) return;
            string output = EditorUtility.SaveFilePanel("Save SAR City Model Package", string.Empty,
                Path.GetFileNameWithoutExtension(input), "sarcity");
            if (string.IsNullOrWhiteSpace(output)) return;
            if (TryConvert(input, output, out string error, out int meshes))
                EditorUtility.DisplayDialog("CADMapper Import Complete",
                    $"Converted {meshes} mesh(es):\n{output}", "OK");
            else
                EditorUtility.DisplayDialog("CADMapper Import Failed", error, "OK");
        }

        public static bool TryConvert(string sketchUpPath, string outputPath, out string error, out int meshCount)
        {
            error = null;
            meshCount = 0;
            if (string.IsNullOrWhiteSpace(sketchUpPath) || !File.Exists(sketchUpPath))
            {
                error = $"The SketchUp file does not exist: {sketchUpPath}";
                return false;
            }
            if (!string.Equals(Path.GetExtension(sketchUpPath), ".skp", StringComparison.OrdinalIgnoreCase))
            {
                error = "Select a SketchUp file in .skp format.";
                return false;
            }

            string folderName = Sanitize(Path.GetFileNameWithoutExtension(sketchUpPath)) + "_" +
                                unchecked((uint)sketchUpPath.GetHashCode()).ToString("X8");
            string assetFolder = ImportedRoot + "/" + folderName;
            string assetPath = assetFolder + "/" + Path.GetFileName(sketchUpPath);
            GameObject instance = null;
            try
            {
                EnsureAssetFolder(ImportedRoot);
                EnsureAssetFolder(assetFolder);
                File.Copy(sketchUpPath, Path.GetFullPath(assetPath), true);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport |
                                                     ImportAssetOptions.ForceUpdate);
                GameObject imported = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (imported == null)
                {
                    error = "Unity could not parse this SketchUp file. Export a Unity-compatible " +
                            "SketchUp version from CADMapper and try again.";
                    return false;
                }

                instance = PrefabUtility.InstantiatePrefab(imported) as GameObject;
                if (instance == null) instance = UnityEngine.Object.Instantiate(imported);
                instance.hideFlags = HideFlags.HideAndDontSave;
                meshCount = SarCityModelPackage.Export(instance.transform, outputPath);
                if (meshCount == 0)
                {
                    error = "The SketchUp file contains no convertible meshes.";
                    return false;
                }
                Debug.Log($"[CADMapper] Converted {sketchUpPath} to {outputPath} ({meshCount} mesh(es)).");
                return true;
            }
            catch (Exception ex)
            {
                error = $"SketchUp conversion failed: {ex.Message}";
                Debug.LogError($"[CADMapper] {error}\n{ex.StackTrace}");
                return false;
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void EnsureAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent)) EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent ?? "Assets", name);
        }

        private static string Sanitize(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "City" : value;
        }
    }
}
