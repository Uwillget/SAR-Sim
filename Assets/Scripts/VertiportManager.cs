using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

using Newtonsoft.Json;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RoadNetworkEditor
{
    [ExecuteAlways]
    public class VertiportManager : MonoBehaviour
    {
        [Header("Vertiport Data")]
        public List<Vertiport> Vertiports = new List<Vertiport>();

        [Header("Visual Settings")]
        public float circleWidth = 0.15f;
        public float vertiportWidth = 0.2f;
        public float helipadWidth = 0.15f;
        public float vertexSize = 0.4f;
        public int circleSegments = 64;
        [Tooltip("The holding-ring base circle is a generation aid only. Disable it to show only nodes and created directed ramps.")]
        public bool showCircleGuides = false;
        public bool showTextAnnotations = true;
        public float corridorTangentRatio = 0.3f;

        // Visual dictionaries
        private Dictionary<string, VertiportVisual> vertiportVisuals = new Dictionary<string, VertiportVisual>();
        private Dictionary<string, HelipadVisual> helipadVisuals = new Dictionary<string, HelipadVisual>();
        private Dictionary<string, CircleVisual> circleVisuals = new Dictionary<string, CircleVisual>();
        private Dictionary<string, VertexVisual> vertexVisuals = new Dictionary<string, VertexVisual>();
        private Dictionary<string, RampVisual> rampVisuals = new Dictionary<string, RampVisual>();
        private Dictionary<string, ApproachNodeVisual> approachNodeVisuals = new Dictionary<string, ApproachNodeVisual>();
        private Dictionary<string, ApproachEdgeVisual> approachEdgeVisuals = new Dictionary<string, ApproachEdgeVisual>();
        private Dictionary<string, CorridorVisual> corridorVisuals = new Dictionary<string, CorridorVisual>();

        // Selection
        private VertiportVisual selectedVertiport;
        private HelipadVisual selectedHelipad;
        private CircleVisual selectedCircle;
        private VertexVisual selectedVertex;
        private RampVisual selectedRamp;
        private ApproachNodeVisual selectedApproachNode;
        private ApproachEdgeVisual selectedApproachEdge;
        private CorridorVisual selectedCorridor;

        // Containers — only root container; per-VP sub-containers are created on demand
        private Transform vertiportsContainer;

        [SerializeField] private bool _manuallyCleared = false;

        // Visual objects mirror the serialized facility model. Rebuilding or unloading
        // those mirrors must never be interpreted as an explicit user deletion.
        [System.NonSerialized] private bool suppressVisualDataDeletion;
        public bool IsVisualDataDeletionSuppressed => suppressVisualDataDeletion;

        private void Awake()
        {
            if (Vertiports == null)
                Vertiports = new List<Vertiport>();

            Transform oldV = transform.Find("Vertiports");
            bool previousSuppression = suppressVisualDataDeletion;
            suppressVisualDataDeletion = true;
            try
            {
                if (oldV != null) DestroyImmediate(oldV.gameObject);
            }
            finally
            {
                suppressVisualDataDeletion = previousSuppression;
            }

            vertiportVisuals.Clear();
            helipadVisuals.Clear();
            circleVisuals.Clear();
            vertexVisuals.Clear();
            rampVisuals.Clear();
            approachNodeVisuals.Clear();
            approachEdgeVisuals.Clear();
            corridorVisuals.Clear();

            InitializeContainers();

            if (Vertiports.Count > 0)
                RebuildVisualsFromData();
        }

        private const string DEFAULT_JSON_PATH = "Assets/DataBase/VertiportData.json";

        private void OnEnable()
        {
            suppressVisualDataDeletion = false;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (vertiportsContainer == null)
                    InitializeContainers();

                if (ShouldAutoLoadFromJSON())
                {
                    Debug.Log("[VertiportManager] Data empty, auto-loading from JSON...");
                    AutoLoadFromJSON();
                }
                else
                {
                    EnsureVisualsSyncWithData();
                }

                EditorApplication.delayCall += () =>
                {
                    if (this == null) return;

                    if (vertiportsContainer == null)
                        InitializeContainers();

                    if (ShouldAutoLoadFromJSON())
                    {
                        Debug.Log("[VertiportManager] Data empty, auto-loading from JSON...");
                        AutoLoadFromJSON();
                    }
                    else
                    {
                        EnsureVisualsSyncWithData();
                    }
                };
            }
#endif
        }

        private void OnDisable()
        {
            // Scene unloads, domain reloads, test isolation and temporary editor
            // deactivation may destroy DontSave visual children after this callback.
            suppressVisualDataDeletion = true;
        }

        private void Start()
        {
            EnsureVisualsSyncWithData();
        }

        private void EnsureVisualsSyncWithData()
        {
            if (Vertiports == null) return;
            bool hasData = Vertiports.Count > 0;
            if (!hasData) return;

            // Tuple-based reverse links are not serialized by Unity. Reconstruct them
            // from the authoritative corridor records whenever scene data is awakened.
            RebuildRampCorridorReferences();
            SyncCorridorsToNetworkLanes(exportJson: false);

            if (vertiportsContainer == null)
                InitializeContainers();

            // DontSave runtime containers created in Edit mode are destroyed while
            // entering Play mode. Their dictionary entries may still have counts,
            // but every UnityEngine.Object value compares equal to null. Detect the
            // actual hierarchy/objects rather than relying on dictionary counts.
            bool missingRoot = vertiportsContainer == null;
            bool missingVertiportContainer = !missingRoot && Vertiports.Any(vp =>
                vp != null && vertiportsContainer.Find($"Vertiport_{vp.VertiportId}") == null);
            bool missingVisuals = vertiportVisuals.Count == 0 ||
                                  vertiportVisuals.Values.All(visual => visual == null);
            if (missingRoot || missingVertiportContainer || missingVisuals)
            {
                Debug.Log("[VertiportManager] Data exists but visuals missing, rebuilding...");
                RebuildVisualsFromData();
            }
        }

        /// <summary>
        /// The original scene keeps this manager GameObject inactive and accesses it
        /// through SimulationManager. Runtime editing still needs its lookup tables
        /// and child containers, so callers can explicitly initialize them without
        /// relying on Awake/Start being invoked.
        /// </summary>
        public void EnsureRuntimeEditingReady()
        {
            EnsureVisualsSyncWithData();
        }

        private bool ShouldAutoLoadFromJSON()
        {
            if (_manuallyCleared) return false;
            bool dataIsEmpty = (Vertiports == null || Vertiports.Count == 0);
            if (!dataIsEmpty) return false;
            return File.Exists(DEFAULT_JSON_PATH);
        }

        private void AutoLoadFromJSON()
        {
            if (!File.Exists(DEFAULT_JSON_PATH))
            {
                Debug.LogWarning($"[VertiportManager] JSON file not found at {DEFAULT_JSON_PATH}");
                return;
            }

            try
            {
                string json = File.ReadAllText(DEFAULT_JSON_PATH);
                JsonSerializerSettings settings = new JsonSerializerSettings
                {
                    Converters = new List<JsonConverter> { new Vector3Converter() }
                };

                List<Vertiport> imported = JsonConvert.DeserializeObject<List<Vertiport>>(json, settings);
                if (imported == null)
                {
                    Debug.LogError("[VertiportManager] Failed to parse JSON file.");
                    return;
                }

                ClearAllData();
                Vertiports = imported;
                _manuallyCleared = false;
                RebuildVisualsFromData();

#if UNITY_EDITOR
                EditorUtility.SetDirty(this);
#endif
                Debug.Log($"[VertiportManager] Auto-loaded {imported.Count} vertiports from JSON.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[VertiportManager] Auto-load failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void InitializeContainers()
        {
            Transform vt = transform.Find("Vertiports");
            if (vt == null)
            {
                GameObject go = new GameObject("Vertiports");
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(transform);
                go.transform.localPosition = Vector3.zero;
                vertiportsContainer = go.transform;
            }
            else vertiportsContainer = vt;
        }

        /// <summary>Get or create a named sub-container under the given Vertiport container.</summary>
        private Transform GetOrCreateVPSubContainer(string vertiportId, string subName)
        {
            if (vertiportsContainer == null) InitializeContainers();
            Transform vpContainer = vertiportsContainer.Find($"Vertiport_{vertiportId}");
            if (vpContainer == null)
            {
                Vertiport data = Vertiports?.Find(vp => vp != null && vp.VertiportId == vertiportId);
                if (data == null) return null;
                GameObject vpGo = new GameObject($"Vertiport_{vertiportId}");
                vpGo.hideFlags = HideFlags.DontSave;
                vpGo.transform.SetParent(vertiportsContainer);
                vpGo.transform.localPosition = Vector3.zero;
                VertiportVisual visual = vpGo.AddComponent<VertiportVisual>();
                visual.Initialize(data, this);
                vertiportVisuals[vertiportId] = visual;
                vpContainer = vpGo.transform;
            }

            Transform sub = vpContainer.Find(subName);
            if (sub == null)
            {
                GameObject go = new GameObject(subName);
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(vpContainer);
                go.transform.localPosition = Vector3.zero;
                sub = go.transform;
            }
            return sub;
        }

        #region Vertiport Operations

        public VertiportVisual CreateVertiport(List<Vector3> boundary)
        {
            if (vertiportsContainer == null) InitializeContainers();

            Vertiport vp = new Vertiport
            {
                VertiportId = GenerateVertiportId(),
                Boundary = new List<Vector3>(boundary),
                Helipads = new List<Helipad>(),
                HoldingStructures = new List<HoldingStructure>()
            };

            Vertiports.Add(vp);
            _manuallyCleared = false;

            GameObject go = new GameObject($"Vertiport_{vp.VertiportId}");
            go.transform.SetParent(vertiportsContainer);
            go.transform.localPosition = Vector3.zero;

            // Create sub-containers under the VP
            foreach (string sub in new[] { "Helipads", "HoldingStructures", "ApproachNet", "Corridors" })
                GetOrCreateVPSubContainer(vp.VertiportId, sub);

            VertiportVisual visual = go.AddComponent<VertiportVisual>();
            visual.Initialize(vp, this);
            vertiportVisuals[vp.VertiportId] = visual;

            Debug.Log($"Created vertiport {vp.VertiportId}");
            return visual;
        }

        public void DeleteVertiport(string id, bool destroyGameObject = true)
        {
            if (!vertiportVisuals.ContainsKey(id)) return;

            Vertiport vp = Vertiports.Find(v => v.VertiportId == id);
            if (vp == null) return;

            // Cascade delete helipads
            foreach (var hp in vp.Helipads.ToList())
                DeleteHelipad(hp.HelipadId);
            // Cascade delete holding structures
            foreach (var hs in vp.HoldingStructures.ToList())
                DeleteHoldingStructure(hs.HoldingStructureId);

            VertiportVisual visual = vertiportVisuals[id];
            if (destroyGameObject && visual != null)
                DestroyImmediate(visual.gameObject);

            vertiportVisuals.Remove(id);
            Vertiports.Remove(vp);

            if (selectedVertiport == visual) selectedVertiport = null;
        }

        public void UpdateVertiportBoundary(string id, List<Vector3> boundary)
        {
            Vertiport vp = Vertiports.Find(v => v.VertiportId == id);
            if (vp == null) return;
            vp.Boundary = new List<Vector3>(boundary);
            if (vertiportVisuals.TryGetValue(id, out VertiportVisual visual))
                visual.UpdateVisual();
        }

        /// <summary>Offset every element of a vertiport by a delta vector.</summary>
        public void OffsetVertiport(string vpId, Vector3 delta)
        {
            var vp = Vertiports.Find(v => v.VertiportId == vpId);
            if (vp == null || delta.sqrMagnitude < 0.0001f) return;

            for (int i = 0; i < vp.Boundary.Count; i++) vp.Boundary[i] += delta;
            foreach (var hp in vp.Helipads)
                for (int i = 0; i < hp.Boundary.Count; i++) hp.Boundary[i] += delta;
            foreach (var hs in vp.HoldingStructures)
            {
                hs.Position += delta;
                foreach (var v in hs.Vertices) v.Position += delta;
                foreach (var c in hs.Circles) c.Center += delta;
            }
            if (vp.ApproachNet != null)
                foreach (var n in vp.ApproachNet.Nodes) n.Position += delta;
            foreach (var c in vp.Corridors)
            { c.LaneLinkPoint += delta; c.RampLinkPoint += delta; }

            string savedSelection = (selectedVertiport != null) ? selectedVertiport.VertiportId : null;
            RebuildVisualsFromData();
            if (savedSelection != null && vertiportVisuals.TryGetValue(savedSelection, out var vpVis))
                SelectVertiport(vpVis);
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>Rotate every element of a vertiport around a center point.</summary>
        public void RotateVertiport(string vpId, Vector3 center, float angleDeg)
        {
            var vp = Vertiports.Find(v => v.VertiportId == vpId);
            if (vp == null || Mathf.Approximately(angleDeg, 0f)) return;

            Quaternion rot = Quaternion.Euler(0, angleDeg, 0);
            Vector3 Rot(Vector3 p) => center + rot * (p - center);

            for (int i = 0; i < vp.Boundary.Count; i++) vp.Boundary[i] = Rot(vp.Boundary[i]);
            foreach (var hp in vp.Helipads)
                for (int i = 0; i < hp.Boundary.Count; i++) hp.Boundary[i] = Rot(hp.Boundary[i]);
            foreach (var hs in vp.HoldingStructures)
            {
                hs.Position = Rot(hs.Position);
                foreach (var v in hs.Vertices) v.Position = Rot(v.Position);
                foreach (var c in hs.Circles) c.Center = Rot(c.Center);
            }
            if (vp.ApproachNet != null)
                foreach (var n in vp.ApproachNet.Nodes) n.Position = Rot(n.Position);
            foreach (var c in vp.Corridors)
            { c.LaneLinkPoint = Rot(c.LaneLinkPoint); c.RampLinkPoint = Rot(c.RampLinkPoint); }

            string savedSelection = (selectedVertiport != null) ? selectedVertiport.VertiportId : null;
            RebuildVisualsFromData();
            if (savedSelection != null && vertiportVisuals.TryGetValue(savedSelection, out var vpVis))
                SelectVertiport(vpVis);
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        #endregion

        #region Helipad Operations

        public HelipadVisual CreateHelipad(string vertiportId, List<Vector3> boundary)
        {
            Vertiport vp = Vertiports.Find(v => v.VertiportId == vertiportId);
            if (vp == null)
            {
                Debug.LogError($"Vertiport {vertiportId} not found.");
                return null;
            }

            Transform hpContainer = GetOrCreateVPSubContainer(vertiportId, "Helipads");
            if (hpContainer == null) { Debug.LogError($"VP container for {vertiportId} not found."); return null; }

            Helipad hp = new Helipad
            {
                HelipadId = GenerateHelipadId(vertiportId),
                Boundary = new List<Vector3>(boundary)
            };

            vp.Helipads.Add(hp);
            _manuallyCleared = false;

            GameObject go = new GameObject($"Helipad_{hp.HelipadId}");
            go.transform.SetParent(hpContainer);
            go.transform.localPosition = Vector3.zero;

            HelipadVisual visual = go.AddComponent<HelipadVisual>();
            visual.Initialize(hp, vertiportId, this);
            helipadVisuals[hp.HelipadId] = visual;

            // Auto-create an approach node at the helipad geometric center
            Vector3 center = Vector3.zero;
            foreach (var p in boundary) center += p;
            center /= boundary.Count;
            CreateApproachNode(vertiportId, center);

            Debug.Log($"Created helipad {hp.HelipadId} with approach node at center, in vertiport {vertiportId}");
            return visual;
        }

        public void DeleteHelipad(string helipadId, bool destroyGameObject = true)
        {
            if (!helipadVisuals.ContainsKey(helipadId)) return;

            HelipadVisual visual = helipadVisuals[helipadId];
            string parentVpId = visual.ParentVertiportId;
            Vertiport vp = Vertiports.Find(v => v.VertiportId == parentVpId);
            if (vp != null)
            {
                Helipad hp = vp.Helipads.Find(h => h.HelipadId == helipadId);
                if (hp != null) vp.Helipads.Remove(hp);
            }

            if (destroyGameObject && visual != null)
                DestroyImmediate(visual.gameObject);

            helipadVisuals.Remove(helipadId);
            if (selectedHelipad == visual) selectedHelipad = null;
        }

        /// <summary>
        /// Snap the approach node nearest to a helipad's geometric center to that center.
        /// Call after modifying helipad boundary points.
        /// </summary>
        public void SyncApproachNodeToHelipad(string helipadId)
        {
            Helipad hp = null;
            Vertiport parentVp = null;
            foreach (var vp in Vertiports)
            {
                hp = vp.Helipads.Find(h => h.HelipadId == helipadId);
                if (hp != null) { parentVp = vp; break; }
            }
            if (hp == null || hp.Boundary == null || hp.Boundary.Count == 0 || parentVp == null) return;

            // Compute geometric center
            Vector3 center = Vector3.zero;
            foreach (var p in hp.Boundary) center += p;
            center /= hp.Boundary.Count;

            // Find nearest approach node (within reasonable range)
            ApproachNode bestNode = null;
            float bestDist = float.MaxValue;
            if (parentVp.ApproachNet?.Nodes != null)
            {
                foreach (var node in parentVp.ApproachNet.Nodes)
                {
                    float d = Vector3.Distance(node.Position, center);
                    if (d < bestDist) { bestDist = d; bestNode = node; }
                }
            }

            // Snap if within 20 units and not an anchor node
            if (bestNode != null && bestDist < 20f && !bestNode.IsAnchor)
            {
                bestNode.Position = center;
                if (approachNodeVisuals.TryGetValue(bestNode.NodeId, out var vis))
                    vis.transform.position = center;
            }
        }

        #endregion

        #region HoldingStructure Operations

        public GameObject CreateHoldingStructure(string vertiportId, Vector3 position)
        {
            Vertiport vp = Vertiports.Find(v => v.VertiportId == vertiportId);
            if (vp == null)
            {
                Debug.LogError($"Vertiport {vertiportId} not found.");
                return null;
            }

            Transform hsContainer = GetOrCreateVPSubContainer(vertiportId, "HoldingStructures");
            if (hsContainer == null) { Debug.LogError($"VP container for {vertiportId} not found."); return null; }

            HoldingStructure hs = new HoldingStructure
            {
                HoldingStructureId = GenerateHoldingStructureId(vertiportId),
                Position = position,
                Circles = new List<Circle>(),
                Vertices = new List<Vertex>(),
                Ramps = new List<Ramp>()
            };

            vp.HoldingStructures.Add(hs);
            _manuallyCleared = false;

            GameObject go = new GameObject($"HS_{hs.HoldingStructureId}");
            go.transform.SetParent(hsContainer);
            go.transform.position = position;

            // Add a small collider so the HS can be clicked in the Scene view
            SphereCollider col = go.AddComponent<SphereCollider>();
            col.radius = 0.4f;
            col.isTrigger = true;

            Debug.Log($"Created holding structure {hs.HoldingStructureId} in vertiport {vertiportId} (anchors defined by approach net connections)");
            return go;
        }

        public void DeleteHoldingStructure(string id)
        {
            // Find the HS in all vertiports and its container GO
            HoldingStructure hs = null;
            Vertiport parentVp = null;
            foreach (var vp in Vertiports)
            {
                hs = vp.HoldingStructures.Find(h => h.HoldingStructureId == id);
                if (hs != null) { parentVp = vp; break; }
            }
            if (hs == null) return;

            // Cascade delete circles
            foreach (var c in hs.Circles.ToList())
                DeleteCircle(c.CircleId);
            // Cascade delete ramps
            foreach (var r in hs.Ramps.ToList())
                DeleteRamp(r.RampId);
            // Cascade delete vertices
            foreach (var v in hs.Vertices.ToList())
                DeleteVertex(v.VertexId);

            parentVp.HoldingStructures.Remove(hs);

            // Destroy container GO (under VP/HoldingStructures)
            Transform hsParent = GetOrCreateVPSubContainer(parentVp.VertiportId, "HoldingStructures");
            if (hsParent != null)
            {
                Transform hsContainer = hsParent.Find($"HS_{id}");
                if (hsContainer != null)
                    DestroyImmediate(hsContainer.gameObject);
            }
        }

        /// <summary>
        /// Move a holding-structure anchor and every resource owned by it as one unit.
        /// Data and runtime visuals are both updated so imported structures behave the
        /// same way as structures created during the current Play session.
        /// </summary>
        public bool MoveHoldingStructure(string id, Vector3 position)
        {
            HoldingStructure hs = null;
            Vertiport parentVp = null;
            foreach (Vertiport vp in Vertiports)
            {
                hs = vp.HoldingStructures?.Find(item => item.HoldingStructureId == id);
                if (hs != null)
                {
                    parentVp = vp;
                    break;
                }
            }
            if (hs == null || parentVp == null) return false;

            Vector3 delta = position - hs.Position;
            if (delta.sqrMagnitude <= 0.000001f) return true;

            hs.Position = position;
            foreach (Circle circle in hs.Circles ?? new List<Circle>())
                circle.Center += delta;
            foreach (Vertex vertex in hs.Vertices ?? new List<Vertex>())
                vertex.Position += delta;

            // Approach anchor nodes are the bridge between the ground approach network
            // and a holding structure. Keep them attached to their linked vertex (or to
            // the legacy HS centre) when the whole structure is translated.
            foreach (ApproachNode node in parentVp.ApproachNet?.Nodes ?? new List<ApproachNode>())
            {
                if (node == null || !node.IsAnchor || node.LinkHSId != id) continue;
                Vertex linkedVertex = !string.IsNullOrEmpty(node.LinkVertexId)
                    ? hs.Vertices?.FirstOrDefault(vertex => vertex.VertexId == node.LinkVertexId)
                    : null;
                Vector3 target = linkedVertex != null ? linkedVertex.Position : hs.Position;
                if (approachNodeVisuals.TryGetValue(node.NodeId, out ApproachNodeVisual approachVisual) &&
                    approachVisual != null)
                    approachVisual.UpdatePosition(target);
                else
                    node.Position = target;
            }

            Transform hsParent = GetOrCreateVPSubContainer(parentVp.VertiportId, "HoldingStructures");
            Transform hsContainer = hsParent != null ? hsParent.Find($"HS_{id}") : null;
            if (hsContainer != null)
            {
                // Circle/vertex/ramp visuals are descendants. Moving the common
                // container avoids applying the delta twice to world transforms.
                hsContainer.position += delta;
            }
            else
            {
                EnsureRuntimeEditingReady();
            }

            foreach (Circle circle in hs.Circles ?? new List<Circle>())
                if (circleVisuals.TryGetValue(circle.CircleId, out CircleVisual circleVisual) && circleVisual != null)
                    circleVisual.UpdateVisual();
            foreach (Ramp ramp in hs.Ramps ?? new List<Ramp>())
                if (rampVisuals.TryGetValue(ramp.RampId, out RampVisual rampVisual) && rampVisual != null)
                    rampVisual.UpdateVisual();

            return true;
        }

        /// <summary>Find the parent HoldingStructure for a given Circle.</summary>
        public HoldingStructure FindParentHSForCircle(string circleId)
        {
            foreach (var vp in Vertiports)
                foreach (var hs in vp.HoldingStructures)
                    if (hs.Circles.Any(c => c.CircleId == circleId))
                        return hs;
            return null;
        }

        /// <summary>Find the parent Vertiport for a given HoldingStructure.</summary>
        public Vertiport FindParentVertiportForHS(string hsId)
        {
            foreach (var vp in Vertiports)
                if (vp.HoldingStructures.Any(h => h.HoldingStructureId == hsId))
                    return vp;
            return null;
        }

        #endregion

        #region Circle Operations

        public CircleVisual CreateCircle(string holdingStructureId, Vector3 center, float radius, int vertexCount, float initialAngle = 0f)
        {
            HoldingStructure hs = null;
            Vertiport parentVp = null;
            foreach (var vp in Vertiports)
            {
                hs = vp.HoldingStructures.Find(h => h.HoldingStructureId == holdingStructureId);
                if (hs != null) { parentVp = vp; break; }
            }
            if (hs == null)
            {
                Debug.LogError($"HoldingStructure {holdingStructureId} not found.");
                return null;
            }

            Transform hsParent = GetOrCreateVPSubContainer(parentVp.VertiportId, "HoldingStructures");
            Transform hsContainer = hsParent != null ? hsParent.Find($"HS_{holdingStructureId}") : null;
            if (hsContainer == null)
            {
                Debug.LogError($"HoldingStructure container for {holdingStructureId} not found.");
                return null;
            }

            Circle circle = new Circle
            {
                CircleId = GenerateCircleId(holdingStructureId),
                Center = center,
                Radius = radius,
                InitialAngle = initialAngle,
                CoveredVertices = new List<string>()
            };

            hs.Circles.Add(circle);
            _manuallyCleared = false;

            GameObject go = new GameObject($"Circle_{circle.CircleId}");
            go.transform.SetParent(hsContainer);
            go.transform.position = center;

            CircleVisual visual = go.AddComponent<CircleVisual>();
            visual.Initialize(circle, holdingStructureId, this);
            circleVisuals[circle.CircleId] = visual;

            // Auto-generate vertices evenly distributed around the circle
            for (int i = 0; i < vertexCount; i++)
            {
                float angle = (initialAngle + (360f / vertexCount) * i) * Mathf.Deg2Rad;
                Vector3 pos = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                CreateVertexInternal(circle.CircleId, pos, hs, go.transform);
            }

            Debug.Log($"Created circle {circle.CircleId} with {vertexCount} vertices (initialAngle={initialAngle})");
            return visual;
        }

        public void DeleteCircle(string circleId)
        {
            if (!circleVisuals.TryGetValue(circleId, out CircleVisual visual)) return;

            Circle circle = visual.CircleData;
            // Cascade delete vertices
            foreach (var vId in circle.CoveredVertices.ToList())
                DeleteVertex(vId);
            // Cascade delete ramps connected to this circle's vertices
            HoldingStructure hs = FindParentHSForCircle(circleId);
            if (hs != null)
            {
                foreach (var r in hs.Ramps.ToList())
                {
                    if (r.LinkCircleId == circleId)
                        DeleteRamp(r.RampId);
                }
            }

            if (hs != null) hs.Circles.Remove(circle);
            circleVisuals.Remove(circleId);
            if (selectedCircle == visual) selectedCircle = null;
            DestroyImmediate(visual.gameObject);
        }

        public void UpdateCircle(string circleId, Vector3 center, float radius, int vertexCount, float initialAngle = -1f)
        {
            if (!circleVisuals.TryGetValue(circleId, out CircleVisual visual)) return;
            Circle circle = visual.CircleData;

            HoldingStructure hs = FindParentHSForCircle(circleId);
            if (hs == null) return;

            // Remove old vertices
            foreach (var vId in circle.CoveredVertices.ToList())
                DeleteVertex(vId);

            circle.Center = center;
            circle.Radius = radius;
            circle.CoveredVertices.Clear();

            visual.transform.position = center;
            if (initialAngle >= 0f) circle.InitialAngle = initialAngle;
            visual.UpdateVisual();

            // Regenerate vertices
            Transform circleTransform = visual.transform;
            for (int i = 0; i < vertexCount; i++)
            {
                float angle = (circle.InitialAngle + (360f / vertexCount) * i) * Mathf.Deg2Rad;
                Vector3 pos = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                CreateVertexInternal(circleId, pos, hs, circleTransform);
            }

            // Update connected ramps
            foreach (var r in hs.Ramps)
            {
                if (r.LinkCircleId == circleId && rampVisuals.TryGetValue(r.RampId, out RampVisual rv))
                    rv.UpdateVisual();
            }
        }

        #endregion

        #region Vertex Operations

        /// <summary>Internal creation without ID generation — used by CreateCircle.</summary>
        private VertexVisual CreateVertexInternal(string circleId, Vector3 position, HoldingStructure hs, Transform parentTransform)
        {
            Vertex v = new Vertex
            {
                VertexId = GenerateVertexId(hs.HoldingStructureId),
                Position = position,
                ConnectedRamps = new List<string>(),
                AccessibleVertices = new List<string>(),
                LinkCircleId = circleId
            };

            hs.Vertices.Add(v);

            Circle circle = hs.Circles.Find(c => c.CircleId == circleId);
            if (circle != null)
                circle.CoveredVertices.Add(v.VertexId);

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"Vertex_{v.VertexId}";
            go.transform.SetParent(parentTransform);
            go.transform.position = position;
            go.transform.localScale = Vector3.one * vertexSize;

            // Keep collider as trigger for editor raycast picking
            Collider col = go.GetComponent<Collider>();
            if (col != null) col.isTrigger = true;

            VertexVisual visual = go.AddComponent<VertexVisual>();
            visual.Initialize(v, circleId, this);
            vertexVisuals[v.VertexId] = visual;

            return visual;
        }

        public VertexVisual CreateVertex(string circleId, Vector3 position)
        {
            HoldingStructure hs = FindParentHSForCircle(circleId);
            if (hs == null)
            {
                Debug.LogError($"Parent HoldingStructure for circle {circleId} not found.");
                return null;
            }

            if (!circleVisuals.TryGetValue(circleId, out CircleVisual circleVisual))
            {
                Debug.LogError($"Circle {circleId} visual not found.");
                return null;
            }

            return CreateVertexInternal(circleId, position, hs, circleVisual.transform);
        }

        public void DeleteVertex(string vertexId, bool destroyGameObject = true)
        {
            if (!vertexVisuals.TryGetValue(vertexId, out VertexVisual visual)) return;

            Vertex v = visual.VertexData;

            // Cascade delete connected ramps
            HoldingStructure hs = null;
            foreach (var vp in Vertiports)
            {
                hs = vp.HoldingStructures.Find(h => h.Vertices.Any(vx => vx.VertexId == vertexId));
                if (hs != null) break;
            }

            if (hs != null)
            {
                foreach (var rId in v.ConnectedRamps.ToList())
                    DeleteRamp(rId);

                hs.Vertices.Remove(v);

                // Remove from circle's CoveredVertices
                Circle circle = hs.Circles.Find(c => c.CircleId == v.LinkCircleId);
                if (circle != null)
                    circle.CoveredVertices.Remove(vertexId);
            }

            vertexVisuals.Remove(vertexId);
            if (selectedVertex == visual) selectedVertex = null;
            if (destroyGameObject && visual != null)
                DestroyImmediate(visual.gameObject);
        }

        public void UpdateVertexPosition(string vertexId, Vector3 pos)
        {
            if (!vertexVisuals.TryGetValue(vertexId, out VertexVisual visual)) return;
            visual.UpdatePosition(pos);
        }

        #endregion

        #region Ramp Operations

        public RampVisual CreateRamp(string fromVertexId, string toVertexId, ArcDirection direction = ArcDirection.Shortest)
        {
            if (!vertexVisuals.TryGetValue(fromVertexId, out VertexVisual fromVis) ||
                !vertexVisuals.TryGetValue(toVertexId, out VertexVisual toVis))
            {
                Debug.LogError("From or to vertex not found.");
                return null;
            }

            // Determine ramp type
            string fromCircleId = fromVis.VertexData.LinkCircleId;
            string toCircleId = toVis.VertexData.LinkCircleId;
            string linkCircleId = (fromCircleId == toCircleId) ? fromCircleId : null;

            // Find parent HS
            HoldingStructure hs = null;
            foreach (var vp in Vertiports)
            {
                hs = vp.HoldingStructures.Find(h =>
                    h.Vertices.Any(vx => vx.VertexId == fromVertexId));
                if (hs != null) break;
            }
            if (hs == null)
            {
                Debug.LogError("Parent HoldingStructure not found.");
                return null;
            }

            Ramp ramp = new Ramp
            {
                RampId = GenerateRampId(hs.HoldingStructureId),
                FromVertexId = fromVertexId,
                ToVertexId = toVertexId,
                LinkCircleId = linkCircleId,
                Direction = direction
            };

            hs.Ramps.Add(ramp);
            _manuallyCleared = false;

            // Add to vertex connections
            fromVis.VertexData.ConnectedRamps.Add(ramp.RampId);
            toVis.VertexData.ConnectedRamps.Add(ramp.RampId);
            if (!fromVis.VertexData.AccessibleVertices.Contains(toVertexId))
                fromVis.VertexData.AccessibleVertices.Add(toVertexId);

            Vertiport rampVp = FindParentVertiportForHS(hs.HoldingStructureId);
            Transform hsParent = rampVp != null ? GetOrCreateVPSubContainer(rampVp.VertiportId, "HoldingStructures") : null;
            Transform hsContainer = hsParent != null ? hsParent.Find($"HS_{hs.HoldingStructureId}") : null;
            if (hsContainer == null)
            {
                Debug.LogError($"HS container not found for {hs.HoldingStructureId}");
                return null;
            }

            GameObject go = new GameObject($"Ramp_{ramp.RampId}");
            go.transform.SetParent(hsContainer);
            go.transform.localPosition = Vector3.zero;

            RampVisual visual = go.AddComponent<RampVisual>();
            visual.Initialize(ramp, fromVis, toVis, linkCircleId, this);
            rampVisuals[ramp.RampId] = visual;

            string typeLabel = linkCircleId != null ? "arc" : "straight";
            Debug.Log($"Created {typeLabel} ramp {ramp.RampId}: {fromVertexId} -> {toVertexId}");
            RecomputeReachableCorridors(rampVp.VertiportId);
            return visual;
        }

        public void DeleteRamp(string rampId)
        {
            if (!rampVisuals.TryGetValue(rampId, out RampVisual visual)) return;

            Ramp ramp = visual.RampData;

            // Remove from vertices
            if (vertexVisuals.TryGetValue(ramp.FromVertexId, out VertexVisual fromVis))
                fromVis.VertexData.ConnectedRamps.Remove(rampId);
            if (vertexVisuals.TryGetValue(ramp.ToVertexId, out VertexVisual toVis))
                toVis.VertexData.ConnectedRamps.Remove(rampId);

            // Remove from HS
            foreach (var vp in Vertiports)
            {
                foreach (var hs in vp.HoldingStructures)
                {
                    if (hs.Ramps.Remove(ramp))
                    {
                        rampVisuals.Remove(rampId);
                        if (selectedRamp == visual) selectedRamp = null;
                        DestroyImmediate(visual.gameObject);
                        RecomputeReachableCorridors(vp.VertiportId);
                        return;
                    }
                }
            }
        }

        #endregion

        #region ApproachNetwork Operations

        public ApproachNodeVisual CreateApproachNode(string vertiportId, Vector3 position, bool isAnchor = false, string linkHSId = null, string linkVertexId = null)
        {
            Vertiport vp = Vertiports.Find(v => v.VertiportId == vertiportId);
            if (vp == null)
            {
                Debug.LogError($"Vertiport {vertiportId} not found.");
                return null;
            }

            if (vp.ApproachNet == null) vp.ApproachNet = new ApproachNetwork();
            _manuallyCleared = false;

            ApproachNode node = new ApproachNode
            {
                NodeId = GenerateApproachNodeId(vertiportId),
                Position = position,
                ConnectedEdges = new List<string>(),
                IsAnchor = isAnchor,
                LinkHSId = linkHSId,
                LinkVertexId = linkVertexId
            };

            vp.ApproachNet.Nodes.Add(node);

            Transform apprContainer = GetOrCreateVPSubContainer(vertiportId, "ApproachNet");
            if (apprContainer == null)
            {
                Debug.LogError($"VP container for {vertiportId} not found.");
                return null;
            }

            GameObject nGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            nGo.name = $"ApprNode_{node.NodeId}";
            nGo.transform.SetParent(apprContainer);
            nGo.transform.position = position;
            nGo.transform.localScale = Vector3.one * vertexSize;
            Collider col = nGo.GetComponent<Collider>();
            if (col != null) col.isTrigger = true;

            ApproachNodeVisual visual = nGo.AddComponent<ApproachNodeVisual>();
            visual.Initialize(node, vertiportId, this);
            approachNodeVisuals[node.NodeId] = visual;

            Debug.Log($"Created approach node {node.NodeId} (anchor={isAnchor}) in vertiport {vertiportId}");
            return visual;
        }

        public void DeleteApproachNode(string nodeId)
        {
            if (!approachNodeVisuals.TryGetValue(nodeId, out ApproachNodeVisual visual)) return;

            ApproachNode node = visual.NodeData;
            string parentVpId = visual.ParentVertiportId;
            Vertiport vp = Vertiports.Find(v => v.VertiportId == parentVpId);
            if (vp != null && vp.ApproachNet != null)
            {
                // Cascade delete connected edges
                foreach (var eId in node.ConnectedEdges.ToList())
                    DeleteApproachEdge(eId);

                vp.ApproachNet.Nodes.Remove(node);
            }

            approachNodeVisuals.Remove(nodeId);
            if (selectedApproachNode == visual) selectedApproachNode = null;
            DestroyImmediate(visual.gameObject);
        }

        public ApproachEdgeVisual CreateApproachEdge(string vertiportId, string fromNodeId, string toNodeId)
        {
            if (!approachNodeVisuals.TryGetValue(fromNodeId, out ApproachNodeVisual fromVis) ||
                !approachNodeVisuals.TryGetValue(toNodeId, out ApproachNodeVisual toVis))
            {
                Debug.LogError("From or to approach node not found.");
                return null;
            }

            Vertiport vp = Vertiports.Find(v => v.VertiportId == vertiportId);
            if (vp == null || vp.ApproachNet == null)
            {
                Debug.LogError($"Vertiport {vertiportId} or its ApproachNetwork not found.");
                return null;
            }

            ApproachEdge edge = new ApproachEdge
            {
                EdgeId = GenerateApproachEdgeId(vertiportId),
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId
            };

            vp.ApproachNet.Edges.Add(edge);
            _manuallyCleared = false;

            fromVis.NodeData.ConnectedEdges.Add(edge.EdgeId);
            toVis.NodeData.ConnectedEdges.Add(edge.EdgeId);

            Transform apprContainer = GetOrCreateVPSubContainer(vertiportId, "ApproachNet");
            if (apprContainer == null)
            {
                Debug.LogError($"VP container for {vertiportId} not found.");
                return null;
            }

            GameObject eGo = new GameObject($"ApprEdge_{edge.EdgeId}");
            eGo.transform.SetParent(apprContainer);
            eGo.transform.localPosition = Vector3.zero;

            ApproachEdgeVisual visual = eGo.AddComponent<ApproachEdgeVisual>();
            visual.Initialize(edge, fromVis, toVis, this);
            approachEdgeVisuals[edge.EdgeId] = visual;

            Debug.Log($"Created approach edge {edge.EdgeId}: {fromNodeId} -> {toNodeId}");
            RecomputeReachableCorridors(vertiportId);
            return visual;
        }

        public void DeleteApproachEdge(string edgeId)
        {
            if (!approachEdgeVisuals.TryGetValue(edgeId, out ApproachEdgeVisual visual)) return;

            ApproachEdge edge = visual.EdgeData;

            if (approachNodeVisuals.TryGetValue(edge.FromNodeId, out ApproachNodeVisual fromVis))
                fromVis.NodeData.ConnectedEdges.Remove(edgeId);
            if (approachNodeVisuals.TryGetValue(edge.ToNodeId, out ApproachNodeVisual toVis))
                toVis.NodeData.ConnectedEdges.Remove(edgeId);

            foreach (var vp in Vertiports)
            {
                if (vp.ApproachNet != null && vp.ApproachNet.Edges.Remove(edge))
                {
                    approachEdgeVisuals.Remove(edgeId);
                    if (selectedApproachEdge == visual) selectedApproachEdge = null;
                    DestroyImmediate(visual.gameObject);
                    RecomputeReachableCorridors(vp.VertiportId);
                    return;
                }
            }
        }

        /// <summary>Find or create an anchor approach node linked to an HS vertex.</summary>
        public ApproachNodeVisual FindOrCreateAnchorApprNode(string hsId, string vertexId = null)
        {
            // Look for existing anchor node linked to this HS (and optionally specific vertex)
            foreach (var vp in Vertiports)
            {
                if (vp.ApproachNet == null) continue;
                foreach (var node in vp.ApproachNet.Nodes)
                {
                    if (node.IsAnchor && node.LinkHSId == hsId)
                    {
                        if (vertexId == null || node.LinkVertexId == vertexId)
                        {
                            if (approachNodeVisuals.TryGetValue(node.NodeId, out ApproachNodeVisual vis))
                                return vis;
                        }
                    }
                }
            }

            // Not found — create one
            Vertiport parentVp = FindParentVertiportForHS(hsId);
            if (parentVp == null)
            {
                Debug.LogError($"Parent Vertiport for HS {hsId} not found.");
                return null;
            }

            HoldingStructure hs = parentVp.HoldingStructures.Find(h => h.HoldingStructureId == hsId);
            if (hs == null) return null;

            Vector3 pos = hs.Position;
            if (vertexId != null)
            {
                var vtx = hs.Vertices.FirstOrDefault(v => v.VertexId == vertexId);
                if (vtx != null) pos = vtx.Position;
            }

            return CreateApproachNode(parentVp.VertiportId, pos, isAnchor: true, linkHSId: hsId, linkVertexId: vertexId);
        }

        /// <summary>Find the parent Vertiport for a given ApproachNode.</summary>
        public Vertiport FindParentVertiportForApprNode(string nodeId)
        {
            foreach (var vp in Vertiports)
                if (vp.ApproachNet != null && vp.ApproachNet.Nodes.Any(n => n.NodeId == nodeId))
                    return vp;
            return null;
        }

        #endregion

        #region Corridor Operations

        public CorridorVisual CreateCorridor(string vertiportId, string corridorType,
            string linkLaneId, string linkRampId, Vector3 laneLinkPoint, Vector3 rampLinkPoint)
        {
            Vertiport vp = Vertiports.Find(v => v.VertiportId == vertiportId);
            if (vp == null)
            {
                Debug.LogError($"Vertiport {vertiportId} not found.");
                return null;
            }

            NetConnectedCorridor corridor = new NetConnectedCorridor
            {
                CorridorId = GenerateCorridorId(vertiportId),
                CorridorType = corridorType,
                LinkLaneId = linkLaneId,
                LinkRampId = linkRampId,
                LaneLinkPoint = laneLinkPoint,
                RampLinkPoint = rampLinkPoint
            };

            vp.Corridors.Add(corridor);
            _manuallyCleared = false;

            // Update Ramp.LinkCorridors
            foreach (var hs in vp.HoldingStructures)
            {
                Ramp ramp = hs.Ramps.Find(r => r.RampId == linkRampId);
                if (ramp != null)
                {
                    ramp.LinkCorridors.Add((corridor.CorridorId, rampLinkPoint));
                    break;
                }
            }

            // Update Lane.LinkCorridors in NetworkManager
            UpdateLaneLinkCorridors(corridor.CorridorId, linkLaneId, laneLinkPoint, add: true);

            Transform corrContainer = GetOrCreateVPSubContainer(vertiportId, "Corridors");
            if (corrContainer == null)
            {
                Debug.LogError($"VP container for {vertiportId} not found.");
                return null;
            }

            GameObject go = new GameObject($"Corridor_{corridor.CorridorId}");
            go.transform.SetParent(corrContainer);
            go.transform.localPosition = Vector3.zero;

            CorridorVisual visual = go.AddComponent<CorridorVisual>();
            visual.Initialize(corridor, vertiportId, this);
            corridorVisuals[corridor.CorridorId] = visual;

            Debug.Log($"Created corridor {corridor.CorridorId} ({corridorType}): lane {linkLaneId} <-> ramp {linkRampId}");
            return visual;
        }

        public void DeleteCorridor(string corridorId)
        {
            corridorVisuals.TryGetValue(corridorId, out CorridorVisual visual);
            NetConnectedCorridor corridor = visual != null ? visual.CorridorData : Vertiports
                .Where(vp => vp?.Corridors != null)
                .SelectMany(vp => vp.Corridors)
                .FirstOrDefault(candidate => candidate != null && candidate.CorridorId == corridorId);
            if (corridor == null) return;

            // Remove from Lane.LinkCorridors in NetworkManager
            UpdateLaneLinkCorridors(corridor.CorridorId, corridor.LinkLaneId, corridor.LaneLinkPoint, add: false);

            // Remove from Ramp.LinkCorridors
            foreach (var vp in Vertiports)
            {
                foreach (var hs in vp.HoldingStructures)
                {
                    Ramp ramp = hs.Ramps.Find(r => r.RampId == corridor.LinkRampId);
                    if (ramp != null)
                    {
                        ramp.LinkCorridors.RemoveAll(t => t.Item1 == corridorId);
                    }
                }

                // Remove from VP.Corridors
                vp.Corridors.RemoveAll(c => c.CorridorId == corridorId);
            }

            corridorVisuals.Remove(corridorId);
            if (selectedCorridor == visual) selectedCorridor = null;
            if (visual != null)
            {
                if (Application.isPlaying) Destroy(visual.gameObject);
                else DestroyImmediate(visual.gameObject);
            }
        }

        /// <summary>Find the VertiportVisual that has a Corridor with the given ID.</summary>
        public VertiportVisual FindParentVertiportForCorridor(string corridorId)
        {
            foreach (var vp in Vertiports)
                if (vp.Corridors.Any(c => c.CorridorId == corridorId))
                    return vertiportVisuals.TryGetValue(vp.VertiportId, out VertiportVisual v) ? v : null;
            return null;
        }

        #endregion

        #region Selection

        public void SelectVertiport(VertiportVisual v)
        {
            ClearSelectionInternal();
            selectedVertiport = v;
            if (selectedVertiport != null) selectedVertiport.SetSelected(true);
        }

        public void SelectHelipad(HelipadVisual h)
        {
            ClearSelectionInternal();
            selectedHelipad = h;
            if (selectedHelipad != null) selectedHelipad.SetSelected(true);
        }

        public void SelectCircle(CircleVisual c)
        {
            ClearSelectionInternal();
            selectedCircle = c;
            if (selectedCircle != null) selectedCircle.SetSelected(true);
        }

        public void SelectVertex(VertexVisual v)
        {
            ClearSelectionInternal();
            selectedVertex = v;
            if (selectedVertex != null) selectedVertex.SetSelected(true);
        }

        public void SelectRamp(RampVisual r)
        {
            ClearSelectionInternal();
            selectedRamp = r;
            if (selectedRamp != null) selectedRamp.SetSelected(true);
        }

        public void SelectApproachNode(ApproachNodeVisual n)
        {
            ClearSelectionInternal();
            selectedApproachNode = n;
            if (selectedApproachNode != null) selectedApproachNode.SetSelected(true);
        }

        public void SelectApproachEdge(ApproachEdgeVisual e)
        {
            ClearSelectionInternal();
            selectedApproachEdge = e;
            if (selectedApproachEdge != null) selectedApproachEdge.SetSelected(true);
        }

        public void SelectCorridor(CorridorVisual c)
        {
            ClearSelectionInternal();
            selectedCorridor = c;
            if (selectedCorridor != null) selectedCorridor.SetSelected(true);
        }

        public void ClearSelection()
        {
            ClearSelectionInternal();
        }

        private void ClearSelectionInternal()
        {
            if (selectedVertiport != null) { selectedVertiport.SetSelected(false); selectedVertiport = null; }
            if (selectedHelipad != null) { selectedHelipad.SetSelected(false); selectedHelipad = null; }
            if (selectedCircle != null) { selectedCircle.SetSelected(false); selectedCircle = null; }
            if (selectedVertex != null) { selectedVertex.SetSelected(false); selectedVertex = null; }
            if (selectedRamp != null) { selectedRamp.SetSelected(false); selectedRamp = null; }
            if (selectedApproachNode != null) { selectedApproachNode.SetSelected(false); selectedApproachNode = null; }
            if (selectedApproachEdge != null) { selectedApproachEdge.SetSelected(false); selectedApproachEdge = null; }
            if (selectedCorridor != null) { selectedCorridor.SetSelected(false); selectedCorridor = null; }
        }

        public VertiportVisual GetSelectedVertiport() => selectedVertiport;
        public HelipadVisual GetSelectedHelipad() => selectedHelipad;
        public CircleVisual GetSelectedCircle() => selectedCircle;
        public VertexVisual GetSelectedVertex() => selectedVertex;
        public RampVisual GetSelectedRamp() => selectedRamp;
        public ApproachNodeVisual GetSelectedApproachNode() => selectedApproachNode;
        public ApproachEdgeVisual GetSelectedApproachEdge() => selectedApproachEdge;
        public CorridorVisual GetSelectedCorridor() => selectedCorridor;

        #endregion

        #region Persistence

        public void ExportToJSON()
        {
            string outputPath = DEFAULT_JSON_PATH;
#if !UNITY_EDITOR
            outputPath = Path.Combine(SarRuntimePaths.NetworkRoot, "VertiportData.json");
#endif
            string directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Converters = new List<JsonConverter> { new Vector3Converter() }
            };

            // Sync lane LinkCorridors to NetworkManager before export
            SyncCorridorsToNetworkLanes();

            string json = JsonConvert.SerializeObject(Vertiports, settings);
            File.WriteAllText(outputPath, json);

#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif
            Debug.Log($"[VertiportManager] Exported {Vertiports.Count} vertiports to: {outputPath}");
        }

        public void ImportFromJSON(string path = null)
        {
            string importPath = path ?? DEFAULT_JSON_PATH;
            if (!File.Exists(importPath))
            {
                Debug.LogError($"JSON file not found: {importPath}");
                return;
            }

            try
            {
                string json = File.ReadAllText(importPath);
                var settings = new JsonSerializerSettings
                {
                    Converters = new List<JsonConverter> { new Vector3Converter() }
                };

                List<Vertiport> imported = JsonConvert.DeserializeObject<List<Vertiport>>(json, settings);
                if (imported == null)
                {
                    Debug.LogError("Failed to parse JSON.");
                    return;
                }

                ClearAllData();
                Vertiports = imported;
                _manuallyCleared = false;
                RebuildVisualsFromData();

#if UNITY_EDITOR
                EditorUtility.SetDirty(this);
#endif
                Debug.Log($"[VertiportManager] Imported {imported.Count} vertiports from: {importPath}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Import failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Replace all facility data from an arbitrary JSON file. Deserialization is
        /// performed before clearing the live scene to keep failed imports reversible.
        /// </summary>
        public bool TryImportFromJSON(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "The facility-structure file path is empty.";
                return false;
            }

            string importPath;
            try { importPath = Path.GetFullPath(path); }
            catch (System.Exception ex)
            {
                error = $"The facility-structure file path is invalid: {ex.Message}";
                return false;
            }
            if (!File.Exists(importPath))
            {
                error = $"The facility-structure file does not exist: {importPath}";
                return false;
            }

            List<Vertiport> previous = Vertiports;
            bool liveDataChanged = false;
            try
            {
                var settings = new JsonSerializerSettings
                {
                    Converters = new List<JsonConverter> { new Vector3Converter() }
                };
                List<Vertiport> imported = JsonConvert.DeserializeObject<List<Vertiport>>(
                    File.ReadAllText(importPath), settings);
                if (imported == null)
                {
                    error = "The facility-structure JSON is empty or malformed.";
                    return false;
                }
                if (!TryPrepareImportedVertiports(imported, out error)) return false;

                ClearAllData(false, clearDataList: false);
                Vertiports = imported;
                liveDataChanged = true;
                _manuallyCleared = false;
                RebuildVisualsFromData();
#if UNITY_EDITOR
                EditorUtility.SetDirty(this);
#endif
                Debug.Log($"[VertiportManager] Imported {Vertiports.Count} vertiport(s) from {importPath}.");
                return true;
            }
            catch (System.Exception ex)
            {
                if (liveDataChanged)
                {
                    try
                    {
                        ClearAllData(false, clearDataList: false);
                        Vertiports = previous ?? new List<Vertiport>();
                        RebuildVisualsFromData();
                    }
                    catch (System.Exception rollbackException)
                    {
                        Debug.LogError($"[VertiportManager] Facility rollback failed: {rollbackException}");
                    }
                }
                error = $"Failed to import the facility structure: {ex.Message}";
                Debug.LogError($"[VertiportManager] {error}\n{ex.StackTrace}");
                return false;
            }
        }

        private static bool TryPrepareImportedVertiports(List<Vertiport> imported, out string error)
        {
            error = null;
            var vertiportIds = new HashSet<string>(System.StringComparer.Ordinal);
            var helipadIds = new HashSet<string>(System.StringComparer.Ordinal);
            var holdingIds = new HashSet<string>(System.StringComparer.Ordinal);
            var circleIds = new HashSet<string>(System.StringComparer.Ordinal);
            var vertexIds = new HashSet<string>(System.StringComparer.Ordinal);
            var rampIds = new HashSet<string>(System.StringComparer.Ordinal);
            var approachNodeIds = new HashSet<string>(System.StringComparer.Ordinal);
            var approachEdgeIds = new HashSet<string>(System.StringComparer.Ordinal);
            var corridorIds = new HashSet<string>(System.StringComparer.Ordinal);

            foreach (Vertiport vertiport in imported)
            {
                if (vertiport == null || string.IsNullOrWhiteSpace(vertiport.VertiportId) ||
                    !vertiportIds.Add(vertiport.VertiportId))
                {
                    error = "The facility structure contains an empty or duplicate vertiport ID.";
                    return false;
                }
                vertiport.Boundary = vertiport.Boundary ?? new List<Vector3>();
                vertiport.Helipads = vertiport.Helipads ?? new List<Helipad>();
                vertiport.HoldingStructures = vertiport.HoldingStructures ?? new List<HoldingStructure>();
                vertiport.ApproachNet = vertiport.ApproachNet ?? new ApproachNetwork();
                vertiport.ApproachNet.Nodes = vertiport.ApproachNet.Nodes ?? new List<ApproachNode>();
                vertiport.ApproachNet.Edges = vertiport.ApproachNet.Edges ?? new List<ApproachEdge>();
                vertiport.Corridors = vertiport.Corridors ?? new List<NetConnectedCorridor>();

                foreach (Helipad helipad in vertiport.Helipads)
                {
                    if (helipad == null || string.IsNullOrWhiteSpace(helipad.HelipadId) ||
                        !helipadIds.Add(helipad.HelipadId))
                    {
                        error = "The facility structure contains an empty or duplicate helipad ID.";
                        return false;
                    }
                    helipad.Boundary = helipad.Boundary ?? new List<Vector3>();
                    helipad.ReachableCorridorIds = helipad.ReachableCorridorIds ?? new List<string>();
                }

                foreach (HoldingStructure holding in vertiport.HoldingStructures)
                {
                    if (holding == null || string.IsNullOrWhiteSpace(holding.HoldingStructureId) ||
                        !holdingIds.Add(holding.HoldingStructureId))
                    {
                        error = "The facility structure contains an empty or duplicate holding-structure ID.";
                        return false;
                    }
                    holding.Circles = holding.Circles ?? new List<Circle>();
                    holding.Vertices = holding.Vertices ?? new List<Vertex>();
                    holding.Ramps = holding.Ramps ?? new List<Ramp>();
                    var localCircleIds = new HashSet<string>(System.StringComparer.Ordinal);
                    var localVertexIds = new HashSet<string>(System.StringComparer.Ordinal);
                    foreach (Circle circle in holding.Circles)
                    {
                        if (circle == null || string.IsNullOrWhiteSpace(circle.CircleId) ||
                            !circleIds.Add(circle.CircleId) || !localCircleIds.Add(circle.CircleId))
                        {
                            error = "The facility structure contains an empty or duplicate holding-ring ID.";
                            return false;
                        }
                        circle.CoveredVertices = circle.CoveredVertices ?? new List<string>();
                    }
                    foreach (Vertex vertex in holding.Vertices)
                    {
                        if (vertex == null || string.IsNullOrWhiteSpace(vertex.VertexId) ||
                            !vertexIds.Add(vertex.VertexId) || !localVertexIds.Add(vertex.VertexId))
                        {
                            error = "The facility structure contains an empty or duplicate holding-node ID.";
                            return false;
                        }
                        vertex.ConnectedRamps = vertex.ConnectedRamps ?? new List<string>();
                        vertex.AccessibleVertices = vertex.AccessibleVertices ?? new List<string>();
                    }
                    foreach (Ramp ramp in holding.Ramps)
                    {
                        if (ramp == null || string.IsNullOrWhiteSpace(ramp.RampId) ||
                            !rampIds.Add(ramp.RampId))
                        {
                            error = "The facility structure contains an empty or duplicate holding-ramp ID.";
                            return false;
                        }
                        if (!localVertexIds.Contains(ramp.FromVertexId ?? string.Empty) ||
                            !localVertexIds.Contains(ramp.ToVertexId ?? string.Empty) ||
                            (!string.IsNullOrEmpty(ramp.LinkCircleId) &&
                             !localCircleIds.Contains(ramp.LinkCircleId)))
                        {
                            error = $"Holding ramp {ramp.RampId} references a missing node or holding ring.";
                            return false;
                        }
                        ramp.LinkCorridors = ramp.LinkCorridors ?? new List<(string, Vector3)>();
                    }
                }

                var localApproachNodes = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (ApproachNode node in vertiport.ApproachNet.Nodes)
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.NodeId) ||
                        !approachNodeIds.Add(node.NodeId) || !localApproachNodes.Add(node.NodeId))
                    {
                        error = "The facility structure contains an empty or duplicate approach-node ID.";
                        return false;
                    }
                    node.ConnectedEdges = new List<string>();
                }
                foreach (ApproachEdge edge in vertiport.ApproachNet.Edges)
                {
                    if (edge == null || string.IsNullOrWhiteSpace(edge.EdgeId) ||
                        !approachEdgeIds.Add(edge.EdgeId))
                    {
                        error = "The facility structure contains an empty or duplicate approach-edge ID.";
                        return false;
                    }
                    if (!localApproachNodes.Contains(edge.FromNodeId ?? string.Empty) ||
                        !localApproachNodes.Contains(edge.ToNodeId ?? string.Empty))
                    {
                        error = $"Approach edge {edge.EdgeId} references a missing node.";
                        return false;
                    }
                    vertiport.ApproachNet.Nodes.First(node => node.NodeId == edge.FromNodeId)
                        .ConnectedEdges.Add(edge.EdgeId);
                    vertiport.ApproachNet.Nodes.First(node => node.NodeId == edge.ToNodeId)
                        .ConnectedEdges.Add(edge.EdgeId);
                }
                foreach (NetConnectedCorridor corridor in vertiport.Corridors)
                {
                    if (corridor == null || string.IsNullOrWhiteSpace(corridor.CorridorId) ||
                        !corridorIds.Add(corridor.CorridorId) ||
                        string.IsNullOrWhiteSpace(corridor.LinkLaneId) ||
                        string.IsNullOrWhiteSpace(corridor.LinkRampId))
                    {
                        error = "The facility structure contains an empty, duplicate, or disconnected facility corridor.";
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>Export all facility data to a caller-selected path.</summary>
        public string ExportToJSON(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new System.ArgumentException("Output path is required.", nameof(outputPath));
            string path = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Converters = new List<JsonConverter> { new Vector3Converter() }
            };
            SyncCorridorsToNetworkLanes();
            File.WriteAllText(path, JsonConvert.SerializeObject(Vertiports, settings));
            Debug.Log($"[VertiportManager] Exported {Vertiports.Count} vertiport(s) to: {path}");
            return path;
        }

        /// <summary>Add or remove a corridor entry from a lane's LinkCorridors list.</summary>
        private void UpdateLaneLinkCorridors(string corridorId, string linkLaneId, Vector3 linkPoint, bool add)
        {
            NetworkManager netMgr = FindObjectOfType<NetworkManager>();
            if (netMgr == null || netMgr.FullNetwork == null) return;

            foreach (var edge in netMgr.FullNetwork.Edges)
            {
                Lane lane = edge.Lanes.Find(l => l.LaneId == linkLaneId);
                if (lane != null)
                {
                    if (add)
                        lane.LinkCorridors.Add((corridorId, linkPoint));
                    else
                        lane.LinkCorridors.RemoveAll(t => t.Item1 == corridorId);

#if UNITY_EDITOR
                    EditorUtility.SetDirty(netMgr);
                    netMgr.ExportFullNetworkToJSON();
#endif
                    return;
                }
            }
        }

        /// <summary>Sync corridor cross-references back to NetworkManager lanes.</summary>
        private void RebuildRampCorridorReferences()
        {
            if (Vertiports == null) return;
            var rampsById = new Dictionary<string, Ramp>(System.StringComparer.Ordinal);
            foreach (Vertiport vertiport in Vertiports.Where(item => item != null))
            {
                foreach (HoldingStructure holding in vertiport.HoldingStructures ?? new List<HoldingStructure>())
                {
                    foreach (Ramp ramp in holding.Ramps ?? new List<Ramp>())
                    {
                        if (ramp == null || string.IsNullOrWhiteSpace(ramp.RampId)) continue;
                        ramp.LinkCorridors = ramp.LinkCorridors ?? new List<(string, Vector3)>();
                        ramp.LinkCorridors.Clear();
                        rampsById[ramp.RampId] = ramp;
                    }
                }
            }

            foreach (Vertiport vertiport in Vertiports.Where(item => item != null))
            {
                foreach (NetConnectedCorridor corridor in vertiport.Corridors ?? new List<NetConnectedCorridor>())
                {
                    if (corridor == null || string.IsNullOrWhiteSpace(corridor.CorridorId)) continue;
                    if (rampsById.TryGetValue(corridor.LinkRampId ?? string.Empty, out Ramp ramp))
                        ramp.LinkCorridors.Add((corridor.CorridorId, corridor.RampLinkPoint));
                }
            }
        }

        private void SyncCorridorsToNetworkLanes(bool exportJson = true)
        {
            RebuildRampCorridorReferences();
            SimulationManager simulation = Resources.FindObjectsOfTypeAll<SimulationManager>()
                .FirstOrDefault(item => item != null && item.gameObject.scene.IsValid() &&
                                        item.vertiportManager == this);
            NetworkManager netMgr = simulation != null ? simulation.networkManager : null;
            if (netMgr == null || netMgr.FullNetwork == null) return;

            // Clear all existing LinkCorridors on lanes
            foreach (var edge in netMgr.FullNetwork.Edges)
                foreach (var lane in edge.Lanes)
                    lane.LinkCorridors.Clear();

            // Rebuild from current Vertiport corridor data
            foreach (var vp in Vertiports)
            {
                foreach (var corridor in vp.Corridors)
                {
                    foreach (var edge in netMgr.FullNetwork.Edges)
                    {
                        Lane lane = edge.Lanes.Find(l => l.LaneId == corridor.LinkLaneId);
                        if (lane != null)
                        {
                            lane.LinkCorridors.Add((corridor.CorridorId, corridor.LaneLinkPoint));
                            break;
                        }
                    }
                }
            }

#if UNITY_EDITOR
            if (exportJson)
            {
                EditorUtility.SetDirty(netMgr);
                netMgr.ExportFullNetworkToJSON();
            }
#endif
        }

        #endregion

        #region Utility

        public void ClearAllData(bool isManualClear = true, bool clearDataList = true)
        {
            var oldVertiportVisuals = new List<VertiportVisual>(vertiportVisuals.Values);
            var oldHelipadVisuals = new List<HelipadVisual>(helipadVisuals.Values);
            var oldCircleVisuals = new List<CircleVisual>(circleVisuals.Values);
            var oldVertexVisuals = new List<VertexVisual>(vertexVisuals.Values);
            var oldRampVisuals = new List<RampVisual>(rampVisuals.Values);
            var oldApproachNodeVisuals = new List<ApproachNodeVisual>(approachNodeVisuals.Values);
            var oldApproachEdgeVisuals = new List<ApproachEdgeVisual>(approachEdgeVisuals.Values);
            var oldCorridorVisuals = new List<CorridorVisual>(corridorVisuals.Values);

            vertiportVisuals.Clear();
            helipadVisuals.Clear();
            circleVisuals.Clear();
            vertexVisuals.Clear();
            rampVisuals.Clear();
            approachNodeVisuals.Clear();
            approachEdgeVisuals.Clear();
            corridorVisuals.Clear();

            bool previousSuppression = suppressVisualDataDeletion;
            suppressVisualDataDeletion = true;
            try
            {
                foreach (var v in oldCorridorVisuals) { if (v != null) DestroyImmediate(v.gameObject); }
                foreach (var v in oldApproachEdgeVisuals) { if (v != null) DestroyImmediate(v.gameObject); }
                foreach (var v in oldApproachNodeVisuals) { if (v != null) DestroyImmediate(v.gameObject); }
                foreach (var v in oldRampVisuals) { if (v != null) DestroyImmediate(v.gameObject); }
                foreach (var v in oldVertexVisuals) { if (v != null) DestroyImmediate(v.gameObject); }
                foreach (var v in oldCircleVisuals) { if (v != null) DestroyImmediate(v.gameObject); }
                foreach (var v in oldHelipadVisuals) { if (v != null) DestroyImmediate(v.gameObject); }
                foreach (var v in oldVertiportVisuals) { if (v != null) DestroyImmediate(v.gameObject); }

                // Destroy VP containers (which cascades to all sub-containers).
                if (vertiportsContainer != null)
                {
                    for (int i = vertiportsContainer.childCount - 1; i >= 0; i--)
                        DestroyImmediate(vertiportsContainer.GetChild(i).gameObject);
                }
            }
            finally
            {
                suppressVisualDataDeletion = previousSuppression;
            }

            if (clearDataList)
                Vertiports.Clear();

            if (isManualClear) _manuallyCleared = true;
            ClearSelectionInternal();
        }

        public void RebuildVisualsFromData()
        {
            RebuildRampCorridorReferences();
            SyncCorridorsToNetworkLanes(exportJson: false);
            if (vertiportsContainer == null) InitializeContainers();
            ClearAllData(false, clearDataList: false);

            if (Vertiports == null || Vertiports.Count == 0) return;

            foreach (var vp in Vertiports)
            {
                // Vertiport container
                GameObject vpGo = new GameObject($"Vertiport_{vp.VertiportId}");
                vpGo.transform.SetParent(vertiportsContainer);
                vpGo.transform.localPosition = Vector3.zero;
                VertiportVisual vpVis = vpGo.AddComponent<VertiportVisual>();
                vpVis.Initialize(vp, this);
                vertiportVisuals[vp.VertiportId] = vpVis;

                // Sub-containers
                Transform hpContainer = GetOrCreateVPSubContainer(vp.VertiportId, "Helipads");
                Transform hsContainer = GetOrCreateVPSubContainer(vp.VertiportId, "HoldingStructures");
                Transform apprContainer = GetOrCreateVPSubContainer(vp.VertiportId, "ApproachNet");
                Transform corrContainer = GetOrCreateVPSubContainer(vp.VertiportId, "Corridors");

                // Helipad visuals
                foreach (var hp in vp.Helipads)
                {
                    GameObject hpGo = new GameObject($"Helipad_{hp.HelipadId}");
                    hpGo.transform.SetParent(hpContainer);
                    hpGo.transform.localPosition = Vector3.zero;
                    HelipadVisual hpVis = hpGo.AddComponent<HelipadVisual>();
                    hpVis.Initialize(hp, vp.VertiportId, this);
                    helipadVisuals[hp.HelipadId] = hpVis;
                }

                // Holding structure containers + contents
                foreach (var hs in vp.HoldingStructures)
                {
                    GameObject hsGo = new GameObject($"HS_{hs.HoldingStructureId}");
                    hsGo.transform.SetParent(hsContainer);
                    hsGo.transform.position = hs.Position;

                    // Visual anchor sphere
                    GameObject anchor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    anchor.name = "Anchor";
                    anchor.transform.SetParent(hsGo.transform);
                    anchor.transform.localPosition = Vector3.zero;
                    anchor.transform.localScale = Vector3.one * 0.6f;

                    // Attach VertexVisual for anchor vertices (LinkCircleId is null or empty)
                    foreach (var v in hs.Vertices)
                    {
                        if (!string.IsNullOrEmpty(v.LinkCircleId)) continue;
                        VertexVisual vVis = anchor.AddComponent<VertexVisual>();
                        vVis.Initialize(v, null, this);
                        vertexVisuals[v.VertexId] = vVis;
                        break;
                    }

                    // Circles + vertices
                    foreach (var c in hs.Circles)
                    {
                        GameObject cGo = new GameObject($"Circle_{c.CircleId}");
                        cGo.transform.SetParent(hsGo.transform);
                        cGo.transform.position = c.Center;
                        CircleVisual cVis = cGo.AddComponent<CircleVisual>();
                        cVis.Initialize(c, hs.HoldingStructureId, this);
                        circleVisuals[c.CircleId] = cVis;

                        foreach (var vId in c.CoveredVertices)
                        {
                            Vertex v = hs.Vertices.Find(vx => vx.VertexId == vId);
                            if (v == null) continue;

                            GameObject vGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                            vGo.name = $"Vertex_{v.VertexId}";
                            vGo.transform.SetParent(cGo.transform);
                            vGo.transform.position = v.Position;
                            vGo.transform.localScale = Vector3.one * vertexSize;
                            Collider col = vGo.GetComponent<Collider>();
                            if (col != null) col.isTrigger = true;

                            VertexVisual vVis = vGo.AddComponent<VertexVisual>();
                            vVis.Initialize(v, c.CircleId, this);
                            vertexVisuals[v.VertexId] = vVis;
                        }
                    }

                    // Ramps
                    foreach (var r in hs.Ramps)
                    {
                        if (!vertexVisuals.TryGetValue(r.FromVertexId, out VertexVisual fromVis) ||
                            !vertexVisuals.TryGetValue(r.ToVertexId, out VertexVisual toVis))
                            continue;

                        GameObject rGo = new GameObject($"Ramp_{r.RampId}");
                        rGo.transform.SetParent(hsGo.transform);
                        rGo.transform.localPosition = Vector3.zero;
                        RampVisual rVis = rGo.AddComponent<RampVisual>();
                        rVis.Initialize(r, fromVis, toVis, r.LinkCircleId, this);
                        rampVisuals[r.RampId] = rVis;
                    }
                }

                // ApproachNetwork visuals
                if (vp.ApproachNet != null)
                {
                    foreach (var node in vp.ApproachNet.Nodes)
                    {
                        GameObject nGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        nGo.name = $"ApprNode_{node.NodeId}";
                        nGo.transform.SetParent(apprContainer);
                        nGo.transform.position = node.Position;
                        nGo.transform.localScale = Vector3.one * vertexSize;
                        Collider nCol = nGo.GetComponent<Collider>();
                        if (nCol != null) nCol.isTrigger = true;
                        ApproachNodeVisual nVis = nGo.AddComponent<ApproachNodeVisual>();
                        nVis.Initialize(node, vp.VertiportId, this);
                        approachNodeVisuals[node.NodeId] = nVis;
                    }

                    foreach (var edge in vp.ApproachNet.Edges)
                    {
                        if (!approachNodeVisuals.TryGetValue(edge.FromNodeId, out ApproachNodeVisual fromN) ||
                            !approachNodeVisuals.TryGetValue(edge.ToNodeId, out ApproachNodeVisual toN))
                            continue;

                        GameObject eGo = new GameObject($"ApprEdge_{edge.EdgeId}");
                        eGo.transform.SetParent(apprContainer);
                        eGo.transform.localPosition = Vector3.zero;
                        ApproachEdgeVisual eVis = eGo.AddComponent<ApproachEdgeVisual>();
                        eVis.Initialize(edge, fromN, toN, this);
                        approachEdgeVisuals[edge.EdgeId] = eVis;
                    }
                }

                // Corridor visuals
                foreach (var corridor in vp.Corridors)
                {
                    GameObject cGo = new GameObject($"Corridor_{corridor.CorridorId}");
                    cGo.transform.SetParent(corrContainer);
                    cGo.transform.localPosition = Vector3.zero;
                    CorridorVisual cVis = cGo.AddComponent<CorridorVisual>();
                    cVis.Initialize(corridor, vp.VertiportId, this);
                    corridorVisuals[corridor.CorridorId] = cVis;
                }
            }

            Debug.Log($"[VertiportManager] Rebuilt visuals: {vertiportVisuals.Count} VP, {helipadVisuals.Count} HP, " +
                      $"{circleVisuals.Count} circles, {vertexVisuals.Count} vertices, {rampVisuals.Count} ramps, " +
                      $"{approachNodeVisuals.Count} apprNodes, {approachEdgeVisuals.Count} apprEdges, " +
                      $"{corridorVisuals.Count} corridors");
        }

        #endregion

        #region Reachable Corridors Recompute

        /// <summary>
        /// Recompute ReachableCorridorIds for all helipads in a given vertiport.
        /// Uses bidirectional BFS on approach network + HS internal graph.
        /// Called automatically after CreateRamp/DeleteRamp/CreateApproachEdge/DeleteApproachEdge.
        /// </summary>
        public void RecomputeReachableCorridors(string vertiportId)
        {
            Vertiport vp = Vertiports.Find(v => v.VertiportId == vertiportId);
            if (vp == null || vp.ApproachNet?.Nodes == null) return;

            // Build bidirectional approach adjacency
            var apprAdj = new Dictionary<string, List<string>>();
            foreach (var n in vp.ApproachNet.Nodes)
                apprAdj[n.NodeId] = new List<string>();
            foreach (var e in vp.ApproachNet.Edges)
            {
                if (apprAdj.ContainsKey(e.FromNodeId))
                    apprAdj[e.FromNodeId].Add(e.ToNodeId);
                if (apprAdj.ContainsKey(e.ToNodeId))
                    apprAdj[e.ToNodeId].Add(e.FromNodeId);
            }

            // Build corridor → ramp mapping
            var rampToCorridors = new Dictionary<string, List<string>>();
            foreach (var cor in vp.Corridors)
            {
                if (!string.IsNullOrEmpty(cor.LinkRampId))
                {
                    if (!rampToCorridors.ContainsKey(cor.LinkRampId))
                        rampToCorridors[cor.LinkRampId] = new List<string>();
                    rampToCorridors[cor.LinkRampId].Add(cor.CorridorId);
                }
            }

            foreach (var hp in vp.Helipads)
            {
                hp.ReachableCorridorIds.Clear();
                Vector3 center = Vector3.zero;
                if (hp.Boundary != null && hp.Boundary.Count > 0)
                {
                    foreach (var p in hp.Boundary) center += p;
                    center /= hp.Boundary.Count;
                }
                else continue;

                // Find closest approach node
                string startNodeId = null;
                float minDist = float.MaxValue;
                foreach (var n in vp.ApproachNet.Nodes)
                {
                    float d = Vector3.Distance(n.Position, center);
                    if (d < minDist) { minDist = d; startNodeId = n.NodeId; }
                }
                if (string.IsNullOrEmpty(startNodeId)) continue;

                // BFS on approach network → find reachable anchor nodes
                var visited = new HashSet<string> { startNodeId };
                var queue = new Queue<string>();
                queue.Enqueue(startNodeId);

                while (queue.Count > 0)
                {
                    string cur = queue.Dequeue();
                    if (apprAdj.TryGetValue(cur, out var neighbors))
                    {
                        foreach (var nb in neighbors)
                        {
                            if (visited.Add(nb))
                                queue.Enqueue(nb);
                        }
                    }
                }

                // For each anchor reachable, traverse its HS
                var anchors = vp.ApproachNet.Nodes
                    .Where(n => n.IsAnchor && !string.IsNullOrEmpty(n.LinkHSId) && visited.Contains(n.NodeId))
                    .ToList();

                foreach (var anchor in anchors)
                {
                    HoldingStructure hs = vp.HoldingStructures
                        .FirstOrDefault(h => h.HoldingStructureId == anchor.LinkHSId);
                    if (hs == null) continue;

                    // Find starting vertex: use LinkVertexId if set, else fall back to legacy anchor
                    string startVertId = anchor.LinkVertexId;
                    if (string.IsNullOrEmpty(startVertId))
                        startVertId = hs.Vertices.FirstOrDefault(v => string.IsNullOrEmpty(v.LinkCircleId))?.VertexId;
                    if (string.IsNullOrEmpty(startVertId)) continue;

                    // BFS on HS graph, bidirectional through ramps, forward through circles
                    var hsVisited = new HashSet<string> { startVertId };
                    var hsQueue = new Queue<string>();
                    hsQueue.Enqueue(startVertId);

                    while (hsQueue.Count > 0)
                    {
                        string curVId = hsQueue.Dequeue();

                        foreach (var ramp in hs.Ramps)
                        {
                            string other = null;
                            if (ramp.FromVertexId == curVId) other = ramp.ToVertexId;
                            else if (ramp.ToVertexId == curVId) other = ramp.FromVertexId;

                            if (other != null)
                            {
                                if (rampToCorridors.TryGetValue(ramp.RampId, out var corrIds))
                                {
                                    foreach (var cid in corrIds)
                                    {
                                        if (!hp.ReachableCorridorIds.Contains(cid))
                                            hp.ReachableCorridorIds.Add(cid);
                                    }
                                }
                                if (hsVisited.Add(other))
                                    hsQueue.Enqueue(other);
                            }
                        }

                        // Circle arcs forward
                        var curV = hs.Vertices.FirstOrDefault(v => v.VertexId == curVId);
                        if (curV != null && !string.IsNullOrEmpty(curV.LinkCircleId))
                        {
                            var circle = hs.Circles.FirstOrDefault(c => c.CircleId == curV.LinkCircleId);
                            if (circle?.CoveredVertices != null)
                            {
                                int idx = circle.CoveredVertices.IndexOf(curVId);
                                if (idx >= 0)
                                {
                                    string nextV = circle.CoveredVertices[(idx + 1) % circle.CoveredVertices.Count];
                                    if (hsVisited.Add(nextV))
                                        hsQueue.Enqueue(nextV);
                                }
                            }
                        }
                    }
                }

                Debug.Log($"[VertiportManager] Helipad {hp.HelipadId}: recomputed {hp.ReachableCorridorIds.Count} reachable corridors: " +
                          $"{string.Join(", ", hp.ReachableCorridorIds)}");
            }
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmos()
        {
            if (!showTextAnnotations) return;

            foreach (var vp in Vertiports)
            {
                if (vp.Boundary.Count > 0)
                {
                    Vector3 center = Vector3.zero;
                    foreach (var p in vp.Boundary) center += p;
                    center /= vp.Boundary.Count;
#if UNITY_EDITOR
                    Handles.Label(center + Vector3.up * 0.5f, vp.VertiportId);
#endif
                }
            }
        }

        #endregion

        #region ID Generation

        // ID naming convention: hierarchical prefixed format
        //   Vertiport:    VP{num}
        //   Helipad:      VP{num}-HP{num}
        //   HS:           VP{num}-HS{num}
        //   Circle:       VP{num}-HS{num}-C{num}
        //   Vertex:       VP{num}-HS{num}-V{num}
        //   Ramp:         VP{num}-HS{num}-R{num}
        //   ApproachNode: VP{num}-APN{num}
        //   ApproachEdge: VP{num}-APE{num}
        //   Corridor:     VP{num}-COR{num}

        private string GenerateVertiportId()
        {
            int max = 0;
            foreach (var vp in Vertiports)
                if (vp.VertiportId.StartsWith("VP") && int.TryParse(vp.VertiportId.Substring(2), out int id))
                    max = Mathf.Max(max, id);
            return $"VP{max + 1}";
        }

        private string GenerateHelipadId(string vertiportId)
        {
            int max = 0;
            string prefix = $"{vertiportId}-HP";
            foreach (var vp in Vertiports)
                foreach (var hp in vp.Helipads)
                    if (hp.HelipadId.StartsWith(prefix) && int.TryParse(hp.HelipadId.Substring(prefix.Length), out int id))
                        max = Mathf.Max(max, id);
            return $"{prefix}{max + 1}";
        }

        private string GenerateHoldingStructureId(string vertiportId)
        {
            int max = 0;
            string prefix = $"{vertiportId}-HS";
            foreach (var vp in Vertiports)
                foreach (var hs in vp.HoldingStructures)
                    if (hs.HoldingStructureId.StartsWith(prefix) && int.TryParse(hs.HoldingStructureId.Substring(prefix.Length), out int id))
                        max = Mathf.Max(max, id);
            return $"{prefix}{max + 1}";
        }

        private string GenerateCircleId(string hsId)
        {
            int max = 0;
            string prefix = $"{hsId}-C";
            foreach (var vp in Vertiports)
                foreach (var hs in vp.HoldingStructures)
                    foreach (var c in hs.Circles)
                        if (c.CircleId.StartsWith(prefix) && int.TryParse(c.CircleId.Substring(prefix.Length), out int id))
                            max = Mathf.Max(max, id);
            return $"{prefix}{max + 1}";
        }

        private string GenerateVertexId(string hsId)
        {
            int max = 0;
            string prefix = $"{hsId}-V";
            foreach (var vp in Vertiports)
                foreach (var hs in vp.HoldingStructures)
                    foreach (var v in hs.Vertices)
                        if (v.VertexId.StartsWith(prefix) && int.TryParse(v.VertexId.Substring(prefix.Length), out int id))
                            max = Mathf.Max(max, id);
            return $"{prefix}{max + 1}";
        }

        private string GenerateRampId(string hsId)
        {
            int max = 0;
            string prefix = $"{hsId}-R";
            foreach (var vp in Vertiports)
                foreach (var hs in vp.HoldingStructures)
                    foreach (var r in hs.Ramps)
                        if (r.RampId.StartsWith(prefix) && int.TryParse(r.RampId.Substring(prefix.Length), out int id))
                            max = Mathf.Max(max, id);
            return $"{prefix}{max + 1}";
        }

        private string GenerateApproachNodeId(string vertiportId)
        {
            int max = 0;
            string prefix = $"{vertiportId}-APN";
            foreach (var vp in Vertiports)
                if (vp.ApproachNet != null)
                    foreach (var n in vp.ApproachNet.Nodes)
                        if (n.NodeId.StartsWith(prefix) && int.TryParse(n.NodeId.Substring(prefix.Length), out int id))
                            max = Mathf.Max(max, id);
            return $"{prefix}{max + 1}";
        }

        private string GenerateApproachEdgeId(string vertiportId)
        {
            int max = 0;
            string prefix = $"{vertiportId}-APE";
            foreach (var vp in Vertiports)
                if (vp.ApproachNet != null)
                    foreach (var e in vp.ApproachNet.Edges)
                        if (e.EdgeId.StartsWith(prefix) && int.TryParse(e.EdgeId.Substring(prefix.Length), out int id))
                            max = Mathf.Max(max, id);
            return $"{prefix}{max + 1}";
        }

        private string GenerateCorridorId(string vertiportId)
        {
            int max = 0;
            string prefix = $"{vertiportId}-COR";
            foreach (var vp in Vertiports)
                foreach (var c in vp.Corridors)
                    if (c.CorridorId.StartsWith(prefix) && int.TryParse(c.CorridorId.Substring(prefix.Length), out int id))
                        max = Mathf.Max(max, id);
            return $"{prefix}{max + 1}";
        }

        #endregion

        #region Public Accessors

        public VertiportVisual GetVertiportVisual(string id)
        {
            vertiportVisuals.TryGetValue(id, out VertiportVisual v);
            return v;
        }

        public HelipadVisual GetHelipadVisual(string id)
        {
            helipadVisuals.TryGetValue(id, out HelipadVisual v);
            return v;
        }

        public CircleVisual GetCircleVisual(string id)
        {
            circleVisuals.TryGetValue(id, out CircleVisual v);
            return v;
        }

        public VertexVisual GetVertexVisual(string id)
        {
            vertexVisuals.TryGetValue(id, out VertexVisual v);
            return v;
        }

        public RampVisual GetRampVisual(string id)
        {
            rampVisuals.TryGetValue(id, out RampVisual v);
            return v;
        }

        public ApproachNodeVisual GetApproachNodeVisual(string id)
        {
            approachNodeVisuals.TryGetValue(id, out ApproachNodeVisual v);
            return v;
        }

        public ApproachEdgeVisual GetApproachEdgeVisual(string id)
        {
            approachEdgeVisuals.TryGetValue(id, out ApproachEdgeVisual v);
            return v;
        }

        public CorridorVisual GetCorridorVisual(string id)
        {
            corridorVisuals.TryGetValue(id, out CorridorVisual v);
            return v;
        }

        public Dictionary<string, VertiportVisual> GetAllVertiportVisuals() => vertiportVisuals;
        public Dictionary<string, HelipadVisual> GetAllHelipadVisuals() => helipadVisuals;
        public Dictionary<string, CircleVisual> GetAllCircleVisuals() => circleVisuals;
        public Dictionary<string, VertexVisual> GetAllVertexVisuals() => vertexVisuals;
        public Dictionary<string, RampVisual> GetAllRampVisuals() => rampVisuals;
        public Dictionary<string, ApproachNodeVisual> GetAllApproachNodeVisuals() => approachNodeVisuals;
        public Dictionary<string, ApproachEdgeVisual> GetAllApproachEdgeVisuals() => approachEdgeVisuals;
        public Dictionary<string, CorridorVisual> GetAllCorridorVisuals() => corridorVisuals;

        public Transform GetVertiportsContainer() => vertiportsContainer;

        #endregion
    }
}
