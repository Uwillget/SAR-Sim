using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace RoadNetworkEditor.RuntimeUI
{
    /// <summary>
    /// Integration point for a licensed SketchUp C SDK converter. The converter is
    /// deliberately a fixed bundled executable; arbitrary programs supplied beside
    /// an imported model are never executed.
    /// </summary>
    public static class SarSketchUpRuntimeConverter
    {
        public static string ConverterPath => Path.Combine(Application.streamingAssetsPath,
            "SketchUpRuntime", "SarSkpConverter.exe");

        public static bool IsInstalled => File.Exists(ConverterPath);

        public static bool TryConvertToObj(string sketchUpPath, out string objPath, out string error)
        {
            objPath = null;
            error = null;
#if !UNITY_STANDALONE_WIN || UNITY_EDITOR
            error = "The runtime SketchUp converter is available only in the packaged Windows application.";
            return false;
#else
            if (!IsInstalled)
            {
                error = "This package does not include the official SketchUp C SDK converter. Import .obj or .sarcity directly; to read .skp files in the application, install a licensed SarSkpConverter component.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(sketchUpPath) || !File.Exists(sketchUpPath))
            {
                error = $"SketchUp file not found: {sketchUpPath}";
                return false;
            }

            string cache = SarRuntimePaths.ImportCacheRoot;
            Directory.CreateDirectory(cache);
            objPath = Path.Combine(cache, Path.GetFileNameWithoutExtension(sketchUpPath) + "-" +
                Guid.NewGuid().ToString("N") + ".obj");
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = ConverterPath,
                    Arguments = $"--input \"{sketchUpPath}\" --output \"{objPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(ConverterPath)
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException("The conversion process could not be started.");
                    if (!process.WaitForExit(10 * 60 * 1000))
                    {
                        process.Kill();
                        error = "SketchUp conversion exceeded 10 minutes and was terminated.";
                        return false;
                    }
                    if (process.ExitCode != 0 || !File.Exists(objPath))
                    {
                        error = $"SketchUp conversion failed (exit code {process.ExitCode}).";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"The SketchUp converter failed: {ex.Message}";
                return false;
            }
#endif
        }
    }
}
