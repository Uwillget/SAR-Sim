using System.Linq;
using System.Reflection;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public class RuntimeAnalyticsTests
    {
        [Test]
        public void CompletedRandomMissions_AreArchivedAsSeparateFullTracks()
        {
            var root = new GameObject("Runtime analytics test");
            try
            {
                RuntimeAnalytics analytics = root.AddComponent<RuntimeAnalytics>();
                analytics.Initialize(null, null);
                MethodInfo complete = typeof(RuntimeAnalytics).GetMethod("HandleMissionCompleted",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                AircraftState first = State("RANDOM-001", "VP1", "VP2", Vector3.zero);
                AircraftState second = State("RANDOM-001", "VP2", "VP3", Vector3.right * 50f);

                complete.Invoke(analytics, new object[] { first, Vector3.right * 100f });
                complete.Invoke(analytics, new object[] { second, Vector3.right * 160f });

                string[] ids = analytics.CompletedTrackIds.ToArray();
                Assert.That(ids, Is.EqualTo(new[] { "RANDOM-001#001", "RANDOM-001#002" }));
                Assert.That(analytics.CompletedTrackCount, Is.EqualTo(2));
                StringAssert.Contains("VP1 → VP2", analytics.GetTrackLabel(ids[0]));
                StringAssert.Contains("VP2 → VP3", analytics.GetTrackLabel(ids[1]));
                Assert.That(analytics.ShowTracks(ids), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Statistics_UsesCompletedMissionTotalInsteadOfDispatchCounter()
        {
            var root = new GameObject("Runtime statistics test");
            try
            {
                SimulationManager manager = root.AddComponent<SimulationManager>();
                manager.cumulTotalMissions = 44;
                manager.cumulSuccessCount = 103;
                manager.cumulFailCount = 0;
                RuntimeAnalytics analytics = root.AddComponent<RuntimeAnalytics>();
                analytics.Initialize(manager, null);

                string statistics = analytics.BuildStatistics();

                StringAssert.Contains("Completed missions   103", statistics);
                StringAssert.DoesNotContain("Completed missions   44", statistics);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void HistoricalLoad_ReplacesPreviousTrackCatalogue()
        {
            var root = new GameObject("Runtime historical analytics test");
            try
            {
                RuntimeAnalytics analytics = root.AddComponent<RuntimeAnalytics>();
                analytics.Initialize(null, null);

                analytics.ReplaceWithHistoricalTracks(new[]
                {
                    new[] { Vector3.zero, Vector3.right },
                    new[] { Vector3.zero, Vector3.up }
                }, "Run A");
                Assert.That(analytics.CompletedTrackCount, Is.EqualTo(2));

                string[] ids = analytics.ReplaceWithHistoricalTracks(new[]
                {
                    new[] { Vector3.one, Vector3.one * 2f }
                }, "Run B").ToArray();

                Assert.That(ids, Is.EqualTo(new[] { "Historical#001" }));
                Assert.That(analytics.CompletedTrackCount, Is.EqualTo(1));
                StringAssert.Contains("Run B", analytics.GetTrackLabel(ids[0]));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static AircraftState State(string aircraftId, string origin, string destination, Vector3 start)
        {
            return new AircraftState
            {
                AircraftId = aircraftId,
                Mission = new Flight { OriginVertiport = origin, DestinationVertiport = destination },
                MissionKeyNodes = new AircraftState.KeyNodes { OriginHelipad = start }
            };
        }
    }
}
