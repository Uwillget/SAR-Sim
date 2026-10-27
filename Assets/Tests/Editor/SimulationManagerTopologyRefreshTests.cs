using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public class SimulationManagerTopologyRefreshTests
    {
        [Test]
        public void LoadNetworkData_RemovesLanesAndEdgesMissingFromAuthoritativeNetwork()
        {
            var networkObject = new GameObject("NetworkManager topology refresh test");
            var simulationObject = new GameObject("SimulationManager topology refresh test");
            networkObject.SetActive(false);
            simulationObject.SetActive(false);

            try
            {
                NetworkManager networkManager = networkObject.AddComponent<NetworkManager>();
                networkManager.FullNetwork = new Network
                {
                    NetworkId = "TEST",
                    Nodes = new List<Node>(),
                    Edges = new List<Edge>(),
                    AltitudeLayers = new List<AltitudeLayer>
                    {
                        new AltitudeLayer { LayerId = "L0", NominalAltitude = 75f }
                    },
                    LaneConnectionCorridors = new List<LaneConnectionCorridor>()
                };

                SimulationManager simulationManager = simulationObject.AddComponent<SimulationManager>();
                simulationManager.networkManager = networkManager;
                simulationManager.Edges["STALE-EDGE"] = new Edge { EdgeId = "STALE-EDGE" };
                simulationManager.Lanes["STALE-LANE"] = new Lane
                {
                    LaneId = "STALE-LANE",
                    Centerline = new List<Vector3> { Vector3.zero, Vector3.right }
                };

                typeof(SimulationManager).GetMethod("LoadNetworkData",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(simulationManager, null);

                Assert.That(simulationManager.Edges.ContainsKey("STALE-EDGE"), Is.False);
                Assert.That(simulationManager.Lanes.ContainsKey("STALE-LANE"), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(simulationObject);
                Object.DestroyImmediate(networkObject);
            }
        }
    }
}
