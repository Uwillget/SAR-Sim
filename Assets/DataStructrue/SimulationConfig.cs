using UnityEngine;

public enum MissionGenerationMode
{
    JSON,
    RandomContinuous
}

[CreateAssetMenu(fileName = "DefaultSimulationConfig", menuName = "SAR-SIM/Simulation Config")]
public class SimulationConfig : ScriptableObject
{
    [Header("File Names")]
    [Tooltip("Missions JSON file name (stored in Assets/DataBase/)")]
    public string missionsFileName = "Missions.json";

    [Tooltip("Results JSON file name (stored in Assets/DataBase/)")]
    public string resultsFileName = "SimulationResults.json";

    [Header("Aircraft Parameters")]
    [Tooltip("Maximum flight speed (units/sec)")]
    public float maxSpeed = 20f;

    [Tooltip("Turning angular speed (degrees/sec)")]
    public float rotationSpeed = 20f;

    [Tooltip("Base acceleration (units/sec^2)")]
    public float acceleration = 20f;

    [Tooltip("Maximum deceleration during braking (units/sec^2). Reached at 0.5*safeInterval distance.")]
    public float maxDeceleration = 40f;

    [Tooltip("Follow-the-leader safety distance: aircraft decelerates when closer than this to predecessor. Collision detection uses the aircraft prefab's actual BoxCollider size.")]
    public float safeInterval = 10f;

    [Tooltip("Longitudinal distance (meters) to advance when changing lanes. Shorter = faster lane change.")]
    public float laneChangeDistance = 2f;

    [Tooltip("Required free distance ahead when merging, as a multiplier of safeInterval.")]
    public float laneChangeFrontGapFactor = 1.0f;

    [Tooltip("Required free distance behind when merging, as a multiplier of safeInterval.")]
    public float laneChangeRearGapFactor = 1.5f;

    [Tooltip("Seconds waiting for a lane change before trying an alternate lane route.")]
    public float laneChangeRerouteTimeout = 15f;

    [Tooltip("Seconds waiting for a lane change before the aircraft is removed as a deadlock. Must exceed reroute timeout.")]
    public float laneChangeRemovalTimeout = 120f;

    [Header("Vertiport Path Parameters")]
    [Tooltip("Time buffer (seconds) around resource occupancy windows. Node: [arrival-buffer, arrival+dwell+buffer], Edge: [entry-buffer, exit+buffer]")]
    public float resourceBuffer = 2f;

    [Tooltip("Maximum seconds an aircraft can wait for a single resource before that path branch is pruned. Large values allow longer waits; 0 = no waiting allowed.")]
    public float maxWaitPerResource = 60f;

    [Tooltip("Maximum seconds an aircraft can wait in the exit corridor for landing HS+approach to become free. If exceeded, aircraft is deregistered to free space.")]
    public float maxCorridorWaitTime = 90f;

    [Tooltip("Maximum seconds an aircraft can wait at a helipad for takeoff path planning to succeed. If exceeded, aircraft is deregistered and helipad freed.")]
    public float maxHelipadWaitTime = 120f;

    [Tooltip("Minimum time (seconds) an aircraft is considered to occupy a node/vertex for dwell-time occupancy checks. Must be large enough to cover typical edge-wait durations (otherwise two aircraft may book overlapping node windows).")]
    public float nodeDwellTime = 3.0f;

    [Header("Traffic Signal Parameters")]
    [Tooltip("Green wave propagation speed (units/sec), used for offset calculation between adjacent intersections")]
    public float greenWaveSpeed = 15f;

    [Tooltip("Duration of each signal phase (seconds). Total cycle = phases × greenSignalTime")]
    public float greenSignalTime = 10f;

    [Tooltip("All-red clearance interval inserted after every green phase (seconds).")]
    public float signalClearanceTime = 2f;

    [Tooltip("Distance before the lane endpoint used as the signal stop line (meters).")]
    public float signalStopLineDistance = 10f;

    [Tooltip("Allow dedicated right-turn lanes to ignore red. Disabled by default because permissive turns need explicit conflict-area yield rules.")]
    public bool permissiveRightTurn = false;

    [Header("Mission Generation")]
    [Tooltip("Mission generation mode: JSON file or random continuous")]
    public MissionGenerationMode missionMode = MissionGenerationMode.JSON;

    [Tooltip("Maximum concurrent aircraft in RandomContinuous mode (limited by total HP count)")]
    public int maxConcurrentAircraft = 10;

    [Tooltip("Seconds a RandomContinuous aircraft remains parked after landing before selecting a new destination.")]
    public float randomTurnaroundTime = 10f;

    [Header("Visualization")]
    [Tooltip("Show lane state gizmos (signal lights, occupancy)")]
    public bool showLaneStateGizmos = true;

    [Tooltip("Size of signal gizmo spheres")]
    public float signalGizmoSize = 1.0f;

    [Tooltip("Height offset for signal gizmos above lanes")]
    public float signalHeightOffset = 3.0f;

    [Header("Information Uncertainty")]
    [Tooltip("Signal propagation delay (seconds). Aircraft read lane/corridor occupancy from this many seconds ago. 0 = instant.")]
    public float signalPropagationDelay = 0f;

    [Tooltip("Positioning error standard deviation (meters). When reading other aircraft positions from lanes/corridors, a N(0,σ) noise is added. 0 = perfect positioning.")]
    public float positioningErrorStdDev = 0f;

}
