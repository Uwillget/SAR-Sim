using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace RoadNetworkEditor.RuntimeUI
{
    /// <summary>
    /// Receives files dropped from Windows Explorer. Native callbacks only enqueue
    /// immutable data; Unity objects are touched later from Update on the main thread.
    /// </summary>
    public sealed class SarWindowsFileDrop : MonoBehaviour
    {
        public event Action<IReadOnlyList<string>, Vector2> FilesDropped;

        private readonly Queue<DropMessage> pending = new Queue<DropMessage>();

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const int GwlWndProc = -4;
        private const uint WmDropFiles = 0x0233;
        private IntPtr windowHandle;
        private IntPtr previousWndProc;
        private WindowProcedure windowProcedure;

        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern IntPtr SetWindowLong32(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message,
            IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll")]
        private static extern void DragAcceptFiles(IntPtr window, bool accept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr dropHandle, uint index, StringBuilder file, uint capacity);

        [DllImport("shell32.dll")]
        private static extern bool DragQueryPoint(IntPtr dropHandle, out NativePoint point);

        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr dropHandle);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

        private static IntPtr SetWindowProcedure(IntPtr window, IntPtr value) => IntPtr.Size == 8
            ? SetWindowLongPtr64(window, GwlWndProc, value)
            : SetWindowLong32(window, GwlWndProc, value);

        private void Start()
        {
            TryInstallWindowHook();
        }

        private void TryInstallWindowHook()
        {
            if (windowHandle != IntPtr.Zero) return;
            windowHandle = GetActiveWindow();
            if (windowHandle == IntPtr.Zero)
                windowHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (windowHandle == IntPtr.Zero) return;
            windowProcedure = HandleWindowMessage;
            IntPtr procedurePointer = Marshal.GetFunctionPointerForDelegate(windowProcedure);
            previousWndProc = SetWindowProcedure(windowHandle, procedurePointer);
            if (previousWndProc != IntPtr.Zero)
            {
                DragAcceptFiles(windowHandle, true);
                Debug.Log("[SarWindowsFileDrop] Windows file-drop receiver is ready.");
                return;
            }

            windowHandle = IntPtr.Zero;
            windowProcedure = null;
        }

        private IntPtr HandleWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmDropFiles)
            {
                var paths = new List<string>();
                uint count = DragQueryFile(wParam, uint.MaxValue, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    uint length = DragQueryFile(wParam, i, null, 0);
                    var buffer = new StringBuilder((int)length + 1);
                    if (DragQueryFile(wParam, i, buffer, (uint)buffer.Capacity) > 0)
                        paths.Add(buffer.ToString());
                }
                bool hasPoint = DragQueryPoint(wParam, out NativePoint point);
                DragFinish(wParam);
                Vector2 screenPoint = hasPoint ? ConvertToUnityScreenPoint(point) : new Vector2(-1f, -1f);
                lock (pending)
                    pending.Enqueue(new DropMessage(paths, screenPoint));
                return IntPtr.Zero;
            }
            return previousWndProc != IntPtr.Zero
                ? CallWindowProc(previousWndProc, window, message, wParam, lParam)
                : IntPtr.Zero;
        }

        private void OnDestroy()
        {
            if (windowHandle == IntPtr.Zero || previousWndProc == IntPtr.Zero) return;
            DragAcceptFiles(windowHandle, false);
            SetWindowProcedure(windowHandle, previousWndProc);
            previousWndProc = IntPtr.Zero;
            windowProcedure = null;
        }
#endif

        private void Update()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (windowHandle == IntPtr.Zero) TryInstallWindowHook();
#endif
            while (true)
            {
                DropMessage message;
                lock (pending)
                {
                    if (pending.Count == 0) break;
                    message = pending.Dequeue();
                }
                Debug.Log($"[SarWindowsFileDrop] Received {message.Paths.Count} file(s) at " +
                          $"({message.ScreenPoint.x:0}, {message.ScreenPoint.y:0}).");
                FilesDropped?.Invoke(message.Paths, message.ScreenPoint);
            }
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private Vector2 ConvertToUnityScreenPoint(NativePoint point)
        {
            float clientWidth = Screen.width;
            float clientHeight = Screen.height;
            if (windowHandle != IntPtr.Zero && GetClientRect(windowHandle, out NativeRect client))
            {
                clientWidth = Math.Max(1, client.Right - client.Left);
                clientHeight = Math.Max(1, client.Bottom - client.Top);
            }

            // DragQueryPoint returns client coordinates. Scale them to Unity's
            // render surface to account for Windows DPI/back-buffer scaling, then
            // flip Y because Win32 starts at the top and Unity starts at the bottom.
            float x = point.X * Screen.width / clientWidth;
            float y = Screen.height - point.Y * Screen.height / clientHeight;
            return new Vector2(x, y);
        }
#endif

        public void SimulateDropForTests(IReadOnlyList<string> paths, Vector2 screenPoint)
        {
            FilesDropped?.Invoke(paths, screenPoint);
        }

        private readonly struct DropMessage
        {
            public readonly IReadOnlyList<string> Paths;
            public readonly Vector2 ScreenPoint;

            public DropMessage(IReadOnlyList<string> paths, Vector2 screenPoint)
            {
                Paths = paths;
                ScreenPoint = screenPoint;
            }
        }
    }
}
