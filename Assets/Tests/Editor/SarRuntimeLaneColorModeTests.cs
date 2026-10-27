using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SarRuntimeLaneColorModeTests
    {
        [Test]
        public void LiveAircraftCount_ExcludesSyntheticStopLineOccupants()
        {
            var state = new LaneState
            {
                AircraftsInLane = new Dictionary<string, float>
                {
                    ["Aircraft-1"] = 0.4f,
                    ["Aircraft-2"] = 0.8f,
                    ["__VIRTUAL__"] = 0.95f
                },
                SignalCheck = true
            };

            Assert.That(RuntimeAirspaceVisualizer.CountLiveAircraft(state), Is.EqualTo(2));
        }

        [Test]
        public void LiveLoadPalette_UsesGrayForZeroAndGreenToRedForPositiveDynamicRange()
        {
            Color zero = RuntimeAirspaceVisualizer.SampleLiveAircraftLoadColor(0, 4);
            Color low = RuntimeAirspaceVisualizer.SampleLiveAircraftLoadColor(1, 4);
            Color high = RuntimeAirspaceVisualizer.SampleLiveAircraftLoadColor(4, 4);

            Assert.That(Mathf.Abs(zero.r - zero.g), Is.LessThan(0.04f));
            Assert.That(Mathf.Abs(zero.g - zero.b), Is.LessThan(0.04f));
            Assert.That(low.g, Is.GreaterThan(low.r));
            Assert.That(high.r, Is.GreaterThan(high.g));
        }

        [Test]
        public void PassabilityPalette_MapsOpenToGreenAndEitherBlockToRed()
        {
            var open = new LaneState();
            var signalBlocked = new LaneState { SignalCheck = true };
            var capacityBlocked = new LaneState { CapabilityCheck = true };

            Assert.That(RuntimeAirspaceVisualizer.IsLaneBlocked(open), Is.False);
            Assert.That(RuntimeAirspaceVisualizer.IsLaneBlocked(signalBlocked), Is.True);
            Assert.That(RuntimeAirspaceVisualizer.IsLaneBlocked(capacityBlocked), Is.True);
            Color green = RuntimeAirspaceVisualizer.SamplePassabilityColor(false);
            Color red = RuntimeAirspaceVisualizer.SamplePassabilityColor(true);
            Assert.That(green.g, Is.GreaterThan(green.r));
            Assert.That(red.r, Is.GreaterThan(red.g));
        }

        [Test]
        public void LaneTypeMode_IgnoresBlockedStateAndRetainsAuthoredTypeColor()
        {
            var managerRoot = new GameObject("Lane mode manager");
            var visualizerRoot = new GameObject("Lane mode visualizer");
            managerRoot.SetActive(false);
            visualizerRoot.SetActive(false);
            try
            {
                SimulationManager manager = managerRoot.AddComponent<SimulationManager>();
                RuntimeAirspaceVisualizer visualizer = visualizerRoot.AddComponent<RuntimeAirspaceVisualizer>();
                var lane = new Lane { LaneId = "L1", LaneType = "Left" };
                manager.LaneStates[lane.LaneId] = new LaneState
                {
                    LaneId = lane.LaneId,
                    SignalCheck = true,
                    AircraftsInLane = new Dictionary<string, float> { ["__VIRTUAL__"] = 1f }
                };
                typeof(RuntimeAirspaceVisualizer).GetField("manager",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(visualizer, manager);
                visualizer.SetLaneColorMode(RuntimeLaneColorMode.LaneType);

                MethodInfo runtimeColor = typeof(RuntimeAirspaceVisualizer).GetMethod(
                    "RuntimeLaneColor", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo authoredColor = typeof(RuntimeAirspaceVisualizer).GetMethod(
                    "LaneColor", BindingFlags.Static | BindingFlags.NonPublic);
                Color actual = (Color)runtimeColor.Invoke(visualizer, new object[] { lane, 1 });
                Color expected = (Color)authoredColor.Invoke(null, new object[] { lane.LaneType });

                Assert.That(actual, Is.EqualTo(expected));
            }
            finally
            {
                Object.DestroyImmediate(visualizerRoot);
                Object.DestroyImmediate(managerRoot);
            }
        }
    }
}
