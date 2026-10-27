using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;
using UnityEngine.UI;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SarRuntimeInterlockTests
    {
        [Test]
        public void RunningSimulation_GuardsWorkspaceNavigationAndKeepsHeatmapModesIndependent()
        {
            var dependencyRoot = new GameObject("Runtime interlock dependencies");
            var root = new GameObject("Runtime interlock workbench");
            dependencyRoot.SetActive(false);
            root.SetActive(false);
            try
            {
                SimulationManager manager = dependencyRoot.AddComponent<SimulationManager>();
                ParameterSweepRunner sweep = dependencyRoot.AddComponent<ParameterSweepRunner>();
                sweep.sim = manager;

                SarRuntimeWorkbench workbench = root.AddComponent<SarRuntimeWorkbench>();
                RuntimeAirspaceVisualizer visualizer = root.AddComponent<RuntimeAirspaceVisualizer>();
                RuntimeAirspaceEditor editor = root.AddComponent<RuntimeAirspaceEditor>();
                RuntimeRouteDesigner routeDesigner = root.AddComponent<RuntimeRouteDesigner>();
                RuntimeAnalytics analytics = root.AddComponent<RuntimeAnalytics>();
                analytics.Initialize(null, visualizer);

                Set(workbench, "manager", manager);
                Set(workbench, "sweepRunner", sweep);
                Set(workbench, "visualizer", visualizer);
                Set(workbench, "editor", editor);
                Set(workbench, "routeDesigner", routeDesigner);
                Set(workbench, "analytics", analytics);

                var pages = new GameObject[4];
                var navigation = new Button[4];
                for (int index = 0; index < pages.Length; index++)
                {
                    pages[index] = new GameObject("Page " + index);
                    pages[index].transform.SetParent(root.transform, false);
                    navigation[index] = Button(root.transform, "Navigation " + index);
                }
                Set(workbench, "pages", pages);
                Set(workbench, "navigationButtons", navigation);

                Button sceneData = Button(root.transform, "Scene Data");
                Button newScene = Button(root.transform, "New Scene");
                Button sceneTab = Button(root.transform, "Scene Tab");
                Button config = Button(root.transform, "Configuration");
                Button start = Button(root.transform, "Start");
                Button stop = Button(root.transform, "Stop");
                Set(workbench, "sceneDataButton", sceneData);
                Set(workbench, "newSceneButton", newScene);
                Set(workbench, "singleSimulationStartButton", start);
                Set(workbench, "singleSimulationStopButton", stop);
                ((List<Button>)Get(workbench, "sceneTabActionButtons")).Add(sceneTab);
                ((List<Selectable>)Get(workbench, "simulationConfigurationControls")).Add(config);

                MethodInfo showPage = Method("ShowPage");
                MethodInfo applyInterlock = Method("ApplySimulationInterlock");
                MethodInfo setDataHeat = Method("SetDataVisualizationHeatmapEnabled");
                MethodInfo setLiveHeat = Method("SetLiveTrafficHeatmapEnabled");
                MethodInfo setLaneMode = Method("SetRuntimeLaneColorMode");
                FieldInfo simulationRunning = typeof(SimulationManager).GetField(
                    "simulationRunning", BindingFlags.Instance | BindingFlags.NonPublic);

                simulationRunning.SetValue(manager, true);
                showPage.Invoke(workbench, new object[] { 0 });

                Assert.That((int)Get(workbench, "currentPageIndex"), Is.EqualTo(2));
                Assert.That(pages[2].activeSelf, Is.True);
                Assert.That(pages[0].activeSelf, Is.False);
                Assert.That(navigation[2].interactable, Is.True);
                Assert.That(navigation[0].interactable, Is.False);
                Assert.That(sceneData.interactable, Is.False);
                Assert.That(newScene.interactable, Is.False);
                Assert.That(sceneTab.interactable, Is.False);
                Assert.That(config.interactable, Is.False);
                Assert.That(start.interactable, Is.False);
                Assert.That(stop.interactable, Is.True);

                // A rejected request is a real ShowPage guard, not merely a disabled button.
                showPage.Invoke(workbench, new object[] { 3 });
                Assert.That((int)Get(workbench, "currentPageIndex"), Is.EqualTo(2));

                simulationRunning.SetValue(manager, false);
                applyInterlock.Invoke(workbench, new object[] { true });
                Assert.That(navigation.All(button => button.interactable), Is.True);
                Assert.That(sceneData.interactable, Is.True);
                Assert.That(config.interactable, Is.True);

                showPage.Invoke(workbench, new object[] { 3 });
                setDataHeat.Invoke(workbench, new object[] { true, false });
                Assert.That(visualizer.IsVisible(AirspaceVisualLayer.Heatmap), Is.True);

                // Leaving Data Visualization restores connection-attribute colors by default.
                showPage.Invoke(workbench, new object[] { 2 });
                Assert.That(visualizer.IsVisible(AirspaceVisualLayer.Heatmap), Is.False);

                // Live load is an explicit lane-line mode, never the archived Heatmap layer.
                setLiveHeat.Invoke(workbench, new object[] { true });
                Assert.That(visualizer.IsVisible(AirspaceVisualLayer.Heatmap), Is.False);
                Assert.That(visualizer.LaneColorMode, Is.EqualTo(RuntimeLaneColorMode.LiveAircraftLoad));
                simulationRunning.SetValue(manager, true);
                applyInterlock.Invoke(workbench, new object[] { true });
                Assert.That(visualizer.IsVisible(AirspaceVisualLayer.Heatmap), Is.False);
                Assert.That(visualizer.LaneColorMode, Is.EqualTo(RuntimeLaneColorMode.LiveAircraftLoad));

                // Selecting passability replaces live load (there is one mode, not two overlays).
                setLaneMode.Invoke(workbench,
                    new object[] { RuntimeLaneColorMode.Passability, true });
                Assert.That(visualizer.LaneColorMode, Is.EqualTo(RuntimeLaneColorMode.Passability));
                simulationRunning.SetValue(manager, false);
                applyInterlock.Invoke(workbench, new object[] { true });
                Assert.That(visualizer.IsVisible(AirspaceVisualLayer.Heatmap), Is.False);

                // Other workspaces restore authored lane-type colors, then returning to
                // Simulation restores the user's selected runtime mode.
                showPage.Invoke(workbench, new object[] { 3 });
                Assert.That(visualizer.LaneColorMode, Is.EqualTo(RuntimeLaneColorMode.LaneType));
                showPage.Invoke(workbench, new object[] { 2 });
                Assert.That(visualizer.LaneColorMode, Is.EqualTo(RuntimeLaneColorMode.Passability));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(dependencyRoot);
            }
        }

        private static MethodInfo Method(string name) => typeof(SarRuntimeWorkbench).GetMethod(
            name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static object Get(SarRuntimeWorkbench target, string name) =>
            typeof(SarRuntimeWorkbench).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private static void Set(SarRuntimeWorkbench target, string name, object value) =>
            typeof(SarRuntimeWorkbench).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static Button Button(Transform parent, string name)
        {
            var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Button));
            gameObject.transform.SetParent(parent, false);
            Button button = gameObject.GetComponent<Button>();
            button.targetGraphic = gameObject.GetComponent<Image>();
            return button;
        }
    }
}
