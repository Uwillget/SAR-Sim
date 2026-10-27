using System;
using System.Collections.Generic;
using System.Linq;
using RoadNetworkEditor.Networking;
using RoadNetworkEditor.Routes;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RoadNetworkEditor.RuntimeUI
{
    public enum RouteSelectionMode
    {
        AutomaticShortest,
        Manual
    }

    public sealed class RuntimeRouteDesigner : MonoBehaviour
    {
        public event Action<string> StatusChanged;
        public event Action StateChanged;

        public string OriginHelipadId { get; private set; }
        public string DestinationHelipadId { get; private set; }
        public string SelectedMissionId { get; private set; }
        public RouteSelectionMode SelectionMode { get; private set; } = RouteSelectionMode.AutomaticShortest;
        private bool laneSelectionEnabled;
        public bool LaneSelectionEnabled
        {
            get => laneSelectionEnabled;
            set
            {
                laneSelectionEnabled = value;
                if (!value) HoveredEdgeId = null;
            }
        }
        public bool RouteConfirmed { get; private set; }
        public readonly List<string> SelectedLaneIds = new List<string>();
        public readonly List<string> SelectedEdgeIds = new List<string>();
        public string LockedFirstEdgeId { get; private set; }
        public bool ManualRouteConnectsDestination { get; private set; }
        public string HoveredEdgeId { get; private set; }

        private string lockedStartLaneId;

        private SimulationManager manager;
        private RuntimeSceneNavigator navigator;
        private RuntimeAirspaceVisualizer visualizer;
        private int taskSequence;

        public void Initialize(SimulationManager simulationManager, RuntimeSceneNavigator sceneNavigator,
            RuntimeAirspaceVisualizer airspaceVisualizer)
        {
            manager = simulationManager;
            navigator = sceneNavigator;
            visualizer = airspaceVisualizer;
            visualizer?.BindRouteDesigner(this);
            List<string> pads = manager.Helipads.Keys.OrderBy(id => id).ToList();
            OriginHelipadId = pads.FirstOrDefault();
            string originVertiport = OriginHelipadId != null &&
                                     manager.HelipadToVertiportMap.TryGetValue(OriginHelipadId, out string vp)
                ? vp
                : null;
            DestinationHelipadId = pads.FirstOrDefault(id =>
                manager.HelipadToVertiportMap.TryGetValue(id, out string destinationVp) &&
                destinationVp != originVertiport) ?? pads.Skip(1).FirstOrDefault() ?? pads.FirstOrDefault();
            BuildShortestRoute(false);
        }

        public void SetOrigin(string helipadId)
        {
            OriginHelipadId = helipadId;
            EndpointChanged();
        }

        public void SetDestination(string helipadId)
        {
            DestinationHelipadId = helipadId;
            EndpointChanged();
        }

        private void EndpointChanged()
        {
            SelectedMissionId = null;
            if (SelectionMode == RouteSelectionMode.AutomaticShortest)
                BuildShortestRoute(false);
            else
                InitializeManualRoute(false);
            StateChanged?.Invoke();
        }

        public void SetSelectionMode(RouteSelectionMode mode)
        {
            SelectionMode = mode;
            SelectedMissionId = null;
            LaneSelectionEnabled = mode == RouteSelectionMode.Manual;
            if (mode == RouteSelectionMode.AutomaticShortest)
            {
                BuildShortestRoute(false);
                Raise("Automatic shortest-path routing selected. ");
            }
            else
            {
                InitializeManualRoute(false);
                Raise(string.IsNullOrEmpty(LockedFirstEdgeId)
                    ? "The first centerline edge from the departure corridor could not be determined. Check endpoint connectivity. "
                    : $"Manual routing enabled: the departure corridor has locked first edge {LockedFirstEdgeId}. Select the next centerline edge. ");
            }
            StateChanged?.Invoke();
        }

        public void FocusOrigin() => FocusHelipad(OriginHelipadId, "origin");

        public void FocusDestination() => FocusHelipad(DestinationHelipadId, "destination");

        private void FocusHelipad(string helipadId, string label)
        {
            if (manager == null || string.IsNullOrEmpty(helipadId) ||
                !manager.Helipads.TryGetValue(helipadId, out Helipad pad) ||
                pad.Boundary == null || pad.Boundary.Count == 0)
            {
                Raise($"The {label} helipad could not be located. ");
                return;
            }
            navigator?.FramePoints(pad.Boundary, 2.2f, true);
            Raise($"Focused the {label} helipad {helipadId}. ");
        }

        public void ClearRoute() => ClearRoute(true);

        private void ClearRoute(bool notify)
        {
            SelectedLaneIds.Clear();
            SelectedEdgeIds.Clear();
            RouteConfirmed = false;
            ManualRouteConnectsDestination = false;
            if (SelectionMode == RouteSelectionMode.Manual && !string.IsNullOrEmpty(LockedFirstEdgeId))
            {
                SelectedEdgeIds.Add(LockedFirstEdgeId);
                TryResolveSelectedEdges(false, out _, out _);
            }
            UpdatePreview();
            StateChanged?.Invoke();
            if (notify) Raise(string.IsNullOrEmpty(LockedFirstEdgeId)
                ? "Manual route cleared. "
                : $"Reverted to the locked first edge {LockedFirstEdgeId}. ");
        }

        public bool BuildShortestRoute(bool notify = true)
        {
            SelectedLaneIds.Clear();
            SelectedEdgeIds.Clear();
            RouteConfirmed = false;
            ManualRouteConnectsDestination = false;
            if (!RouteDemandAuthoring.TryBuildShortestLaneRoute(manager, OriginHelipadId,
                    DestinationHelipadId, out List<string> route, out string routeError))
            {
                UpdatePreview();
                if (notify) Raise(routeError + " ");
                return false;
            }

            SelectedLaneIds.AddRange(route);
            SelectedEdgeIds.AddRange(RouteDemandAuthoring.CollapseLaneEdges(manager, SelectedLaneIds));
            RouteConfirmed = true;
            UpdatePreview();
            StateChanged?.Invoke();
            if (notify) Raise($"Shortest path generated: {SelectedEdgeIds.Count} centerline edges resolved to {SelectedLaneIds.Count} lanes. ");
            return true;
        }

        public bool ConfirmManualRoute()
        {
            if (SelectionMode != RouteSelectionMode.Manual)
            {
                Raise("Automatic shortest-path routing does not require manual confirmation. ");
                return false;
            }
            if (!TryResolveSelectedEdges(true, out List<string> laneIds, out string error))
            {
                RouteConfirmed = false;
                Raise(error);
                StateChanged?.Invoke();
                return false;
            }
            SelectedLaneIds.Clear();
            SelectedLaneIds.AddRange(laneIds);
            RouteConfirmed = true;
            ManualRouteConnectsDestination = true;
            LaneSelectionEnabled = false;
            UpdatePreview();
            StateChanged?.Invoke();
            Raise($"Manual route confirmed: {SelectedEdgeIds.Count} centerline edges resolved to {SelectedLaneIds.Count} lanes. ");
            return true;
        }

        public bool AddSingle(float departureTime, int priority = 1)
        {
            if (!Validate(out string error)) { Raise(error); return false; }
            manager.AddMission(CreateMission(departureTime, priority));
            SelectedMissionId = null;
            StateChanged?.Invoke();
            Raise("One aircraft mission added. ");
            return true;
        }

        public int AddFlow(int count, float interval, float startTime, int priority = 1)
        {
            if (!Validate(out string error)) { Raise(error); return 0; }
            count = Mathf.Clamp(count, 1, 10000);
            interval = Mathf.Max(0.1f, interval);
            for (int i = 0; i < count; i++)
                manager.AddMission(CreateMission(startTime + i * interval, priority));
            SelectedMissionId = null;
            StateChanged?.Invoke();
            Raise($"Traffic flow generated: {count} aircraft at {interval:F1}-second intervals. ");
            return count;
        }

        public bool LoadMissionForEdit(string taskId)
        {
            MissionTask mission = manager?.GetPendingMissions()
                .FirstOrDefault(task => task.TaskId == taskId);
            if (mission == null) { Raise("The selected mission is no longer in the pending queue. "); return false; }

            SelectedMissionId = mission.TaskId;
            OriginHelipadId = mission.OriginHelipadId;
            DestinationHelipadId = mission.DestinationHelipadId;
            SelectedLaneIds.Clear();
            if (mission.RouteLaneIds != null) SelectedLaneIds.AddRange(mission.RouteLaneIds);
            SelectedEdgeIds.Clear();
            SelectedEdgeIds.AddRange(CollapseLaneEdges(SelectedLaneIds));
            SelectionMode = SelectedLaneIds.Count > 0
                ? RouteSelectionMode.Manual
                : RouteSelectionMode.AutomaticShortest;
            LaneSelectionEnabled = false;
            RouteConfirmed = SelectionMode == RouteSelectionMode.Manual;
            lockedStartLaneId = SelectedLaneIds.FirstOrDefault();
            LockedFirstEdgeId = SelectedEdgeIds.FirstOrDefault();
            ManualRouteConnectsDestination = RouteConfirmed;
            if (SelectionMode == RouteSelectionMode.AutomaticShortest)
                BuildShortestRoute(false);
            else
                UpdatePreview();
            StateChanged?.Invoke();
            Raise($"Mission {taskId} loaded. Edit it and update the queued entry. ");
            return true;
        }

        public bool UpdateSelectedMission(float departureTime, int priority = 1)
        {
            if (string.IsNullOrEmpty(SelectedMissionId))
            {
                Raise("Select a mission to edit from the mission list first. ");
                return false;
            }
            if (!Validate(out string error)) { Raise(error); return false; }
            MissionTask replacement = CreateMission(departureTime, priority);
            replacement.TaskId = SelectedMissionId;
            if (!manager.ReplaceMission(SelectedMissionId, replacement))
            {
                Raise("Update failed: the mission is no longer in the pending queue. ");
                return false;
            }
            StateChanged?.Invoke();
            Raise($"Mission {SelectedMissionId} updated. ");
            return true;
        }

        public string RouteSummary
        {
            get
            {
                if (SelectionMode == RouteSelectionMode.AutomaticShortest)
                    return SelectedLaneIds.Count == 0
                        ? "Automatic shortest path · Disconnected"
                        : $"Automatic shortest path · {SelectedEdgeIds.Count} centerline edges";
                return $"Manual route · {SelectedEdgeIds.Count} centerline edges · " +
                       (RouteConfirmed ? "Confirmed" : ManualRouteConnectsDestination ? "Connected; confirmation required" : "Select the next edge");
            }
        }

        private MissionTask CreateMission(float departureTime, int priority)
        {
            int sequence = ++taskSequence;
            return RouteDemandAuthoring.CreateMission(manager, OriginHelipadId, DestinationHelipadId,
                SelectionMode == RouteSelectionMode.Manual ? SelectedLaneIds : Enumerable.Empty<string>(),
                departureTime, priority, $"UI-{DateTime.Now:HHmmss}-{sequence:D4}",
                $"R-UI-{sequence:D4}");
        }

        private bool Validate(out string error)
        {
            if (manager == null || manager.IsRunning())
            { error = "Stop the simulation before editing demand. "; return false; }
            if (!ValidateEndpoints(out error)) return false;
            if (SelectionMode == RouteSelectionMode.AutomaticShortest)
            {
                if (!BuildShortestRoute(false))
                { error = "No automatic shortest path is available. Check origin-destination connectivity. "; return false; }
            }
            else if (!RouteConfirmed || !ValidateExplicitRoute(out error))
            {
                if (string.IsNullOrEmpty(error)) error = "Confirm the manual route first. ";
                return false;
            }
            error = null;
            return true;
        }

        private bool ValidateEndpoints(out string error)
        {
            return RouteDemandAuthoring.ValidateEndpoints(manager, OriginHelipadId,
                DestinationHelipadId, out error);
        }

        private bool ValidateExplicitRoute(out string error)
        {
            if (!TryResolveSelectedEdges(true, out List<string> lanes, out error)) return false;
            SelectedLaneIds.Clear();
            SelectedLaneIds.AddRange(lanes);
            return true;
        }

        private bool InitializeManualRoute(bool notify)
        {
            SelectedLaneIds.Clear();
            SelectedEdgeIds.Clear();
            LockedFirstEdgeId = null;
            lockedStartLaneId = null;
            RouteConfirmed = false;
            ManualRouteConnectsDestination = false;
            if (!ValidateEndpoints(out string error))
            {
                UpdatePreview();
                if (notify) Raise(error);
                return false;
            }

            if (!RouteDemandAuthoring.TryGetManualRouteStart(manager, OriginHelipadId,
                    DestinationHelipadId, out lockedStartLaneId, out string lockedFirstEdgeId,
                    out string startError))
            {
                UpdatePreview();
                if (notify) Raise(startError + " ");
                return false;
            }
            LockedFirstEdgeId = lockedFirstEdgeId;
            SelectedEdgeIds.Add(LockedFirstEdgeId);
            ManualRouteConnectsDestination = TryResolveSelectedEdges(true,
                out List<string> initialRoute, out _);
            if (!ManualRouteConnectsDestination)
                TryResolveSelectedEdges(false, out initialRoute, out _);
            SelectedLaneIds.Clear();
            SelectedLaneIds.AddRange(initialRoute);
            UpdatePreview();
            StateChanged?.Invoke();
            if (notify) Raise(ManualRouteConnectsDestination
                ? $"The locked departure edge {LockedFirstEdgeId} connects directly to the destination. The route can now be confirmed. "
                : $"Departure access edge {LockedFirstEdgeId} locked. ");
            return true;
        }

        private bool TryResolveSelectedEdges(bool requireDestination, out List<string> laneIds, out string error)
        {
            return RouteDemandAuthoring.TryResolveManualEdges(manager, OriginHelipadId,
                DestinationHelipadId, SelectedEdgeIds, requireDestination, out laneIds, out error);
        }

        private void Update()
        {
            if (!LaneSelectionEnabled || SelectionMode != RouteSelectionMode.Manual ||
                navigator?.ControlledCamera == null)
            {
                HoveredEdgeId = null;
                return;
            }
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                HoveredEdgeId = null;
                return;
            }

            Edge edge = FindNearestAxisEdge(30f);
            HoveredEdgeId = edge?.EdgeId;
            if (!Input.GetMouseButtonDown(0)) return;
            if (edge == null) return;

            if (!RouteDemandAuthoring.TryApplyManualEdgeClick(manager, OriginHelipadId,
                    DestinationHelipadId, SelectedEdgeIds, edge.EdgeId,
                    out List<string> resolvedLanes, out bool connectsDestination,
                    out string selectionError))
            {
                Raise($"Edge {edge.EdgeId} was rejected: {selectionError} ");
                return;
            }

            RouteConfirmed = false;
            ManualRouteConnectsDestination = connectsDestination;
            SelectedLaneIds.Clear();
            SelectedLaneIds.AddRange(resolvedLanes);
            UpdatePreview();
            StateChanged?.Invoke();
            Raise(ManualRouteConnectsDestination
                ? "The selected centerline edges now form a complete route to the destination exit corridor. Confirm to save. "
                : $"Centerline edge {edge.EdgeId} appended. Lane-level connectivity is valid; select the next edge. ");
        }

        private Edge FindNearestAxisEdge(float maxPixels)
        {
            Edge best = null;
            float bestDistance = maxPixels;
            Vector2 mouse = Input.mousePosition;
            IEnumerable<Edge> edges = manager.networkManager?.AxisNetwork?.Edges;
            if (edges == null) edges = manager.Edges.Values;
            foreach (Edge edge in edges)
            {
                if (edge?.Centerline == null || edge.Centerline.Count < 2) continue;
                for (int i = 1; i < edge.Centerline.Count; i++)
                {
                    Vector3 a3 = navigator.ControlledCamera.WorldToScreenPoint(edge.Centerline[i - 1]);
                    Vector3 b3 = navigator.ControlledCamera.WorldToScreenPoint(edge.Centerline[i]);
                    if (a3.z < 0f || b3.z < 0f) continue;
                    float distance = DistanceToSegment(mouse, new Vector2(a3.x, a3.y),
                        new Vector2(b3.x, b3.y));
                    if (distance < bestDistance) { bestDistance = distance; best = edge; }
                }
            }
            return best;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 from, Vector2 to)
        {
            Vector2 line = to - from;
            float lengthSq = line.sqrMagnitude;
            if (lengthSq <= 0.001f) return Vector2.Distance(point, from);
            float t = Mathf.Clamp01(Vector2.Dot(point - from, line) / lengthSq);
            return Vector2.Distance(point, from + line * t);
        }

        public void UpdatePreview()
        {
            List<Edge> edges = SelectedEdgeIds
                .Select(id => manager != null && manager.Edges.TryGetValue(id, out Edge edge) ? edge : null)
                .Where(edge => edge != null).ToList();
            visualizer?.SetRouteEdgePreview(edges);
        }

        private void AddPadCenter(string id, List<Vector3> points)
        {
            if (manager == null || string.IsNullOrEmpty(id) || !manager.Helipads.TryGetValue(id, out Helipad pad) ||
                pad.Boundary == null || pad.Boundary.Count == 0) return;
            points.Add(pad.Boundary.Aggregate(Vector3.zero, (sum, point) => sum + point) / pad.Boundary.Count);
        }

        private List<string> CollapseLaneEdges(IEnumerable<string> laneIds)
        {
            return RouteDemandAuthoring.CollapseLaneEdges(manager, laneIds);
        }

        private void Raise(string text) => StatusChanged?.Invoke(text);
    }
}
