using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RoadNetworkEditor.Networking;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public class LaneRoutingUtilityTests
    {
        [Test]
        public void FindShortestPath_UsesAdjacentLaneChangeToReachRequiredTurn()
        {
            Lane outer = TestLane("E1-OUTER", 0f);
            Lane inner = TestLane("E1-INNER", 4f);
            Lane next = new Lane
            {
                LaneId = "E2-NEXT",
                Centerline = new List<Vector3>
                {
                    new Vector3(100f, 75f, 4f),
                    new Vector3(200f, 75f, 4f)
                },
                Length = 100f
            };
            inner.ToLanesId.Add(next.LaneId);
            next.FromLanesId.Add(inner.LaneId);
            var edge1 = new Edge { EdgeId = "E1", Lanes = new List<Lane> { outer, inner } };
            var edge2 = new Edge { EdgeId = "E2", Lanes = new List<Lane> { next } };
            var lanes = new[] { outer, inner, next }.ToDictionary(lane => lane.LaneId);

            List<Lane> route = LaneRoutingUtility.FindShortestPath(
                lanes,
                new[] { edge1, edge2 },
                new List<LaneConnectionCorridor>(),
                outer.LaneId,
                next.LaneId);

            Assert.That(route, Is.Not.Null);
            Assert.That(route.Select(lane => lane.LaneId),
                Is.EqualTo(new[] { "E1-OUTER", "E1-INNER", "E2-NEXT" }));
        }

        [Test]
        public void AreAdjacentLanesOnSameEdge_RejectsSkippedAndOppositeLanes()
        {
            Lane lane0 = TestLane("L0", 0f);
            Lane lane1 = TestLane("L1", 4f);
            Lane lane2 = TestLane("L2", 8f);
            Lane opposite = new Lane
            {
                LaneId = "OPP",
                Centerline = new List<Vector3>
                {
                    new Vector3(100f, 75f, 12f),
                    new Vector3(0f, 75f, 12f)
                }
            };
            var lanes = new[] { lane0, lane1, lane2, opposite }
                .ToDictionary(lane => lane.LaneId);
            var laneToEdge = lanes.Keys.ToDictionary(id => id, _ => "E1");

            Assert.That(LaneRoutingUtility.AreAdjacentLanesOnSameEdge(
                lane0, lane1, lanes, laneToEdge), Is.True);
            Assert.That(LaneRoutingUtility.AreAdjacentLanesOnSameEdge(
                lane0, lane2, lanes, laneToEdge), Is.False);
            Assert.That(LaneRoutingUtility.AreAdjacentLanesOnSameEdge(
                lane2, opposite, lanes, laneToEdge), Is.False);
        }

        [Test]
        public void FindShortestPath_CanAvoidBlockedLaneForDeadlockReroute()
        {
            Lane current = TestLane("CURRENT", 0f);
            Lane blocked = TestLane("BLOCKED", 4f);
            Lane alternate = new Lane
            {
                LaneId = "ALTERNATE",
                Centerline = new List<Vector3>
                {
                    new Vector3(100f, 75f, 0f),
                    new Vector3(200f, 75f, 0f)
                },
                Length = 100f
            };
            Lane target = new Lane
            {
                LaneId = "TARGET",
                Centerline = new List<Vector3>
                {
                    new Vector3(200f, 75f, 0f),
                    new Vector3(300f, 75f, 0f)
                },
                Length = 100f
            };
            current.ToLanesId.Add(alternate.LaneId);
            blocked.ToLanesId.Add(target.LaneId);
            alternate.ToLanesId.Add(target.LaneId);
            var edges = new[]
            {
                new Edge { EdgeId = "E1", Lanes = new List<Lane> { current, blocked } },
                new Edge { EdgeId = "E2", Lanes = new List<Lane> { alternate } },
                new Edge { EdgeId = "E3", Lanes = new List<Lane> { target } }
            };
            var lanes = edges.SelectMany(edge => edge.Lanes).ToDictionary(lane => lane.LaneId);

            List<Lane> route = LaneRoutingUtility.FindShortestPath(
                lanes,
                edges,
                new List<LaneConnectionCorridor>(),
                current.LaneId,
                target.LaneId,
                10f,
                new HashSet<string> { blocked.LaneId });

            Assert.That(route.Select(lane => lane.LaneId),
                Is.EqualTo(new[] { "CURRENT", "ALTERNATE", "TARGET" }));
        }

        [Test]
        public void BundledNetwork_AllOrderedVertiportPairsAreReachableWithLaneChanges()
        {
            string networkPath = Path.Combine(Application.dataPath, "DataBase", "FullNetwork.json");
            string vertiportPath = Path.Combine(Application.dataPath, "DataBase", "VertiportData.json");
            JObject networkJson = JObject.Parse(File.ReadAllText(networkPath));
            JArray vertiportJson = JArray.Parse(File.ReadAllText(vertiportPath));

            List<Edge> edges = ParseEdges(networkJson);
            Dictionary<string, Lane> lanes = edges
                .SelectMany(edge => edge.Lanes)
                .ToDictionary(lane => lane.LaneId);
            int checkedPairs = 0;

            foreach (JObject origin in vertiportJson.OfType<JObject>())
            {
                foreach (JObject destination in vertiportJson.OfType<JObject>())
                {
                    string originId = origin.Value<string>("VertiportId");
                    string destinationId = destination.Value<string>("VertiportId");
                    if (originId == destinationId) continue;

                    List<string> starts = CorridorLaneIds(origin, "Entry");
                    List<string> targets = CorridorLaneIds(destination, "Exit");
                    bool reachable = starts.Any(start => targets.Any(target =>
                        LaneRoutingUtility.FindShortestPath(
                            lanes,
                            edges,
                            new List<LaneConnectionCorridor>(),
                            start,
                            target) != null));

                    Assert.That(reachable, Is.True,
                        $"No lane route, including legal adjacent lane changes, for {originId} -> {destinationId}.");
                    checkedPairs++;
                }
            }

            Assert.That(checkedPairs, Is.EqualTo(42));
        }

        private static Lane TestLane(string id, float z)
        {
            return new Lane
            {
                LaneId = id,
                Centerline = new List<Vector3>
                {
                    new Vector3(0f, 75f, z),
                    new Vector3(100f, 75f, z)
                },
                Length = 100f
            };
        }

        private static List<Edge> ParseEdges(JObject network)
        {
            var result = new List<Edge>();
            foreach (JObject edgeJson in (network["Edges"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var edge = new Edge
                {
                    EdgeId = edgeJson.Value<string>("EdgeId"),
                    Lanes = new List<Lane>()
                };
                foreach (JObject laneJson in
                         (edgeJson["Lanes"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    edge.Lanes.Add(new Lane
                    {
                        LaneId = laneJson.Value<string>("LaneId"),
                        Length = laneJson.Value<float?>("Length") ?? 0f,
                        FromLanesId = (laneJson["FromLanesId"] as JArray ?? new JArray())
                            .Values<string>().ToList(),
                        ToLanesId = (laneJson["ToLanesId"] as JArray ?? new JArray())
                            .Values<string>().ToList(),
                        Centerline = (laneJson["Centerline"] as JArray ?? new JArray())
                            .OfType<JObject>()
                            .Select(point => new Vector3(
                                point.Value<float>("x"),
                                point.Value<float>("y"),
                                point.Value<float>("z")))
                            .ToList()
                    });
                }
                result.Add(edge);
            }
            return result;
        }

        private static List<string> CorridorLaneIds(JObject vertiport, string type)
        {
            return (vertiport["Corridors"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Where(corridor => corridor.Value<string>("CorridorType") == type)
                .Select(corridor => corridor.Value<string>("LinkLaneId"))
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
        }
    }
}
