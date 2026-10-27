using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#region 1. Lane State
public class LaneState
{
    public string LaneId;
    public float Length;
    public Dictionary<string, float> AircraftsInLane = new Dictionary<string, float>();
    public bool SignalCheck = false;
    public bool CapabilityCheck = false;
    public bool EntryPermission;
    public bool ExitPermission;
}
#endregion

#region 2. Vertiport State
public class HoldingStructureState
{
    public string HoldingStructureId;
    public List<OccupiedTimeWindow> OccupiedTimeWindows = new List<OccupiedTimeWindow>();
}
public class HelipadState
{
    public string HelipadId;
    public List<OccupiedTimeWindow> OccupiedTimeWindows = new List<OccupiedTimeWindow>();
}
public class VertexState
{
    public string VertexId;
    public List<OccupiedTimeWindow> OccupiedTimeWindows = new List<OccupiedTimeWindow>();
}
public class RampState
{
    public string RampId;
    public List<OccupiedTimeWindow> OccupiedTimeWindows = new List<OccupiedTimeWindow>();
}
public class ApproachNodeState
{
    public string NodeId;
    public List<OccupiedTimeWindow> OccupiedTimeWindows = new List<OccupiedTimeWindow>();
}
public class ApproachEdgeState
{
    public string EdgeId;
    public List<OccupiedTimeWindow> OccupiedTimeWindows = new List<OccupiedTimeWindow>();
}
public class CorridorState
{
    public string CorridorId;
    public List<OccupiedTimeWindow> OccupiedTimeWindows = new List<OccupiedTimeWindow>();
    public Dictionary<string, float> AircraftsInCorridor = new Dictionary<string, float>();
}
public class OccupiedTimeWindow
{
    public string OccupiedAircraftId;
    public float StartTime;
    public float EndTime;
    public bool IsStandingReservation;
}
#endregion

#region 2.5 Occupancy Log (Export)
/// <summary>
/// Single continuous time window [StartTime, EndTime].
/// </summary>
[System.Serializable]
public class TimeWindowEntry
{
    public float StartTime;
    public float EndTime;
}

/// <summary>
/// All time windows that one aircraft occupies on a single resource.
/// One aircraft may book a resource multiple times (approach node + cross-type from HS vertex, etc.).
/// </summary>
[System.Serializable]
public class AircraftOccupancy
{
    public string AircraftId;
    public List<TimeWindowEntry> TimeWindows = new List<TimeWindowEntry>();
}

/// <summary>
/// Occupancy log for a single resource (node, edge, ramp, vertex, corridor, helipad).
/// Contains all aircraft that have booked this resource.
/// </summary>
[System.Serializable]
public class ResourceOccupancyLog
{
    public string ResourceId;
    public List<AircraftOccupancy> Occupancies = new List<AircraftOccupancy>();
}

/// <summary>
/// Top-level wrapper for exporting occupancy logs to JSON.
/// </summary>
[System.Serializable]
public class OccupancyExport
{
    public List<ResourceOccupancyLog> Resources = new List<ResourceOccupancyLog>();
}
#endregion

#region 3. Aircraft State
public class AircraftState
{
    public string AircraftId;
    public Flight Mission;
    public string MissionPhase;
    public string State; // "ACTIVE" / "SUCCESS" / "FAIL"

    public Vector3 Position;
    public float FlightTime;
    public float FlightLength;

    public string CurrentLaneId;

    // Path planning data
    public KeyNodes MissionKeyNodes;
    public List<Edge> EdgeSequence;
    public List<Lane> LaneSequence;
    public List<RampPoint> RampTrajectory;     // Takeoff path: Helipad → ApproachNet → HS → Corridor entry
    public List<RampPoint> LandingTrajectory;   // Landing path: Corridor exit → HS → ApproachNet → Helipad

    // Vertiport resource references
    public string OriginHelipadId;
    public string DestinationHelipadId;
    public string EntryCorridorId;
    public string ExitCorridorId;

    // Deferred landing planning
    public bool LandingPlanTriggered = false;
    public float LandingRetryStartTime = -1f;   // Time when first landing attempt was made (used for corridor-wait timeout)
    public float LastLandingRetryTime = -1f;    // Time of last landing retry attempt
    public string DestinationHSId;
    public string DestinationApproachAnchorNodeId;

    public class KeyNodes
    {
        public Vector3 OriginHelipad;
        public Vector3 EntryCorridorPoint;
        public Vector3 ExitCorridorPoint;
        public Vector3 DestinationHelipad;
    }
}

public class RampPoint
{
    public Vector3 Point;
    public float WaitTime;
    public float ReachTime;
    public string CheckpointType; // null, "EntryMerge", "LandingTrigger"
}
#endregion

#region 4. Mission Task
public class MissionTask
{
    public string TaskId;
    public string RouteId;
    public string OriginVertiportId;
    public string DestinationVertiportId;
    public string OriginHelipadId;      // null = auto-select
    public string DestinationHelipadId; // null = auto-select
    public List<string> RouteLaneIds = new List<string>(); // empty = automatic shortest route
    public float DepartureTime;
    public int Priority;
}

public class MissionSaveData
{
    public int SchemaVersion = 2;
    public List<MissionTask> Missions = new List<MissionTask>();
}
#endregion

#region 5. Simulation Metrics
public class SimulationMetrics
{
    // Basic
    public double ScheduleDelay;
    public int SystemCapacity;
    public float AverageFlightLength;
    public float AverageFlightTime;
    public float TakeoffDelayTime;
    public int SuccessfulFlight;
    public int FailedFlight;
    public double FailureRate;

    // Chapter 6 Vertiport Configuration metrics
    public float SimulationDuration;          // total simulation time (seconds)
    public double Throughput;                 // successful missions per second
    public float AvgMissionDuration;          // average mission time (start→end)
    public float AvgResourceWaitTime;         // average waiting before reserved resources (Dijkstra waits)
    public float ReservationUtilization;      // 0-1 ratio of occupied vs total resource-time
    public int TotalMissionsAttempted;        // total missions created
    public float TotalCumulativeWait;         // sum of all WaitTime in all trajectories
    public int TotalResourceBookings;         // total occupancy windows booked
    public float TotalResourceTime;           // sum of (endTime - startTime) of all bookings
}
#endregion
