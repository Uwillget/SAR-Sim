using UnityEngine;

namespace RoadNetworkEditor
{
    public class FlightController : MonoBehaviour
    {
        [Header("====== Flight Parameters ======")]
        [Tooltip("Maximum flight speed (units/sec)")]
        public float maxSpeed = 20f;

        [Tooltip("Turning rate (degrees/sec)")]
        public float rotationSpeed = 20f;

        [Tooltip("Acceleration (units/sec^2)")]
        public float acceleration = 20f;

        [Tooltip("Safe following distance")]
        public float safeInterval = 10f;

        [Header("====== Runtime State ======")]
        public Vector3 targetPosition;
        public float waitTime = 0f;
        public float moveSpeed = 20f;
        public float currentWaitTimer = 0f;
        public bool isWaiting = false;
        public bool hasScheduledRelease = false;
        public float scheduledReachTime = 0f;
        public float scheduledReleaseTime = 0f;

        [Header("====== Rotors ======")]
        public Transform rotorA;
        public Transform rotorB;
        public Transform rotorC;
        public Transform rotorD;
        public float rotorSpinSpeed = 800f;

        void Start()
        {
            if (rotorA == null) rotorA = transform.Find("Aircraft/WingA/RotorA");
            if (rotorB == null) rotorB = transform.Find("Aircraft/WingB/RotorB");
            if (rotorC == null) rotorC = transform.Find("Aircraft/WingC/RotorC");
            if (rotorD == null) rotorD = transform.Find("Aircraft/WingD/RotorD");
        }

        void Update()
        {
            RotateRotors();

            if (Vector3.Distance(transform.position, targetPosition) > 0.1f && !isWaiting)
            {
                RotateToTarget();
                MoveToTarget();
            }

            if (Vector3.Distance(transform.position, targetPosition) <= 0.1f &&
                !isWaiting &&
                HasPendingHold())
            {
                isWaiting = true;
                currentWaitTimer = 0f;
                string holdDescription = hasScheduledRelease
                    ? $"until t={scheduledReleaseTime:F2} (reach={scheduledReachTime:F2})"
                    : $"for {waitTime:F2}s";
                Debug.Log($"[FlightController] {name} START WAIT {holdDescription} at pos={transform.position}");
            }

            if (isWaiting)
            {
                currentWaitTimer += Time.deltaTime;
                bool holdComplete = hasScheduledRelease
                    ? Time.time >= scheduledReleaseTime - 0.001f
                    : currentWaitTimer >= waitTime;
                if (holdComplete)
                {
                    Debug.Log($"[FlightController] {name} END WAIT at pos={transform.position}, t={Time.time:F2}");
                    currentWaitTimer = 0f;
                    isWaiting = false;
                    waitTime = 0f;
                    hasScheduledRelease = false;
                }
            }
        }

        /// <summary>Returns true when the aircraft has reached the current target
        /// and completed any required wait. Safe for the agent to advance.</summary>
        public bool HasReachedAndWaited()
        {
            return !isWaiting &&
                   !HasPendingHold() &&
                   Vector3.Distance(transform.position, targetPosition) <= 0.5f;
        }

        public void SetTargetPosition(Vector3 newTarget, float newWaitTime = 0f)
        {
            // Preserve active wait: ignore new target until current wait completes.
            // This prevents the agent from overwriting Dijkstra-planned WaitTime.
            if (isWaiting) return;
            targetPosition = newTarget;
            waitTime = Mathf.Max(0f, newWaitTime);
            hasScheduledRelease = false;
        }

        /// <summary>
        /// Sets a path-planned target whose absolute release time is
        /// ReachTime + WaitTime. Early arrivals hold until that timestamp;
        /// late arrivals do not repeat wait time that has already elapsed.
        /// </summary>
        public void SetScheduledTargetPosition(
            Vector3 newTarget,
            float reachTime,
            float plannedWaitTime)
        {
            if (isWaiting) return;

            targetPosition = newTarget;
            waitTime = Mathf.Max(0f, plannedWaitTime);
            scheduledReachTime = reachTime;
            scheduledReleaseTime = reachTime + waitTime;
            hasScheduledRelease = true;
        }

        public void ClearWaitState()
        {
            waitTime = 0f;
            currentWaitTimer = 0f;
            isWaiting = false;
            hasScheduledRelease = false;
            scheduledReachTime = 0f;
            scheduledReleaseTime = 0f;
        }

        private bool HasPendingHold()
        {
            return hasScheduledRelease
                ? Time.time < scheduledReleaseTime - 0.001f
                : waitTime > 0.001f;
        }

        /// <summary>
        /// Follow-the-leader speed control. Finds the immediate predecessor on this lane
        /// and adjusts speed based on distance to it.
        /// </summary>
        public void SetMoveSpeed(string aircraftId, LaneState laneState)
        {
            var sim = RoadNetworkEditor.SimulationManager.Instance;
            float delay = (sim != null && sim.config != null) ? sim.config.signalPropagationDelay : 0f;

            // Self: always use real-time progress
            float myProgress = -1f;
            foreach (var kvp in laneState.AircraftsInLane)
            {
                if (kvp.Key == aircraftId) { myProgress = kvp.Value; break; }
            }

            if (myProgress < 0f) return;

            // Find the immediate predecessor — use delayed data for OTHER aircraft
            float nextProgress = float.MaxValue;
            bool hasPredecessor = false;
            foreach (var kvp in laneState.AircraftsInLane)
            {
                if (kvp.Value <= myProgress) continue;
                float otherProgress = kvp.Value;
                if (kvp.Key != aircraftId && kvp.Key != "__VIRTUAL__" &&
                    !kvp.Key.StartsWith("__LC_") && !kvp.Key.StartsWith("__ENTRY_"))
                {
                    // Apply signal delay (see delayed position)
                    if (delay > 0f)
                    {
                        float delayed = sim.GetDelayedProgressPublic(kvp.Key);
                        if (delayed >= 0f) otherProgress = delayed;
                    }
                    // Apply positioning error (N(0,σ) in meters, converted to progress)
                    float posErr = (sim != null && sim.config != null) ? sim.config.positioningErrorStdDev : 0f;
                    if (posErr > 0f && laneState.Length > 0f)
                    {
                        // Box-Muller: N(0,σ)
                        float u1 = UnityEngine.Random.value;
                        float u2 = UnityEngine.Random.value;
                        float noise = posErr * Mathf.Sqrt(-2f * Mathf.Log(Mathf.Max(u1, 0.0001f))) * Mathf.Cos(2f * Mathf.PI * u2);
                        otherProgress += noise / laneState.Length;
                    }
                }

                if (otherProgress < nextProgress)
                {
                    nextProgress = otherProgress;
                    hasPredecessor = true;
                }
            }

            if (!hasPredecessor)
            {
                // No aircraft ahead: accelerate to maxSpeed
                if (moveSpeed < maxSpeed)
                    moveSpeed = Mathf.Min(moveSpeed + acceleration * Time.deltaTime, maxSpeed);
                return;
            }

            float distanceToNext = (nextProgress - myProgress) * laneState.Length;
            if (distanceToNext < safeInterval)
            {
                // Threshold = distance to stop from maxSpeed at maxDecel, with 10% safety margin
                var sm = RoadNetworkEditor.SimulationManager.Instance;
                float maxDecel = (sm != null && sm.config != null) ? sm.config.maxDeceleration : 40f;
                float stopDist = (maxSpeed * maxSpeed) / (2f * maxDecel);
                float threshold = stopDist * 2f;
                float decel;
                if (distanceToNext <= threshold)
                    decel = maxDecel;
                else
                    decel = maxDecel * (safeInterval - distanceToNext) / (safeInterval - threshold);
                moveSpeed = Mathf.Max(moveSpeed - decel * Time.deltaTime, 0f);
            }
            else
            {
                if (moveSpeed < maxSpeed)
                    moveSpeed = Mathf.Min(moveSpeed + acceleration * Time.deltaTime, maxSpeed);
            }

            // Continuous-time braking alone can overshoot at high time scales or
            // when a queue leader stops abruptly. This frame-discrete cap prevents
            // the follower from crossing the configured minimum gap even if the
            // predecessor makes no forward progress in the current frame.
            moveSpeed = Mathf.Min(
                moveSpeed,
                FollowingSafetyUtility.CalculateMaximumSpeed(
                    distanceToNext,
                    safeInterval,
                    Mathf.Max(Time.deltaTime, 0.0001f)));
        }

        void MoveToTarget()
        {
            transform.position = Vector3.MoveTowards(
                transform.position,
                targetPosition,
                moveSpeed * Time.deltaTime
            );
        }

        void RotateToTarget()
        {
            Vector3 direction = targetPosition - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) return;

            Quaternion targetRot = Quaternion.LookRotation(direction.normalized);
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRot,
                rotationSpeed * Time.deltaTime
            );
        }

        void RotateRotors()
        {
            float spin = rotorSpinSpeed * Time.deltaTime;

            if (rotorA) rotorA.Rotate(Vector3.forward * spin, Space.Self);
            if (rotorB) rotorB.Rotate(Vector3.forward * -spin, Space.Self);
            if (rotorC) rotorC.Rotate(Vector3.forward * -spin, Space.Self);
            if (rotorD) rotorD.Rotate(Vector3.forward * spin, Space.Self);
        }
    }
}
