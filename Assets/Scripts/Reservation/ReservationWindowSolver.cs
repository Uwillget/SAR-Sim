using System;
using System.Collections.Generic;

namespace RoadNetworkEditor.Reservations
{
    /// <summary>
    /// Immutable scheduling view of an occupied resource interval.
    /// Runtime state objects are adapted to this small value type at the boundary.
    /// </summary>
    public struct ReservationWindow
    {
        public readonly string AircraftId;
        public readonly float StartTime;
        public readonly float EndTime;

        public ReservationWindow(string aircraftId, float startTime, float endTime)
        {
            AircraftId = aircraftId;
            StartTime = startTime;
            EndTime = endTime;
        }
    }

    /// <summary>
    /// One resource interval requested as part of an atomic booking batch.
    /// </summary>
    public struct ResourceReservationRequest
    {
        public readonly string ResourceId;
        public readonly float StartTime;
        public readonly float EndTime;
        public readonly bool IsStandingReservation;

        public ResourceReservationRequest(
            string resourceId,
            float startTime,
            float endTime,
            bool isStandingReservation = false)
        {
            ResourceId = resourceId;
            StartTime = startTime;
            EndTime = endTime;
            IsStandingReservation = isStandingReservation;
        }
    }

    /// <summary>
    /// Result of searching for the earliest conflict-free reservation interval.
    /// All reservation intervals use half-open semantics: [start, end).
    /// </summary>
    public struct ReservationWindowSearchResult
    {
        public bool Success;
        public float StartTime;
        public float EndTime;
        public float WaitTime;
        public int ConflictsResolved;
        public string FailureReason;
    }

    /// <summary>
    /// Result of scheduling one node-to-node movement with the same buffer and
    /// dwell intervals that final booking will commit.
    /// </summary>
    public struct ReservationTransitionSearchResult
    {
        public bool Success;
        public float DepartureTime;
        public float ArrivalTime;
        public float WaitTime;
        public int Iterations;
        public string FailureReason;
    }

    /// <summary>
    /// Pure time-window scheduling logic shared by path planning and final booking.
    /// The solver deliberately has no Unity lifecycle dependencies so it can be
    /// covered by deterministic EditMode tests.
    /// </summary>
    public static class ReservationWindowSolver
    {
        public const float DefaultEpsilon = 0.01f;

        /// <summary>
        /// Finds the earliest placement of [requestedStart, requestedEnd) that does
        /// not overlap an existing window. Moving past one conflict may expose a
        /// later conflict, so all windows are processed in chronological order.
        /// </summary>
        public static ReservationWindowSearchResult FindEarliest(
            IReadOnlyList<ReservationWindow> windows,
            float requestedStart,
            float requestedEnd,
            float maxWait,
            string ignoredAircraftId = null,
            float epsilon = DefaultEpsilon)
        {
            if (!IsFinite(requestedStart) || !IsFinite(requestedEnd) || requestedEnd < requestedStart)
            {
                return Failure("Invalid requested reservation interval.");
            }

            if (float.IsNaN(maxWait) || maxWait < 0f)
            {
                return Failure("Maximum wait must be non-negative.");
            }

            float duration = requestedEnd - requestedStart;
            float candidateStart = requestedStart;
            int conflictsResolved = 0;

            var orderedWindows = new List<ReservationWindow>();
            if (windows != null)
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    ReservationWindow window = windows[i];
                    if (!IsValid(window)) continue;
                    if (!string.IsNullOrEmpty(ignoredAircraftId) &&
                        window.AircraftId == ignoredAircraftId)
                    {
                        continue;
                    }
                    orderedWindows.Add(window);
                }
            }

            orderedWindows.Sort((left, right) =>
            {
                int startCompare = left.StartTime.CompareTo(right.StartTime);
                return startCompare != 0 ? startCompare : left.EndTime.CompareTo(right.EndTime);
            });

            for (int i = 0; i < orderedWindows.Count; i++)
            {
                ReservationWindow window = orderedWindows[i];
                float candidateEnd = candidateStart + duration;

                if (!Overlaps(candidateStart, candidateEnd, window.StartTime, window.EndTime, epsilon))
                    continue;

                candidateStart = Math.Max(candidateStart, window.EndTime);
                conflictsResolved++;

                float wait = candidateStart - requestedStart;
                if (!float.IsPositiveInfinity(maxWait) && wait > maxWait + epsilon)
                {
                    return Failure(
                        string.Format(
                            "Required wait {0:F3}s exceeds maximum {1:F3}s.",
                            wait,
                            maxWait),
                        requestedStart,
                        duration,
                        wait,
                        conflictsResolved);
                }
            }

            float finalWait = candidateStart - requestedStart;
            return new ReservationWindowSearchResult
            {
                Success = true,
                StartTime = candidateStart,
                EndTime = candidateStart + duration,
                WaitTime = finalWait,
                ConflictsResolved = conflictsResolved,
                FailureReason = null
            };
        }

        public static bool HasConflict(
            IReadOnlyList<ReservationWindow> windows,
            float startTime,
            float endTime,
            string ignoredAircraftId = null,
            float epsilon = DefaultEpsilon)
        {
            if (windows == null) return false;

            for (int i = 0; i < windows.Count; i++)
            {
                ReservationWindow window = windows[i];
                if (!IsValid(window)) continue;
                if (!string.IsNullOrEmpty(ignoredAircraftId) &&
                    window.AircraftId == ignoredAircraftId)
                {
                    continue;
                }

                if (Overlaps(startTime, endTime, window.StartTime, window.EndTime, epsilon))
                    return true;
            }

            return false;
        }

        public static bool Overlaps(
            float startA,
            float endA,
            float startB,
            float endB,
            float epsilon = DefaultEpsilon)
        {
            return startA < endB - epsilon && endA > startB + epsilon;
        }

        private static bool IsValid(ReservationWindow window)
        {
            return IsFinite(window.StartTime) &&
                   IsFinite(window.EndTime) &&
                   window.EndTime >= window.StartTime;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static ReservationWindowSearchResult Failure(
            string reason,
            float requestedStart = 0f,
            float duration = 0f,
            float wait = 0f,
            int conflictsResolved = 0)
        {
            return new ReservationWindowSearchResult
            {
                Success = false,
                StartTime = requestedStart,
                EndTime = requestedStart + duration,
                WaitTime = wait,
                ConflictsResolved = conflictsResolved,
                FailureReason = reason
            };
        }
    }

    /// <summary>
    /// Jointly schedules the current-node stay, edge traversal, and target-node
    /// dwell. A target delay can move the edge into another occupied window, so
    /// edge and target are checked repeatedly until both exact booking intervals
    /// are stable.
    /// </summary>
    public static class ReservationTransitionSolver
    {
        public static ReservationTransitionSearchResult FindEarliest(
            IReadOnlyList<ReservationWindow> currentNodeWindows,
            IReadOnlyList<ReservationWindow> edgeWindows,
            IReadOnlyList<ReservationWindow> targetNodeWindows,
            float currentArrivalTime,
            float travelTime,
            float nodeDwellTime,
            float resourceBuffer,
            float maxWait,
            string ignoredAircraftId = null)
        {
            if (!IsFinite(currentArrivalTime) ||
                !IsFinite(travelTime) || travelTime < 0f ||
                !IsFinite(nodeDwellTime) || nodeDwellTime < 0f ||
                !IsFinite(resourceBuffer) || resourceBuffer < 0f ||
                float.IsNaN(maxWait) || maxWait < 0f)
            {
                return Failure("Invalid transition timing parameters.");
            }

            float candidateDeparture = currentArrivalTime;
            int iterationLimit = Count(edgeWindows) + Count(targetNodeWindows) + 2;

            for (int iteration = 1; iteration <= iterationLimit; iteration++)
            {
                float waitSoFar = candidateDeparture - currentArrivalTime;
                float remainingWait = float.IsPositiveInfinity(maxWait)
                    ? float.PositiveInfinity
                    : Math.Max(0f, maxWait - waitSoFar);

                ReservationWindowSearchResult edgeResult = ReservationWindowSolver.FindEarliest(
                    edgeWindows,
                    candidateDeparture - resourceBuffer,
                    candidateDeparture + travelTime + resourceBuffer,
                    remainingWait,
                    ignoredAircraftId);
                if (!edgeResult.Success)
                    return Failure("Edge interval cannot be scheduled: " + edgeResult.FailureReason, iteration);

                candidateDeparture += edgeResult.WaitTime;
                waitSoFar = candidateDeparture - currentArrivalTime;
                if (!WithinWaitLimit(waitSoFar, maxWait))
                    return Failure("Transition exceeds the maximum wait.", iteration);

                float candidateArrival = candidateDeparture + travelTime;
                remainingWait = float.IsPositiveInfinity(maxWait)
                    ? float.PositiveInfinity
                    : Math.Max(0f, maxWait - waitSoFar);

                ReservationWindowSearchResult targetResult = ReservationWindowSolver.FindEarliest(
                    targetNodeWindows,
                    candidateArrival - resourceBuffer,
                    candidateArrival + nodeDwellTime + resourceBuffer,
                    remainingWait,
                    ignoredAircraftId);
                if (!targetResult.Success)
                    return Failure("Target-node interval cannot be scheduled: " + targetResult.FailureReason, iteration);

                if (targetResult.WaitTime > ReservationWindowSolver.DefaultEpsilon)
                {
                    candidateDeparture += targetResult.WaitTime;
                    if (!WithinWaitLimit(candidateDeparture - currentArrivalTime, maxWait))
                        return Failure("Transition exceeds the maximum wait.", iteration);
                    continue;
                }

                float currentStayEnd = candidateDeparture + nodeDwellTime + resourceBuffer;
                if (ReservationWindowSolver.HasConflict(
                        currentNodeWindows,
                        currentArrivalTime - resourceBuffer,
                        currentStayEnd,
                        ignoredAircraftId))
                {
                    return Failure(
                        "Current node cannot remain occupied until the scheduled departure.",
                        iteration);
                }

                return new ReservationTransitionSearchResult
                {
                    Success = true,
                    DepartureTime = candidateDeparture,
                    ArrivalTime = candidateArrival,
                    WaitTime = candidateDeparture - currentArrivalTime,
                    Iterations = iteration,
                    FailureReason = null
                };
            }

            return Failure("Transition scheduling did not converge.", iterationLimit);
        }

        private static int Count(IReadOnlyList<ReservationWindow> windows)
        {
            return windows != null ? windows.Count : 0;
        }

        private static bool WithinWaitLimit(float wait, float maxWait)
        {
            return float.IsPositiveInfinity(maxWait) ||
                   wait <= maxWait + ReservationWindowSolver.DefaultEpsilon;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static ReservationTransitionSearchResult Failure(string reason, int iterations = 0)
        {
            return new ReservationTransitionSearchResult
            {
                Success = false,
                DepartureTime = 0f,
                ArrivalTime = 0f,
                WaitTime = 0f,
                Iterations = iterations,
                FailureReason = reason
            };
        }
    }
}
