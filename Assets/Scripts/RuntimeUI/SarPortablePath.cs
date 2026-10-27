using System;
using System.IO;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Converts application-owned absolute paths into portable URI-like paths.
    /// The tokens are resolved against the current installation/user directory,
    /// so scene registries remain valid after copying the program to another PC.
    /// </summary>
    public static class SarPortablePath
    {
        public const string ApplicationPrefix = "app://";
        public const string UserPrefix = "user://";
        public const string StreamingPrefix = "streaming://";
        public const string ScenePrefix = "scene://";

        public static string ApplicationRoot
        {
            get
            {
                string root = Application.isEditor
                    ? Path.Combine(Application.dataPath, "..")
                    : Path.Combine(Application.dataPath, "..");
                return NormalizeFullPath(root);
            }
        }

        public static string Resolve(string path, string sceneFolder = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string value = path.Trim().Replace('\\', '/');
            if (value.StartsWith(ScenePrefix, StringComparison.OrdinalIgnoreCase))
                return Combine(sceneFolder, value.Substring(ScenePrefix.Length));
            if (value.StartsWith(StreamingPrefix, StringComparison.OrdinalIgnoreCase))
                return Combine(Application.streamingAssetsPath, value.Substring(StreamingPrefix.Length));
            if (value.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase))
                return Combine(SarRuntimePaths.DataRoot, value.Substring(UserPrefix.Length));
            if (value.StartsWith(ApplicationPrefix, StringComparison.OrdinalIgnoreCase))
                return Combine(ApplicationRoot, value.Substring(ApplicationPrefix.Length));
            if (Path.IsPathRooted(path)) return NormalizeFullPath(path);
            return Combine(string.IsNullOrWhiteSpace(sceneFolder) ? ApplicationRoot : sceneFolder, path);
        }

        public static string ToPortable(string path, string sceneFolder = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (HasKnownPrefix(path)) return path.Replace('\\', '/');
            string full = Resolve(path, sceneFolder);
            if (!string.IsNullOrWhiteSpace(sceneFolder) && TryRelative(full, sceneFolder, out string sceneRelative))
                return ScenePrefix + sceneRelative;
            if (TryRelative(full, Application.streamingAssetsPath, out string streamingRelative))
                return StreamingPrefix + streamingRelative;
            if (TryRelative(full, SarRuntimePaths.DataRoot, out string userRelative))
                return UserPrefix + userRelative;
            if (TryRelative(full, ApplicationRoot, out string applicationRelative))
                return ApplicationPrefix + applicationRelative;
            return full.Replace('\\', '/');
        }

        public static bool HasKnownPrefix(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.StartsWith(ApplicationPrefix, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(StreamingPrefix, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(ScenePrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string Combine(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(root)) return string.Empty;
            string clean = (relative ?? string.Empty).TrimStart('/', '\\')
                .Replace('/', Path.DirectorySeparatorChar);
            return NormalizeFullPath(Path.Combine(root, clean));
        }

        private static bool TryRelative(string fullPath, string root, out string relative)
        {
            relative = null;
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(root)) return false;
            string full = NormalizeFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string basePath = NormalizeFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.Equals(basePath, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            relative = full.Length == basePath.Length
                ? string.Empty
                : full.Substring(basePath.Length + 1).Replace('\\', '/');
            return true;
        }

        private static string NormalizeFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path ?? string.Empty; }
        }
    }
}
