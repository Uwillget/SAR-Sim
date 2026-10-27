using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RoadNetworkEditor.Routes
{
    public sealed class RouteImportResult
    {
        public readonly List<MissionTask> Missions = new List<MissionTask>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Success => Errors.Count == 0;
    }

    public static class RouteFileImporter
    {
        public static RouteImportResult Load(
            string filePath,
            ISet<string> knownLaneIds,
            ISet<string> knownVertiportIds,
            ISet<string> knownHelipadIds = null)
        {
            var result = new RouteImportResult();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                result.Errors.Add($"Route file not found: {filePath}.");
                return result;
            }

            try
            {
                return ParseContent(
                    File.ReadAllText(filePath),
                    Path.GetExtension(filePath),
                    knownLaneIds,
                    knownVertiportIds,
                    knownHelipadIds);
            }
            catch (Exception exception)
            {
                result.Errors.Add($"Failed to read route file: {exception.Message}");
                return result;
            }
        }

        /// <summary>
        /// Parses route-file text without touching the file system. The format can be
        /// ".json"/"json" or ".xml"/"xml".
        /// </summary>
        public static RouteImportResult ParseContent(
            string content,
            string format,
            ISet<string> knownLaneIds,
            ISet<string> knownVertiportIds,
            ISet<string> knownHelipadIds = null)
        {
            var result = new RouteImportResult();
            try
            {
                string normalizedFormat = (format ?? string.Empty).Trim().ToLowerInvariant();
                if (!normalizedFormat.StartsWith(".")) normalizedFormat = "." + normalizedFormat;
                if (normalizedFormat == ".xml")
                    ParseXml(content, result);
                else if (normalizedFormat == ".json")
                    ParseJson(content, result);
                else
                    result.Errors.Add($"Unsupported route file format '{format}'. Use JSON or XML.");
            }
            catch (Exception exception)
            {
                result.Errors.Add($"Failed to parse route file: {exception.Message}");
                return result;
            }

            Validate(result, knownLaneIds, knownVertiportIds, knownHelipadIds);
            result.Missions.Sort((left, right) =>
            {
                int departureCompare = left.DepartureTime.CompareTo(right.DepartureTime);
                return departureCompare != 0
                    ? departureCompare
                    : string.CompareOrdinal(left.TaskId, right.TaskId);
            });
            return result;
        }

        private static void ParseJson(string json, RouteImportResult result)
        {
            JObject root = JObject.Parse(json);
            JToken legacyMissions = GetProperty(root, "Missions");
            if (legacyMissions is JArray legacyArray)
            {
                List<MissionTask> missions = legacyArray.ToObject<List<MissionTask>>() ?? new List<MissionTask>();
                result.Missions.AddRange(missions);
                return;
            }

            var routes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (GetProperty(root, "routes") is JArray routeArray)
            {
                foreach (JObject routeObject in routeArray.OfType<JObject>())
                {
                    string routeId = Value(routeObject, "id");
                    List<string> lanes = StringList(GetProperty(routeObject, "lanes") ??
                                                    GetProperty(routeObject, "edges"));
                    AddRoute(routes, routeId, lanes, result);
                }
            }

            JArray demandArray = GetProperty(root, "vehicles") as JArray ??
                                 GetProperty(root, "trips") as JArray;
            if (demandArray == null)
            {
                result.Errors.Add("JSON route file needs a 'vehicles', 'trips', or legacy 'Missions' array.");
                return;
            }

            foreach (JObject demand in demandArray.OfType<JObject>())
                result.Missions.Add(ParseJsonDemand(demand, routes));
        }

        private static MissionTask ParseJsonDemand(
            JObject demand,
            IReadOnlyDictionary<string, List<string>> routes)
        {
            string routeId = Value(demand, "route") ?? Value(demand, "routeId");
            List<string> inlineLanes = StringList(GetProperty(demand, "lanes") ??
                                                  GetProperty(demand, "edges"));
            List<string> routeLanes = inlineLanes.Count > 0
                ? inlineLanes
                : ResolveRoute(routeId, routes);

            return new MissionTask
            {
                TaskId = Value(demand, "id") ?? Value(demand, "taskId"),
                RouteId = routeId,
                OriginVertiportId = Value(demand, "originVertiport") ??
                                    Value(demand, "originVertiportId") ??
                                    Value(demand, "from"),
                DestinationVertiportId = Value(demand, "destinationVertiport") ??
                                         Value(demand, "destinationVertiportId") ??
                                         Value(demand, "to"),
                OriginHelipadId = Value(demand, "originHelipad") ?? Value(demand, "originHelipadId"),
                DestinationHelipadId = Value(demand, "destinationHelipad") ??
                                       Value(demand, "destinationHelipadId"),
                RouteLaneIds = routeLanes,
                DepartureTime = FloatValue(demand, "depart", FloatValue(demand, "departureTime", 0f)),
                Priority = IntValue(demand, "priority", 1)
            };
        }

        private static void ParseXml(string xml, RouteImportResult result)
        {
            XDocument document = XDocument.Parse(xml, LoadOptions.SetLineInfo);
            XElement root = document.Root;
            if (root == null || root.Name.LocalName != "routes")
            {
                result.Errors.Add("XML root element must be <routes>.");
                return;
            }

            var routes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (XElement route in root.Elements().Where(element => element.Name.LocalName == "route"))
            {
                string routeId = Attribute(route, "id");
                List<string> lanes = SplitIds(Attribute(route, "lanes") ?? Attribute(route, "edges"));
                AddRoute(routes, routeId, lanes, result);
            }

            foreach (XElement element in root.Elements())
            {
                string elementName = element.Name.LocalName;
                if (elementName == "vehicle" || elementName == "trip")
                {
                    result.Missions.Add(ParseXmlDemand(element, routes));
                }
                else if (elementName == "flow")
                {
                    ExpandFlow(element, routes, result);
                }
            }
        }

        private static MissionTask ParseXmlDemand(
            XElement element,
            IReadOnlyDictionary<string, List<string>> routes)
        {
            string routeId = Attribute(element, "route");
            List<string> inlineLanes = SplitIds(
                Attribute(element, "lanes") ?? Attribute(element, "edges"));
            XElement inlineRoute = element.Elements().FirstOrDefault(child => child.Name.LocalName == "route");
            if (inlineRoute != null)
            {
                inlineLanes = SplitIds(
                    Attribute(inlineRoute, "lanes") ?? Attribute(inlineRoute, "edges"));
            }

            return new MissionTask
            {
                TaskId = Attribute(element, "id"),
                RouteId = routeId,
                OriginVertiportId = Attribute(element, "originVertiport") ?? Attribute(element, "from"),
                DestinationVertiportId = Attribute(element, "destinationVertiport") ?? Attribute(element, "to"),
                OriginHelipadId = Attribute(element, "originHelipad"),
                DestinationHelipadId = Attribute(element, "destinationHelipad"),
                RouteLaneIds = inlineLanes.Count > 0 ? inlineLanes : ResolveRoute(routeId, routes),
                DepartureTime = ParseFloat(Attribute(element, "depart"), 0f),
                Priority = ParseInt(Attribute(element, "priority"), 1)
            };
        }

        private static void ExpandFlow(
            XElement flow,
            IReadOnlyDictionary<string, List<string>> routes,
            RouteImportResult result)
        {
            string baseId = Attribute(flow, "id") ?? "flow";
            float begin = ParseFloat(Attribute(flow, "begin"), 0f);
            float end = ParseFloat(Attribute(flow, "end"), begin);
            int number = ParseInt(Attribute(flow, "number"), 0);
            float period = ParseFloat(Attribute(flow, "period"), 0f);

            if (number <= 0 && period > 0f && end >= begin)
                number = (int)Math.Floor((end - begin) / period) + 1;
            if (number <= 0)
            {
                result.Errors.Add($"Flow '{baseId}' needs a positive number or period/end range.");
                return;
            }
            if (period <= 0f)
                period = number > 1 ? Math.Max(0f, end - begin) / (number - 1) : 0f;

            for (int i = 0; i < number; i++)
            {
                MissionTask mission = ParseXmlDemand(flow, routes);
                mission.TaskId = $"{baseId}.{i}";
                mission.DepartureTime = begin + period * i;
                result.Missions.Add(mission);
            }
        }

        private static void Validate(
            RouteImportResult result,
            ISet<string> knownLaneIds,
            ISet<string> knownVertiportIds,
            ISet<string> knownHelipadIds)
        {
            var taskIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = result.Missions.Count - 1; i >= 0; i--)
            {
                MissionTask mission = result.Missions[i];
                string prefix = string.IsNullOrWhiteSpace(mission?.TaskId)
                    ? $"Mission[{i}]"
                    : $"Mission '{mission.TaskId}'";
                bool valid = true;

                if (mission == null)
                {
                    result.Errors.Add($"Mission[{i}] is null.");
                    result.Missions.RemoveAt(i);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(mission.TaskId))
                {
                    result.Errors.Add($"{prefix} needs an id.");
                    valid = false;
                }
                else if (!taskIds.Add(mission.TaskId))
                {
                    result.Errors.Add($"Duplicate mission id '{mission.TaskId}'.");
                    valid = false;
                }
                if (!IsFinite(mission.DepartureTime) || mission.DepartureTime < 0f)
                {
                    result.Errors.Add($"{prefix} has invalid departure time {mission.DepartureTime}.");
                    valid = false;
                }
                if (knownVertiportIds != null &&
                    !knownVertiportIds.Contains(mission.OriginVertiportId ?? string.Empty))
                {
                    result.Errors.Add($"{prefix} references unknown origin vertiport '{mission.OriginVertiportId}'.");
                    valid = false;
                }
                if (knownVertiportIds != null &&
                    !knownVertiportIds.Contains(mission.DestinationVertiportId ?? string.Empty))
                {
                    result.Errors.Add($"{prefix} references unknown destination vertiport '{mission.DestinationVertiportId}'.");
                    valid = false;
                }
                if (mission.OriginVertiportId == mission.DestinationVertiportId)
                {
                    result.Errors.Add($"{prefix} origin and destination vertiports must differ.");
                    valid = false;
                }
                if (knownHelipadIds != null && !string.IsNullOrWhiteSpace(mission.OriginHelipadId) &&
                    !knownHelipadIds.Contains(mission.OriginHelipadId))
                {
                    result.Errors.Add($"{prefix} references unknown origin helipad '{mission.OriginHelipadId}'.");
                    valid = false;
                }
                if (knownHelipadIds != null && !string.IsNullOrWhiteSpace(mission.DestinationHelipadId) &&
                    !knownHelipadIds.Contains(mission.DestinationHelipadId))
                {
                    result.Errors.Add($"{prefix} references unknown destination helipad '{mission.DestinationHelipadId}'.");
                    valid = false;
                }

                mission.RouteLaneIds = mission.RouteLaneIds ?? new List<string>();
                foreach (string laneId in mission.RouteLaneIds)
                {
                    if (knownLaneIds != null && !knownLaneIds.Contains(laneId))
                    {
                        result.Errors.Add($"{prefix} route references unknown lane or connector '{laneId}'.");
                        valid = false;
                    }
                }
                if (!string.IsNullOrWhiteSpace(mission.RouteId) && mission.RouteLaneIds.Count == 0)
                {
                    result.Errors.Add($"{prefix} references missing or empty route '{mission.RouteId}'.");
                    valid = false;
                }

                if (!valid) result.Missions.RemoveAt(i);
            }
        }

        private static void AddRoute(
            IDictionary<string, List<string>> routes,
            string routeId,
            List<string> lanes,
            RouteImportResult result)
        {
            if (string.IsNullOrWhiteSpace(routeId))
            {
                result.Errors.Add("Route definition needs an id.");
                return;
            }
            if (routes.ContainsKey(routeId))
            {
                result.Errors.Add($"Duplicate route id '{routeId}'.");
                return;
            }
            if (lanes.Count == 0)
                result.Warnings.Add($"Route '{routeId}' has no lane IDs.");
            routes[routeId] = lanes;
        }

        private static List<string> ResolveRoute(
            string routeId,
            IReadOnlyDictionary<string, List<string>> routes)
        {
            return !string.IsNullOrWhiteSpace(routeId) && routes.TryGetValue(routeId, out var lanes)
                ? new List<string>(lanes)
                : new List<string>();
        }

        private static JToken GetProperty(JObject value, string name)
        {
            return value.Properties()
                .FirstOrDefault(property => string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        private static string Value(JObject value, string name)
        {
            JToken token = GetProperty(value, name);
            return token?.Type == JTokenType.Null ? null : token?.ToString();
        }

        private static float FloatValue(JObject value, string name, float fallback)
        {
            return ParseFloat(Value(value, name), fallback);
        }

        private static int IntValue(JObject value, string name, int fallback)
        {
            return ParseInt(Value(value, name), fallback);
        }

        private static List<string> StringList(JToken token)
        {
            if (token is JArray array)
                return array.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            return SplitIds(token?.ToString());
        }

        private static List<string> SplitIds(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static string Attribute(XElement element, string name)
        {
            return element.Attributes()
                .FirstOrDefault(attribute => string.Equals(
                    attribute.Name.LocalName,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        private static float ParseFloat(string value, float fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : float.NaN;
        }

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
