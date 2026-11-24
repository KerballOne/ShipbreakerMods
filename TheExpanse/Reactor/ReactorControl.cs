using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using BepInEx.Configuration;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace TheExpanse
{
    // Reactor control on the MCRN Corvette: a ball of fusion plasma held in a magnetic bottle in the reactor chamber by
    // eight plasma injectors in a ring around it, each pushing on it from its side.
    //
    // With all eight connected the pushes cancel: the ball sits in the middle with a slow, small pulse. Disconnecting
    // one leaves a gap; the pushes no longer cancel and the ball drifts toward the gap and pulses faster and larger.
    // The imbalance is the sum of the directions (ball to nozzle, across the ring) of the disconnected injectors, in
    // injectors' worth: one gap is 1, two opposite gaps cancel to 0, two side by side are 1.85, a slightly off opposite
    // pair (135 degrees apart) is 0.77. Above UnstableAt (1.05) the plasma is unstable: a fire cloud wraps it and it
    // burns what it touches. So the safe way is the car-wheel way: opposite sides, then the pair in between. Badly
    // unbalanced plasma (MeltdownAt for MeltdownSeconds) melts down, the game's own meltdown. With all eight
    // disconnected the plasma fades out; it also fades if the reactor core is cut loose or destroyed.
    //
    // An injector is disconnected once its fuel feed pipes are no longer jointed to it (its handle releases them), or,
    // for an injector without feeds, once nothing at all is jointed to it. Nothing is saved: on a reload the state is
    // rebuilt from which injectors are still connected.
    internal static class ReactorControl
    {
        enum Phase { Waiting, Running, Meltdown, Fading, Gone }

        const int Count = 8;
        const float LogicSeconds = 0.25f;
        const float SetupDelay = 3f;            // after the ship spawns, for the structure graph to settle
        const float SetupGiveUp = 30f;
        const float FadeSeconds = 5f;
        const float MeltdownGiveUp = 60f;
        static float sBodyReach = 4f;           // m across the ring from the ball to the injectors' bodies: the most it bends
        static float sBodyHeight;               // m the injectors' bodies sit above the ball (against "down")
        const string FireFxGuid = "71965b2f0487c9947903596d99098ae9";       // the game's FX_Fire_01

        sealed class Injector
        {
            internal int Index;
            internal readonly List<StructurePart> Parts = new List<StructurePart>();
            internal Vector3 LocalSlot;         // where it sat at setup, in the core's space
            internal Vector3 LocalNozzle;       // its nozzle's end (its drawn surface nearest the ball), core space
            internal Vector3 LocalDirection;    // unit, ball toward nozzle across the ring, core space
            internal Vector3 LocalHandle;       // its control handle (the console jointed to it), core space
            internal bool HasHandle;
            internal bool Connected = true;
            internal bool UsesFeeds;            // a fuel line found at setup: whether it still reaches its anchors decides it
            internal readonly HashSet<Entity> Anchors = new HashSet<Entity>();   // where its fuel line joined the ship at setup
            internal readonly HashSet<Entity> Released = new HashSet<Entity>();  // what its handle lets go of, from setup
            internal int LastAttached = -1;     // joints at the last check (-1 before the first)
            internal float LastHeated = -100f;
            internal GameObject? FireFx;
        }

        static readonly Injector[] sInjectors = new Injector[Count];
        static readonly bool[] sSimulated = new bool[Count];       // test tools: treated as disconnected
        static Phase sPhase = Phase.Waiting;
        static float sSpawnedAt = -1f, sLogicDue, sPhaseStarted, sNextBurn, sBadSince = -1f;
        static StructurePart? sCore;
        static Entity sCoreEntity;
        static Vector3 sLocalCenter, sLocalDown;
        static Vector3 sLocalCoreTip;           // the end of the big nozzle above the ball, core space
        static Vector3 sWorldCenter, sWorldDown;    // last known, kept when the core is gone
        static Quaternion sLastCoreRotation = Quaternion.identity;
        static float sShownImbalance;           // eased, for the pulse and size
        static readonly ReactorFeeds.Feed[] sFeeds = new ReactorFeeds.Feed[Count];
        static float sFade = 1f;
        static GameObject? sFirePrefab;
        static bool sFireLoading, sSetupFailedLogged;

        static bool Allowed => Plugin.Enabled.Value && Plugin.ReactorControl.Value && PartSeparation.CustomShip;

        internal static void Reset()
        {
            foreach (var injector in sInjectors) if (injector?.FireFx != null) Object.Destroy(injector.FireFx);
            for (int i = 0; i < Count; i++) { sInjectors[i] = null!; sSimulated[i] = false; }
            ReactorVisuals.Destroy();
            ReactorFire.Reset();
            sPhase = Phase.Waiting;
            sSpawnedAt = -1f;
            sCore = null; sCoreEntity = Entity.Null;

            sShownImbalance = 0f;
            sShownFeed = 1f;
            sPulseAt = -1f;
            sBadSince = -1f;
            sTracked = false; sJumpFrames = 0; sJumpsHeld = 0;
            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (sBlast != Entity.Null && world != null && world.IsCreated && world.EntityManager.Exists(sBlast)) world.EntityManager.DestroyEntity(sBlast);
            }
            catch { }
            sBlast = Entity.Null;
            sFade = 1f;
            sSetupFailedLogged = false;
        }

        internal static void OnShipSpawned() { Reset(); sSpawnedAt = Time.time; }

        static bool Connected(int i) => sInjectors[i].Connected && !sSimulated[i];

        // ---- every frame --------------------------------------------------------------------------------------------
        internal static void Frame()
        {
            if (sPhase == Phase.Waiting)
            {
                if (sSpawnedAt < 0f || !Allowed || Time.time - sSpawnedAt < SetupDelay) return;
                if (!TrySetUp())
                {
                    if (Time.time - sSpawnedAt > SetupGiveUp) { sPhase = Phase.Gone; sSpawnedAt = -1f; }
                    return;
                }
            }
            if (sPhase == Phase.Gone) return;
            if (!Allowed) { ReactorVisuals.Destroy(); return; }
            if (!ReactorVisuals.Built) ReactorVisuals.Build();

            if (Time.time >= sLogicDue)
            {
                sLogicDue = Time.time + LogicSeconds;
                Logic(LogicSeconds);
            }
            Draw();
        }

        // ---- finding the reactor ------------------------------------------------------------------------------------
        static bool TrySetUp()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;
            var em = world.EntityManager;

            // The core: the ship's reactor (the game's ReactorComponent) with a StructurePart.
            StructurePart? core = null;
            using (var query = em.CreateEntityQuery(ComponentType.ReadOnly<ReactorComponent>()))
            using (var entities = query.ToEntityArray(Allocator.TempJob))
                foreach (var e in entities)
                    if (em.HasComponent<ShipMemberComponent>(e) || core == null)
                        if (em.TryGetComponentObject<StructurePart>(e, out var part) && part != null) { core = part; sCoreEntity = e; }
            if (core == null) return Fail("no reactor core on this ship");

            // The injectors: the game's emergency-control thrusters close to the core.
            Vector3 corePosition = core.transform.position;
            Vector3 axis = core.transform.up;
            var groups = new Dictionary<int, List<StructurePart>>();
            foreach (var part in Object.FindObjectsOfType<StructurePart>())
            {
                if (part == null || part == core) continue;
                if ((part.transform.position - corePosition).sqrMagnitude > 12f * 12f) continue;
                if (!IsInjector(part, out int key)) continue;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<StructurePart>();
                list.Add(part);
            }
            string how = "by name";
            if (groups.Count != Count)
            {
                // The game reparents parts, so names above the part may be gone: find them by where they sit instead.
                var byShape = InjectorsByShape(core, corePosition, axis);
                if (byShape == null)
                    return Fail($"{groups.Count} injector(s) found near the core by name, not {Count}, and none by shape: " + Describe(core, corePosition, axis));
                groups = byShape;
                how = "by shape (thruster parts on the ring)";
            }

            // Ring middle, and "down": along the core's axis, toward the side the ring sits on.
            Vector3 ring = Vector3.zero;
            foreach (var list in groups.Values) ring += Middle(list);
            ring /= Count;
            Vector3 down = Vector3.Dot(ring - corePosition, axis) <= 0f ? -axis : axis;

            // The ball sits where the game's own reactor fire effect was placed, if it is there to be found, else
            // a little below the ring, where the injector nozzles point.
            Vector3 center = ring + down * Mathf.Max(0f, Plugin.ReactorBallDepth.Value);
            var marker = FindMarker("FX_Fire_01", corePosition, 15f);
            string where = $"{Plugin.ReactorBallDepth.Value:0.#} m below the ring";
            if (marker != null)
            {
                float along = Vector3.Dot(marker.position - ring, down);
                center = ring + down * along;
                where = $"level with the ship's reactor fire marker, {along:0.#} m below the ring";
            }

            // Number the injectors by angle around the axis.
            Vector3 reference = Vector3.ProjectOnPlane(Middle(groups.Values.First()) - ring, down).normalized;
            var ordered = groups.Values.Select(list => (list, angle: Vector3.SignedAngle(reference, Vector3.ProjectOnPlane(Middle(list) - ring, down), down)))
                                      .Select(x => (x.list, angle: x.angle < 0f ? x.angle + 360f : x.angle))
                                      .OrderBy(x => x.angle).ToList();
            var angles = new List<string>();
            for (int i = 0; i < Count; i++)
            {
                var injector = new Injector { Index = i };
                injector.Parts.AddRange(ordered[i].list);
                injector.LocalSlot = core.transform.InverseTransformPoint(Middle(ordered[i].list));
                var nozzle = NozzleTip(ordered[i].list, center);
                injector.LocalNozzle = core.transform.InverseTransformPoint(nozzle);
                // which way it pushes from: ball toward its nozzle, across the ring
                var across = Vector3.ProjectOnPlane(nozzle - center, down);
                injector.LocalDirection = core.transform.InverseTransformDirection(across.sqrMagnitude > 1e-6f ? across.normalized : Vector3.zero);
                sInjectors[i] = injector;
                angles.Add($"{i}:{ordered[i].angle:0}");
            }

            sCore = core;
            sLocalCenter = core.transform.InverseTransformPoint(center);
            sLocalCoreTip = core.transform.InverseTransformPoint(NearestDrawn(new List<StructurePart> { core }, center));
            Plugin.Log.LogInfo($"[Reactor] the big nozzle above the ball ends {(core.transform.TransformPoint(sLocalCoreTip) - center).magnitude:0.0} m from it.");
            sLocalDown = core.transform.InverseTransformDirection(down);
            sWorldCenter = center; sWorldDown = down;
            sLastCoreRotation = core.transform.rotation;
            sTracked = true;
            sBodyReach = sInjectors.Average(x => Vector3.ProjectOnPlane(core.transform.TransformPoint(x.LocalSlot) - center, down).magnitude);
            sBodyHeight = sInjectors.Average(x => -Vector3.Dot(core.transform.TransformPoint(x.LocalSlot) - center, down));

            Plugin.Log.LogInfo($"[Reactor] reactor found: core '{core.name}', {Count} injectors {how} at angles {string.Join(" ", angles)} " +
                               $"({string.Join(", ", sInjectors.Select(x => x.Parts.Count))} part(s) each); plasma ball {where}; injector bodies {sBodyReach:0.0} m across and {sBodyHeight:0.0} m up from it.");

            foreach (var injector in sInjectors)
                Plugin.Log.LogInfo($"[Reactor] injector {injector.Index} nozzle end {(core.transform.TransformPoint(injector.LocalNozzle) - center).magnitude:0.0} m from the ball; parts:" + string.Concat(injector.Parts.Select(p =>
                    $" {p.name} [{(p.StructurePartAsset != null ? p.StructurePartAsset.name : "?")}]")));

            // Rebuild from what is already disconnected (a reloaded save).
            UpdateConnections(em);
            foreach (var injector in sInjectors)
            {
                if (!injector.HasHandle)
                {
                    var top = injector.Parts.Where(x => x != null).OrderBy(x => Vector3.Dot(x.transform.position - center, down)).FirstOrDefault();
                    injector.LocalHandle = top != null ? core.transform.InverseTransformPoint(top.transform.position) : injector.LocalSlot;
                }
                Plugin.Log.LogInfo($"[Reactor] injector {injector.Index} handle {(injector.HasHandle ? "console" : "(none found: its top part)")} " +
                                   $"{(core.transform.TransformPoint(injector.LocalHandle) - center).magnitude:0.0} m from the ball.");
            }
            sPhase = Phase.Running;
            int left = sInjectors.Count(x => x.Connected);
            if (left == 0) BeginFade("all injectors were already disconnected");
            else if (left < Count) Plugin.Log.LogInfo($"[Reactor] {Count - left} injector(s) already disconnected: {BalanceText()}.");
            if (!CoreAttached(em)) Plugin.Log.LogWarning("[Reactor] the core does not seem to be part of the ship; check the connection test.");
            return true;
        }

        static bool Fail(string why)
        {
            if (!sSetupFailedLogged || Time.time - sSpawnedAt > SetupGiveUp)
            {
                sSetupFailedLogged = true;
                Plugin.Log.LogInfo("[Reactor] no reactor control on this ship: " + why);
            }
            return false;
        }

        // An injector part, and which injector it belongs to (the stock thruster copy it came from).
        static bool IsInjector(StructurePart part, out int key)
        {
            key = 0;
            for (var t = part.transform; t != null; t = t.parent)
            {
                if (t.name.IndexOf("EmergencyControl", System.StringComparison.OrdinalIgnoreCase) >= 0) { key = t.GetInstanceID(); }
                else if (t.name == "Injector" && t.parent != null && t.parent.name.StartsWith("Injector Assembly")) { key = t.GetInstanceID(); return true; }
            }
            if (key != 0) return true;
            var asset = part.StructurePartAsset;
            if (asset != null && asset.name.IndexOf("EmergencyControl", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                key = part.GetInstanceID();
                return true;
            }
            return false;
        }

        // Where a point sits around the core: distance from the axis, height along it, angle around it (0..360).
        static (float radius, float height, float angle) AroundCore(StructurePart core, Vector3 axis, Vector3 point)
        {
            var offset = point - core.transform.position;
            float height = Vector3.Dot(offset, axis);
            var flat = offset - axis * height;
            float angle = Vector3.SignedAngle(Vector3.ProjectOnPlane(core.transform.forward, axis), flat, axis);
            return (flat.magnitude, height, angle < 0f ? angle + 360f : angle);
        }

        static bool IsThrusterPart(StructurePart part)
        {
            var asset = part.StructurePartAsset;
            return part.name.IndexOf("Thruster", System.StringComparison.OrdinalIgnoreCase) >= 0
                || (asset != null && asset.name.IndexOf("Thruster", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Thruster parts 1.5 to 8 m off the core's axis, all on one side of it, that fall into exactly 8 clusters by
        // angle (gaps of at least 15 degrees between clusters). Null if that is not what is there.
        static Dictionary<int, List<StructurePart>>? InjectorsByShape(StructurePart core, Vector3 corePosition, Vector3 axis)
        {
            var found = new List<(StructurePart part, float angle, float height)>();
            foreach (var part in Object.FindObjectsOfType<StructurePart>())
            {
                if (part == null || part == core || !IsThrusterPart(part)) continue;
                if ((part.transform.position - corePosition).sqrMagnitude > 12f * 12f) continue;
                var (radius, height, angle) = AroundCore(core, axis, part.transform.position);
                if (radius < 1.5f || radius > 8f) continue;
                found.Add((part, angle, height));
            }
            if (found.Count < Count) return null;
            // keep the side of the core (along the axis) that has more of them
            int below = found.Count(f => f.height < 0f);
            found = found.Where(f => below * 2 >= found.Count ? f.height < 0f : f.height >= 0f).ToList();
            found.Sort((a, b) => a.angle.CompareTo(b.angle));

            // split at the gaps; join the last cluster to the first if they meet across 0/360
            var clusters = new List<List<(StructurePart part, float angle, float height)>>();
            for (int i = 0; i < found.Count; i++)
            {
                if (i == 0 || found[i].angle - found[i - 1].angle >= 15f) clusters.Add(new List<(StructurePart, float, float)>());
                clusters[clusters.Count - 1].Add(found[i]);
            }
            if (clusters.Count > 1 && found[0].angle + 360f - found[found.Count - 1].angle < 15f)
            {
                clusters[0].AddRange(clusters[clusters.Count - 1]);
                clusters.RemoveAt(clusters.Count - 1);
            }
            Plugin.Log.LogInfo($"[Reactor] by shape: {found.Count} thruster part(s) on the ring in {clusters.Count} cluster(s) at " +
                               string.Join(" ", clusters.Select(c => $"{c.Average(x => x.angle):0}({c.Count})")));
            if (clusters.Count != Count) return null;
            var groups = new Dictionary<int, List<StructurePart>>();
            for (int i = 0; i < clusters.Count; i++) groups[i] = clusters[i].Select(x => x.part).ToList();
            return groups;
        }

        static string Describe(StructurePart core, Vector3 corePosition, Vector3 axis)
        {
            var near = Object.FindObjectsOfType<StructurePart>()
                .Where(p => p != null && p != core && (p.transform.position - corePosition).sqrMagnitude < 12f * 12f)
                .Select(p => (p, where: AroundCore(core, axis, p.transform.position)))
                .OrderBy(x => x.where.radius)
                .Take(60)
                .Select(x => $"{x.p.name} [{(x.p.StructurePartAsset != null ? x.p.StructurePartAsset.name : "?")}] r {x.where.radius:0.0} h {x.where.height:0.0} a {x.where.angle:0}");
            return $"core '{core.name}' axis {axis}; parts near the core: " + string.Join("; ", near);
        }

        static Transform? FindMarker(string name, Vector3 near, float within)
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t != null && t.name == name && t.gameObject.scene.IsValid() && (t.position - near).sqrMagnitude < within * within)
                    return t;
            return null;
        }

        static Vector3 Middle(List<StructurePart> parts)
        {
            Vector3 sum = Vector3.zero; int n = 0;
            foreach (var part in parts) if (part != null) { sum += part.transform.position; n++; }
            return n > 0 ? sum / n : Vector3.zero;
        }

        // The end of the injector's nozzle: its nozzle part's mesh, in the mesh's own space (so a turned injector does
        // not widen it), the middle of the face of its box that looks most toward the ball. Without a nozzle part: the
        // point of everything its parts draw nearest the ball.
        //
        // The Tachi's injectors carry a Mistral thruster nozzle (PRF_Plug_MistralThruster_Nozzle, baked) over the stock
        // one: its mouth (the _End part) within NozzleSearch m of the injector is used first, then its main part, then
        // the injector's own nozzle part.
        const float NozzleSearch = 4f;

        static Vector3 NozzleTip(List<StructurePart> parts, Vector3 ball)
        {
            var middle = Middle(parts);
            foreach (var key in new[] { "MistralThruster_Nozzle_4Cx4Cx4B_End", "MistralThruster_Nozzle" })
            {
                StructurePart? best = null; float bestDistance = NozzleSearch * NozzleSearch;
                foreach (var part in Object.FindObjectsOfType<StructurePart>())
                {
                    if (part == null || part.name.IndexOf(key, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    float d = (part.transform.position - middle).sqrMagnitude;
                    if (d < bestDistance) { bestDistance = d; best = part; }
                }
                if (best != null && TryFaceToward(best, ball, out var face)) return face;
            }
            foreach (var part in parts)
                if (part != null && part.name.IndexOf("Nozzle", System.StringComparison.OrdinalIgnoreCase) >= 0 && TryFaceToward(part, ball, out var face))
                    return face;
            return NearestDrawn(parts, ball);
        }

        // The middle of the face of the part's mesh box (in the mesh's own space) that looks most toward the ball.
        static bool TryFaceToward(StructurePart part, Vector3 ball, out Vector3 point)
        {
            point = Vector3.zero;
            foreach (var filter in part.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                var b = filter.sharedMesh.bounds;
                var t = filter.transform;
                var toBall = t.InverseTransformDirection(ball - t.TransformPoint(b.center)).normalized;
                var face = Vector3.zero; float best = float.MinValue;
                for (int axis = 0; axis < 3; axis++)
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        var n = Vector3.zero; n[axis] = sign;
                        float score = Vector3.Dot(n, toBall);
                        if (score > best) { best = score; face = n; }
                    }
                point = t.TransformPoint(b.center + Vector3.Scale(face, b.extents));
                return true;
            }
            return false;
        }

        static Vector3 NearestDrawn(List<StructurePart> parts, Vector3 ball)
        {
            Vector3 best = Middle(parts);
            float bestDistance = float.MaxValue;
            foreach (var part in parts)
            {
                if (part == null) continue;
                foreach (var renderer in part.GetComponentsInChildren<Renderer>())
                {
                    var p = renderer.bounds.ClosestPoint(ball);
                    float d = (p - ball).sqrMagnitude;
                    if (d < bestDistance) { bestDistance = d; best = p; }
                }
            }
            return best;
        }

        // ---- connections --------------------------------------------------------------------------------------------
        static bool TryGraphKey(EntityManager em, Entity e, out int key)
        {
            key = 0;
            if (!em.Exists(e) || em.HasComponent<PendingDestroySalvageable>(e) || em.HasComponent<PendingSalvageSalvageable>(e)
                || em.HasComponent<PendingDestruction>(e)) return false;
            if (em.HasComponent<StructureGraphNodeComponent>(e)) { key = em.GetComponentData<StructureGraphNodeComponent>(e).GraphData.GraphKey; return true; }
            if (em.HasComponent<StructureGroupMember>(e))
            {
                var group = em.GetComponentData<StructureGroupMember>(e).GroupEntity;
                if (em.Exists(group) && em.HasComponent<StructureGraphNodeComponent>(group))
                { key = em.GetComponentData<StructureGraphNodeComponent>(group).GraphData.GraphKey; return true; }
            }
            return false;
        }

        static Entity EntityOf(StructurePart? part)
        {
            if (part == null) return Entity.Null;
            var blueprint = part.EntityBlueprintComponent;
            return blueprint != null ? blueprint.Entity : Entity.Null;
        }

        // The ship's main piece: the structure-graph key most of the ship's parts share.
        static bool TryMainKey(EntityManager em, out int main)
        {
            main = 0;
            var counts = new Dictionary<int, int>();
            using (var query = em.CreateEntityQuery(ComponentType.ReadOnly<StructureGraphNodeComponent>(), ComponentType.ReadOnly<ShipMemberComponent>()))
            using (var nodes = query.ToComponentDataArray<StructureGraphNodeComponent>(Allocator.TempJob))
                foreach (var node in nodes)
                {
                    int k = node.GraphData.GraphKey;
                    counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
                }
            if (counts.Count == 0) return false;
            main = counts.OrderByDescending(x => x.Value).First().Key;
            return true;
        }

        static bool CoreAttached(EntityManager em)
        {
            if (!TryGraphKey(em, sCoreEntity, out int core)) return false;
            return !TryMainKey(em, out int main) || main == core;
        }

        // The structure graph's nodes that stand for a part: the part itself, or (for a part inside a group) the
        // group and its connection points.
        static void NodesOf(EntityManager em, StructurePart? part, HashSet<Entity> into)
        {
            var e = EntityOf(part);
            if (e == Entity.Null || !em.Exists(e)) return;
            into.Add(e);
            if (em.HasComponent<StructureGroupMember>(e))
            {
                var group = em.GetComponentData<StructureGroupMember>(e).GroupEntity;
                if (em.Exists(group))
                {
                    into.Add(group);
                    if (em.HasComponent<PartConnection>(group))
                        foreach (var connection in em.GetBuffer<PartConnection>(group)) into.Add(connection.Value);
                }
            }
        }

        static bool IsFuelPipe(EntityManager em, Entity e)
        {
            if (!em.Exists(e) || !em.TryGetComponentObject<StructurePart>(e, out var part) || part == null) return false;
            var asset = part.StructurePartAsset;
            return asset != null && asset.name.IndexOf("FuelPipe", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string NameOf(EntityManager em, Entity e)
        {
            if (em.Exists(e) && em.TryGetComponentObject<StructurePart>(e, out var part) && part != null)
                return $"{part.name} [{(part.StructurePartAsset != null ? part.StructurePartAsset.name : "?")}]";
            return $"entity {e.Index}";
        }

        // Returns the injectors that were connected and no longer are, in index order.
        //
        // Each injector is fed by a line: the purple pipe running from it up to the reactor's lid, jointed pipe to pipe
        // (anything named or typed as a pipe or a cut point). At setup the line is traced from the injector through the
        // pipes to where it joins the rest of the ship (its anchors: the first non-pipe parts, other injectors left
        // out); the anchors within LidReach of the reactor core are kept (the lid), or all of them if none are that
        // close. From then on the injector is connected while its line still reaches one of those anchors: pulling its
        // handle, cutting the line anywhere along it (frozen first or not) or losing a pipe all disconnect it. An
        // injector with no line at setup counts as connected while anything at all is jointed to it.
        const int LineMaxPipes = 200;
        const float LidReach = 8f;

        static bool IsPipeLike(EntityManager em, Entity e)
        {
            if (!em.Exists(e) || !em.TryGetComponentObject<StructurePart>(e, out var part) || part == null) return false;
            var asset = part.StructurePartAsset;
            string a = asset != null ? asset.name : "";
            return part.name.IndexOf("Pipe", System.StringComparison.OrdinalIgnoreCase) >= 0
                || a.IndexOf("Pipe", System.StringComparison.OrdinalIgnoreCase) >= 0
                || a.IndexOf("CutPoint", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsHandleOrCutPoint(EntityManager em, Entity e)
        {
            if (!em.Exists(e) || !em.TryGetComponentObject<StructurePart>(e, out var part) || part == null) return false;
            var asset = part.StructurePartAsset;
            return part.name.IndexOf("AirlockConsole", System.StringComparison.OrdinalIgnoreCase) >= 0
                || (asset != null && asset.name.IndexOf("CutPoint", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static Vector3? PositionOf(EntityManager em, Entity e) =>
            em.Exists(e) && em.TryGetComponentObject<StructurePart>(e, out var part) && part != null ? part.transform.position : (Vector3?)null;

        static List<int> UpdateConnections(EntityManager em)
        {
            var lost = new List<int>();
            NativeArray<StructureGraphEdgeComponent> edges;
            using (var query = em.CreateEntityQuery(ComponentType.ReadOnly<StructureGraphEdgeComponent>()))
                edges = query.ToComponentDataArray<StructureGraphEdgeComponent>(Allocator.TempJob);
            try
            {
                // who is jointed to whom
                var links = new Dictionary<Entity, List<Entity>>();
                foreach (var edge in edges)
                {
                    var data = edge.GraphData;
                    if (!links.TryGetValue(data.Node1, out var l1)) links[data.Node1] = l1 = new List<Entity>();
                    if (!links.TryGetValue(data.Node2, out var l2)) links[data.Node2] = l2 = new List<Entity>();
                    l1.Add(data.Node2); l2.Add(data.Node1);
                }
                var pipeCache = new Dictionary<Entity, bool>();
                bool Pipe(Entity e) { if (!pipeCache.TryGetValue(e, out bool p)) pipeCache[e] = p = IsPipeLike(em, e); return p; }

                var nodesOf = new HashSet<Entity>[Count];
                var anyInjector = new HashSet<Entity>();
                for (int i = 0; i < Count; i++)
                {
                    nodesOf[i] = new HashSet<Entity>();
                    foreach (var part in sInjectors[i].Parts) NodesOf(em, part, nodesOf[i]);
                    anyInjector.UnionWith(nodesOf[i]);
                }

                for (int i = 0; i < Count; i++)
                {
                    var injector = sInjectors[i];
                    var nodes = nodesOf[i];
                    // its own joints to the rest of the ship
                    int joints = 0;
                    var holders = new List<string>();
                    var start = new List<Entity>();
                    foreach (var node in nodes)
                        if (links.TryGetValue(node, out var near))
                            foreach (var other in near)
                            {
                                if (nodes.Contains(other)) continue;
                                joints++;
                                if (Pipe(other) && !anyInjector.Contains(other)) start.Add(other);
                                if (!injector.HasHandle && sCore != null && em.Exists(other) && em.TryGetComponentObject<StructurePart>(other, out var held)
                                    && held != null && held.name.IndexOf("AirlockConsole", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    injector.LocalHandle = sCore.transform.InverseTransformPoint(held.transform.position);
                                    injector.HasHandle = true;
                                }
                                if (holders.Count < 8) holders.Add(NameOf(em, other));
                            }

                    // its fuel line: pipes reachable through pipes, and the non-pipe parts the line joins
                    var pipes = new HashSet<Entity>();
                    var anchors = new HashSet<Entity>();
                    var queue = new Queue<Entity>(start);
                    foreach (var e in start) pipes.Add(e);
                    while (queue.Count > 0 && pipes.Count < LineMaxPipes)
                    {
                        var at = queue.Dequeue();
                        if (!links.TryGetValue(at, out var near)) continue;
                        foreach (var other in near)
                        {
                            if (nodes.Contains(other) || pipes.Contains(other)) continue;
                            if (anyInjector.Contains(other)) continue;                  // other injectors are not anchors
                            if (Pipe(other)) { pipes.Add(other); queue.Enqueue(other); }
                            else anchors.Add(other);
                        }
                    }

                    // Its handle lets go of the injector's mounting: every outside joint except to the handle's own console
                    // and to the cut point of its purple pipe (with the stock nozzles, the four fuel feeds; with the Mistral
                    // nozzles, the engine nozzle panel). Recorded at setup; all of them gone: the handle was pulled.
                    var mounts = new HashSet<Entity>();
                    foreach (var node in nodes)
                        if (links.TryGetValue(node, out var near))
                            foreach (var other in near)
                                if (!nodes.Contains(other) && !IsHandleOrCutPoint(em, other)) mounts.Add(other);

                    bool first = injector.LastAttached < 0;
                    if (first) { injector.Released.Clear(); injector.Released.UnionWith(mounts); }
                    int mounted = mounts.Count(x => injector.Released.Contains(x));
                    bool handlePulled = injector.Released.Count > 0 && mounted == 0;
                    if (first)
                    {
                        // keep the anchors at the reactor's lid, if the line reaches it
                        var corePosition = sCore != null ? sCore.transform.position : sWorldCenter;
                        var lid = anchors.Where(x => PositionOf(em, x) is Vector3 p && (p - corePosition).sqrMagnitude < LidReach * LidReach).ToList();
                        injector.Anchors.Clear();
                        injector.Anchors.UnionWith(lid.Count > 0 ? lid : anchors.ToList());
                        injector.UsesFeeds = pipes.Count > 0 && injector.Anchors.Count > 0;
                        string Where(Entity x) => NameOf(em, x) + (PositionOf(em, x) is Vector3 p ? $" {(p - corePosition).magnitude:0.0} m from the core" : "");
                        Plugin.Log.LogInfo($"[Reactor] injector {i} line: {pipes.Count} pipe part(s) " +
                                           $"({string.Join(", ", pipes.Take(6).Select(x => NameOf(em, x)).Distinct())}), joining the ship at {anchors.Count} part(s)" +
                                           (anchors.Count > 0 ? ": " + string.Join("; ", anchors.Take(8).Select(Where)) : "") +
                                           (injector.UsesFeeds ? (lid.Count > 0 ? $"; {lid.Count} at the lid decide it." : "; none at the lid: all of them decide it.")
                                                               : "; no line: any joint keeps it connected."));
                    }
                    int reached = injector.UsesFeeds ? anchors.Count(x => injector.Anchors.Contains(x)) : 0;
                    bool connected = (injector.UsesFeeds ? reached > 0 : joints > 0) && !handlePulled;

                    // Diagnostic: every change in its joints, its line, or what the line still reaches.
                    int state = joints * 100000 + pipes.Count * 1000 + reached * 10 + mounted;
                    if (state != injector.LastAttached)
                    {
                        if (!first || sPhase == Phase.Waiting)
                            Plugin.Log.LogInfo($"[Reactor] injector {i}: {joints} joint(s) to the rest of the ship" +
                                               (holders.Count > 0 ? " (" + string.Join(", ", holders.Distinct()) + ")" : "") +
                                               $"; line {pipes.Count} pipe part(s), reaching {reached} of its {injector.Anchors.Count} anchor(s)" +
                                               $"; mounting {mounted} of {injector.Released.Count} joint(s){(handlePulled ? " (handle pulled)" : "")}" +
                                               (connected ? "." : "; it counts as disconnected."));
                        injector.LastAttached = state;
                    }
                    if (injector.Connected && !connected && !sSimulated[i]) lost.Add(i);
                    injector.Connected = connected;
                }
            }
            finally { edges.Dispose(); }
            return lost;
        }

        // ---- the balance --------------------------------------------------------------------------------------------
        // The unbalanced push, in the core's space: the sum of the disconnected injectors' directions (also counting
        // injector `also` as disconnected, for the test keys' look-ahead).
        static Vector3 LocalImbalance(int also = -1)
        {
            var sum = Vector3.zero;
            for (int i = 0; i < Count; i++)
                if (!Connected(i) || i == also) sum += sInjectors[i].LocalDirection;
            return sum;
        }

        static float Imbalance => LocalImbalance().magnitude;
        static bool Unstable => Imbalance > Mathf.Max(0f, Plugin.ReactorUnstableAt.Value);

        static string BalanceText()
        {
            float m = Imbalance;
            string gaps = string.Concat(Enumerable.Range(0, Count).Select(i => Connected(i) ? i.ToString() : "-"));
            return $"injectors {gaps}, imbalance {m:0.00}" + (m > Plugin.ReactorUnstableAt.Value ? " (UNSTABLE)" : m < 0.05f ? " (in balance)" : " (safe)");
        }

        // ---- the rules ----------------------------------------------------------------------------------------------
        static void Logic(float dt)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;

            switch (sPhase)
            {
                case Phase.Running:
                {
                    if (!CoreAttached(em)) { Explode(em, "the reactor core was pulled out, cut loose or destroyed"); break; }
                    foreach (int i in UpdateConnections(em)) OnDisconnected(i);
                    if (sPhase != Phase.Running) break;
                    // Meltdown: badly unbalanced for too long.
                    if (Plugin.ReactorMeltdown.Value && Imbalance >= Plugin.ReactorMeltdownAt.Value)
                    {
                        if (sBadSince < 0f)
                        {
                            sBadSince = Time.time;
                            Plugin.Log.LogInfo($"[Reactor] badly unbalanced ({BalanceText()}): meltdown in {Plugin.ReactorMeltdownSeconds.Value:0} s unless it is evened out.");
                        }
                        else if (Time.time - sBadSince >= Mathf.Max(1f, Plugin.ReactorMeltdownSeconds.Value))
                        {
                            sPhase = Phase.Meltdown;
                            sPhaseStarted = Time.time;
                            Plugin.Log.LogInfo("[Reactor] MELTDOWN, started as " + ReactorFire.StartMeltdown(em, sCoreEntity) + ".");
                        }
                    }
                    else if (sBadSince >= 0f)
                    {
                        sBadSince = -1f;
                        Plugin.Log.LogInfo($"[Reactor] evened out in time: {BalanceText()}.");
                    }
                    break;
                }
                case Phase.Meltdown:
                    if (!em.Exists(sCoreEntity) || em.HasComponent<ExplodedComponent>(sCoreEntity) || sCore == null)
                        BeginFade("the reactor core exploded");
                    else if (Time.time - sPhaseStarted > MeltdownGiveUp)
                        BeginFade($"the meltdown did not finish within {MeltdownGiveUp:0} s");
                    break;
                case Phase.Fading:
                    sFade = Mathf.Clamp01(1f - (Time.time - sPhaseStarted) / FadeSeconds);
                    if (sFade <= 0f)
                    {
                        sPhase = Phase.Gone;
                        ReactorVisuals.Destroy();
                        foreach (var injector in sInjectors) if (injector?.FireFx != null) Object.Destroy(injector.FireFx);
                        Plugin.Log.LogInfo("[Reactor] the plasma is out.");
                        return;
                    }
                    break;
            }
            Hazards(em, dt);
            UpdateFireFx();
        }

        static void OnDisconnected(int i)
        {
            if (sInjectors.All(x => !Connected(x.Index))) { BeginFade($"injector {i} was the last one: the reactor is shut down"); return; }
            Plugin.Log.LogInfo($"[Reactor] injector {i} disconnected: {BalanceText()}.");
        }

        // The reactor pulled while the plasma burns: a real explosion at the ball, the game's own. A short-lived entity
        // at the ball carries a copy of the reactor core's explosive settings (what it would use in a meltdown), with
        // its reach doubled, and is marked as exploded; the game's ExplosionSystem then damages, shatters and pushes
        // the parts in reach, hurts and knocks back the player and spawns the explosion effect, just as for a reactor
        // that blows. The core itself is not touched. If the core has no explosive settings, the game's reactor core
        // explosion effect plays instead, and the player and parts nearby are set on fire. The plasma is gone within
        // a second either way.
        const string ExplosionGuid = "c620190dd56b9b64badf8501cd8939ef";    // FX_Explosion_ReactorCore
        const float ExplosionReach = 2f;                                   // times the core's own
        const float ExplosionPlayerRadius = 12f, ExplosionPartRadius = 10f; // the fallback's
        static Entity sBlast = Entity.Null;

        static void Explode(EntityManager em, string why)
        {
            var at = BallCenter;
            Plugin.Log.LogInfo($"[Reactor] EXPLOSION: {why}.");
            if (!RealExplosion(em, at)) FakeExplosion(em, at);
            BeginFade(why);
            sPhaseStarted = Time.time - FadeSeconds * 0.8f;     // gone within a second
        }

        static bool RealExplosion(EntityManager em, Vector3 at)
        {
            if (!em.Exists(sCoreEntity) || !em.HasComponent<ExplosiveComponent>(sCoreEntity))
            {
                Plugin.Log.LogInfo("[Reactor] the reactor core has no explosive settings: a fire-only explosion instead.");
                return false;
            }
            try
            {
                if (sBlast != Entity.Null && em.Exists(sBlast)) em.DestroyEntity(sBlast);
                var explosive = em.GetComponentData<ExplosiveComponent>(sCoreEntity);
                explosive.ElementalBounds *= ExplosionReach;
                explosive.MaxFXScale *= ExplosionReach;
                var blast = em.CreateEntity();
                em.AddComponentData(blast, explosive);
                string what = $"reach {explosive.ElementalBounds:0.#} m, strength {explosive.ElementalStrength:0.#}";
                if (em.HasComponent<ExplosiveDamageSettings>(sCoreEntity))
                {
                    var damage = em.GetComponentData<ExplosiveDamageSettings>(sCoreEntity);
                    damage.PlayerDamageRadius *= ExplosionReach;
                    em.AddComponentData(blast, damage);
                    what += $", part damage {damage.ExplosionDamage:0.#}, player damage {damage.PlayerExplosionDamage:0.#} within {damage.PlayerDamageRadius:0.#} m";
                }
                if (em.HasComponent<ExplosiveForceSettings>(sCoreEntity))
                {
                    var force = em.GetComponentData<ExplosiveForceSettings>(sCoreEntity);
                    force.PlayerForceRadius *= ExplosionReach;
                    em.AddComponentData(blast, force);
                    what += $", force {force.ExplosionForce:0.#} (player {force.PlayerExplosionForce:0.#} within {force.PlayerForceRadius:0.#} m)";
                }
                if (em.HasComponent<ExplosiveShatterSettings>(sCoreEntity))
                {
                    em.AddComponentData(blast, em.GetComponentData<ExplosiveShatterSettings>(sCoreEntity));
                    what += ", shatters";
                }
                em.AddComponentData(blast, new Unity.Transforms.LocalToWorld
                {
                    Value = Unity.Mathematics.float4x4.TRS(new Unity.Mathematics.float3(at.x, at.y, at.z), Unity.Mathematics.quaternion.identity, new Unity.Mathematics.float3(1f, 1f, 1f))
                });
                em.AddComponentData(blast, new ExplodedComponent { Time = (float)World.DefaultGameObjectInjectionWorld.Time.ElapsedTime });
                sBlast = blast;
                Plugin.Log.LogInfo($"[Reactor] a real explosion at the ball, from the core's own settings doubled: {what}.");
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Reactor] the real explosion could not be set off: " + ex.Message + "; a fire-only explosion instead.");
                return false;
            }
        }

        static void FakeExplosion(EntityManager em, Vector3 at)
        {
            try
            {
                Addressables.LoadAssetAsync<GameObject>(ExplosionGuid).Completed += handle =>
                {
                    if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                    { Plugin.Log.LogWarning("[Reactor] the reactor core explosion effect could not be loaded."); return; }
                    var boom = Object.Instantiate(handle.Result, at, Quaternion.identity);
                    boom.name = "TheExpanse_ReactorExplosion";
                    boom.transform.localScale = Vector3.one * ExplosionReach;
                    boom.SetActive(true);
                    foreach (var effect in boom.GetComponentsInChildren<UnityEngine.VFX.VisualEffect>(true))
                    { effect.enabled = true; effect.Reinit(); effect.Play(); effect.SendEvent("Start"); effect.SendEvent("OnPlay"); }
                    foreach (var system in boom.GetComponentsInChildren<ParticleSystem>(true)) system.Play(true);
                    Object.Destroy(boom, 15f);
                };
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Reactor] the reactor core explosion effect could not be loaded: " + ex.Message); }

            var player = PlayerProfileService.Instance?.Profile?.PlayerEntity ?? Entity.Null;
            if (player != Entity.Null && em.Exists(player) && em.HasComponent<HealthComponent>(player) && GhostNodes.TryGetPosition(em, player, out var where)
                && (where - at).sqrMagnitude < ExplosionPlayerRadius * ExplosionPlayerRadius)
                ReactorFire.IgnitePlayer(em, player, 0.1f * em.GetComponentData<HealthComponent>(player).Max);
            if (Plugin.ReactorBurnsParts.Value) BurnAround(em, at, ExplosionPartRadius, null);
        }

        static void BeginFade(string why)
        {
            if (sPhase == Phase.Fading || sPhase == Phase.Gone) return;
            sPhase = Phase.Fading;
            sPhaseStarted = Time.time;
            Plugin.Log.LogInfo($"[Reactor] the plasma fades out: {why}.");
        }

        // ---- where things are ---------------------------------------------------------------------------------------
        // Follows the core. A sudden jump (over JumpMeters or JumpDegrees in one frame) is held off for up to JumpFrames
        // frames: when a part comes loose the game rebuilds the ship's part hierarchy, and the core can report a wrong
        // place for a frame or two. A jump that lasts longer is real and is taken.
        const float JumpMeters = 0.5f, JumpDegrees = 10f;
        const int JumpFrames = 5;
        static bool sTracked;
        static int sJumpFrames, sJumpFrameSeen = -1, sJumpsHeld;

        static void Track()
        {
            if (sCore == null) return;
            var center = sCore.transform.TransformPoint(sLocalCenter);
            var rotation = sCore.transform.rotation;
            if (sTracked && ((center - sWorldCenter).sqrMagnitude > JumpMeters * JumpMeters || Quaternion.Angle(rotation, sLastCoreRotation) > JumpDegrees))
            {
                if (sJumpFrameSeen != Time.frameCount) { sJumpFrameSeen = Time.frameCount; sJumpFrames++; }
                if (sJumpFrames <= JumpFrames)
                {
                    if (sJumpFrames == 1 && sJumpsHeld++ < 20)
                        Plugin.Log.LogInfo($"[Reactor] the core jumped {(center - sWorldCenter).magnitude:0.00} m / {Quaternion.Angle(rotation, sLastCoreRotation):0} degrees in a frame: held off.");
                    return;
                }
                Plugin.Log.LogInfo($"[Reactor] the core's jump lasted {JumpFrames} frames: taken as real.");
            }
            sJumpFrames = 0;
            sTracked = true;
            sWorldCenter = center;
            sWorldDown = rotation * sLocalDown;
            sLastCoreRotation = rotation;
        }

        static Vector3 ToWorldDirection(Vector3 local) => sLastCoreRotation * local;

        // The ball stays in the middle. In balance it pulses slowly, 1x..2x with all eight feeding, and every balanced
        // disconnect calms it: smaller, slower, a smaller swing. Off balance it pulses faster, larger and bigger
        // with every injector's worth.
        static Vector3 BallCenter => sWorldCenter;

        static float BallDiameter()
        {
            float m = sShownImbalance, feed = sShownFeed;
            float size = Mathf.Max(0.1f, Plugin.ReactorBallSize.Value) * (0.5f + 0.5f * feed);
            float period = Mathf.Max(0.2f, Plugin.ReactorPulseSeconds.Value) * (2f - feed) / (1f + 1.5f * m);
            float depth = 0.5f * feed + 0.12f * m;        // 1x..2x with all eight feeding
            float pulse = 1f + depth * (1f - Mathf.Cos(PulsePhase(period)));
            float grow = 1f + 0.15f * m + (sPhase == Phase.Meltdown ? 0.5f : 0f);
            return size * pulse * grow;
        }

        // The pulse's phase advances at the current rate, so a change of rate speeds it up or slows it down smoothly.
        static float sPulsePhase, sPulseAt = -1f;
        static float PulsePhase(float period)
        {
            if (sPulseAt != Time.time)
            {
                if (sPulseAt >= 0f) sPulsePhase = (sPulsePhase + (Time.time - sPulseAt) * Mathf.PI * 2f / period) % (Mathf.PI * 2f);
                sPulseAt = Time.time;
            }
            return sPulsePhase;
        }

        // How much the injectors still feed the plasma: 1 with all eight connected, falling as they are disconnected.
        // Eased like the imbalance, so the ball settles down gradually.
        static float sShownFeed = 1f;

        // The color step: 1 safe, 2 unstable, 3 badly unbalanced.
        static int ShownLevel(float m) => m <= Plugin.ReactorUnstableAt.Value ? 1 : m < Plugin.ReactorMeltdownAt.Value ? 2 : 3;

        static void Draw()
        {
            Track();
            float m = Imbalance;
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            sShownImbalance = Mathf.MoveTowards(sShownImbalance, m, dt * 0.5f);
            sShownFeed = Mathf.MoveTowards(sShownFeed, sInjectors.Count(x => x != null && Connected(x.Index)) / (float)Count, dt * 0.05f);
            for (int i = 0; i < Count; i++)
            {
                var x = sInjectors[i];
                sFeeds[i] = new ReactorFeeds.Feed { Connected = Connected(i) && sPhase != Phase.Fading, LocalNozzle = x.LocalNozzle,
                    LocalSlot = x.LocalSlot, LocalHandle = x.LocalHandle, LocalDirection = x.LocalDirection };
            }
            ReactorFeeds.Show(sCore != null ? sCore.transform : null, sLocalCenter, sLocalDown, sLocalCoreTip, sFeeds, LocalImbalance(),
                              sPhase == Phase.Fading ? sFade : 1f, sPulsePhase / (Mathf.PI * 2f));
            ReactorVisuals.Show(BallCenter, BallDiameter(), ShownLevel(sShownImbalance), Unstable && sPhase != Phase.Fading,
                                sPhase == Phase.Fading ? sFade : 1f);
        }

        // ---- hazards ------------------------------------------------------------------------------------------------
        static void Hazards(EntityManager em, float dt)
        {
            if (sPhase == Phase.Gone || sPhase == Phase.Waiting) return;
            float fade = sPhase == Phase.Fading ? sFade : 1f;
            if (fade <= 0.05f) return;
            Track();
            var center = BallCenter;
            float ballRadius = BallDiameter() * 0.5f * fade;

            var player = PlayerProfileService.Instance?.Profile?.PlayerEntity ?? Entity.Null;
            if (player != Entity.Null && em.Exists(player) && em.HasComponent<HealthComponent>(player) && GhostNodes.TryGetPosition(em, player, out var at))
            {
                float fromSurface = Vector3.Distance(at, center) - ballRadius;
                float health = em.GetComponentData<HealthComponent>(player).Max;

                // Radiation, falling evenly to nothing at the edge.
                float reach = Mathf.Max(0.1f, Plugin.ReactorRadiationRadius.Value);
                if (fromSurface < reach)
                {
                    float dose = 1f - Mathf.Clamp01(fromSurface / reach);
                    ReactorFire.IrradiatePlayer(em, player, dose * Mathf.Max(0f, Plugin.ReactorRadiationDamage.Value) / 100f * health * dt * fade);
                }

                // Touching the ball sets the player on fire; so does the leak cone, while the plasma is unstable.
                if (fromSurface < Mathf.Max(0f, Plugin.ReactorHeatRadius.Value)
                    || (Unstable && ReactorFeeds.TryCone(out var apex, out var axis, out float coneReach, out float half) && InCone(at, apex, axis, coneReach, half, 0.3f)))
                    ReactorFire.IgnitePlayer(em, player, 0.02f * health);
            }

            // Unstable plasma burns what it touches.
            if (Unstable && Plugin.ReactorBurnsParts.Value && (sPhase == Phase.Running || sPhase == Phase.Meltdown) && Time.time >= sNextBurn)
            {
                sNextBurn = Time.time + Mathf.Max(0.1f, Plugin.ReactorBurnInterval.Value);
                BurnAround(em, center, ballRadius + 0.3f, null);
                if (ReactorFeeds.TryCone(out var apex, out var axis, out float coneReach, out float half))
                {
                    // the cone's bounding capsule, then only what lies inside the cone
                    float wide = coneReach * Mathf.Tan(half * Mathf.Deg2Rad);
                    BurnAround(em, apex + axis * (coneReach * 0.5f), Mathf.Sqrt(coneReach * coneReach * 0.25f + wide * wide),
                               p => InCone(p, apex, axis, coneReach, half, 0.3f));
                }
            }
        }

        static bool InCone(Vector3 point, Vector3 apex, Vector3 axis, float reach, float halfDegrees, float margin)
        {
            var v = point - apex;
            float along = Vector3.Dot(v, axis);
            if (along < 0f || along > reach + margin) return false;
            return (v - axis * along).magnitude <= along * Mathf.Tan(halfDegrees * Mathf.Deg2Rad) + margin;
        }

        static readonly Collider[] sHits = new Collider[256];

        static void BurnAround(EntityManager em, Vector3 center, float radius, System.Func<Vector3, bool>? inside)
        {
            int n = Physics.OverlapSphereNonAlloc(center, radius, sHits, ~0, QueryTriggerInteraction.Ignore);
            var done = new HashSet<StructurePart>();
            int lit = 0, heated = 0;
            for (int h = 0; h < n; h++)
            {
                var part = sHits[h] != null ? sHits[h].GetComponentInParent<StructurePart>() : null;
                if (part == null || part == sCore) continue;
                if (inside != null && !inside(sHits[h].bounds.center) && !inside(sHits[h].ClosestPoint(center))) continue;
                if (!done.Add(part)) continue;
                var owner = sInjectors.FirstOrDefault(x => x.Parts.Contains(part));
                if (owner != null)
                {
                    ReactorFire.HeatPart(part, Mathf.Max(0f, Plugin.ReactorPartDamage.Value));
                    owner.LastHeated = Time.time;
                    heated++;
                }
                else if (ReactorFire.TryIgnitePart(em, EntityOf(part))) lit++;
            }
            if (lit + heated > 0) Plugin.Log.LogInfo($"[Reactor] the unstable plasma burns: {heated} injector part(s) heated, {lit} part(s) set on fire.");
        }

        // A fire effect on each injector being burned, for a few seconds after the last burn.
        static void UpdateFireFx()
        {
            foreach (var injector in sInjectors)
            {
                if (injector == null) continue;
                bool burning = Time.time - injector.LastHeated < 4f && sPhase != Phase.Gone;
                if (!burning)
                {
                    if (injector.FireFx != null) { Object.Destroy(injector.FireFx); injector.FireFx = null; }
                    continue;
                }
                var host = injector.Parts.FirstOrDefault(x => x != null);
                if (injector.FireFx != null || host == null) continue;
                var prefab = FirePrefab();
                if (prefab == null) continue;
                injector.FireFx = Object.Instantiate(prefab, host.transform.position, host.transform.rotation, host.transform);
                injector.FireFx.SetActive(true);
            }
        }

        static GameObject? FirePrefab()
        {
            if (sFirePrefab != null || sFireLoading) return sFirePrefab;
            sFireLoading = true;
            try
            {
                Addressables.LoadAssetAsync<GameObject>(FireFxGuid).Completed += handle =>
                {
                    if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null) sFirePrefab = handle.Result;
                    else { Plugin.Log.LogWarning("[Reactor] the game's fire effect could not be loaded; burning injectors show no flames."); }
                };
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Reactor] the game's fire effect could not be loaded: " + ex.Message); }
            return null;
        }

        // ---- test tools ---------------------------------------------------------------------------------------------
        // ReactorRightKey: the connected injector that leaves the plasma best balanced; ReactorWrongKey: the worst.
        internal static void TestKeys()
        {
            if (!Plugin.TestTools.Value || !Allowed || sPhase != Phase.Running) return;
            bool best = Plugin.ReactorRightKey.Value.IsDown(), worst = Plugin.ReactorWrongKey.Value.IsDown();
            if (!best && !worst) return;
            var connected = Enumerable.Range(0, Count).Where(Connected).ToList();
            if (connected.Count == 0) return;
            var scored = connected.Select(i => (i, after: LocalImbalance(also: i).magnitude)).ToList();
            float pickScore = best ? scored.Min(x => x.after) : scored.Max(x => x.after);
            var ties = scored.Where(x => Mathf.Abs(x.after - pickScore) < 0.01f).ToList();
            int pick = ties[Random.Range(0, ties.Count)].i;
            sSimulated[pick] = true;
            Plugin.Log.LogInfo($"[Test] treating injector {pick} as disconnected ({(best ? "the best balanced choice" : "the worst choice")}).");
            OnDisconnected(pick);
        }

        static GUIStyle? sStyle;

        internal static void DrawTest()
        {
            if (!Plugin.TestTools.Value || !Allowed || sPhase == Phase.Waiting || sInjectors[0] == null) return;
            sStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            string text = sPhase == Phase.Gone ? "Reactor: out"
                : $"Reactor: {sPhase}, {BalanceText()}   {Plugin.ReactorRightKey.Value}: best   {Plugin.ReactorWrongKey.Value}: worst";
            var area = new Rect(20, 56, 1400, 32);
            sStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(area.x + 2, area.y + 2, area.width, area.height), text, sStyle);
            sStyle.normal.textColor = new Color(1f, 0.75f, 0.4f);
            GUI.Label(area, text, sStyle);
        }
    }
}
