using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace RoadNetworkEditor.Tests
{
    public class SimulationParameterCatalogTests
    {
        [Test]
        public void Catalog_MapsEveryDescriptorToAUniqueConfigField()
        {
            Assert.That(SimulationParameterCatalog.All.Count, Is.GreaterThanOrEqualTo(20));
            Assert.That(SimulationParameterCatalog.All.Select(item => item.FieldName).Distinct().Count(),
                Is.EqualTo(SimulationParameterCatalog.All.Count));
            foreach (SimulationParameterDescriptor descriptor in SimulationParameterCatalog.All)
            {
                Assert.That(descriptor.ValueType,
                    Is.EqualTo(typeof(float)).Or.EqualTo(typeof(int)), descriptor.FieldName);
                Assert.That(descriptor.Minimum, Is.LessThan(descriptor.Maximum), descriptor.FieldName);
            }
        }

        [Test]
        public void Catalog_AppliesValidatedFloatAndIntegerValues()
        {
            SimulationConfig config = ScriptableObject.CreateInstance<SimulationConfig>();
            try
            {
                Assert.That(SimulationParameterCatalog.TryGet("maxSpeed", out SimulationParameterDescriptor speed),
                    Is.True);
                Assert.That(SimulationParameterCatalog.TrySetFromText(config, speed, "42.5", out _), Is.True);
                Assert.That(config.maxSpeed, Is.EqualTo(42.5f).Within(0.001f));

                Assert.That(SimulationParameterCatalog.TrySetNumeric(config, "maxConcurrentAircraft", 12.6f,
                    out _), Is.True);
                Assert.That(config.maxConcurrentAircraft, Is.EqualTo(13));
                Assert.That(SimulationParameterCatalog.TrySetFromText(config, speed, "-1", out string error),
                    Is.False);
                Assert.That(error, Does.Contain("within"));
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void Sweep_StartResolvesGridWithoutInspectorSideEffects()
        {
            GameObject root = new GameObject("Sweep test");
            SimulationConfig config = ScriptableObject.CreateInstance<SimulationConfig>();
            try
            {
                SimulationManager manager = root.AddComponent<SimulationManager>();
                manager.config = config;
                ParameterSweepRunner runner = root.AddComponent<ParameterSweepRunner>();
                runner.sim = manager;
                runner.batchDuration = 10f;
                runner.variables.Clear();
                runner.variables.Add(new ParameterSweepRunner.SweepVariable
                    { configField = "signalPropagationDelay", startValue = 0f, step = 0.5f, count = 3 });
                runner.variables.Add(new ParameterSweepRunner.SweepVariable
                    { configField = "positioningErrorStdDev", startValue = 0f, step = 1f, count = 2 });

                Assert.That(runner.totalBatches, Is.EqualTo(0));
                Assert.That(runner.StartSweep(), Is.True, runner.lastError);
                Assert.That(runner.totalBatches, Is.EqualTo(6));
                Assert.That(runner.isRunning, Is.True);
                runner.StopSweepInternal();
                Assert.That(runner.isRunning, Is.False);

                runner.variables[1].configField = "signalPropagationDelay";
                LogAssert.Expect(LogType.Error,
                    "[Sweep] Sweep variable 'signalPropagationDelay' is enabled more than once.");
                Assert.That(runner.StartSweep(), Is.False);
                Assert.That(runner.lastError, Does.Contain("more than once"));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(config);
            }
        }
    }
}
