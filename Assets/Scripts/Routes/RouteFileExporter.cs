using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;

namespace RoadNetworkEditor.Routes
{
    public static class RouteFileExporter
    {
        public static void Save(string filePath, IEnumerable<MissionTask> source)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Route output path is empty.", nameof(filePath));

            string path = Path.GetFullPath(filePath);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            List<MissionTask> missions = (source ?? Enumerable.Empty<MissionTask>())
                .Where(task => task != null)
                .OrderBy(task => task.DepartureTime)
                .ThenBy(task => task.TaskId, StringComparer.Ordinal)
                .ToList();

            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".xml")
                File.WriteAllText(path, SerializeXml(missions));
            else if (extension == ".json" || string.IsNullOrEmpty(extension))
                File.WriteAllText(path, SerializeJson(missions));
            else
                throw new NotSupportedException("Route output must use .json or .xml.");
        }

        public static string SerializeJson(IEnumerable<MissionTask> source)
        {
            var data = new MissionSaveData
            {
                Missions = (source ?? Enumerable.Empty<MissionTask>())
                    .Where(task => task != null)
                    .ToList()
            };
            return JsonConvert.SerializeObject(data, Formatting.Indented);
        }

        public static string SerializeXml(IEnumerable<MissionTask> source)
        {
            List<MissionTask> missions = (source ?? Enumerable.Empty<MissionTask>())
                .Where(task => task != null)
                .ToList();
            var root = new XElement("routes");
            var routeIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var taskRoutes = new Dictionary<MissionTask, string>();

            for (int i = 0; i < missions.Count; i++)
            {
                MissionTask task = missions[i];
                if (task.RouteLaneIds == null || task.RouteLaneIds.Count == 0) continue;
                string routeId = string.IsNullOrWhiteSpace(task.RouteId)
                    ? $"route-{i + 1:D4}"
                    : task.RouteId;
                string uniqueId = routeId;
                int suffix = 1;
                while (routeIds.TryGetValue(uniqueId, out List<string> existing) &&
                       !existing.SequenceEqual(task.RouteLaneIds))
                    uniqueId = routeId + "-" + (++suffix).ToString(CultureInfo.InvariantCulture);
                taskRoutes[task] = uniqueId;
                if (!routeIds.ContainsKey(uniqueId))
                    routeIds[uniqueId] = new List<string>(task.RouteLaneIds);
            }

            foreach (var route in routeIds)
                root.Add(new XElement("route",
                    new XAttribute("id", route.Key),
                    new XAttribute("lanes", string.Join(" ", route.Value))));

            foreach (MissionTask task in missions)
            {
                var vehicle = new XElement("vehicle",
                    new XAttribute("id", task.TaskId ?? string.Empty),
                    new XAttribute("originVertiport", task.OriginVertiportId ?? string.Empty),
                    new XAttribute("destinationVertiport", task.DestinationVertiportId ?? string.Empty),
                    new XAttribute("depart", task.DepartureTime.ToString("0.###", CultureInfo.InvariantCulture)),
                    new XAttribute("priority", task.Priority));
                if (!string.IsNullOrWhiteSpace(task.OriginHelipadId))
                    vehicle.Add(new XAttribute("originHelipad", task.OriginHelipadId));
                if (!string.IsNullOrWhiteSpace(task.DestinationHelipadId))
                    vehicle.Add(new XAttribute("destinationHelipad", task.DestinationHelipadId));
                if (task.RouteLaneIds != null && task.RouteLaneIds.Count > 0 &&
                    taskRoutes.TryGetValue(task, out string exportedRouteId))
                    vehicle.Add(new XAttribute("route", exportedRouteId));
                root.Add(vehicle);
            }

            return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).ToString();
        }
    }
}
