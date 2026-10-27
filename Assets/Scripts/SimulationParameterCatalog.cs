using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace RoadNetworkEditor
{
    public enum SimulationParameterGroup
    {
        Aircraft,
        Reservation,
        TrafficControl,
        Demand,
        InformationUncertainty
    }

    /// <summary>
    /// Describes one operator-editable SimulationConfig field. Both the Unity
    /// Editor and the packaged workbench use this catalog so validation and
    /// parameter-sweep support cannot silently diverge.
    /// </summary>
    public sealed class SimulationParameterDescriptor
    {
        public string FieldName { get; }
        public string DisplayName { get; }
        public SimulationParameterGroup Group { get; }
        public float Minimum { get; }
        public float Maximum { get; }
        public bool CanSweep { get; }
        public Type ValueType { get; }

        internal SimulationParameterDescriptor(string fieldName, string displayName,
            SimulationParameterGroup group, float minimum, float maximum, bool canSweep)
        {
            FieldName = fieldName;
            DisplayName = displayName;
            Group = group;
            Minimum = minimum;
            Maximum = maximum;
            CanSweep = canSweep;
            ValueType = typeof(SimulationConfig).GetField(fieldName,
                BindingFlags.Public | BindingFlags.Instance)?.FieldType;
        }
    }

    public static class SimulationParameterCatalog
    {
        private static readonly List<SimulationParameterDescriptor> Descriptors =
            new List<SimulationParameterDescriptor>
            {
                Float("maxSpeed", "Maximum Speed", SimulationParameterGroup.Aircraft, 0.1f, 1000f),
                Float("rotationSpeed", "Maximum Turn Rate", SimulationParameterGroup.Aircraft, 0.1f, 1000f),
                Float("acceleration", "Acceleration", SimulationParameterGroup.Aircraft, 0.01f, 1000f),
                Float("maxDeceleration", "Maximum Deceleration", SimulationParameterGroup.Aircraft, 0.01f, 1000f),
                Float("safeInterval", "Following Safety Gap", SimulationParameterGroup.Aircraft, 0.1f, 10000f),
                Float("laneChangeDistance", "Lane-Change Distance", SimulationParameterGroup.Aircraft, 0.1f, 10000f),
                Float("laneChangeFrontGapFactor", "Lane-Change Front-Gap Factor", SimulationParameterGroup.Aircraft, 0f, 100f),
                Float("laneChangeRearGapFactor", "Lane-Change Rear-Gap Factor", SimulationParameterGroup.Aircraft, 0f, 100f),
                Float("laneChangeRerouteTimeout", "Lane-Change Reroute Timeout", SimulationParameterGroup.Aircraft, 0f, 100000f),
                Float("laneChangeRemovalTimeout", "Lane-Change Deadlock Removal Timeout", SimulationParameterGroup.Aircraft, 0f, 100000f),

                Float("resourceBuffer", "Reservation Buffer", SimulationParameterGroup.Reservation, 0f, 10000f),
                Float("maxWaitPerResource", "Maximum Wait per Resource", SimulationParameterGroup.Reservation, 0f, 100000f),
                Float("maxCorridorWaitTime", "Maximum Corridor Wait", SimulationParameterGroup.Reservation, 0f, 100000f),
                Float("maxHelipadWaitTime", "Maximum Helipad Wait", SimulationParameterGroup.Reservation, 0f, 100000f),
                Float("nodeDwellTime", "Minimum Node Occupancy Time", SimulationParameterGroup.Reservation, 0f, 10000f),

                Float("greenWaveSpeed", "Green-Wave Propagation Speed", SimulationParameterGroup.TrafficControl, 0.1f, 1000f),
                Float("greenSignalTime", "Green-Signal Duration", SimulationParameterGroup.TrafficControl, 0.1f, 100000f),
                Float("signalClearanceTime", "All-Red Clearance Interval", SimulationParameterGroup.TrafficControl, 0f, 100000f),
                Float("signalStopLineDistance", "Signal Stop-Line Distance", SimulationParameterGroup.TrafficControl, 0f, 10000f),

                Integer("maxConcurrentAircraft", "Random-Fleet Aircraft Count", SimulationParameterGroup.Demand, 1f, 100000f),
                Float("randomTurnaroundTime", "Random-Demand Turnaround Time", SimulationParameterGroup.Demand, 0f, 100000f),

                Float("signalPropagationDelay", "Information Propagation Delay", SimulationParameterGroup.InformationUncertainty, 0f, 100000f),
                Float("positioningErrorStdDev", "Positioning-Error Standard Deviation", SimulationParameterGroup.InformationUncertainty, 0f, 100000f)
            };

        private static readonly Dictionary<string, SimulationParameterDescriptor> ByField =
            Descriptors.ToDictionary(item => item.FieldName, StringComparer.Ordinal);

        public static IReadOnlyList<SimulationParameterDescriptor> All => Descriptors;

        public static IReadOnlyList<string> SweepableFieldNames => Descriptors
            .Where(item => item.CanSweep)
            .Select(item => item.FieldName)
            .ToList();

        public static bool TryGet(string fieldName, out SimulationParameterDescriptor descriptor) =>
            ByField.TryGetValue(fieldName ?? string.Empty, out descriptor);

        public static string FormatValue(SimulationConfig config, SimulationParameterDescriptor descriptor)
        {
            if (config == null || descriptor == null) return string.Empty;
            FieldInfo field = GetField(descriptor);
            object value = field?.GetValue(config);
            if (value is float number) return number.ToString("0.###", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public static bool TrySetFromText(SimulationConfig config, SimulationParameterDescriptor descriptor,
            string text, out string error)
        {
            error = null;
            if (config == null || descriptor == null)
            {
                error = "Simulation configuration is unavailable.";
                return false;
            }

            float value;
            bool parsed = float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                          float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
            if (!parsed || float.IsNaN(value) || float.IsInfinity(value) ||
                value < descriptor.Minimum || value > descriptor.Maximum)
            {
                error = $"{descriptor.DisplayName} must be within " +
                        $"{descriptor.Minimum:0.###}–{descriptor.Maximum:0.###}.";
                return false;
            }

            return TrySetNumeric(config, descriptor.FieldName, value, out error);
        }

        public static bool TrySetNumeric(SimulationConfig config, string fieldName, float value,
            out string error)
        {
            error = null;
            if (config == null || !TryGet(fieldName, out SimulationParameterDescriptor descriptor))
            {
                error = $"Unsupported simulation parameter '{fieldName}'.";
                return false;
            }
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < descriptor.Minimum || value > descriptor.Maximum)
            {
                error = $"{fieldName} is outside the supported range " +
                        $"{descriptor.Minimum:0.###}–{descriptor.Maximum:0.###}.";
                return false;
            }

            FieldInfo field = GetField(descriptor);
            if (field == null)
            {
                error = $"SimulationConfig.{fieldName} was not found.";
                return false;
            }
            if (field.FieldType == typeof(int)) field.SetValue(config, MathfRoundToInt(value));
            else if (field.FieldType == typeof(float)) field.SetValue(config, value);
            else
            {
                error = $"SimulationConfig.{fieldName} is not numeric.";
                return false;
            }
            return true;
        }

        public static object ReadValue(SimulationConfig config, string fieldName)
        {
            if (config == null || !TryGet(fieldName, out SimulationParameterDescriptor descriptor)) return null;
            return GetField(descriptor)?.GetValue(config);
        }

        public static void RestoreValue(SimulationConfig config, string fieldName, object value)
        {
            if (config == null || !TryGet(fieldName, out SimulationParameterDescriptor descriptor)) return;
            FieldInfo field = GetField(descriptor);
            if (field != null && value != null && field.FieldType.IsInstanceOfType(value))
                field.SetValue(config, value);
        }

        private static FieldInfo GetField(SimulationParameterDescriptor descriptor) =>
            typeof(SimulationConfig).GetField(descriptor.FieldName,
                BindingFlags.Public | BindingFlags.Instance);

        private static SimulationParameterDescriptor Float(string fieldName, string displayName,
            SimulationParameterGroup group, float minimum, float maximum) =>
            new SimulationParameterDescriptor(fieldName, displayName, group, minimum, maximum, true);

        private static SimulationParameterDescriptor Integer(string fieldName, string displayName,
            SimulationParameterGroup group, float minimum, float maximum) =>
            new SimulationParameterDescriptor(fieldName, displayName, group, minimum, maximum, true);

        private static int MathfRoundToInt(float value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
