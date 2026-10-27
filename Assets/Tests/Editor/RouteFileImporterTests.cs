using System.Collections.Generic;
using NUnit.Framework;
using RoadNetworkEditor.Routes;

namespace RoadNetworkEditor.Tests.Editor
{
    public class RouteFileImporterTests
    {
        private static readonly HashSet<string> LaneIds =
            new HashSet<string> { "L-A", "LC-1", "L-B" };
        private static readonly HashSet<string> VertiportIds =
            new HashSet<string> { "VP-A", "VP-B" };
        private static readonly HashSet<string> HelipadIds =
            new HashSet<string> { "HP-A", "HP-B" };

        [Test]
        public void ParseContent_JsonNamedRoute_SortsTrafficByDeparture()
        {
            const string json = @"{
  ""routes"": [{ ""id"": ""R-1"", ""lanes"": [""L-A"", ""LC-1"", ""L-B""] }],
  ""vehicles"": [
    { ""id"": ""late"", ""route"": ""R-1"", ""from"": ""VP-A"", ""to"": ""VP-B"", ""depart"": 12 },
    { ""id"": ""early"", ""route"": ""R-1"", ""from"": ""VP-A"", ""to"": ""VP-B"", ""depart"": 3 }
  ]
}";

            RouteImportResult result = RouteFileImporter.ParseContent(
                json, "json", LaneIds, VertiportIds, HelipadIds);

            Assert.That(result.Success, Is.True, string.Join("; ", result.Errors));
            Assert.That(result.Missions, Has.Count.EqualTo(2));
            Assert.That(result.Missions[0].TaskId, Is.EqualTo("early"));
            Assert.That(result.Missions[0].RouteLaneIds,
                Is.EqualTo(new[] { "L-A", "LC-1", "L-B" }));
        }

        [Test]
        public void ParseContent_XmlFlow_ExpandsDepartures()
        {
            const string xml = @"<routes>
  <route id=""R-1"" edges=""L-A L-B"" />
  <flow id=""morning"" route=""R-1"" from=""VP-A"" to=""VP-B""
        begin=""10"" period=""5"" number=""3"" />
</routes>";

            RouteImportResult result = RouteFileImporter.ParseContent(
                xml, ".xml", LaneIds, VertiportIds, HelipadIds);

            Assert.That(result.Success, Is.True, string.Join("; ", result.Errors));
            Assert.That(result.Missions, Has.Count.EqualTo(3));
            Assert.That(result.Missions[0].TaskId, Is.EqualTo("morning.0"));
            Assert.That(result.Missions[2].DepartureTime, Is.EqualTo(20f));
        }

        [Test]
        public void ParseContent_LegacyMissionFile_RemainsCompatible()
        {
            const string json = @"{
  ""Missions"": [{
    ""TaskId"": ""legacy-1"",
    ""OriginVertiportId"": ""VP-A"",
    ""DestinationVertiportId"": ""VP-B"",
    ""DepartureTime"": 2
  }]
}";

            RouteImportResult result = RouteFileImporter.ParseContent(
                json, ".json", LaneIds, VertiportIds, HelipadIds);

            Assert.That(result.Success, Is.True, string.Join("; ", result.Errors));
            Assert.That(result.Missions, Has.Count.EqualTo(1));
            Assert.That(result.Missions[0].RouteLaneIds, Is.Empty);
        }

        [Test]
        public void ParseContent_InvalidRecords_AreReportedWithoutDiscardingValidTraffic()
        {
            const string json = @"{
  ""vehicles"": [
    { ""id"": ""valid"", ""lanes"": [""L-A"", ""L-B""], ""from"": ""VP-A"", ""to"": ""VP-B"", ""depart"": 0 },
    { ""id"": ""bad-lane"", ""lanes"": [""MISSING""], ""from"": ""VP-A"", ""to"": ""VP-B"", ""depart"": 1 },
    { ""id"": ""bad-time"", ""from"": ""VP-A"", ""to"": ""VP-B"", ""depart"": ""soon"" }
  ]
}";

            RouteImportResult result = RouteFileImporter.ParseContent(
                json, ".json", LaneIds, VertiportIds, HelipadIds);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Missions, Has.Count.EqualTo(1));
            Assert.That(result.Missions[0].TaskId, Is.EqualTo("valid"));
            Assert.That(result.Errors.Exists(error => error.Contains("MISSING")), Is.True);
            Assert.That(result.Errors.Exists(error => error.Contains("invalid departure")), Is.True);
        }

        [TestCase(".json")]
        [TestCase(".xml")]
        public void ExportThenImport_PreservesEditableMissionData(string extension)
        {
            var missions = new List<MissionTask>
            {
                new MissionTask
                {
                    TaskId = "editable-1",
                    RouteId = "R-editable",
                    OriginVertiportId = "VP-A",
                    DestinationVertiportId = "VP-B",
                    OriginHelipadId = "HP-A",
                    DestinationHelipadId = "HP-B",
                    RouteLaneIds = new List<string> { "L-A", "LC-1", "L-B" },
                    DepartureTime = 7.5f,
                    Priority = 3
                }
            };

            string content = extension == ".xml"
                ? RouteFileExporter.SerializeXml(missions)
                : RouteFileExporter.SerializeJson(missions);
            RouteImportResult result = RouteFileImporter.ParseContent(
                content, extension, LaneIds, VertiportIds, HelipadIds);

            Assert.That(result.Success, Is.True, string.Join("; ", result.Errors));
            Assert.That(result.Missions, Has.Count.EqualTo(1));
            MissionTask restored = result.Missions[0];
            Assert.That(restored.TaskId, Is.EqualTo("editable-1"));
            Assert.That(restored.OriginHelipadId, Is.EqualTo("HP-A"));
            Assert.That(restored.DestinationHelipadId, Is.EqualTo("HP-B"));
            Assert.That(restored.RouteLaneIds, Is.EqualTo(new[] { "L-A", "LC-1", "L-B" }));
            Assert.That(restored.DepartureTime, Is.EqualTo(7.5f).Within(0.001f));
            Assert.That(restored.Priority, Is.EqualTo(3));
        }
    }
}
