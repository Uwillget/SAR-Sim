using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public class SimulationManagerMissionLifecycleTests
    {
        [Test]
        public void FileMissionCompletion_RemovesInstanceAndLiveOccupancy()
        {
            var managerObject = new GameObject("File lifecycle manager");
            var aircraftObject = new GameObject("Completed file aircraft");
            managerObject.SetActive(false);
            aircraftObject.SetActive(false);

            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                FlightController controller = aircraftObject.AddComponent<FlightController>();
                AircraftAgent agent = aircraftObject.AddComponent<AircraftAgent>();
                agent.controller = controller;
                agent.dispatchCenter = manager;
                agent.aircraftState = new AircraftState
                {
                    AircraftId = "FILE-001",
                    MissionPhase = "END",
                    State = "SUCCESS"
                };
                agent.hasMissionCompleted = true;

                manager.AircraftObjects["FILE-001"] = aircraftObject;
                manager.AircraftStates["FILE-001"] = agent.aircraftState;
                manager.LaneStates["L1"] = new LaneState();
                manager.LaneStates["L1"].AircraftsInLane["FILE-001"] = 0.5f;
                manager.CorridorStatesDict["C1"] = new CorridorState();
                manager.CorridorStatesDict["C1"].AircraftsInCorridor["FILE-001"] = 0.4f;
                manager.HelipadStatesDict["HP1"] = new HelipadState
                {
                    OccupiedTimeWindows = new List<OccupiedTimeWindow>
                    {
                        new OccupiedTimeWindow { OccupiedAircraftId = "FILE-001", StartTime = 0f, EndTime = 50f }
                    }
                };
                manager.OccupiedHelipads.Add("HP1");
                manager.AircraftOriginHP["FILE-001"] = "HP1";

                InvokePrivate(manager, "ProcessCompletedFileMissions");

                Assert.That(manager.AircraftObjects.ContainsKey("FILE-001"), Is.False);
                Assert.That(manager.AircraftStates.ContainsKey("FILE-001"), Is.True,
                    "Completed history must remain available to metrics.");
                Assert.That(manager.LaneStates["L1"].AircraftsInLane.ContainsKey("FILE-001"), Is.False);
                Assert.That(manager.CorridorStatesDict["C1"].AircraftsInCorridor.ContainsKey("FILE-001"), Is.False);
                Assert.That(manager.HelipadStatesDict["HP1"].OccupiedTimeWindows, Is.Empty);
                Assert.That(manager.OccupiedHelipads.Contains("HP1"), Is.False);
                Assert.That(aircraftObject == null, Is.True);
            }
            finally
            {
                if (aircraftObject != null) Object.DestroyImmediate(aircraftObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void RandomMissionCompletion_WaitsForConfiguredTurnaround()
        {
            var managerObject = new GameObject("Random lifecycle manager");
            var aircraftObject = new GameObject("Parked random aircraft");
            managerObject.SetActive(false);
            aircraftObject.SetActive(false);
            SimulationConfig config = ScriptableObject.CreateInstance<SimulationConfig>();

            try
            {
                config.missionMode = MissionGenerationMode.RandomContinuous;
                config.randomTurnaroundTime = 30f;
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                manager.config = config;
                AircraftAgent agent = aircraftObject.AddComponent<AircraftAgent>();
                agent.aircraftState = new AircraftState
                {
                    AircraftId = "RANDOM-001",
                    MissionPhase = "END",
                    State = "SUCCESS"
                };
                agent.hasMissionCompleted = true;
                manager.AircraftObjects["RANDOM-001"] = aircraftObject;

                InvokePrivate(manager, "ProcessCompletedMissions");

                Assert.That(agent.hasMissionCompleted, Is.True,
                    "Aircraft must remain parked during its turnaround dwell.");
                var completedAt = (Dictionary<string, float>)typeof(SimulationManager)
                    .GetField("completedMissionAt", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(manager);
                Assert.That(completedAt.ContainsKey("RANDOM-001"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(config);
                Object.DestroyImmediate(aircraftObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void DurationLimit_AppliesOnlyToRandomContinuousMode()
        {
            var managerObject = new GameObject("Duration limit manager");
            managerObject.SetActive(false);
            SimulationConfig config = ScriptableObject.CreateInstance<SimulationConfig>();
            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                manager.config = config;
                manager.maxSimDuration = 120f;
                typeof(SimulationManager).GetField("cumulSimTime",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(manager, 120f);
                MethodInfo check = typeof(SimulationManager).GetMethod("HasReachedRandomDurationLimit",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                config.missionMode = MissionGenerationMode.JSON;
                Assert.That((bool)check.Invoke(manager, null), Is.False,
                    "File-driven demand must be allowed to complete regardless of the random duration limit.");

                config.missionMode = MissionGenerationMode.RandomContinuous;
                Assert.That((bool)check.Invoke(manager, null), Is.True);

                manager.maxSimDuration = -1f;
                Assert.That((bool)check.Invoke(manager, null), Is.False,
                    "Non-positive duration means unlimited random operation.");
            }
            finally
            {
                Object.DestroyImmediate(config);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void CollisionPause_RaisesFocusRequestAndStopsTime()
        {
            var managerObject = new GameObject("Collision pause manager");
            managerObject.SetActive(false);
            float originalTimeScale = Time.timeScale;

            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                Vector3 receivedPoint = Vector3.zero;
                string receivedPair = null;
                manager.CollisionPauseRequested += (point, pair) =>
                {
                    receivedPoint = point;
                    receivedPair = pair;
                };

                Vector3 collisionPoint = new Vector3(12f, 75f, -8f);
                typeof(SimulationManager).GetMethod("PauseForCollision",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, new object[] { "A<->B", collisionPoint });

                Assert.That(Time.timeScale, Is.EqualTo(0f));
                Assert.That(manager.IsCollisionPaused, Is.True);
                Assert.That(receivedPair, Is.EqualTo("A<->B"));
                Assert.That(receivedPoint, Is.EqualTo(collisionPoint));
                manager.ResumeAfterCollisionPause();
                Assert.That(manager.IsCollisionPaused, Is.False);
            }
            finally
            {
                Time.timeScale = originalTimeScale;
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void RandomFleetCounts_AlwaysPartitionConfiguredAircraftIntoActiveAndPending()
        {
            var managerObject = new GameObject("Fleet status manager");
            managerObject.SetActive(false);
            SimulationConfig config = ScriptableObject.CreateInstance<SimulationConfig>();
            var aircraftObjects = new List<GameObject>();
            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                manager.Vertiports.Clear();
                for (int vpIndex = 1; vpIndex <= 2; vpIndex++)
                {
                    var vertiport = new Vertiport { VertiportId = "VP" + vpIndex };
                    for (int hpIndex = 1; hpIndex <= 4; hpIndex++)
                        vertiport.Helipads.Add(new Helipad
                            { HelipadId = $"VP{vpIndex}-HP{hpIndex}" });
                    manager.Vertiports[vertiport.VertiportId] = vertiport;
                }
                config.missionMode = MissionGenerationMode.RandomContinuous;
                config.maxConcurrentAircraft = 3;
                manager.config = config;

                manager.GetOperationalAircraftCounts(out int active, out int pending, out int total);
                Assert.That((active, pending, total), Is.EqualTo((0, 3, 3)));

                foreach ((string id, string phase) in new[]
                         {
                             ("ACTIVE-1", "CRUISE"), ("WAITING-1", "WAITING"), ("PARKED-1", "END")
                         })
                {
                    var aircraft = new GameObject(id);
                    aircraft.SetActive(false);
                    aircraft.AddComponent<AircraftAgent>().aircraftState = new AircraftState
                        { AircraftId = id, MissionPhase = phase };
                    manager.AircraftObjects[id] = aircraft;
                    aircraftObjects.Add(aircraft);
                }

                manager.GetOperationalAircraftCounts(out active, out pending, out total);
                Assert.That((active, pending, total), Is.EqualTo((1, 2, 3)));
                Assert.That(active + pending, Is.EqualTo(config.maxConcurrentAircraft));
            }
            finally
            {
                foreach (GameObject aircraft in aircraftObjects) Object.DestroyImmediate(aircraft);
                Object.DestroyImmediate(config);
                Object.DestroyImmediate(managerObject);
            }
        }

        private static void InvokePrivate(SimulationManager manager, string methodName)
        {
            typeof(SimulationManager).GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, null);
        }
    }
}
