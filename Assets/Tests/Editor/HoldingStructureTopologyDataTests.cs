using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public class HoldingStructureTopologyDataTests
    {
        [Test]
        public void EveryDirectedCircleSegment_HasExactlyOneExplicitRampResource()
        {
            string dataPath = Path.Combine(Application.dataPath, "DataBase", "VertiportData.json");
            JArray vertiports = JArray.Parse(File.ReadAllText(dataPath));
            int checkedSegments = 0;

            foreach (JObject vertiport in vertiports.OfType<JObject>())
            {
                foreach (JObject holdingStructure in
                         (vertiport["HoldingStructures"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    string holdingStructureId = holdingStructure.Value<string>("HoldingStructureId");
                    var ramps = (holdingStructure["Ramps"] as JArray ?? new JArray())
                        .OfType<JObject>()
                        .ToList();

                    foreach (JObject circle in
                             (holdingStructure["Circles"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        string circleId = circle.Value<string>("CircleId");
                        var vertexIds = (circle["CoveredVertices"] as JArray ?? new JArray())
                            .Values<string>()
                            .ToList();
                        if (vertexIds.Count < 2) continue;

                        for (int i = 0; i < vertexIds.Count; i++)
                        {
                            string fromId = vertexIds[i];
                            string toId = vertexIds[(i + 1) % vertexIds.Count];
                            int matchingRampCount = ramps.Count(ramp =>
                                ramp.Value<string>("FromVertexId") == fromId &&
                                ramp.Value<string>("ToVertexId") == toId &&
                                !string.IsNullOrEmpty(ramp.Value<string>("RampId")));

                            Assert.That(
                                matchingRampCount,
                                Is.EqualTo(1),
                                $"{holdingStructureId}/{circleId}: {fromId} -> {toId} must map " +
                                "to exactly one reservable ramp.");
                            checkedSegments++;
                        }
                    }
                }
            }

            Assert.That(checkedSegments, Is.GreaterThan(0), "No circle segments were found in the dataset.");
        }
    }
}
