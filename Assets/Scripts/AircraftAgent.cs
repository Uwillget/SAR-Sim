using System.Collections.Generic;
using RoadNetworkEditor.Networking;
using UnityEngine;

namespace RoadNetworkEditor
{
    public class AircraftAgent : MonoBehaviour
    {
        public SimulationManager dispatchCenter;
        public FlightController controller;
        public AircraftState aircraftState;

        public int currentTakeoffpointIndex = 0;
        public int currentLandingpointIndex = 0;
        public int currentLaneSequenceIndex = 0;
        public bool isWaitingForLaneChange = false;
        public bool isWaitingForEntry = false;
        public bool isHoldingForLandingPlan = false;
        public bool isWaitingForVertiportResource = false;
        public string pendingTargetLaneId;
        public string activeLaneChangeTargetLaneId;
        public float laneChangeWaitStartedAt = -1f;
        public float lastLaneChangeRerouteAt = -1f;
        public string currentCorridorId;
        public bool hasMissionCompleted = false;

        private int cachedTraversalLaneIndex = -1;
        private Lane cachedTraversalLane;
        private List<Vector3> cachedTraversal;

        void Start()
        {
            if (dispatchCenter == null)
                dispatchCenter = FindObjectOfType<SimulationManager>();
        }

        void Update()
        {
            if (aircraftState == null) return;

            if (!string.IsNullOrEmpty(activeLaneChangeTargetLaneId) &&
                aircraftState.CurrentLaneId == activeLaneChangeTargetLaneId)
            {
                activeLaneChangeTargetLaneId = null;
            }

            aircraftState.FlightTime += Time.deltaTime;
            aircraftState.FlightLength += Vector3.Distance(transform.position, aircraftState.Position);
            aircraftState.Position = transform.position;

            if (aircraftState.State == "FAIL")
            {
                controller.SetTargetPosition(transform.position);
                aircraftState.MissionPhase = "END";
                return;
            }

            // WAITING phase: aircraft stays idle until SimulationManager assigns a new mission
            if (aircraftState.MissionPhase == "WAITING")
                return;

            // Corridor following: adjust speed based on corridor occupancy
            if (!string.IsNullOrEmpty(currentCorridorId) &&
                (aircraftState.MissionPhase == "TAKEOFF" || aircraftState.MissionPhase == "LANDING"))
            {
                dispatchCenter.ApplyCorridorFollowing(aircraftState.AircraftId, currentCorridorId, controller);
            }

            // Phase transition trigger: arrived at target or START phase
            if (controller.HasReachedAndWaited() || aircraftState.MissionPhase == "START")
            {
                if (!isWaitingForLaneChange)
                    HandlePhaseTransition();
            }

            // Lane change waiting
            if (isWaitingForLaneChange)
            {
                Vector3 changingPosition = dispatchCenter.GetChangingPosition(
                    pendingTargetLaneId, aircraftState.CurrentLaneId, aircraftState.AircraftId);
                if (changingPosition != Vector3.zero)
                {
                    Debug.Log($"[Aircraft {aircraftState.AircraftId}] Lane change approved.");
                    activeLaneChangeTargetLaneId = pendingTargetLaneId;
                    currentLaneSequenceIndex++;
                    controller.SetTargetPosition(changingPosition);
                    isWaitingForLaneChange = false;
                    pendingTargetLaneId = null;
                    laneChangeWaitStartedAt = -1f;
                    lastLaneChangeRerouteAt = -1f;
                }
                else
                {
                    if (controller.moveSpeed > 0.5f)
                        controller.moveSpeed -= controller.acceleration * Time.deltaTime;
                    else
                        controller.moveSpeed = 0f;

                    if (laneChangeWaitStartedAt < 0f)
                        laneChangeWaitStartedAt = Time.time;
                    float waitDuration = Time.time - laneChangeWaitStartedAt;
                    float rerouteTimeout = dispatchCenter.config != null
                        ? dispatchCenter.config.laneChangeRerouteTimeout : 15f;
                    float removalTimeout = dispatchCenter.config != null
                        ? Mathf.Max(dispatchCenter.config.laneChangeRemovalTimeout, rerouteTimeout)
                        : 120f;

                    if (waitDuration >= rerouteTimeout &&
                        (lastLaneChangeRerouteAt < 0f || Time.time - lastLaneChangeRerouteAt >= 2f))
                    {
                        lastLaneChangeRerouteAt = Time.time;
                        if (dispatchCenter.TryRerouteCruise(this, pendingTargetLaneId))
                        {
                            InvalidateRouteTraversalCache();
                            Debug.LogWarning($"[Aircraft {aircraftState.AircraftId}] " +
                                             $"Lane-change wait resolved by rerouting after {waitDuration:F1}s.");
                            isWaitingForLaneChange = false;
                            pendingTargetLaneId = null;
                            laneChangeWaitStartedAt = -1f;
                            lastLaneChangeRerouteAt = -1f;
                            controller.moveSpeed = Mathf.Max(controller.moveSpeed, controller.acceleration * Time.deltaTime);
                            return;
                        }
                    }

                    if (waitDuration >= removalTimeout)
                    {
                        dispatchCenter.RemoveDeadlockedAircraft(this, waitDuration);
                        isWaitingForLaneChange = false;
                        pendingTargetLaneId = null;
                        return;
                    }
                }
            }
        }

        public void Initialize(AircraftState missionState)
        {
            aircraftState = missionState;
            InvalidateRouteTraversalCache();
            controller = GetComponent<FlightController>();
            if (controller == null)
                controller = gameObject.AddComponent<FlightController>();

            if (dispatchCenter != null && dispatchCenter.config != null)
            {
                var cfg = dispatchCenter.config;
                controller.maxSpeed = cfg.maxSpeed;
                controller.rotationSpeed = cfg.rotationSpeed;
                controller.acceleration = cfg.acceleration;
                controller.safeInterval = cfg.safeInterval;
            }
        }

        public void HandlePhaseTransition()
        {
            switch (aircraftState.MissionPhase)
            {
                case "START":
                    Debug.Log($"[Aircraft {aircraftState.AircraftId}] START -> TAKEOFF.");
                    aircraftState.MissionPhase = "TAKEOFF";
                    SimulationRecorder.Instance?.RecordEvent("PhaseChange", aircraftState.AircraftId, "START -> TAKEOFF", transform.position);
                    currentTakeoffpointIndex = 0;
                    currentCorridorId = null;
                    if (aircraftState.RampTrajectory != null && aircraftState.RampTrajectory.Count > 0)
                    {
                        var pt = aircraftState.RampTrajectory[0];
                        Debug.Log($"[Aircraft {aircraftState.AircraftId}] Waypoint 0/{aircraftState.RampTrajectory.Count}: pos={pt.Point} wait={pt.WaitTime:F2}s reach={pt.ReachTime:F2}");
                        controller.SetScheduledTargetPosition(pt.Point, pt.ReachTime, pt.WaitTime);
                    }
                    else
                    {
                        Debug.LogWarning($"[Aircraft {aircraftState.AircraftId}] No RampTrajectory, skipping to END.");
                        aircraftState.MissionPhase = "END";
                        aircraftState.State = "FAIL";
                        if (dispatchCenter != null) dispatchCenter.cumulFailCount++;
                    }
                    break;

                case "TAKEOFF":
                    // Only advance when aircraft has reached current target and completed its wait.
                    if (controller.HasReachedAndWaited())
                        currentTakeoffpointIndex++;
                    if (aircraftState.RampTrajectory == null ||
                        currentTakeoffpointIndex >= aircraftState.RampTrajectory.Count)
                    {
                        Debug.Log($"[Aircraft {aircraftState.AircraftId}] TAKEOFF -> CRUISE.");
                        aircraftState.MissionPhase = "CRUISE";
                        SimulationRecorder.Instance?.RecordEvent("PhaseChange", aircraftState.AircraftId, "TAKEOFF -> CRUISE", transform.position);
                        currentLaneSequenceIndex = 0;
                        currentCorridorId = null;
                        if (aircraftState.LaneSequence != null && aircraftState.LaneSequence.Count > 0)
                        {
                            aircraftState.CurrentLaneId = aircraftState.LaneSequence[0].LaneId;
                            controller.SetTargetPosition(aircraftState.MissionKeyNodes.EntryCorridorPoint);
                        }
                        else
                        {
                            Debug.LogWarning($"[Aircraft {aircraftState.AircraftId}] No LaneSequence, skipping to LANDING.");
                            aircraftState.MissionPhase = "LANDING";
                            currentLandingpointIndex = 0;
                        }
                        break;
                    }

                    var curRP = aircraftState.RampTrajectory[currentTakeoffpointIndex];

                    if (!TryAcquireVertiportWaypoint(curRP, ref currentTakeoffpointIndex))
                        break;

                    // Entry merge checkpoint: check lane proximity before entering corridor.
                    // Corridor occupancy is handled by time-window booking (no real-time gate to avoid deadlock).
                    if (curRP.CheckpointType == "EntryMerge" &&
                        !string.IsNullOrEmpty(aircraftState.EntryCorridorId))
                    {
                        bool laneConflict = dispatchCenter.CheckEntryConflict(aircraftState.EntryCorridorId);

                        if (laneConflict)
                        {
                            Debug.LogWarning($"[Aircraft {aircraftState.AircraftId}] Entry blocked, waiting.");
                            controller.SetTargetPosition(curRP.Point, 1f);
                            currentTakeoffpointIndex--;
                            isWaitingForEntry = true;
                            break;
                        }
                        else if (isWaitingForEntry)
                        {
                            isWaitingForEntry = false;
                            currentTakeoffpointIndex++;
                            curRP = aircraftState.RampTrajectory[currentTakeoffpointIndex];
                        }
                    }

                    // Set currentCorridorId only AFTER conflict check passes
                    if (curRP.CheckpointType == "EntryMerge")
                    {
                        currentCorridorId = aircraftState.EntryCorridorId;
                    }

                    Debug.Log($"[Aircraft {aircraftState.AircraftId}] Waypoint {currentTakeoffpointIndex}/{aircraftState.RampTrajectory.Count}: pos={curRP.Point} wait={curRP.WaitTime:F2}s reach={curRP.ReachTime:F2} t={Time.time:F2}");
                    controller.SetScheduledTargetPosition(curRP.Point, curRP.ReachTime, curRP.WaitTime);
                    break;

                case "CRUISE":
                    if (aircraftState.LaneSequence == null ||
                        currentLaneSequenceIndex >= aircraftState.LaneSequence.Count)
                    {
                        // Keep ownership of the final lane until the first landing-
                        // corridor point is physically available. Clearing CurrentLaneId
                        // before this gate made a stopped aircraft invisible to lane
                        // following and allowed a queue to pile up at the exit point.
                        if (aircraftState.LandingTrajectory != null &&
                            aircraftState.LandingTrajectory.Count > 0 &&
                            !dispatchCenter.TryClaimVertiportWaypoint(
                                aircraftState.AircraftId,
                                aircraftState.LandingTrajectory[0].Point))
                        {
                            isWaitingForVertiportResource = true;
                            controller.SetTargetPosition(transform.position, 0.25f);
                            break;
                        }

                        Debug.Log($"[Aircraft {aircraftState.AircraftId}] CRUISE -> LANDING (exit corridor).");
                        aircraftState.MissionPhase = "LANDING";
                        SimulationRecorder.Instance?.RecordEvent("PhaseChange", aircraftState.AircraftId, "CRUISE -> LANDING", transform.position);
                        currentLandingpointIndex = 0;
                        aircraftState.CurrentLaneId = null;
                        currentCorridorId = aircraftState.ExitCorridorId;

                        if (aircraftState.LandingTrajectory != null && aircraftState.LandingTrajectory.Count > 0)
                        {
                            var firstPt = aircraftState.LandingTrajectory[0];
                            isWaitingForVertiportResource = false;
                            controller.SetScheduledTargetPosition(
                                firstPt.Point,
                                firstPt.ReachTime,
                                firstPt.WaitTime);
                        }
                        else
                        {
                            Debug.LogWarning($"[Aircraft {aircraftState.AircraftId}] No LandingTrajectory; " +
                                             "using the normal LANDING terminal transition.");
                            // Keep the phase in LANDING. On the next transition the shared
                            // terminal branch performs metrics, recorder notification,
                            // runtime-claim release, and hasMissionCompleted consistently.
                            currentLandingpointIndex = 0;
                            controller.SetTargetPosition(transform.position);
                        }
                        break;
                    }

                    aircraftState.CurrentLaneId = aircraftState.LaneSequence[currentLaneSequenceIndex].LaneId;

                    List<Vector3> traversalCenterline = GetRouteTraversal(currentLaneSequenceIndex);
                    if (traversalCenterline.Count == 0)
                    {
                        Debug.LogError($"[Aircraft {aircraftState.AircraftId}] Invalid route geometry at lane " +
                                       $"{aircraftState.CurrentLaneId}; a layer connector would require backtracking.");
                        aircraftState.State = "FAIL";
                        aircraftState.MissionPhase = "END";
                        controller.SetTargetPosition(transform.position);
                        dispatchCenter.cumulFailCount++;
                        break;
                    }

                    // If current lane has the exit corridor, go directly to exit point
                    if (CurrentLaneHasExitCorridor())
                    {
                        Debug.Log($"[Aircraft {aircraftState.AircraftId}] Exit corridor on lane {aircraftState.CurrentLaneId}, heading to exit.");
                        controller.SetTargetPosition(aircraftState.MissionKeyNodes.ExitCorridorPoint);
                        currentLaneSequenceIndex = aircraftState.LaneSequence.Count;
                        break;
                    }

                    // Arrived at the routed end of this lane? For a source lane followed
                    // by a layer connector this is the connector attachment point, not
                    // the physical lane's terminal node.
                    if (controller.targetPosition == traversalCenterline[traversalCenterline.Count - 1])
                    {
                        int nextLaneIndex = currentLaneSequenceIndex + 1;
                        if (nextLaneIndex < aircraftState.LaneSequence.Count)
                        {
                            Lane nextLane = aircraftState.LaneSequence[nextLaneIndex];
                            if (!dispatchCenter.TryReserveLaneEntry(
                                    aircraftState.CurrentLaneId,
                                    nextLane.LaneId,
                                    aircraftState.AircraftId))
                            {
                                controller.moveSpeed = 0f;
                                break;
                            }

                            currentLaneSequenceIndex = nextLaneIndex;
                            // Change ownership before traversing the inter-lane gap.
                            // This keeps the aircraft visible to downstream following
                            // and prevents a second aircraft entering in the same frame.
                            aircraftState.CurrentLaneId = nextLane.LaneId;
                            List<Vector3> nextTraversal = GetRouteTraversal(nextLaneIndex);
                            if (nextTraversal.Count == 0)
                            {
                                Debug.LogError($"[Aircraft {aircraftState.AircraftId}] Invalid route geometry at lane " +
                                               $"{nextLane.LaneId}; cannot enter without reversing.");
                                aircraftState.State = "FAIL";
                                aircraftState.MissionPhase = "END";
                                controller.SetTargetPosition(transform.position);
                                dispatchCenter.cumulFailCount++;
                                break;
                            }
                            controller.SetTargetPosition(nextTraversal[0]);
                        }
                        else
                        {
                            currentLaneSequenceIndex = nextLaneIndex;
                        }
                    }
                    else
                    {
                        if (currentLaneSequenceIndex == aircraftState.LaneSequence.Count - 1)
                        {
                            controller.SetTargetPosition(aircraftState.MissionKeyNodes.ExitCorridorPoint);
                            currentLaneSequenceIndex++;
                        }
                        else if (IsSameEdge(aircraftState.CurrentLaneId,
                            aircraftState.LaneSequence[currentLaneSequenceIndex + 1].LaneId))
                        {
                            isWaitingForLaneChange = true;
                            pendingTargetLaneId = aircraftState.LaneSequence[currentLaneSequenceIndex + 1].LaneId;
                            if (laneChangeWaitStartedAt < 0f)
                                laneChangeWaitStartedAt = Time.time;
                        }
                        else
                        {
                            // Follow centerline waypoints incrementally instead of
                            // jumping directly to the last point. This fixes the bug
                            // where after a left-turn + lane change, the aircraft would
                            // skip all intermediate centerline waypoints and fly diagonally
                            // across the lane to its exit.
                            Vector3 nextPt = GetNextCenterlinePoint(traversalCenterline, transform.position);
                            controller.SetTargetPosition(nextPt);
                        }
                    }
                    break;

                case "LANDING":
                    // Only advance when aircraft has reached current target and completed its wait,
                    // and not holding for landing plan retry.
                    if (currentLandingpointIndex < 0)
                    {
                        if (controller.HasReachedAndWaited() && !isHoldingForLandingPlan)
                            currentLandingpointIndex = 0;
                        else
                            break;
                    }
                    else if (controller.HasReachedAndWaited() && !isHoldingForLandingPlan)
                        currentLandingpointIndex++;
                    if (aircraftState.LandingTrajectory == null ||
                        currentLandingpointIndex >= aircraftState.LandingTrajectory.Count)
                    {
                        Debug.Log($"[Aircraft {aircraftState.AircraftId}] Landing complete. Mission SUCCESS.");
                        aircraftState.MissionPhase = "END";
                        aircraftState.State = "SUCCESS";
                        // Save the completed trajectory
                        if (aircraftState.RampTrajectory != null)
                            PathPlanner.DumpTrajectory(aircraftState.AircraftId, "TAKEOFF", aircraftState.RampTrajectory, aircraftState.LaneSequence);
                        if (aircraftState.LandingTrajectory != null)
                            PathPlanner.DumpTrajectory(aircraftState.AircraftId, "LANDING", aircraftState.LandingTrajectory, null);
                        if (dispatchCenter != null) {
                            dispatchCenter.cumulSuccessCount++;
                            dispatchCenter.cumulTotalFlightTime += aircraftState.FlightTime;
                            dispatchCenter.cumulTotalFlightLength += aircraftState.FlightLength;
                            // Straight-line distance origin→destination
                            if (aircraftState.Mission != null) {
                                var mission = aircraftState.Mission;
                                if (dispatchCenter.Vertiports.TryGetValue(mission.OriginVertiport, out var ovp) &&
                                    dispatchCenter.Vertiports.TryGetValue(mission.DestinationVertiport, out var dvp)) {
                                    var ohp = ovp.Helipads.Find(h => h.HelipadId == aircraftState.OriginHelipadId);
                                    var dhp = dvp.Helipads.Find(h => h.HelipadId == aircraftState.DestinationHelipadId);
                                    if (ohp != null && dhp != null) {
                                        Vector3 oc = Vector3.zero, dc = Vector3.zero;
                                        foreach (var p in ohp.Boundary) oc += p; oc /= ohp.Boundary.Count;
                                        foreach (var p in dhp.Boundary) dc += p; dc /= dhp.Boundary.Count;
                                        dispatchCenter.cumulTotalStraightDist += Vector3.Distance(oc, dc);
                                    }
                                }
                            }
                            // Accumulate wait time
                            if (aircraftState.RampTrajectory != null)
                                foreach (var rp in aircraftState.RampTrajectory)
                                    dispatchCenter.cumulTotalWaitTime += rp.WaitTime;
                            if (aircraftState.LandingTrajectory != null)
                                foreach (var rp in aircraftState.LandingTrajectory)
                                    dispatchCenter.cumulTotalWaitTime += rp.WaitTime;
                        }
                        SimulationRecorder.Instance?.RecordEvent("PhaseChange", aircraftState.AircraftId, "Mission SUCCESS", transform.position);
                        currentCorridorId = null;
                        dispatchCenter?.ReleaseRuntimeWaypointClaims(aircraftState.AircraftId);
                        dispatchCenter?.NotifyMissionCompleted(aircraftState, transform.position);
                        hasMissionCompleted = true;
                        break;
                    }

                    var landPt = aircraftState.LandingTrajectory[currentLandingpointIndex];

                    // Landing trigger checkpoint: deferred landing path planning
                    // Aircraft WAITS in the exit corridor if HS/approach is occupied,
                    // retrying periodically up to maxCorridorWaitTime before deregistering.
                    if (landPt.CheckpointType == "LandingTrigger" && !aircraftState.LandingPlanTriggered)
                    {
                        float now = Time.time;
                        float maxCorridorWait = (dispatchCenter != null && dispatchCenter.config != null)
                            ? dispatchCenter.config.maxCorridorWaitTime : 90f;
                        float retryInterval = 1.5f; // retry every 1.5 seconds

                        // Record first attempt time
                        if (aircraftState.LandingRetryStartTime < 0f)
                        {
                            aircraftState.LandingRetryStartTime = now;
                            aircraftState.LastLandingRetryTime = 0f; // force immediate first attempt
                            Debug.Log($"[Aircraft {aircraftState.AircraftId}] Landing checkpoint reached. Starting corridor wait (max={maxCorridorWait}s).");
                        }

                        float elapsed = now - aircraftState.LandingRetryStartTime;

                        // Timeout: deregister to free space for other aircraft
                        if (elapsed > maxCorridorWait)
                        {
                            Debug.LogError($"[Aircraft {aircraftState.AircraftId}] Corridor wait timeout ({elapsed:F1}s > {maxCorridorWait}s). Deregistering.");
                            aircraftState.State = "FAIL";
                            if (dispatchCenter != null) dispatchCenter.cumulFailCount++;
                            break;
                        }

                        // Retry landing path planning at intervals
                        if (now - aircraftState.LastLandingRetryTime >= retryInterval || aircraftState.LastLandingRetryTime <= 0f)
                        {
                            aircraftState.LastLandingRetryTime = now;
                            Debug.Log($"[Aircraft {aircraftState.AircraftId}] Retrying landing plan (elapsed={elapsed:F1}s/{maxCorridorWait}s)...");

                            var newLandingPath = dispatchCenter.pathPlanner.PlanLandingPath(
                                aircraftState.AircraftId,
                                aircraftState.ExitCorridorId,
                                aircraftState.DestinationHSId,
                                aircraftState.DestinationHelipadId,
                                now, // use current time: aircraft has been waiting
                                aircraftState.Mission.DestinationVertiport);

                            if (newLandingPath != null && newLandingPath.Count > 0)
                            {
                                // Splice: keep corridor points up to and including the checkpoint,
                                // append the new planned landing path
                                var merged = new List<RampPoint>();
                                for (int i = 0; i <= currentLandingpointIndex; i++)
                                    merged.Add(aircraftState.LandingTrajectory[i]);
                                merged.AddRange(newLandingPath);
                                aircraftState.LandingTrajectory = merged;
                                aircraftState.LandingPlanTriggered = true;
                                isHoldingForLandingPlan = false;
                                Debug.Log($"[Aircraft {aircraftState.AircraftId}] Landing path spliced after {elapsed:F1}s corridor wait: {merged.Count} total points.");
                            }
                            // else: stay at checkpoint, will retry next interval
                        }

                        // Hold position at checkpoint until landing plan succeeds or timeout.
                        // Set a wait on the controller to prevent index advancement and
                        // enforce retryInterval between planning attempts.
                        if (!aircraftState.LandingPlanTriggered)
                        {
                            isHoldingForLandingPlan = true;
                            controller.SetTargetPosition(landPt.Point, retryInterval);
                            break;
                        }
                    }

                    // After passing the corridor segment, clear corridor ID for HS traversal
                    if (aircraftState.LandingPlanTriggered &&
                        string.IsNullOrEmpty(landPt.CheckpointType) &&
                        currentLandingpointIndex > 2)
                    {
                        currentCorridorId = null;
                    }

                    if (!TryAcquireVertiportWaypoint(landPt, ref currentLandingpointIndex))
                        break;

                    controller.SetScheduledTargetPosition(
                        landPt.Point,
                        landPt.ReachTime,
                        landPt.WaitTime);
                    break;

                case "WAITING":
                    // Do nothing — SimulationManager will assign next mission
                    break;

                case "END":
                    // Mission complete. SimulationManager will chain next mission
                    // or enter WAITING if no destination available.
                    break;
            }
        }

        /// <summary>
        /// Reset agent state for a new mission (called by SimulationManager during chaining).
        /// </summary>
        public void ResetForNewMission()
        {
            currentTakeoffpointIndex = 0;
            currentLandingpointIndex = 0;
            currentLaneSequenceIndex = 0;
            isWaitingForLaneChange = false;
            isWaitingForEntry = false;
            pendingTargetLaneId = null;
            activeLaneChangeTargetLaneId = null;
            laneChangeWaitStartedAt = -1f;
            lastLaneChangeRerouteAt = -1f;
            currentCorridorId = null;
            hasMissionCompleted = false;
            isWaitingForVertiportResource = false;
            InvalidateRouteTraversalCache();
            dispatchCenter?.ReleaseRuntimeWaypointClaims(aircraftState?.AircraftId);
            controller.ClearWaitState();
            controller.moveSpeed = controller.maxSpeed;
        }

        private bool TryAcquireVertiportWaypoint(RampPoint point, ref int waypointIndex)
        {
            if (dispatchCenter == null ||
                dispatchCenter.TryClaimVertiportWaypoint(aircraftState.AircraftId, point.Point))
            {
                if (isWaitingForVertiportResource)
                    Debug.Log($"[RuntimeGate] {aircraftState.AircraftId} released toward waypoint {waypointIndex}.");
                isWaitingForVertiportResource = false;
                return true;
            }

            if (!isWaitingForVertiportResource)
                Debug.Log($"[RuntimeGate] {aircraftState.AircraftId} holding before waypoint {waypointIndex}.");
            isWaitingForVertiportResource = true;
            waypointIndex = Mathf.Max(-1, waypointIndex - 1);
            controller.SetTargetPosition(transform.position, 0.25f);
            return false;
        }

        bool CurrentLaneHasExitCorridor()
        {
            string laneId = aircraftState.CurrentLaneId;
            string exitCorrId = aircraftState.ExitCorridorId;
            if (string.IsNullOrEmpty(laneId) || string.IsNullOrEmpty(exitCorrId))
                return false;
            if (dispatchCenter == null) return false;
            if (!dispatchCenter.Corridors.TryGetValue(exitCorrId, out var cor))
                return false;
            return cor.LinkLaneId == laneId;
        }

        bool IsSameEdge(string laneId1, string laneId2)
        {
            if (string.IsNullOrEmpty(laneId1) || string.IsNullOrEmpty(laneId2))
                return false;

            if (dispatchCenter == null || dispatchCenter.LaneToEdgeMap == null)
                return false;

            return dispatchCenter.LaneToEdgeMap.TryGetValue(laneId1, out string edge1)
                && dispatchCenter.LaneToEdgeMap.TryGetValue(laneId2, out string edge2)
                && edge1 == edge2;
        }

        /// <summary>
        /// Find the next centerline waypoint downstream from the aircraft's current position.
        /// Finds the closest centerline point to the aircraft, then returns the NEXT point
        /// after it (toward the lane exit). If the closest point is already the last one,
        /// returns the last point.
        ///
        /// This enables incremental waypoint-by-waypoint lane following, fixing the bug where
        /// after a lane change the aircraft would skip all intermediate centerline points
        /// and fly directly to the lane exit point.
        /// </summary>
        private Vector3 GetNextCenterlinePoint(List<Vector3> centerline, Vector3 currentPos)
        {
            if (centerline == null || centerline.Count == 0)
                return currentPos;

            // Find the closest centerline point to the aircraft
            int closestIdx = 0;
            float minDist = float.MaxValue;
            for (int i = 0; i < centerline.Count; i++)
            {
                float d = Vector3.Distance(currentPos, centerline[i]);
                if (d < minDist)
                {
                    minDist = d;
                    closestIdx = i;
                }
            }

            // Return the NEXT point after the closest one (toward lane exit).
            // If already at the last point, return it (advancing to next lane
            // will be handled by the Centerline[last] arrival check).
            int nextIdx = Mathf.Min(closestIdx + 1, centerline.Count - 1);

            // If the next point is behind us (e.g. we overshot it), skip forward
            // to find the first point that is meaningfully ahead.
            // Check by comparing distances to the lane exit: a downstream point
            // should be closer to the exit than the current position.
            float distToExit = Vector3.Distance(currentPos, centerline[centerline.Count - 1]);
            int safety = 0;
            while (nextIdx < centerline.Count - 1 &&
                   Vector3.Distance(centerline[nextIdx], centerline[centerline.Count - 1]) > distToExit + 0.5f)
            {
                nextIdx++;
                safety++;
                if (safety > centerline.Count) break; // safeguard
            }

            return centerline[nextIdx];
        }

        private List<Vector3> GetRouteTraversal(int laneIndex)
        {
            if (aircraftState?.LaneSequence == null ||
                laneIndex < 0 || laneIndex >= aircraftState.LaneSequence.Count)
            {
                return new List<Vector3>();
            }

            Lane lane = aircraftState.LaneSequence[laneIndex];
            if (cachedTraversal != null &&
                cachedTraversalLaneIndex == laneIndex &&
                ReferenceEquals(cachedTraversalLane, lane))
            {
                return cachedTraversal;
            }

            cachedTraversal = LaneGeometryUtility.BuildRouteTraversal(
                aircraftState?.LaneSequence,
                laneIndex,
                dispatchCenter?.LaneConnectionCorridors);
            cachedTraversalLaneIndex = laneIndex;
            cachedTraversalLane = lane;
            return cachedTraversal;
        }

        private void InvalidateRouteTraversalCache()
        {
            cachedTraversalLaneIndex = -1;
            cachedTraversalLane = null;
            cachedTraversal = null;
        }
    }
}
