using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace RoadNetworkEditor.RuntimeUI
{
    public enum AirspaceVisualLayer
    {
        City,
        Network,
        Approach,
        Holding,
        Facilities,
        Corridors,
        LayerConnectors,
        Signals,
        RoutePreview,
        Heatmap
    }

    /// <summary>
    /// Mutually exclusive lane-line rendering modes used by the Simulation page.
    /// Historical heat-map rendering remains controlled by <see cref="AirspaceVisualLayer.Heatmap"/>
    /// and is intentionally independent from this runtime display choice.
    /// </summary>
    public enum RuntimeLaneColorMode
    {
        LaneType,
        LiveAircraftLoad,
        Passability
    }

    /// <summary>Player-build replacement for editor-only Gizmos.</summary>
    public sealed class RuntimeAirspaceVisualizer : MonoBehaviour
    {
        private readonly Dictionary<AirspaceVisualLayer, bool> visibility =
            Enum.GetValues(typeof(AirspaceVisualLayer)).Cast<AirspaceVisualLayer>()
                .ToDictionary(layer => layer, _ => true);

        private SimulationManager manager;
        private Material lineMaterial;
        private CityModelController cityModel;
        private RuntimeAirspaceEditor editor;
        private RuntimeRouteDesigner routeDesigner;
        private readonly List<List<Vector3>> routePreviews = new List<List<Vector3>>();
        private readonly List<RouteLabel> routeLabels = new List<RouteLabel>();
        private readonly Dictionary<string, float> historicalLaneLoads =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> historicalResourceLoads =
            new Dictionary<string, float>(StringComparer.Ordinal);
        // Analysis-layer visibility is deliberately kept outside AltitudeLayer.Enabled.
        // The latter is authored scene data and is persisted with the network; using it
        // for a temporary heat-map filter would silently change the user's scene.
        private readonly Dictionary<string, bool> heatmapLayerVisibility =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        // The layer eye controls are view state, not authored network data.  The
        // workbench replaces this map whenever the active function page changes,
        // which lets Edit, Routes, Simulation, and Analytics keep independent
        // choices without ever writing AltitudeLayer.Enabled into a scene/snapshot.
        private readonly Dictionary<string, bool> displayLayerVisibility =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, Lane> historicalLanes =
            new Dictionary<string, Lane>(StringComparer.Ordinal);
        private readonly Dictionary<string, Vertiport> historicalVertiports =
            new Dictionary<string, Vertiport>(StringComparer.Ordinal);
        private readonly Dictionary<string, Helipad> historicalHelipads =
            new Dictionary<string, Helipad>(StringComparer.Ordinal);
        private readonly Dictionary<string, HoldingStructure> historicalHoldingStructures =
            new Dictionary<string, HoldingStructure>(StringComparer.Ordinal);
        private readonly Dictionary<string, Circle> historicalCircles =
            new Dictionary<string, Circle>(StringComparer.Ordinal);
        private readonly Dictionary<string, Vertex> historicalVertices =
            new Dictionary<string, Vertex>(StringComparer.Ordinal);
        private readonly Dictionary<string, Ramp> historicalRamps =
            new Dictionary<string, Ramp>(StringComparer.Ordinal);
        private readonly Dictionary<string, ApproachNode> historicalApproachNodes =
            new Dictionary<string, ApproachNode>(StringComparer.Ordinal);
        private readonly Dictionary<string, ApproachEdge> historicalApproachEdges =
            new Dictionary<string, ApproachEdge>(StringComparer.Ordinal);
        private readonly Dictionary<string, NetConnectedCorridor> historicalCorridors =
            new Dictionary<string, NetConnectedCorridor>(StringComparer.Ordinal);
        private Network historicalNetwork;
        private bool historicalMode;
        private float historicalPeakLoad;
        private float historicalPeakResourceLoad;
        private int historicalDrawableResourceCount;
        private Camera routeLabelCamera;
        private GUIStyle routeLabelStyle;
        private readonly List<SolidMarker> solidMarkers = new List<SolidMarker>();
        public bool EditorOverlayVisible { get; set; }
        public bool TallBuildingHighlightEnabled => cityModel != null && cityModel.enableHighlight;
        public float TallBuildingHeightThreshold => cityModel != null ? cityModel.heightThreshold : 50f;
        public int TallBuildingHighlightCount => cityModel != null ? cityModel.HighlightedBuildingCount : 0;
        public bool HasHistoricalAnalysis => historicalMode;
        public bool HistoricalModeActive => historicalMode;
        public int HistoricalMatchedLaneCount { get; private set; }
        public int HistoricalRecordedLaneCount { get; private set; }
        public float HistoricalPeakLaneLoad => CurrentVisibleHistoricalPeakLaneLoad();
        public float HistoricalPeakResourceLoad => historicalPeakResourceLoad;
        public int HistoricalDrawableResourceCount => historicalDrawableResourceCount;
        public RuntimeLaneColorMode LaneColorMode { get; private set; } = RuntimeLaneColorMode.LaneType;

        private struct SolidMarker
        {
            public Vector3 Point;
            public float Size;
            public Color Color;
            public bool Billboard;
        }

        private struct RouteLabel
        {
            public Vector3 Point;
            public string Text;
        }

        public void Initialize(SimulationManager simulationManager)
        {
            manager = simulationManager;
            cityModel = FindObjectOfType<CityModelController>(true);
            if (cityModel != null)
            {
                if (cityModel.heightThreshold <= 0f) cityModel.heightThreshold = 50f;
                cityModel.enableHighlight = false;
                cityModel.RemoveHighlight();
            }
            visibility[AirspaceVisualLayer.Heatmap] = false;
            CreateMaterial();
        }

        public void BindEditor(RuntimeAirspaceEditor runtimeEditor)
        {
            editor = runtimeEditor;
        }

        public void BindRouteDesigner(RuntimeRouteDesigner runtimeRouteDesigner)
        {
            routeDesigner = runtimeRouteDesigner;
        }

        public void SetLaneColorMode(RuntimeLaneColorMode mode)
        {
            LaneColorMode = mode;
        }

        public bool IsVisible(AirspaceVisualLayer layer) => visibility.TryGetValue(layer, out bool value) && value;

        public bool IsPerLayerVisible(string layerId)
        {
            if (string.IsNullOrWhiteSpace(layerId)) return true;
            // Legacy/virtual lanes that do not map to an authored altitude layer
            // remain visible for backward compatibility.
            return !displayLayerVisibility.TryGetValue(layerId, out bool visible) || visible;
        }

        public void SetDisplayLayerVisible(string layerId, bool visible)
        {
            if (string.IsNullOrWhiteSpace(layerId)) return;
            displayLayerVisibility[layerId] = visible;
        }

        public void ResetDisplayLayerVisibility()
        {
            displayLayerVisibility.Clear();
        }

        private AltitudeLayer FindHeatmapAltitudeLayer(string layerId)
        {
            if (string.IsNullOrWhiteSpace(layerId)) return null;
            if (historicalMode)
                return historicalNetwork?.AltitudeLayers?.FirstOrDefault(item =>
                    item != null && item.LayerId == layerId);

            // AxisNetwork owns the authored visibility flag. FullNetwork is derived and
            // is initialized with a placeholder L0 even before generation, so consulting
            // it first can return stale metadata for an otherwise valid authored layer.
            AltitudeLayer authoredLayer = manager?.networkManager?.AxisNetwork?.AltitudeLayers?
                .FirstOrDefault(item => item != null && item.LayerId == layerId);
            return authoredLayer ?? manager?.networkManager?.FullNetwork?.AltitudeLayers?
                .FirstOrDefault(item => item != null && item.LayerId == layerId);
        }

        public bool IsNetworkLayerRenderable(string layerId) =>
            IsVisible(AirspaceVisualLayer.Network) && IsPerLayerVisible(layerId);

        /// <summary>
        /// Returns the altitude layers available to the current heat-map data source.
        /// The returned list is a snapshot and changing it cannot mutate scene data.
        /// </summary>
        public IReadOnlyList<AltitudeLayer> GetHeatmapAltitudeLayers()
        {
            IEnumerable<AltitudeLayer> layers;
            if (historicalMode)
            {
                layers = historicalNetwork?.AltitudeLayers;
            }
            else
            {
                List<AltitudeLayer> axisLayers = manager?.networkManager?.AxisNetwork?.AltitudeLayers ??
                                                 new List<AltitudeLayer>();
                List<AltitudeLayer> fullLayers = manager?.networkManager?.FullNetwork?.AltitudeLayers ??
                                                 new List<AltitudeLayer>();
                int axisIdCount = axisLayers.Where(layer => layer != null &&
                        !string.IsNullOrWhiteSpace(layer.LayerId))
                    .Select(layer => layer.LayerId).Distinct(StringComparer.Ordinal).Count();
                int fullIdCount = fullLayers.Where(layer => layer != null &&
                        !string.IsNullOrWhiteSpace(layer.LayerId))
                    .Select(layer => layer.LayerId).Distinct(StringComparer.Ordinal).Count();
                // Legacy AxisNetwork data can contain only Network's constructor L0
                // placeholder while FullNetwork owns the complete authored L0/L1 list.
                // Prefer the source with more distinct IDs; an equal count keeps Axis as
                // the authoritative editing source.
                layers = fullIdCount > axisIdCount ? fullLayers : axisLayers;
            }
            return (layers ?? Enumerable.Empty<AltitudeLayer>())
                .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                .GroupBy(layer => layer.LayerId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(layer => layer.NominalAltitude)
                .ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// A missing entry means visible, so legacy networks and newly loaded layers are
        /// immediately usable without modifying their persistent Enabled flag.
        /// </summary>
        public bool IsHeatmapLayerVisible(string layerId)
        {
            if (string.IsNullOrWhiteSpace(layerId)) return true;
            return !heatmapLayerVisibility.TryGetValue(layerId, out bool visible) || visible;
        }

        public void SetHeatmapLayerVisible(string layerId, bool visible)
        {
            if (string.IsNullOrWhiteSpace(layerId)) return;
            heatmapLayerVisibility[layerId] = visible;
        }

        public void ResetHeatmapLayerVisibility()
        {
            heatmapLayerVisibility.Clear();
        }

        private bool IsNetworkLayerRenderableForCurrentDisplay(string layerId) =>
            IsNetworkLayerRenderable(layerId) &&
            (!IsVisible(AirspaceVisualLayer.Heatmap) || IsHeatmapLayerVisible(layerId));

        public void SetVisible(AirspaceVisualLayer layer, bool value)
        {
            visibility[layer] = value;
            if (layer == AirspaceVisualLayer.City && cityModel != null)
                cityModel.gameObject.SetActive(value);
        }

        public bool Toggle(AirspaceVisualLayer layer)
        {
            bool value = !IsVisible(layer);
            SetVisible(layer, value);
            return value;
        }

        public int SetTallBuildingHighlight(float threshold, bool enabled)
        {
            if (cityModel == null) return 0;
            cityModel.heightThreshold = Mathf.Max(0f, threshold);
            cityModel.enableHighlight = enabled;
            if (enabled) cityModel.ApplyHighlight();
            else cityModel.RemoveHighlight();
            return cityModel.HighlightedBuildingCount;
        }

        /// <summary>Reconnect the city layer after a scene package replaces the model root.</summary>
        public void RebindCityModel()
        {
            cityModel = FindObjectOfType<CityModelController>(true);
            if (cityModel != null)
                cityModel.gameObject.SetActive(IsVisible(AirspaceVisualLayer.City));
        }

        public void SetRoutePreview(IEnumerable<Vector3> points)
        {
            routePreviews.Clear();
            routeLabels.Clear();
            List<Vector3> preview = points?.ToList();
            if (preview != null && preview.Count > 1) routePreviews.Add(preview);
        }

        public void SetRoutePreviews(IEnumerable<IEnumerable<Vector3>> paths)
        {
            routePreviews.Clear();
            routeLabels.Clear();
            foreach (IEnumerable<Vector3> path in paths ?? Enumerable.Empty<IEnumerable<Vector3>>())
            {
                List<Vector3> points = path?.ToList();
                if (points != null && points.Count > 1) routePreviews.Add(points);
            }
        }

        public void SetHistoricalRecording(SimulationRecording recording)
        {
            SetHistoricalRecordings(recording == null
                ? Enumerable.Empty<SimulationRecording>()
                : new[] { recording });
        }

        /// <summary>
        /// Selects an archived lane-level topology for history playback.  The snapshot
        /// is visual-only and never replaces or edits the active scene network.
        /// </summary>
        public void SetHistoricalTopology(Network network)
        {
            historicalMode = true;
            historicalNetwork = network;
            historicalLanes.Clear();
            foreach (Lane lane in network?.Edges?
                         .Where(edge => edge?.Lanes != null)
                         .SelectMany(edge => edge.Lanes)
                         .Where(lane => lane != null && !string.IsNullOrWhiteSpace(lane.LaneId)) ??
                     Enumerable.Empty<Lane>())
                historicalLanes[lane.LaneId] = lane;
            RebuildHistoricalStatistics();
        }

        /// <summary>
        /// Selects the archived vertiport/approach/holding geometry that belongs to the
        /// historical run. The data is indexed locally and never mutates the active scene.
        /// </summary>
        public void SetHistoricalGroundGeometry(IEnumerable<Vertiport> vertiports)
        {
            historicalMode = true;
            ClearHistoricalGroundGeometry();
            foreach (Vertiport vertiport in vertiports ?? Enumerable.Empty<Vertiport>())
            {
                if (vertiport == null) continue;
                if (!string.IsNullOrWhiteSpace(vertiport.VertiportId))
                    historicalVertiports[vertiport.VertiportId] = vertiport;

                foreach (Helipad helipad in vertiport.Helipads ?? new List<Helipad>())
                    if (helipad != null && !string.IsNullOrWhiteSpace(helipad.HelipadId))
                        historicalHelipads[helipad.HelipadId] = helipad;

                foreach (HoldingStructure holding in vertiport.HoldingStructures ??
                         new List<HoldingStructure>())
                {
                    if (holding == null) continue;
                    if (!string.IsNullOrWhiteSpace(holding.HoldingStructureId))
                        historicalHoldingStructures[holding.HoldingStructureId] = holding;
                    foreach (Circle circle in holding.Circles ?? new List<Circle>())
                        if (circle != null && !string.IsNullOrWhiteSpace(circle.CircleId))
                            historicalCircles[circle.CircleId] = circle;
                    foreach (Vertex vertex in holding.Vertices ?? new List<Vertex>())
                        if (vertex != null && !string.IsNullOrWhiteSpace(vertex.VertexId))
                            historicalVertices[vertex.VertexId] = vertex;
                    foreach (Ramp ramp in holding.Ramps ?? new List<Ramp>())
                        if (ramp != null && !string.IsNullOrWhiteSpace(ramp.RampId))
                            historicalRamps[ramp.RampId] = ramp;
                }

                foreach (ApproachNode node in vertiport.ApproachNet?.Nodes ?? new List<ApproachNode>())
                    if (node != null && !string.IsNullOrWhiteSpace(node.NodeId))
                        historicalApproachNodes[node.NodeId] = node;
                foreach (ApproachEdge edge in vertiport.ApproachNet?.Edges ?? new List<ApproachEdge>())
                    if (edge != null && !string.IsNullOrWhiteSpace(edge.EdgeId))
                        historicalApproachEdges[edge.EdgeId] = edge;
                foreach (NetConnectedCorridor corridor in vertiport.Corridors ??
                         new List<NetConnectedCorridor>())
                    if (corridor != null && !string.IsNullOrWhiteSpace(corridor.CorridorId))
                        historicalCorridors[corridor.CorridorId] = corridor;
            }
            RebuildHistoricalStatistics();
        }

        /// <summary>
        /// Legacy in-memory entry point used by tests and current-session data. Historical
        /// lane heat is cumulative lane-entry count, matching TrafficHeatmap.py semantics.
        /// </summary>
        public void SetHistoricalRecordings(IEnumerable<SimulationRecording> recordings)
        {
            historicalMode = true;
            historicalLaneLoads.Clear();
            historicalResourceLoads.Clear();
            var laneRunTraversals = new Dictionary<string, List<float>>(StringComparer.Ordinal);
            var tracks = new List<List<Vector3>>();
            foreach (SimulationRecording recording in recordings ?? Enumerable.Empty<SimulationRecording>())
            {
                if (recording == null) continue;
                foreach (IGrouping<string, LaneTimeSeriesRecord> group in
                         (recording.LaneTimeSeries ?? new List<LaneTimeSeriesRecord>())
                         .Where(item => item != null && !string.IsNullOrWhiteSpace(item.LaneId))
                         .GroupBy(item => item.LaneId, StringComparer.Ordinal))
                {
                    float traversalCount = SarSimulationHistory.CountLaneTraversals(group
                        .SelectMany(item => item.Snapshots ?? new List<LaneSnapshot>()));
                    if (!laneRunTraversals.TryGetValue(group.Key, out List<float> values))
                    {
                        values = new List<float>();
                        laneRunTraversals[group.Key] = values;
                    }
                    values.Add(traversalCount);
                }

                tracks.AddRange((recording.AircraftTrajectories ?? new List<AircraftTrajectoryRecord>())
                    .Where(item => item?.Positions != null && item.Positions.Count > 1)
                    .Select(item => new List<Vector3>(item.Positions)));
            }

            foreach (KeyValuePair<string, List<float>> pair in laneRunTraversals)
            {
                float average = pair.Value.Average();
                if (IsPositiveFinite(average)) historicalLaneLoads[pair.Key] = average;
            }
            RebuildHistoricalStatistics();
            SetRoutePreviews(tracks);
        }

        /// <summary>
        /// Applies only archived lane/resource loads. This intentionally does not touch
        /// route previews, so a 2D heat-map request cannot inject unlisted trajectories
        /// into the shared 3D view.
        /// </summary>
        public void SetHistoricalLoadData(SarHistoricalVisualizationData data)
        {
            historicalMode = true;
            historicalLaneLoads.Clear();
            historicalResourceLoads.Clear();
            foreach (KeyValuePair<string, float> pair in data?.LaneTraversals ??
                     new Dictionary<string, float>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && IsPositiveFinite(pair.Value))
                    historicalLaneLoads[pair.Key] = pair.Value;
            }
            foreach (KeyValuePair<string, float> pair in data?.ResourceReservationSeconds ??
                     new Dictionary<string, float>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && IsPositiveFinite(pair.Value))
                    historicalResourceLoads[pair.Key] = pair.Value;
            }
            RebuildHistoricalStatistics();
        }

        public void SetHistoricalVisualizationData(SarHistoricalVisualizationData data)
        {
            SetHistoricalLoadData(data);
            SetRoutePreviews(data?.Trajectories);
        }

        public void ClearHistoricalAnalysis()
        {
            historicalMode = false;
            historicalLaneLoads.Clear();
            historicalResourceLoads.Clear();
            historicalNetwork = null;
            historicalLanes.Clear();
            ClearHistoricalGroundGeometry();
            historicalPeakLoad = 0f;
            historicalPeakResourceLoad = 0f;
            historicalDrawableResourceCount = 0;
            HistoricalMatchedLaneCount = 0;
            HistoricalRecordedLaneCount = 0;
            SetRoutePreviews(null);
        }

        private void ClearHistoricalGroundGeometry()
        {
            historicalVertiports.Clear();
            historicalHelipads.Clear();
            historicalHoldingStructures.Clear();
            historicalCircles.Clear();
            historicalVertices.Clear();
            historicalRamps.Clear();
            historicalApproachNodes.Clear();
            historicalApproachEdges.Clear();
            historicalCorridors.Clear();
        }

        private void RebuildHistoricalStatistics()
        {
            historicalPeakLoad = 0f;
            historicalPeakResourceLoad = 0f;
            historicalDrawableResourceCount = 0;
            HistoricalRecordedLaneCount = historicalLaneLoads.Count;
            HistoricalMatchedLaneCount = 0;
            foreach (KeyValuePair<string, float> pair in historicalLaneLoads)
            {
                if (!IsPositiveFinite(pair.Value) ||
                    !historicalLanes.TryGetValue(pair.Key, out Lane lane) ||
                    !IsDrawablePolyline(lane.Centerline)) continue;
                HistoricalMatchedLaneCount++;
                historicalPeakLoad = Mathf.Max(historicalPeakLoad, pair.Value);
            }

            foreach (KeyValuePair<string, float> pair in historicalResourceLoads)
            {
                if (!IsPositiveFinite(pair.Value) ||
                    !TryGetHistoricalResourcePolyline(pair.Key, out _)) continue;
                historicalDrawableResourceCount++;
                historicalPeakResourceLoad = Mathf.Max(historicalPeakResourceLoad, pair.Value);
            }
        }

        private float CurrentVisibleHistoricalPeakLaneLoad()
        {
            float peak = 0f;
            foreach (KeyValuePair<string, float> pair in historicalLaneLoads)
            {
                if (!IsPositiveFinite(pair.Value) ||
                    !historicalLanes.TryGetValue(pair.Key, out Lane lane) ||
                    !IsDrawablePolyline(lane.Centerline) ||
                    !IsNetworkLayerRenderable(lane.LayerId) ||
                    !IsHeatmapLayerVisible(lane.LayerId)) continue;
                peak = Mathf.Max(peak, pair.Value);
            }
            return peak;
        }

        private bool TryGetHistoricalResourcePolyline(string resourceId, out List<Vector3> points)
        {
            points = null;
            if (string.IsNullOrWhiteSpace(resourceId)) return false;
            if (historicalApproachEdges.TryGetValue(resourceId, out ApproachEdge edge) &&
                edge != null &&
                historicalApproachNodes.TryGetValue(edge.FromNodeId, out ApproachNode from) &&
                historicalApproachNodes.TryGetValue(edge.ToNodeId, out ApproachNode to))
            {
                points = new List<Vector3> { from.Position, to.Position };
                return IsDrawablePolyline(points);
            }
            if (!historicalRamps.TryGetValue(resourceId, out Ramp ramp) || ramp == null) return false;
            points = RuntimeAirspaceGeometry.BuildRampPolyline(ramp, historicalVertices,
                historicalCircles, 40);
            return IsDrawablePolyline(points);
        }

        private static bool IsDrawablePolyline(IList<Vector3> points)
        {
            if (points == null || points.Count < 2) return false;
            for (int index = 0; index < points.Count; index++)
                if (!IsFinite(points[index])) return false;
            for (int index = 1; index < points.Count; index++)
                if ((points[index] - points[index - 1]).sqrMagnitude > 0.000001f) return true;
            return false;
        }

        private static bool IsPositiveFinite(float value) =>
            value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 point) =>
            !float.IsNaN(point.x) && !float.IsInfinity(point.x) &&
            !float.IsNaN(point.y) && !float.IsInfinity(point.y) &&
            !float.IsNaN(point.z) && !float.IsInfinity(point.z);

        public void SetRouteEdgePreview(IEnumerable<Edge> edges)
        {
            routePreviews.Clear();
            routeLabels.Clear();
            int sequence = 0;
            foreach (Edge edge in edges ?? Enumerable.Empty<Edge>())
            {
                if (edge?.Centerline == null || edge.Centerline.Count < 2) continue;
                List<Vector3> lifted = edge.Centerline.Select(point => point + Vector3.up * 0.75f).ToList();
                routePreviews.Add(lifted);
                routeLabels.Add(new RouteLabel
                {
                    Point = PointAtPolylineRatio(lifted, 0.5f) + Vector3.up * 0.35f,
                    Text = (++sequence).ToString()
                });
            }
        }

        private void CreateMaterial()
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
            if (shader == null) return;
            lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
        }

        private void OnDestroy()
        {
            if (lineMaterial == null) return;
            if (Application.isPlaying) Destroy(lineMaterial);
            else DestroyImmediate(lineMaterial);
        }

        private void OnRenderObject()
        {
            if (manager == null || lineMaterial == null || Camera.current == null || Camera.current.targetDisplay != 0)
                return;

            routeLabelCamera = Camera.current;

            lineMaterial.SetPass(0);
            solidMarkers.Clear();
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.LINES);

            if (IsVisible(AirspaceVisualLayer.Network)) DrawNetwork();
            if (IsVisible(AirspaceVisualLayer.Approach)) DrawApproach();
            if (IsVisible(AirspaceVisualLayer.Holding)) DrawHolding();
            if (IsVisible(AirspaceVisualLayer.Facilities)) DrawFacilities();
            if (IsVisible(AirspaceVisualLayer.Corridors)) DrawCorridors();
            if (IsVisible(AirspaceVisualLayer.LayerConnectors)) DrawLayerConnectors();
            if (IsVisible(AirspaceVisualLayer.Signals)) DrawSignals();
            if (!historicalMode && routeDesigner != null &&
                routeDesigner.SelectionMode == RouteSelectionMode.Manual &&
                routeDesigner.LaneSelectionEnabled && IsVisible(AirspaceVisualLayer.Network))
                DrawRouteSelectionOverlay();
            if (IsVisible(AirspaceVisualLayer.RoutePreview)) DrawRoutePreviews();
            if (IsVisible(AirspaceVisualLayer.Heatmap)) DrawHeatmap();
            if (EditorOverlayVisible && editor != null) DrawEditorOverlay();

            GL.End();
            if (solidMarkers.Count > 0)
            {
                // Editor handles must remain visible and selectable even when city geometry
                // lies between them and the camera.
                lineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
                lineMaterial.SetPass(0);
                GL.Begin(GL.TRIANGLES);
                foreach (SolidMarker marker in solidMarkers) DrawSolidDiamond(marker);
                GL.End();
                lineMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            }
            GL.PopMatrix();
        }

        private void OnGUI()
        {
            if (routeLabelCamera == null || routeLabelCamera.targetDisplay != 0) return;
            if (!IsVisible(AirspaceVisualLayer.RoutePreview) || routeLabels.Count == 0) return;
            if (routeLabelStyle == null)
            {
                routeLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 18,
                    fontStyle = FontStyle.Bold
                };
                routeLabelStyle.normal.textColor = Color.white;
            }

            foreach (RouteLabel label in routeLabels)
            {
                Vector3 screen = routeLabelCamera.WorldToScreenPoint(label.Point);
                if (screen.z <= 0f) continue;
                float size = 32f;
                var rect = new Rect(screen.x - size * 0.5f, Screen.height - screen.y - size * 0.5f,
                    size, size);
                Color previous = GUI.color;
                GUI.color = new Color(0.78f, 0.20f, 0.10f, 0.96f);
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = previous;
                GUI.Label(rect, label.Text, routeLabelStyle);
            }
        }

        private void DrawRoutePreviews()
        {
            Color[] palette =
            {
                new Color(0.86f, 0.22f, 0.10f, 1f),
                new Color(0.12f, 0.48f, 0.78f, 1f),
                new Color(0.08f, 0.62f, 0.46f, 1f),
                new Color(0.65f, 0.25f, 0.72f, 1f),
                new Color(0.92f, 0.55f, 0.08f, 1f)
            };
            for (int i = 0; i < routePreviews.Count; i++)
                DrawPlanarThickPolyline(routePreviews[i], palette[i % palette.Length], 0.55f);
        }

        private void DrawNetwork()
        {
            if (historicalMode)
            {
                if (historicalNetwork == null) return;
                foreach (Edge edge in historicalNetwork.Edges ?? new List<Edge>())
                {
                    if (edge?.Lanes == null ||
                        !IsNetworkLayerRenderableForCurrentDisplay(edge.LayerId)) continue;
                    foreach (Lane lane in edge.Lanes)
                        if (lane?.Centerline != null &&
                            IsNetworkLayerRenderableForCurrentDisplay(lane.LayerId))
                            DrawPolyline(lane.Centerline, LaneColor(lane.LaneType));
                }
                foreach (Lane lane in historicalLanes.Values)
                {
                    if (lane?.Centerline == null || lane.Centerline.Count == 0 ||
                        !IsNetworkLayerRenderableForCurrentDisplay(lane.LayerId)) continue;
                    Vector3 end = lane.Centerline[lane.Centerline.Count - 1];
                    foreach (string toId in lane.ToLanesId ?? new List<string>())
                        if (historicalLanes.TryGetValue(toId, out Lane target) &&
                            target.Centerline != null && target.Centerline.Count > 0 &&
                            IsNetworkLayerRenderableForCurrentDisplay(target.LayerId))
                            Segment(end, target.Centerline[0], new Color(0.83f, 0.62f, 0.18f, 0.85f));
                }
                return;
            }

            int livePeak = LaneColorMode == RuntimeLaneColorMode.LiveAircraftLoad
                ? CurrentPeakLiveAircraftLoad()
                : 0;

            foreach (Edge edge in manager.Edges.Values)
            {
                if (edge?.Lanes == null ||
                    !IsNetworkLayerRenderableForCurrentDisplay(edge.LayerId)) continue;
                foreach (Lane lane in edge.Lanes)
                    if (lane != null && IsNetworkLayerRenderableForCurrentDisplay(lane.LayerId))
                        DrawPolyline(lane.Centerline, RuntimeLaneColor(lane, livePeak));
            }

            foreach (Lane lane in manager.Lanes.Values)
            {
                if (lane?.Centerline == null || lane.Centerline.Count == 0 ||
                    !IsNetworkLayerRenderableForCurrentDisplay(lane.LayerId)) continue;
                Vector3 end = lane.Centerline[lane.Centerline.Count - 1];
                foreach (string toId in lane.ToLanesId ?? new List<string>())
                    if (manager.Lanes.TryGetValue(toId, out Lane target) && target.Centerline.Count > 0 &&
                        IsNetworkLayerRenderableForCurrentDisplay(target.LayerId))
                        Segment(end, target.Centerline[0], new Color(0.83f, 0.62f, 0.18f, 0.85f));
            }
        }

        private void DrawApproach()
        {
            Color edgeColor = new Color(0.13f, 0.48f, 0.72f, 1f);
            IReadOnlyDictionary<string, ApproachEdge> edges = historicalMode
                ? historicalApproachEdges
                : manager.ApproachEdges;
            IReadOnlyDictionary<string, ApproachNode> nodes = historicalMode
                ? historicalApproachNodes
                : manager.ApproachNodes;
            foreach (ApproachEdge edge in edges.Values)
                if (nodes.TryGetValue(edge.FromNodeId, out ApproachNode from) &&
                    nodes.TryGetValue(edge.ToNodeId, out ApproachNode to))
                    DrawPolylineWithArrow(new[] { from.Position, to.Position }, edgeColor, 1.45f);

            foreach (ApproachNode node in nodes.Values)
                Cross(node.Position, node.IsAnchor ? 0.95f : 0.55f,
                    node.IsAnchor ? new Color(0.75f, 0.25f, 0.58f, 1f) : edgeColor);
        }

        private void DrawHolding()
        {
            IReadOnlyDictionary<string, Ramp> ramps = historicalMode ? historicalRamps : manager.Ramps;
            IReadOnlyDictionary<string, Vertex> vertices = historicalMode ? historicalVertices : manager.Vertices;
            IReadOnlyDictionary<string, Circle> circles = historicalMode ? historicalCircles : manager.Circles;
            foreach (Ramp ramp in ramps.Values)
            {
                Color color = !historicalMode && ramp.RampId == editor?.SelectedHoldingRampId
                    ? new Color(1f, 0.55f, 0.08f, 1f)
                    : string.IsNullOrEmpty(ramp.LinkCircleId)
                        ? new Color(0.95f, 0.47f, 0.12f, 1f)
                        : new Color(0.65f, 0.34f, 0.73f, 1f);
                DrawPolylineWithArrow(RuntimeAirspaceGeometry.BuildRampPolyline(
                    ramp, vertices, circles, 40), color, 1.35f);
            }
            foreach (Vertex vertex in vertices.Values)
            {
                bool vertexSelected = !historicalMode && vertex.VertexId == editor?.SelectedHoldingVertexId;
                bool circleSelected = !historicalMode && !string.IsNullOrEmpty(vertex.LinkCircleId) &&
                                      vertex.LinkCircleId == editor?.SelectedHoldingCircleId;
                QueueSolidDiamond(vertex.Position, vertexSelected ? 0.85f : circleSelected ? 0.62f : 0.45f,
                    vertexSelected || circleSelected
                        ? new Color(1f, 0.55f, 0.08f, 1f)
                        : new Color(0.55f, 0.22f, 0.66f, 1f));
            }

            if (!historicalMode && editor != null &&
                editor.TryGetHoldingConnectionPreview(out Vector3 from, out Vector3 to))
                Segment(from, to, new Color(1f, 0.55f, 0.08f, 0.9f));
        }

        private void DrawFacilities()
        {
            IReadOnlyDictionary<string, Vertiport> vertiports = historicalMode
                ? historicalVertiports
                : manager.Vertiports;
            IReadOnlyDictionary<string, Helipad> helipads = historicalMode
                ? historicalHelipads
                : manager.Helipads;
            foreach (Vertiport vertiport in vertiports.Values)
                DrawClosed(vertiport.Boundary, new Color(0.74f, 0.39f, 0.20f, 1f));
            foreach (Helipad pad in helipads.Values)
            {
                DrawClosed(pad.Boundary, new Color(0.10f, 0.55f, 0.47f, 1f));
                if (pad.Boundary != null && pad.Boundary.Count > 0)
                    Cross(pad.Boundary.Aggregate(Vector3.zero, (sum, point) => sum + point) / pad.Boundary.Count,
                        0.8f, new Color(0.10f, 0.55f, 0.47f, 1f));
            }
        }

        private void DrawCorridors()
        {
            IReadOnlyDictionary<string, NetConnectedCorridor> corridors = historicalMode
                ? historicalCorridors
                : manager.Corridors;
            IReadOnlyDictionary<string, Lane> lanes = historicalMode ? historicalLanes : manager.Lanes;
            IReadOnlyDictionary<string, Ramp> ramps = historicalMode ? historicalRamps : manager.Ramps;
            IReadOnlyDictionary<string, Vertex> vertices = historicalMode ? historicalVertices : manager.Vertices;
            IReadOnlyDictionary<string, Circle> circles = historicalMode ? historicalCircles : manager.Circles;
            foreach (NetConnectedCorridor corridor in corridors.Values)
            {
                lanes.TryGetValue(corridor.LinkLaneId, out Lane lane);
                ramps.TryGetValue(corridor.LinkRampId, out Ramp ramp);
                bool selected = !historicalMode && corridor.CorridorId == editor?.SelectedFacilityCorridorId;
                Color color = selected
                    ? new Color(1f, 0.55f, 0.08f, 1f)
                    : corridor.CorridorType == "Entry"
                        ? new Color(0.18f, 0.62f, 0.38f, 1f)
                        : new Color(0.86f, 0.42f, 0.20f, 1f);
                float ratio = !historicalMode && manager.vertiportManager != null
                    ? manager.vertiportManager.corridorTangentRatio
                    : 0.3f;
                List<Vector3> curve = RuntimeAirspaceGeometry.BuildCorridorPolyline(corridor, lane, ramp,
                    vertices, circles, ratio, 36);
                DrawPolylineWithArrow(curve, color, selected ? 1.8f : 1.45f);
                if (selected)
                {
                    QueueSolidDiamond(corridor.LaneLinkPoint, 0.8f, new Color(0.15f, 0.75f, 0.38f, 1f));
                    QueueSolidDiamond(corridor.RampLinkPoint, 0.8f, new Color(0.85f, 0.20f, 0.68f, 1f));
                }
            }
        }

        private void DrawLayerConnectors()
        {
            IEnumerable<LaneConnectionCorridor> corridors = historicalMode
                ? historicalNetwork?.LaneConnectionCorridors ?? Enumerable.Empty<LaneConnectionCorridor>()
                : manager.LaneConnectionCorridors.Values;
            foreach (LaneConnectionCorridor corridor in corridors)
            {
                if (!IsNetworkLayerRenderableForCurrentDisplay(corridor.FromLayerId) ||
                    !IsNetworkLayerRenderableForCurrentDisplay(corridor.ToLayerId)) continue;
                bool selected = corridor.CorridorId == editor?.SelectedLayerConnectorId;
                Color color = selected
                    ? new Color(1f, 0.55f, 0.08f, 1f)
                    : new Color(0.91f, 0.18f, 0.69f, 1f);
                DrawPolylineWithArrow(corridor.Centerline, color, selected ? 1.8f : 1.35f);
                if (selected)
                {
                    QueueSolidDiamond(corridor.FromPoint, 0.9f,
                        new Color(0.18f, 0.62f, 0.38f, 1f));
                    QueueSolidDiamond(corridor.ToPoint, 0.9f,
                        new Color(0.85f, 0.20f, 0.68f, 1f));
                }
            }
        }

        private void DrawSignals()
        {
            if (historicalMode) return;
            foreach (var pair in manager.LaneStates)
            {
                if (!manager.Lanes.TryGetValue(pair.Key, out Lane lane) || lane.Centerline == null || lane.Centerline.Count == 0)
                    continue;
                if (!IsNetworkLayerRenderable(lane.LayerId)) continue;
                Vector3 point = lane.Centerline[lane.Centerline.Count - 1] + Vector3.up * 1.4f;
                // Signals are endpoint markers only. Lane-line coloring is resolved
                // independently by the explicitly selected RuntimeLaneColorMode.
                Color color = SamplePassabilityColor(IsLaneBlocked(pair.Value));
                Cross(point, 0.52f, color);
            }
        }

        private void DrawHeatmap()
        {
            if (historicalMode)
            {
                var baseColor = new Color(0.43f, 0.45f, 0.47f, 0.82f);
                foreach (Lane lane in historicalLanes.Values)
                {
                    if (!IsDrawablePolyline(lane?.Centerline) ||
                        !IsNetworkLayerRenderable(lane.LayerId) ||
                        !IsHeatmapLayerVisible(lane.LayerId)) continue;
                    DrawPolyline(lane.Centerline, baseColor);
                }

                float denominator = Mathf.Max(0.001f, HistoricalPeakLaneLoad);
                foreach (KeyValuePair<string, float> pair in historicalLaneLoads)
                {
                    if (!IsPositiveFinite(pair.Value) ||
                        !historicalLanes.TryGetValue(pair.Key, out Lane historicalLane) ||
                        !IsDrawablePolyline(historicalLane.Centerline) ||
                        !IsNetworkLayerRenderable(historicalLane.LayerId) ||
                        !IsHeatmapLayerVisible(historicalLane.LayerId)) continue;
                    DrawPolyline(historicalLane.Centerline, HeatColor(pair.Value / denominator));
                }

                float resourceDenominator = Mathf.Max(0.001f, historicalPeakResourceLoad);
                foreach (KeyValuePair<string, float> pair in historicalResourceLoads)
                {
                    if (!IsPositiveFinite(pair.Value) ||
                        !TryGetHistoricalResourcePolyline(pair.Key, out List<Vector3> points)) continue;
                    DrawPolyline(points, HeatColor(pair.Value / resourceDenominator));
                }
                return;
            }

            // Runtime lane coloring is handled by DrawNetwork's mutually exclusive
            // lane display mode. Keeping this layer historical-only prevents a stale
            // analysis toggle (or a signal-generated virtual stop-line vehicle) from
            // silently recoloring live lane lines.
        }

        private Color RuntimeLaneColor(Lane lane, int livePeak)
        {
            if (lane == null) return LaneColor(null);
            if (!manager.LaneStates.TryGetValue(lane.LaneId, out LaneState state) || state == null)
                return LaneColorMode == RuntimeLaneColorMode.LiveAircraftLoad
                    ? SampleLiveAircraftLoadColor(0, livePeak)
                    : LaneColor(lane.LaneType);

            switch (LaneColorMode)
            {
                case RuntimeLaneColorMode.LiveAircraftLoad:
                    return SampleLiveAircraftLoadColor(CountLiveAircraft(state), livePeak);
                case RuntimeLaneColorMode.Passability:
                    return SamplePassabilityColor(IsLaneBlocked(state));
                default:
                    return LaneColor(lane.LaneType);
            }
        }

        private int CurrentPeakLiveAircraftLoad()
        {
            int peak = 0;
            foreach (KeyValuePair<string, LaneState> pair in manager.LaneStates)
            {
                if (!manager.Lanes.TryGetValue(pair.Key, out Lane lane) ||
                    !IsNetworkLayerRenderableForCurrentDisplay(lane.LayerId)) continue;
                peak = Mathf.Max(peak, CountLiveAircraft(pair.Value));
            }
            return peak;
        }

        /// <summary>
        /// Counts physical aircraft only. SimulationManager adds a synthetic
        /// "__VIRTUAL__" occupant to blocked lanes as a stop-line constraint; that
        /// implementation detail must never be mistaken for traffic demand.
        /// </summary>
        public static int CountLiveAircraft(LaneState state)
        {
            if (state?.AircraftsInLane == null) return 0;
            return state.AircraftsInLane.Keys.Count(id =>
                !string.IsNullOrWhiteSpace(id) &&
                !id.StartsWith("__", StringComparison.Ordinal));
        }

        public static bool IsLaneBlocked(LaneState state) =>
            state != null && (state.SignalCheck || state.CapabilityCheck);

        public static Color SampleLiveAircraftLoadColor(int aircraftCount, int peakAircraftCount)
        {
            if (aircraftCount <= 0)
                return new Color(0.43f, 0.45f, 0.47f, 0.88f);

            // Dynamic normalization maps the least positive load to green and the
            // busiest positive lane to red. Zero-load gray remains outside the
            // chromatic scale. With only one occupied lane it is, by definition, the
            // current peak and therefore uses the red endpoint.
            float normalized = peakAircraftCount <= 1
                ? 1f
                : Mathf.Clamp01((aircraftCount - 1f) / (peakAircraftCount - 1f));
            return HeatColor(normalized);
        }

        public static Color SamplePassabilityColor(bool blocked) => blocked
            ? new Color(0.82f, 0.20f, 0.18f, 1f)
            : new Color(0.15f, 0.66f, 0.32f, 1f);

        private static Color HeatColor(float normalized)
        {
            float value = Mathf.Clamp01(normalized);
            var low = new Color(0.15f, 0.62f, 0.28f, 0.86f);
            var middle = new Color(0.96f, 0.78f, 0.08f, 0.94f);
            var high = new Color(0.88f, 0.18f, 0.10f, 1f);
            return value <= 0.5f
                ? Color.Lerp(low, middle, value * 2f)
                : Color.Lerp(middle, high, (value - 0.5f) * 2f);
        }

        public static Color SampleHeatmapColor(float normalized) => HeatColor(normalized);

        private void DrawEditorOverlay()
        {
            switch (editor.Workspace)
            {
                case AirspaceEditWorkspace.NetworkLayers:
                    DrawEditingGrid(editor.ActiveLayer?.NominalAltitude ?? editor.EditingAltitude,
                        new Color(0.66f, 0.45f, 0.34f, 0.16f));
                    DrawAxisNetworkEditor();
                    break;
                case AirspaceEditWorkspace.GroundFacility:
                    DrawEditingGrid(0.08f, new Color(0.18f, 0.55f, 0.52f, 0.13f));
                    DrawGroundEditorPoints();
                    break;
                case AirspaceEditWorkspace.HoldingStructure:
                    DrawHoldingEditor();
                    break;
                case AirspaceEditWorkspace.ConnectionCorridor:
                    DrawCorridorSelection();
                    break;
            }
        }

        private void DrawAxisNetworkEditor()
        {
            Network axis = manager.networkManager?.AxisNetwork;
            if (axis == null || !IsVisible(AirspaceVisualLayer.Network)) return;
            const float lift = 0.35f;
            foreach (Edge edge in axis.Edges.Where(edge => edge?.Centerline != null && edge.Centerline.Count > 1))
            {
                if (!IsPerLayerVisible(edge.LayerId)) continue;
                bool active = edge.LayerId == editor.ActiveLayerId;
                bool highlighted = edge.EdgeId == editor.SelectedNetworkEdgeId ||
                                   edge.EdgeId == editor.HoveredNetworkEdgeId;
                Color color = highlighted
                    ? new Color(1f, 0.55f, 0.08f, 1f)
                    : active
                        ? new Color(0.30f, 0.16f, 0.10f, 1f)
                        : new Color(0.30f, 0.28f, 0.26f, 0.16f);
                List<Vector3> lifted = edge.Centerline.Select(point => point + Vector3.up * lift).ToList();
                if (active || highlighted)
                    DrawPlanarThickPolyline(lifted, color, highlighted ? 5.0f : 3.2f);
                else
                    DrawPolyline(lifted, color);
                if (!active) continue;
                for (int i = 1; i < lifted.Count - 1; i++)
                {
                    bool selectedWaypoint = edge.EdgeId == editor.SelectedNetworkEdgeId &&
                                            i == editor.SelectedNetworkWaypointIndex;
                    bool hoveredWaypoint = edge.EdgeId == editor.HoveredWaypointEdgeId &&
                                           i == editor.HoveredWaypointIndex;
                    Diamond(lifted[i], selectedWaypoint || hoveredWaypoint ? 5.2f : 3.8f,
                        selectedWaypoint || hoveredWaypoint
                            ? new Color(1f, 0.55f, 0.08f, 1f)
                            : new Color(0.78f, 0.31f, 0.17f, 1f));
                }
            }

            foreach (Node node in axis.Nodes.Where(node => node != null))
            {
                if (!IsPerLayerVisible(node.LayerId)) continue;
                bool active = node.LayerId == editor.ActiveLayerId;
                bool selected = node.NodeId == editor.SelectedNetworkNodeId;
                bool hovered = node.NodeId == editor.HoveredNetworkNodeId;
                Color color = selected || hovered
                    ? new Color(1f, 0.55f, 0.08f, 1f)
                    : active
                        ? new Color(0.78f, 0.31f, 0.17f, 1f)
                        : new Color(0.30f, 0.28f, 0.26f, 0.15f);
                float size = selected || hovered ? 8.0f : active ? 5.5f : 0.8f;
                QueueSolidDiamond(node.Position + Vector3.up * lift, size, color);
                if (selected) CircleLine(node.Position + Vector3.up * lift, 4.2f, color);
            }

            if (IsPerLayerVisible(editor.ActiveLayerId) &&
                editor.TryGetNetworkConnectionPreview(out Vector3 previewFrom, out Vector3 previewTo))
            {
                Color previewColor = new Color(1f, 0.55f, 0.08f, 0.92f);
                Segment(previewFrom + Vector3.up * lift, previewTo + Vector3.up * lift, previewColor);
                CircleLine(previewTo + Vector3.up * lift, 3.2f, previewColor);
            }
        }

        private void DrawRouteSelectionOverlay()
        {
            Network axis = manager.networkManager?.AxisNetwork;
            IEnumerable<Edge> edges = axis?.Edges;
            if (edges == null) edges = manager.Edges.Values;
            HashSet<string> selected = new HashSet<string>(routeDesigner.SelectedEdgeIds,
                StringComparer.Ordinal);
            const float lift = 0.48f;
            foreach (Edge edge in edges.Where(item => item?.Centerline != null && item.Centerline.Count > 1))
            {
                if (!IsNetworkLayerRenderable(edge.LayerId)) continue;
                bool hovered = edge.EdgeId == routeDesigner.HoveredEdgeId;
                bool included = selected.Contains(edge.EdgeId);
                Color color = hovered
                    ? new Color(1f, 0.62f, 0.08f, 1f)
                    : included
                        ? new Color(0.84f, 0.24f, 0.10f, 1f)
                        : new Color(0.25f, 0.22f, 0.19f, 0.82f);
                List<Vector3> lifted = edge.Centerline.Select(point => point + Vector3.up * lift).ToList();
                DrawPlanarThickPolyline(lifted, color, hovered ? 3.6f : included ? 2.8f : 1.3f);
            }
        }

        private void QueueSolidDiamond(Vector3 point, float size, Color color, bool billboard = false)
        {
            solidMarkers.Add(new SolidMarker
                { Point = point, Size = size, Color = color, Billboard = billboard });
        }

        private static void DrawSolidDiamond(SolidMarker marker)
        {
            Vector3 horizontal = marker.Billboard && Camera.current != null
                ? Camera.current.transform.right : Vector3.right;
            Vector3 vertical = marker.Billboard && Camera.current != null
                ? Camera.current.transform.up : Vector3.forward;
            Vector3 left = marker.Point - horizontal * marker.Size;
            Vector3 top = marker.Point + vertical * marker.Size;
            Vector3 right = marker.Point + horizontal * marker.Size;
            Vector3 bottom = marker.Point - vertical * marker.Size;
            GL.Color(marker.Color);
            GL.Vertex(left);
            GL.Vertex(top);
            GL.Vertex(right);
            GL.Vertex(left);
            GL.Vertex(right);
            GL.Vertex(bottom);
        }

        private void DrawGroundEditorPoints()
        {
            foreach (ApproachNode node in manager.ApproachNodes.Values)
            {
                if (manager.ApproachNodeToVertiportMap.TryGetValue(node.NodeId, out string vp) &&
                    vp == editor.ActiveVertiportId)
                    QueueSolidDiamond(node.Position + Vector3.up * 0.12f,
                        node.NodeId == editor.SelectedApproachNodeId ? 1.7f : 0.9f,
                        node.NodeId == editor.SelectedApproachNodeId
                            ? new Color(1f, 0.55f, 0.08f, 1f)
                            : new Color(0.10f, 0.50f, 0.68f, 1f));
            }

            if (manager.Vertiports.TryGetValue(editor.ActiveVertiportId ?? string.Empty, out Vertiport vertiport))
            {
                List<Vector3> boundary = vertiport.Boundary ?? new List<Vector3>();
                for (int i = 0; i < boundary.Count; i++)
                {
                    bool selected = i == editor.SelectedBoundaryPointIndex;
                    QueueSolidDiamond(boundary[i] + Vector3.up * 0.12f, selected ? 1.15f : 0.7f,
                        selected ? new Color(1f, 0.55f, 0.08f, 1f) : new Color(0.78f, 0.31f, 0.17f, 1f));
                }
                int segment = editor.SelectedBoundarySegmentIndex;
                if (segment >= 0 && boundary.Count > 1)
                    Segment(boundary[segment] + Vector3.up * 0.16f,
                        boundary[(segment + 1) % boundary.Count] + Vector3.up * 0.16f,
                        new Color(1f, 0.55f, 0.08f, 1f));
            }

            if (editor.TryGetVertiportBoundaryPreview(out List<Vector3> newBoundaryPreview))
            {
                Color boundaryColor = new Color(0.78f, 0.31f, 0.17f, 0.95f);
                if (newBoundaryPreview.Count > 1)
                    DrawPolyline(newBoundaryPreview.Select(point => point + Vector3.up * 0.2f).ToList(),
                        boundaryColor);
                foreach (Vector3 point in editor.PendingVertiportBoundary)
                    QueueSolidDiamond(point + Vector3.up * 0.2f, 0.85f, boundaryColor);
                if (editor.PendingVertiportBoundary.Count >= 3 &&
                    newBoundaryPreview.Count > editor.PendingVertiportBoundary.Count)
                    Segment(newBoundaryPreview[newBoundaryPreview.Count - 1] + Vector3.up * 0.2f,
                        editor.PendingVertiportBoundary[0] + Vector3.up * 0.2f,
                        new Color(boundaryColor.r, boundaryColor.g, boundaryColor.b, 0.45f));
            }

            if (editor.TryGetHelipadPreview(out List<Vector3> preview))
                DrawClosed(preview.Select(point => point + Vector3.up * 0.18f).ToList(),
                    new Color(0.10f, 0.55f, 0.47f, 0.85f));

            if (editor.TryGetApproachConnectionPreview(out Vector3 from, out Vector3 to))
            {
                Color color = new Color(1f, 0.55f, 0.08f, 0.92f);
                Segment(from + Vector3.up * 0.16f, to + Vector3.up * 0.16f, color);
                CircleLine(to + Vector3.up * 0.16f, 1.2f, color);
            }
        }

        private void DrawHoldingEditor()
        {
            foreach (HoldingStructure holding in editor.EnumerateActiveHoldingStructures())
            {
                bool selected = holding.HoldingStructureId == editor.SelectedHoldingStructureId;
                Color color = selected
                    ? new Color(1f, 0.55f, 0.08f, 1f)
                    : new Color(0.78f, 0.31f, 0.17f, 1f);
                QueueSolidDiamond(holding.Position, selected ? 1.35f : 0.85f, color, true);
                Cross(holding.Position, selected ? 1.8f : 1.15f, color);
                if (selected) CircleLine(holding.Position, 2.2f, color);
            }
        }

        private void DrawCorridorSelection()
        {
            string laneId = !string.IsNullOrEmpty(editor.SelectedLaneId)
                ? editor.SelectedLaneId : editor.HoveredLaneId;
            if (!string.IsNullOrEmpty(laneId) && manager.Lanes.TryGetValue(laneId, out Lane lane))
                DrawPolyline(lane.Centerline, new Color(1f, 0.55f, 0.08f, 1f));

            if (editor.CorridorEditMode == RuntimeCorridorEditMode.Facility)
            {
                string rampId = !string.IsNullOrEmpty(editor.SelectedRampId)
                    ? editor.SelectedRampId : editor.HoveredRampId;
                if (!string.IsNullOrEmpty(rampId) && manager.Ramps.TryGetValue(rampId, out Ramp ramp))
                    DrawPolyline(RuntimeAirspaceGeometry.BuildRampPolyline(
                            ramp, manager.Vertices, manager.Circles, 48),
                        new Color(1f, 0.55f, 0.08f, 1f));
            }

            if (editor.TryGetFacilityCorridorPreview(out List<Vector3> curve))
                DrawPolylineWithArrow(curve, new Color(0.18f, 0.62f, 0.38f, 0.85f), 1.45f);
        }

        private void DrawEditingGrid(float altitude, Color color)
        {
            Vector3 center = Vector3.zero;
            List<Vector3> points = manager.networkManager?.AxisNetwork?.Nodes?
                .Where(node => node != null).Select(node => node.Position).ToList() ?? new List<Vector3>();
            if (points.Count > 0) center = points.Aggregate(Vector3.zero, (sum, point) => sum + point) / points.Count;
            const float spacing = 50f;
            const int halfLines = 16;
            float cx = Mathf.Round(center.x / spacing) * spacing;
            float cz = Mathf.Round(center.z / spacing) * spacing;
            for (int i = -halfLines; i <= halfLines; i++)
            {
                float offset = i * spacing;
                Segment(new Vector3(cx - halfLines * spacing, altitude, cz + offset),
                    new Vector3(cx + halfLines * spacing, altitude, cz + offset), color);
                Segment(new Vector3(cx + offset, altitude, cz - halfLines * spacing),
                    new Vector3(cx + offset, altitude, cz + halfLines * spacing), color);
            }
        }

        private static Color LaneColor(string type)
        {
            switch (type)
            {
                case "Left": return new Color(0.20f, 0.42f, 0.82f, 0.95f);
                case "Right": return new Color(0.91f, 0.49f, 0.16f, 0.95f);
                case "StraightLeft": return new Color(0.10f, 0.58f, 0.58f, 0.95f);
                case "StraightRight": return new Color(0.48f, 0.65f, 0.13f, 0.95f);
                default: return new Color(0.16f, 0.62f, 0.35f, 0.9f);
            }
        }

        private static void DrawPolyline(IList<Vector3> points, Color color)
        {
            if (points == null) return;
            for (int i = 1; i < points.Count; i++) Segment(points[i - 1], points[i], color);
        }

        private static Vector3 PointAtPolylineRatio(IList<Vector3> points, float ratio)
        {
            if (points == null || points.Count == 0) return Vector3.zero;
            if (points.Count == 1) return points[0];
            float total = 0f;
            for (int i = 1; i < points.Count; i++) total += Vector3.Distance(points[i - 1], points[i]);
            float target = Mathf.Clamp01(ratio) * total;
            float walked = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                float length = Vector3.Distance(points[i - 1], points[i]);
                if (walked + length >= target && length > 0.001f)
                    return Vector3.Lerp(points[i - 1], points[i], (target - walked) / length);
                walked += length;
            }
            return points[points.Count - 1];
        }

        private static void DrawPolylineWithArrow(IList<Vector3> points, Color color, float arrowSize)
        {
            DrawPolyline(points, color);
            if (points == null || points.Count < 2) return;

            float total = 0f;
            for (int i = 1; i < points.Count; i++) total += Vector3.Distance(points[i - 1], points[i]);
            if (total <= 0.001f) return;

            float target = total * 0.58f;
            float walked = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 from = points[i - 1];
                Vector3 to = points[i];
                float length = Vector3.Distance(from, to);
                if (length <= 0.001f) continue;
                if (walked + length < target)
                {
                    walked += length;
                    continue;
                }

                Vector3 direction = (to - from).normalized;
                Vector3 point = Vector3.Lerp(from, to, Mathf.Clamp01((target - walked) / length));
                Vector3 side = Vector3.Cross(Vector3.up, direction);
                if (side.sqrMagnitude <= 0.001f) side = Vector3.Cross(Vector3.right, direction);
                side.Normalize();
                Vector3 back = -direction * arrowSize;
                Segment(point, point + back + side * arrowSize * 0.55f, color);
                Segment(point, point + back - side * arrowSize * 0.55f, color);
                return;
            }
        }

        private static void DrawPlanarThickPolyline(IList<Vector3> points, Color color, float halfWidth)
        {
            if (points == null) return;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 from = points[i - 1];
                Vector3 to = points[i];
                Vector3 direction = to - from;
                Vector3 right = Vector3.Cross(Vector3.up, direction).normalized;
                Segment(from, to, color);
                Segment(from + right * halfWidth, to + right * halfWidth, color);
                Segment(from - right * halfWidth, to - right * halfWidth, color);
                Segment(from + right * halfWidth * 0.5f, to + right * halfWidth * 0.5f, color);
                Segment(from - right * halfWidth * 0.5f, to - right * halfWidth * 0.5f, color);
            }
        }

        private static void DrawClosed(IList<Vector3> points, Color color)
        {
            DrawPolyline(points, color);
            if (points != null && points.Count > 2) Segment(points[points.Count - 1], points[0], color);
        }

        private static void CircleLine(Vector3 center, float radius, Color color)
        {
            const int steps = 32;
            Vector3 last = center + Vector3.right * radius;
            for (int i = 1; i <= steps; i++)
            {
                float angle = i * Mathf.PI * 2f / steps;
                Vector3 next = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                Segment(last, next, color);
                last = next;
            }
        }

        private static void Cross(Vector3 point, float size, Color color)
        {
            Segment(point - Vector3.right * size, point + Vector3.right * size, color);
            Segment(point - Vector3.forward * size, point + Vector3.forward * size, color);
            Segment(point - Vector3.up * size, point + Vector3.up * size, color);
        }

        private static void Diamond(Vector3 point, float size, Color color)
        {
            Vector3 left = point - Vector3.right * size;
            Vector3 top = point + Vector3.forward * size;
            Vector3 right = point + Vector3.right * size;
            Vector3 bottom = point - Vector3.forward * size;
            Segment(left, top, color);
            Segment(top, right, color);
            Segment(right, bottom, color);
            Segment(bottom, left, color);
        }

        private static void Segment(Vector3 from, Vector3 to, Color color)
        {
            GL.Color(color);
            GL.Vertex(from);
            GL.Vertex(to);
        }
    }
}
