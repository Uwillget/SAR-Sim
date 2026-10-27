using UnityEngine;

namespace RoadNetworkEditor
{
    public class ShenzhenDemoFollowCamera : MonoBehaviour
    {
        public Transform target;
        public Vector3 offset = new Vector3(0f, 28f, -70f);
        public float lerp = 0.12f;

        void LateUpdate()
        {
            if (target == null) return;
            Vector3 desired = target.position + target.rotation * offset;
            transform.position = Vector3.Lerp(transform.position, desired, lerp);
            transform.LookAt(target.position + Vector3.up * 4f);
        }
    }
}
