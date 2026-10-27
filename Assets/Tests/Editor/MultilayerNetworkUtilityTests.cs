using System.Collections.Generic;
using NUnit.Framework;
using RoadNetworkEditor.Networking;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public class MultilayerNetworkUtilityTests
    {
        [Test]
        public void NormalizeAndValidate_AcceptsDirectedCrossLayerConnector()
        {
            Network network = CreateTwoLayerNetwork();
            network.LaneConnectionCorridors.Add(new LaneConnectionCorridor
            {
                CorridorId = "LC-0001",
                FromLaneId = "LANE-LOW",
                ToLaneId = "LANE-HIGH",
                Centerline = new List<Vector3>
                {
                    new Vector3(10f, 75f, 0f),
                    new Vector3(15f, 100f, 0f),
                    new Vector3(20f, 125f, 0f)
                }
            });

            NetworkValidationResult validation =
                MultilayerNetworkUtility.NormalizeAndValidate(network);

            Assert.That(validation.IsValid, Is.True, string.Join("; ", validation.Errors));
            LaneConnectionCorridor corridor = network.LaneConnectionCorridors[0];
            Assert.That(corridor.FromLayerId, Is.EqualTo("L0"));
            Assert.That(corridor.ToLayerId, Is.EqualTo("L1"));
            Assert.That(corridor.Length, Is.GreaterThan(50f));
            Assert.That(corridor.Centerline, Has.Count.EqualTo(33));
            Assert.That(corridor.Centerline[0], Is.EqualTo(new Vector3(10f, 75f, 0f)));
            Assert.That(corridor.Centerline[corridor.Centerline.Count - 1],
                Is.EqualTo(new Vector3(20f, 125f, 0f)));

            Lane virtualLane = MultilayerNetworkUtility.CreateVirtualLane(corridor);
            Assert.That(virtualLane.LaneType, Is.EqualTo("LayerConnector"));
            Assert.That(virtualLane.FromLanesId, Is.EqualTo(new[] { "LANE-LOW" }));
            Assert.That(virtualLane.ToLanesId, Is.EqualTo(new[] { "LANE-HIGH" }));
        }

        [Test]
        public void NormalizeAndValidate_UpgradesLegacySingleLayerNetworkInMemory()
        {
            var network = new Network
            {
                NetworkId = "LEGACY",
                AltitudeLayers = new List<AltitudeLayer>(),
                Edges = new List<Edge>
                {
                    new Edge
                    {
                        EdgeId = "E1",
                        Lanes = new List<Lane>
                        {
                            new Lane
                            {
                                LaneId = "L1",
                                LayerId = null,
                                Centerline = new List<Vector3>
                                {
                                    new Vector3(0f, 90f, 0f),
                                    new Vector3(10f, 90f, 0f)
                                }
                            }
                        }
                    }
                }
            };

            NetworkValidationResult validation =
                MultilayerNetworkUtility.NormalizeAndValidate(network);

            Assert.That(validation.IsValid, Is.True);
            Assert.That(network.SchemaVersion, Is.EqualTo(2));
            Assert.That(network.AltitudeLayers, Has.Count.EqualTo(1));
            Assert.That(network.AltitudeLayers[0].NominalAltitude, Is.EqualTo(90f).Within(0.001f));
            Assert.That(network.Edges[0].Lanes[0].LayerId, Is.EqualTo("L0"));
        }

        [Test]
        public void NormalizeAndValidate_RejectsConnectorWithUnknownLane()
        {
            Network network = CreateTwoLayerNetwork();
            network.LaneConnectionCorridors.Add(new LaneConnectionCorridor
            {
                CorridorId = "LC-BAD",
                FromLaneId = "MISSING",
                ToLaneId = "LANE-HIGH"
            });

            NetworkValidationResult validation =
                MultilayerNetworkUtility.NormalizeAndValidate(network);

            Assert.That(validation.IsValid, Is.False);
            Assert.That(validation.Errors.Exists(error => error.Contains("unknown FromLaneId")), Is.True);
        }

        [Test]
        public void NormalizeAndValidate_RejectsSameLayerConnector()
        {
            Network network = CreateTwoLayerNetwork();
            network.Edges[0].Lanes.Add(new Lane
            {
                LaneId = "LANE-LOW-2",
                LayerId = "L0",
                Centerline = new List<Vector3>
                {
                    new Vector3(0f, 75f, 10f),
                    new Vector3(10f, 75f, 10f)
                }
            });
            network.LaneConnectionCorridors.Add(new LaneConnectionCorridor
            {
                CorridorId = "LC-SAME",
                FromLaneId = "LANE-LOW",
                ToLaneId = "LANE-LOW-2"
            });

            NetworkValidationResult validation =
                MultilayerNetworkUtility.NormalizeAndValidate(network);

            Assert.That(validation.IsValid, Is.False);
            Assert.That(validation.Errors.Exists(error => error.Contains("different layers")), Is.True);
        }

        private static Network CreateTwoLayerNetwork()
        {
            return new Network
            {
                NetworkId = "MULTI",
                AltitudeLayers = new List<AltitudeLayer>
                {
                    new AltitudeLayer { LayerId = "L0", NominalAltitude = 75f },
                    new AltitudeLayer { LayerId = "L1", NominalAltitude = 125f }
                },
                Edges = new List<Edge>
                {
                    new Edge
                    {
                        EdgeId = "E-LOW",
                        Lanes = new List<Lane>
                        {
                            new Lane
                            {
                                LaneId = "LANE-LOW",
                                LayerId = "L0",
                                Centerline = new List<Vector3>
                                {
                                    new Vector3(0f, 75f, 0f),
                                    new Vector3(10f, 75f, 0f)
                                }
                            }
                        }
                    },
                    new Edge
                    {
                        EdgeId = "E-HIGH",
                        Lanes = new List<Lane>
                        {
                            new Lane
                            {
                                LaneId = "LANE-HIGH",
                                LayerId = "L1",
                                Centerline = new List<Vector3>
                                {
                                    new Vector3(20f, 125f, 0f),
                                    new Vector3(30f, 125f, 0f)
                                }
                            }
                        }
                    }
                }
            };
        }
    }
}
