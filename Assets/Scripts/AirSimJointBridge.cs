using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace RoadNetworkEditor
{
#if UNITY_EDITOR
    /// <summary>
    /// Sends kinematic aircraft poses to the AirSim joint bridge while a simulation is running.
    /// The bridge process is started from SimulationManager when joint simulation is enabled.
    /// Editor only: the Windows player has no Python interpreter and no Unreal host.
    /// </summary>
    public sealed class AirSimJointBridge : MonoBehaviour
    {
        public const int PosePort = 49001;

        // The interpreter and the bridge package live outside the Unity project, so
        // neither path can be compiled in. Override them with the environment
        // variables below, or set them in the JointSimulation section of the
        // SimulationManager Inspector before starting Play Mode.
        public const string PythonPathVariable = "SAR_BRIDGE_PYTHON";
        public const string WorkingDirectoryVariable = "SAR_BRIDGE_WORKDIR";
        private const string DefaultPythonPath = @"python.exe";
        private const string DefaultWorkingDirectory = ".";

        [Tooltip("Python interpreter that runs the bridge. Empty reads " + PythonPathVariable + ".")]
        public string pythonPathOverride = string.Empty;

        [Tooltip("Folder containing the airsim_bridge package. Empty reads " + WorkingDirectoryVariable + ".")]
        public string workingDirectoryOverride = string.Empty;

        private SimulationManager manager;
        private UdpClient udp;
        private IPEndPoint endpoint;
        private Process bridgeProcess;
        private float sendAccumulator;
        private uint sequence;
        private readonly Dictionary<string, Vector3> previousPositions = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, float> previousTimes = new Dictionary<string, float>();
        private readonly byte[] packet = new byte[65507];
        private int diagnosticFrameCount;
        private float diagnosticLogAccumulator;

        /// <summary>Resolved interpreter: Inspector override, then environment, then PATH.</summary>
        public string ResolvedPythonPath
        {
            get
            {
                if (!string.IsNullOrEmpty(pythonPathOverride)) return pythonPathOverride;
                string fromEnvironment = Environment.GetEnvironmentVariable(PythonPathVariable);
                return string.IsNullOrEmpty(fromEnvironment) ? DefaultPythonPath : fromEnvironment;
            }
        }

        /// <summary>Resolved bridge folder: Inspector override, then environment, then the current directory.</summary>
        public string ResolvedWorkingDirectory
        {
            get
            {
                if (!string.IsNullOrEmpty(workingDirectoryOverride)) return workingDirectoryOverride;
                string fromEnvironment = Environment.GetEnvironmentVariable(WorkingDirectoryVariable);
                return string.IsNullOrEmpty(fromEnvironment) ? DefaultWorkingDirectory : fromEnvironment;
            }
        }

        /// <summary>
        /// Turn an interpreter name into an existing file. A bare name such as
        /// <c>python.exe</c> is resolved against PATH, because Process.Start accepts it
        /// while File.Exists cannot. Returns null when nothing matches.
        /// </summary>
        private static string ResolveInterpreter(string python)
        {
            if (File.Exists(python)) return Path.GetFullPath(python);
            if (python.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                python.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                python.IndexOf(':') >= 0)
            {
                // An explicit path that does not exist must not fall back to PATH.
                return null;
            }
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string folder in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                string candidate;
                try
                {
                    candidate = Path.Combine(folder.Trim('"'), python);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public bool IsBridgeRunning => bridgeProcess != null && !bridgeProcess.HasExited;

        public void Begin(SimulationManager simulationManager)
        {
            manager = simulationManager;
            if (udp == null)
            {
                udp = new UdpClient();
                endpoint = new IPEndPoint(IPAddress.Loopback, PosePort);
            }
            StartBridgeProcess();
        }

        public void End()
        {
            if (bridgeProcess == null) return;
            try
            {
                if (!bridgeProcess.HasExited)
                    bridgeProcess.Kill();
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[AirSimJoint] Bridge process did not stop cleanly: " + exception.Message);
            }
            bridgeProcess.Dispose();
            bridgeProcess = null;
        }

        private void StartBridgeProcess()
        {
            if (IsBridgeRunning) return;
            string configured = ResolvedPythonPath;
            string workingDirectory = ResolvedWorkingDirectory;
            string python = ResolveInterpreter(configured);
            if (python == null)
            {
                UnityEngine.Debug.LogError(
                    "[AirSimJoint] Python was not found at \"" + configured + "\". Set " +
                    PythonPathVariable + " (or the Inspector override) to the interpreter " +
                    "that has the airsim package installed.");
                return;
            }
            if (!Directory.Exists(workingDirectory))
            {
                UnityEngine.Debug.LogError(
                    "[AirSimJoint] Bridge folder was not found at \"" + workingDirectory + "\". Set " +
                    WorkingDirectoryVariable + " (or the Inspector override) to the folder that " +
                    "contains the airsim_bridge package.");
                return;
            }
            var start = new ProcessStartInfo
            {
                FileName = python,
                Arguments = "-m airsim_bridge.joint_bridge --launch-airsim",
                WorkingDirectory = Path.GetFullPath(workingDirectory),
                UseShellExecute = true,
                CreateNoWindow = false
            };
            bridgeProcess = Process.Start(start);
            UnityEngine.Debug.Log("[AirSimJoint] Bridge started. UDP 127.0.0.1:" + PosePort);
        }

        private void LateUpdate()
        {
            if (manager == null || !manager.IsRunning() || udp == null) return;
            sendAccumulator += Time.unscaledDeltaTime;
            if (sendAccumulator < 1f / 30f) return;
            sendAccumulator = 0f;
            SendPoses();

            // Diagnostic: log first-aircraft pose every ~2 s
            diagnosticLogAccumulator += Time.unscaledDeltaTime;
            if (diagnosticLogAccumulator > 2f)
            {
                diagnosticLogAccumulator = 0f;
                LogDiagnostic();
            }
        }

        private void LogDiagnostic()
        {
            var aircraft = manager.AircraftObjects;
            if (aircraft == null || aircraft.Count == 0) return;
            int n = 0;
            foreach (var pair in aircraft)
            {
                if (pair.Value == null) continue;
                Transform body = pair.Value.transform;
                Vector3 p = body.position;
                Quaternion q = body.rotation;
                // yaw from quaternion
                float yaw = Mathf.Atan2(2f * (q.w * q.y + q.x * q.z),
                    1f - 2f * (q.y * q.y + q.z * q.z)) * Mathf.Rad2Deg;
                UnityEngine.Debug.Log($"[AirSimJoint-diag] #{diagnosticFrameCount} \"{pair.Key}\" " +
                    $"pos=({p.x:F3},{p.y:F3},{p.z:F3}) " +
                    $"quat=({q.x:F4},{q.y:F4},{q.z:F4},{q.w:F4}) yaw={yaw:F2}°");
                n++;
                if (n >= 3) break; // top 3 only
            }
            diagnosticFrameCount++;
        }

        private void SendPoses()
        {
            var aircraft = manager.AircraftObjects;
            if (aircraft == null || aircraft.Count == 0) return;

            int offset = 0;
            packet[offset++] = (byte)'I';
            packet[offset++] = (byte)'T';
            packet[offset++] = (byte)'S';
            packet[offset++] = (byte)'1';
            packet[offset++] = 1;
            int countOffset = offset;
            offset += 2;
            byte[] seqBytes = BitConverter.GetBytes(sequence++);
            Buffer.BlockCopy(seqBytes, 0, packet, offset, 4);
            offset += 4;

            int count = 0;
            float now = Time.unscaledTime;
            foreach (var pair in aircraft)
            {
                if (pair.Value == null || count >= 255) continue;
                string name = Sanitize(pair.Key);
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                if (nameBytes.Length == 0 || nameBytes.Length > 64) continue;
                Transform body = pair.Value.transform;
                Vector3 position = body.position;
                Quaternion rotation = body.rotation;
                Vector3 velocity = Vector3.zero;
                if (previousPositions.TryGetValue(pair.Key, out Vector3 previous) &&
                    previousTimes.TryGetValue(pair.Key, out float previousTime))
                {
                    float dt = now - previousTime;
                    if (dt > 1e-4f) velocity = (position - previous) / dt;
                }
                previousPositions[pair.Key] = position;
                previousTimes[pair.Key] = now;

                int recordSize = 1 + nameBytes.Length + 40;
                if (offset + recordSize > packet.Length) break;
                packet[offset++] = (byte)nameBytes.Length;
                Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
                offset += nameBytes.Length;
                offset = WriteFloat(packet, offset, position.x);
                offset = WriteFloat(packet, offset, position.y);
                offset = WriteFloat(packet, offset, position.z);
                offset = WriteFloat(packet, offset, rotation.x);
                offset = WriteFloat(packet, offset, rotation.y);
                offset = WriteFloat(packet, offset, rotation.z);
                offset = WriteFloat(packet, offset, rotation.w);
                offset = WriteFloat(packet, offset, velocity.x);
                offset = WriteFloat(packet, offset, velocity.y);
                offset = WriteFloat(packet, offset, velocity.z);
                count++;
            }

            byte[] countBytes = BitConverter.GetBytes((ushort)count);
            packet[countOffset] = countBytes[0];
            packet[countOffset + 1] = countBytes[1];
            if (count > 0)
                udp.Send(packet, offset, endpoint);
        }

        private static int WriteFloat(byte[] buffer, int offset, float value)
        {
            byte[] raw = BitConverter.GetBytes(value);
            Buffer.BlockCopy(raw, 0, buffer, offset, 4);
            return offset + 4;
        }

        private static string Sanitize(string aircraftId)
        {
            if (string.IsNullOrEmpty(aircraftId)) return "A";
            var builder = new StringBuilder(aircraftId.Length);
            foreach (char c in aircraftId)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
                    builder.Append(c);
            }
            return builder.Length == 0 ? "A" : builder.ToString();
        }

        private void OnDestroy()
        {
            End();
            if (udp != null)
            {
                udp.Close();
                udp = null;
            }
        }
    }
#endif
}

