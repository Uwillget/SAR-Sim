using System.Collections.Generic;
using NUnit.Framework;
using RoadNetworkEditor.Reservations;

namespace RoadNetworkEditor.Tests.Editor
{
    public class ReservationWindowSolverTests
    {
        [Test]
        public void FindEarliest_ResolvesCascadingConflictsAfterIntervalShift()
        {
            var windows = new List<ReservationWindow>
            {
                Window("A1", 2f, 4f),
                Window("A2", 8f, 10f)
            };

            ReservationWindowSearchResult result = ReservationWindowSolver.FindEarliest(
                windows, 0f, 5f, 30f);

            Assert.That(result.Success, Is.True);
            Assert.That(result.StartTime, Is.EqualTo(10f).Within(0.0001f));
            Assert.That(result.EndTime, Is.EqualTo(15f).Within(0.0001f));
            Assert.That(result.WaitTime, Is.EqualTo(10f).Within(0.0001f));
            Assert.That(result.ConflictsResolved, Is.EqualTo(2));
        }

        [Test]
        public void FindEarliest_SortsUnorderedReservationData()
        {
            var windows = new List<ReservationWindow>
            {
                Window("A2", 8f, 10f),
                Window("A1", 2f, 4f)
            };

            ReservationWindowSearchResult result = ReservationWindowSolver.FindEarliest(
                windows, 0f, 5f, 30f);

            Assert.That(result.Success, Is.True);
            Assert.That(result.StartTime, Is.EqualTo(10f).Within(0.0001f));
        }

        [Test]
        public void FindEarliest_AllowsIntervalsToTouchWithoutOverlap()
        {
            var windows = new List<ReservationWindow>
            {
                Window("A1", 2f, 4f)
            };

            ReservationWindowSearchResult result = ReservationWindowSolver.FindEarliest(
                windows, 4f, 6f, 30f);

            Assert.That(result.Success, Is.True);
            Assert.That(result.WaitTime, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void FindEarliest_FailsWhenRequiredWaitExceedsLimit()
        {
            var windows = new List<ReservationWindow>
            {
                Window("A1", 0f, 20f)
            };

            ReservationWindowSearchResult result = ReservationWindowSolver.FindEarliest(
                windows, 0f, 5f, 10f);

            Assert.That(result.Success, Is.False);
            StringAssert.Contains("exceeds maximum", result.FailureReason);
        }

        [Test]
        public void FindEarliest_CanIgnoreTheRequestingAircraftOwnWindow()
        {
            var windows = new List<ReservationWindow>
            {
                Window("SELF", 0f, 20f)
            };

            ReservationWindowSearchResult result = ReservationWindowSolver.FindEarliest(
                windows, 0f, 5f, 10f, ignoredAircraftId: "SELF");

            Assert.That(result.Success, Is.True);
            Assert.That(result.WaitTime, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void HasConflict_UsesTheSameIntervalSemanticsAsSearch()
        {
            var windows = new List<ReservationWindow>
            {
                Window("A1", 2f, 4f)
            };

            Assert.That(ReservationWindowSolver.HasConflict(windows, 1f, 3f), Is.True);
            Assert.That(ReservationWindowSolver.HasConflict(windows, 4f, 6f), Is.False);
        }

        [Test]
        public void TransitionSearch_RechecksEdgeAfterTargetNodeDelay()
        {
            var edgeWindows = new List<ReservationWindow>
            {
                Window("EDGE-BLOCKER", 5f, 8f)
            };
            var targetWindows = new List<ReservationWindow>
            {
                Window("NODE-BLOCKER", 2f, 7f)
            };

            ReservationTransitionSearchResult result = ReservationTransitionSolver.FindEarliest(
                new List<ReservationWindow>(),
                edgeWindows,
                targetWindows,
                currentArrivalTime: 0f,
                travelTime: 2f,
                nodeDwellTime: 1f,
                resourceBuffer: 0f,
                maxWait: 30f);

            Assert.That(result.Success, Is.True);
            Assert.That(result.DepartureTime, Is.EqualTo(8f).Within(0.0001f));
            Assert.That(result.ArrivalTime, Is.EqualTo(10f).Within(0.0001f));
            Assert.That(result.WaitTime, Is.EqualTo(8f).Within(0.0001f));
            Assert.That(result.Iterations, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void TransitionSearch_RejectsWaitThatConflictsWithCurrentNodeStay()
        {
            var currentWindows = new List<ReservationWindow>
            {
                Window("CURRENT-BLOCKER", 5f, 6f)
            };
            var edgeWindows = new List<ReservationWindow>
            {
                Window("EDGE-BLOCKER", 0f, 10f)
            };

            ReservationTransitionSearchResult result = ReservationTransitionSolver.FindEarliest(
                currentWindows,
                edgeWindows,
                new List<ReservationWindow>(),
                currentArrivalTime: 0f,
                travelTime: 2f,
                nodeDwellTime: 1f,
                resourceBuffer: 0f,
                maxWait: 30f);

            Assert.That(result.Success, Is.False);
            StringAssert.Contains("Current node", result.FailureReason);
        }

        [Test]
        public void TransitionSearch_AppliesBookingBufferToEdgeAndTargetNode()
        {
            var edgeWindows = new List<ReservationWindow>
            {
                Window("EDGE-BLOCKER", 2.5f, 4f)
            };

            ReservationTransitionSearchResult result = ReservationTransitionSolver.FindEarliest(
                new List<ReservationWindow>(),
                edgeWindows,
                new List<ReservationWindow>(),
                currentArrivalTime: 0f,
                travelTime: 2f,
                nodeDwellTime: 1f,
                resourceBuffer: 1f,
                maxWait: 30f);

            Assert.That(result.Success, Is.True);
            Assert.That(result.DepartureTime, Is.EqualTo(5f).Within(0.0001f));
            Assert.That(result.ArrivalTime, Is.EqualTo(7f).Within(0.0001f));
        }

        private static ReservationWindow Window(string aircraftId, float start, float end)
        {
            return new ReservationWindow(aircraftId, start, end);
        }
    }
}
