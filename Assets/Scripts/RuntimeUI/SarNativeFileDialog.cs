using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace RoadNetworkEditor.RuntimeUI
{
    /// <summary>Small Windows file-picker bridge used by the standalone research application.</summary>
    public static class SarNativeFileDialog
    {
        // Prevent a second button dispatch while a native dialog is already open.
        private static int dialogOpen;

        public static bool IsDialogOpen => Volatile.Read(ref dialogOpen) != 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FilterSpec
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Name;
            [MarshalAs(UnmanagedType.LPWStr)] public string Pattern;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private const uint ForceFileSystem = 0x00000040;
        private const uint NoChangeDirectory = 0x00000008;
        private const uint PathMustExist = 0x00000800;
        private const uint FileMustExist = 0x00001000;
        private const uint OverwritePrompt = 0x00000002;
        private const uint FileSystemPath = 0x80058000;
        private const int CancelledHResult = unchecked((int)0x800704C7);

        [ComImport]
        [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint displayName, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem item, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr bindContext,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);
#endif

        /// <summary>
        /// Opens the modern Windows IFileDialog on a dedicated STA thread. The older
        /// OPENFILENAME structure is intentionally avoided: Mono's 64-bit marshaller
        /// can throw StackOverflowException before that legacy dialog is displayed.
        /// </summary>
        public static void OpenFileAsync(string title, string filter, string initialPath,
            Action<string> completed)
        {
#if UNITY_EDITOR
            completed?.Invoke(OpenFile(title, filter, initialPath));
#elif UNITY_STANDALONE_WIN
            SynchronizationContext unityContext = SynchronizationContext.Current;
            if (unityContext == null)
            {
                Debug.LogError("[SarNativeFileDialog] Unity main-thread context is unavailable.");
                completed?.Invoke(string.Empty);
                return;
            }
            if (!TryEnterDialog()) return;

            FilterSpec[] filters = ParseFilters(filter);
            string initialDirectory = InitialDirectory(initialPath);
            IntPtr owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            StartDialogThread(() => OpenFileWindowsCore(title, filters, initialDirectory, owner),
                unityContext, completed);
#else
            Debug.LogWarning("[SarNativeFileDialog] This platform does not provide a native file dialog.");
            completed?.Invoke(string.Empty);
#endif
        }

        public static void SaveFileAsync(string title, string filter, string initialPath,
            string defaultExtension, Action<string> completed)
        {
#if UNITY_EDITOR
            completed?.Invoke(SaveFile(title, filter, initialPath, defaultExtension));
#elif UNITY_STANDALONE_WIN
            SynchronizationContext unityContext = SynchronizationContext.Current;
            if (unityContext == null)
            {
                Debug.LogError("[SarNativeFileDialog] Unity main-thread context is unavailable.");
                completed?.Invoke(string.Empty);
                return;
            }
            if (!TryEnterDialog()) return;

            FilterSpec[] filters = ParseFilters(filter);
            string initialDirectory = InitialDirectory(initialPath);
            string initialName = string.IsNullOrWhiteSpace(initialPath)
                ? string.Empty : System.IO.Path.GetFileName(initialPath);
            string extension = string.IsNullOrWhiteSpace(defaultExtension)
                ? null : defaultExtension.TrimStart('.');
            IntPtr owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            StartDialogThread(
                () => SaveFileWindowsCore(title, filters, initialDirectory, initialName, extension, owner),
                unityContext, completed);
#else
            Debug.LogWarning("[SarNativeFileDialog] This platform does not provide a native file dialog.");
            completed?.Invoke(string.Empty);
#endif
        }

        public static string OpenFile(string title, string filter, string initialPath = null)
        {
#if UNITY_EDITOR
            string extension = FilterExtension(filter);
            return UnityEditor.EditorUtility.OpenFilePanel(title, InitialDirectory(initialPath), extension);
#elif UNITY_STANDALONE_WIN
            if (!TryEnterDialog()) return string.Empty;
            try
            {
                return OpenFileWindowsCore(title, ParseFilters(filter), InitialDirectory(initialPath),
                    System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
            }
            finally
            {
                ExitDialog();
            }
#else
            Debug.LogWarning("[SarNativeFileDialog] This platform does not provide a native file dialog.");
            return string.Empty;
#endif
        }

        public static string SaveFile(string title, string filter, string initialPath = null,
            string defaultExtension = null)
        {
            string initialDirectory = InitialDirectory(initialPath);
            string initialName = string.IsNullOrWhiteSpace(initialPath) ? string.Empty :
                System.IO.Path.GetFileName(initialPath);
#if UNITY_EDITOR
            string extension = string.IsNullOrWhiteSpace(defaultExtension)
                ? FilterExtension(filter).Split(',')[0]
                : defaultExtension.TrimStart('.');
            return UnityEditor.EditorUtility.SaveFilePanel(title, initialDirectory, initialName, extension);
#elif UNITY_STANDALONE_WIN
            if (!TryEnterDialog()) return string.Empty;
            try
            {
                return SaveFileWindowsCore(title, ParseFilters(filter), initialDirectory, initialName,
                    string.IsNullOrWhiteSpace(defaultExtension) ? null : defaultExtension.TrimStart('.'),
                    System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
            }
            finally
            {
                ExitDialog();
            }
#else
            Debug.LogWarning("[SarNativeFileDialog] This platform does not provide a native file dialog.");
            return string.Empty;
#endif
        }

        public static string NormalizeFilter(string filter)
        {
            string value = string.IsNullOrWhiteSpace(filter) ? "All files|*.*" : filter;
            return value.Replace('|', '\0') + "\0";
        }

        private static bool TryEnterDialog()
        {
            return Interlocked.CompareExchange(ref dialogOpen, 1, 0) == 0;
        }

        private static void ExitDialog()
        {
            Interlocked.Exchange(ref dialogOpen, 0);
        }

#if UNITY_STANDALONE_WIN
        private static void StartDialogThread(Func<string> operation, SynchronizationContext unityContext,
            Action<string> completed)
        {
            try
            {
                var thread = new Thread(() =>
                {
                    string result = string.Empty;
                    string failure = null;
                    try { result = operation?.Invoke() ?? string.Empty; }
                    catch (Exception ex) { failure = ex.ToString(); }
                    finally { ExitDialog(); }

                    unityContext.Post(_ =>
                    {
                        if (!string.IsNullOrWhiteSpace(failure))
                            Debug.LogError($"[SarNativeFileDialog] Native dialog failed: {failure}");
                        completed?.Invoke(result);
                    }, null);
                })
                {
                    IsBackground = true,
                    Name = "SAR Native File Dialog"
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch (Exception ex)
            {
                ExitDialog();
                Debug.LogError($"[SarNativeFileDialog] Could not start native dialog: {ex.Message}");
                completed?.Invoke(string.Empty);
            }
        }
#endif

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private static string OpenFileWindowsCore(string title, FilterSpec[] filters,
            string initialDirectory, IntPtr owner)
        {
            return ShowWindowsDialog(false, title, filters, initialDirectory, null, null, owner);
        }

        private static string SaveFileWindowsCore(string title, FilterSpec[] filters,
            string initialDirectory, string initialName, string defaultExtension, IntPtr owner)
        {
            return ShowWindowsDialog(true, title, filters, initialDirectory, initialName,
                defaultExtension, owner);
        }

        private static string ShowWindowsDialog(bool save, string title, FilterSpec[] filters,
            string initialDirectory, string initialName, string defaultExtension, IntPtr owner)
        {
            object instance = null;
            IFileDialog dialog = null;
            IShellItem result = null;
            IShellItem folder = null;
            try
            {
                Guid classId = new Guid(save
                    ? "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B"
                    : "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
                Type dialogType = Type.GetTypeFromCLSID(classId, true);
                instance = Activator.CreateInstance(dialogType);
                dialog = (IFileDialog)instance;
                dialog.SetTitle(string.IsNullOrWhiteSpace(title) ? "Select File" : title);
                dialog.SetOptions(ForceFileSystem | NoChangeDirectory | PathMustExist |
                                  (save ? OverwritePrompt : FileMustExist));
                if (filters != null && filters.Length > 0)
                {
                    dialog.SetFileTypes((uint)filters.Length, filters);
                    dialog.SetFileTypeIndex(1);
                }
                if (!string.IsNullOrWhiteSpace(initialDirectory) &&
                    TryCreateShellItem(initialDirectory, out folder))
                    dialog.SetFolder(folder);
                if (save && !string.IsNullOrWhiteSpace(initialName)) dialog.SetFileName(initialName);
                if (save && !string.IsNullOrWhiteSpace(defaultExtension))
                    dialog.SetDefaultExtension(defaultExtension.TrimStart('.'));

                int showResult = dialog.Show(owner);
                if (showResult == CancelledHResult) return string.Empty;
                Marshal.ThrowExceptionForHR(showResult);
                dialog.GetResult(out result);
                result.GetDisplayName(FileSystemPath, out IntPtr namePointer);
                try { return Marshal.PtrToStringUni(namePointer) ?? string.Empty; }
                finally { if (namePointer != IntPtr.Zero) Marshal.FreeCoTaskMem(namePointer); }
            }
            finally
            {
                ReleaseComObject(result);
                ReleaseComObject(folder);
                ReleaseComObject(instance);
            }
        }

        private static bool TryCreateShellItem(string path, out IShellItem item)
        {
            Guid shellItemId = typeof(IShellItem).GUID;
            int result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out item);
            return result >= 0 && item != null;
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
#endif

        public static bool ValidateWindowsInterop(out string error)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            error = null;
            object instance = null;
            try
            {
                Type dialogType = Type.GetTypeFromCLSID(
                    new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true);
                instance = Activator.CreateInstance(dialogType);
                var dialog = (IFileDialog)instance;
                dialog.SetTitle("SAR file-dialog interop test");
                dialog.SetOptions(ForceFileSystem | NoChangeDirectory | PathMustExist | FileMustExist);
                FilterSpec[] filters = ParseFilters("JSON files|*.json|All files|*.*");
                dialog.SetFileTypes((uint)filters.Length, filters);
                dialog.GetOptions(out uint options);
                if ((options & ForceFileSystem) == 0)
                {
                    error = "IFileDialog did not preserve the requested options.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return false;
            }
            finally { ReleaseComObject(instance); }
#else
            error = "Windows IFileDialog is unavailable on this platform.";
            return false;
#endif
        }

        private static FilterSpec[] ParseFilters(string filter)
        {
            string value = string.IsNullOrWhiteSpace(filter) ? "All files|*.*" : filter;
            string[] parts = value.Split('|');
            var filters = new List<FilterSpec>();
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                if (string.IsNullOrWhiteSpace(parts[i + 1])) continue;
                filters.Add(new FilterSpec { Name = parts[i], Pattern = parts[i + 1] });
            }
            if (filters.Count == 0) filters.Add(new FilterSpec { Name = "All files", Pattern = "*.*" });
            return filters.ToArray();
        }

        private static string FilterExtension(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return string.Empty;
            string[] parts = filter.Split('|');
            if (parts.Length < 2) return string.Empty;
            string[] extensions = parts[1].Split(';');
            for (int i = 0; i < extensions.Length; i++)
                extensions[i] = extensions[i].Replace("*.", string.Empty).Replace("*", string.Empty).TrimStart('.');
            return string.Join(",", extensions);
        }

        private static string InitialDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (System.IO.Directory.Exists(path)) return path;
            try
            {
                string fullPath = System.IO.Path.GetFullPath(path);
                string directory = System.IO.Path.GetDirectoryName(fullPath);
                return System.IO.Directory.Exists(directory) ? directory : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
