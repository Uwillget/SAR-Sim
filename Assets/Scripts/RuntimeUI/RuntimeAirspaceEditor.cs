using System;
using System.Collections.Generic;
using System.Linq;
using RoadNetworkEditor.Networking;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RoadNetworkEditor.RuntimeUI
{
    public enum AirspaceEditWorkspace
    {
        NetworkLayers,
        GroundFacility,
        HoldingStructure,
        ConnectionCorridor
    }

    public enum RuntimeEditTool
    {
        Inspect,
        AddNetworkNode,
        ConnectNetworkNodes,
        AddNetworkWaypoint,
        MoveNetworkNode,
        MoveNetworkWaypoint,
        DrawVertiportBoundary,
        AddVertiportBoundaryPoint,
        MoveVertiportBoundaryPoint,
        AddHelipad,
        AddApproachNode,
        MoveApproachNode,
        ConnectApproachNodes,
        AddHoldingStructure,
        MoveHoldingStructure,
        ConnectHoldingVertices,
        AddFacilityCorridor,
        MoveFacilityCorridorLink,
        AddLayerConnector
    }

    public enum RuntimeAxisSelectionKind
    {
        None,
        Node,
        Edge,
        Waypoint
    }

    public enum RuntimeHelipadShape
    {
        Rectangle,
        Circle,
        Polygon
    }

    public enum RuntimeHoldingSelectionKind
    {
        None,
        Anchor,
        Circle,
        Vertex,
        Ramp
    }

    public enum RuntimeCorridorEditMode
    {
        Facility,
        Layer
    }

    /// <summary>
    /// Runtime netedit workflow. Planar resources are edited on locked horizontal planes;
    /// holding structures and corridors use the perspective scene view.
    /// </summary>
    public sealed class RuntimeAirspaceEditor : MonoBehaviour
    {
        public event Action<string> StatusChanged;
        public event Action StateChanged;

        public RuntimeEditTool Tool { get; private set; } = RuntimeEditTool.Inspect;
        public AirspaceEditWorkspace Workspace { get; private set; } = AirspaceEditWorkspace.NetworkLayers;
        public RuntimeCorridorEditMode CorridorEditMode { get; private set; } = RuntimeCorridorEditMode.Facility;
        public string ActiveVertiportId { get; private set; }
        public string SelectedHelipadId { get; private set; }
        public string ActiveLayerId { get; private set; }
        public float EditingAltitude { get; set; } = 75f;
        public string CorridorType { get; set; } = "Entry";

        public string SelectedNetworkNodeId => selectedNetworkNode;
        public string SelectedNetworkEdgeId => selectedNetworkEdge;
        public int SelectedNetworkWaypointIndex => selectedNetworkWaypointIndex;
        public RuntimeAxisSelectionKind AxisSelectionKind { get; private set; }
        public string SelectedApproachNodeId => firstApproachNode;
        public string SelectedLaneId => !string.IsNullOrEmpty(firstLane) ? firstLane : corridorLane;
        public string HoveredNetworkNodeId { get; private set; }
        public string HoveredNetworkEdgeId { get; private set; }
        public string HoveredWaypointEdgeId { get; private set; }
        public int HoveredWaypointIndex { get; private set; } = -1;
        public string HoveredLaneId { get; private set; }
        public bool HasPendingNetworkConnection => Tool == RuntimeEditTool.ConnectNetworkNodes &&
                                                   !string.IsNullOrEmpty(firstNetworkNode);
        public bool HasPendingApproachConnection => Tool == RuntimeEditTool.ConnectApproachNodes &&
                                                    !string.IsNullOrEmpty(firstApproachNode);
        public RuntimeHelipadShape HelipadShape { get; private set; } = RuntimeHelipadShape.Rectangle;
        public float HelipadWidth { get; private set; } = 8f;
        public float HelipadDepth { get; private set; } = 8f;
        public float HelipadRadius { get; private set; } = 4f;
        public float HelipadRotationDegrees { get; private set; }
        public IReadOnlyList<Vector3> PendingVertiportBoundary => pendingVertiportBoundary;
        public IReadOnlyList<Vector3> PendingHelipadPolygon => pendingHelipadPolygon;
        public int SelectedBoundarySegmentIndex { get; private set; } = -1;
        public int SelectedBoundaryPointIndex { get; private set; } = -1;
        public string SelectedHoldingStructureId { get; private set; }
        public string SelectedHoldingCircleId { get; private set; }
        public string SelectedHoldingVertexId { get; private set; }
        public string SelectedHoldingRampId { get; private set; }
        public RuntimeHoldingSelectionKind HoldingSelectionKind { get; private set; }
        public string SelectedFacilityCorridorId { get; private set; }
        public string SelectedLayerConnectorId { get; private set; }
        public string SelectedRampId => !string.IsNullOrEmpty(corridorRamp) ? corridorRamp : SelectedHoldingRampId;
        public string HoveredRampId { get; private set; }
        public float HoldingRingRadius { get; private set; } = 8f;
        public int HoldingRingVertexCount { get; private set; } = 8;
        public float HoldingRingInitialAngle { get; private set; }
        public ArcDirection HoldingRampDirection { get; private set; } = ArcDirection.CounterClockwise;
        public AltitudeLayer ActiveLayer => MultilayerNetworkUtility.FindLayer(AxisNetwork, ActiveLayerId);
        public bool ActiveLayerIsFrozen => ActiveLayer != null && ActiveLayer.Frozen;
        public bool ActiveLayerIsVisible => ActiveLayer == null ||
                                            visualizer == null ||
                                            visualizer.IsPerLayerVisible(ActiveLayer.LayerId);
        public IReadOnlyList<AltitudeLayer> Layers => AxisNetwork?.AltitudeLayers?
            .Where(layer => layer != null)
            .OrderByDescending(layer => layer.NominalAltitude)
            .ToList() ?? new List<AltitudeLayer>();

        private Network AxisNetwork => manager?.networkManager?.AxisNetwork;
        private SimulationManager manager;
        private RuntimeSceneNavigator navigator;
        private RuntimeAirspaceVisualizer visualizer;
        private readonly Stack<Action> undoActions = new Stack<Action>();
        private string firstNetworkNode;
        private string selectedNetworkNode;
        private string selectedNetworkEdge;
        private int selectedNetworkWaypointIndex = -1;
        private string firstApproachNode;
        private string firstLane;
        private Vector3 firstLayerConnectorPoint;
        private string corridorLane;
        private Node movingNode;
        private Edge movingWaypointEdge;
        private int movingWaypointIndex = -1;
        private AxisEditSnapshot movingAxisSnapshot;
        private readonly List<Vector3> pendingVertiportBoundary = new List<Vector3>();
        private readonly List<Vector3> pendingHelipadPolygon = new List<Vector3>();
        private int movingBoundaryPointIndex = -1;
        private List<Vector3> movingBoundaryBackup;
        private ApproachNode movingApproachNode;
        private Vector3 movingApproachNodeBackup;
        private float holdingAltitude = 75f;
        private string firstHoldingVertex;
        private HoldingStructure movingHoldingStructure;
        private Vector3 movingHoldingStructureBackup;
        private float movingHoldingStructureAltitude;
        private Vector3 corridorLanePoint;
        private string corridorRamp;
        private NetConnectedCorridor movingCorridor;
        private bool movingCorridorLanePoint;
        private Vector3 movingCorridorOldLanePoint;
        private Vector3 movingCorridorOldRampPoint;
        private LaneConnectionCorridor movingLayerConnector;
        private bool movingLayerConnectorFromPoint;
        private Vector3 movingLayerConnectorOldFromPoint;
        private Vector3 movingLayerConnectorOldToPoint;
        private bool? previousFullNetworkGizmoState;

        private sealed class AxisEditSnapshot
        {
            public Network Axis;
            public readonly List<FacilityCorridorBackup> FacilityCorridors = new List<FacilityCorridorBackup>();
        }

        private sealed class FacilityCorridorBackup
        {
            public string VertiportId;
            public NetConnectedCorridor Corridor;
        }

        public void Initialize(SimulationManager simulationManager, RuntimeSceneNavigator sceneNavigator,
            RuntimeAirspaceVisualizer airspaceVisualizer)
        {
            manager = simulationManager;
            navigator = sceneNavigator;
            visualizer = airspaceVisualizer;
            TakeOverLegacyAxisVisualization();
            manager.networkManager?.EnsureRuntimeEditingReady();
            manager.vertiportManager?.EnsureRuntimeEditingReady();
            ActiveVertiportId = manager.Vertiports.Keys.OrderBy(id => id).FirstOrDefault();
            SelectedHelipadId = ActiveVertiport()?.Helipads?.FirstOrDefault()?.HelipadId;
            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            AltitudeLayer initial = AxisNetwork?.AltitudeLayers?
                .Where(layer => layer != null)
                .OrderBy(layer => Mathf.Abs(layer.NominalAltitude - 75f))
                .FirstOrDefault();
            ActiveLayerId = initial?.LayerId;
            EditingAltitude = initial?.NominalAltitude ?? 75f;
            visualizer?.BindEditor(this);
            SetWorkspace(AirspaceEditWorkspace.NetworkLayers, false);
            SetTool(RuntimeEditTool.Inspect, false);
        }

        private void OnDestroy()
        {
            // Restore the legacy editor visualization state when the runtime workbench
            // is removed (for example when Unity Play Mode UI is disabled).  During the
            // workbench lifetime the runtime visualizer remains the single owner.
            if (manager?.networkManager == null) return;
            manager.networkManager.SetRuntimeAxisOverlayActive(false);
            if (previousFullNetworkGizmoState.HasValue)
                manager.networkManager.showFullNetwork = previousFullNetworkGizmoState.Value;
        }

        /// <summary>Reset editor selections after the scene workspace replaces all topology data.</summary>
        public void HandleSceneDataReloaded()
        {
            TakeOverLegacyAxisVisualization();
            manager.networkManager?.EnsureRuntimeEditingReady();
            manager.vertiportManager?.EnsureRuntimeEditingReady();
            selectedNetworkNode = null;
            selectedNetworkEdge = null;
            selectedNetworkWaypointIndex = -1;
            ResetCorridorSelection();
            ClearHoldingSelection();
            ActiveVertiportId = manager.Vertiports.Keys.OrderBy(id => id).FirstOrDefault();
            SelectedHelipadId = ActiveVertiport()?.Helipads?.FirstOrDefault()?.HelipadId;
            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            AltitudeLayer initial = AxisNetwork?.AltitudeLayers?.Where(layer => layer != null)
                .OrderBy(layer => Mathf.Abs(layer.NominalAltitude - 75f)).FirstOrDefault();
            ActiveLayerId = initial?.LayerId;
            EditingAltitude = initial?.NominalAltitude ?? 75f;
            SetWorkspace(Workspace, false);
            StateChanged?.Invoke();
        }

        public void SetWorkspace(AirspaceEditWorkspace value, bool notify = true)
        {
            // The runtime visualizer owns all axis/full-network rendering for every
            // workspace.  Releasing the overlay when switching to facilities or
            // corridors reactivates the legacy Nodes/Edges GameObjects and bypasses
            // global/per-layer visibility.  Keep the takeover for this editor's
            // complete runtime lifetime instead of tying it to one workspace.
            TakeOverLegacyAxisVisualization();
            Workspace = value;
            CancelPendingSelection();
            SetTool(RuntimeEditTool.Inspect, false);
            switch (value)
            {
                case AirspaceEditWorkspace.NetworkLayers:
                    if (ActiveLayer != null) EditingAltitude = ActiveLayer.NominalAltitude;
                    navigator?.SetPreset(SceneViewPreset.Top);
                    break;
                case AirspaceEditWorkspace.GroundFacility:
                    EditingAltitude = 0f;
                    navigator?.SetPreset(SceneViewPreset.Top);
                    break;
                case AirspaceEditWorkspace.HoldingStructure:
                    EditingAltitude = holdingAltitude;
                    navigator?.SetPreset(SceneViewPreset.Top);
                    FocusActiveHoldingArea(false);
                    break;
                default:
                    navigator?.SetPreset(SceneViewPreset.Top);
                    break;
            }
            StateChanged?.Invoke();
            if (notify) Raise($"Editing workspace: {WorkspaceName(value)}. ");
        }

        public void SetTool(RuntimeEditTool value, bool notify = true)
        {
            if (Workspace == AirspaceEditWorkspace.NetworkLayers &&
                IsNetworkMutationTool(value) && ActiveLayerIsFrozen)
            {
                Tool = RuntimeEditTool.Inspect;
                CancelPendingSelection();
                StateChanged?.Invoke();
                if (notify) Raise("The current network layer is frozen. Click the snowflake button to unfreeze it first. ");
                return;
            }
            Tool = value;
            CancelPendingSelection();
            if (Workspace == AirspaceEditWorkspace.GroundFacility && value == RuntimeEditTool.Inspect)
                FocusActiveVertiport();
            StateChanged?.Invoke();
            if (notify) Raise($"Editing tool: {ToolName(value)}. ");
        }

        public void SetCorridorEditMode(RuntimeCorridorEditMode value, bool notify = true)
        {
            CorridorEditMode = value;
            ResetCorridorSelection();
            Tool = RuntimeEditTool.Inspect;
            CancelPendingSelection();
            StateChanged?.Invoke();
            if (notify)
                Raise(value == RuntimeCorridorEditMode.Facility
                    ? "Connection-corridor mode: vertiport corridor editing. "
                    : "Connection-corridor mode: interlayer corridor editing. ");
        }

        public bool SetActiveVertiport(string vertiportId, bool focus = false)
        {
            if (string.IsNullOrEmpty(vertiportId) || !manager.Vertiports.ContainsKey(vertiportId)) return false;
            ActiveVertiportId = vertiportId;
            SelectedHelipadId = ActiveVertiport()?.Helipads?.FirstOrDefault()?.HelipadId;
            pendingHelipadPolygon.Clear();
            SelectedBoundarySegmentIndex = -1;
            SelectedBoundaryPointIndex = -1;
            if (focus) FocusActiveVertiport();
            StateChanged?.Invoke();
            Raise($"Active vertiport: {vertiportId}. ");
            return true;
        }

        public IEnumerable<HoldingStructure> EnumerateActiveHoldingStructures()
        {
            return ActiveVertiport()?.HoldingStructures?.Where(holding => holding != null) ??
                   Enumerable.Empty<HoldingStructure>();
        }

        public void FocusActiveVertiport()
        {
            Vertiport vertiport = ActiveVertiport();
            if (vertiport?.Boundary == null || vertiport.Boundary.Count == 0) return;
            navigator?.FramePoints(vertiport.Boundary, 1.35f, true);
            Raise($"Focused vertiport {vertiport.VertiportId}. ");
        }

        public bool SetActiveHelipad(string helipadId, bool focus = false)
        {
            Vertiport vertiport = ActiveVertiport();
            Helipad pad = vertiport?.Helipads?.FirstOrDefault(item => item != null && item.HelipadId == helipadId);
            if (pad == null) return false;
            SelectedHelipadId = pad.HelipadId;
            if (focus) FocusActiveHelipad();
            StateChanged?.Invoke();
            Raise($"Active helipad: {pad.HelipadId}. ");
            return true;
        }

        public void FocusActiveHelipad()
        {
            Vertiport vertiport = ActiveVertiport();
            Helipad pad = vertiport?.Helipads?.FirstOrDefault(item => item != null &&
                item.HelipadId == SelectedHelipadId);
            if (pad?.Boundary == null || pad.Boundary.Count == 0)
            {
                Raise("Select a helipad from the menu first. ");
                return;
            }
            navigator?.FramePoints(pad.Boundary, 1.8f, true);
            Raise($"Focused helipad {pad.HelipadId}. ");
        }

        public void FocusActiveHoldingArea(bool notify = true)
        {
            Vertiport vertiport = ActiveVertiport();
            if (vertiport == null) return;
            var points = new List<Vector3>();
            foreach (HoldingStructure holding in vertiport.HoldingStructures ?? new List<HoldingStructure>())
            {
                points.Add(holding.Position);
                points.AddRange((holding.Circles ?? new List<Circle>()).Select(circle => circle.Center));
                points.AddRange((holding.Vertices ?? new List<Vertex>()).Select(vertex => vertex.Position));
            }
            if (points.Count == 0)
                points.AddRange(vertiport.Boundary ?? new List<Vector3>());
            if (points.Count > 0) navigator?.FramePoints(points, 1.55f, false);
            if (notify) Raise($"Focused the holding structures of {vertiport.VertiportId}. Right-click to switch to free 3D navigation. ");
        }

        public void ConfigureHelipad(RuntimeHelipadShape shape, float width, float depth, float radius,
            float rotationDegrees)
        {
            HelipadShape = shape;
            HelipadWidth = Mathf.Max(1f, width);
            HelipadDepth = Mathf.Max(1f, depth);
            HelipadRadius = Mathf.Max(0.5f, radius);
            HelipadRotationDegrees = Mathf.Repeat(rotationDegrees, 360f);
            if (shape != RuntimeHelipadShape.Polygon) pendingHelipadPolygon.Clear();
            StateChanged?.Invoke();
        }

        public void CancelVertiportBoundary()
        {
            pendingVertiportBoundary.Clear();
            StateChanged?.Invoke();
            Raise("New vertiport-boundary drawing cancelled. ");
        }

        public bool FinishVertiportBoundary()
        {
            if (pendingVertiportBoundary.Count < 3)
            {
                Raise("A new vertiport boundary requires at least three points. ");
                return false;
            }
            List<Vector3> boundary = new List<Vector3>(pendingVertiportBoundary);
            if (Mathf.Abs(SignedAreaXZ(boundary)) < 1f)
            {
                Raise("The new vertiport boundary is too small or its points are collinear. Draw it again. ");
                return false;
            }
            VertiportVisual visual = manager.vertiportManager?.CreateVertiport(boundary);
            if (visual?.VertiportData == null)
            {
                Raise("The new vertiport could not be created. ");
                return false;
            }

            string createdId = visual.VertiportData.VertiportId;
            pendingVertiportBoundary.Clear();
            manager.RefreshRuntimeTopology();
            ActiveVertiportId = createdId;
            SelectedHelipadId = null;
            undoActions.Push(() =>
            {
                manager.vertiportManager.DeleteVertiport(createdId);
                manager.RefreshRuntimeTopology();
            });
            SetTool(RuntimeEditTool.Inspect, false);
            FocusActiveVertiport();
            StateChanged?.Invoke();
            Raise($"Vertiport {createdId} created. You can now design its internal structure. ");
            return true;
        }

        public void CancelHelipadPolygon()
        {
            pendingHelipadPolygon.Clear();
            StateChanged?.Invoke();
            Raise("Freehand helipad drawing cancelled. ");
        }

        public bool FinishHelipadPolygon()
        {
            if (pendingHelipadPolygon.Count < 3)
            {
                Raise("A freehand helipad requires at least three boundary points. ");
                return false;
            }
            List<Vector3> boundary = RotateAroundCentroid(new List<Vector3>(pendingHelipadPolygon),
                HelipadRotationDegrees);
            pendingHelipadPolygon.Clear();
            return CreateHelipad(boundary, "freehand polygon");
        }

        public bool SetActiveLayer(string layerId)
        {
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(AxisNetwork, layerId);
            if (layer == null) return false;
            ActiveLayerId = layer.LayerId;
            EditingAltitude = layer.NominalAltitude;
            if (Workspace == AirspaceEditWorkspace.NetworkLayers) navigator?.SetPreset(SceneViewPreset.Top);
            CancelPendingSelection();
            StateChanged?.Invoke();
            Raise($"Active network layer: {SarRuntimeLocalization.Literal(LayerLabel(layer))}. ");
            return true;
        }

        public bool IsLayerVisible(string layerId)
        {
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(AxisNetwork, layerId);
            return layer == null || visualizer == null || visualizer.IsPerLayerVisible(layer.LayerId);
        }

        public bool IsLayerFrozen(string layerId)
        {
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(AxisNetwork, layerId);
            return layer != null && layer.Frozen;
        }

        public bool SetLayerVisible(string layerId, bool visible)
        {
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(AxisNetwork, layerId);
            if (layer == null) return false;
            if (IsLayerVisible(layerId) == visible) return true;
            visualizer?.SetDisplayLayerVisible(layer.LayerId, visible);
            StateChanged?.Invoke();
            Raise($"Network layer \"{SarRuntimeLocalization.Literal(LayerLabel(layer))}\" is now {(visible ? "visible" : "hidden")}. ");
            return true;
        }

        public bool SetLayerFrozen(string layerId, bool frozen)
        {
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(AxisNetwork, layerId);
            if (layer == null) return false;
            bool changed = layer.Frozen != frozen;
            layer.Frozen = frozen;
            changed |= SyncDerivedLayerMetadata(layer, copyVisibility: false, copyFrozen: true);
            if (!changed) return true;
            if (frozen && layer.LayerId == ActiveLayerId)
            {
                Tool = RuntimeEditTool.Inspect;
                CancelPendingSelection();
            }
            StateChanged?.Invoke();
            Raise($"Network layer \"{SarRuntimeLocalization.Literal(LayerLabel(layer))}\" is now {(frozen ? "frozen" : "unfrozen")}. ");
            return true;
        }

        public void SetHoldingAltitude(float altitude)
        {
            holdingAltitude = altitude;
            if (Workspace == AirspaceEditWorkspace.HoldingStructure) EditingAltitude = altitude;
            StateChanged?.Invoke();
            Raise($"Holding-structure placement altitude: {altitude:F1} m. ");
        }

        public void ConfigureHoldingRing(float radius, int vertexCount, float initialAngle,
            ArcDirection direction = ArcDirection.CounterClockwise)
        {
            HoldingRingRadius = Mathf.Max(0.5f, radius);
            HoldingRingVertexCount = Mathf.Clamp(vertexCount, 2, 64);
            HoldingRingInitialAngle = Mathf.Repeat(initialAngle, 360f);
            HoldingRampDirection = direction;
            StateChanged?.Invoke();
        }

        public bool CreateHoldingCircle()
        {
            HoldingStructure holding = FindSelectedHoldingStructure();
            if (holding == null)
            {
                Raise("Create or select a holding-structure anchor in View mode first. ");
                return false;
            }
            Vector3 center = new Vector3(holding.Position.x, holdingAltitude, holding.Position.z);
            CircleVisual visual = manager.vertiportManager.CreateCircle(holding.HoldingStructureId, center,
                HoldingRingRadius, HoldingRingVertexCount, HoldingRingInitialAngle);
            if (visual == null) { Raise("The holding ring could not be created. "); return false; }
            SelectedHoldingStructureId = holding.HoldingStructureId;
            SelectedHoldingCircleId = visual.CircleId;
            HoldingSelectionKind = RuntimeHoldingSelectionKind.Circle;
            undoActions.Push(() => manager.vertiportManager.DeleteCircle(visual.CircleId));
            RefreshTopology();
            StateChanged?.Invoke();
            Raise($"Holding ring {visual.CircleId} generated: radius {HoldingRingRadius:F1} m, " +
                  $"{HoldingRingVertexCount} vertices, initial angle {HoldingRingInitialAngle:F1}°. ");
            return true;
        }

        public bool UpdateSelectedHoldingCircle()
        {
            if (string.IsNullOrEmpty(SelectedHoldingCircleId) ||
                !manager.Circles.TryGetValue(SelectedHoldingCircleId, out Circle circle))
            {
                Raise("Select a holding ring in View mode first. ");
                return false;
            }
            Vector3 center = circle.Center;
            center.y = holdingAltitude;
            manager.vertiportManager.UpdateCircle(circle.CircleId, center, HoldingRingRadius,
                HoldingRingVertexCount, HoldingRingInitialAngle);
            RefreshTopology();
            StateChanged?.Invoke();
            Raise($"Holding ring {circle.CircleId} updated. Use Connect Holding Vertices to create the required one-way ramps. ");
            return true;
        }

        public bool DeleteSelectedHoldingElement()
        {
            switch (HoldingSelectionKind)
            {
                case RuntimeHoldingSelectionKind.Anchor:
                    manager.vertiportManager.DeleteHoldingStructure(SelectedHoldingStructureId);
                    break;
                case RuntimeHoldingSelectionKind.Circle:
                    manager.vertiportManager.DeleteCircle(SelectedHoldingCircleId);
                    break;
                case RuntimeHoldingSelectionKind.Vertex:
                    manager.vertiportManager.DeleteVertex(SelectedHoldingVertexId);
                    break;
                case RuntimeHoldingSelectionKind.Ramp:
                    manager.vertiportManager.DeleteRamp(SelectedHoldingRampId);
                    break;
                default:
                    Raise("Select a holding-structure object in View mode first. ");
                    return false;
            }
            string deleted = HoldingSelectionKind.ToString();
            ClearHoldingSelection();
            RefreshTopology();
            StateChanged?.Invoke();
            Raise($"Holding-structure object {deleted} deleted. ");
            return true;
        }

        public AltitudeLayer AddLayer()
        {
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            float highest = AxisNetwork?.AltitudeLayers?.Where(layer => layer != null)
                .Select(layer => layer.NominalAltitude).DefaultIfEmpty(25f).Max() ?? 25f;
            AltitudeLayer layer = MultilayerNetworkUtility.AddAxisLayer(AxisNetwork, highest + 50f);
            PushAxisUndo(snapshot);
            ActiveLayerId = layer.LayerId;
            EditingAltitude = layer.NominalAltitude;
            RebuildLayeredNetwork();
            StateChanged?.Invoke();
            Raise($"Created {SarRuntimeLocalization.Literal(LayerLabel(layer))}. ");
            return layer;
        }

        public bool SetActiveLayerAltitude(float altitude)
        {
            AltitudeLayer layer = ActiveLayer;
            if (layer == null) return false;
            if (!RequireEditableActiveLayer("change the layer altitude")) return false;
            float old = layer.NominalAltitude;
            if (Mathf.Approximately(old, altitude)) return true;
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            PushAxisUndo(snapshot);
            MultilayerNetworkUtility.SetAxisLayerAltitude(AxisNetwork, layer.LayerId, altitude);
            EditingAltitude = altitude;
            RebuildLayeredNetwork();
            StateChanged?.Invoke();
            Raise($"Altitude of {layer.LayerId} set to {altitude:F1} m; planar coordinates were preserved. ");
            return true;
        }

        public bool RenameActiveLayer(string displayName)
        {
            if (ActiveLayer == null || string.IsNullOrWhiteSpace(displayName))
            {
                Raise("Layer name cannot be empty. ");
                return false;
            }
            if (!RequireEditableActiveLayer("rename the layer")) return false;
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            if (!MultilayerNetworkUtility.RenameAxisLayer(AxisNetwork, ActiveLayerId, displayName)) return false;
            PushAxisUndo(snapshot);
            RebuildLayeredNetwork();
            StateChanged?.Invoke();
            Raise($"Layer renamed to \"{SarRuntimeLocalization.Literal(ActiveLayer.DisplayName)}\". ");
            return true;
        }

        public AltitudeLayer DuplicateActiveLayer()
        {
            if (ActiveLayer == null) return null;
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            float highest = AxisNetwork.AltitudeLayers.Where(layer => layer != null)
                .Select(layer => layer.NominalAltitude).DefaultIfEmpty(25f).Max();
            AltitudeLayer copy = MultilayerNetworkUtility.DuplicateAxisLayer(
                AxisNetwork, ActiveLayerId, highest + 50f);
            if (copy == null) return null;
            PushAxisUndo(snapshot);
            ActiveLayerId = copy.LayerId;
            EditingAltitude = copy.NominalAltitude;
            RebuildLayeredNetwork();
            StateChanged?.Invoke();
            Raise($"Layer duplicated in full: {SarRuntimeLocalization.Literal(LayerLabel(copy))}. ");
            return copy;
        }

        public bool DeleteActiveLayer()
        {
            if (ActiveLayer == null) return false;
            if (!RequireEditableActiveLayer("delete the layer")) return false;
            if (AxisNetwork.AltitudeLayers.Count <= 1)
            {
                Raise("At least one network layer must be retained. ");
                return false;
            }
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            string deletedId = ActiveLayerId;
            if (!MultilayerNetworkUtility.DeleteAxisLayer(AxisNetwork, deletedId)) return false;
            PushAxisUndo(snapshot);
            AltitudeLayer next = AxisNetwork.AltitudeLayers.Where(layer => layer != null)
                .OrderBy(layer => Mathf.Abs(layer.NominalAltitude - EditingAltitude)).FirstOrDefault();
            ActiveLayerId = next?.LayerId;
            EditingAltitude = next?.NominalAltitude ?? 75f;
            ClearNetworkSelection(false);
            RebuildLayeredNetwork();
            StateChanged?.Invoke();
            Raise($"Layer {deletedId} deleted together with its nodes, edges, and waypoints. ");
            return true;
        }

        public bool ReorderActiveLayer(int targetDescendingIndex)
        {
            if (ActiveLayer == null) return false;
            if (!CanReorderActiveLayerTo(targetDescendingIndex, out AltitudeLayer blockingLayer))
            {
                Raise(blockingLayer == ActiveLayer
                    ? "The current network layer is frozen, so its order cannot be changed. "
                    : $"Layer order cannot cross frozen layer \"{SarRuntimeLocalization.Literal(LayerLabel(blockingLayer))}\". ");
                return false;
            }
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            if (!MultilayerNetworkUtility.ReorderAxisLayer(AxisNetwork, ActiveLayerId, targetDescendingIndex))
                return false;
            PushAxisUndo(snapshot);
            EditingAltitude = ActiveLayer?.NominalAltitude ?? EditingAltitude;
            RebuildLayeredNetwork();
            StateChanged?.Invoke();
            Raise($"Layer moved to position {targetDescendingIndex + 1}; all layer altitudes were reassigned. ");
            return true;
        }

        public bool MoveActiveLayer(int direction)
        {
            if (!CanSwapActiveLayer(direction, out AltitudeLayer blockingLayer))
            {
                if (blockingLayer != null)
                    Raise(blockingLayer == ActiveLayer
                        ? "The current network layer is frozen, so its order cannot be changed. "
                        : $"Adjacent layer \"{SarRuntimeLocalization.Literal(LayerLabel(blockingLayer))}\" is frozen and cannot exchange positions. ");
                else
                    Raise(direction > 0 ? "This is already the highest layer. " : "This is already the lowest layer. ");
                return false;
            }
            if (!MultilayerNetworkUtility.SwapAxisLayerOrder(AxisNetwork, ActiveLayerId, direction))
            {
                Raise(direction > 0 ? "This is already the highest layer. " : "This is already the lowest layer. ");
                return false;
            }
            undoActions.Push(() => MultilayerNetworkUtility.SwapAxisLayerOrder(AxisNetwork, ActiveLayerId, -direction));
            EditingAltitude = ActiveLayer?.NominalAltitude ?? EditingAltitude;
            RebuildLayeredNetwork();
            StateChanged?.Invoke();
            Raise($"Layer order exchanged; {ActiveLayerId} is now at {EditingAltitude:F1} m. ");
            return true;
        }

        public void Undo()
        {
            if (manager == null || manager.IsRunning()) { Raise("Stop the simulation first. "); return; }
            if (undoActions.Count == 0) { Raise("There are no edits to undo. "); return; }
            undoActions.Pop()?.Invoke();
            RebuildLayeredNetwork();
            manager.vertiportManager?.RebuildVisualsFromData();
            RefreshTopology();
            StateChanged?.Invoke();
            Raise("The previous edit was undone. ");
        }

        public void Export()
        {
            manager?.networkManager?.ExportAxisNetworkToJSON();
            manager?.networkManager?.ExportFullNetworkToJSON();
            manager?.vertiportManager?.ExportToJSON();
            Raise("Network and vertiport data validated and exported. ");
        }

        private void Update()
        {
            if (manager == null || navigator?.ControlledCamera == null) return;
            if (manager.IsRunning()) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                ClearHover();
                return;
            }

            if (Workspace == AirspaceEditWorkspace.NetworkLayers &&
                IsNetworkMutationTool(Tool) && ActiveLayerIsFrozen)
            {
                Tool = RuntimeEditTool.Inspect;
                CancelPendingSelection();
                StateChanged?.Invoke();
                Raise("The current network layer is frozen; the edit was cancelled. ");
                return;
            }

            UpdateHover();

            if (Tool == RuntimeEditTool.Inspect)
            {
                if (Workspace == AirspaceEditWorkspace.NetworkLayers && Input.GetMouseButtonDown(0))
                    InspectNetworkAtCursor();
                else if (Workspace == AirspaceEditWorkspace.HoldingStructure && Input.GetMouseButtonDown(0))
                    InspectHoldingAtCursor();
                else if (Workspace == AirspaceEditWorkspace.ConnectionCorridor && Input.GetMouseButtonDown(0))
                    InspectFacilityCorridorAtCursor();
                return;
            }

            if (Tool == RuntimeEditTool.MoveNetworkNode)
            {
                HandleMove();
                return;
            }
            if (Tool == RuntimeEditTool.MoveNetworkWaypoint)
            {
                HandleWaypointMove();
                return;
            }
            if (Tool == RuntimeEditTool.MoveVertiportBoundaryPoint)
            {
                HandleBoundaryPointMove();
                return;
            }
            if (Tool == RuntimeEditTool.MoveApproachNode)
            {
                HandleApproachNodeMove();
                return;
            }
            if (Tool == RuntimeEditTool.MoveHoldingStructure)
            {
                HandleHoldingStructureMove();
                return;
            }
            if (Tool == RuntimeEditTool.MoveFacilityCorridorLink)
            {
                HandleFacilityCorridorLinkMove();
                return;
            }
            if (!Input.GetMouseButtonDown(0)) return;

            switch (Tool)
            {
                case RuntimeEditTool.AddNetworkNode:
                    if (TryWorldPoint(out Vector3 networkPoint)) AddNetworkNode(networkPoint);
                    break;
                case RuntimeEditTool.ConnectNetworkNodes: ConnectNetworkNodes(); break;
                case RuntimeEditTool.AddNetworkWaypoint: AddNetworkWaypoint(); break;
                case RuntimeEditTool.DrawVertiportBoundary:
                    if (TryWorldPoint(out Vector3 boundaryPoint)) AddPendingVertiportBoundaryPoint(boundaryPoint);
                    break;
                case RuntimeEditTool.AddVertiportBoundaryPoint: AddVertiportBoundaryPoint(); break;
                case RuntimeEditTool.AddHelipad:
                    if (TryWorldPoint(out Vector3 helipadPoint)) AddHelipad(helipadPoint);
                    break;
                case RuntimeEditTool.AddHoldingStructure:
                    if (TryWorldPoint(out Vector3 holdingPoint)) AddHolding(holdingPoint);
                    break;
                case RuntimeEditTool.AddApproachNode:
                    if (TryWorldPoint(out Vector3 approachPoint)) AddApproachNode(approachPoint);
                    break;
                case RuntimeEditTool.ConnectApproachNodes: ConnectApproachNodes(); break;
                case RuntimeEditTool.ConnectHoldingVertices: ConnectHoldingVertices(); break;
                case RuntimeEditTool.AddFacilityCorridor: AddFacilityCorridor(); break;
                case RuntimeEditTool.AddLayerConnector: AddLayerConnector(); break;
            }
        }

        private void AddNetworkNode(Vector3 point)
        {
            if (!RequireEditableActiveLayer("add a node")) return;
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            point.y = ActiveLayer?.NominalAltitude ?? EditingAltitude;
            NodeVisual visual = manager.networkManager?.CreateNode(point, ActiveLayerId);
            if (visual == null) { Raise("The node could not be created. "); return; }
            string id = visual.NodeData.NodeId;
            PushAxisUndo(snapshot);
            RefreshNetwork();
            Raise($"Node {id} created on the {ActiveLayerId} centerline. ");
        }

        private void ConnectNetworkNodes()
        {
            if (!RequireEditableActiveLayer("connect nodes")) return;
            Node node = NearestNode();
            if (node == null) { Raise("Click a centerline node on the current layer; it highlights when the pointer approaches. "); return; }
            if (string.IsNullOrEmpty(firstNetworkNode))
            {
                firstNetworkNode = node.NodeId;
                selectedNetworkNode = node.NodeId;
                selectedNetworkEdge = null;
                selectedNetworkWaypointIndex = -1;
                AxisSelectionKind = RuntimeAxisSelectionKind.Node;
                StateChanged?.Invoke();
                Raise($"Start node {node.NodeId} locked. Select an end node on the same layer. ");
                return;
            }
            if (firstNetworkNode == node.NodeId) return;
            Node first = AxisNetwork.Nodes.FirstOrDefault(item => item.NodeId == firstNetworkNode);
            if (first?.LayerId != node.LayerId)
            {
                Raise("A standard network edge can only connect nodes on the same altitude layer. Use an interlayer corridor to connect layers. ");
                return;
            }
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            EdgeVisual visual = manager.networkManager.CreateEdge(firstNetworkNode, node.NodeId);
            firstNetworkNode = null;
            StateChanged?.Invoke();
            if (visual == null) { Raise("The edge could not be created. "); return; }
            string id = visual.EdgeData.EdgeId;
            selectedNetworkNode = null;
            selectedNetworkEdge = id;
            selectedNetworkWaypointIndex = -1;
            AxisSelectionKind = RuntimeAxisSelectionKind.Edge;
            PushAxisUndo(snapshot);
            RefreshNetwork();
            Raise($"Centerline edge {id} created on {ActiveLayerId}. ");
        }

        private void AddNetworkWaypoint()
        {
            if (!RequireEditableActiveLayer("add a waypoint")) return;
            if (!NearestEdgeSegment(out Edge edge, out int insertIndex, out Vector3 point))
            {
                Raise("Click near a centerline edge on the current layer to insert a waypoint. ");
                return;
            }
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            point.y = ActiveLayer?.NominalAltitude ?? EditingAltitude;
            if (!manager.networkManager.InsertAxisWaypoint(edge.EdgeId, point, insertIndex))
            {
                Raise("The waypoint could not be inserted because the centerline-edge data is invalid. ");
                return;
            }
            selectedNetworkEdge = edge.EdgeId;
            selectedNetworkNode = null;
            selectedNetworkWaypointIndex = insertIndex;
            AxisSelectionKind = RuntimeAxisSelectionKind.Waypoint;
            PushAxisUndo(snapshot);
            RefreshNetwork();
            StateChanged?.Invoke();
            Raise($"Waypoint added to centerline edge {edge.EdgeId}. ");
        }

        private void HandleMove()
        {
            if (!RequireEditableActiveLayer("move a node")) return;
            if (Input.GetMouseButtonDown(0))
            {
                movingNode = NearestNode();
                if (movingNode != null)
                {
                    movingAxisSnapshot = CaptureAxisSnapshot();
                    firstNetworkNode = movingNode.NodeId;
                    selectedNetworkNode = movingNode.NodeId;
                    selectedNetworkEdge = null;
                    selectedNetworkWaypointIndex = -1;
                    AxisSelectionKind = RuntimeAxisSelectionKind.Node;
                    StateChanged?.Invoke();
                }
            }
            if (movingNode != null && Input.GetMouseButton(0) && TryWorldPoint(out Vector3 point))
            {
                point.y = ActiveLayer?.NominalAltitude ?? EditingAltitude;
                manager.networkManager.UpdateNodePosition(movingNode.NodeId, point);
            }
            if (movingNode != null && Input.GetMouseButtonUp(0))
            {
                string id = movingNode.NodeId;
                PushAxisUndo(movingAxisSnapshot);
                movingNode = null;
                movingAxisSnapshot = null;
                firstNetworkNode = null;
                RefreshNetwork();
                StateChanged?.Invoke();
                Raise($"Node {id} moved within the {ActiveLayerId} plane. ");
            }
        }

        private void HandleWaypointMove()
        {
            if (!RequireEditableActiveLayer("move a waypoint")) return;
            if (Input.GetMouseButtonDown(0) && NearestWaypoint(out Edge edge, out int index))
            {
                movingWaypointEdge = edge;
                movingWaypointIndex = index;
                movingAxisSnapshot = CaptureAxisSnapshot();
                selectedNetworkNode = null;
                selectedNetworkEdge = edge.EdgeId;
                selectedNetworkWaypointIndex = index;
                AxisSelectionKind = RuntimeAxisSelectionKind.Waypoint;
                StateChanged?.Invoke();
            }
            if (movingWaypointEdge != null && Input.GetMouseButton(0) && TryWorldPoint(out Vector3 point))
            {
                point.y = ActiveLayer?.NominalAltitude ?? EditingAltitude;
                manager.networkManager.UpdateAxisWaypoint(movingWaypointEdge.EdgeId, movingWaypointIndex, point);
            }
            if (movingWaypointEdge != null && Input.GetMouseButtonUp(0))
            {
                string edgeId = movingWaypointEdge.EdgeId;
                int movedIndex = movingWaypointIndex;
                PushAxisUndo(movingAxisSnapshot);
                movingWaypointEdge = null;
                movingWaypointIndex = -1;
                movingAxisSnapshot = null;
                RefreshNetwork();
                StateChanged?.Invoke();
                Raise($"Waypoint {movedIndex} of {edgeId} moved within the {ActiveLayerId} plane. ");
            }
        }

        private void AddVertiportBoundaryPoint()
        {
            Vertiport vertiport = ActiveVertiport();
            if (vertiport == null) { Raise("Select a vertiport first. "); return; }
            if (!NearestBoundarySegment(out int segmentIndex, out Vector3 point))
            {
                Raise("Click an existing boundary segment. The new point will be inserted into that segment and locked to ground altitude. ");
                return;
            }
            List<Vector3> old = new List<Vector3>(vertiport.Boundary ?? new List<Vector3>());
            List<Vector3> updated = new List<Vector3>(old);
            int insertIndex = Mathf.Clamp(segmentIndex + 1, 0, updated.Count);
            updated.Insert(insertIndex, point);
            manager.vertiportManager.UpdateVertiportBoundary(vertiport.VertiportId, updated);
            undoActions.Push(() => manager.vertiportManager.UpdateVertiportBoundary(vertiport.VertiportId, old));
            SelectedBoundarySegmentIndex = segmentIndex;
            SelectedBoundaryPointIndex = insertIndex;
            RefreshTopology();
            StateChanged?.Invoke();
            Raise($"Boundary point {insertIndex} inserted into segment {segmentIndex}. ");
        }

        private void AddPendingVertiportBoundaryPoint(Vector3 point)
        {
            point.y = 0f;
            if (pendingVertiportBoundary.Count > 0 &&
                Vector3.Distance(pendingVertiportBoundary[pendingVertiportBoundary.Count - 1], point) < 1f)
            {
                Raise("The new boundary point is too close to the previous point. Choose another position. ");
                return;
            }
            pendingVertiportBoundary.Add(point);
            StateChanged?.Invoke();
            Raise($"New vertiport boundary point {pendingVertiportBoundary.Count} drawn. At least three points are required to finish the boundary. ");
        }

        private void AddHelipad(Vector3 point)
        {
            if (string.IsNullOrEmpty(ActiveVertiportId)) { Raise("Select a vertiport first. "); return; }
            point.y = 0f;
            if (!IsInsideActiveVertiport(point))
            {
                Raise("Helipad boundary points must lie within the active vertiport. ");
                return;
            }
            if (HelipadShape == RuntimeHelipadShape.Polygon)
            {
                pendingHelipadPolygon.Add(point);
                StateChanged?.Invoke();
                Raise($"Freehand helipad boundary point {pendingHelipadPolygon.Count} added. Click Finish Polygon when done. ");
                return;
            }
            List<Vector3> boundary = HelipadShape == RuntimeHelipadShape.Circle
                ? BuildCircle(point, HelipadRadius, 24)
                : BuildRectangle(point, HelipadWidth, HelipadDepth, HelipadRotationDegrees);
            CreateHelipad(boundary, HelipadShape == RuntimeHelipadShape.Circle ? "circular" : "rectangular");
        }

        private bool CreateHelipad(List<Vector3> boundary, string shapeLabel)
        {
            if (boundary == null || boundary.Count < 3 || boundary.Any(point => !IsInsideActiveVertiport(point)))
            {
                Raise("The entire helipad boundary must lie within the active vertiport. ");
                return false;
            }
            HelipadVisual visual = manager.vertiportManager.CreateHelipad(ActiveVertiportId, boundary);
            if (visual == null) { Raise("The helipad could not be created. Ensure the selected position is inside the vertiport boundary. "); return false; }
            string id = visual.HelipadData.HelipadId;
            undoActions.Push(() => manager.vertiportManager.DeleteHelipad(id));
            RefreshTopology();
            Raise($"Created {shapeLabel} helipad {id}; its boundary lies entirely within the vertiport. ");
            return true;
        }

        private void AddHolding(Vector3 point)
        {
            if (string.IsNullOrEmpty(ActiveVertiportId)) { Raise("Select a vertiport first. "); return; }
            GameObject holder = manager.vertiportManager?.CreateHoldingStructure(ActiveVertiportId, point);
            if (holder == null) { Raise("The holding structure could not be created. "); return; }
            string id = holder.name.Replace("HS_", string.Empty);
            SelectedHoldingStructureId = id;
            HoldingSelectionKind = RuntimeHoldingSelectionKind.Anchor;
            undoActions.Push(() => manager.vertiportManager.DeleteHoldingStructure(id));
            RefreshTopology();
            StateChanged?.Invoke();
            Raise($"Holding-structure anchor {id} created. Set the ring parameters, then click Generate Holding Ring. ");
        }

        private void AddApproachNode(Vector3 point)
        {
            if (string.IsNullOrEmpty(ActiveVertiportId)) { Raise("Select a vertiport first. "); return; }
            point.y = 0f;
            if (!IsInsideActiveVertiport(point)) { Raise("Approach nodes must lie within the active vertiport boundary. "); return; }
            ApproachNodeVisual visual = manager.vertiportManager?.CreateApproachNode(ActiveVertiportId, point);
            if (visual == null) { Raise("The approach node could not be created. "); return; }
            string id = visual.NodeData.NodeId;
            undoActions.Push(() => manager.vertiportManager.DeleteApproachNode(id));
            RefreshTopology();
            Raise($"Ground approach node {id} created. ");
        }

        private void HandleBoundaryPointMove()
        {
            Vertiport vertiport = ActiveVertiport();
            if (vertiport?.Boundary == null) return;
            if (Input.GetMouseButtonDown(0))
            {
                movingBoundaryPointIndex = NearestBoundaryPoint();
                if (movingBoundaryPointIndex >= 0)
                {
                    movingBoundaryBackup = new List<Vector3>(vertiport.Boundary);
                    SelectedBoundaryPointIndex = movingBoundaryPointIndex;
                    StateChanged?.Invoke();
                }
            }
            if (movingBoundaryPointIndex >= 0 && Input.GetMouseButton(0) && TryWorldPoint(out Vector3 point))
            {
                List<Vector3> updated = new List<Vector3>(vertiport.Boundary);
                point.y = 0f;
                updated[movingBoundaryPointIndex] = point;
                manager.vertiportManager.UpdateVertiportBoundary(vertiport.VertiportId, updated);
            }
            if (movingBoundaryPointIndex >= 0 && Input.GetMouseButtonUp(0))
            {
                List<Vector3> backup = movingBoundaryBackup;
                string id = vertiport.VertiportId;
                int index = movingBoundaryPointIndex;
                undoActions.Push(() => manager.vertiportManager.UpdateVertiportBoundary(id, backup));
                movingBoundaryPointIndex = -1;
                movingBoundaryBackup = null;
                RefreshTopology();
                StateChanged?.Invoke();
                Raise($"Vertiport boundary point {index} moved. ");
            }
        }

        private void HandleApproachNodeMove()
        {
            if (Input.GetMouseButtonDown(0))
            {
                movingApproachNode = NearestApproachNode();
                if (movingApproachNode != null)
                {
                    movingApproachNodeBackup = movingApproachNode.Position;
                    firstApproachNode = movingApproachNode.NodeId;
                    StateChanged?.Invoke();
                }
            }
            if (movingApproachNode != null && Input.GetMouseButton(0) && TryWorldPoint(out Vector3 point))
            {
                point.y = 0f;
                if (IsInsideActiveVertiport(point))
                {
                    ApproachNodeVisual visual = manager.vertiportManager.GetApproachNodeVisual(movingApproachNode.NodeId);
                    if (visual != null) visual.UpdatePosition(point);
                    else movingApproachNode.Position = point;
                }
            }
            if (movingApproachNode != null && Input.GetMouseButtonUp(0))
            {
                string id = movingApproachNode.NodeId;
                Vector3 old = movingApproachNodeBackup;
                undoActions.Push(() => manager.vertiportManager.GetApproachNodeVisual(id)?.UpdatePosition(old));
                movingApproachNode = null;
                firstApproachNode = null;
                RefreshTopology();
                StateChanged?.Invoke();
                Raise($"Approach node {id} moved. ");
            }
        }

        private void ConnectApproachNodes()
        {
            ApproachNode node = NearestApproachNode();
            bool createdHoldingAnchor = false;
            if (node == null)
            {
                Vertex vertex = NearestHoldingVertex(34f);
                if (vertex != null && manager.VertexToHSMap.TryGetValue(vertex.VertexId, out string hsId))
                {
                    ApproachNodeVisual anchor = manager.vertiportManager.FindOrCreateAnchorApprNode(
                        hsId, vertex.VertexId);
                    node = anchor?.NodeData;
                    createdHoldingAnchor = node != null;
                }
            }
            if (node == null)
            {
                Raise("Click an approach node or holding-ring vertex in the active vertiport. ");
                return;
            }
            if (createdHoldingAnchor)
                RefreshTopology();
            if (string.IsNullOrEmpty(firstApproachNode))
            {
                firstApproachNode = node.NodeId;
                StateChanged?.Invoke();
                Raise(node.IsAnchor
                    ? $"Holding-ring anchor {node.LinkVertexId} locked as the approach-edge start. "
                    : $"Approach-edge start {node.NodeId} locked. ");
                return;
            }
            if (firstApproachNode == node.NodeId) return;
            string vp = manager.ApproachNodeToVertiportMap.TryGetValue(firstApproachNode, out string value)
                ? value : ActiveVertiportId;
            ApproachEdgeVisual visual = manager.vertiportManager.CreateApproachEdge(vp, firstApproachNode, node.NodeId);
            firstApproachNode = null;
            StateChanged?.Invoke();
            if (visual == null) { Raise("The approach edge could not be created. "); return; }
            string id = visual.EdgeData.EdgeId;
            undoActions.Push(() => manager.vertiportManager.DeleteApproachEdge(id));
            RefreshTopology();
            Raise($"Ground approach edge {id} created. ");
        }

        private void ConnectHoldingVertices()
        {
            Vertex vertex = NearestHoldingVertex();
            if (vertex == null) { Raise("Click a solid vertex on a holding ring. "); return; }
            if (string.IsNullOrEmpty(firstHoldingVertex))
            {
                firstHoldingVertex = vertex.VertexId;
                SelectedHoldingVertexId = vertex.VertexId;
                HoldingSelectionKind = RuntimeHoldingSelectionKind.Vertex;
                StateChanged?.Invoke();
                Raise($"Ramp start {vertex.VertexId} locked. ");
                return;
            }
            if (firstHoldingVertex == vertex.VertexId) return;
            RampVisual visual = manager.vertiportManager.CreateRamp(firstHoldingVertex, vertex.VertexId,
                HoldingRampDirection);
            firstHoldingVertex = null;
            if (visual == null) { Raise("The holding ramp could not be created. "); return; }
            SelectedHoldingRampId = visual.RampId;
            HoldingSelectionKind = RuntimeHoldingSelectionKind.Ramp;
            undoActions.Push(() => manager.vertiportManager.DeleteRamp(visual.RampId));
            RefreshTopology();
            StateChanged?.Invoke();
            Raise(string.IsNullOrEmpty(visual.LinkCircleId)
                ? $"Interlayer straight ramp {visual.RampId} created. "
                : $"In-ring arc ramp {visual.RampId} created. ");
        }

        private HoldingStructure FindSelectedHoldingStructure()
        {
            HoldingStructure selected = ResolveHoldingStructure(SelectedHoldingStructureId);
            if (selected != null) return selected;
            Vertiport vertiport = ActiveVertiport();
            HoldingStructure holding = vertiport?.HoldingStructures?.FirstOrDefault();
            if (holding != null) SelectedHoldingStructureId = holding.HoldingStructureId;
            return holding;
        }

        private HoldingStructure ResolveHoldingStructure(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (manager?.HoldingStructures != null && manager.HoldingStructures.TryGetValue(id, out HoldingStructure cached))
                return cached;
            return manager?.vertiportManager?.Vertiports?
                .Where(vertiport => vertiport?.HoldingStructures != null)
                .SelectMany(vertiport => vertiport.HoldingStructures)
                .FirstOrDefault(holding => holding != null && holding.HoldingStructureId == id);
        }

        private void InspectHoldingAtCursor()
        {
            Vertex vertex = NearestHoldingVertex(24f);
            if (vertex != null)
            {
                SelectHoldingVertex(vertex.VertexId);
                return;
            }
            Ramp ramp = NearestRamp(out _, 24f);
            if (ramp != null)
            {
                SelectHoldingRamp(ramp.RampId);
                return;
            }
            Circle circle = NearestHoldingCircle(28f);
            if (circle != null)
            {
                SelectHoldingCircle(circle.CircleId);
                return;
            }
            HoldingStructure anchor = NearestHoldingAnchor(28f);
            if (anchor != null)
                SelectHoldingAnchor(anchor.HoldingStructureId);
        }

        public bool SelectHoldingAnchor(string id)
        {
            if (ResolveHoldingStructure(id) == null) return false;
            ClearHoldingSelection();
            SelectedHoldingStructureId = id;
            HoldingSelectionKind = RuntimeHoldingSelectionKind.Anchor;
            StateChanged?.Invoke();
            Raise($"Holding-structure anchor {id} selected. ");
            return true;
        }

        public bool SelectHoldingCircle(string id)
        {
            if (string.IsNullOrEmpty(id) || !manager.Circles.ContainsKey(id)) return false;
            ClearHoldingSelection();
            SelectedHoldingCircleId = id;
            SelectedHoldingStructureId = manager.vertiportManager.FindParentHSForCircle(id)?.HoldingStructureId;
            HoldingSelectionKind = RuntimeHoldingSelectionKind.Circle;
            manager.vertiportManager.SelectCircle(manager.vertiportManager.GetCircleVisual(id));
            StateChanged?.Invoke();
            Raise($"Holding ring {id} selected. ");
            return true;
        }

        public bool SelectHoldingVertex(string id)
        {
            if (string.IsNullOrEmpty(id) || !manager.Vertices.ContainsKey(id)) return false;
            ClearHoldingSelection();
            SelectedHoldingVertexId = id;
            SelectedHoldingStructureId = manager.VertexToHSMap.TryGetValue(id, out string hs) ? hs : null;
            HoldingSelectionKind = RuntimeHoldingSelectionKind.Vertex;
            manager.vertiportManager.SelectVertex(manager.vertiportManager.GetVertexVisual(id));
            StateChanged?.Invoke();
            Raise($"Holding vertex {id} selected. ");
            return true;
        }

        public bool SelectHoldingRamp(string id)
        {
            if (string.IsNullOrEmpty(id) || !manager.Ramps.TryGetValue(id, out Ramp ramp)) return false;
            ClearHoldingSelection();
            SelectedHoldingRampId = id;
            SelectedHoldingStructureId = manager.VertexToHSMap.TryGetValue(ramp.FromVertexId, out string hs) ? hs : null;
            HoldingSelectionKind = RuntimeHoldingSelectionKind.Ramp;
            manager.vertiportManager.SelectRamp(manager.vertiportManager.GetRampVisual(id));
            StateChanged?.Invoke();
            Raise($"Holding ramp {id} selected. ");
            return true;
        }

        private void HandleHoldingStructureMove()
        {
            if (Input.GetMouseButtonDown(0))
            {
                movingHoldingStructure = NearestHoldingAnchor(32f);
                if (movingHoldingStructure != null)
                {
                    movingHoldingStructureBackup = movingHoldingStructure.Position;
                    movingHoldingStructureAltitude = movingHoldingStructure.Position.y;
                    SelectHoldingAnchor(movingHoldingStructure.HoldingStructureId);
                }
            }
            if (movingHoldingStructure != null && Input.GetMouseButton(0) &&
                TryWorldPointAtAltitude(movingHoldingStructureAltitude, out Vector3 point))
            {
                // Moving an existing anchor is planar at its own altitude. Do not snap
                // imported/non-default structures to the ground or to the current ring input.
                point.y = movingHoldingStructureAltitude;
                manager.vertiportManager.MoveHoldingStructure(movingHoldingStructure.HoldingStructureId, point);
            }
            if (movingHoldingStructure != null && Input.GetMouseButtonUp(0))
            {
                string id = movingHoldingStructure.HoldingStructureId;
                Vector3 old = movingHoldingStructureBackup;
                undoActions.Push(() => manager.vertiportManager.MoveHoldingStructure(id, old));
                movingHoldingStructure = null;
                RefreshTopology();
                StateChanged?.Invoke();
                Raise($"Holding-structure anchor {id} moved together with its rings, vertices, and ramps. ");
            }
        }

        private void ClearHoldingSelection()
        {
            SelectedHoldingStructureId = null;
            SelectedHoldingCircleId = null;
            SelectedHoldingVertexId = null;
            SelectedHoldingRampId = null;
            firstHoldingVertex = null;
            HoldingSelectionKind = RuntimeHoldingSelectionKind.None;
            manager.vertiportManager.ClearSelection();
        }

        public string GetHoldingSelectionDescription()
        {
            switch (HoldingSelectionKind)
            {
                case RuntimeHoldingSelectionKind.Anchor:
                    return $"Type: Holding-structure anchor\nID: {SelectedHoldingStructureId}";
                case RuntimeHoldingSelectionKind.Circle:
                    if (manager.Circles.TryGetValue(SelectedHoldingCircleId, out Circle circle))
                        return $"Type: Holding ring\nID: {circle.CircleId}\nAltitude: {circle.Center.y:F1} m  Radius: {circle.Radius:F1} m\n" +
                               $"Initial angle: {circle.InitialAngle:F1}°  Vertices: {circle.CoveredVertices?.Count ?? 0}";
                    break;
                case RuntimeHoldingSelectionKind.Vertex:
                    return $"Type: Holding vertex\nID: {SelectedHoldingVertexId}";
                case RuntimeHoldingSelectionKind.Ramp:
                    if (manager.Ramps.TryGetValue(SelectedHoldingRampId, out Ramp ramp))
                        return $"Type: {(string.IsNullOrEmpty(ramp.LinkCircleId) ? "Interlayer straight ramp" : "In-ring arc ramp")}\nID: {ramp.RampId}";
                    break;
            }
            return "No holding-structure object selected.";
        }

        public bool TryGetHoldingConnectionPreview(out Vector3 from, out Vector3 to)
        {
            from = default;
            to = default;
            if (Tool != RuntimeEditTool.ConnectHoldingVertices || string.IsNullOrEmpty(firstHoldingVertex) ||
                !manager.Vertices.TryGetValue(firstHoldingVertex, out Vertex vertex) || !TryWorldPoint(out to))
                return false;
            from = vertex.Position;
            return true;
        }

        private void AddLayerConnector()
        {
            if (CorridorEditMode != RuntimeCorridorEditMode.Layer)
            {
                Raise("Switch to Interlayer Corridor Editing mode first. ");
                return;
            }
            Lane lane = NearestLane(out Vector3 lanePoint);
            if (lane == null) { Raise("Click a lane; the selected lane will be highlighted. "); return; }
            if (string.IsNullOrEmpty(firstLane))
            {
                firstLane = lane.LaneId;
                firstLayerConnectorPoint = lanePoint;
                StateChanged?.Invoke();
                Raise($"Connector start {lane.LaneId} ({lane.LayerId}) locked. Select a lane on another altitude layer. ");
                return;
            }
            if (firstLane == lane.LaneId) return;
            Lane source = manager.Lanes[firstLane];
            if (source.LayerId == lane.LayerId)
            {
                Raise("The start and end lanes of an interlayer connector must be on different altitude layers. ");
                return;
            }
            LaneConnectionCorridor corridor = manager.networkManager.CreateLaneConnectionCorridor(
                firstLane, lane.LaneId, new List<Vector3> { firstLayerConnectorPoint, lanePoint });
            firstLane = null;
            StateChanged?.Invoke();
            if (corridor == null) { Raise("The interlayer connector could not be created. Check direction and connectivity. "); return; }
            string id = corridor.CorridorId;
            SelectedLayerConnectorId = id;
            SelectedFacilityCorridorId = null;
            undoActions.Push(() => manager.networkManager.DeleteLaneConnectionCorridor(id));
            RefreshTopology();
            Raise($"3D interlayer connector {id} created. ");
        }

        private void AddFacilityCorridor()
        {
            if (CorridorEditMode != RuntimeCorridorEditMode.Facility)
            {
                Raise("Switch to Vertiport Corridor Editing mode first. ");
                return;
            }
            if (string.IsNullOrEmpty(corridorLane))
            {
                Lane lane = NearestLane(out Vector3 lanePoint);
                if (lane == null) { Raise("Click the connecting lane first. "); return; }
                corridorLane = lane.LaneId;
                corridorLanePoint = lanePoint;
                StateChanged?.Invoke();
                Raise($"Lane {corridorLane} and its access point are locked. Click a holding-structure ramp. ");
                return;
            }

            Ramp ramp = NearestRamp(out Vector3 rampPoint);
            if (ramp == null) { Raise("Click a holding-structure ramp. "); return; }
            corridorRamp = ramp.RampId;
            CorridorVisual corridor = manager.vertiportManager.CreateCorridor(ActiveVertiportId, CorridorType,
                corridorLane, ramp.RampId, corridorLanePoint, rampPoint);
            corridorLane = null;
            corridorRamp = null;
            StateChanged?.Invoke();
            if (corridor == null) { Raise("The vertiport connection corridor could not be created. "); return; }
            string id = corridor.CorridorData.CorridorId;
            SelectedFacilityCorridorId = id;
            undoActions.Push(() => manager.vertiportManager.DeleteCorridor(id));
            RefreshTopology();
            Raise($"Tangential Bézier vertiport corridor {id} created. ");
        }

        private void InspectFacilityCorridorAtCursor()
        {
            if (CorridorEditMode == RuntimeCorridorEditMode.Layer)
            {
                LaneConnectionCorridor layerConnector = NearestLayerConnector(26f, out _);
                if (layerConnector == null)
                {
                    ResetCorridorSelection();
                    StateChanged?.Invoke();
                    Raise("No interlayer connection corridor was selected. ");
                    return;
                }
                SelectedFacilityCorridorId = null;
                SelectedLayerConnectorId = layerConnector.CorridorId;
                manager.vertiportManager.ClearSelection();
                StateChanged?.Invoke();
                Raise($"Interlayer Bézier connector {layerConnector.CorridorId} selected. ");
                return;
            }

            NetConnectedCorridor corridor = NearestFacilityCorridor(26f, out _);
            if (corridor == null)
            {
                ResetCorridorSelection();
                StateChanged?.Invoke();
                Raise("No vertiport connection corridor in the active vertiport was selected. ");
                return;
            }
            SelectedLayerConnectorId = null;
            SelectedFacilityCorridorId = corridor.CorridorId;
            manager.vertiportManager.SelectCorridor(manager.vertiportManager.GetCorridorVisual(corridor.CorridorId));
            StateChanged?.Invoke();
            Raise($"Bézier corridor {corridor.CorridorId} selected. ");
        }

        public bool DeleteSelectedFacilityCorridor()
        {
            if (!string.IsNullOrEmpty(SelectedLayerConnectorId))
            {
                string layerConnectorId = SelectedLayerConnectorId;
                manager.networkManager.DeleteLaneConnectionCorridor(layerConnectorId);
                SelectedLayerConnectorId = null;
                RefreshTopology();
                StateChanged?.Invoke();
                Raise($"Interlayer connector {layerConnectorId} deleted. ");
                return true;
            }
            if (string.IsNullOrEmpty(SelectedFacilityCorridorId))
            {
                Raise(CorridorEditMode == RuntimeCorridorEditMode.Layer
                    ? "Select an interlayer connection corridor in View mode first. "
                    : "Select a vertiport connection corridor in View mode first. ");
                return false;
            }
            string id = SelectedFacilityCorridorId;
            manager.vertiportManager.DeleteCorridor(id);
            SelectedFacilityCorridorId = null;
            RefreshTopology();
            StateChanged?.Invoke();
            Raise($"Vertiport connection corridor {id} deleted. ");
            return true;
        }

        public void ClearCorridorSelection()
        {
            ResetCorridorSelection();
            StateChanged?.Invoke();
            Raise("Connection-corridor selection cleared. ");
        }

        private void ResetCorridorSelection()
        {
            SelectedFacilityCorridorId = null;
            SelectedLayerConnectorId = null;
            firstLane = null;
            corridorLane = null;
            corridorRamp = null;
            manager?.vertiportManager?.ClearSelection();
        }

        private void HandleFacilityCorridorLinkMove()
        {
            if (Input.GetMouseButtonDown(0))
            {
                if (CorridorEditMode == RuntimeCorridorEditMode.Layer)
                {
                    LaneConnectionCorridor layerConnector = !string.IsNullOrEmpty(SelectedLayerConnectorId) &&
                                                             manager.LaneConnectionCorridors.TryGetValue(
                                                                 SelectedLayerConnectorId, out LaneConnectionCorridor selectedLayer)
                        ? selectedLayer : NearestLayerConnector(30f, out _);
                    if (layerConnector != null)
                    {
                        Camera camera = navigator.ControlledCamera;
                        Vector2 mouse = Input.mousePosition;
                        Vector3 fromScreen = camera.WorldToScreenPoint(layerConnector.FromPoint);
                        Vector3 toScreen = camera.WorldToScreenPoint(layerConnector.ToPoint);
                        float fromDistance = Vector2.Distance(mouse, new Vector2(fromScreen.x, fromScreen.y));
                        float toDistance = Vector2.Distance(mouse, new Vector2(toScreen.x, toScreen.y));
                        if (Mathf.Min(fromDistance, toDistance) <= 32f)
                        {
                            movingLayerConnector = layerConnector;
                            movingLayerConnectorFromPoint = fromDistance <= toDistance;
                            movingLayerConnectorOldFromPoint = layerConnector.FromPoint;
                            movingLayerConnectorOldToPoint = layerConnector.ToPoint;
                            SelectedLayerConnectorId = layerConnector.CorridorId;
                            SelectedFacilityCorridorId = null;
                            StateChanged?.Invoke();
                        }
                    }
                    return;
                }

                NetConnectedCorridor corridor = !string.IsNullOrEmpty(SelectedFacilityCorridorId) &&
                                                  manager.Corridors.TryGetValue(SelectedFacilityCorridorId, out NetConnectedCorridor selected)
                    ? selected : NearestFacilityCorridor(30f);
                if (corridor != null)
                {
                    Camera camera = navigator.ControlledCamera;
                    Vector2 mouse = Input.mousePosition;
                    Vector3 laneScreen = camera.WorldToScreenPoint(corridor.LaneLinkPoint);
                    Vector3 rampScreen = camera.WorldToScreenPoint(corridor.RampLinkPoint);
                    float laneDistance = Vector2.Distance(mouse, new Vector2(laneScreen.x, laneScreen.y));
                    float rampDistance = Vector2.Distance(mouse, new Vector2(rampScreen.x, rampScreen.y));
                    if (Mathf.Min(laneDistance, rampDistance) <= 32f)
                    {
                        movingCorridor = corridor;
                        movingCorridorLanePoint = laneDistance <= rampDistance;
                        movingCorridorOldLanePoint = corridor.LaneLinkPoint;
                        movingCorridorOldRampPoint = corridor.RampLinkPoint;
                        SelectedFacilityCorridorId = corridor.CorridorId;
                        StateChanged?.Invoke();
                    }
                }
            }
            if (movingLayerConnector != null && Input.GetMouseButton(0))
            {
                string laneId = movingLayerConnectorFromPoint
                    ? movingLayerConnector.FromLaneId : movingLayerConnector.ToLaneId;
                if (manager.Lanes.TryGetValue(laneId, out Lane lane) &&
                    ClosestPointOnScreenPolyline(lane.Centerline, out Vector3 point, out _))
                    manager.networkManager.UpdateLaneConnectionCorridorLink(
                        movingLayerConnector.CorridorId, movingLayerConnectorFromPoint, point);
                return;
            }
            if (movingLayerConnector != null && Input.GetMouseButtonUp(0))
            {
                string id = movingLayerConnector.CorridorId;
                Vector3 oldFrom = movingLayerConnectorOldFromPoint;
                Vector3 oldTo = movingLayerConnectorOldToPoint;
                undoActions.Push(() =>
                {
                    manager.networkManager.UpdateLaneConnectionCorridorLink(id, true, oldFrom);
                    manager.networkManager.UpdateLaneConnectionCorridorLink(id, false, oldTo);
                });
                movingLayerConnector = null;
                RefreshTopology();
                StateChanged?.Invoke();
                Raise($"Interlayer-connector tangent point {id} moved along its lane. ");
                return;
            }
            if (movingCorridor != null && Input.GetMouseButton(0))
            {
                if (movingCorridorLanePoint && manager.Lanes.TryGetValue(movingCorridor.LinkLaneId, out Lane lane) &&
                    ClosestPointOnScreenPolyline(lane.Centerline, out Vector3 point, out _))
                    movingCorridor.LaneLinkPoint = point;
                else if (!movingCorridorLanePoint && manager.Ramps.TryGetValue(movingCorridor.LinkRampId, out Ramp ramp))
                {
                    List<Vector3> rampLine = RuntimeAirspaceGeometry.BuildRampPolyline(ramp, manager.Vertices, manager.Circles, 48);
                    if (ClosestPointOnScreenPolyline(rampLine, out Vector3 rampPoint, out _))
                        movingCorridor.RampLinkPoint = rampPoint;
                }
                SyncCorridorLinks(movingCorridor);
            }
            if (movingCorridor != null && Input.GetMouseButtonUp(0))
            {
                NetConnectedCorridor corridor = movingCorridor;
                Vector3 oldLane = movingCorridorOldLanePoint;
                Vector3 oldRamp = movingCorridorOldRampPoint;
                undoActions.Push(() =>
                {
                    corridor.LaneLinkPoint = oldLane;
                    corridor.RampLinkPoint = oldRamp;
                    SyncCorridorLinks(corridor);
                });
                movingCorridor = null;
                RefreshTopology();
                StateChanged?.Invoke();
                Raise($"Corridor access point moved along its {(movingCorridorLanePoint ? "lane" : "ramp")}. ");
            }
        }

        private NetConnectedCorridor NearestFacilityCorridor(float maxPixels) =>
            NearestFacilityCorridor(maxPixels, out _);

        private NetConnectedCorridor NearestFacilityCorridor(float maxPixels, out float distance)
        {
            NetConnectedCorridor bestCorridor = null;
            float best = maxPixels;
            foreach (NetConnectedCorridor corridor in manager.Corridors.Values)
            {
                if (!manager.CorridorToVertiportMap.TryGetValue(corridor.CorridorId, out string vertiportId) ||
                    vertiportId != ActiveVertiportId) continue;
                List<Vector3> curve = BuildFacilityCorridorCurve(corridor);
                if (!ClosestPointOnScreenPolyline(curve, out _, out float candidateDistance) ||
                    candidateDistance >= best) continue;
                best = candidateDistance;
                bestCorridor = corridor;
            }
            distance = bestCorridor != null ? best : float.MaxValue;
            return bestCorridor;
        }

        private LaneConnectionCorridor NearestLayerConnector(float maxPixels, out float distance)
        {
            LaneConnectionCorridor bestConnector = null;
            float best = maxPixels;
            foreach (LaneConnectionCorridor connector in manager.LaneConnectionCorridors.Values)
            {
                if (!ClosestPointOnScreenPolyline(connector.Centerline, out _, out float candidateDistance) ||
                    candidateDistance >= best) continue;
                best = candidateDistance;
                bestConnector = connector;
            }
            distance = bestConnector != null ? best : float.MaxValue;
            return bestConnector;
        }

        private List<Vector3> BuildFacilityCorridorCurve(NetConnectedCorridor corridor)
        {
            manager.Lanes.TryGetValue(corridor.LinkLaneId, out Lane lane);
            manager.Ramps.TryGetValue(corridor.LinkRampId, out Ramp ramp);
            float ratio = manager.vertiportManager != null ? manager.vertiportManager.corridorTangentRatio : 0.3f;
            return RuntimeAirspaceGeometry.BuildCorridorPolyline(corridor, lane, ramp,
                manager.Vertices, manager.Circles, ratio, 32);
        }

        private void SyncCorridorLinks(NetConnectedCorridor corridor)
        {
            if (corridor == null) return;
            if (manager.Lanes.TryGetValue(corridor.LinkLaneId, out Lane lane))
            {
                int index = lane.LinkCorridors.FindIndex(link => link.Item1 == corridor.CorridorId);
                if (index >= 0) lane.LinkCorridors[index] = (corridor.CorridorId, corridor.LaneLinkPoint);
            }
            if (manager.Ramps.TryGetValue(corridor.LinkRampId, out Ramp ramp))
            {
                int index = ramp.LinkCorridors.FindIndex(link => link.Item1 == corridor.CorridorId);
                if (index >= 0) ramp.LinkCorridors[index] = (corridor.CorridorId, corridor.RampLinkPoint);
            }
            manager.vertiportManager.GetCorridorVisual(corridor.CorridorId)?.UpdateVisual();
        }

        public string GetCorridorSelectionDescription()
        {
            if (!string.IsNullOrEmpty(SelectedLayerConnectorId) &&
                manager.LaneConnectionCorridors.TryGetValue(SelectedLayerConnectorId,
                    out LaneConnectionCorridor layerConnector))
                return $"Type: Lane-to-lane interlayer Bézier connector\nID: {layerConnector.CorridorId}\n" +
                       $"Start lane: {layerConnector.FromLaneId} ({layerConnector.FromLayerId})\n" +
                       $"End lane: {layerConnector.ToLaneId} ({layerConnector.ToLayerId})\n" +
                       $"Start tangent point: {layerConnector.FromPoint:F1}\nEnd tangent point: {layerConnector.ToPoint:F1}";
            if (string.IsNullOrEmpty(SelectedFacilityCorridorId) ||
                !manager.Corridors.TryGetValue(SelectedFacilityCorridorId, out NetConnectedCorridor corridor))
                return CorridorEditMode == RuntimeCorridorEditMode.Layer
                    ? "No interlayer connection corridor selected."
                    : "No vertiport connection corridor selected.";
            return $"Type: {(corridor.CorridorType == "Entry" ? "Arrival" : "Departure")} Bézier corridor\n" +
                   $"ID: {corridor.CorridorId}\nLane: {corridor.LinkLaneId}\nRamp: {corridor.LinkRampId}\n" +
                   $"Lane access point: {corridor.LaneLinkPoint:F1}\nRamp access point: {corridor.RampLinkPoint:F1}";
        }

        public bool TryGetFacilityCorridorPreview(out List<Vector3> curve)
        {
            curve = null;
            if (CorridorEditMode != RuntimeCorridorEditMode.Facility ||
                Tool != RuntimeEditTool.AddFacilityCorridor || string.IsNullOrEmpty(corridorLane) ||
                !manager.Lanes.TryGetValue(corridorLane, out Lane lane)) return false;
            Ramp ramp = NearestRamp(out Vector3 rampPoint, 48f);
            if (ramp == null) return false;
            var preview = new NetConnectedCorridor
            {
                CorridorType = CorridorType,
                LinkLaneId = lane.LaneId,
                LinkRampId = ramp.RampId,
                LaneLinkPoint = corridorLanePoint,
                RampLinkPoint = rampPoint
            };
            float ratio = manager.vertiportManager != null ? manager.vertiportManager.corridorTangentRatio : 0.3f;
            curve = RuntimeAirspaceGeometry.BuildCorridorPolyline(preview, lane, ramp,
                manager.Vertices, manager.Circles, ratio, 32);
            return curve.Count > 1;
        }

        public void ClearNetworkSelection(bool notify = true)
        {
            firstNetworkNode = null;
            selectedNetworkNode = null;
            selectedNetworkEdge = null;
            selectedNetworkWaypointIndex = -1;
            AxisSelectionKind = RuntimeAxisSelectionKind.None;
            manager?.networkManager?.ClearSelection();
            StateChanged?.Invoke();
            if (notify) Raise("Network selection cleared. ");
        }

        public bool DeleteSelectedNetworkElement()
        {
            if (manager == null || manager.IsRunning())
            {
                Raise("Stop the simulation before deleting network objects. ");
                return false;
            }
            if (!RequireEditableActiveLayer("delete a network object")) return false;
            AxisEditSnapshot snapshot = CaptureAxisSnapshot();
            string deleted;
            switch (AxisSelectionKind)
            {
                case RuntimeAxisSelectionKind.Node:
                    if (string.IsNullOrEmpty(selectedNetworkNode)) return false;
                    deleted = $"node {selectedNetworkNode} and its connected edges and waypoints";
                    manager.networkManager.DeleteNode(selectedNetworkNode);
                    break;
                case RuntimeAxisSelectionKind.Edge:
                    if (string.IsNullOrEmpty(selectedNetworkEdge)) return false;
                    deleted = $"edge {selectedNetworkEdge} and its waypoints";
                    manager.networkManager.DeleteEdge(selectedNetworkEdge);
                    break;
                case RuntimeAxisSelectionKind.Waypoint:
                    EdgeVisual edgeVisual = manager.networkManager.GetEdgeVisual(selectedNetworkEdge);
                    if (edgeVisual == null || selectedNetworkWaypointIndex <= 0 ||
                        selectedNetworkWaypointIndex >= edgeVisual.EdgeData.Centerline.Count - 1) return false;
                    deleted = $"waypoint {selectedNetworkWaypointIndex} of {selectedNetworkEdge}";
                    manager.networkManager.DeleteWaypoint(edgeVisual, selectedNetworkWaypointIndex);
                    break;
                default:
                    Raise("Select a node, edge, or waypoint with View / Select first. ");
                    return false;
            }
            PushAxisUndo(snapshot);
            ClearNetworkSelection(false);
            RefreshNetwork();
            StateChanged?.Invoke();
            Raise($"Deleted {deleted}; derived lanes and connectors were recalculated. ");
            return true;
        }

        public void RebuildDerivedNetwork()
        {
            if (manager == null || manager.IsRunning())
            {
                Raise("Stop the simulation first. ");
                return;
            }
            RefreshNetwork();
            StateChanged?.Invoke();
            Raise("Lane counts, lane geometry, and intersection lane connectors were recalculated from the current centerlines. ");
        }

        public string GetSelectionDescription()
        {
            if (AxisNetwork == null) return "No centerline network is loaded.";
            if (AxisSelectionKind == RuntimeAxisSelectionKind.Node)
            {
                Node node = AxisNetwork.Nodes.FirstOrDefault(item => item.NodeId == selectedNetworkNode);
                if (node == null) return "No object selected.";
                return $"Type: Centerline node\nID: {node.NodeId}\nLayer: {node.LayerId}\n" +
                       $"Coordinates: X {node.Position.x:F2} / Y {node.Position.y:F2} / Z {node.Position.z:F2}\n" +
                       $"Connected edges: {node.ConnectedEdges?.Count ?? 0}  " +
                       $"Adjacent nodes: {node.NeighborNodes?.Count ?? 0}";
            }
            Edge axisEdge = AxisNetwork.Edges.FirstOrDefault(item => item.EdgeId == selectedNetworkEdge);
            if (axisEdge == null) return "No object selected.";
            Edge fullEdge = manager?.networkManager?.FullNetwork?.Edges?
                .FirstOrDefault(item => item.EdgeId == axisEdge.EdgeId);
            int lanes = fullEdge?.Lanes?.Count ?? 0;
            int outbound = fullEdge?.Lanes?.Sum(lane => lane.ToLanesId?.Count ?? 0) ?? 0;
            int inbound = fullEdge?.Lanes?.Sum(lane => lane.FromLanesId?.Count ?? 0) ?? 0;
            if (AxisSelectionKind == RuntimeAxisSelectionKind.Waypoint &&
                selectedNetworkWaypointIndex > 0 && selectedNetworkWaypointIndex < axisEdge.Centerline.Count - 1)
            {
                Vector3 point = axisEdge.Centerline[selectedNetworkWaypointIndex];
                return $"Type: Edge waypoint\nParent edge: {axisEdge.EdgeId}\nIndex: {selectedNetworkWaypointIndex}\n" +
                       $"Coordinates: X {point.x:F2} / Y {point.y:F2} / Z {point.z:F2}\n" +
                       $"Derived lanes: {lanes}  Inbound connectors: {inbound}  Outbound connectors: {outbound}";
            }
            return $"Type: Centerline edge\nID: {axisEdge.EdgeId}\nLayer: {axisEdge.LayerId}\n" +
                   $"Endpoints: {string.Join(" → ", axisEdge.LinkNodesId ?? new List<string>())}\n" +
                   $"Length: {axisEdge.Length:F2} m  Waypoints: {Mathf.Max(0, axisEdge.Centerline.Count - 2)}\n" +
                   $"Derived lanes: {lanes}  Inbound connectors: {inbound}  Outbound connectors: {outbound}";
        }

        private void RefreshNetwork()
        {
            manager.networkManager.GenerateFullNetworkFromAxis();
            PruneInvalidFacilityCorridors();
            RefreshTopology();
        }

        private void RebuildLayeredNetwork()
        {
            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            manager.networkManager.RebuildVisualsFromData();
            TakeOverLegacyAxisVisualization();
            RefreshNetwork();
        }

        private void PruneInvalidFacilityCorridors()
        {
            var validLaneIds = new HashSet<string>(manager.networkManager.FullNetwork.Edges
                .Where(edge => edge?.Lanes != null).SelectMany(edge => edge.Lanes)
                .Where(lane => lane != null).Select(lane => lane.LaneId));
            List<string> invalidCorridorIds = manager.vertiportManager.Vertiports
                .Where(vertiport => vertiport?.Corridors != null)
                .SelectMany(vertiport => vertiport.Corridors)
                .Where(corridor => corridor != null && !validLaneIds.Contains(corridor.LinkLaneId))
                .Select(corridor => corridor.CorridorId).Distinct().ToList();
            foreach (string corridorId in invalidCorridorIds)
                manager.vertiportManager.DeleteCorridor(corridorId);
            if (invalidCorridorIds.Count > 0)
                Debug.LogWarning($"[RuntimeAirspaceEditor] Removed {invalidCorridorIds.Count} facility corridor(s) " +
                                 "whose lanes no longer exist after axis regeneration.");
        }

        private void RefreshTopology() => manager.RefreshRuntimeTopology();

        private bool TryWorldPoint(out Vector3 point)
        {
            float altitude = Workspace == AirspaceEditWorkspace.GroundFacility
                ? 0f
                : Workspace == AirspaceEditWorkspace.NetworkLayers
                    ? ActiveLayer?.NominalAltitude ?? EditingAltitude
                    : EditingAltitude;
            return TryWorldPointAtAltitude(altitude, out point);
        }

        private bool TryWorldPointAtAltitude(float altitude, out Vector3 point)
        {
            Ray ray = navigator.ControlledCamera.ScreenPointToRay(Input.mousePosition);
            Plane plane = new Plane(Vector3.up, new Vector3(0f, altitude, 0f));
            if (plane.Raycast(ray, out float enter))
            {
                point = ray.GetPoint(enter);
                point.y = altitude;
                return true;
            }
            point = default;
            return false;
        }

        private void UpdateHover()
        {
            ClearHover();
            if (Workspace == AirspaceEditWorkspace.NetworkLayers)
            {
                if (Tool == RuntimeEditTool.Inspect)
                {
                    Node node = NearestNode(20f);
                    if (node != null) HoveredNetworkNodeId = node.NodeId;
                    else if (NearestWaypoint(out Edge waypointEdge, out int waypointIndex, 20f))
                    {
                        HoveredWaypointEdgeId = waypointEdge.EdgeId;
                        HoveredWaypointIndex = waypointIndex;
                        HoveredNetworkEdgeId = waypointEdge.EdgeId;
                    }
                    else if (NearestEdgeSegment(out Edge edge, out _)) HoveredNetworkEdgeId = edge.EdgeId;
                }
                else if (Tool == RuntimeEditTool.ConnectNetworkNodes || Tool == RuntimeEditTool.MoveNetworkNode)
                    HoveredNetworkNodeId = NearestNode()?.NodeId;
                else if (Tool == RuntimeEditTool.AddNetworkWaypoint &&
                         NearestEdgeSegment(out Edge edge, out _))
                    HoveredNetworkEdgeId = edge.EdgeId;
                else if (Tool == RuntimeEditTool.MoveNetworkWaypoint &&
                         NearestWaypoint(out Edge waypointEdge, out int waypointIndex))
                {
                    HoveredWaypointEdgeId = waypointEdge.EdgeId;
                    HoveredWaypointIndex = waypointIndex;
                    HoveredNetworkEdgeId = waypointEdge.EdgeId;
                }
            }
            else if (Workspace == AirspaceEditWorkspace.ConnectionCorridor)
            {
                if (Tool == RuntimeEditTool.AddFacilityCorridor && !string.IsNullOrEmpty(corridorLane))
                    HoveredRampId = NearestRamp()?.RampId;
                else
                    HoveredLaneId = NearestLane()?.LaneId;
            }
        }

        private void ClearHover()
        {
            HoveredNetworkNodeId = null;
            HoveredNetworkEdgeId = null;
            HoveredWaypointEdgeId = null;
            HoveredWaypointIndex = -1;
            HoveredLaneId = null;
            HoveredRampId = null;
        }

        private void InspectNetworkAtCursor()
        {
            manager.networkManager.ClearSelection();
            Node node = NearestNode(20f);
            if (node != null)
            {
                selectedNetworkNode = node.NodeId;
                selectedNetworkEdge = null;
                selectedNetworkWaypointIndex = -1;
                AxisSelectionKind = RuntimeAxisSelectionKind.Node;
                manager.networkManager.SelectNode(manager.networkManager.GetNodeVisual(node.NodeId));
                StateChanged?.Invoke();
                Raise($"Centerline node {node.NodeId} selected. ");
                return;
            }
            if (NearestWaypoint(out Edge waypointEdge, out int waypointIndex, 20f))
            {
                selectedNetworkNode = null;
                selectedNetworkEdge = waypointEdge.EdgeId;
                selectedNetworkWaypointIndex = waypointIndex;
                AxisSelectionKind = RuntimeAxisSelectionKind.Waypoint;
                WaypointVisual visual = manager.networkManager.GetEdgeVisual(waypointEdge.EdgeId)?.GetWaypoints()
                    .FirstOrDefault(item => item != null && item.WaypointIndex == waypointIndex);
                manager.networkManager.SelectWaypoint(visual);
                StateChanged?.Invoke();
                Raise($"Waypoint {waypointIndex} of {waypointEdge.EdgeId} selected. ");
                return;
            }
            if (NearestEdgeSegment(out Edge edge, out _))
            {
                selectedNetworkNode = null;
                selectedNetworkEdge = edge.EdgeId;
                selectedNetworkWaypointIndex = -1;
                AxisSelectionKind = RuntimeAxisSelectionKind.Edge;
                manager.networkManager.SelectEdge(manager.networkManager.GetEdgeVisual(edge.EdgeId));
                StateChanged?.Invoke();
                Raise($"Centerline edge {edge.EdgeId} selected. ");
                return;
            }
            ClearNetworkSelection(false);
            Raise("No object selected. Click near a node, waypoint, or centerline edge. ");
        }

        private Node NearestNode(float maxPixels = 30f)
        {
            if (!ActiveLayerCanBeSeen()) return null;
            return NearestScreen(AxisNetwork.Nodes
                .Where(node => node != null && node.LayerId == ActiveLayerId), node => node.Position, maxPixels);
        }

        private bool NearestWaypoint(out Edge bestEdge, out int bestIndex, float maxPixels = 24f)
        {
            bestEdge = null;
            bestIndex = -1;
            if (!ActiveLayerCanBeSeen()) return false;
            Camera camera = navigator.ControlledCamera;
            Vector2 mouse = Input.mousePosition;
            float bestDistance = maxPixels;
            foreach (Edge edge in AxisNetwork.Edges.Where(edge => edge != null && edge.LayerId == ActiveLayerId &&
                                                                   edge.Centerline != null))
            {
                for (int i = 1; i < edge.Centerline.Count - 1; i++)
                {
                    Vector3 screen = camera.WorldToScreenPoint(edge.Centerline[i]);
                    if (screen.z < 0f) continue;
                    float distance = Vector2.Distance(mouse, new Vector2(screen.x, screen.y));
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    bestEdge = edge;
                    bestIndex = i;
                }
            }
            return bestEdge != null;
        }

        private ApproachNode NearestApproachNode() => NearestScreen(manager.ApproachNodes.Values.Where(node =>
                manager.ApproachNodeToVertiportMap.TryGetValue(node.NodeId, out string vp) && vp == ActiveVertiportId),
            node => node.Position, 30f);

        private Vertex NearestHoldingVertex(float maxPixels = 30f) => NearestScreen(manager.Vertices.Values.Where(vertex =>
                manager.VertexToHSMap.TryGetValue(vertex.VertexId, out string hs) &&
                manager.HSToVertiportMap.TryGetValue(hs, out string vp) && vp == ActiveVertiportId),
            vertex => vertex.Position, maxPixels);

        private Circle NearestHoldingCircle(float maxPixels = 30f) => NearestScreen(manager.Circles.Values.Where(circle =>
            {
                HoldingStructure holding = manager.vertiportManager.FindParentHSForCircle(circle.CircleId);
                return holding != null && manager.HSToVertiportMap.TryGetValue(holding.HoldingStructureId, out string vp) &&
                       vp == ActiveVertiportId;
            }), circle => circle.Center, maxPixels);

        private HoldingStructure NearestHoldingAnchor(float maxPixels = 30f) =>
            NearestScreen(EnumerateActiveHoldingStructures(),
                holding => holding.Position, maxPixels);

        private Lane NearestLane() => NearestLane(out _);

        private Lane NearestLane(out Vector3 closestPoint, float maxPixels = 38f)
        {
            Lane bestLane = null;
            closestPoint = default;
            float best = maxPixels;
            foreach (Lane lane in manager.Lanes.Values.Where(lane => lane.Centerline?.Count > 1 &&
                         (visualizer == null || visualizer.IsNetworkLayerRenderable(lane.LayerId))))
            {
                if (!ClosestPointOnScreenPolyline(lane.Centerline, out Vector3 point, out float distance) || distance >= best)
                    continue;
                best = distance;
                bestLane = lane;
                closestPoint = point;
            }
            return bestLane;
        }

        private Ramp NearestRamp() => NearestRamp(out _, 38f);

        private Ramp NearestRamp(out Vector3 closestPoint, float maxPixels = 38f)
        {
            Ramp bestRamp = null;
            closestPoint = default;
            float best = maxPixels;
            foreach (Ramp ramp in manager.Ramps.Values)
            {
                List<Vector3> points = RuntimeAirspaceGeometry.BuildRampPolyline(ramp, manager.Vertices, manager.Circles, 40);
                if (!ClosestPointOnScreenPolyline(points, out Vector3 point, out float distance) || distance >= best) continue;
                best = distance;
                bestRamp = ramp;
                closestPoint = point;
            }
            return bestRamp;
        }

        private bool ClosestPointOnScreenPolyline(IList<Vector3> points, out Vector3 closestPoint, out float distance)
        {
            closestPoint = default;
            distance = float.MaxValue;
            if (points == null || points.Count < 2) return false;
            Camera camera = navigator.ControlledCamera;
            Vector2 mouse = Input.mousePosition;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 a3 = camera.WorldToScreenPoint(points[i - 1]);
                Vector3 b3 = camera.WorldToScreenPoint(points[i]);
                if (a3.z < 0f || b3.z < 0f) continue;
                float current = DistanceToSegment(mouse, new Vector2(a3.x, a3.y), new Vector2(b3.x, b3.y), out float t);
                if (current >= distance) continue;
                distance = current;
                closestPoint = Vector3.Lerp(points[i - 1], points[i], t);
            }
            return distance < float.MaxValue;
        }

        private bool NearestEdgeSegment(out Edge bestEdge, out int insertIndex)
        {
            return NearestEdgeSegment(out bestEdge, out insertIndex, out _);
        }

        private bool NearestEdgeSegment(out Edge bestEdge, out int insertIndex, out Vector3 projectedPoint)
        {
            bestEdge = null;
            insertIndex = -1;
            projectedPoint = default;
            if (!ActiveLayerCanBeSeen()) return false;
            Camera camera = navigator.ControlledCamera;
            Vector2 mouse = Input.mousePosition;
            float best = 26f;
            foreach (Edge edge in AxisNetwork.Edges.Where(edge => edge != null && edge.LayerId == ActiveLayerId &&
                                                                   edge.Centerline != null && edge.Centerline.Count > 1))
            {
                for (int i = 1; i < edge.Centerline.Count; i++)
                {
                    Vector3 a3 = camera.WorldToScreenPoint(edge.Centerline[i - 1]);
                    Vector3 b3 = camera.WorldToScreenPoint(edge.Centerline[i]);
                    if (a3.z < 0f || b3.z < 0f) continue;
                    Vector2 a = new Vector2(a3.x, a3.y);
                    Vector2 b = new Vector2(b3.x, b3.y);
                    float distance = DistanceToSegment(mouse, a, b, out float t);
                    if (distance >= best) continue;
                    best = distance;
                    bestEdge = edge;
                    insertIndex = i;
                    projectedPoint = Vector3.Lerp(edge.Centerline[i - 1], edge.Centerline[i], t);
                }
            }
            return bestEdge != null;
        }

        private T NearestScreen<T>(IEnumerable<T> source, Func<T, Vector3> position, float maxPixels) where T : class
        {
            Camera camera = navigator.ControlledCamera;
            Vector2 mouse = Input.mousePosition;
            T best = null;
            float bestDistance = maxPixels;
            foreach (T item in source)
            {
                Vector3 screen = camera.WorldToScreenPoint(position(item));
                if (screen.z < 0f) continue;
                float distancePixels = Vector2.Distance(mouse, new Vector2(screen.x, screen.y));
                if (distancePixels < bestDistance) { bestDistance = distancePixels; best = item; }
            }
            return best;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
        {
            return DistanceToSegment(point, start, end, out _);
        }

        private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end, out float t)
        {
            Vector2 segment = end - start;
            float denominator = segment.sqrMagnitude;
            if (denominator < 0.001f)
            {
                t = 0f;
                return Vector2.Distance(point, start);
            }
            t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / denominator);
            return Vector2.Distance(point, start + segment * t);
        }

        public bool TryGetNetworkConnectionPreview(out Vector3 from, out Vector3 to)
        {
            from = default;
            to = default;
            if (!HasPendingNetworkConnection || AxisNetwork == null) return false;
            Node node = AxisNetwork.Nodes.FirstOrDefault(item => item != null && item.NodeId == firstNetworkNode);
            if (node == null || !TryWorldPoint(out to)) return false;
            from = node.Position;
            to.y = node.Position.y;
            return true;
        }

        public bool TryGetApproachConnectionPreview(out Vector3 from, out Vector3 to)
        {
            from = default;
            to = default;
            if (!HasPendingApproachConnection ||
                !manager.ApproachNodes.TryGetValue(firstApproachNode, out ApproachNode node)) return false;
            from = node.Position;
            ApproachNode targetNode = NearestApproachNode();
            if (targetNode != null)
                to = targetNode.Position;
            else
            {
                Vertex targetVertex = NearestHoldingVertex(40f);
                if (targetVertex != null)
                    to = targetVertex.Position;
                else
                {
                    if (!TryWorldPoint(out to)) return false;
                    to.y = 0f;
                }
            }
            return true;
        }

        public bool TryGetHelipadPreview(out List<Vector3> boundary)
        {
            boundary = null;
            if (Workspace != AirspaceEditWorkspace.GroundFacility || Tool != RuntimeEditTool.AddHelipad ||
                !TryWorldPoint(out Vector3 point)) return false;
            point.y = 0f;
            if (HelipadShape == RuntimeHelipadShape.Polygon)
            {
                boundary = new List<Vector3>(pendingHelipadPolygon);
                if (IsInsideActiveVertiport(point)) boundary.Add(point);
                return boundary.Count > 0;
            }
            boundary = HelipadShape == RuntimeHelipadShape.Circle
                ? BuildCircle(point, HelipadRadius, 24)
                : BuildRectangle(point, HelipadWidth, HelipadDepth, HelipadRotationDegrees);
            return true;
        }

        public bool TryGetVertiportBoundaryPreview(out List<Vector3> boundary)
        {
            boundary = new List<Vector3>();
            if (Workspace != AirspaceEditWorkspace.GroundFacility ||
                Tool != RuntimeEditTool.DrawVertiportBoundary) return false;
            boundary.AddRange(pendingVertiportBoundary);
            if (TryWorldPoint(out Vector3 point))
            {
                point.y = 0f;
                boundary.Add(point);
            }
            return boundary.Count > 0;
        }

        private int NearestBoundaryPoint(float maxPixels = 24f)
        {
            Vertiport vertiport = ActiveVertiport();
            if (vertiport?.Boundary == null) return -1;
            Camera camera = navigator.ControlledCamera;
            Vector2 mouse = Input.mousePosition;
            float best = maxPixels;
            int bestIndex = -1;
            for (int i = 0; i < vertiport.Boundary.Count; i++)
            {
                Vector3 screen = camera.WorldToScreenPoint(vertiport.Boundary[i]);
                if (screen.z < 0f) continue;
                float distance = Vector2.Distance(mouse, new Vector2(screen.x, screen.y));
                if (distance >= best) continue;
                best = distance;
                bestIndex = i;
            }
            return bestIndex;
        }

        private bool NearestBoundarySegment(out int segmentIndex, out Vector3 projectedPoint, float maxPixels = 28f)
        {
            segmentIndex = -1;
            projectedPoint = default;
            Vertiport vertiport = ActiveVertiport();
            if (vertiport?.Boundary == null || vertiport.Boundary.Count < 2) return false;
            Camera camera = navigator.ControlledCamera;
            Vector2 mouse = Input.mousePosition;
            float best = maxPixels;
            for (int i = 0; i < vertiport.Boundary.Count; i++)
            {
                int next = (i + 1) % vertiport.Boundary.Count;
                Vector3 a3 = camera.WorldToScreenPoint(vertiport.Boundary[i]);
                Vector3 b3 = camera.WorldToScreenPoint(vertiport.Boundary[next]);
                if (a3.z < 0f || b3.z < 0f) continue;
                float distance = DistanceToSegment(mouse, new Vector2(a3.x, a3.y),
                    new Vector2(b3.x, b3.y), out float t);
                if (distance >= best) continue;
                best = distance;
                segmentIndex = i;
                projectedPoint = Vector3.Lerp(vertiport.Boundary[i], vertiport.Boundary[next], t);
                projectedPoint.y = 0f;
            }
            return segmentIndex >= 0;
        }

        private bool IsInsideActiveVertiport(Vector3 point)
        {
            Vertiport vertiport = ActiveVertiport();
            return vertiport?.Boundary != null && PointInPolygonXZ(point, vertiport.Boundary);
        }

        public static bool PointInPolygonXZ(Vector3 point, IList<Vector3> polygon)
        {
            if (polygon == null || polygon.Count < 3) return false;
            bool inside = false;
            int previous = polygon.Count - 1;
            for (int current = 0; current < polygon.Count; current++)
            {
                Vector3 a = polygon[current];
                Vector3 b = polygon[previous];
                float dz = b.z - a.z;
                if (Mathf.Abs(dz) < 0.000001f) dz = dz < 0f ? -0.000001f : 0.000001f;
                bool crosses = (a.z > point.z) != (b.z > point.z) &&
                               point.x < (b.x - a.x) * (point.z - a.z) / dz + a.x;
                if (crosses) inside = !inside;
                previous = current;
            }
            return inside;
        }

        private static float SignedAreaXZ(IList<Vector3> polygon)
        {
            if (polygon == null || polygon.Count < 3) return 0f;
            float twiceArea = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector3 current = polygon[i];
                Vector3 next = polygon[(i + 1) % polygon.Count];
                twiceArea += current.x * next.z - next.x * current.z;
            }
            return twiceArea * 0.5f;
        }

        private static List<Vector3> BuildRectangle(Vector3 center, float width, float depth,
            float rotationDegrees)
        {
            float x = width * 0.5f;
            float z = depth * 0.5f;
            List<Vector3> points = new List<Vector3>
            {
                center + new Vector3(-x, 0f, -z), center + new Vector3(x, 0f, -z),
                center + new Vector3(x, 0f, z), center + new Vector3(-x, 0f, z)
            };
            return RotateAround(points, center, rotationDegrees);
        }

        private static List<Vector3> RotateAroundCentroid(List<Vector3> points, float rotationDegrees)
        {
            if (points == null || points.Count == 0) return points ?? new List<Vector3>();
            Vector3 center = points.Aggregate(Vector3.zero, (sum, point) => sum + point) / points.Count;
            return RotateAround(points, center, rotationDegrees);
        }

        private static List<Vector3> RotateAround(List<Vector3> points, Vector3 center, float rotationDegrees)
        {
            if (points == null || Mathf.Approximately(Mathf.Repeat(rotationDegrees, 360f), 0f))
                return points ?? new List<Vector3>();
            Quaternion rotation = Quaternion.AngleAxis(rotationDegrees, Vector3.up);
            return points.Select(point => center + rotation * (point - center)).ToList();
        }

        private static List<Vector3> BuildCircle(Vector3 center, float radius, int segments)
        {
            var result = new List<Vector3>(segments);
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                result.Add(center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius);
            }
            return result;
        }

        private bool RequireEditableActiveLayer(string action)
        {
            if (!ActiveLayerIsFrozen) return true;
            Raise($"The current network layer is frozen, so you cannot {action}. Click the snowflake button to unfreeze it first. ");
            return false;
        }

        private void TakeOverLegacyAxisVisualization()
        {
            if (manager?.networkManager == null) return;
            if (!previousFullNetworkGizmoState.HasValue)
                previousFullNetworkGizmoState = manager.networkManager.showFullNetwork;
            manager.networkManager.showFullNetwork = false;
            manager.networkManager.SetRuntimeAxisOverlayActive(true);
        }

        private bool SyncDerivedLayerMetadata(AltitudeLayer source, bool copyVisibility, bool copyFrozen)
        {
            AltitudeLayer derived = MultilayerNetworkUtility.FindLayer(
                manager?.networkManager?.FullNetwork, source?.LayerId);
            if (derived == null || source == null) return false;
            bool changed = false;
            if (copyVisibility && derived.Enabled != source.Enabled)
            {
                derived.Enabled = source.Enabled;
                changed = true;
            }
            if (copyFrozen && derived.Frozen != source.Frozen)
            {
                derived.Frozen = source.Frozen;
                changed = true;
            }
            return changed;
        }

        private bool ActiveLayerCanBeSeen()
        {
            return ActiveLayerIsVisible &&
                   (visualizer == null || visualizer.IsVisible(AirspaceVisualLayer.Network));
        }

        private bool CanReorderActiveLayerTo(int targetDescendingIndex, out AltitudeLayer blockingLayer)
        {
            blockingLayer = null;
            List<AltitudeLayer> ordered = Layers.ToList();
            int sourceIndex = ordered.FindIndex(layer => layer.LayerId == ActiveLayerId);
            if (sourceIndex < 0 || ordered.Count == 0) return false;
            targetDescendingIndex = Mathf.Clamp(targetDescendingIndex, 0, ordered.Count - 1);
            int first = Mathf.Min(sourceIndex, targetDescendingIndex);
            int last = Mathf.Max(sourceIndex, targetDescendingIndex);
            blockingLayer = ordered.Skip(first).Take(last - first + 1)
                .FirstOrDefault(layer => layer.Frozen);
            return blockingLayer == null;
        }

        private bool CanSwapActiveLayer(int direction, out AltitudeLayer blockingLayer)
        {
            blockingLayer = null;
            if (direction == 0) return false;
            List<AltitudeLayer> ordered = AxisNetwork?.AltitudeLayers?
                .Where(layer => layer != null)
                .OrderBy(layer => layer.NominalAltitude)
                .ThenBy(layer => layer.LayerId)
                .ToList() ?? new List<AltitudeLayer>();
            int sourceIndex = ordered.FindIndex(layer => layer.LayerId == ActiveLayerId);
            int targetIndex = sourceIndex + Math.Sign(direction);
            if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= ordered.Count) return false;
            blockingLayer = ordered[sourceIndex].Frozen
                ? ordered[sourceIndex]
                : ordered[targetIndex].Frozen ? ordered[targetIndex] : null;
            return blockingLayer == null;
        }

        private static bool IsNetworkMutationTool(RuntimeEditTool tool)
        {
            return tool == RuntimeEditTool.AddNetworkNode ||
                   tool == RuntimeEditTool.ConnectNetworkNodes ||
                   tool == RuntimeEditTool.AddNetworkWaypoint ||
                   tool == RuntimeEditTool.MoveNetworkNode ||
                   tool == RuntimeEditTool.MoveNetworkWaypoint;
        }

        private Vertiport ActiveVertiport() => manager?.vertiportManager?.Vertiports?
            .FirstOrDefault(vertiport => vertiport.VertiportId == ActiveVertiportId);

        private void CancelPendingSelection()
        {
            firstNetworkNode = null;
            selectedNetworkNode = null;
            selectedNetworkEdge = null;
            selectedNetworkWaypointIndex = -1;
            AxisSelectionKind = RuntimeAxisSelectionKind.None;
            firstApproachNode = null;
            firstLane = null;
            firstLayerConnectorPoint = default;
            corridorLane = null;
            movingNode = null;
            movingWaypointEdge = null;
            movingWaypointIndex = -1;
            movingAxisSnapshot = null;
            movingBoundaryPointIndex = -1;
            movingBoundaryBackup = null;
            movingApproachNode = null;
            firstHoldingVertex = null;
            movingHoldingStructure = null;
            corridorLane = null;
            corridorRamp = null;
            movingCorridor = null;
            movingLayerConnector = null;
            pendingVertiportBoundary.Clear();
            pendingHelipadPolygon.Clear();
            SelectedBoundarySegmentIndex = -1;
            SelectedBoundaryPointIndex = -1;
            manager?.networkManager?.ClearSelection();
            ClearHover();
        }

        private AxisEditSnapshot CaptureAxisSnapshot()
        {
            var snapshot = new AxisEditSnapshot
            {
                Axis = MultilayerNetworkUtility.CloneAxisNetwork(AxisNetwork)
            };
            foreach (Vertiport vertiport in manager?.vertiportManager?.Vertiports ?? new List<Vertiport>())
                foreach (NetConnectedCorridor corridor in vertiport.Corridors ?? new List<NetConnectedCorridor>())
                    snapshot.FacilityCorridors.Add(new FacilityCorridorBackup
                    {
                        VertiportId = vertiport.VertiportId,
                        Corridor = CloneFacilityCorridor(corridor)
                    });
            return snapshot;
        }

        private void PushAxisUndo(AxisEditSnapshot snapshot)
        {
            if (snapshot?.Axis == null) return;
            string previousLayerId = ActiveLayerId;
            undoActions.Push(() =>
            {
                manager.networkManager.AxisNetwork = MultilayerNetworkUtility.CloneAxisNetwork(snapshot.Axis);
                RestoreFacilityCorridors(snapshot.FacilityCorridors);
                ActiveLayerId = MultilayerNetworkUtility.FindLayer(manager.networkManager.AxisNetwork, previousLayerId)
                    ?.LayerId ?? manager.networkManager.AxisNetwork.AltitudeLayers.FirstOrDefault()?.LayerId;
                EditingAltitude = ActiveLayer?.NominalAltitude ?? EditingAltitude;
            });
        }

        private void RestoreFacilityCorridors(IEnumerable<FacilityCorridorBackup> backups)
        {
            foreach (Vertiport vertiport in manager.vertiportManager.Vertiports)
            {
                vertiport.Corridors = new List<NetConnectedCorridor>();
                foreach (HoldingStructure holding in vertiport.HoldingStructures ?? new List<HoldingStructure>())
                    foreach (Ramp ramp in holding.Ramps ?? new List<Ramp>())
                        ramp.LinkCorridors = new List<(string, Vector3)>();
            }
            foreach (FacilityCorridorBackup backup in backups ?? Enumerable.Empty<FacilityCorridorBackup>())
            {
                Vertiport vertiport = manager.vertiportManager.Vertiports
                    .FirstOrDefault(item => item.VertiportId == backup.VertiportId);
                if (vertiport == null || backup.Corridor == null) continue;
                NetConnectedCorridor corridor = CloneFacilityCorridor(backup.Corridor);
                vertiport.Corridors.Add(corridor);
                Ramp ramp = vertiport.HoldingStructures?
                    .SelectMany(holding => holding.Ramps ?? new List<Ramp>())
                    .FirstOrDefault(item => item.RampId == corridor.LinkRampId);
                ramp?.LinkCorridors.Add((corridor.CorridorId, corridor.RampLinkPoint));
            }
        }

        private static NetConnectedCorridor CloneFacilityCorridor(NetConnectedCorridor source)
        {
            if (source == null) return null;
            return new NetConnectedCorridor
            {
                CorridorId = source.CorridorId,
                CorridorType = source.CorridorType,
                LinkLaneId = source.LinkLaneId,
                LinkRampId = source.LinkRampId,
                LaneLinkPoint = source.LaneLinkPoint,
                RampLinkPoint = source.RampLinkPoint
            };
        }

        private void Raise(string text) => StatusChanged?.Invoke(text);

        public static string WorkspaceName(AirspaceEditWorkspace workspace)
        {
            switch (workspace)
            {
                case AirspaceEditWorkspace.NetworkLayers: return "Network Layer Editing";
                case AirspaceEditWorkspace.GroundFacility: return "Vertiport Ground Structure";
                case AirspaceEditWorkspace.HoldingStructure: return "Holding Structure";
                case AirspaceEditWorkspace.ConnectionCorridor: return "Connection Corridors";
                default: return workspace.ToString();
            }
        }

        public static string LayerLabel(AltitudeLayer layer)
        {
            if (layer == null) return "No layer selected";
            string name = string.IsNullOrWhiteSpace(layer.DisplayName) ? layer.LayerId : layer.DisplayName;
            return $"{name} · {layer.NominalAltitude:F0} m";
        }

        public static string ToolName(RuntimeEditTool tool)
        {
            switch (tool)
            {
                case RuntimeEditTool.AddNetworkNode: return "Add Centerline Node";
                case RuntimeEditTool.ConnectNetworkNodes: return "Connect Centerline Edge";
                case RuntimeEditTool.AddNetworkWaypoint: return "Add Edge Waypoint";
                case RuntimeEditTool.MoveNetworkNode: return "Move Node in Plane";
                case RuntimeEditTool.MoveNetworkWaypoint: return "Move Waypoint in Plane";
                case RuntimeEditTool.DrawVertiportBoundary: return "Draw New Vertiport Boundary";
                case RuntimeEditTool.AddVertiportBoundaryPoint: return "Add Boundary Point";
                case RuntimeEditTool.MoveVertiportBoundaryPoint: return "Move Boundary Point";
                case RuntimeEditTool.AddHelipad: return "Add Helipad";
                case RuntimeEditTool.AddHoldingStructure: return "Add Holding Structure";
                case RuntimeEditTool.MoveHoldingStructure: return "Move Holding Anchor";
                case RuntimeEditTool.ConnectHoldingVertices: return "Connect Holding Vertices";
                case RuntimeEditTool.AddApproachNode: return "Add Approach Node";
                case RuntimeEditTool.MoveApproachNode: return "Move Approach Node";
                case RuntimeEditTool.ConnectApproachNodes: return "Connect Approach Edge";
                case RuntimeEditTool.AddFacilityCorridor: return "Add Vertiport Corridor";
                case RuntimeEditTool.MoveFacilityCorridorLink: return "Move Corridor Access Point";
                case RuntimeEditTool.AddLayerConnector: return "Add Interlayer Connector";
                default: return "View / Select";
            }
        }
    }
}
