using System.IO;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SarRuntimeLocalizationTests
    {
        [Test]
        public void EnglishLocale_LeavesSourceUiTextUnchanged()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.DefaultLocale);
                Assert.AreEqual("Routes & Flow", localization.Translate("Routes & Flow"));
                Assert.AreEqual("Review Result Table", localization.Translate("Review Result Table"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChineseLocale_TranslatesStaticUiAtSource()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);
                Assert.AreEqual("仿真数据可视化", localization.Translate("Simulation Data Visualization"));
                Assert.AreEqual("参数敏感性图", localization.Translate("Parameter Sensitivity Plot"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void UserAuthoredLayerName_IsNeverPhraseTranslated()
        {
            GameObject root = new GameObject("LocalizationLiteralTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);
                string source = "Simulation / " + SarRuntimeLocalization.Literal("Simulation");
                Assert.AreEqual("仿真运行 / Simulation", localization.Translate(source),
                    "The same word is UI copy in the first position and user-authored data in the second.");

                localization.ConfigureForTests(SarRuntimeLocalization.DefaultLocale, null);
                Assert.AreEqual("Layer: Default Layer",
                    localization.Translate("Layer: " + SarRuntimeLocalization.Literal("Default Layer")),
                    "Literal markers must also be removed in the English release.");

                localization.ConfigureForTests(SarRuntimeLocalization.ChineseLocale,
                    LoadChineseCatalog());
                string renderedNotice = localization.Translate("Traffic-heatmap layer ") +
                                        "Lower Network Layer" +
                                        localization.Translate(": Visible. ");
                string renderedFooter = localization.Translate(
                    SarRuntimeLocalization.Literal(renderedNotice) + " Active 0");
                StringAssert.Contains("Lower Network Layer", renderedFooter,
                    "A second footer-localization pass must preserve the authored name verbatim.");
                StringAssert.DoesNotContain("下层路网", renderedFooter);
                StringAssert.Contains("活动 0", renderedFooter,
                    "Static footer counters must still be localized around a literal notice.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChineseLocale_TranslatesEveryDynamicAssignmentSynchronously()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);

                string history = localization.Translate(
                    "Scene: Alpha\nRecords: 2 · Selected: 1 explicit items / 25 analysis runs · Comparable: 25 runs\n" +
                    "3D target: Run 03/25 · 900 s · Completed");
                StringAssert.Contains("场景：Alpha", history);
                StringAssert.Contains("记录：2", history);
                StringAssert.Contains("已选择：1 个显式条目 / 25 个分析运行", history);
                StringAssert.Contains("可比较：25 个运行", history);
                StringAssert.Contains("三维目标：第 03/25 · 900 秒 · 已完成", history);

                Assert.AreEqual("● 仿真运行中", localization.Translate("● Running"));
                StringAssert.Contains("层间连接走廊", localization.Translate("Interlayer corridors        4"));
                string footer = localization.Translate(
                    "Active 2    Pending 3    Succeeded 4    Failed 1    Collisions 0");
                StringAssert.Contains("活动 2", footer);
                StringAssert.Contains("待执行 3", footer);
                StringAssert.Contains("成功 4", footer);
                StringAssert.Contains("失败 1", footer);
                StringAssert.Contains("碰撞 0", footer);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChineseLocale_TranslatesLayerSceneAndViewLabels()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);
                Assert.AreEqual("● 城市建筑", localization.Translate("● City Buildings"));
                Assert.AreEqual("● 空中路网", localization.Translate("● Air Network"));
                Assert.AreEqual("● 地面进近", localization.Translate("● Ground Approach"));
                Assert.AreEqual("● 等待结构", localization.Translate("● Holding Structures"));
                Assert.AreEqual("● 起降场", localization.Translate("● Vertiports"));
                Assert.AreEqual("● 起降场走廊", localization.Translate("● Vertiport Corridors"));
                Assert.AreEqual("● 层间走廊", localization.Translate("● Interlayer Corridors"));
                Assert.AreEqual("● 交叉口信号", localization.Translate("● Intersection Signals"));
                Assert.AreEqual("≡  ●  下层路网 · 75 m",
                    localization.Translate("≡  ●  Lower Network Layer · 75 m"));
                Assert.AreEqual("≡  ○  上层路网 · 120 m",
                    localization.Translate("≡  ○  Upper Network Layer · 120 m"));
                Assert.AreEqual("默认研究场景", localization.Translate("Default Research Scene"));
                Assert.AreEqual("当前：默认研究场景",
                    localization.Translate("Active: Default Research Scene"));
                Assert.AreEqual("自由视图", localization.Translate("Free View"));
                Assert.AreEqual("透视", localization.Translate("Perspective"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChineseCatalog_CoversSimulationPanelAndParameterCatalog()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);
                string[] requiredUi =
                {
                    "Runtime Parameters", "Parameter Controls", "Aircraft Dynamics",
                    "Reservation System", "Traffic Control", "Information Uncertainty",
                    "Turnaround Time After Arrival (s)",
                    "Maximum Simulation Duration (s; 0 = unlimited)",
                    "Set up batch experiment", "Pause and focus automatically on collision",
                    "Show aircraft trails"
                };
                foreach (string source in requiredUi)
                    Assert.AreNotEqual(source, localization.Translate(source),
                        "Missing zh-CN UI translation: " + source);

                foreach (SimulationParameterDescriptor descriptor in SimulationParameterCatalog.All)
                {
                    Assert.AreNotEqual(descriptor.DisplayName, localization.Translate(descriptor.DisplayName),
                        "Missing zh-CN parameter label: " + descriptor.DisplayName);
                    Assert.AreNotEqual(descriptor.FieldName, localization.Translate(descriptor.FieldName),
                        "Missing zh-CN sweep-option label: " + descriptor.FieldName);
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChineseCatalog_TranslatesCompositeFoldoutsAnalysisLayersAndViewerControls()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);

                Assert.AreEqual("▾  参数控制", localization.Translate("▾  Parameter Controls"));
                Assert.AreEqual("▸  飞行器动力学", localization.Translate("▸  Aircraft Dynamics"));
                Assert.AreEqual("▸  预约系统", localization.Translate("▸  Reservation System"));
                Assert.AreEqual("▸  交通管制", localization.Translate("▸  Traffic Control"));
                Assert.AreEqual("▸  信息不确定性", localization.Translate("▸  Information Uncertainty"));
                Assert.AreEqual("● 交通热力图", localization.Translate("● Traffic Heatmap"));
                Assert.AreEqual("● 路径 / 轨迹", localization.Translate("● Routes / Trajectories"));

                string[] viewerControls =
                {
                    "Zoom In", "Zoom Out", "Reset View", "Fit to Window", "Actual Size",
                    "Export Image", "Export PNG", "Previous Page", "Next Page", "Page", "Go",
                    "Previous Figure", "Next Figure", "Analysis Figure"
                };
                foreach (string source in viewerControls)
                    Assert.AreNotEqual(source, localization.Translate(source),
                        "Missing zh-CN chart/table viewer translation: " + source);

                Assert.AreEqual("页码 3 / 20", localization.Translate("Page 3 / 20"));
                Assert.AreEqual("图 3/9", localization.Translate("Figure 3/9"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChineseCatalog_ExactAndPhraseOverlapsNeverDisagree()
        {
            SarRuntimeLocalizationCatalog catalog = LoadChineseCatalog();
            foreach (var pair in catalog.Exact)
            {
                if (!catalog.Phrases.TryGetValue(pair.Key, out string phraseValue)) continue;
                Assert.AreEqual(pair.Value, phraseValue,
                    "Conflicting exact/phrase translation for: " + pair.Key);
            }
        }

        [Test]
        public void ChineseCatalog_CoversAnalysisExportPaginationAndHeatmapOverlayStrings()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);

                Assert.AreEqual("导出分析图像", localization.Translate("Export Analysis Image"));
                Assert.AreEqual("请先打开图表或结果表页面，再导出图像。 ",
                    localization.Translate("Open a chart or result-table page before exporting an image. "));
                StringAssert.StartsWith("分析图像已导出：",
                    localization.Translate("Analysis image exported: C:/analysis.png "));
                StringAssert.StartsWith("结果表图像已导出：",
                    localization.Translate("Result-table image exported: C:/table.png "));
                StringAssert.StartsWith("图像导出失败：",
                    localization.Translate("Image export failed: write denied "));
                Assert.AreEqual("当前没有可导出的已生成分析图像。 ",
                    localization.Translate("No generated analysis image is available to export. "));
                Assert.AreEqual("载入归档三维结果前，请先停止当前单次或批量仿真。 ",
                    localization.Translate(
                        "Stop the current single or batch simulation before loading an archived 3D result. "));

                Assert.AreEqual("归档警告：missing MetricsTable.csv",
                    localization.Translate("Archive warnings: missing MetricsTable.csv"));
                Assert.AreEqual("批次 3/9", localization.Translate("Batch 3/9"));
                Assert.AreEqual("页码 4/20", localization.Translate("Page 4/20"));
                StringAssert.Contains("每页显示一个实验",
                    localization.Translate("20 page(s) · 1 batch summary table(s) · one experiment per page"));
                StringAssert.StartsWith("请输入 1 至 20",
                    localization.Translate("Enter a page number from 1 to 20. "));

                Assert.AreEqual("车道通行次数  0 – 32",
                    localization.Translate("Lane traversals  0 – 32"));
                Assert.AreEqual("预约时长（秒）  0 – 34.47",
                    localization.Translate("Reservation time (s)  0 – 34.47"));
                Assert.AreEqual("轨迹", localization.Translate("Trajectory"));

                Assert.AreEqual("▸  任务完成情况", localization.Translate("▸  Mission Outcomes"));
                Assert.AreEqual("▸  运行效率", localization.Translate("▸  Mobility Efficiency"));
                Assert.AreEqual("▸  资源利用", localization.Translate("▸  Resource Utilization"));
                Assert.AreEqual("▸  安全与碰撞", localization.Translate("▸  Safety and Collisions"));
                Assert.AreEqual("▸  诊断与样本", localization.Translate("▸  Diagnostics and Samples"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChineseCatalog_CoversLiveHeatmapTrafficFigureAndSimulationInterlock()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);

                Assert.AreEqual("实时交通热力图", localization.Translate("Live Traffic Heatmap"));
                Assert.AreEqual("仿真运行时显示实时交通热力图",
                    localization.Translate("Show live traffic heatmap while simulation is running"));
                Assert.AreEqual("运行时车道配色",
                    localization.Translate("Runtime Lane Coloring"));
                Assert.AreEqual("车道类型配色（不进行实时染色）",
                    localization.Translate("Lane-type colors (no live recoloring)"));
                Assert.AreEqual("实时飞行器负载（灰 → 绿 → 红）",
                    localization.Translate("Live aircraft load (gray → green → red)"));
                Assert.AreEqual("车道通行状态（绿色可通行，红色不可通行）",
                    localization.Translate("Lane passability (green = open, red = blocked)"));
                Assert.AreEqual("请选择一种车道线配色来源；交叉口信号标记仍作为独立可视图层显示",
                    localization.Translate(
                        "Choose exactly one lane-line color source; intersection-signal markers remain a separate visual layer"));
                Assert.AreEqual("车道显示模式：实时飞行器负载。",
                    localization.Translate("Lane display mode: live aircraft load."));
                Assert.AreEqual("车道显示模式：通行状态。",
                    localization.Translate("Lane display mode: passability."));
                Assert.AreEqual("车道显示模式：车道类型配色。",
                    localization.Translate("Lane display mode: lane-type colors."));
                Assert.AreEqual("交通热力图高度层",
                    localization.Translate("Traffic-Heatmap Altitude Layers"));
                Assert.AreEqual("生成 / 刷新二维交通热力图",
                    localization.Translate("Generate / Refresh 2D Traffic Heatmap"));
                Assert.AreEqual("显示全部高度层", localization.Translate("Show All Altitude Layers"));
                Assert.AreEqual("正在生成二维交通热力图",
                    localization.Translate("Generating 2D Traffic Heatmap"));
                Assert.AreEqual("归档交通热力图", localization.Translate("Archived Traffic Heatmap"));

                string hint = localization.Translate(
                    "TIP  Select exactly one single run or batch member. The Python figure separates " +
                    "visible altitude layers into square map panels, includes vertiport reservation resources, " +
                    "and deliberately excludes lane-to-lane connector curves. ");
                StringAssert.StartsWith("提示  请仅选择一条单次运行记录或一个批次成员", hint);
                StringAssert.Contains("正方形地图面板", hint);
                StringAssert.Contains("排除车道间连接曲线", hint);

                string layerNotice = localization.Translate(
                    "Traffic-heatmap layer Lower Network Layer: Visible. " +
                    "Use Generate / Refresh 2D Traffic Heatmap to update the Python figure. ");
                Assert.AreEqual(
                    "交通热力图高度层 下层路网: 可见. " +
                    "请使用“生成 / 刷新二维交通热力图”更新 Python 图。 ",
                    layerNotice);

                string caption = localization.Translate(
                    "1280 lane(s) across 2 visible altitude layer(s) · 28 reservation resource(s) · " +
                    "3 panel(s) · Lane-to-lane connection corridors are intentionally excluded");
                StringAssert.Contains("1280 条车道，分布于 2 个可见高度层", caption);
                StringAssert.Contains("28 个预约资源", caption);
                StringAssert.Contains("已有意排除车道间连接走廊", caption);
                StringAssert.Contains("1 个已有负载的资源 ID 缺少几何",
                    localization.Translate("1 loaded resource ID(s) lacked geometry"));
                Assert.AreEqual("以下资源 ID 存在预约负载，但没有可绘制几何：MISSING-RESOURCE",
                    localization.Translate(
                        "Reservation load has no drawable geometry for resource ID(s): MISSING-RESOURCE"));

                string locked = localization.Translate(
                    "Airspace editing, Routes & Flow, Data Visualization, and scene switching are locked " +
                    "until the current simulation ends. ");
                StringAssert.Contains("空域结构编辑、路径与交通流、数据可视化", locked);
                StringAssert.Contains("均已锁定", locked);

                string[] guardedActions =
                {
                    "Stop the current single or batch simulation before opening scene data. ",
                    "Stop the current single or batch simulation before creating a scene. ",
                    "Stop the current single or batch simulation before switching scene tabs. ",
                    "Stop the current single or batch simulation before closing a scene tab. "
                };
                foreach (string source in guardedActions)
                {
                    string translated = localization.Translate(source);
                    Assert.AreNotEqual(source, translated, "Missing zh-CN simulation interlock message: " + source);
                    StringAssert.DoesNotContain("Stop the current", translated);
                }

                StringAssert.StartsWith("交通热力图绘制失败：",
                    localization.Translate("Traffic-heatmap plotting failed: worker unavailable"));
                StringAssert.Contains("已生成", localization.Translate(
                    "Python traffic heatmap generated. Layer panels use lane centerlines only; " +
                    "lane-to-lane connectors are excluded. "));

                Assert.AreEqual("正在载入归档拓扑，并生成可翻页的路网高度层全景图与各起降场独立全景图……",
                    localization.Translate(
                        "Loading the archived topology and generating paged network-layer and full-size vertiport figures..."));
                string pagedCaption = localization.Translate(
                    "2 full-size network page(s) · 7 vertiport page(s) · 640 visible lane(s) · " +
                    "1974 reservation resource(s)");
                StringAssert.Contains("2 个路网全景页", pagedCaption);
                StringAssert.Contains("7 个起降场全景页", pagedCaption);
                StringAssert.Contains("640 条可见车道", pagedCaption);
                Assert.AreEqual("图 2/9: 路网 — 下层路网 · 75 m — 640 条车道中心线",
                    localization.Translate(
                        "Figure 2/9: Road network — Lower Network Layer · 75 m — 640 lane centerline(s)"));
                string pagedNotice = localization.Translate(
                    "Python traffic heatmap generated. Use Previous Figure / Next Figure to review " +
                    "network-layer overviews and full-size vertiport pages. Lane-to-lane connectors are excluded. ");
                StringAssert.Contains("上一张图 / 下一张图", pagedNotice);
                StringAssert.Contains("各起降场独立全景页", pagedNotice);
                StringAssert.DoesNotContain("Previous Figure", pagedNotice);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChineseCatalog_CoversVisibleWorkbenchControlsDialogsAndDynamicErrors()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);
                string[] controls =
                {
                    "Layer name", "Altitude (m)", "Building-height threshold (m)",
                    "Rectangle width (m)", "Rectangle depth (m)", "Circle radius (m)",
                    "Centroid rotation (deg)", "Holding-structure altitude",
                    "Horizontal radius about the selected anchor",
                    "Number of evenly spaced vertices around the circle",
                    "Angle from the X axis to the first holding vertex",
                    "Aircraft count", "Departure time (s)", "Dispatch interval (s)",
                    "Input route file (.json / .xml)", "Output path (.json / .xml)",
                    "Scene name", "Move Current Scene to Recycle Bin", "Reopen Selected Scene",
                    "Drop a city model here\n.sarcity / .obj / .dxf / .skp",
                    "Position X", "Position Y", "Position Z", "Rotation X", "Rotation Y", "Rotation Z",
                    "Select Centerline-Network JSON", "Select Vertiport-Structure JSON",
                    "Select City Model", "Select Routes and Traffic-Flow File",
                    "Select Simulation-Demand File", "Export Routes and Traffic-Flow File",
                    "JSON / XML Route File"
                };
                foreach (string source in controls)
                    Assert.AreNotEqual(source, localization.Translate(source),
                        "Missing zh-CN visible control/dialog translation: " + source);

                string[] dynamicMessages =
                {
                    "Invalid building-height threshold. Enter a number from 0 to 1000. ",
                    "Invalid helipad circle radius. Enter a number from 0.1 to 100. ",
                    "Invalid holding-ring vertex count. Enter an integer from 3 to 128. ",
                    "Invalid duration per batch. Enter a number from 0.1 to 10000000. ",
                    "Mission M-12 deleted. ",
                    "Collision detected between A01 / A02; the simulation was paused and focused on the collision point. ",
                    "Scene \"Demo\" contains unsaved airspace edits. Save the scene files before closing?"
                };
                foreach (string source in dynamicMessages)
                    Assert.AreNotEqual(source, localization.Translate(source),
                        "Missing zh-CN dynamic UI translation: " + source);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChineseCatalog_CoversEveryDisplayedMetricsTableLabel()
        {
            GameObject root = new GameObject("LocalizationTestRoot");
            try
            {
                SarRuntimeLocalization localization = Configure(root, SarRuntimeLocalization.ChineseLocale);
                string[] metrics =
                {
                    "Simulation Duration (s)", "Throughput (successful missions)",
                    "Throughput (missions/s)", "Average Travel Distance (m)",
                    "Average Straight-line Distance (m)", "Detour Ratio",
                    "Average Mission Duration (s)", "Average Resource Wait Time (s)",
                    "Average Headway Distance (m)", "Reservation-Window Utilization",
                    "Lane Utilization (avg over time)", "Lane Utilization Variance",
                    "Corridor Utilization (avg over time)",
                    "Reservation Utilization (avg per resource)",
                    "Reservation Utilization Variance", "Utilization Sample Count",
                    "Reservation Util Sample Count", "Total Missions", "Successful", "Failed",
                    "Failure Rate", "Total Resource Bookings", "Total Booked Resource-Time (s)",
                    "Headway Samples", "Total Collisions", "Lane Collisions", "Approach Collisions",
                    "Ramp Collisions", "Corridor Collisions", "Helipad Collisions",
                    "Unclassified / Mixed-Resource Collisions", "Collision Classification Residual"
                };
                foreach (string source in metrics)
                    Assert.AreNotEqual(source, localization.Translate(source),
                        "Missing zh-CN displayed metric label: " + source);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Implementation_UsesSourceAssignmentsWithoutCanvasScanningOrPolling()
        {
            string localizationSource = File.ReadAllText(Path.Combine(Application.dataPath,
                "Scripts", "RuntimeUI", "SarRuntimeLocalization.cs"));
            StringAssert.DoesNotContain("willRenderCanvases", localizationSource);
            StringAssert.DoesNotContain("LateUpdate", localizationSource);
            StringAssert.DoesNotContain("GetComponentsInChildren", localizationSource);
            StringAssert.DoesNotContain("RefreshAll", localizationSource);

            string workbenchSource = File.ReadAllText(Path.Combine(Application.dataPath,
                "Scripts", "RuntimeUI", "SarRuntimeWorkbench.cs"));
            StringAssert.Contains("localization = SarRuntimeLocalization.Ensure(gameObject);", workbenchSource);
            StringAssert.Contains("text.text = L(value);", workbenchSource);
            StringAssert.Contains("private void SetLocalizedText", workbenchSource);
            StringAssert.Contains("OpenFileAsync(L(\"Select Centerline-Network JSON\")", workbenchSource);
            StringAssert.Contains("SaveFileAsync(L(\"Export Routes and Traffic-Flow File\")", workbenchSource);
            string[] criticalDynamicLabels =
            {
                "statusText.text =", "footerText.text =", "editStats.text =",
                "simulationStats.text =", "analyticsStats.text =", "sweepStatusText.text =",
                "historySelectionSummary.text =", "sceneDataSummary.text ="
            };
            foreach (string assignment in criticalDynamicLabels)
                StringAssert.DoesNotContain(assignment, workbenchSource,
                    "Critical UI text bypasses source localization: " + assignment);
        }

        private static SarRuntimeLocalization Configure(GameObject root, string locale)
        {
            SarRuntimeLocalization localization = root.AddComponent<SarRuntimeLocalization>();
            localization.ConfigureForTests(locale, LoadChineseCatalog());
            return localization;
        }

        private static SarRuntimeLocalizationCatalog LoadChineseCatalog()
        {
            string path = Path.Combine(Application.dataPath, "StreamingAssets", "SARLocalization", "zh-CN.json");
            Assert.IsTrue(File.Exists(path), "Missing runtime localization catalog: " + path);
            return SarRuntimeLocalizationCatalog.FromJson(File.ReadAllText(path));
        }
    }
}
