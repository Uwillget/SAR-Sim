using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;
using UnityEngine.TestTools;

namespace RoadNetworkEditor.Tests.Editor
{
    public class SimulationManagerCollisionAccountingTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        [Test]
        public void ContinuousPairOverlap_IsCountedOncePerEpisode()
        {
            GameObject managerObject = new GameObject("Collision accounting manager");
            GameObject aircraftA = new GameObject("Collision aircraft A");
            GameObject aircraftB = new GameObject("Collision aircraft B");
            managerObject.SetActive(false);
            aircraftA.SetActive(false);
            aircraftB.SetActive(false);
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;

            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                manager.AircraftObjects["A"] = aircraftA;
                manager.AircraftObjects["B"] = aircraftB;
                aircraftA.transform.position = Vector3.zero;
                aircraftB.transform.position = Vector3.right;

                Invoke(manager, "CheckAircraftCollisions");
                Invoke(manager, "CheckAircraftCollisions");
                Assert.That(manager.TotalCollisionEvents, Is.Zero,
                    "An overlap is finalized when it ends, not once per sampled frame.");

                aircraftB.transform.position = Vector3.right * 10f;
                Invoke(manager, "CheckAircraftCollisions");
                Invoke(manager, "CheckAircraftCollisions");
                Assert.That(manager.TotalCollisionEvents, Is.EqualTo(1));
                Assert.That(manager.cumulUnclassifiedCollisions, Is.EqualTo(1));
                Assert.That(manager.CollisionClassificationResidual, Is.Zero);

                aircraftB.transform.position = Vector3.right;
                Invoke(manager, "CheckAircraftCollisions");
                aircraftB.transform.position = Vector3.right * 10f;
                Invoke(manager, "CheckAircraftCollisions");
                Assert.That(manager.TotalCollisionEvents, Is.EqualTo(2),
                    "A new overlap after separation is a new collision episode.");
                Assert.That(manager.CollisionBreakdownTotal, Is.EqualTo(2));
                Assert.That(manager.CollisionClassificationResidual, Is.Zero);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
                Object.DestroyImmediate(aircraftB);
                Object.DestroyImmediate(aircraftA);
                Object.DestroyImmediate(managerObject);
            }
        }

        [TestCase("lane:L1", "lane:L1", "Lane")]
        [TestCase("approach:AP1", "apprNode=AP1 (matchDist=0.1)", "Approach")]
        [TestCase("ramp:R1", "vertex=V1 (matchDist=0.1)", "Ramp")]
        [TestCase("corridor:C1", "corridor:C1", "Corridor")]
        [TestCase("helipad=HP1 (matchDist=0.1)", "helipad=HP1 (matchDist=0.2)", "Helipad")]
        [TestCase("lane:L1", "pos:(0,0)", "Lane")]
        [TestCase("lane:L1", "corridor:C1", "Unclassified")]
        [TestCase("none", "pos:(0,0)", "Unclassified")]
        public void ResourcePair_ResolvesToOneExhaustiveCategory(
            string resourceA, string resourceB, string expectedCategory)
        {
            MethodInfo resolve = typeof(SimulationManager).GetMethod(
                "ResolveCollisionCategory", PrivateStatic);

            Assert.That(resolve, Is.Not.Null);
            object category = resolve.Invoke(null, new object[] { resourceA, resourceB });
            Assert.That(category.ToString(), Is.EqualTo(expectedCategory));
        }

        [Test]
        public void MissingStartMetadata_IsCountedOnceAsUnclassified()
        {
            GameObject managerObject = new GameObject("Missing collision metadata manager");
            managerObject.SetActive(false);
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;

            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                var activePairs = (HashSet<string>)typeof(SimulationManager)
                    .GetField("_activeCollisions", PrivateInstance)
                    .GetValue(manager);
                activePairs.Add("A<->B");

                Invoke(manager, "FinalizeActiveCollisions", "test boundary");
                Invoke(manager, "FinalizeActiveCollisions", "duplicate boundary");

                Assert.That(manager.TotalCollisionEvents, Is.EqualTo(1));
                Assert.That(manager.cumulUnclassifiedCollisions, Is.EqualTo(1));
                Assert.That(manager.CollisionBreakdownTotal, Is.EqualTo(1));
                Assert.That(manager.CollisionClassificationResidual, Is.Zero);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void RunReset_ClearsActivePairsAndAllCollisionCounters()
        {
            GameObject managerObject = new GameObject("Collision run reset manager");
            managerObject.SetActive(false);

            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                var activePairs = (HashSet<string>)typeof(SimulationManager)
                    .GetField("_activeCollisions", PrivateInstance)
                    .GetValue(manager);
                activePairs.Add("STALE-A<->STALE-B");
                manager.cumulLaneCollisions = 1;
                manager.cumulApproachCollisions = 1;
                manager.cumulRampCollisions = 1;
                manager.cumulCorridorCollisions = 1;
                manager.cumulHelipadCollisions = 1;
                manager.cumulUnclassifiedCollisions = 1;

                Invoke(manager, "ResetCollisionAccounting");

                Assert.That(activePairs, Is.Empty,
                    "An active pair from one batch must never leak into the next batch.");
                Assert.That(manager.TotalCollisionEvents, Is.Zero);
                Assert.That(manager.CollisionBreakdownTotal, Is.Zero);
                Assert.That(manager.CollisionClassificationResidual, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void CollisionCsv_RetainsLegacyRowsAndExportsVerifiableBreakdown()
        {
            GameObject managerObject = new GameObject("Collision CSV manager");
            managerObject.SetActive(false);

            try
            {
                SimulationManager manager = managerObject.AddComponent<SimulationManager>();
                manager.cumulLaneCollisions = 1;
                manager.cumulApproachCollisions = 2;
                manager.cumulRampCollisions = 3;
                manager.cumulCorridorCollisions = 4;
                manager.cumulHelipadCollisions = 5;
                manager.cumulUnclassifiedCollisions = 6;
                typeof(SimulationManager).GetProperty(nameof(SimulationManager.TotalCollisionEvents))
                    .GetSetMethod(true).Invoke(manager, new object[] { 21 });

                var writer = new StringWriter();
                Invoke(manager, "WriteCollisionMetrics", writer);
                string csv = writer.ToString();
                Dictionary<string, double> parsed =
                    SarSimulationHistory.ParseMetricsTableText(csv);

                Assert.That(parsed["Total Collisions"], Is.EqualTo(21));
                Assert.That(parsed["Lane Collisions"], Is.EqualTo(1));
                Assert.That(parsed["Approach Collisions"], Is.EqualTo(2));
                Assert.That(parsed["Ramp Collisions"], Is.EqualTo(3));
                Assert.That(parsed["Corridor Collisions"], Is.EqualTo(4));
                Assert.That(parsed["Helipad Collisions"], Is.EqualTo(5));
                Assert.That(parsed["Unclassified / Mixed-Resource Collisions"], Is.EqualTo(6));
                Assert.That(parsed["Collision Classification Residual"], Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
            }
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, PrivateInstance);
            Assert.That(method, Is.Not.Null, $"Expected private method {methodName}.");
            return method.Invoke(target, arguments);
        }
    }
}
