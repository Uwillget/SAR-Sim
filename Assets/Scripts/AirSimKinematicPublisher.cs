using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace RoadNetworkEditor
{
#if UNITY_EDITOR
    /// <summary>
    /// Master-side kinematic stream for AirSim. Sends Unity ENU poses (metres,
    /// x=east y=up z=north) to 127.0.0.1:49001. Python airsim_bridge.kinematic_slave
    /// converts to NED and calls simSetVehiclePose(ignore_collision=true).
    /// Editor only: the Shenzhen demo scene that uses it is not distributed, and the
    /// Windows player has no Unreal host to talk to.
    /// </summary>
    public class AirSimKinematicPublisher : MonoBehaviour
    {
        [Header("UDP")]
        public string host = "127.0.0.1";
        public int port = 49001;
        [Range(1f, 120f)] public float sendHz = 50f;
        public string defaultAirSimVehicle = "Drone1";

        [Header("Sources")]
        [Tooltip("If empty, publishes every AircraftAgent / FlightController in the scene.")]
        public Transform[] explicitVehicles;

        UdpClient _udp;
        IPEndPoint _ep;
        float _accum;
        readonly StringBuilder _sb = new StringBuilder(2048);

        void OnEnable()
        {
            _ep = new IPEndPoint(IPAddress.Parse(host), port);
            _udp = new UdpClient();
        }

        void OnDisable()
        {
            if (_udp != null)
            {
                _udp.Close();
                _udp = null;
            }
        }

        void LateUpdate()
        {
            if (_udp == null) return;
            _accum += Time.deltaTime;
            float dt = 1f / Mathf.Max(1f, sendHz);
            if (_accum < dt) return;
            _accum = 0f;
            SendFrame();
        }

        void SendFrame()
        {
            _sb.Length = 0;
            _sb.Append("{\"frame\":\"unity_enu\",\"t\":");
            _sb.Append(Time.time.ToString("R"));
            _sb.Append(",\"vehicles\":[");
            int count = 0;

            if (explicitVehicles != null)
            {
                for (int i = 0; i < explicitVehicles.Length; i++)
                {
                    var t = explicitVehicles[i];
                    if (t == null) continue;
                    AppendVehicle(ref count, t.name, i == 0 ? defaultAirSimVehicle : t.name, t);
                }
            }

            if (count == 0)
            {
                var agents = FindObjectsOfType<AircraftAgent>();
                for (int i = 0; i < agents.Length; i++)
                {
                    var a = agents[i];
                    string id = a.aircraftState != null ? a.aircraftState.AircraftId : a.name;
                    string air = i == 0 ? defaultAirSimVehicle : id;
                    AppendVehicle(ref count, id, air, a.transform);
                }
            }

            if (count == 0)
            {
                var fcs = FindObjectsOfType<FlightController>();
                for (int i = 0; i < fcs.Length; i++)
                {
                    string id = fcs[i].name;
                    string air = i == 0 ? defaultAirSimVehicle : id;
                    AppendVehicle(ref count, id, air, fcs[i].transform);
                }
            }

            if (count == 0) return;
            _sb.Append("]}");
            byte[] payload = Encoding.UTF8.GetBytes(_sb.ToString());
            try
            {
                _udp.Send(payload, payload.Length, _ep);
            }
            catch (Exception)
            {
            }
        }

        void AppendVehicle(ref int count, string id, string airsimName, Transform t)
        {
            if (count > 0) _sb.Append(',');
            Vector3 p = t.position;
            Quaternion q = t.rotation;
            _sb.Append("{\"id\":\"");
            _sb.Append(Escape(id));
            _sb.Append("\",\"airsim\":\"");
            _sb.Append(Escape(airsimName));
            _sb.Append("\",\"pos\":[");
            _sb.Append(p.x.ToString("R"));
            _sb.Append(',');
            _sb.Append(p.y.ToString("R"));
            _sb.Append(',');
            _sb.Append(p.z.ToString("R"));
            _sb.Append("],\"quat\":[");
            _sb.Append(q.x.ToString("R"));
            _sb.Append(',');
            _sb.Append(q.y.ToString("R"));
            _sb.Append(',');
            _sb.Append(q.z.ToString("R"));
            _sb.Append(',');
            _sb.Append(q.w.ToString("R"));
            _sb.Append("]}");
            count++;
        }

        static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("ITSAC/Add AirSim Kinematic Publisher")]
        static void AddToScene()
        {
            var existing = FindObjectOfType<AirSimKinematicPublisher>();
            if (existing != null)
            {
                UnityEditor.Selection.activeObject = existing.gameObject;
                Debug.Log("[AirSim] publisher already in scene: " + existing.name);
                return;
            }
            var go = new GameObject("AirSimKinematicPublisher");
            go.AddComponent<AirSimKinematicPublisher>();
            UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Add AirSim publisher");
            UnityEditor.Selection.activeObject = go;
            Debug.Log("[AirSim] added AirSimKinematicPublisher. Play mode sends Unity ENU poses to 127.0.0.1:49001");
        }
#endif
    }
#endif
}
