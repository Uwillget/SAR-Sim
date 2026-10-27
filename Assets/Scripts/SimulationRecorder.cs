using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Records simulation data to SimulationRecording.json at regular intervals.
    /// Captures: lane occupancy, corridor occupancy, ramp occupancy, approach edge occupancy,
    /// aircraft trajectories, phase change events, and collision events.
    /// Auto-started by SimulationManager when simulation begins.
    /// </summary>
    public class SimulationRecorder : MonoBehaviour
    {
        public static SimulationRecorder Instance { get; private set; }

        private SimulationManager sim;
        private float recordInterval = 0.5f;
        private float nextRecordTime;
        private bool isRecording;

        // Recording data
        private SimulationRecording recording;
        private float elapsedTime;
        private int frameCount;
        private int recordFrameCount;
        private readonly Dictionary<string, LaneTimeSeriesRecord> laneSeries =
            new Dictionary<string, LaneTimeSeriesRecord>();
        private readonly Dictionary<string, CorridorTimeSeriesRecord> corridorSeries =
            new Dictionary<string, CorridorTimeSeriesRecord>();
        private readonly Dictionary<string, RampTimeSeriesRecord> rampSeries =
            new Dictionary<string, RampTimeSeriesRecord>();
        private readonly Dictionary<string, ApproachEdgeTimeSeriesRecord> approachSeries =
            new Dictionary<string, ApproachEdgeTimeSeriesRecord>();
        private readonly Dictionary<string, AircraftTrajectoryRecord> trajectorySeries =
            new Dictionary<string, AircraftTrajectoryRecord>();
        private readonly Dictionary<string, int> trajectorySequences =
            new Dictionary<string, int>();

        public string LastOutputPath { get; private set; }

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
        }

        public void StartRecording(SimulationManager simulationManager)
        {
            if (sim != null)
                sim.MissionCompleted -= HandleMissionCompleted;
            sim = simulationManager;
            if (sim != null)
                sim.MissionCompleted += HandleMissionCompleted;
            recordInterval = 0.5f;
            nextRecordTime = Time.time;
            isRecording = true;
            elapsedTime = 0f;
            frameCount = 0;
            recordFrameCount = 0;
            LastOutputPath = null;
            laneSeries.Clear();
            corridorSeries.Clear();
            rampSeries.Clear();
            approachSeries.Clear();
            trajectorySeries.Clear();
            trajectorySequences.Clear();

            recording = new SimulationRecording
            {
                LaneTimeSeries = new List<LaneTimeSeriesRecord>(),
                CorridorTimeSeries = new List<CorridorTimeSeriesRecord>(),
                RampTimeSeries = new List<RampTimeSeriesRecord>(),
                ApproachEdgeTimeSeries = new List<ApproachEdgeTimeSeriesRecord>(),
                AircraftTrajectories = new List<AircraftTrajectoryRecord>(),
                Events = new List<SimulationEvent>()
            };

            Debug.Log($"[SimulationRecorder] Recording started. Interval={recordInterval}s");
        }

        void Update()
        {
            if (!isRecording || sim == null) return;
            elapsedTime += Time.deltaTime;
            frameCount++;

            if (Time.time >= nextRecordTime)
            {
                nextRecordTime = Time.time + recordInterval;
                RecordSnapshot();
                recordFrameCount++;
            }
        }

        void RecordSnapshot()
        {
            float t = elapsedTime;

            foreach (var laneKv in sim.Lanes)
            {
                Lane lane = laneKv.Value;
                if (lane == null) continue;
                if (!laneSeries.TryGetValue(lane.LaneId, out LaneTimeSeriesRecord series))
                {
                    series = new LaneTimeSeriesRecord
                    {
                        LaneId = lane.LaneId,
                        EdgeId = sim.LaneToEdgeMap.ContainsKey(lane.LaneId) ? sim.LaneToEdgeMap[lane.LaneId] : "",
                        Length = lane.Length,
                        Snapshots = new List<LaneSnapshot>()
                    };
                    laneSeries[lane.LaneId] = series;
                    recording.LaneTimeSeries.Add(series);
                }
                if (!sim.LaneStates.TryGetValue(lane.LaneId, out LaneState state)) state = null;
                var ids = new List<string>();
                var progresses = new List<float>();
                if (state != null)
                {
                    foreach (var pair in state.AircraftsInLane)
                    {
                        ids.Add(pair.Key);
                        progresses.Add(Mathf.Clamp01(pair.Value));
                    }
                }
                series.Snapshots.Add(new LaneSnapshot
                {
                    Time = t,
                    AircraftCount = ids.Count,
                    SignalCheck = state != null && state.SignalCheck,
                    CapabilityCheck = state != null && state.CapabilityCheck,
                    AircraftIds = ids,
                    AircraftProgresses = progresses
                });
            }

            foreach (var rampKv in sim.Ramps)
            {
                Ramp ramp = rampKv.Value;
                if (ramp == null) continue;
                if (!rampSeries.TryGetValue(ramp.RampId, out RampTimeSeriesRecord series))
                {
                    series = new RampTimeSeriesRecord
                        { RampId = ramp.RampId, Snapshots = new List<RampSnapshot>() };
                    rampSeries[ramp.RampId] = series;
                    recording.RampTimeSeries.Add(series);
                }
                var ids = new List<string>();
                if (sim.RampStatesDict.TryGetValue(ramp.RampId, out RampState rampState))
                    foreach (var window in rampState.OccupiedTimeWindows)
                        if (Time.time >= window.StartTime && Time.time <= window.EndTime &&
                            !ids.Contains(window.OccupiedAircraftId)) ids.Add(window.OccupiedAircraftId);
                series.Snapshots.Add(new RampSnapshot { Time = t, AircraftCount = ids.Count, AircraftIds = ids });
            }

            foreach (var edgeKv in sim.ApproachEdges)
            {
                ApproachEdge edge = edgeKv.Value;
                if (edge == null) continue;
                if (!approachSeries.TryGetValue(edge.EdgeId, out ApproachEdgeTimeSeriesRecord series))
                {
                    series = new ApproachEdgeTimeSeriesRecord
                        { EdgeId = edge.EdgeId, Snapshots = new List<ApproachEdgeSnapshot>() };
                    approachSeries[edge.EdgeId] = series;
                    recording.ApproachEdgeTimeSeries.Add(series);
                }
                var ids = new List<string>();
                if (sim.ApproachEdgeStates.TryGetValue(edge.EdgeId, out ApproachEdgeState edgeState))
                    foreach (var window in edgeState.OccupiedTimeWindows)
                        if (Time.time >= window.StartTime && Time.time <= window.EndTime &&
                            !ids.Contains(window.OccupiedAircraftId)) ids.Add(window.OccupiedAircraftId);
                series.Snapshots.Add(new ApproachEdgeSnapshot
                    { Time = t, AircraftCount = ids.Count, AircraftIds = ids });
            }

            foreach (var corridorKv in sim.CorridorStatesDict)
            {
                CorridorState state = corridorKv.Value;
                if (state == null) continue;
                if (!corridorSeries.TryGetValue(state.CorridorId, out CorridorTimeSeriesRecord series))
                {
                    series = new CorridorTimeSeriesRecord
                    {
                        CorridorId = state.CorridorId,
                        VertiportId = sim.CorridorToVertiportMap.ContainsKey(state.CorridorId)
                            ? sim.CorridorToVertiportMap[state.CorridorId] : "",
                        Snapshots = new List<CorridorSnapshot>()
                    };
                    corridorSeries[state.CorridorId] = series;
                    recording.CorridorTimeSeries.Add(series);
                }
                var ids = new List<string>(state.AircraftsInCorridor.Keys);
                series.Snapshots.Add(new CorridorSnapshot
                    { Time = t, AircraftCount = ids.Count, AircraftIds = ids });
            }

            foreach (var aircraftKv in sim.AircraftObjects)
            {
                GameObject aircraft = aircraftKv.Value;
                if (aircraft == null) continue;
                AircraftAgent agent = aircraft.GetComponent<AircraftAgent>();
                string aircraftId = agent?.aircraftState?.AircraftId;
                if (string.IsNullOrWhiteSpace(aircraftId)) aircraftId = aircraftKv.Key;
                // WAITING is the parked interval between two random-continuous missions.
                // Starting the next series there would attach the previous mission's
                // metadata and waiting points to the next completed trajectory.
                if (!IsTrackableMissionPhase(agent?.aircraftState?.MissionPhase))
                    continue;
                if (!trajectorySeries.TryGetValue(aircraftId, out AircraftTrajectoryRecord trajectory))
                {
                    trajectory = CreateTrajectoryRecord(aircraftId, agent?.aircraftState);
                    trajectorySeries[aircraftId] = trajectory;
                    recording.AircraftTrajectories.Add(trajectory);
                }
                trajectory.Positions.Add(aircraft.transform.position);
                trajectory.Times.Add(t);
            }
        }

        private static bool IsTrackableMissionPhase(string phase)
        {
            return string.Equals(phase, "START", System.StringComparison.Ordinal) ||
                   string.Equals(phase, "TAKEOFF", System.StringComparison.Ordinal) ||
                   string.Equals(phase, "CRUISE", System.StringComparison.Ordinal) ||
                   string.Equals(phase, "LANDING", System.StringComparison.Ordinal);
        }

        private AircraftTrajectoryRecord CreateTrajectoryRecord(string aircraftId, AircraftState state)
        {
            int sequence = trajectorySequences.TryGetValue(aircraftId, out int current)
                ? current + 1
                : 1;
            trajectorySequences[aircraftId] = sequence;
            return new AircraftTrajectoryRecord
            {
                AircraftId = aircraftId,
                TrackId = $"{aircraftId}#{sequence:D3}",
                MissionCompleted = false,
                CompletionState = null,
                OriginVertiport = state?.Mission?.OriginVertiport,
                DestinationVertiport = state?.Mission?.DestinationVertiport,
                Positions = new List<Vector3>(),
                Times = new List<float>()
            };
        }

        private void HandleMissionCompleted(AircraftState state, Vector3 finalPosition)
        {
            if (!isRecording || state == null) return;
            string aircraftId = state.AircraftId;
            if (string.IsNullOrWhiteSpace(aircraftId)) return;
            if (!trajectorySeries.TryGetValue(aircraftId, out AircraftTrajectoryRecord trajectory))
            {
                trajectory = CreateTrajectoryRecord(aircraftId, state);
                trajectorySeries[aircraftId] = trajectory;
                recording.AircraftTrajectories.Add(trajectory);
            }

            if (trajectory.Positions.Count == 0 ||
                Vector3.Distance(trajectory.Positions[trajectory.Positions.Count - 1], finalPosition) > 0.01f)
            {
                trajectory.Positions.Add(finalPosition);
                trajectory.Times.Add(elapsedTime);
            }
            trajectory.MissionCompleted = true;
            trajectory.CompletionState = string.IsNullOrWhiteSpace(state.State) ? "SUCCESS" : state.State;
            trajectory.CompletedTime = elapsedTime;

            // Random-continuous mode may reuse one aircraft identifier for another
            // mission. Removing only the active series starts a new, separately
            // identifiable track when that next mission begins.
            trajectorySeries.Remove(aircraftId);
        }

        public void RecordEvent(string eventType, string aircraftId, string description, Vector3 position)
        {
            if (!isRecording) return;
            recording.Events.Add(new SimulationEvent
            {
                Time = elapsedTime,
                EventType = eventType,
                AircraftId = aircraftId,
                Description = description,
                Position = new SerializableVector3 { x = position.x, y = position.y, z = position.z }
            });
        }

        public void StopRecording()
        {
            if (!isRecording)
            {
                if (sim != null) sim.MissionCompleted -= HandleMissionCompleted;
                return;
            }
            isRecording = false;
            if (sim != null) sim.MissionCompleted -= HandleMissionCompleted;

            float totalTime = elapsedTime;

            // Compute metrics
            int successCount = sim != null ? sim.cumulSuccessCount : 0;
            int failCount = sim != null ? sim.cumulFailCount : 0;
            float totalFlightTime = sim != null ? sim.cumulTotalFlightTime : 0f;
            float totalFlightLength = sim != null ? sim.cumulTotalFlightLength : 0f;
            float totalDelay = sim != null ? sim.cumulTotalWaitTime : 0f;
            // Count total distinct collision events (from CollisionMonitor or similar)
            int eventCollisionCount = 0;
            foreach (var evt in recording.Events)
                if (evt.EventType == "Collision") eventCollisionCount++;

            int totalFlights = successCount + failCount;
            float avgFlightTime = totalFlights > 0 ? totalFlightTime / totalFlights : 0f;
            float avgFlightLength = totalFlights > 0 ? totalFlightLength / totalFlights : 0f;
            float failureRate = totalFlights > 0 ? (float)failCount / totalFlights : 0f;
            float throughput = totalTime > 0 ? (totalFlights * avgFlightLength) / totalTime : 0f;
            float avgDelay = successCount > 0 ? totalDelay / successCount : 0f;

            recording.Stats = new SimulationRecordingStats
            {
                TotalSimTime = totalTime,
                TotalFrames = frameCount,
                RecordedFrames = recordFrameCount,
                RecordInterval = recordInterval,
                AircraftSnapshots = recording.AircraftTrajectories.Sum(item => item?.Positions?.Count ?? 0),
                LaneSnapshots = recording.LaneTimeSeries.Sum(item => item?.Snapshots?.Count ?? 0),
                CorridorSnapshots = recording.CorridorTimeSeries.Sum(item => item?.Snapshots?.Count ?? 0),
                RampSnapshots = recording.RampTimeSeries.Sum(item => item?.Snapshots?.Count ?? 0),
                ApproachEdgeSnapshots = recording.ApproachEdgeTimeSeries.Sum(item => item?.Snapshots?.Count ?? 0),
                EventCount = recording.Events.Count
            };

            recording.Metrics = new SimulationMetricsData
            {
                ScheduleDelay = totalTime,
                SystemCapacity = totalFlights,
                AverageFlightLength = avgFlightLength,
                AverageFlightTime = avgFlightTime,
                TakeoffDelayTime = 0f,
                SuccessfulFlight = successCount,
                FailedFlight = failCount,
                FailureRate = failureRate,
                Throughput = throughput,
                AverageDelay = avgDelay,
                AverageSpeed = avgFlightTime > 0 ? avgFlightLength / avgFlightTime : 0f,
                TotalCollisionEvents = sim != null ? sim.TotalCollisionEvents : eventCollisionCount,
                TotalBuildingCollisions = 0,
                AverageLaneOccupancy = 0f,
                PeakLaneOccupancy = 0f,
                AverageCorridorOccupancy = 0f,
                LaneUtilizationRate = 0f,
                CorridorUtilizationRate = 0f,
                TotalFlights = totalFlights
            };

            // Write to file
            string outDir = sim != null ? sim.GetOutputDirectory() : SarRuntimePaths.OutputRoot;
            System.IO.Directory.CreateDirectory(outDir);
            string path = Path.Combine(outDir, "SimulationRecording.json");
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Converters = new List<JsonConverter> { new Vector3Converter() }
            };
            string temporaryPath = path + ".tmp";
            var serializer = JsonSerializer.Create(settings);
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var textWriter = new StreamWriter(stream))
            using (var jsonWriter = new JsonTextWriter(textWriter) { Formatting = Formatting.Indented })
                serializer.Serialize(jsonWriter, recording);
            File.Copy(temporaryPath, path, true);
            File.Delete(temporaryPath);
            LastOutputPath = path;

            Debug.Log($"[SimulationRecorder] Recording saved: {path}\n" +
                      $"  Time={totalTime:F1}s, Frames={frameCount}, SnapshotFrames={recordFrameCount}\n" +
                      $"  Success={successCount}, Fail={failCount}, Collisions={eventCollisionCount}");
        }

        void OnDestroy()
        {
            StopRecording();
        }
    }

    // ==================== Data Structures ====================

    [System.Serializable]
    public class SimulationRecording
    {
        public SimulationRecordingStats Stats;
        public SimulationMetricsData Metrics;
        public List<AircraftTrajectoryRecord> AircraftTrajectories;
        public List<LaneTimeSeriesRecord> LaneTimeSeries;
        public List<CorridorTimeSeriesRecord> CorridorTimeSeries;
        public List<RampTimeSeriesRecord> RampTimeSeries;
        public List<ApproachEdgeTimeSeriesRecord> ApproachEdgeTimeSeries;
        public List<SimulationEvent> Events;
    }

    [System.Serializable]
    public class SimulationRecordingStats
    {
        public float TotalSimTime;
        public int TotalFrames;
        public int RecordedFrames;
        public float RecordInterval;
        public int AircraftSnapshots;
        public int LaneSnapshots;
        public int CorridorSnapshots;
        public int RampSnapshots;
        public int ApproachEdgeSnapshots;
        public int EventCount;
    }

    [System.Serializable]
    public class SimulationMetricsData
    {
        public float ScheduleDelay;
        public int SystemCapacity;
        public float AverageFlightLength;
        public float AverageFlightTime;
        public float TakeoffDelayTime;
        public int SuccessfulFlight;
        public int FailedFlight;
        public float FailureRate;
        public float Throughput;
        public float AverageDelay;
        public float AverageSpeed;
        public int TotalCollisionEvents;
        public int TotalBuildingCollisions;
        public float AverageLaneOccupancy;
        public float PeakLaneOccupancy;
        public float AverageCorridorOccupancy;
        public float LaneUtilizationRate;
        public float CorridorUtilizationRate;
        public Dictionary<string, float> CustomMetrics;
        public int TotalFlights;
    }

    [System.Serializable]
    public class AircraftTrajectoryRecord
    {
        public string AircraftId;
        public string TrackId;
        // Nullable for backward compatibility: legacy recordings did not persist
        // completion state and are inferred from their Mission SUCCESS events.
        public bool? MissionCompleted;
        public string CompletionState;
        public float? CompletedTime;
        public string OriginVertiport;
        public string DestinationVertiport;
        public List<Vector3> Positions;
        public List<float> Times;
    }

    [System.Serializable]
    public class LaneTimeSeriesRecord
    {
        public string LaneId;
        public string EdgeId;
        public float Length;
        public List<LaneSnapshot> Snapshots;
    }

    [System.Serializable]
    public class LaneSnapshot
    {
        public float Time;
        public int AircraftCount;
        public bool SignalCheck;
        public bool CapabilityCheck;
        public List<string> AircraftIds;
        public List<float> AircraftProgresses;
    }

    [System.Serializable]
    public class CorridorTimeSeriesRecord
    {
        public string CorridorId;
        public string VertiportId;
        public List<CorridorSnapshot> Snapshots;
    }

    [System.Serializable]
    public class CorridorSnapshot
    {
        public float Time;
        public int AircraftCount;
        public List<string> AircraftIds;
    }

    [System.Serializable]
    public class RampTimeSeriesRecord
    {
        public string RampId;
        public List<RampSnapshot> Snapshots;
    }

    [System.Serializable]
    public class RampSnapshot
    {
        public float Time;
        public int AircraftCount;
        public List<string> AircraftIds;
    }

    [System.Serializable]
    public class ApproachEdgeTimeSeriesRecord
    {
        public string EdgeId;
        public List<ApproachEdgeSnapshot> Snapshots;
    }

    [System.Serializable]
    public class ApproachEdgeSnapshot
    {
        public float Time;
        public int AircraftCount;
        public List<string> AircraftIds;
    }

    [System.Serializable]
    public class SimulationEvent
    {
        public float Time;
        public string EventType;
        public string AircraftId;
        public string Description;
        public SerializableVector3 Position;
    }

    [System.Serializable]
    public class SerializableVector3
    {
        public float x, y, z;
    }
}
