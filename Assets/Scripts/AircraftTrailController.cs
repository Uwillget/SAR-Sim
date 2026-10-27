using UnityEngine;
using UnityEngine.Rendering;

namespace RoadNetworkEditor
{
    /// <summary>Runtime flight trail bounded to a fixed number of aircraft body lengths.</summary>
    public sealed class AircraftTrailController : MonoBehaviour
    {
        private TrailRenderer trail;
        private Material runtimeMaterial;
        private Vector3 lastPosition;
        private float smoothedSpeed;
        private float targetLength;
        private bool positionInitialized;

        public float TargetLength => targetLength;
        public TrailRenderer Renderer => trail;

        public void Configure(bool enabled, string aircraftId, float bodyLengths = 8f)
        {
            EnsureRenderer(aircraftId);
            float bodyLength = EstimateBodyLength();
            targetLength = bodyLength * Mathf.Clamp(bodyLengths, 5f, 10f);
            trail.minVertexDistance = Mathf.Max(0.12f, bodyLength * 0.12f);
            trail.widthMultiplier = Mathf.Max(0.22f, bodyLength * 0.28f);
            trail.time = Mathf.Clamp(targetLength / Mathf.Max(smoothedSpeed, 8f), 0.15f, 6f);
            trail.emitting = enabled;
            trail.enabled = enabled;
            if (!enabled)
            {
                trail.Clear();
                positionInitialized = false;
                smoothedSpeed = 0f;
            }
        }

        private void EnsureRenderer(string aircraftId)
        {
            if (trail != null) return;
            trail = GetComponent<TrailRenderer>();
            if (trail == null) trail = gameObject.AddComponent<TrailRenderer>();
            trail.autodestruct = false;
            trail.minVertexDistance = 0.4f;
            // TrailRenderer evaluates 0 at the newly emitted head and 1 at the old
            // tail. Keep the head bold, then taper continuously to zero.
            trail.widthCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.45f, 0.62f),
                new Keyframe(0.82f, 0.22f),
                new Keyframe(1f, 0f));
            trail.alignment = LineAlignment.View;
            trail.numCornerVertices = 2;
            trail.numCapVertices = 2;
            trail.shadowCastingMode = ShadowCastingMode.Off;
            trail.receiveShadows = false;
            Color colour = ColourFor(aircraftId);
            trail.colorGradient = new Gradient
            {
                colorKeys = new[]
                {
                    new GradientColorKey(colour, 0f),
                    new GradientColorKey(colour, 1f)
                },
                alphaKeys = new[]
                {
                    new GradientAlphaKey(0.94f, 0f),
                    new GradientAlphaKey(0.76f, 0.35f),
                    new GradientAlphaKey(0.28f, 0.82f),
                    new GradientAlphaKey(0f, 1f)
                }
            };
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            runtimeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (runtimeMaterial.HasProperty("_Color")) runtimeMaterial.color = colour;
            trail.material = runtimeMaterial;
        }

        private void LateUpdate()
        {
            if (trail == null || !trail.enabled || !trail.emitting) return;
            Vector3 current = transform.position;
            if (!positionInitialized)
            {
                lastPosition = current;
                positionInitialized = true;
                return;
            }
            float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
            float instantaneousSpeed = Vector3.Distance(current, lastPosition) / deltaTime;
            lastPosition = current;
            smoothedSpeed = Mathf.Lerp(smoothedSpeed, instantaneousSpeed,
                1f - Mathf.Exp(-6f * deltaTime));
            if (smoothedSpeed > 0.1f)
                trail.time = Mathf.Clamp(targetLength / smoothedSpeed, 0.15f, 6f);
        }

        private float EstimateBodyLength()
        {
            float length = 0f;
            foreach (Renderer candidate in GetComponentsInChildren<Renderer>(true))
            {
                if (candidate == trail) continue;
                Vector3 size = candidate.bounds.size;
                length = Mathf.Max(length, size.x, size.y, size.z);
            }
            return length > 0.1f ? length : 2.5f;
        }

        private static Color ColourFor(string aircraftId)
        {
            int hash = string.IsNullOrEmpty(aircraftId) ? 0 : aircraftId.GetHashCode();
            float hue = Mathf.Repeat((hash & 0xFFFF) / 65535f + 0.48f, 1f);
            return Color.HSVToRGB(hue, 0.72f, 1f);
        }

        private void OnDestroy()
        {
            if (runtimeMaterial != null) Destroy(runtimeMaterial);
        }
    }
}
