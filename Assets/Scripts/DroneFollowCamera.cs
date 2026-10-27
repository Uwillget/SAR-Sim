using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// TPS-style smooth follow camera for visualizing drone movement during simulation.
    /// Attach to the Main Camera. Supports manual Transform assignment, aircraft-ID lookup
    /// via SimulationManager, and Tab-key cycling through active drones.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class DroneFollowCamera : MonoBehaviour
    {
        [Header("====== Follow Target ======")]
        [Tooltip("Drag a drone GameObject here to follow it directly. If both this and Aircraft ID are set, Aircraft ID takes priority.")]
        public Transform targetDrone;

        [Tooltip("Aircraft ID to follow (looked up from SimulationManager at runtime).")]
        public string aircraftId;

        [Tooltip("When no target is assigned, automatically follow the first active drone.")]
        public bool autoFollowFirstDrone = true;

        [Header("====== Drone Cycling ======")]
        [Tooltip("Press this key to cycle to the next active drone.")]
        public KeyCode cycleKey = KeyCode.Tab;

        [Tooltip("Hold this modifier and press Cycle Key to cycle backwards.")]
        public KeyCode cycleReverseModifier = KeyCode.LeftShift;

        [Header("====== View Settings ======")]
        [Tooltip("Camera offset relative to the drone (X:Right, Y:Up, Z:Forward).")]
        public Vector3 offset = new Vector3(0, 2, -5);

        [Tooltip("Position smoothness (lower = more lag / cinematic, higher = tighter follow).")]
        public float smoothSpeed = 5f;

        [Tooltip("Rotation smoothness (lower = slower turn, higher = snappier turn).")]
        public float rotationSmoothness = 10f;

        private SimulationManager simManager;
        private int currentDroneIndex = -1;
        private float cycleCooldown = 0f;
        private const float CycleCooldownDuration = 0.25f;

        private void Start()
        {
            simManager = SimulationManager.Instance;
        }

        private void LateUpdate()
        {
            ResolveTarget();
            HandleCycling();

            if (targetDrone == null) return;

            // Smoothly move the camera to the desired offset position.
            Vector3 desiredPosition = targetDrone.TransformPoint(offset);
            Vector3 smoothedPosition = Vector3.Lerp(
                transform.position, desiredPosition, smoothSpeed * Time.deltaTime);
            transform.position = smoothedPosition;

            // Smoothly rotate the camera to look at the drone.
            Quaternion targetRotation = Quaternion.LookRotation(
                targetDrone.position - transform.position);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, targetRotation, rotationSmoothness * Time.deltaTime);
        }

        /// <summary>
        /// Resolve the follow target from the highest-priority source:
        /// 1. aircraftId lookup in SimulationManager
        /// 2. targetDrone manual assignment
        /// 3. autoFollowFirstDrone fallback
        /// </summary>
        private void ResolveTarget()
        {
            if (simManager == null)
            {
                simManager = SimulationManager.Instance;
                if (simManager == null) return;
            }

            // Priority 1: aircraft ID lookup.
            if (!string.IsNullOrEmpty(aircraftId))
            {
                if (simManager.AircraftObjects.TryGetValue(aircraftId, out GameObject droneObj)
                    && droneObj != null)
                {
                    targetDrone = droneObj.transform;
                    return;
                }
            }

            // Priority 2: manually assigned target is still valid.
            if (targetDrone != null)
                return;

            // Priority 3: auto-follow first active drone.
            if (autoFollowFirstDrone && simManager.AircraftObjects.Count > 0)
            {
                var first = simManager.AircraftObjects.FirstOrDefault(kv => kv.Value != null);
                if (first.Value != null)
                {
                    targetDrone = first.Value.transform;
                    aircraftId = first.Key;
                }
            }
        }

        /// <summary>
        /// Cycle through active drones when the user presses the cycle key.
        /// </summary>
        private void HandleCycling()
        {
            cycleCooldown -= Time.deltaTime;

            if (cycleCooldown > 0f) return;
            if (simManager == null) return;

            bool cycleForward = Input.GetKeyDown(cycleKey);
            if (!cycleForward) return;

            bool reverse = Input.GetKey(cycleReverseModifier);

            List<KeyValuePair<string, GameObject>> activeDrones = simManager.AircraftObjects
                .Where(kv => kv.Value != null)
                .ToList();

            if (activeDrones.Count == 0) return;

            // Determine the new index.
            if (currentDroneIndex < 0 || currentDroneIndex >= activeDrones.Count)
            {
                currentDroneIndex = 0;
            }
            else
            {
                int step = reverse ? -1 : 1;
                currentDroneIndex = (currentDroneIndex + step + activeDrones.Count) % activeDrones.Count;
            }

            var selected = activeDrones[currentDroneIndex];
            targetDrone = selected.Value.transform;
            aircraftId = selected.Key;
            cycleCooldown = CycleCooldownDuration;

            Debug.Log($"[DroneFollowCamera] Switched to drone: {aircraftId} ({currentDroneIndex + 1}/{activeDrones.Count})");
        }

        /// <summary>
        /// Programmatically set the target to a specific aircraft by ID.
        /// </summary>
        public void FollowAircraft(string id)
        {
            aircraftId = id;
            targetDrone = null; // force lookup on next ResolveTarget
        }

        /// <summary>
        /// Clear the current target (camera stops following).
        /// </summary>
        public void ClearTarget()
        {
            targetDrone = null;
            aircraftId = null;
            currentDroneIndex = -1;
        }
    }
}
