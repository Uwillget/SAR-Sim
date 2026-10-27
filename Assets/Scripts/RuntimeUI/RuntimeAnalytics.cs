using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RoadNetworkEditor.RuntimeUI
{
    public sealed class RuntimeAnalytics : MonoBehaviour
    {
        private sealed class ActiveTrack
        {
            public AircraftState State;
            public readonly List<Vector3> Points = new List<Vector3>();
        }

        private SimulationManager manager;
        private RuntimeAirspaceVisualizer visualizer;
        private readonly Dictionary<AircraftState, ActiveTrack> activeTracks =
            new Dictionary<AircraftState, ActiveTrack>();
        private readonly Dictionary<string, List<Vector3>> completedTracks =
            new Dictionary<string, List<Vector3>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> trackLabels =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> aircraftMissionSequences =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<AircraftState> completedStates = new HashSet<AircraftState>();
        private float nextSample;

        public void Initialize(SimulationManager simulationManager, RuntimeAirspaceVisualizer airspaceVisualizer)
        {
            if (manager != null)
            {
                manager.MissionCompleted -= HandleMissionCompleted;
                manager.SimulationStarted -= HandleSimulationStarted;
            }
            manager = simulationManager;
            visualizer = airspaceVisualizer;
            if (manager != null)
            {
                manager.MissionCompleted += HandleMissionCompleted;
                manager.SimulationStarted += HandleSimulationStarted;
            }
        }

        private void OnDestroy()
        {
            if (manager != null)
            {
                manager.MissionCompleted -= HandleMissionCompleted;
                manager.SimulationStarted -= HandleSimulationStarted;
            }
        }

        private void HandleSimulationStarted()
        {
            ClearTracks();
        }

        private void Update()
        {
            if (manager == null || Time.unscaledTime < nextSample) return;
            nextSample = Time.unscaledTime + 0.25f;
            foreach (GameObject aircraftObject in manager.AircraftObjects.Values)
            {
                if (aircraftObject == null) continue;
                AircraftAgent agent = aircraftObject.GetComponent<AircraftAgent>();
                AircraftState state = agent?.aircraftState;
                if (state == null || completedStates.Contains(state)) continue;
                if (!activeTracks.TryGetValue(state, out ActiveTrack track))
                {
                    track = new ActiveTrack { State = state };
                    activeTracks[state] = track;
                    if (state.MissionKeyNodes != null)
                        AddPoint(track.Points, state.MissionKeyNodes.OriginHelipad, 0f);
                }
                AddPoint(track.Points, aircraftObject.transform.position, 0.35f);
            }
        }

        private void HandleMissionCompleted(AircraftState state, Vector3 finalPosition)
        {
            if (state == null || !completedStates.Add(state)) return;
            if (!activeTracks.TryGetValue(state, out ActiveTrack active))
            {
                active = new ActiveTrack { State = state };
                if (state.MissionKeyNodes != null)
                    AddPoint(active.Points, state.MissionKeyNodes.OriginHelipad, 0f);
            }
            AddPoint(active.Points, finalPosition, 0f);

            string aircraftId = string.IsNullOrWhiteSpace(state.AircraftId) ? "Unnamed aircraft" : state.AircraftId;
            int sequence = aircraftMissionSequences.TryGetValue(aircraftId, out int current) ? current + 1 : 1;
            aircraftMissionSequences[aircraftId] = sequence;
            string trackId = $"{aircraftId}#{sequence:D3}";
            completedTracks[trackId] = new List<Vector3>(active.Points);
            string origin = state.Mission?.OriginVertiport ?? state.OriginHelipadId ?? "?";
            string destination = state.Mission?.DestinationVertiport ?? state.DestinationHelipadId ?? "?";
            trackLabels[trackId] = $"{aircraftId} · Run {sequence} · {origin} → {destination}";
            activeTracks.Remove(state);
        }

        public IEnumerable<string> CompletedTrackIds => completedTracks.Keys.OrderBy(id => id, StringComparer.Ordinal);
        public int CompletedTrackCount => completedTracks.Count;

        public string GetTrackLabel(string trackId)
        {
            return trackLabels.TryGetValue(trackId ?? string.Empty, out string label) ? label : trackId;
        }

        public bool ShowTracks(IEnumerable<string> trackIds)
        {
            List<List<Vector3>> paths = (trackIds ?? Enumerable.Empty<string>())
                .Where(id => completedTracks.ContainsKey(id))
                .Select(id => completedTracks[id])
                .Where(points => points.Count > 1).ToList();
            visualizer?.SetRoutePreviews(paths);
            return paths.Count > 0;
        }

        /// <summary>
        /// Replaces the current-session trajectory catalogue with one archived run.
        /// Keeping the catalogue and visualizer in sync prevents a later checklist
        /// interaction from redrawing trajectories that belonged to the previous run.
        /// </summary>
        public IReadOnlyList<string> ReplaceWithHistoricalTracks(
            IEnumerable<IEnumerable<Vector3>> paths, string sourceLabel)
        {
            ClearTrackState();
            int sequence = 0;
            foreach (IEnumerable<Vector3> path in paths ?? Enumerable.Empty<IEnumerable<Vector3>>())
            {
                List<Vector3> points = path?.ToList();
                if (points == null || points.Count < 2) continue;
                sequence++;
                string id = $"Historical#{sequence:D3}";
                completedTracks[id] = points;
                trackLabels[id] = $"{(string.IsNullOrWhiteSpace(sourceLabel) ? "Archived run" : sourceLabel)} · Trajectory {sequence:D3}";
            }

            List<string> ids = CompletedTrackIds.ToList();
            ShowTracks(ids);
            return ids;
        }

        public void ClearTrackDisplay()
        {
            visualizer?.SetRoutePreviews(null);
        }

        public void ClearTracks()
        {
            ClearTrackState();
            ClearTrackDisplay();
        }

        private void ClearTrackState()
        {
            activeTracks.Clear();
            completedTracks.Clear();
            trackLabels.Clear();
            aircraftMissionSequences.Clear();
            completedStates.Clear();
        }

        public string BuildStatistics()
        {
            if (manager == null) return "No statistics available";
            int completedMissionCount = manager.cumulSuccessCount + manager.cumulFailCount;
            float avgTime = manager.cumulSuccessCount > 0 ? manager.cumulTotalFlightTime / manager.cumulSuccessCount : 0f;
            float avgDistance = manager.cumulSuccessCount > 0 ? manager.cumulTotalFlightLength / manager.cumulSuccessCount : 0f;
            float laneUtil = manager.cumulUtilSampleCount > 0 ? manager.cumulLaneUtilSum / manager.cumulUtilSampleCount : 0f;
            return
                $"Completed missions   {completedMissionCount}\n" +
                $"Succeeded / Failed   {manager.cumulSuccessCount} / {manager.cumulFailCount}\n" +
                $"Collision events     {manager.TotalCollisionEvents}\n" +
                $"Mean flight time     {avgTime:F1} s\n" +
                $"Mean flight distance {avgDistance:F1} m\n" +
                $"Mean lane occupancy  {laneUtil:P1}\n" +
                $"Completed trajectories {completedTracks.Count}";
        }

        private static void AddPoint(List<Vector3> points, Vector3 point, float minDistance)
        {
            if (points.Count == 0 || Vector3.Distance(points[points.Count - 1], point) > minDistance)
                points.Add(point);
        }
    }
}
