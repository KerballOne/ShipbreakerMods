using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;

namespace TheExpanse
{
    // Flight Control: the bay's jacks (the Master Jack and the five light jacks) are let go when the ship first moves
    // under its own power, so the hull shoves them out of the way instead of passing through them.
    //
    // Each jack becomes a free body with a weight ([Flight] MasterJackMass, LightJackMass), without gravity. Their
    // meshes are statically batched: baked into one big mesh where they stood when the bay loaded, so they would not
    // follow the body. Each such mesh is drawn by the mod instead, every frame, at the body's place; the jack's own
    // renderer is switched off and left with no materials, which also ends any other mod's redraw of it (BiggerBays
    // draws moved jacks with the renderer's own materials).
    // Where they stood when the ship began to spawn is the baked place, recorded then, so a jack another mod moved
    // afterward is still drawn right. Pairs of jack and ship colliders already touching are kept apart from each other
    // until they have separated, so nothing is flung by the overlap.
    internal static class FlightJacks
    {
        sealed class Piece
        {
            internal Mesh Mesh = null!;
            internal int First, Count, Layer;
            internal Material[] Materials = null!;
            internal UnityEngine.Rendering.ShadowCastingMode Shadows;
            internal bool ReceiveShadows;
        }

        sealed class Jack
        {
            internal Transform Root = null!;
            internal Rigidbody Body = null!;
            internal JackContact Contact = null!;
            internal Vector3 BakedPosition;
            internal Quaternion BakedRotation;
            internal readonly List<Piece> Pieces = new List<Piece>();
            internal readonly List<MeshRenderer> Blanked = new List<MeshRenderer>();
            internal Transform? Rider;                      // the Master Jack's hex lights, kept on it
            internal Vector3 RiderLocalPosition;
            internal Quaternion RiderLocalRotation;
        }

        static readonly Dictionary<int, (Vector3 position, Quaternion rotation)> sBaked = new Dictionary<int, (Vector3, Quaternion)>();
        static readonly List<Jack> sJacks = new List<Jack>();
        static readonly List<(Collider jack, Collider ship)> sIgnored = new List<(Collider, Collider)>();
        static readonly Dictionary<int, Bounds[]> sSubMeshBounds = new Dictionary<int, Bounds[]>();
        static readonly HashSet<long> sTaken = new HashSet<long>();
        static readonly Collider[] sOverlaps = new Collider[512];
        static bool sReleased;
        static float sNextSeparationCheck;

        internal static void Reset()
        {
            sBaked.Clear();
            sJacks.Clear();
            sIgnored.Clear();
            sSubMeshBounds.Clear();
            sTaken.Clear();
            sReleased = false;
        }

        static void FindRoots(List<(Transform root, bool master)> into)
        {
            // the jacks are top-level objects of the bay's scene
            foreach (var t in SceneLookup.Roots())
            {
                if (t == null) continue;
                if (t.name.StartsWith("PRF_MasterJack", System.StringComparison.Ordinal)) into.Add((t, true));
                else if (t.name.StartsWith("PRF_Prop_JaxLight", System.StringComparison.Ordinal)) into.Add((t, false));
            }
        }

        // When a ship begins to spawn: the jacks stand where they were baked (anything that moves them comes later).
        internal static void RecordBaked()
        {
            var roots = new List<(Transform root, bool master)>();
            FindRoots(roots);
            foreach (var (root, _) in roots)
            {
                int id = root.GetInstanceID();
                if (!sBaked.ContainsKey(id)) sBaked[id] = (root.position, root.rotation);
            }
        }

        // The first time the ship moves under its own power in this bay.
        internal static void Release()
        {
            if (sReleased || !Plugin.FlightFreeJacks.Value) return;
            sReleased = true;
            var roots = new List<(Transform root, bool master)>();
            FindRoots(roots);
            foreach (var (root, master) in roots) Take(root, master);
        }

        static void Take(Transform root, bool master)
        {
            var body = root.GetComponent<Rigidbody>();
            bool added = body == null;
            if (body == null) body = root.gameObject.AddComponent<Rigidbody>();
            if (!sBaked.TryGetValue(root.GetInstanceID(), out var baked))
            {
                baked = (root.position, root.rotation);
                Plugin.Log.LogWarning($"[Flight] '{root.name}': where it was baked was not recorded at spawn; taking where it is now.");
            }
            var jack = new Jack { Root = root, Body = body, BakedPosition = baked.position, BakedRotation = baked.rotation };
            var moved = root.position - baked.position;

            // the batched meshes: drawn here from now on, their renderers removed
            int batched = 0, matched = 0;
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.isPartOfStaticBatch) continue;
                batched++;
                var piece = Match(renderer, moved);
                if (piece == null) continue;
                matched++;
                jack.Pieces.Add(piece);
                Blank(renderer);
                jack.Blanked.Add(renderer);
            }

            // a free body needs convex mesh colliders
            int convexed = 0;
            foreach (var collider in root.GetComponentsInChildren<MeshCollider>(true))
                if (!collider.convex) { collider.convex = true; convexed++; }

            if (master)
            {
                var lights = GameObject.Find("PRF_BayAnimatedEmmissiveLighting/HexEmissiveLights_MasterJack");
                if (lights != null)
                {
                    jack.Rider = lights.transform;
                    jack.RiderLocalPosition = Quaternion.Inverse(root.rotation) * (lights.transform.position - root.position);
                    jack.RiderLocalRotation = Quaternion.Inverse(root.rotation) * lights.transform.rotation;
                }
            }

            int ignored = KeepApart(root);
            float tonnes = Mathf.Max(0.1f, master ? Plugin.FlightMasterJackMass.Value : Plugin.FlightLightJackMass.Value);
            body.isKinematic = false;
            body.useGravity = false;
            body.mass = tonnes * 1000f;
            body.drag = 0f;
            body.angularDrag = 0.05f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.detectCollisions = true;
            body.velocity = body.angularVelocity = Vector3.zero;
            jack.Contact = root.gameObject.GetComponent<JackContact>() ?? root.gameObject.AddComponent<JackContact>();
            sJacks.Add(jack);
            Plugin.Log.LogInfo($"[Flight] jack '{root.name}' at {root.position} let go at {tonnes:0} t" + (added ? " (body added)" : "") +
                               $"; {matched}/{batched} batched mesh(es) drawn by the mod (baked {moved.magnitude:0.0} m from here); " +
                               $"{convexed} mesh collider(s) made convex; {ignored} touching ship collider(s) kept apart" +
                               (jack.Rider != null ? "; hex lights ride on it." : "."));
        }

        static readonly Material[] sNoMaterials = new Material[0];

        static void Blank(MeshRenderer renderer)
        {
            renderer.forceRenderingOff = true;
            renderer.enabled = false;
            renderer.sharedMaterials = sNoMaterials;
        }

        // The run of submeshes in the combined mesh that is this renderer, matched by its bounds (where it was baked:
        // the renderer's bounds as they are, or moved back by however far the jack has been moved since).
        static Piece? Match(MeshRenderer renderer, Vector3 moved)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            var materials = renderer.sharedMaterials;
            if (mesh == null || materials.Length == 0) return null;
            int meshId = mesh.GetInstanceID();
            if (!sSubMeshBounds.TryGetValue(meshId, out var subs))
            {
                subs = new Bounds[mesh.subMeshCount];
                for (int i = 0; i < subs.Length; i++) subs[i] = mesh.GetSubMesh(i).bounds;
                sSubMeshBounds[meshId] = subs;
            }
            int count = materials.Length, best = -1;
            float bestError = float.MaxValue;
            foreach (var shift in new[] { Vector3.zero, -moved })
            {
                var want = renderer.bounds;
                want.center += shift;
                for (int s = 0; s + count <= subs.Length; s++)
                {
                    var b = subs[s];
                    for (int k = 1; k < count; k++) b.Encapsulate(subs[s + k]);
                    float error = (b.min - want.min).magnitude + (b.max - want.max).magnitude;
                    if (error > 0.1f) continue;
                    if (sTaken.Contains(((long)meshId << 32) | (uint)s)) error += 0.05f;
                    if (error < bestError) { bestError = error; best = s; }
                }
                if (best >= 0) break;
            }
            if (best < 0)
            {
                Plugin.Log.LogWarning($"[Flight] '{renderer.name}': its batched mesh was not found; it is left drawn where it was baked.");
                return null;
            }
            sTaken.Add(((long)meshId << 32) | (uint)best);
            return new Piece
            {
                Mesh = mesh, First = best, Count = count, Layer = renderer.gameObject.layer, Materials = materials,
                Shadows = renderer.shadowCastingMode, ReceiveShadows = renderer.receiveShadows,
            };
        }

        // Ship colliders already touching the jack's are kept from colliding with them until they have separated.
        static int KeepApart(Transform root)
        {
            int count = 0;
            foreach (var mine in root.GetComponentsInChildren<Collider>(true))
            {
                if (mine == null || mine.isTrigger || !mine.enabled) continue;
                var b = mine.bounds;
                int hits = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * 0.25f, sOverlaps, Quaternion.identity,
                                                      Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits; i++)
                {
                    var other = sOverlaps[i];
                    if (other == null || other.transform.IsChildOf(root)) continue;
                    var part = other.GetComponentInParent<StructurePart>();
                    if (!FlightModel.IsShipPart(part)) continue;
                    Physics.IgnoreCollision(mine, other, true);
                    sIgnored.Add((mine, other));
                    count++;
                }
            }
            return count;
        }

        // Every frame: the jacks' meshes where their bodies are; the hex lights with the Master Jack; pairs kept apart
        // collide again once they are clear of each other.
        // A jack the hull keeps pushing along stays pressed against it (nothing in the physics makes it bounce off), and
        // working out that contact against the hull's thousands of colliders every step cost most of the frame rate.
        // After PushSeconds against the hull, it is thrown clear: out sideways from the ship's long axis, tumbling.
        const float PushSeconds = 0.3f;

        static void ThrowClear(Jack jack)
        {
            if (jack.Contact.TouchingSince < 0f || Time.time - jack.Contact.TouchingSince < PushSeconds) return;
            if (!FlightModel.TryAxis(out var axisPoint, out var axisDirection)) return;
            var body = jack.Body;
            var offset = body.worldCenterOfMass - axisPoint;
            var sideways = offset - axisDirection * Vector3.Dot(offset, axisDirection);
            if (sideways.sqrMagnitude < 0.01f) sideways = Vector3.Cross(axisDirection, Vector3.up);
            sideways.Normalize();
            var ship = FlightModel.PointVelocity(body.worldCenterOfMass);
            float kick = Mathf.Max(4f, ship.magnitude * 0.5f);
            body.velocity = ship + sideways * kick;
            body.angularVelocity = Random.onUnitSphere * 0.5f;
            jack.Contact.TouchingSince = -1f;
            Plugin.Log.LogInfo($"[Flight] jack '{jack.Root.name}' pressed against the hull: thrown clear at {kick:0.0} m/s sideways.");
        }

        internal static void Frame()
        {
            if (sJacks.Count == 0) return;
            foreach (var jack in sJacks)
                if (jack.Body != null && jack.Contact != null) ThrowClear(jack);
            for (int j = sJacks.Count - 1; j >= 0; j--)
            {
                var jack = sJacks[j];
                if (jack.Root == null || jack.Body == null) { sJacks.RemoveAt(j); continue; }
                var t = jack.Root;
                var matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one) *
                             Matrix4x4.TRS(jack.BakedPosition, jack.BakedRotation, Vector3.one).inverse;
                foreach (var piece in jack.Pieces)
                    for (int k = 0; k < piece.Count && k < piece.Materials.Length; k++)
                        if (piece.Materials[k] != null)
                            Graphics.DrawMesh(piece.Mesh, matrix, piece.Materials[k], piece.Layer, null, piece.First + k, null,
                                              piece.Shadows, piece.ReceiveShadows);
                // the scanner's highlight puts materials back on for a moment: blank again
                foreach (var renderer in jack.Blanked)
                    if (renderer != null && renderer.sharedMaterials.Length > 0) Blank(renderer);
                if (jack.Rider != null)
                    jack.Rider.SetPositionAndRotation(t.position + t.rotation * jack.RiderLocalPosition, t.rotation * jack.RiderLocalRotation);
            }

            if (sIgnored.Count == 0 || Time.time < sNextSeparationCheck) return;
            sNextSeparationCheck = Time.time + 0.5f;
            for (int i = sIgnored.Count - 1; i >= 0; i--)
            {
                var (mine, other) = sIgnored[i];
                if (mine == null || other == null) { sIgnored.RemoveAt(i); continue; }
                var a = mine.bounds; var b = other.bounds;
                a.Expand(0.5f);
                if (a.Intersects(b)) continue;
                Physics.IgnoreCollision(mine, other, false);
                sIgnored.RemoveAt(i);
            }
        }
    }

    // Notes when a let-go jack is touching the flown ship.
    internal class JackContact : MonoBehaviour
    {
        internal float TouchingSince = -1f;
        int sLastFrame;

        void OnCollisionStay(Collision collision)
        {
            if (!FlightModel.UnderWay || collision.collider == null) return;
            if (!FlightModel.IsShipPart(collision.collider.GetComponentInParent<StructurePart>())) return;
            if (TouchingSince < 0f) TouchingSince = Time.time;
            sLastFrame = Time.frameCount;
        }

        void Update()
        {
            if (TouchingSince >= 0f && Time.frameCount - sLastFrame > 3) TouchingSince = -1f;      // no longer touching
        }
    }
}
