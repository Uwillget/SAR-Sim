using System.Collections.Generic;
using System.IO;
using System.Linq;
using RoadNetworkEditor.Networking;
using RoadNetworkEditor.Reservations;
using RoadNetworkEditor.Routes;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Path planner for the simulation system. Handles:
    /// - Helipad ↔ HS anchor via ApproachNetwork graph search (time-window-aware Dijkstra)
    /// - HS anchor ↔ Corridor via HoldingStructure Vertex-Ramp graph search (time-window-aware Dijkstra)
    /// - Road network Edge + Lane sequence planning (A*)
    /// - Vertiport resource time-window conflict checking and booking
    /// - Deferred landing path planning (triggered at exit corridor checkpoint)
    /// </summary>
    public class PathPlanner
    {
        private SimulationManager sim;

        public PathPlanner(SimulationManager manager)
        {
            sim = manager;
        }

        // ====================================================================
        // INTERNAL PATH TYPES
        // ====================================================================
        /// <summary>
        /// Self-contained path step. ReachTime = when the aircraft is ready at this node
        /// (= bestArrival[node]). WaitTime = total seconds the aircraft waits at this node
        /// before departing to the next. DepartTime = ReachTime + WaitTime.
        /// WaitTime is computed during reconstruction as:
        ///   WaitTime[node] = bestArrival[next] - bestArrival[node] - travelTime[node→next]
        /// Steps[end].WaitTime is always 0 (no outgoing edge).
        /// </summary>
        private class PathStep
        {
            public string NodeId;
            public string IncomingEdgeId;   // Edge traversed to reach this node (null for start)
            public float ReachTime;         // = bestArrival[node], when aircraft is ready at this node
            public float WaitTime;          // Total wait at this node before departing to next
            public List<Vector3> ArcWaypoints;  // Arc-interpolated positions from previous step (null = straight line)
            public Vector3 Position;
        }

        /// <summary>
        /// Unified timed path. Steps[0] = start, Steps[N-1] = end.
        /// </summary>
        private class TimedPath
        {
            public List<PathStep> Steps;
            public float FinalArrivalTime;
            public float StartTime;
        }

        // ====================================================================
        // MIN-HEAP for Dijkstra (O(log n) pop instead of O(n) linear scan)
        // ====================================================================
        private class MinHeap<TKey, TValue> where TKey : System.IComparable<TKey>
        {
            private List<(TKey key, TValue value)> data = new List<(TKey, TValue)>();
            public int Count => data.Count;

            public void Push(TKey key, TValue value)
            {
                data.Add((key, value));
                int i = data.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (data[p].key.CompareTo(data[i].key) <= 0) break;
                    (data[p], data[i]) = (data[i], data[p]);
                    i = p;
                }
            }

            public (TKey key, TValue value) Pop()
            {
                var result = data[0];
                data[0] = data[data.Count - 1];
                data.RemoveAt(data.Count - 1);
                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1, right = 2 * i + 2, smallest = i;
                    if (left < data.Count && data[left].key.CompareTo(data[smallest].key) < 0)
                        smallest = left;
                    if (right < data.Count && data[right].key.CompareTo(data[smallest].key) < 0)
                        smallest = right;
                    if (smallest == i) break;
                    (data[i], data[smallest]) = (data[smallest], data[i]);
                    i = smallest;
                }
                return result;
            }
        }

        // ====================================================================
        // CANDIDATE EDGE
        // ====================================================================
        private struct CandidateEdge
        {
            public string EdgeId;
            public string LaneId;
            public string CorridorId;
        }

        // ====================================================================
        // TIME-WINDOW HELPER (no reflection)
        // ====================================================================
        private static List<ReservationWindow> ToReservationWindows(List<OccupiedTimeWindow> windows)
        {
            var result = new List<ReservationWindow>(windows != null ? windows.Count : 0);
            if (windows == null) return result;

            foreach (var window in windows)
            {
                result.Add(new ReservationWindow(
                    window.OccupiedAircraftId,
                    window.StartTime,
                    window.EndTime));
            }

            return result;
        }

        private bool TryGetReservationWindows(
            string resourceId,
            out List<ReservationWindow> windows)
        {
            if (!sim.TryGetOccupiedWindows(resourceId, out var occupiedWindows))
            {
                windows = null;
                Debug.LogError($"[Reservation] Unknown resource in path search: {resourceId}.");
                return false;
            }

            windows = ToReservationWindows(occupiedWindows);
            return true;
        }

        private bool TryScheduleNodeArrival(
            string nodeId,
            float requestedArrival,
            float maxWait,
            out float scheduledArrival)
        {
            scheduledArrival = requestedArrival;
            if (!TryGetReservationWindows(nodeId, out var nodeWindows))
                return false;

            float buffer = sim.config != null ? sim.config.resourceBuffer : 2f;
            float dwell = sim.config != null ? sim.config.nodeDwellTime : 3f;
            ReservationWindowSearchResult result = ReservationWindowSolver.FindEarliest(
                nodeWindows,
                requestedArrival - buffer,
                requestedArrival + dwell + buffer,
                maxWait);

            if (!result.Success)
                return false;

            scheduledArrival = requestedArrival + result.WaitTime;
            return true;
        }

        private bool TryScheduleTransition(
            string currentNodeId,
            string edgeId,
            string targetNodeId,
            float currentArrival,
            float travelTime,
            float maxWait,
            out ReservationTransitionSearchResult result)
        {
            result = default;
            if (!TryGetReservationWindows(currentNodeId, out var currentWindows) ||
                !TryGetReservationWindows(edgeId, out var edgeWindows) ||
                !TryGetReservationWindows(targetNodeId, out var targetWindows))
            {
                return false;
            }

            float buffer = sim.config != null ? sim.config.resourceBuffer : 2f;
            float dwell = sim.config != null ? sim.config.nodeDwellTime : 3f;
            result = ReservationTransitionSolver.FindEarliest(
                currentWindows,
                edgeWindows,
                targetWindows,
                currentArrival,
                travelTime,
                dwell,
                buffer,
                maxWait);
            return result.Success;
        }

        // ====================================================================
        // MAIN ENTRY POINT (Phase 5.8 — Takeoff only, landing deferred)
        // ====================================================================
        public AircraftState PlanMission(string originVP, string destVP, float departureTime,
            string originHelipadId = null, string destHelipadId = null, string missionId = null,
            IReadOnlyList<string> explicitRouteLaneIds = null, int priority = 1)
        {
            if (!sim.Vertiports.ContainsKey(originVP) || !sim.Vertiports.ContainsKey(destVP))
            {
                Debug.LogWarning($"[PathPlanner] Invalid vertiport IDs.");
                return null;
            }

            float speed = sim.config != null ? sim.config.maxSpeed : 20f;

            // 1. Select helipads
            Helipad originHP = ResolveHelipad(originVP, originHelipadId);
            Helipad destHP = ResolveHelipad(destVP, destHelipadId);
            if (originHP == null || destHP == null) return null;
            Vector3 originCenter = GetHelipadCenter(originHP);
            Vector3 destCenter = GetHelipadCenter(destHP);

            // 2. Gather candidate edges from helipad-reachable corridors
            List<CandidateEdge> entryCandidates = GetCandidateEdgesForHelipad(originHP, originVP, "Entry");
            List<CandidateEdge> exitCandidates = GetCandidateEdgesForHelipad(destHP, destVP, "Exit");
            if (entryCandidates.Count == 0 || exitCandidates.Count == 0)
            {
                Debug.LogWarning($"[PathPlanner] No candidate entry({entryCandidates.Count})/exit({exitCandidates.Count}) edges " +
                               $"for helipads {originHP.HelipadId}/{destHP.HelipadId}.");
                return null;
            }

            // 3. Build all valid (entry, exit) pairs, scored by cosine similarity between
            //    lane direction at corridor connection point and the global origin→dest vector.
            //    Higher score = lane direction better aligned with travel direction.
            Vector3 globalDir = (destCenter - originCenter).normalized;
            var pairs = new List<(CandidateEdge entry, CandidateEdge exit, float score)>();
            foreach (var e in entryCandidates)
            {
                if (!sim.Edges.TryGetValue(e.EdgeId, out var entryEdge) || entryEdge.LinkNodesId == null) continue;
                if (!sim.Lanes.TryGetValue(e.LaneId, out var entryLane)) continue;
                if (!sim.Corridors.TryGetValue(e.CorridorId, out var entryCorCheck)) continue;
                float entryDot = Vector3.Dot(globalDir,
                    GetLaneDirectionAtPoint(entryLane, entryCorCheck.LaneLinkPoint));

                foreach (var x in exitCandidates)
                {
                    if (!sim.Edges.TryGetValue(x.EdgeId, out var exitEdge) || exitEdge.LinkNodesId == null) continue;
                    if (e.EdgeId == x.EdgeId) continue;
                    if (!sim.Lanes.TryGetValue(x.LaneId, out var exitLane)) continue;
                    if (!sim.Corridors.TryGetValue(x.CorridorId, out var exitCorCheck)) continue;
                    float exitDot = Vector3.Dot(globalDir,
                        GetLaneDirectionAtPoint(exitLane, exitCorCheck.LaneLinkPoint));

                    float score = entryDot + exitDot; // both range [-1, 1], sum range [-2, 2]
                    pairs.Add((e, x, score));
                }
            }
            if (pairs.Count == 0)
            {
                Debug.LogWarning("[PathPlanner] No valid entry/exit edge pairs.");
                return null;
            }
            // Sort descending: highest dot product first (best lane alignment)
            pairs.Sort((a, b) => b.score.CompareTo(a.score));

            // 4. Try each pair until lane sequence succeeds
            string aircraftId = !string.IsNullOrEmpty(missionId)
                ? missionId : $"AC_{originVP}_{destVP}_{System.DateTime.Now.Ticks}";

            foreach (var (entry, exit, score) in pairs)
            {
                NetConnectedCorridor entryCor = sim.Corridors[entry.CorridorId];
                NetConnectedCorridor exitCor = sim.Corridors[exit.CorridorId];

                bool hasExplicitRoute = explicitRouteLaneIds != null && explicitRouteLaneIds.Count > 0;
                if (hasExplicitRoute &&
                    (!string.Equals(explicitRouteLaneIds[0], entryCor.LinkLaneId,
                        System.StringComparison.Ordinal) ||
                     !string.Equals(explicitRouteLaneIds[explicitRouteLaneIds.Count - 1],
                        exitCor.LinkLaneId, System.StringComparison.Ordinal)))
                {
                    continue;
                }

                // 4a. Use the imported route exactly when supplied; otherwise run A*.
                List<Edge> edgeSequence = null;
                List<Lane> laneSequence = null;
                if (hasExplicitRoute)
                {
                    ExplicitRouteResolveResult explicitResult = ExplicitRouteResolver.Resolve(
                        explicitRouteLaneIds,
                        sim.Lanes,
                        sim.LaneConnectionCorridors.Values,
                        entryCor.LinkLaneId,
                        exitCor.LinkLaneId,
                        sim.LaneToEdgeMap);
                    if (!explicitResult.Success)
                    {
                        Debug.LogWarning($"[PathPlanner] Imported route rejected: {explicitResult.Error}");
                        continue;
                    }
                    laneSequence = explicitResult.Lanes;
                    edgeSequence = BuildEdgeSequenceFromLanes(laneSequence);
                }
                else
                {
                    edgeSequence = FindEdgeSequence(entryCor.CorridorId, exitCor.CorridorId);
                    if (edgeSequence != null && edgeSequence.Count > 0)
                        laneSequence = FindLaneSequence(edgeSequence, entryCor.CorridorId, exitCor.CorridorId);

                    // A lane-level fallback can cross disconnected altitude layers through
                    // explicit lane-connection corridors represented as virtual lanes.
                    if (laneSequence == null || laneSequence.Count == 0)
                    {
                        laneSequence = FindLaneSequenceWithLayerConnections(
                            entryCor.LinkLaneId,
                            exitCor.LinkLaneId);
                    }
                }
                if (laneSequence == null || laneSequence.Count == 0) continue;
                edgeSequence = edgeSequence ?? new List<Edge>();

                Debug.Log($"[PathPlanner] Pair accepted: entry={entry.CorridorId}({entry.EdgeId}) " +
                          $"exit={exit.CorridorId}({exit.EdgeId}) score={score:F2}");

                // 4c. Identify HS and anchor for both vertiports
                string originHSId = FindHSForCorridor(entryCor, originVP);
                string destHSId = FindHSForCorridor(exitCor, destVP);
                if (string.IsNullOrEmpty(originHSId) || string.IsNullOrEmpty(destHSId)) continue;
                string destAnchorNodeId = FindApproachAnchorNode(destHSId, destVP);

                // 4d. Takeoff approach: Helipad → HS anchor (time-window-aware)
                var apprTakeoff = FindApproachPathToHS(originCenter, originHSId, originVP, departureTime, speed);
                if (apprTakeoff == null) continue;
                List<RampPoint> takeoffA = TimedPathToRampPoints(apprTakeoff);
                float currentTime = apprTakeoff.FinalArrivalTime;

                // 4e. Takeoff HS internal: anchor → corridor (time-window-aware)
                string anchorVertId = FindAnchorVertex(originHSId);
                if (string.IsNullOrEmpty(anchorVertId)) continue;
                var hsTakeoff = FindHSPathToCorridorTimeAware(anchorVertId, entryCor.CorridorId, originHSId, currentTime, speed);
                if (hsTakeoff == null) continue;
                List<RampPoint> hsPts = HSTimedPathToRampPoints(hsTakeoff);
                currentTime = hsTakeoff.FinalArrivalTime;

                // 4e-bis. Verify no extended-stay conflict (post-reconstruction safety net)
                if (!VerifyPathExtendedStay(apprTakeoff, hsTakeoff))
                    continue;

                // 4f. Build takeoff trajectory
                List<RampPoint> takeoffTraj = new List<RampPoint>();
                float originHold = takeoffA.Count > 0
                    ? Reservations.TakeoffOriginSchedule.CalculateHold(
                        departureTime,
                        originCenter,
                        takeoffA[0].Point,
                        takeoffA[0].ReachTime,
                        speed)
                    : 0f;
                takeoffTraj.Add(new RampPoint
                {
                    Point = originCenter, WaitTime = originHold, ReachTime = departureTime, CheckpointType = null
                });
                takeoffTraj.AddRange(takeoffA);
                takeoffTraj.AddRange(hsPts);

                var corridorPts = SampleCorridorBezier(entryCor, isEntry: true);
                for (int i = 0; i < corridorPts.Count; i++)
                {
                    float d = (i == 0 && takeoffTraj.Count > 0)
                        ? Vector3.Distance(corridorPts[i], takeoffTraj.Last().Point) : 0f;
                    if (takeoffTraj.Count > 0 && i > 0)
                        d = Vector3.Distance(corridorPts[i], corridorPts[i - 1]);
                    currentTime += d / speed;
                    string checkpoint = null;
                    if (i == corridorPts.Count - 3 && corridorPts.Count >= 3)
                        checkpoint = "EntryMerge";
                    takeoffTraj.Add(new RampPoint
                    {
                        Point = corridorPts[i], WaitTime = 0f, ReachTime = currentTime, CheckpointType = checkpoint
                    });
                }

                // 4g. Pre-sample exit corridor initial segment
                var exitSample = SampleCorridorBezier(exitCor, isEntry: false);
                List<RampPoint> landingPartial = new List<RampPoint>();
                float landTime = 0f;
                for (int i = 0; i < exitSample.Count; i++)
                {
                    if (i > 0) landTime += Vector3.Distance(exitSample[i], exitSample[i - 1]) / speed;
                    string checkpoint = null;
                    if (i == exitSample.Count / 2) checkpoint = "LandingTrigger";
                    landingPartial.Add(new RampPoint
                    {
                        Point = exitSample[i], WaitTime = 0f, ReachTime = landTime, CheckpointType = checkpoint
                    });
                }

                // 4h. Book takeoff resources
                if (!BookTakeoffResources(
                        aircraftId,
                        originHP,
                        entryCor,
                        apprTakeoff,
                        hsTakeoff,
                        takeoffTraj))
                {
                    Debug.LogWarning($"[PathPlanner] Takeoff booking rejected for {aircraftId}; trying next route pair.");
                    continue;
                }

                var state = new AircraftState
                {
                    AircraftId = aircraftId,
                    Mission = new Flight
                    {
                        OriginVertiport = originVP,
                        DestinationVertiport = destVP,
                        DepartureTime = departureTime,
                        Priority = priority
                    },
                    MissionPhase = "START",
                    State = "ACTIVE",
                    Position = originCenter,
                    FlightTime = 0f,
                    FlightLength = 0f,
                    CurrentLaneId = null,
                    MissionKeyNodes = new AircraftState.KeyNodes
                    {
                        OriginHelipad = originCenter,
                        EntryCorridorPoint = entryCor.LaneLinkPoint,
                        ExitCorridorPoint = exitCor.LaneLinkPoint,
                        DestinationHelipad = destCenter
                    },
                    EdgeSequence = edgeSequence,
                    LaneSequence = laneSequence,
                    RampTrajectory = takeoffTraj,
                    LandingTrajectory = landingPartial,
                    OriginHelipadId = originHP.HelipadId,
                    DestinationHelipadId = destHP.HelipadId,
                    EntryCorridorId = entryCor.CorridorId,
                    ExitCorridorId = exitCor.CorridorId,
                    DestinationHSId = destHSId,
                    DestinationApproachAnchorNodeId = destAnchorNodeId,
                    LandingPlanTriggered = false
                };

                Debug.Log($"[PathPlanner] Planned takeoff: entryEdge={entry.EdgeId} exitEdge={exit.EdgeId} " +
                          $"Edges={edgeSequence.Count} Lanes={laneSequence.Count} " +
                          $"Takeoff={takeoffTraj.Count} LandingPartial={landingPartial.Count}");

                return state;
            }

            Debug.LogWarning($"[PathPlanner] All {pairs.Count} corridor pairs exhausted. Lane sequences failed for all.");
            return null;
        }

        private List<Edge> BuildEdgeSequenceFromLanes(IEnumerable<Lane> lanes)
        {
            var result = new List<Edge>();
            string previousEdgeId = null;
            foreach (Lane lane in lanes)
            {
                if (!sim.LaneToEdgeMap.TryGetValue(lane.LaneId, out string edgeId) ||
                    string.Equals(edgeId, previousEdgeId, System.StringComparison.Ordinal) ||
                    !sim.Edges.TryGetValue(edgeId, out Edge edge))
                {
                    continue;
                }

                result.Add(edge);
                previousEdgeId = edgeId;
            }
            return result;
        }

        // ====================================================================
        // DEFERRED LANDING PLANNING
        // ====================================================================
        public List<RampPoint> PlanLandingPath(string aircraftId, string exitCorridorId,
            string destHSId, string destHelipadId, float arrivalTimeAtCheckpoint, string destVP)
        {
            if (!sim.Corridors.TryGetValue(exitCorridorId, out var exitCor))
            {
                Debug.LogWarning("[PathPlanner] Exit corridor not found.");
                return null;
            }

            float speed = sim.config != null ? sim.config.maxSpeed : 20f;
            var result = new List<RampPoint>();
            float t = arrivalTimeAtCheckpoint;

            // 1. Remaining corridor: from checkpoint (midpoint) to RampLinkPoint
            var fullSample = SampleCorridorBezier(exitCor, isEntry: false);
            int checkpointIdx = fullSample.Count / 2;
            for (int i = checkpointIdx + 1; i < fullSample.Count; i++)
            {
                t += Vector3.Distance(fullSample[i], fullSample[i - 1]) / speed;
                result.Add(new RampPoint
                {
                    Point = fullSample[i],
                    WaitTime = 0f,
                    ReachTime = t,
                    CheckpointType = null
                });
            }

            // 2. HS landing: corridor ramp → HS anchor (time-window-aware)
            // Try all anchor vertices — pick the first one with a valid complete landing path.
            // Anchors may have directed edges (entry-only or exit-only); the planner must
            // find an anchor whose approach-network edges point in the landing direction.
            var allAnchorVertIds = FindAllAnchorVertices(destHSId);
            if (allAnchorVertIds == null || allAnchorVertIds.Count == 0)
            {
                Debug.LogWarning("[PathPlanner] No destination anchor vertices.");
                return null;
            }

            Helipad destHelipad = ResolveHelipad(destVP, destHelipadId);
            Vector3 destCenter = GetHelipadCenter(destHelipad);
            TimedPath hsLanding = null;
            TimedPath apprLanding = null;
            string chosenAnchorVertId = null;
            string chosenAnchorNodeId = null;

            foreach (var anchorVertId in allAnchorVertIds)
            {
                // 2a. Try HS path to this anchor vertex
                var hsPath = FindHSPathFromCorridorTimeAware(
                    exitCorridorId, anchorVertId, destHSId, t, speed);
                if (hsPath == null)
                {
                    Debug.Log($"[PathPlanner] HS path to anchor {anchorVertId} failed, trying next.");
                    continue;
                }

                // 2b. Find the corresponding approach anchor node
                string apprNodeId = FindApproachAnchorNodeForVertex(destHSId, destVP, anchorVertId);
                if (string.IsNullOrEmpty(apprNodeId))
                {
                    Debug.Log($"[PathPlanner] No approach node for anchor {anchorVertId}, trying next.");
                    continue;
                }

                // 2c. Try approach path from this anchor node to the destination helipad
                var apprPath = FindApproachPathFromHS(apprNodeId, destCenter, destVP, t, speed);
                if (apprPath == null)
                {
                    Debug.Log($"[PathPlanner] Approach path from anchor {anchorVertId} (node {apprNodeId}) failed, trying next.");
                    continue;
                }

                // Both paths valid — use this anchor
                hsLanding = hsPath;
                apprLanding = apprPath;
                chosenAnchorVertId = anchorVertId;
                chosenAnchorNodeId = apprNodeId;
                Debug.Log($"[PathPlanner] Selected anchor {anchorVertId} (node {apprNodeId}) for landing path.");
                break;
            }

            if (hsLanding == null || apprLanding == null)
            {
                Debug.LogWarning($"[PathPlanner] No valid landing path through any of {allAnchorVertIds.Count} anchor(s).");
                return null;
            }

            // Build HS trajectory from TimedPath, skipping vertex 0 (at corridor ramp)
            // hsLanding.Steps[0] = vertex 0 (corridor ramp, aircraft is at RampLinkPoint)
            // hsLanding.Steps[1..N-1] = vertices to add to trajectory
            if (hsLanding.Steps.Count > 1)
            {
                var hsPts = HSTimedPathToRampPoints(hsLanding);
                // Skip Step[0] (vertex 0 at corridor ramp)
                for (int i = 1; i < hsPts.Count; i++)
                    result.Add(hsPts[i]);

                // ReachTime already includes all travel + waits from Dijkstra
                t = result.Count > 0 ? result.Last().ReachTime : t;
            }

            // 3. Build approach trajectory from TimedPath
            var apprPts = TimedPathToRampPoints(apprLanding);
            result.AddRange(apprPts);

            // Add final waypoint at destination helipad center
            {
                float finalTime = apprLanding.FinalArrivalTime;
                result.Add(new RampPoint
                {
                    Point = destCenter,
                    WaitTime = 0f,
                    ReachTime = finalTime,
                    CheckpointType = null
                });
            }

            // 3b. Post-reconstruction verification
            if (!VerifyPathExtendedStay(apprLanding, hsLanding))
            {
                Debug.LogWarning($"[PathPlanner] Landing path rejected for {aircraftId}: extended-stay conflict.");
                return null;
            }

            // 4. Book landing resources
            if (!BookLandingResources(
                    aircraftId,
                    destHelipad,
                    exitCor,
                    hsLanding,
                    apprLanding,
                    result))
            {
                Debug.LogWarning($"[PathPlanner] Landing booking rejected for {aircraftId}; path was not committed.");
                return null;
            }

            Debug.Log($"[PathPlanner] Landing path planned: {result.Count} points via anchor {chosenAnchorVertId}.");

            return result;
        }

        // ====================================================================
        // TRAJECTORY DUMP (debug)
        // ====================================================================
        public static void DumpTrajectory(string aircraftId, string phase, List<RampPoint> trajectory,
            List<Lane> laneSeq = null)
        {
            try
            {
                string dir = Application.dataPath + "/../Output/Trajectories";
                Directory.CreateDirectory(dir);
                string path = $"{dir}/{aircraftId}_{phase}.txt";
                using (var w = new StreamWriter(path))
                {
                    w.WriteLine($"=== {aircraftId} {phase} ===");
                    w.WriteLine($"Time: {System.DateTime.Now:HH:mm:ss}");
                    w.WriteLine();
                    if (trajectory != null)
                    {
                        w.WriteLine($"[Trajectory] {trajectory.Count} waypoints:");
                        w.WriteLine($"{"Idx",4}  {"Position",-44}  {"ReachTime",10}  {"WaitTime",8}  {"DepartTime",10}  Checkpoint");
                        w.WriteLine(new string('-', 110));
                        for (int i = 0; i < trajectory.Count; i++)
                        {
                            var rp = trajectory[i];
                            float depart = rp.ReachTime + rp.WaitTime;
                            string cp = rp.CheckpointType ?? "-";
                            w.WriteLine($"{i,4}  ({rp.Point.x,6:F1} {rp.Point.y,6:F1} {rp.Point.z,7:F1})  {rp.ReachTime,10:F2}  {rp.WaitTime,8:F2}  {depart,10:F2}  {cp}");
                        }
                    }
                    if (laneSeq != null && laneSeq.Count > 0)
                    {
                        w.WriteLine();
                        w.WriteLine($"[LaneSequence] {laneSeq.Count} lanes:");
                        for (int i = 0; i < laneSeq.Count; i++)
                            w.WriteLine($"  {i}: {laneSeq[i].LaneId}");
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PathPlanner] Failed to dump trajectory for {aircraftId}: {e.Message}");
            }
        }

        // ====================================================================
        // RESOURCE SELECTION
        // ====================================================================
        private Helipad ResolveHelipad(string vpId, string preferredId)
        {
            if (!sim.Vertiports.TryGetValue(vpId, out var vp)) return null;
            if (!string.IsNullOrEmpty(preferredId) && vp.Helipads != null)
            {
                var h = vp.Helipads.Find(x => x.HelipadId == preferredId);
                if (h != null) return h;
            }
            if (vp.Helipads != null && vp.Helipads.Count > 0)
                return vp.Helipads[0];
            return null;
        }

        /// <summary>
        /// Get candidate edges from corridors of the given type that are reachable from the specified helipad.
        /// Falls back to all corridors of the type if the helipad has no ReachableCorridorIds computed.
        /// </summary>
        private List<CandidateEdge> GetCandidateEdgesForHelipad(Helipad hp, string vpId, string corridorType)
        {
            var result = new List<CandidateEdge>();
            var allCorridors = GetCorridorsOfType(vpId, corridorType);

            foreach (var cor in allCorridors)
            {
                // Filter: only include corridors that are reachable from this helipad
                if (hp.ReachableCorridorIds.Count > 0 &&
                    !hp.ReachableCorridorIds.Contains(cor.CorridorId))
                    continue;

                if (!sim.Lanes.ContainsKey(cor.LinkLaneId)) continue;
                if (!sim.LaneToEdgeMap.TryGetValue(cor.LinkLaneId, out string edgeId)) continue;
                if (!sim.Edges.ContainsKey(edgeId)) continue;
                result.Add(new CandidateEdge
                {
                    EdgeId = edgeId,
                    LaneId = cor.LinkLaneId,
                    CorridorId = cor.CorridorId
                });
            }

            if (result.Count == 0 && hp.ReachableCorridorIds.Count > 0)
            {
                Debug.LogWarning($"[PathPlanner] Helipad {hp.HelipadId}: no {corridorType} corridors passed filter. " +
                                 $"Reachable=[{string.Join(",", hp.ReachableCorridorIds)}], " +
                                 $"Available=[{string.Join(",", allCorridors.Select(c => c.CorridorId))}]");
            }
            return result;
        }

        // ====================================================================
        // APPROACH NETWORK PATH — Time-window-aware Dijkstra (refactored)
        // ====================================================================
        private TimedPath FindApproachPathToHS(Vector3 helipadCenter, string hsId, string vpId,
            float startTime, float speed)
        {
            Vertiport vp = sim.Vertiports[vpId];
            if (vp.ApproachNet == null || vp.ApproachNet.Nodes == null)
                return null;

            var nodes = vp.ApproachNet.Nodes;
            var edges = vp.ApproachNet.Edges;
            float maxWait = sim.config != null ? sim.config.maxWaitPerResource : 60f;
            ApproachNode startNode = null;
            float minDist = float.MaxValue;
            foreach (var n in nodes)
            {
                float d = Vector3.Distance(n.Position, helipadCenter);
                if (d < minDist) { minDist = d; startNode = n; }
            }
            if (startNode == null) return null;

            float travelToStart = Vector3.Distance(helipadCenter, startNode.Position) / speed;
            float adjustedStart = startTime + travelToStart;

            // Find end node (HS anchor)
            ApproachNode endNode = nodes.FirstOrDefault(n => n.IsAnchor && n.LinkHSId == hsId);
            if (endNode == null) return null;

            // Build adjacency
            var adjacency = new Dictionary<string, List<ApproachEdge>>();
            foreach (var e in edges)
            {
                if (!adjacency.ContainsKey(e.FromNodeId))
                    adjacency[e.FromNodeId] = new List<ApproachEdge>();
                adjacency[e.FromNodeId].Add(e);
            }

            // Time-window-aware Dijkstra with heap
            var bestArrival = new Dictionary<string, float>();
            var prevNode = new Dictionary<string, string>();
            var prevEdge = new Dictionary<string, ApproachEdge>();

            foreach (var n in nodes)
                bestArrival[n.NodeId] = float.MaxValue;

            // Start node occupancy check — user's Isoccupy(t_start, startNode)
            if (!TryScheduleNodeArrival(
                    startNode.NodeId,
                    adjustedStart,
                    maxWait,
                    out float effectiveStart))
            {
                return null;
            }
            bestArrival[startNode.NodeId] = effectiveStart;

            var heap = new MinHeap<float, string>();
            heap.Push(effectiveStart, startNode.NodeId);

            while (heap.Count > 0)
            {
                var (curTime, current) = heap.Pop();
                if (curTime > bestArrival.GetValueOrDefault(current, float.MaxValue)) continue;
                if (current == endNode.NodeId) break;

                if (!adjacency.ContainsKey(current)) continue;
                var curNode = nodes.First(n => n.NodeId == current);

                foreach (var edge in adjacency[current])
                {
                    string neighbor = edge.ToNodeId;
                    var toNode = nodes.FirstOrDefault(n => n.NodeId == neighbor);
                    if (toNode == null) continue;

                    float travelTime = Vector3.Distance(curNode.Position, toNode.Position) / speed;
                    float departTime = bestArrival[current];

                    if (!TryScheduleTransition(
                            current,
                            edge.EdgeId,
                            neighbor,
                            departTime,
                            travelTime,
                            maxWait,
                            out ReservationTransitionSearchResult transition))
                    {
                        continue;
                    }

                    float effectiveArrival = transition.ArrivalTime;

                    // Relaxation
                    if (effectiveArrival < bestArrival.GetValueOrDefault(neighbor, float.MaxValue))
                    {
                        bestArrival[neighbor] = effectiveArrival;
                        prevNode[neighbor] = current;
                        prevEdge[neighbor] = edge;
                        heap.Push(effectiveArrival, neighbor);
                    }
                }
            }

            if (!prevNode.ContainsKey(endNode.NodeId) && startNode.NodeId != endNode.NodeId)
            {
                Debug.LogWarning($"[PathPlanner] No approach path: all routes to anchor {endNode.NodeId} pruned " +
                               $"(departure at {adjustedStart:F1}s, maxWait={maxWait:F1}s). Check resource bookings.");
                return null;
            }

            // Reconstruct into PathSteps.
            // WaitTime[node] = bestArrival[next] - bestArrival[node] - travelTime[node→next]
            // This naturally includes edge wait + next-node occupancy wait.
            var stepsRev = new List<PathStep>();
            string cur = endNode.NodeId;

            // Add end node (no outgoing edge → WaitTime = 0)
            {
                var node = nodes.FirstOrDefault(n => n.NodeId == cur);
                stepsRev.Add(new PathStep
                {
                    NodeId = cur,
                    IncomingEdgeId = null,
                    ReachTime = bestArrival[cur],
                    WaitTime = 0f,
                    Position = node != null ? node.Position : Vector3.zero,
                });
            }

            while (cur != startNode.NodeId)
            {
                if (!prevNode.ContainsKey(cur)) break;
                string pred = prevNode[cur];
                var predNode = nodes.FirstOrDefault(n => n.NodeId == pred);
                var curNode = nodes.FirstOrDefault(n => n.NodeId == cur);
                float travelTime = Vector3.Distance(predNode.Position, curNode.Position) / speed;

                // WaitTime at pred = total elapsed - travel
                float waitAtPred = bestArrival[cur] - bestArrival[pred] - travelTime;
                if (waitAtPred < 1e-4f) waitAtPred = 0f; // clamp floating-point artifact

                // Set incoming edge on the previously added step (cur in forward direction)
                stepsRev.Last().IncomingEdgeId = prevEdge.ContainsKey(cur) ? prevEdge[cur].EdgeId : null;

                stepsRev.Add(new PathStep
                {
                    NodeId = pred,
                    IncomingEdgeId = null,
                    ReachTime = bestArrival[pred],
                    WaitTime = waitAtPred,
                    Position = predNode != null ? predNode.Position : Vector3.zero,
                });
                cur = pred;
            }
            stepsRev.Reverse();

            return new TimedPath
            {
                Steps = stepsRev,
                FinalArrivalTime = bestArrival[endNode.NodeId],
                StartTime = adjustedStart
            };
        }

        private TimedPath FindApproachPathFromHS(string anchorNodeId, Vector3 helipadCenter,
            string vpId, float startTime, float speed)
        {
            Vertiport vp = sim.Vertiports[vpId];
            if (vp.ApproachNet == null || vp.ApproachNet.Nodes == null) return null;

            var nodes = vp.ApproachNet.Nodes;
            var startNode = nodes.FirstOrDefault(n => n.NodeId == anchorNodeId);
            if (startNode == null) return null;
            float maxWait = sim.config != null ? sim.config.maxWaitPerResource : 60f;

            ApproachNode endNode = null;
            float minDist = float.MaxValue;
            foreach (var n in nodes)
            {
                float d = Vector3.Distance(n.Position, helipadCenter);
                if (d < minDist) { minDist = d; endNode = n; }
            }
            if (endNode == null) return null;

            float travelFromLast = Vector3.Distance(endNode.Position, helipadCenter) / speed;

            var edges = vp.ApproachNet.Edges;
            var adjacency = new Dictionary<string, List<ApproachEdge>>();
            foreach (var e in edges)
            {
                if (!adjacency.ContainsKey(e.FromNodeId))
                    adjacency[e.FromNodeId] = new List<ApproachEdge>();
                adjacency[e.FromNodeId].Add(e);
            }

            var bestArrival = new Dictionary<string, float>();
            var prevNode = new Dictionary<string, string>();
            var prevEdge = new Dictionary<string, ApproachEdge>();

            foreach (var n in nodes)
                bestArrival[n.NodeId] = float.MaxValue;

            // Start node occupancy check — user's Isoccupy
            if (!TryScheduleNodeArrival(
                    startNode.NodeId,
                    startTime,
                    maxWait,
                    out float effectiveStartTime))
            {
                return null;
            }
            bestArrival[startNode.NodeId] = effectiveStartTime;

            var heap = new MinHeap<float, string>();
            heap.Push(effectiveStartTime, startNode.NodeId);

            while (heap.Count > 0)
            {
                var (curTime, current) = heap.Pop();
                if (curTime > bestArrival.GetValueOrDefault(current, float.MaxValue)) continue;
                if (current == endNode.NodeId) break;

                if (!adjacency.ContainsKey(current)) continue;
                var curNode = nodes.First(n => n.NodeId == current);

                foreach (var edge in adjacency[current])
                {
                    string neighbor = edge.ToNodeId;
                    var toNode = nodes.FirstOrDefault(n => n.NodeId == neighbor);
                    if (toNode == null) continue;

                    float travelTime = Vector3.Distance(curNode.Position, toNode.Position) / speed;
                    float departTime = bestArrival[current];

                    if (!TryScheduleTransition(
                            current,
                            edge.EdgeId,
                            neighbor,
                            departTime,
                            travelTime,
                            maxWait,
                            out ReservationTransitionSearchResult transition))
                    {
                        continue;
                    }

                    float effectiveArrival = transition.ArrivalTime;

                    // Relaxation
                    if (effectiveArrival < bestArrival.GetValueOrDefault(neighbor, float.MaxValue))
                    {
                        bestArrival[neighbor] = effectiveArrival;
                        prevNode[neighbor] = current;
                        prevEdge[neighbor] = edge;
                        heap.Push(effectiveArrival, neighbor);
                    }
                }
            }

            if (!prevNode.ContainsKey(endNode.NodeId) && startNode.NodeId != endNode.NodeId)
            {
                Debug.LogWarning($"[PathPlanner] No landing approach path: all routes from anchor {anchorNodeId} pruned.");
                return null;
            }

            // Reconstruct into PathSteps.
            var stepsRev = new List<PathStep>();
            string cur2 = endNode.NodeId;

            {
                var node = nodes.FirstOrDefault(n => n.NodeId == cur2);
                stepsRev.Add(new PathStep
                {
                    NodeId = cur2,
                    IncomingEdgeId = null,
                    ReachTime = bestArrival[cur2],
                    WaitTime = 0f,
                    Position = node != null ? node.Position : Vector3.zero,
                });
            }

            while (cur2 != startNode.NodeId)
            {
                if (!prevNode.ContainsKey(cur2)) break;
                string pred = prevNode[cur2];
                var predNode = nodes.FirstOrDefault(n => n.NodeId == pred);
                var curNode = nodes.FirstOrDefault(n => n.NodeId == cur2);
                float travelTime = Vector3.Distance(predNode.Position, curNode.Position) / speed;

                float waitAtPred = bestArrival[cur2] - bestArrival[pred] - travelTime;
                if (waitAtPred < 1e-4f) waitAtPred = 0f;

                stepsRev.Last().IncomingEdgeId = prevEdge.ContainsKey(cur2) ? prevEdge[cur2].EdgeId : null;

                stepsRev.Add(new PathStep
                {
                    NodeId = pred,
                    IncomingEdgeId = null,
                    ReachTime = bestArrival[pred],
                    WaitTime = waitAtPred,
                    Position = predNode != null ? predNode.Position : Vector3.zero,
                });
                cur2 = pred;
            }
            stepsRev.Reverse();

            return new TimedPath
            {
                Steps = stepsRev,
                FinalArrivalTime = bestArrival[endNode.NodeId] + travelFromLast,
                StartTime = startTime
            };
        }

        /// <summary>
        /// Convert TimedPath to List&lt;RampPoint&gt; for approach network.
        /// Direct 1:1 mapping — WaitTime already includes all delays computed during reconstruction.
        /// </summary>
        private List<RampPoint> TimedPathToRampPoints(TimedPath path)
        {
            var points = new List<RampPoint>();
            if (path == null || path.Steps == null) return points;

            foreach (var step in path.Steps)
            {
                points.Add(new RampPoint
                {
                    Point = step.Position,
                    WaitTime = step.WaitTime,
                    ReachTime = step.ReachTime,
                    CheckpointType = null
                });
            }

            return points;
        }

        // ====================================================================
        // HS INTERNAL PATH — Time-window-aware Dijkstra (refactored)
        // ====================================================================
        private TimedPath FindHSPathToCorridorTimeAware(string startVertexId, string corridorId,
            string hsId, float startTime, float speed)
        {
            if (!sim.HoldingStructures.TryGetValue(hsId, out var hs)) return null;
            if (!sim.Corridors.TryGetValue(corridorId, out var corData)) return null;

            string targetVertexId = null;
            foreach (var ramp in hs.Ramps)
            {
                if (ramp.LinkCorridors != null && ramp.LinkCorridors.Any(lc => lc.Item1 == corridorId))
                { targetVertexId = ramp.FromVertexId; break; }
                if (ramp.RampId == corData.LinkRampId)
                { targetVertexId = ramp.FromVertexId; break; }
            }
            if (string.IsNullOrEmpty(targetVertexId)) return null;

            return FindHSPathTimeAware(startVertexId, targetVertexId, hs, startTime, speed);
        }

        private TimedPath FindHSPathFromCorridorTimeAware(string corridorId, string targetVertexId,
            string hsId, float startTime, float speed)
        {
            if (!sim.HoldingStructures.TryGetValue(hsId, out var hs)) return null;
            if (!sim.Corridors.TryGetValue(corridorId, out var corData)) return null;

            string startVertexId = null;
            foreach (var ramp in hs.Ramps)
            {
                if (ramp.LinkCorridors != null && ramp.LinkCorridors.Any(lc => lc.Item1 == corridorId))
                { startVertexId = ramp.FromVertexId; break; }
                if (ramp.RampId == corData.LinkRampId)
                { startVertexId = ramp.FromVertexId; break; }
            }
            if (string.IsNullOrEmpty(startVertexId)) return null;

            return FindHSPathTimeAware(startVertexId, targetVertexId, hs, startTime, speed);
        }

        /// <summary>
        /// Time-window-aware Dijkstra on the HS explicit ramp graph.
        /// Circle arcs are represented by ramps whose LinkCircleId selects arc geometry;
        /// no implicit edge is allowed because every traversed edge must be reservable.
        /// Fixed: ramp wait keyed by (source, target) pair to prevent overwrites.
        /// Uses MinHeap and maxWaitPerResource pruning.
        /// </summary>
        private TimedPath FindHSPathTimeAware(string startId, string endId, HoldingStructure hs,
            float startTime, float speed)
        {
            float maxWait = sim.config != null ? sim.config.maxWaitPerResource : 60f;

            // Build adjacency: directed
            var adjacency = new Dictionary<string, List<(string neighbor, float cost, string rampId)>>();
            foreach (var v in hs.Vertices)
                adjacency[v.VertexId] = new List<(string, float, string)>();

            // Ramps: FromVertex → ToVertex (one-way)
            foreach (var ramp in hs.Ramps)
            {
                if (string.IsNullOrEmpty(ramp.RampId))
                {
                    Debug.LogError($"[PathPlanner] Holding structure {hs.HoldingStructureId} has a ramp " +
                                   $"without a resource ID ({ramp.FromVertexId} -> {ramp.ToVertexId}); skipped.");
                    continue;
                }
                if (!adjacency.ContainsKey(ramp.FromVertexId) || !adjacency.ContainsKey(ramp.ToVertexId))
                    continue;
                var fromV = hs.Vertices.FirstOrDefault(v => v.VertexId == ramp.FromVertexId);
                var toV = hs.Vertices.FirstOrDefault(v => v.VertexId == ramp.ToVertexId);
                if (fromV == null || toV == null) continue;
                float cost;
                if (!string.IsNullOrEmpty(ramp.LinkCircleId))
                {
                    var circle = hs.Circles.FirstOrDefault(c => c.CircleId == ramp.LinkCircleId);
                    if (circle != null)
                        cost = ComputeArcLength(fromV.Position, toV.Position, circle.Center, circle.Radius, ramp.Direction);
                    else
                        cost = Vector3.Distance(fromV.Position, toV.Position);
                }
                else
                {
                    cost = Vector3.Distance(fromV.Position, toV.Position);
                }
                adjacency[ramp.FromVertexId].Add((ramp.ToVertexId, cost, ramp.RampId));
            }

            // Validate that every directed circle segment has an explicit, reservable ramp.
            // Previously an extra null-ID chord was inserted here. Because the chord is shorter
            // than the arc, Dijkstra selected it and final booking silently skipped the traversal.
            foreach (var circle in hs.Circles)
            {
                if (circle.CoveredVertices == null || circle.CoveredVertices.Count < 2) continue;
                for (int i = 0; i < circle.CoveredVertices.Count; i++)
                {
                    string vA = circle.CoveredVertices[i];
                    string vB = circle.CoveredVertices[(i + 1) % circle.CoveredVertices.Count];
                    int explicitRampCount = hs.Ramps.Count(ramp =>
                        ramp.FromVertexId == vA &&
                        ramp.ToVertexId == vB &&
                        !string.IsNullOrEmpty(ramp.RampId));

                    if (explicitRampCount == 0)
                    {
                        Debug.LogError($"[PathPlanner] Holding structure {hs.HoldingStructureId}, " +
                                       $"circle {circle.CircleId} is missing an explicit ramp " +
                                       $"for {vA} -> {vB}; that segment is disconnected.");
                    }
                    else if (explicitRampCount > 1)
                    {
                        Debug.LogWarning($"[PathPlanner] Holding structure {hs.HoldingStructureId}, " +
                                         $"circle {circle.CircleId} has {explicitRampCount} ramps " +
                                         $"for {vA} -> {vB}; verify the duplicate definitions.");
                    }
                }
            }

            // Time-window-aware Dijkstra with heap
            var bestArrival = new Dictionary<string, float>();
            var prevNode = new Dictionary<string, string>();
            // Also store incoming ramp ID keyed by target
            var incomingRamp = new Dictionary<string, string>();

            foreach (var v in hs.Vertices)
                bestArrival[v.VertexId] = float.MaxValue;

            // Start vertex occupancy check — user's Isoccupy
            if (!TryScheduleNodeArrival(
                    startId,
                    startTime,
                    maxWait,
                    out float effectiveStartVertex))
            {
                return null;
            }
            bestArrival[startId] = effectiveStartVertex;

            var heap = new MinHeap<float, string>();
            heap.Push(effectiveStartVertex, startId);

            while (heap.Count > 0)
            {
                var (curTime, current) = heap.Pop();
                if (curTime > bestArrival.GetValueOrDefault(current, float.MaxValue)) continue;
                if (current == endId) break;

                foreach (var (neighbor, cost, rampId) in adjacency[current])
                {
                    var toVertex = hs.Vertices.FirstOrDefault(v => v.VertexId == neighbor);
                    if (toVertex == null) continue;

                    float travelTime = cost / speed;
                    float departTime = bestArrival[current];

                    if (!TryScheduleTransition(
                            current,
                            rampId,
                            neighbor,
                            departTime,
                            travelTime,
                            maxWait,
                            out ReservationTransitionSearchResult transition))
                    {
                        continue;
                    }

                    float effectiveArrival = transition.ArrivalTime;

                    // Relaxation
                    if (effectiveArrival < bestArrival.GetValueOrDefault(neighbor, float.MaxValue))
                    {
                        bestArrival[neighbor] = effectiveArrival;
                        prevNode[neighbor] = current;
                        if (!string.IsNullOrEmpty(rampId))
                            incomingRamp[neighbor] = rampId;
                        heap.Push(effectiveArrival, neighbor);
                    }
                }
            }

            if (!prevNode.ContainsKey(endId) && startId != endId)
            {
                Debug.LogWarning($"[PathPlanner] No HS path: all routes from {startId} to {endId} pruned " +
                               $"(startTime={startTime:F1}s, maxWait={maxWait:F1}s).");
                return null;
            }

            // Reconstruct into PathSteps.
            var stepsRev = new List<PathStep>();
            string cur = endId;

            {
                var vtx = hs.Vertices.FirstOrDefault(v => v.VertexId == cur);
                stepsRev.Add(new PathStep
                {
                    NodeId = cur,
                    IncomingEdgeId = null,
                    ReachTime = bestArrival[cur],
                    WaitTime = 0f,
                    Position = vtx != null ? vtx.Position : Vector3.zero,
                });
            }

            while (cur != startId)
            {
                if (!prevNode.ContainsKey(cur)) break;
                string pred = prevNode[cur];
                var predVtx = hs.Vertices.FirstOrDefault(v => v.VertexId == pred);
                var curVtx = hs.Vertices.FirstOrDefault(v => v.VertexId == cur);

                // Use arc length for circle ramps, chord distance for straight
                string incRampId = incomingRamp.GetValueOrDefault(cur, null);
                if (string.IsNullOrEmpty(incRampId))
                {
                    Debug.LogError($"[PathPlanner] Rejected unreservable HS edge {pred} -> {cur} " +
                                   $"in {hs.HoldingStructureId}: no ramp resource ID was recorded.");
                    return null;
                }
                float arcDist = Vector3.Distance(predVtx.Position, curVtx.Position);
                if (!string.IsNullOrEmpty(incRampId))
                {
                    var incRamp = hs.Ramps.FirstOrDefault(r => r.RampId == incRampId);
                    if (incRamp != null && !string.IsNullOrEmpty(incRamp.LinkCircleId))
                    {
                        var incCircle = hs.Circles.FirstOrDefault(c => c.CircleId == incRamp.LinkCircleId);
                        if (incCircle != null)
                            arcDist = ComputeArcLength(predVtx.Position, curVtx.Position,
                                incCircle.Center, incCircle.Radius, incRamp.Direction);
                    }
                }
                float travelTime = arcDist / speed;

                float waitAtPred = bestArrival[cur] - bestArrival[pred] - travelTime;
                if (waitAtPred < 1e-4f) waitAtPred = 0f;

                stepsRev.Last().IncomingEdgeId = incRampId;

                // Store arc waypoints so trajectory follows the circle arc
                if (!string.IsNullOrEmpty(incRampId))
                {
                    var incRamp2 = hs.Ramps.FirstOrDefault(r => r.RampId == incRampId);
                    if (incRamp2 != null && !string.IsNullOrEmpty(incRamp2.LinkCircleId))
                    {
                        var incCircle2 = hs.Circles.FirstOrDefault(c => c.CircleId == incRamp2.LinkCircleId);
                        if (incCircle2 != null)
                        {
                            stepsRev.Last().ArcWaypoints = ComputeArcPositions(
                                predVtx.Position, curVtx.Position,
                                incCircle2.Center, incCircle2.Radius, incRamp2.Direction);
                        }
                    }
                }

                stepsRev.Add(new PathStep
                {
                    NodeId = pred,
                    IncomingEdgeId = null,
                    ReachTime = bestArrival[pred],
                    WaitTime = waitAtPred,
                    Position = predVtx != null ? predVtx.Position : Vector3.zero,
                });
                cur = pred;
            }
            stepsRev.Reverse();

            return new TimedPath
            {
                Steps = stepsRev,
                FinalArrivalTime = bestArrival[endId],
                StartTime = startTime
            };
        }

        /// <summary>
        /// Convert TimedPath to List&lt;RampPoint&gt; for HS internal graph.
        /// Expands arc waypoints (circle ramps) into discrete trajectory points.
        /// WaitTime already includes all delays computed during reconstruction.
        /// </summary>
        private List<RampPoint> HSTimedPathToRampPoints(TimedPath path)
        {
            var points = new List<RampPoint>();
            if (path == null || path.Steps == null) return points;

            for (int i = 0; i < path.Steps.Count; i++)
            {
                var step = path.Steps[i];

                if (i > 0 && step.ArcWaypoints != null && step.ArcWaypoints.Count >= 2)
                {
                    // Arc ramp: interpolate along the arc waypoints
                    var prevStep = path.Steps[i - 1];
                    float departTime = prevStep.ReachTime + prevStep.WaitTime;
                    float arriveTime = step.ReachTime;
                    float totalSpan = arriveTime - departTime;

                    for (int j = 0; j < step.ArcWaypoints.Count; j++)
                    {
                        float t = j / (float)(step.ArcWaypoints.Count - 1);
                        float tReach = departTime + t * totalSpan;
                        float tWait = (j == step.ArcWaypoints.Count - 1) ? step.WaitTime : 0f;
                        points.Add(new RampPoint
                        {
                            Point = step.ArcWaypoints[j],
                            WaitTime = tWait,
                            ReachTime = tReach,
                            CheckpointType = null
                        });
                    }
                }
                else
                {
                    points.Add(new RampPoint
                    {
                        Point = step.Position,
                        WaitTime = step.WaitTime,
                        ReachTime = step.ReachTime,
                        CheckpointType = null
                    });
                }
            }

            return points;
        }

        // ====================================================================
        // POST-RECONSTRUCTION OCCUPANCY VERIFICATION
        // ====================================================================
        /// <summary>
        /// Re-check the exact reconstructed node intervals before trajectory assembly.
        /// The atomic final booking performs the authoritative validation; this earlier
        /// check provides a local diagnostic if reconstruction and search ever diverge.
        /// </summary>
        private bool VerifyPathExtendedStay(TimedPath approachPath, TimedPath hsPath)
        {
            float buf = sim.config != null ? sim.config.resourceBuffer : 2f;
            float nodeDwell = sim.config != null ? sim.config.nodeDwellTime : 1.0f;

            if (approachPath != null)
            {
                foreach (var step in approachPath.Steps)
                {
                    float intervalStart = step.ReachTime - buf;
                    float intervalEnd = step.ReachTime + step.WaitTime + nodeDwell + buf;
                    if (HasOccupancyConflict(step.NodeId, intervalStart, intervalEnd))
                    {
                        Debug.LogWarning($"[PathPlanner] Reconstructed occupancy conflict at approach node " +
                            $"{step.NodeId}: [{intervalStart:F2},{intervalEnd:F2})");
                        return false;
                    }
                }
            }

            if (hsPath != null)
            {
                foreach (var step in hsPath.Steps)
                {
                    float intervalStart = step.ReachTime - buf;
                    float intervalEnd = step.ReachTime + step.WaitTime + nodeDwell + buf;
                    if (HasOccupancyConflict(step.NodeId, intervalStart, intervalEnd))
                    {
                        Debug.LogWarning($"[PathPlanner] Reconstructed occupancy conflict at HS vertex " +
                            $"{step.NodeId}: [{intervalStart:F2},{intervalEnd:F2})");
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true if any existing booking overlaps with [startTime, endTime).
        /// </summary>
        private bool HasOccupancyConflict(string resourceId, float startTime, float endTime)
        {
            if (!sim.TryGetOccupiedWindows(resourceId, out var windows))
                return false;

            return ReservationWindowSolver.HasConflict(
                ToReservationWindows(windows),
                startTime,
                endTime);
        }

        // ====================================================================
        // TIME-WINDOW BOOKING (derived directly from TimedPath — single source of truth)
        // ====================================================================
        /// <summary>
        /// Book takeoff resources from TimedPath steps.
        /// Node i: occupied [ReachTime - buf, ReachTime + WaitTime + buf]
        /// Edge i→i+1: occupied [DepartTime_i - buf, ReachTime_{i+1} + buf]
        ///   where DepartTime_i = ReachTime_i + WaitTime_i
        /// </summary>
        private bool BookTakeoffResources(string aircraftId, Helipad originHelipad,
            NetConnectedCorridor entryCor,
            TimedPath approachPath, TimedPath hsPath, List<RampPoint> takeoffTraj)
        {
            string acId = aircraftId;
            float buf = sim.config != null ? sim.config.resourceBuffer : 2f;
            float dwell = sim.config != null ? sim.config.nodeDwellTime : 3f;
            var requests = new List<ResourceReservationRequest>();

            // Trajectory layout:
            //   [0] = originCenter (helipad, not booked here)
            //   [1 .. N_appr] = approach nodes (from TimedPath)
            //   [N_appr+1 .. N_appr+N_hs] = HS vertices (from TimedPath)
            //   remaining = corridor Bezier points
            int N_appr = approachPath != null ? approachPath.Steps.Count : 0;
            int N_hs = hsPath != null ? hsPath.Steps.Count : 0;

            // The aircraft physically occupies its origin helipad while waiting for
            // the first approach-node slot. Co-located-resource propagation also
            // reserves an approach node that shares the helipad centre, preventing
            // another path from crossing a waiting aircraft.
            if (originHelipad != null && takeoffTraj.Count > 0)
            {
                RampPoint originPoint = takeoffTraj[0];
                requests.Add(new ResourceReservationRequest(
                    originHelipad.HelipadId,
                    originPoint.ReachTime - buf,
                    originPoint.ReachTime + originPoint.WaitTime + buf));
            }

            // ---- Book approach nodes and edges from TimedPath ----
            if (approachPath != null && N_appr > 0)
            {
                for (int i = 0; i < N_appr; i++)
                {
                    var step = approachPath.Steps[i];
                    float departTime = step.ReachTime + step.WaitTime;

                    requests.Add(new ResourceReservationRequest(
                        step.NodeId,
                        step.ReachTime - buf,
                        departTime + dwell + buf));

                    // Book edge to next node
                    if (i + 1 < N_appr)
                    {
                        var nextStep = approachPath.Steps[i + 1];
                        if (!string.IsNullOrEmpty(nextStep.IncomingEdgeId))
                        {
                            requests.Add(new ResourceReservationRequest(
                                nextStep.IncomingEdgeId,
                                departTime - buf,
                                nextStep.ReachTime + buf));
                        }
                    }
                }
            }

            // ---- Book HS vertices and ramps from TimedPath ----
            if (hsPath != null && N_hs > 0)
            {
                for (int i = 0; i < N_hs; i++)
                {
                    var step = hsPath.Steps[i];
                    float departTime = step.ReachTime + step.WaitTime;

                    requests.Add(new ResourceReservationRequest(
                        step.NodeId,
                        step.ReachTime - buf,
                        departTime + dwell + buf));

                    // Book ramp to next vertex
                    if (i + 1 < N_hs)
                    {
                        var nextStep = hsPath.Steps[i + 1];
                        if (!string.IsNullOrEmpty(nextStep.IncomingEdgeId))
                        {
                            requests.Add(new ResourceReservationRequest(
                                nextStep.IncomingEdgeId,
                                departTime - buf,
                                nextStep.ReachTime + buf));
                        }
                    }
                }
            }

            // ---- Book entry corridor ----
            int corridorStartIdx = 1 + N_appr + N_hs;
            if (takeoffTraj.Count > corridorStartIdx)
            {
                float corrStartTime = takeoffTraj[corridorStartIdx].ReachTime;
                float corrEndTime = takeoffTraj[takeoffTraj.Count - 1].ReachTime + buf;
                requests.Add(new ResourceReservationRequest(
                    entryCor.CorridorId,
                    corrStartTime - buf,
                    corrEndTime));
            }

            if (!sim.TryBookVertiportResources(acId, requests, out string failureReason))
            {
                Debug.LogWarning($"[Reservation] Takeoff batch rejected for {acId}: {failureReason}");
                return false;
            }

            if (originHelipad != null && takeoffTraj.Count > 0)
            {
                RampPoint originPoint = takeoffTraj[0];
                float standClearTime = originPoint.ReachTime + originPoint.WaitTime + buf;
                sim.ReleaseHelipadStandReservation(
                    acId,
                    originHelipad.HelipadId,
                    standClearTime);
            }

            Debug.Log($"[PathPlanner] Booked takeoff resources for {acId}: " +
                      $"Approach={N_appr} nodes, HS={N_hs} vertices, total={requests.Count} intervals.");
            return true;
        }

        /// <summary>
        /// Book landing resources from TimedPath steps.
        /// Node i: occupied [ReachTime - buf, ReachTime + WaitTime + buf]
        /// Edge i→i+1: occupied [DepartTime_i - buf, ReachTime_{i+1} + buf]
        /// </summary>
        private bool BookLandingResources(string aircraftId, Helipad destinationHelipad,
            NetConnectedCorridor exitCor,
            TimedPath hsPath, TimedPath approachPath, List<RampPoint> landingTraj)
        {
            string acId = aircraftId;
            float buf = sim.config != null ? sim.config.resourceBuffer : 2f;
            float dwell = sim.config != null ? sim.config.nodeDwellTime : 3f;
            var requests = new List<ResourceReservationRequest>();

            int N_hs_in_traj = hsPath != null && hsPath.Steps.Count > 1 ? hsPath.Steps.Count - 1 : 0;
            int N_appr = approachPath != null ? approachPath.Steps.Count : 0;

            // A landed aircraft remains physically on its destination helipad while
            // a chained mission is being planned. Reserve that stand, and therefore
            // its co-located approach node, for the entire permitted retry period.
            // A successful takeoff truncates this provisional window precisely.
            if (destinationHelipad != null && landingTraj.Count > 0)
            {
                float arrivalTime = landingTraj[landingTraj.Count - 1].ReachTime;
                float maxStandWait = sim.config != null
                    ? sim.config.maxHelipadWaitTime
                    : 120f;
                requests.Add(new ResourceReservationRequest(
                    destinationHelipad.HelipadId,
                    arrivalTime - buf,
                    HelipadStandReservation.CalculateEnd(arrivalTime, maxStandWait, buf),
                    isStandingReservation: true));
            }

            // Book exit corridor
            if (hsPath != null && landingTraj.Count > 0)
            {
                int hsFirstIdx = landingTraj.Count - N_hs_in_traj - N_appr - 1;
                if (hsFirstIdx > 0)
                {
                    float corrStart = landingTraj[0].ReachTime - buf;
                    float corrEnd = landingTraj[hsFirstIdx - 1].ReachTime + buf;
                    requests.Add(new ResourceReservationRequest(
                        exitCor.CorridorId,
                        corrStart,
                        corrEnd));
                }
            }

            // ---- Book HS vertices and ramps from TimedPath ----
            if (hsPath != null && N_hs_in_traj > 0)
            {
                // hsPath.Steps[0] = vertex 0 (corridor-connected, not in landing trajectory)
                // hsPath.Steps[1..N-1] = vertices in landing trajectory
                for (int i = 1; i < hsPath.Steps.Count; i++)
                {
                    var step = hsPath.Steps[i];
                    float departTime = step.ReachTime + step.WaitTime;

                    requests.Add(new ResourceReservationRequest(
                        step.NodeId,
                        step.ReachTime - buf,
                        departTime + dwell + buf));

                    // Book ramp from this vertex to next
                    if (i + 1 < hsPath.Steps.Count)
                    {
                        var nextStep = hsPath.Steps[i + 1];
                        if (!string.IsNullOrEmpty(nextStep.IncomingEdgeId))
                        {
                            requests.Add(new ResourceReservationRequest(
                                nextStep.IncomingEdgeId,
                                departTime - buf,
                                nextStep.ReachTime + buf));
                        }
                    }
                }

                // Book vertex 0 (corridor-connected, not in landing trajectory)
                if (hsPath.Steps.Count > 1)
                {
                    var step0 = hsPath.Steps[0];
                    float vert0Departure = step0.ReachTime + step0.WaitTime;
                    requests.Add(new ResourceReservationRequest(
                        step0.NodeId,
                        step0.ReachTime - buf,
                        vert0Departure + dwell + buf));
                    var step1 = hsPath.Steps[1];
                    if (!string.IsNullOrEmpty(step1.IncomingEdgeId))
                    {
                        requests.Add(new ResourceReservationRequest(
                            step1.IncomingEdgeId,
                            vert0Departure - buf,
                            step1.ReachTime + buf));
                    }
                }
            }

            // ---- Book approach nodes and edges from TimedPath ----
            if (approachPath != null && N_appr > 0)
            {
                for (int i = 0; i < N_appr; i++)
                {
                    var step = approachPath.Steps[i];
                    float departTime = step.ReachTime + step.WaitTime;

                    requests.Add(new ResourceReservationRequest(
                        step.NodeId,
                        step.ReachTime - buf,
                        departTime + dwell + buf));

                    if (i + 1 < N_appr)
                    {
                        var nextStep = approachPath.Steps[i + 1];
                        if (!string.IsNullOrEmpty(nextStep.IncomingEdgeId))
                        {
                            requests.Add(new ResourceReservationRequest(
                                nextStep.IncomingEdgeId,
                                departTime - buf,
                                nextStep.ReachTime + buf));
                        }
                    }
                }
            }

            if (!sim.TryBookVertiportResources(acId, requests, out string failureReason))
            {
                Debug.LogWarning($"[Reservation] Landing batch rejected for {acId}: {failureReason}");
                return false;
            }

            Debug.Log($"[PathPlanner] Booked landing resources for {acId}: " +
                      $"HS={N_hs_in_traj} verts, Appr={N_appr} nodes, total={requests.Count} intervals.");
            return true;
        }

        // ====================================================================
        // ROAD NETWORK PATH (A*) — Unchanged
        // ====================================================================
        public List<Edge> FindEdgeSequence(string entryCorridorId, string exitCorridorId)
        {
            if (!sim.Corridors.ContainsKey(entryCorridorId) || !sim.Corridors.ContainsKey(exitCorridorId))
            {
                Debug.LogWarning("[PathPlanner] Invalid corridor IDs for edge search.");
                return null;
            }

            string entryLaneId = sim.Corridors[entryCorridorId].LinkLaneId;
            string exitLaneId = sim.Corridors[exitCorridorId].LinkLaneId;

            if (!sim.Lanes.ContainsKey(entryLaneId) || !sim.Lanes.ContainsKey(exitLaneId))
            {
                Debug.LogWarning("[PathPlanner] Corridor linked lane not found.");
                return null;
            }

            if (!sim.LaneToEdgeMap.ContainsKey(entryLaneId) || !sim.LaneToEdgeMap.ContainsKey(exitLaneId))
            {
                Debug.LogWarning("[PathPlanner] Lane-to-edge mapping missing.");
                return null;
            }

            string entryEdgeId = sim.LaneToEdgeMap[entryLaneId];
            string exitEdgeId = sim.LaneToEdgeMap[exitLaneId];

            if (entryEdgeId == exitEdgeId)
                return new List<Edge> { sim.Edges[entryEdgeId] };

            Lane entryLane = sim.Lanes[entryLaneId];
            Vector3 entryLaneEnd = entryLane.Centerline.Last();
            Edge entryEdge = sim.Edges[entryEdgeId];
            string startNodeId = ClosestNode(entryLaneEnd, entryEdge);

            Lane exitLane = sim.Lanes[exitLaneId];
            Vector3 exitLaneStart = exitLane.Centerline.First();
            Edge exitEdge = sim.Edges[exitEdgeId];
            string endNodeId = ClosestNode(exitLaneStart, exitEdge);

            if (startNodeId == endNodeId)
                return new List<Edge> { entryEdge, exitEdge };

            var graph = BuildRoadGraph(entryEdgeId, exitEdgeId);
            var path = AStarSearch(graph, startNodeId, endNodeId, entryEdgeId, exitEdgeId);
            return path;
        }

        private string ClosestNode(Vector3 point, Edge edge)
        {
            string closest = null;
            float minDist = float.MaxValue;
            foreach (var nodeId in edge.LinkNodesId)
            {
                if (!sim.Nodes.ContainsKey(nodeId)) continue;
                float d = Vector3.Distance(sim.Nodes[nodeId].Position, point);
                if (d < minDist) { minDist = d; closest = nodeId; }
            }
            return closest;
        }

        private Dictionary<string, List<string>> BuildRoadGraph(string entryEdgeId, string exitEdgeId)
        {
            var graph = new Dictionary<string, List<string>>();
            foreach (var edge in sim.Edges.Values)
            {
                if (edge.LinkNodesId == null || edge.LinkNodesId.Count < 2) continue;
                string n1 = edge.LinkNodesId[0];
                string n2 = edge.LinkNodesId[1];
                if (!graph.ContainsKey(n1)) graph[n1] = new List<string>();
                if (!graph.ContainsKey(n2)) graph[n2] = new List<string>();
                if (!graph[n1].Contains(n2)) graph[n1].Add(n2);
                if (!graph[n2].Contains(n1)) graph[n2].Add(n1);
            }
            return graph;
        }

        private List<Edge> AStarSearch(Dictionary<string, List<string>> graph,
            string startNodeId, string endNodeId, string entryEdgeId, string exitEdgeId)
        {
            if (!sim.Nodes.ContainsKey(startNodeId) || !sim.Nodes.ContainsKey(endNodeId))
                return null;

            var openSet = new List<string> { startNodeId };
            var cameFrom = new Dictionary<string, string>();
            var gScore = new Dictionary<string, float> { [startNodeId] = 0f };
            var fScore = new Dictionary<string, float>
            {
                [startNodeId] = Vector3.Distance(sim.Nodes[startNodeId].Position, sim.Nodes[endNodeId].Position)
            };

            while (openSet.Count > 0)
            {
                string current = openSet.OrderBy(n => fScore.GetValueOrDefault(n, float.MaxValue)).First();
                if (current == endNodeId)
                    return ReconstructEdgePath(cameFrom, current, entryEdgeId, exitEdgeId);

                openSet.Remove(current);
                if (graph.ContainsKey(current))
                {
                    foreach (var neighbor in graph[current])
                    {
                        if (!sim.Nodes.ContainsKey(neighbor)) continue;
                        float tentativeG = gScore[current] +
                            Vector3.Distance(sim.Nodes[current].Position, sim.Nodes[neighbor].Position);
                        if (tentativeG < gScore.GetValueOrDefault(neighbor, float.MaxValue))
                        {
                            cameFrom[neighbor] = current;
                            gScore[neighbor] = tentativeG;
                            fScore[neighbor] = gScore[neighbor] +
                                Vector3.Distance(sim.Nodes[neighbor].Position, sim.Nodes[endNodeId].Position);
                            if (!openSet.Contains(neighbor))
                                openSet.Add(neighbor);
                        }
                    }
                }
            }

            Debug.LogWarning($"[PathPlanner] A* failed: {startNodeId} -> {endNodeId}");
            return null;
        }

        private List<Edge> ReconstructEdgePath(Dictionary<string, string> cameFrom, string currentId,
            string entryEdgeId, string exitEdgeId)
        {
            var nodePath = new List<string> { currentId };
            while (cameFrom.ContainsKey(currentId))
            {
                currentId = cameFrom[currentId];
                nodePath.Add(currentId);
            }
            nodePath.Reverse();

            var edgePath = new List<Edge>();
            for (int i = 0; i < nodePath.Count - 1; i++)
            {
                string fromN = nodePath[i];
                string toN = nodePath[i + 1];
                var edge = sim.Edges.Values.FirstOrDefault(e =>
                    e.LinkNodesId != null && e.LinkNodesId.Count >= 2 &&
                    ((e.LinkNodesId[0] == fromN && e.LinkNodesId[1] == toN) ||
                     (e.LinkNodesId[0] == toN && e.LinkNodesId[1] == fromN)));
                if (edge != null && !edgePath.Any(e => e.EdgeId == edge.EdgeId))
                    edgePath.Add(edge);
            }

            if (sim.Edges.ContainsKey(entryEdgeId) && !edgePath.Any(e => e.EdgeId == entryEdgeId))
                edgePath.Insert(0, sim.Edges[entryEdgeId]);
            if (sim.Edges.ContainsKey(exitEdgeId) && !edgePath.Any(e => e.EdgeId == exitEdgeId))
                edgePath.Add(sim.Edges[exitEdgeId]);

            return edgePath;
        }

        public List<Lane> FindLaneSequence(List<Edge> edgeSequence, string entryCorridorId, string exitCorridorId)
        {
            if (edgeSequence == null || edgeSequence.Count == 0) return null;

            string entryLaneId = sim.Corridors[entryCorridorId].LinkLaneId;
            string exitLaneId = sim.Corridors[exitCorridorId].LinkLaneId;

            if (!sim.Lanes.ContainsKey(entryLaneId) || !sim.Lanes.ContainsKey(exitLaneId))
                return null;

            Lane exitLane = sim.Lanes[exitLaneId];
            string exitEdgeId = sim.LaneToEdgeMap.ContainsKey(exitLaneId)
                ? sim.LaneToEdgeMap[exitLaneId] : null;

            var laneSequence = new List<Lane>();
            Lane currentLane = sim.Lanes[entryLaneId];
            laneSequence.Add(currentLane);

            for (int i = 1; i < edgeSequence.Count; i++)
            {
                Edge currentEdge = edgeSequence[i - 1];
                Edge targetEdge = edgeSequence[i];
                Lane nextLane = FindDirectConnectedLane(currentLane, targetEdge);

                if (nextLane == null)
                {
                    var transition = FindSmartLateralPath(currentLane, currentEdge, targetEdge);
                    if (transition != null && transition.Count > 0)
                    {
                        laneSequence.AddRange(transition);
                        currentLane = transition.Last();
                        nextLane = FindDirectConnectedLane(currentLane, targetEdge);
                    }

                    if (nextLane == null)
                    {
                        Debug.LogWarning($"[PathPlanner] Lane {currentLane.LaneId} cannot connect to target edge {targetEdge.EdgeId}.");
                        return null;
                    }
                }

                laneSequence.Add(nextLane);
                currentLane = nextLane;
            }

            if (laneSequence.Last().LaneId != exitLaneId)
            {
                string lastEdgeId = sim.LaneToEdgeMap.ContainsKey(laneSequence.Last().LaneId)
                    ? sim.LaneToEdgeMap[laneSequence.Last().LaneId] : null;

                if (lastEdgeId != exitEdgeId)
                {
                    Debug.LogWarning($"[PathPlanner] Lane sequence ended on edge {lastEdgeId}, exit on {exitEdgeId}.");
                    return null;
                }

                var endPath = FindLateralPathBetweenLanes(laneSequence.Last(), exitLane);
                if (endPath == null)
                {
                    Debug.LogWarning($"[PathPlanner] Cannot find lateral path to exit lane {exitLaneId}.");
                    return null;
                }
                laneSequence.AddRange(endPath);
            }

            if (laneSequence.Last().LaneId != exitLaneId)
            {
                Debug.LogWarning("[PathPlanner] Lane sequence does not end at exit lane.");
                return null;
            }

            return laneSequence;
        }

        /// <summary>
        /// Dijkstra on the directed lane graph. Normal lane ToLanesId links remain
        /// available, while each LaneConnectionCorridor contributes
        /// FromLane -> virtual connector lane -> ToLane. This lets routes move
        /// between altitude layers even when the legacy edge graph is disconnected.
        /// </summary>
        public List<Lane> FindLaneSequenceWithLayerConnections(
            string startLaneId,
            string targetLaneId)
        {
            float laneChangePenalty = sim.config != null
                ? Mathf.Max(sim.config.safeInterval, sim.config.laneChangeDistance)
                : 10f;
            List<Lane> route = LaneRoutingUtility.FindShortestPath(
                sim.Lanes,
                sim.Edges.Values,
                sim.LaneConnectionCorridors.Values,
                startLaneId,
                targetLaneId,
                laneChangePenalty);
            if (route == null)
            {
                Debug.LogWarning($"[PathPlanner] No lane-level route from {startLaneId} to {targetLaneId}, " +
                                 "including adjacent lane changes and layer connectors.");
                return null;
            }

            int layerTransitions = route.Count(lane => lane.LaneType == "LayerConnector");
            Debug.Log($"[PathPlanner] Lane-level route: {route.Count} lanes, " +
                      $"{layerTransitions} layer connector(s).");
            return route;
        }

        private Lane FindDirectConnectedLane(Lane current, Edge target)
        {
            if (current.ToLanesId == null || target.Lanes == null) return null;
            foreach (string toId in current.ToLanesId)
            {
                Lane match = target.Lanes.FirstOrDefault(l => l.LaneId == toId);
                if (match != null) return match;
            }
            return null;
        }

        private List<Lane> FindSmartLateralPath(Lane current, Edge currentEdge, Edge targetEdge)
        {
            var sorted = SortLanesSpatially(currentEdge);
            if (sorted == null) return null;
            var sameDirLanes = sorted.Where(l => SameDirection(current, l)).ToList();
            if (sameDirLanes.Count == 0) return null;
            int curIdx = sameDirLanes.FindIndex(l => l.LaneId == current.LaneId);
            if (curIdx < 0) return null;

            int bestIdx = -1;
            int minDist = int.MaxValue;
            for (int i = 0; i < sameDirLanes.Count; i++)
            {
                if (CanLaneConnectToEdge(sameDirLanes[i], targetEdge))
                {
                    int d = Mathf.Abs(i - curIdx);
                    if (d < minDist) { minDist = d; bestIdx = i; }
                }
            }
            if (bestIdx < 0) return null;

            var path = new List<Lane>();
            int step = bestIdx > curIdx ? 1 : -1;
            for (int k = curIdx + step; ; k += step)
            {
                path.Add(sameDirLanes[k]);
                if (k == bestIdx) break;
            }
            return path;
        }

        private List<Lane> FindLateralPathBetweenLanes(Lane start, Lane end)
        {
            if (start == null || end == null || start.LaneId == end.LaneId) return null;
            if (!SameDirection(start, end))
            {
                Debug.LogWarning($"[PathPlanner] Lateral path rejected: {start.LaneId} and {end.LaneId} have opposite directions.");
                return null;
            }
            if (!sim.LaneToEdgeMap.ContainsKey(start.LaneId) || !sim.LaneToEdgeMap.ContainsKey(end.LaneId))
                return null;

            string startEdgeId = sim.LaneToEdgeMap[start.LaneId];
            string endEdgeId = sim.LaneToEdgeMap[end.LaneId];
            if (startEdgeId != endEdgeId || !sim.Edges.ContainsKey(startEdgeId)) return null;

            var sorted = SortLanesSpatially(sim.Edges[startEdgeId])
                .Where(l => SameDirection(start, l)).ToList();
            int si = sorted.FindIndex(l => l.LaneId == start.LaneId);
            int ei = sorted.FindIndex(l => l.LaneId == end.LaneId);
            if (si < 0 || ei < 0 || si == ei)
            {
                Debug.LogWarning($"[PathPlanner] Lateral path: {start.LaneId}(idx={si}) -> {end.LaneId}(idx={ei}) on edge {startEdgeId}. " +
                                 $"Sorted same-dir lanes: {string.Join(", ", sorted.Select(l => l.LaneId))}");
                return null;
            }

            var path = new List<Lane>();
            int step = ei > si ? 1 : -1;
            for (int k = si + step; ; k += step)
            {
                path.Add(sorted[k]);
                if (k == ei) break;
            }
            return path;
        }

        private bool SameDirection(Lane a, Lane b)
        {
            if (a.Centerline == null || a.Centerline.Count < 2) return true;
            if (b.Centerline == null || b.Centerline.Count < 2) return true;
            Vector3 dirA = (a.Centerline.Last() - a.Centerline.First()).normalized;
            Vector3 dirB = (b.Centerline.Last() - b.Centerline.First()).normalized;
            return Vector3.Dot(dirA, dirB) > 0.5f;
        }

        private List<Lane> SortLanesSpatially(Edge edge)
        {
            if (edge.Lanes == null || edge.Lanes.Count <= 1) return edge.Lanes;
            Vector3 edgeStart = edge.Lanes[0].Centerline[0];
            Vector3 edgeEnd = edge.Lanes[0].Centerline.Last();
            Vector3 forward = (edgeEnd - edgeStart).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            return edge.Lanes.OrderBy(l => Vector3.Dot(l.Centerline[0], right)).ToList();
        }

        private bool CanLaneConnectToEdge(Lane lane, Edge edge)
        {
            if (lane.ToLanesId == null || edge.Lanes == null) return false;
            foreach (var toId in lane.ToLanesId)
            {
                if (edge.Lanes.Any(l => l.LaneId == toId))
                    return true;
            }
            return false;
        }

        // ====================================================================
        // HELPERS
        // ====================================================================
        private string FindHSForCorridor(NetConnectedCorridor cor, string vpId)
        {
            if (!sim.Vertiports.TryGetValue(vpId, out var vp)) return null;
            foreach (var hs in vp.HoldingStructures)
            {
                foreach (var ramp in hs.Ramps)
                {
                    if (ramp.LinkCorridors != null && ramp.LinkCorridors.Any(lc => lc.Item1 == cor.CorridorId))
                        return hs.HoldingStructureId;
                    if (ramp.RampId == cor.LinkRampId)
                        return hs.HoldingStructureId;
                }
            }
            return null;
        }

        /// <summary>Find any HS vertex that has a paired approach network anchor node.</summary>
        private string FindAnchorVertex(string hsId)
        {
            if (!sim.HoldingStructures.TryGetValue(hsId, out var hs)) return null;

            // First: find vertex explicitly linked by an approach node
            foreach (var vp in sim.Vertiports.Values)
            {
                if (vp.ApproachNet?.Nodes == null) continue;
                foreach (var node in vp.ApproachNet.Nodes)
                {
                    if (node.IsAnchor && node.LinkHSId == hsId
                        && !string.IsNullOrEmpty(node.LinkVertexId)
                        && hs.Vertices.Any(v => v.VertexId == node.LinkVertexId))
                        return node.LinkVertexId;
                }
            }

            // Fallback: legacy anchor (vertex with null LinkCircleId)
            var legacy = hs.Vertices.FirstOrDefault(v => string.IsNullOrEmpty(v.LinkCircleId));
            return legacy?.VertexId;
        }

        /// <summary>
        /// Find ALL HS vertices that have a paired approach network anchor node.
        /// Used when the path planner needs to try multiple anchors to find one
        /// with valid approach-network connectivity for the desired travel direction.
        /// </summary>
        private List<string> FindAllAnchorVertices(string hsId)
        {
            var result = new List<string>();
            if (!sim.HoldingStructures.TryGetValue(hsId, out var hs)) return result;

            foreach (var vp in sim.Vertiports.Values)
            {
                if (vp.ApproachNet?.Nodes == null) continue;
                foreach (var node in vp.ApproachNet.Nodes)
                {
                    if (node.IsAnchor && node.LinkHSId == hsId
                        && !string.IsNullOrEmpty(node.LinkVertexId)
                        && hs.Vertices.Any(v => v.VertexId == node.LinkVertexId)
                        && !result.Contains(node.LinkVertexId))
                        result.Add(node.LinkVertexId);
                }
            }

            // Fallback: legacy anchors
            if (result.Count == 0)
            {
                var legacy = hs.Vertices.Where(v => string.IsNullOrEmpty(v.LinkCircleId))
                    .Select(v => v.VertexId).ToList();
                result.AddRange(legacy);
            }
            return result;
        }

        /// <summary>
        /// Find ALL approach anchor node IDs for a given HS in a vertiport.
        /// </summary>
        private List<string> FindAllApproachAnchorNodes(string hsId, string vpId)
        {
            var result = new List<string>();
            if (!sim.Vertiports.TryGetValue(vpId, out var vp)) return result;
            if (vp.ApproachNet?.Nodes == null) return result;

            foreach (var node in vp.ApproachNet.Nodes)
            {
                if (node.IsAnchor && node.LinkHSId == hsId
                    && !string.IsNullOrEmpty(node.LinkVertexId)
                    && !result.Contains(node.NodeId))
                    result.Add(node.NodeId);
            }

            // Fallback: any anchor for this HS
            if (result.Count == 0)
            {
                foreach (var node in vp.ApproachNet.Nodes)
                {
                    if (node.IsAnchor && node.LinkHSId == hsId)
                        result.Add(node.NodeId);
                }
            }
            return result;
        }

        /// <summary>Find an approach node linked to the given HS. Tries LinkVertexId first.</summary>
        private string FindApproachAnchorNode(string hsId, string vpId)
        {
            if (!sim.Vertiports.TryGetValue(vpId, out var vp)) return null;
            if (vp.ApproachNet?.Nodes == null) return null;
            // Prefer nodes with explicit LinkVertexId
            var anchor = vp.ApproachNet.Nodes.FirstOrDefault(n =>
                n.IsAnchor && n.LinkHSId == hsId && !string.IsNullOrEmpty(n.LinkVertexId));
            if (anchor != null) return anchor.NodeId;
            // Fallback: any anchor for this HS
            anchor = vp.ApproachNet.Nodes.FirstOrDefault(n => n.IsAnchor && n.LinkHSId == hsId);
            return anchor?.NodeId;
        }

        /// <summary>Find approach anchor node linked to a specific HS vertex.</summary>
        private string FindApproachAnchorNodeForVertex(string hsId, string vpId, string vertexId)
        {
            if (!sim.Vertiports.TryGetValue(vpId, out var vp)) return null;
            if (vp.ApproachNet?.Nodes == null) return null;
            var anchor = vp.ApproachNet.Nodes.FirstOrDefault(n =>
                n.IsAnchor && n.LinkHSId == hsId && n.LinkVertexId == vertexId);
            if (anchor != null) return anchor.NodeId;
            // Fallback to any anchor for this HS
            return FindApproachAnchorNode(hsId, vpId);
        }

        private Vector3 GetHelipadCenter(Helipad hp)
        {
            if (hp.Boundary == null || hp.Boundary.Count == 0) return Vector3.zero;
            Vector3 sum = Vector3.zero;
            foreach (var p in hp.Boundary) sum += p;
            return sum / hp.Boundary.Count;
        }

        private List<NetConnectedCorridor> GetCorridorsOfType(string vpId, string corridorType)
        {
            var result = new List<NetConnectedCorridor>();
            if (!sim.Vertiports.TryGetValue(vpId, out var vp)) return result;
            if (vp.Corridors == null) return result;
            foreach (var c in vp.Corridors)
                if (c.CorridorType == corridorType)
                    result.Add(c);
            return result;
        }

        private List<Vector3> SampleCorridorBezier(NetConnectedCorridor cor, bool isEntry)
        {
            var pts = new List<Vector3>();
            Vector3 lanePt = cor.LaneLinkPoint;
            Vector3 rampPt = cor.RampLinkPoint;

            if (!sim.Lanes.TryGetValue(cor.LinkLaneId, out var lane) || lane.Centerline == null || lane.Centerline.Count < 2)
            {
                pts.Add(isEntry ? rampPt : lanePt);
                return pts;
            }

            Vector3 laneDir = GetLaneDirectionAtPoint(lane, lanePt);
            Vector3 rampDir = GetRampDirectionAtPoint(cor.LinkRampId, rampPt);

            float bezierDist = Vector3.Distance(lanePt, rampPt);
            float d = bezierDist * 0.3f;

            Vector3 p0, p1, p2, p3;
            if (isEntry)
            {
                if (Vector3.Dot(rampDir, lanePt - rampPt) < 0) rampDir = -rampDir;
                p0 = rampPt; p3 = lanePt;
                p1 = p0 + rampDir * d;
                p2 = p3 - laneDir * d;
            }
            else
            {
                if (Vector3.Dot(laneDir, rampPt - lanePt) < 0) laneDir = -laneDir;
                p0 = lanePt; p3 = rampPt;
                p1 = p0 + laneDir * d;
                p2 = p3 - rampDir * d;
            }

            int segments = 8;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                pts.Add(EvaluateBezier(p0, p1, p2, p3, t));
            }
            return pts;
        }

        private Vector3 GetLaneDirectionAtPoint(Lane lane, Vector3 point)
        {
            float bestDist = float.MaxValue;
            Vector3 bestDir = Vector3.forward;
            for (int i = 0; i < lane.Centerline.Count - 1; i++)
            {
                Vector3 proj = ProjectPointOnSegment(point, lane.Centerline[i], lane.Centerline[i + 1]);
                float d = Vector3.Distance(point, proj);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestDir = (lane.Centerline[i + 1] - lane.Centerline[i]).normalized;
                }
            }
            return bestDir;
        }

        private Vector3 GetRampDirectionAtPoint(string rampId, Vector3 point)
        {
            if (!sim.Ramps.TryGetValue(rampId, out var ramp)) return Vector3.forward;
            if (!sim.Vertices.TryGetValue(ramp.FromVertexId, out var fromV) ||
                !sim.Vertices.TryGetValue(ramp.ToVertexId, out var toV))
                return Vector3.forward;

            if (string.IsNullOrEmpty(ramp.LinkCircleId))
            {
                return (toV.Position - fromV.Position).normalized;
            }
            else
            {
                if (!sim.Circles.TryGetValue(ramp.LinkCircleId, out var circle)) return Vector3.forward;
                Vector3 center = circle.Center;
                Vector3 radial = point - center;
                radial.y = 0;
                if (radial.magnitude < 0.001f) return Vector3.forward;
                radial.Normalize();
                Vector3 fromDir = fromV.Position - center;
                Vector3 toDir = toV.Position - center;
                fromDir.y = 0; toDir.y = 0;
                float signedAngle = Mathf.Atan2(
                    Vector3.Dot(Vector3.up, Vector3.Cross(fromDir, toDir)),
                    Vector3.Dot(fromDir, toDir));
                Vector3 tangent = Vector3.Cross(Vector3.up, radial);
                if (signedAngle < 0) tangent = -tangent;
                return tangent.normalized;
            }
        }

        private Vector3 EvaluateBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1 - t;
            return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
        }

        private Vector3 ProjectPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            Vector3 ap = point - a;
            float t = Vector3.Dot(ap, ab) / Mathf.Max(ab.sqrMagnitude, 0.0001f);
            t = Mathf.Clamp01(t);
            return a + t * ab;
        }

        // ====================================================================
        // ARC GEOMETRY HELPERS
        // ====================================================================

        /// <summary>
        /// Compute arc length between two vertices on a circle, respecting direction.
        /// </summary>
        private float ComputeArcLength(Vector3 fromPos, Vector3 toPos, Vector3 center,
            float radius, ArcDirection direction)
        {
            Vector3 dirFrom = fromPos - center;
            Vector3 dirTo = toPos - center;
            float angleFrom = Mathf.Atan2(dirFrom.z, dirFrom.x);
            float angleTo = Mathf.Atan2(dirTo.z, dirTo.x);

            float angleDiff = angleTo - angleFrom;
            while (angleDiff < 0f) angleDiff += 2f * Mathf.PI;
            while (angleDiff >= 2f * Mathf.PI) angleDiff -= 2f * Mathf.PI;

            if (direction == ArcDirection.Clockwise)
                angleDiff = angleDiff - 2f * Mathf.PI;   // negative = clockwise
            else if (direction == ArcDirection.Shortest && angleDiff > Mathf.PI)
                angleDiff = angleDiff - 2f * Mathf.PI;

            return Mathf.Abs(angleDiff) * radius;
        }

        /// <summary>
        /// Generate interpolated positions along an arc between two vertices on a circle.
        /// </summary>
        private List<Vector3> ComputeArcPositions(Vector3 fromPos, Vector3 toPos,
            Vector3 center, float radius, ArcDirection direction, int segments = 24)
        {
            var result = new List<Vector3>();

            Vector3 dirFrom = fromPos - center;
            Vector3 dirTo = toPos - center;
            float angleFrom = Mathf.Atan2(dirFrom.z, dirFrom.x);
            float angleTo = Mathf.Atan2(dirTo.z, dirTo.x);

            float angleDiff = angleTo - angleFrom;
            while (angleDiff < 0f) angleDiff += 2f * Mathf.PI;
            while (angleDiff >= 2f * Mathf.PI) angleDiff -= 2f * Mathf.PI;

            if (direction == ArcDirection.Clockwise)
                angleDiff = angleDiff - 2f * Mathf.PI;
            else if (direction == ArcDirection.Shortest && angleDiff > Mathf.PI)
                angleDiff = angleDiff - 2f * Mathf.PI;

            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = angleFrom + angleDiff * t;
                Vector3 pos = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                result.Add(pos);
            }

            return result;
        }
    }
}
