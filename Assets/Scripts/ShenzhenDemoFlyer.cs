using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Lightweight kinematic follower for the Shenzhen air-corridor demo.
    /// Drives FlightController targets so AirSimKinematicPublisher can stream poses.
    /// </summary>
    public class ShenzhenDemoFlyer : MonoBehaviour
    {
        public Vector3[] waypoints;
        public int loopFromIndex = 3;
        public float arriveDistance = 4f;

        FlightController _fc;
        int _i;

        public int WaypointIndex { get { return _i; } }

        void Start()
        {
            _fc = GetComponent<FlightController>();
            if (_fc != null)
            {
                _fc.maxSpeed = 18f;
                _fc.moveSpeed = 18f;
                _fc.acceleration = 12f;
                _fc.rotationSpeed = 90f;
            }
            if (waypoints != null && waypoints.Length > 0)
            {
                transform.position = waypoints[0];
                _i = 0;
                PushTarget();
            }
        }

        void Update()
        {
            if (waypoints == null || waypoints.Length < 2) return;
            Vector3 target = waypoints[_i];
            if (_fc == null)
            {
                Vector3 delta = target - transform.position;
                if (delta.sqrMagnitude > 0.01f)
                {
                    Quaternion look = Quaternion.LookRotation(delta.normalized, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, look, Time.deltaTime * 4f);
                    transform.position = Vector3.MoveTowards(transform.position, target, 18f * Time.deltaTime);
                }
            }
            else
            {
                _fc.SetTargetPosition(target);
            }

            if (Vector3.Distance(transform.position, target) <= arriveDistance)
            {
                _i++;
                if (_i >= waypoints.Length)
                    _i = Mathf.Clamp(loopFromIndex, 0, waypoints.Length - 1);
                PushTarget();
            }
        }

        void PushTarget()
        {
            if (_fc != null && waypoints != null && _i < waypoints.Length)
                _fc.SetTargetPosition(waypoints[_i]);
        }
    }
}
