using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SimulationRecorderTrajectoryTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase("START", true)]
        [TestCase("TAKEOFF", true)]
        [TestCase("CRUISE", true)]
        [TestCase("LANDING", true)]
        [TestCase("WAITING", false)]
        [TestCase("END", false)]
        [TestCase(null, false)]
        public void RecorderOnlySamplesActiveMissionPhases(string phase, bool expected)
        {
            MethodInfo method = typeof(SimulationRecorder).GetMethod(
                "IsTrackableMissionPhase", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Assert.That((bool)method.Invoke(null, new object[] { phase }), Is.EqualTo(expected));
        }

        [Test]
        public void ReusedAircraftId_ProducesSeparateCompletedMissionTracks()
        {
            var managerObject = new GameObject("Recorder trajectory manager");
            var recorderObject = new GameObject("Recorder trajectory service");
            managerObject.SetActive(false);
            recorderObject.SetActive(false);
            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                SimulationRecorder recorder = recorderObject.AddComponent<SimulationRecorder>();
                recorder.StartRecording(manager);

                var first = new AircraftState
                {
                    AircraftId = "AC-1",
                    State = "SUCCESS",
                    Mission = new Flight { OriginVertiport = "VP-A", DestinationVertiport = "VP-B" }
                };
                manager.NotifyMissionCompleted(first, Vector3.right);
                var second = new AircraftState
                {
                    AircraftId = "AC-1",
                    State = "SUCCESS",
                    Mission = new Flight { OriginVertiport = "VP-B", DestinationVertiport = "VP-C" }
                };
                manager.NotifyMissionCompleted(second, Vector3.forward);

                SimulationRecording recording = (SimulationRecording)typeof(SimulationRecorder)
                    .GetField("recording", PrivateInstance).GetValue(recorder);
                Assert.That(recording.AircraftTrajectories, Has.Count.EqualTo(2));
                Assert.That(recording.AircraftTrajectories[0].TrackId, Is.EqualTo("AC-1#001"));
                Assert.That(recording.AircraftTrajectories[1].TrackId, Is.EqualTo("AC-1#002"));
                Assert.That(recording.AircraftTrajectories.TrueForAll(
                    item => item.MissionCompleted == true), Is.True);
                Assert.That(recording.AircraftTrajectories[0].OriginVertiport, Is.EqualTo("VP-A"));
                Assert.That(recording.AircraftTrajectories[1].DestinationVertiport, Is.EqualTo("VP-C"));

                // Keep cleanup read-only: OnDestroy must detach the event without writing
                // a test recording into the project's normal Output directory.
                typeof(SimulationRecorder).GetField("isRecording", PrivateInstance)
                    .SetValue(recorder, false);
            }
            finally
            {
                Object.DestroyImmediate(recorderObject);
                Object.DestroyImmediate(managerObject);
                typeof(SimulationRecorder).GetField("<Instance>k__BackingField",
                        BindingFlags.Static | BindingFlags.NonPublic)
                    ?.SetValue(null, null);
            }
        }

        [Test]
        public void MissingLandingTrajectory_UsesSharedSuccessfulCompletionPath()
        {
            var managerObject = new GameObject("Landing terminal manager");
            var aircraftObject = new GameObject("Landing terminal aircraft");
            managerObject.SetActive(false);
            aircraftObject.SetActive(false);
            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                FlightController controller = aircraftObject.AddComponent<FlightController>();
                AircraftAgent agent = aircraftObject.AddComponent<AircraftAgent>();
                agent.dispatchCenter = manager;
                agent.controller = controller;
                agent.aircraftState = new AircraftState
                {
                    AircraftId = "AC-terminal",
                    State = "ACTIVE",
                    MissionPhase = "CRUISE",
                    LaneSequence = new System.Collections.Generic.List<Lane>(),
                    LandingTrajectory = null
                };
                bool notified = false;
                manager.MissionCompleted += (_, __) => notified = true;

                agent.HandlePhaseTransition();
                Assert.That(agent.aircraftState.MissionPhase, Is.EqualTo("LANDING"),
                    "The no-trajectory branch must not bypass shared terminal bookkeeping.");
                Assert.That(agent.hasMissionCompleted, Is.False);

                agent.HandlePhaseTransition();
                Assert.That(agent.aircraftState.MissionPhase, Is.EqualTo("END"));
                Assert.That(agent.aircraftState.State, Is.EqualTo("SUCCESS"));
                Assert.That(agent.hasMissionCompleted, Is.True);
                Assert.That(manager.cumulSuccessCount, Is.EqualTo(1));
                Assert.That(notified, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(aircraftObject);
                Object.DestroyImmediate(managerObject);
            }
        }
    }
}
