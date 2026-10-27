using System;
using System.Collections.Generic;
using System.Linq;

namespace RoadNetworkEditor
{
    public sealed class HelipadEndpoint
    {
        public string VertiportId;
        public string HelipadId;

        public HelipadEndpoint(string vertiportId, string helipadId)
        {
            VertiportId = vertiportId;
            HelipadId = helipadId;
        }
    }

    public sealed class RandomMissionEndpoints
    {
        public HelipadEndpoint Origin;
        public HelipadEndpoint Destination;
    }

    /// <summary>
    /// Allocates disjoint origin/destination helipads for the initial random fleet.
    /// A helipad can appear in at most one role, and every trip crosses vertiports.
    /// </summary>
    public static class RandomDemandAllocator
    {
        /// <summary>
        /// Returns the largest fleet that can reserve one unique origin and one unique
        /// destination helipad per aircraft while keeping every pair cross-vertiport.
        /// </summary>
        public static int CalculateSafeFleetCapacity(IReadOnlyList<HelipadEndpoint> endpoints)
        {
            if (endpoints == null) return 0;
            List<HelipadEndpoint> unique = endpoints
                .Where(endpoint => endpoint != null &&
                                   !string.IsNullOrWhiteSpace(endpoint.VertiportId) &&
                                   !string.IsNullOrWhiteSpace(endpoint.HelipadId))
                .GroupBy(endpoint => endpoint.HelipadId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (unique.Count < 2) return 0;

            int largestVertiport = unique.GroupBy(endpoint => endpoint.VertiportId, StringComparer.Ordinal)
                .Max(group => group.Count());
            return Math.Max(0, Math.Min(unique.Count / 2, unique.Count - largestVertiport));
        }

        public static List<RandomMissionEndpoints> Allocate(
            IReadOnlyList<HelipadEndpoint> endpoints,
            int requestedCount,
            Random random)
        {
            var result = new List<RandomMissionEndpoints>();
            if (endpoints == null || requestedCount <= 0)
                return result;

            random = random ?? new Random();
            var available = endpoints
                .Where(endpoint => endpoint != null &&
                                   !string.IsNullOrWhiteSpace(endpoint.VertiportId) &&
                                   !string.IsNullOrWhiteSpace(endpoint.HelipadId))
                .GroupBy(endpoint => endpoint.HelipadId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            int target = Math.Min(requestedCount, CalculateSafeFleetCapacity(available));
            var groups = available
                .GroupBy(endpoint => endpoint.VertiportId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(_ => random.Next()).ToList())
                .ToList();

            // Pair the two currently largest vertiport pools. This greedy matching
            // reaches the calculated capacity and avoids a random early stop when one
            // vertiport contains most of the available helipads.
            while (result.Count < target)
            {
                List<List<HelipadEndpoint>> nonEmpty = groups.Where(group => group.Count > 0)
                    .OrderByDescending(group => group.Count)
                    .ThenBy(_ => random.Next())
                    .ToList();
                if (nonEmpty.Count < 2) break;

                List<HelipadEndpoint> firstGroup = nonEmpty[0];
                List<HelipadEndpoint> secondGroup = nonEmpty[1];
                HelipadEndpoint first = firstGroup[firstGroup.Count - 1];
                HelipadEndpoint second = secondGroup[secondGroup.Count - 1];
                firstGroup.RemoveAt(firstGroup.Count - 1);
                secondGroup.RemoveAt(secondGroup.Count - 1);
                bool swap = random.Next(2) == 1;
                result.Add(new RandomMissionEndpoints
                {
                    Origin = swap ? second : first,
                    Destination = swap ? first : second
                });
            }

            return result;
        }
    }
}
