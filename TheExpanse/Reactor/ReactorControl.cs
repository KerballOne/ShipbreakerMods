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
    internal static partial class ReactorControl
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
            internal StructurePart? HandlePart; // the handle's console
            internal bool Connected = true;
            internal bool HandlePulled;         // it went by its handle (timed), not by a cut pipe
            internal float PressAt = -100f;     // when its handle switch was last used, and whether the lights were dark then
            internal bool PressSafe;
            internal float JointAt = -1f;       // its first joint event since the last check, and whether it came in the window
            internal bool JointSafe;
            internal bool UsesFeeds;            // a fuel line found at setup: whether it still reaches its anchors decides it
            internal readonly HashSet<Entity> Anchors = new HashSet<Entity>();   // where its fuel line joined the ship at setup
            internal readonly HashSet<Entity> Released = new HashSet<Entity>();  // what its handle lets go of, from setup
            internal bool EngineMounted;        // jointed to the big engine nozzle at setup: that joint decides the handle
            internal readonly HashSet<Entity> Line = new HashSet<Entity>();      // its fuel line's pipe parts, as last checked
            internal int LastAttached = -1;     // joints at the last check (-1 before the first)
            internal float LastHeated = -100f;
            internal GameObject? FireFx;
        }

        static readonly Injector[] sInjectors = new Injector[Count];
        static readonly bool[] sSimulated = new bool[Count];       // test tools: treated as disconnected
        static Phase sPhase = Phase.Waiting;
        static float sNextSetupTry;
        static float sSpawnedAt = -1f, sLogicDue, sPhaseStarted, sBadSince = -1f, sNextCountdownLog;
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
            HandleLights.Destroy();
            TerminalScreens.Destroy();
            ReactorFire.Reset();
            sPhase = Phase.Waiting;
            sSpawnedAt = -1f;
            sCore = null; sCoreEntity = Entity.Null;

            sShownImbalance = 0f;
            sShownFeed = 1f;
            sPulseAt = -1f;
            sBadSince = -1f;
            sEscalation = 0; sEscalateFrom = -1f; sLastRaw = 0f;
            sDonutDone = false;
            sPendingOutcome.Clear();
            sTracked = false; sJumpFrames = 0; sJumpsHeld = 0;
            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (sBlast != Entity.Null && world != null && world.IsCreated && world.EntityManager.Exists(sBlast)) world.EntityManager.DestroyEntity(sBlast);
            }
            catch { }
            sBlast = Entity.Null;
            sBlastCached = false;
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
                if (sSpawnedAt < 0f || !Allowed || Time.time - sSpawnedAt < SetupDelay || Time.time < sNextSetupTry) return;
                sNextSetupTry = Time.time + 1f;          // a failed setup is retried once a second, not every frame
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool ready = TrySetUp();
                Plugin.Log.LogInfo($"[Reactor] setup {(ready ? "done" : "tried")} in {watch.Elapsed.TotalMilliseconds:0} ms (one frame).");
                if (!ready)
                {
                    if (Time.time - sSpawnedAt > SetupGiveUp) { sPhase = Phase.Gone; sSpawnedAt = -1f; }
                    return;
                }
            }
            if (sPhase == Phase.Gone) { if (!Allowed) HandleLights.Destroy(); return; }
            if (!Allowed) { ReactorVisuals.Destroy(); HandleLights.Destroy(); TerminalScreens.Destroy(); return; }
            if (!ReactorVisuals.Built) ReactorVisuals.Build();

            if (Time.time >= sLogicDue)
            {
                sLogicDue = Time.time + LogicSeconds;
                Logic(LogicSeconds);
            }
            Draw();
        }

        // ---- finding the reactor ------------------------------------------------------------------------------------
        // The scene's parts, gathered once per setup (asking the game for them is slow; setup used to ask ~20 times).
        static StructurePart[]? sParts;
        static bool sInSetUp;
        internal static StructurePart[] Parts => sInSetUp ? (sParts ??= Object.FindObjectsOfType<StructurePart>()) : Object.FindObjectsOfType<StructurePart>();

        static bool TrySetUp()
        {
            sInSetUp = true;
            try { return SetUp(); }
            finally { sParts = null; sInSetUp = false; }
        }

        static bool SetUp()
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
            foreach (var part in Parts)
            {
                if (part == null || part == core) continue;
                if ((part.transform.position - corePosition).sqrMagnitude > 12f * 12f) continue;
                if (!IsInjector(part, out int key)) continue;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<StructurePart>();
                list.Add(part);
            }
            string how = "by name";
            sSlotAngles = null;
            if (groups.Count != Count)
            {
                // The game reparents parts, so names above the part may be gone: find them by where they sit instead.
                var byShape = InjectorsByShape(core, corePosition, axis);
                if (byShape == null)
                    return Fail($"{groups.Count} injector(s) found near the core by name, not {Count}, and none by shape: " + Describe(core, corePosition, axis));
                groups = byShape;
                how = "by shape (thruster parts on the ring)";
            }

            // Ring middle (on the core's axis, level with the injectors), and "down": along the axis, toward the ring's side.
            var present = groups.Values.Where(l => l.Count > 0).ToList();
            float level = present.Average(l => Vector3.Dot(Middle(l) - corePosition, axis));
            Vector3 ring = corePosition + axis * level;
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

            // Number the injectors by angle around the axis (an empty slot by its slot angle).
            // Numbered from the slot nearest the core's own forward (going round the axis), whatever is left on the
            // ring, so a reload with some already taken off numbers them the same as the fresh ship (and its order
            // means the same injectors). Every slot's direction is the first present one's, turned about the axis.
            Vector3 firstDir = Vector3.ProjectOnPlane(Middle(present.First()) - ring, down).normalized;
            float firstAngle = AroundCore(core, axis, Middle(present.First())).angle;
            float startAngle = sSlotAngles != null ? sSlotAngles[0] : present.Min(l => AroundCore(core, axis, Middle(l)).angle);
            Vector3 SlotDir(float angle) => Quaternion.AngleAxis(angle - firstAngle, axis) * firstDir;
            Vector3 reference = SlotDir(startAngle);
            float SlotAngle(KeyValuePair<int, List<StructurePart>> kv)
            {
                if (kv.Value.Count > 0) return Vector3.SignedAngle(reference, Vector3.ProjectOnPlane(Middle(kv.Value) - ring, down), down);
                return Vector3.SignedAngle(reference, SlotDir(sSlotAngles![kv.Key]), down);     // an empty slot: its fitted angle
            }
            var ordered = groups.Select(kv => (list: kv.Value, angle: SlotAngle(kv)))
                                      .Select(x => (x.list, angle: x.angle < 0f ? x.angle + 360f : x.angle))
                                      .OrderBy(x => x.angle).ToList();
            var angles = new List<string>();
            var template = -1;
            for (int i = 0; i < Count; i++)
            {
                if (ordered[i].list.Count == 0) continue;           // empty slots after, from a present one
                var injector = new Injector { Index = i };
                injector.Parts.AddRange(ordered[i].list);
                injector.LocalSlot = core.transform.InverseTransformPoint(Middle(ordered[i].list));
                var nozzle = NozzleTip(ordered[i].list, center);
                injector.LocalNozzle = core.transform.InverseTransformPoint(nozzle);
                // which way it pushes from: ball toward its nozzle, across the ring
                var across = Vector3.ProjectOnPlane(nozzle - center, down);
                injector.LocalDirection = core.transform.InverseTransformDirection(across.sqrMagnitude > 1e-6f ? across.normalized : Vector3.zero);
                sInjectors[i] = injector;
                if (template < 0) template = i;
            }
            // Empty slots (injectors already taken off): the present one's place, nozzle and push, turned about the axis
            // to the slot. No parts: it counts as disconnected.
            for (int i = 0; i < Count; i++)
            {
                if (sInjectors[i] != null && ordered[i].list.Count > 0) continue;
                var t = sInjectors[template];
                var turn = Quaternion.AngleAxis(Vector3.SignedAngle(Vector3.ProjectOnPlane(core.transform.TransformPoint(t.LocalSlot) - ring, down),
                                                                    Quaternion.AngleAxis(ordered[i].angle - ordered[template].angle, down) * Vector3.ProjectOnPlane(core.transform.TransformPoint(t.LocalSlot) - ring, down), down), down);
                Vector3 Turned(Vector3 local) => core.transform.InverseTransformPoint(center + turn * (core.transform.TransformPoint(local) - center));
                sInjectors[i] = new Injector
                {
                    Index = i, Connected = false,
                    LocalSlot = Turned(t.LocalSlot), LocalNozzle = Turned(t.LocalNozzle),
                    LocalDirection = core.transform.InverseTransformDirection(turn * core.transform.TransformDirection(t.LocalDirection)),
                };
            }
            for (int i = 0; i < Count; i++) angles.Add($"{i}:{ordered[i].angle:0}{(ordered[i].list.Count == 0 ? " (taken off)" : "")}");

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
            TerminalScreens.Find(core, center, down);
            ChooseMode(core);
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
            // Only parts jointed to the ship (in the reactor's structure graph): an injector taken off, or anything
            // loose, does not count wherever it drifts.
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            bool keyed = TryGraphKey(em, EntityOf(core), out int shipKey);
            int loose = 0;
            var found = new List<(StructurePart part, float angle, float height)>();
            foreach (var part in Parts)
            {
                if (part == null || part == core || !IsThrusterPart(part)) continue;
                if (keyed && (!TryGraphKey(em, EntityOf(part), out int key) || key != shipKey))
                {
                    if ((part.transform.position - corePosition).sqrMagnitude <= 12f * 12f) loose++;
                    continue;
                }
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
            Plugin.Log.LogInfo($"[Reactor] by shape: {found.Count} thruster part(s) jointed to the ship on the ring" + (loose > 0 ? $" ({loose} loose near it left out)" : "") + $", in {clusters.Count} cluster(s) at " +
                               string.Join(" ", clusters.Select(c => $"{c.Average(x => x.angle):0}({c.Count})")));
            // The slots, always (even with eight clusters, a slot may hold only leftovers): the injectors sit
            // every 45 degrees, so fit that lattice to the parts, and put each part within 12 degrees of a slot into it.
            // A slot with too few parts is empty: its injector is taken off (filled in later, from a present one).
            float best = 0f, bestScore = -1f;
            for (float phase = 0f; phase < 45f; phase += 0.5f)
            {
                float score = 0f;
                foreach (var f in found) { float d = Mathf.Abs(Mathf.DeltaAngle(f.angle, phase + Mathf.Round((f.angle - phase) / 45f) * 45f)); score += Mathf.Max(0f, 12f - d); }
                if (score > bestScore) { bestScore = score; best = phase; }
            }
            var slots = new Dictionary<int, List<StructurePart>>();
            for (int k = 0; k < Count; k++) slots[k] = new List<StructurePart>();
            foreach (var f in found)
            {
                int k = ((int)Mathf.Round((f.angle - best) / 45f) % Count + Count) % Count;
                if (Mathf.Abs(Mathf.DeltaAngle(f.angle, best + k * 45f)) <= 12f) slots[k].Add(f.part);
            }
            // A slot has its injector if its thruster body (the part the handle ejects: _Mid or _Rear) is jointed to the
            // ship (all parts here are); the leftovers that stay jointed (nozzle stub, crossbeam, front) do not count. Ships whose parts are named
            // otherwise: at least 60% of the fullest slot's parts.
            bool Body(StructurePart p) => p.name.IndexOf("1Ax1Ax6_Mid", System.StringComparison.OrdinalIgnoreCase) >= 0
                                       || p.name.IndexOf("1Ax1Ax6_Rear", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool byBody = slots.Values.Any(l => l.Any(Body));
            int enough = Mathf.Max(3, Mathf.CeilToInt(slots.Values.Max(l => l.Count) * 0.6f));
            bool Has(List<StructurePart> l) => byBody ? l.Any(Body) : l.Count >= enough;
            int present = slots.Values.Count(Has);
            Plugin.Log.LogInfo($"[Reactor] by shape: fitted the ring's slots at {best:0} + 45 n degrees; {present} injector(s) still there, " +
                               $"{Count - present} already taken off (by {(byBody ? "the thruster body" : "part count")}; {string.Join(" ", slots.Select(kv => $"{best + kv.Key * 45f:0}({kv.Value.Count})"))}).");
            if (present == 0) return null;
            // The Expanse reactor's injectors are the long 1Ax1Ax6 thrusters. A ring of other thrusters (a stock small
            // reactor's, as on The Wombat) is not one: no reactor game there (it showed a pulsing ball in the ship).
            if (!byBody)
            {
                Plugin.Log.LogInfo("[Reactor] by shape: no injector thruster bodies (1Ax1Ax6) on the ring: not the Expanse reactor; no reactor game on this ship.");
                return null;
            }
            sSlotAngles = Enumerable.Range(0, Count).Select(k => best + k * 45f).ToArray();
            foreach (var key in slots.Keys.ToList()) if (!Has(slots[key])) slots[key].Clear();
            return slots;
        }

        // The slot angles of a fitted ring (AroundCore's angles), for filling in injectors already taken off; null when
        // all eight were found.
        static float[]? sSlotAngles;

        static string Describe(StructurePart core, Vector3 corePosition, Vector3 axis)
        {
            var near = Parts
                .Where(p => p != null && p != core && (p.transform.position - corePosition).sqrMagnitude < 12f * 12f)
                .Select(p => (p, where: AroundCore(core, axis, p.transform.position)))
                .OrderBy(x => x.where.radius)
                .Take(60)
                .Select(x => $"{x.p.name} [{(x.p.StructurePartAsset != null ? x.p.StructurePartAsset.name : "?")}] r {x.where.radius:0.0} h {x.where.height:0.0} a {x.where.angle:0}");
            return $"core '{core.name}' axis {axis}; parts near the core: " + string.Join("; ", near);
        }

        // Searched under the hierarchies of the parts near the core (inactive objects included), not every object loaded.
        static Transform? FindMarker(string name, Vector3 near, float within)
        {
            var roots = new HashSet<Transform>();
            foreach (var part in Parts)
                if (part != null && (part.transform.position - near).sqrMagnitude < 40f * 40f) roots.Add(part.transform.root);
            foreach (var root in roots)
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name == name && (t.position - near).sqrMagnitude < within * within) return t;
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
                foreach (var part in Parts)
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
                var engineCache = new Dictionary<Entity, bool>();
                bool Engine(Entity e)
                {
                    if (!engineCache.TryGetValue(e, out bool n))
                        engineCache[e] = n = em.Exists(e) && em.TryGetComponentObject<StructurePart>(e, out var part) && part != null
                                             && part.name.StartsWith("EngineNozzle", System.StringComparison.OrdinalIgnoreCase);
                    return n;
                }

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
                    int joints = 0, engineJoints = 0;
                    var holders = new List<string>();
                    var start = new List<Entity>();
                    foreach (var node in nodes)
                        if (links.TryGetValue(node, out var near))
                            foreach (var other in near)
                            {
                                if (nodes.Contains(other)) continue;
                                joints++;
                                if (Engine(other)) engineJoints++;
                                if (Pipe(other) && !anyInjector.Contains(other)) start.Add(other);
                                if (!injector.HasHandle && sCore != null && em.Exists(other) && em.TryGetComponentObject<StructurePart>(other, out var held)
                                    && held != null && held.name.IndexOf("AirlockConsole", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    injector.LocalHandle = sCore.transform.InverseTransformPoint(held.transform.position);
                                    injector.HasHandle = true;
                                    injector.HandlePart = held;
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

                    // The handle lets go of the injector's joint to the big engine nozzle (its own panel of it): that
                    // joint gone, the injector is off. (Waiting for every mounting joint to go missed injectors whose
                    // nozzle flaps stay jointed to them.) Ships without that joint: every mounting joint recorded at
                    // setup gone.
                    bool first = injector.LastAttached < 0;
                    if (first) { injector.Released.Clear(); injector.Released.UnionWith(mounts); injector.EngineMounted = engineJoints > 0; }
                    int mounted = injector.EngineMounted ? engineJoints : mounts.Count(x => injector.Released.Contains(x));
                    bool handlePulled = injector.EngineMounted ? engineJoints == 0 : injector.Released.Count > 0 && mounted == 0;
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
                    injector.Line.Clear(); injector.Line.UnionWith(pipes);
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
                                               (injector.EngineMounted ? $"; {engineJoints} joint(s) to the engine nozzle{(handlePulled ? " (handle pulled: off the engine nozzle)" : "")}"
                                                                       : $"; mounting {mounted} of {injector.Released.Count} joint(s){(handlePulled ? " (handle pulled)" : "")}") +
                                               (connected ? "." : "; it counts as disconnected."));
                        injector.LastAttached = state;
                    }
                    injector.HandlePulled = handlePulled;
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

        // The balance counts toward the level only in Balance; in Memorize and Resonance the order is the only rule.
        static bool OrderMode => CurrentMode != Mode.Balance;
        static float RawImbalance => OrderMode ? 0f : LocalImbalance().magnitude;
        static float Imbalance => Escalated(RawImbalance);

        // The push with escalation: the same direction, the escalated strength.
        static Vector3 EscalatedLocalImbalance()
        {
            var v = LocalImbalance();
            float raw = v.magnitude;
            return raw > 1e-4f ? v * (Imbalance / raw) : v;
        }
        static bool Unstable => Imbalance > Mathf.Max(0f, Plugin.ReactorUnstableAt.Value);

        static string BalanceText()
        {
            float m = Imbalance;
            string escalated = sEscalation > 0 ? $", raised from level {LevelOf(RawImbalance)} by {sEscalation} of waiting" : "";
            string gaps = string.Concat(Enumerable.Range(0, Count).Select(i => Connected(i) ? i.ToString() : "-"));
            string state = m > Plugin.ReactorUnstableAt.Value ? "UNSTABLE" : m < 0.05f ? "in balance" : "safe";
            string level = m < 0.05f ? "level 0" : $"LEVEL {LevelOf(m)}";
            return $"{level} ({state}{escalated}), injectors {gaps}, imbalance {m:0.00}";
        }

        // ---- escalation ---------------------------------------------------------------------------------------------
        // Left off balance, the plasma gets worse by itself: every EscalateSeconds it goes up a level (1 to 2, 2 to 3; at
        // level 3 the donut flare countdown, MeltdownSeconds, starts). A disconnect that evens the balance out restarts
        // the wait and takes a level of escalation off; a wrong one leaves the clock running. Back in balance it is all gone.
        static int sEscalation;                 // levels added by waiting
        static float sEscalateFrom = -1f;       // when the wait for the next level began
        static float sLastRaw;                  // the balance the wait began at

        // Where level 2 sits: two gaps side by side (1.85 with the default thresholds).
        static float LevelTwoAt => Mathf.Lerp(Plugin.ReactorUnstableAt.Value, Plugin.ReactorMeltdownAt.Value, 0.6f);
        static int LevelOf(float m) => m <= Plugin.ReactorUnstableAt.Value ? 1 : m < Plugin.ReactorMeltdownAt.Value ? 2 : 3;

        static float Escalated(float raw)
        {
            if (OrderMode)
            {
                // the order modes: mistakes and stalling only
                int stacked = Mathf.Min(3, sEscalation + sPenalty);
                return stacked <= 0 ? 0f : stacked >= 3 ? Plugin.ReactorMeltdownAt.Value : stacked == 2 ? LevelTwoAt : 1f;
            }
            bool balanced = raw < 0.05f;
            int added = (balanced ? 0 : sEscalation) + sPenalty;      // waiting only counts off balance; mistakes always
            if (added <= 0) return raw;
            int level = Mathf.Min(3, (balanced ? 0 : LevelOf(raw)) + added);
            return Mathf.Max(raw, level >= 3 ? Plugin.ReactorMeltdownAt.Value : level == 2 ? LevelTwoAt : 1f);
        }

        static float EscalationDelay(float m) => Mathf.Max(1f, Plugin.ReactorEscalateSeconds.Value);

        // Seconds until the plasma goes up a level by itself, or melts down; -1 when nothing is counting down.
        static float NextStepIn()
        {
            if (sPhase != Phase.Running) return -1f;
            float m = Imbalance;
            if (m >= Plugin.ReactorMeltdownAt.Value)
                return sBadSince >= 0f && !sDonutDone
                    ? Mathf.Max(1f, Plugin.ReactorMeltdownSeconds.Value) - (Time.time - sBadSince) : -1f;
            if (Plugin.ReactorEscalation.Value && sEscalateFrom >= 0f && (OrderMode ? Owed >= 0 && sPenalty > 0 : RawImbalance >= 0.05f))
                return EscalationDelay(m) - (Time.time - sEscalateFrom);
            return -1f;
        }

        static void Escalate()
        {
            if (OrderMode) { EscalateOrder(); return; }
            float raw = RawImbalance;
            if (!Plugin.ReactorEscalation.Value || raw < 0.05f)
            {
                if (sEscalation > 0) Plugin.Log.LogInfo("[Reactor] escalation cleared: " + (raw < 0.05f ? "back in balance." : "escalation is off."));
                sEscalation = 0; sEscalateFrom = -1f; sLastRaw = raw;
                return;
            }
            if (Mathf.Abs(raw - sLastRaw) > 0.01f)
            {
                // Only a disconnect that evens the balance out restarts the wait; a wrong one leaves the clock running.
                if (raw < sLastRaw)
                {
                    if (sEscalation > 0)
                    {
                        sEscalation--;
                        Plugin.Log.LogInfo($"[Reactor] evened out a level of escalation: {BalanceText()}.");
                    }
                    sEscalateFrom = Time.time;
                }
                else if (sEscalateFrom >= 0f) Plugin.Log.LogInfo($"[Reactor] a wrong disconnect: the escalation clock keeps running ({Time.time - sEscalateFrom:0} s in).");
                sLastRaw = raw;
            }
            if (sEscalateFrom < 0f) sEscalateFrom = Time.time;
            float m = Imbalance;
            if (m >= Plugin.ReactorMeltdownAt.Value) return;        // level 3: the meltdown countdown takes over
            float delay = EscalationDelay(m);
            if (Time.time - sEscalateFrom < delay) return;
            sEscalation++;
            sEscalateFrom = Time.time;
            float next = Imbalance;
            Plugin.Log.LogInfo($"[Reactor] left off balance for {delay:0} s: escalated to level {LevelOf(next)}: {BalanceText()}" +
                               (next < Plugin.ReactorMeltdownAt.Value ? $"; next level in {EscalationDelay(next):0} s." : "."));
        }

        // Memorize and Resonance: following the order is safe (no clock). After a mistake the clock runs: every
        // EscalateSeconds until a correct disconnect, up a level. A correct one resets it all (CheckOrder).
        static void EscalateOrder()
        {
            if (!Plugin.ReactorEscalation.Value || Owed < 0 || sPenalty <= 0) { sEscalation = 0; sEscalateFrom = -1f; return; }
            if (sEscalateFrom < 0f) sEscalateFrom = Time.time;
            if (Imbalance >= Plugin.ReactorMeltdownAt.Value) return;           // level 3: the donut countdown takes over
            float delay = EscalationDelay(0f);
            if (Time.time - sEscalateFrom < delay) return;
            sEscalation++;
            sEscalateFrom = Time.time;
            Plugin.Log.LogInfo($"[Reactor] no correct disconnect for {delay:0} s: up to level {ShownLevel(Imbalance)} ({sEscalation} from waiting, {sPenalty} from mistakes).");
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
                    CacheBlast(em);
                    sWindowSinceCheck = sWindowSinceCheckNext || InSafeWindow();
                    sWindowSinceCheckNext = false;
                    if (!CoreAttached(em)) { Explode(em, "the reactor was pulled out, cut loose or destroyed"); break; }
                    foreach (int i in UpdateConnections(em)) OnDisconnected(i);
                    sLastCheckAt = Time.time;
                    if (sPhase != Phase.Running) break;
                    Escalate();
                    // Meltdown: badly unbalanced for too long.
                    // Level 3: after MeltdownSeconds the donut flare, which cannot be undone.
                    if (!sDonutDone && Imbalance >= Plugin.ReactorMeltdownAt.Value)
                    {
                        if (sBadSince < 0f)
                        {
                            sBadSince = Time.time;
                            Plugin.Log.LogInfo($"[Reactor] badly unbalanced ({BalanceText()}): the donut flare in {Plugin.ReactorMeltdownSeconds.Value:0} s unless it is evened out.");
                        }
                        else if (Time.time >= sNextCountdownLog && Time.time - sBadSince < Mathf.Max(1f, Plugin.ReactorMeltdownSeconds.Value))
                        {
                            // Diagnostic: the countdown, every 2 s (a meltdown once failed to come).
                            sNextCountdownLog = Time.time + 2f;
                            Plugin.Log.LogInfo($"[Reactor] donut flare countdown: {Mathf.Max(1f, Plugin.ReactorMeltdownSeconds.Value) - (Time.time - sBadSince):0.0} s left ({BalanceText()}).");
                        }
                        if (sBadSince >= 0f && Time.time - sBadSince >= Mathf.Max(1f, Plugin.ReactorMeltdownSeconds.Value))
                        {
                            Donut(em);
                            if (MeltdownAllowed)
                            {
                                sPhase = Phase.Meltdown;
                                sPhaseStarted = Time.time;
                                Plugin.Log.LogInfo("[Reactor] MELTDOWN, started as " + ReactorFire.StartMeltdown(em, sCoreEntity) + ".");
                            }
                        }
                    }
                    else if (sBadSince >= 0f && !sDonutDone)
                    {
                        sBadSince = -1f;
                        Plugin.Log.LogInfo($"[Reactor] evened out in time: {BalanceText()}.");
                    }
                    break;
                }
                case Phase.Meltdown:
                    if (!em.Exists(sCoreEntity) || em.HasComponent<ExplodedComponent>(sCoreEntity) || sCore == null)
                        Explode(em, "the reactor above it exploded, and the core went with it");
                    else if (Time.time - sPhaseStarted > MeltdownGiveUp)
                        BeginFade($"the meltdown did not finish within {MeltdownGiveUp:0} s");
                    break;
                case Phase.Fading:
                    sFade = Mathf.Clamp01(1f - (Time.time - sPhaseStarted) / FadeSeconds);
                    if (sFade <= 0f)
                    {
                        sPhase = Phase.Gone;
                        ReactorVisuals.Destroy();
                        ShowHandleLights();         // the signs stay on the consoles, unlit
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
            if (!sDonutDone)                    // the donut flare's destruction is neither a pull nor a choice
            {
                if (sInjectors[i].HandlePulled) JudgePull(i);
                CheckOrder(i);
            }
            if (sInjectors.All(x => !Connected(x.Index))) { BeginFade($"injector {i} was the last one: the reactor is shut down"); return; }
            Plugin.Log.LogInfo($"[Reactor] injector {i} disconnected: {BalanceText()}.");
        }

        // ---- handle timing ------------------------------------------------------------------------------------------
        // Like the game's breakers: an injector's handle is pulled safely only while the core is at the low point of its
        // pulse (its three handle lights all dark). Pulled at any other moment, the plasma flashes back along it and sets
        // you on fire. The moment of the pull is taken from the game's own joint events (any joints removed, or a
        // breakable joint broken), which come on the very frame; the injectors themselves are checked only every
        // quarter second. If no such event came, the pull counts as safe when the window was open at any time since the
        // last check. Cutting the purple pipe instead is not timed, as cutting a breaker's fuse is not.
        static float sCurrentPeriod = 4f;
        static bool sWindowSinceCheck, sWindowSinceCheckNext;

        // The lights' cycle, like a breaker panel's: dark (safe), 1, 2, 3 lit, dark again, over four of the core's beats
        // and counted from the same clock as the pulse (a step changes on the beat, the core's peak). The dark step is a
        // whole beat at level 1 or below and narrows to a third of a beat at level 3; the lit steps share the rest.
        static float LightCycle => (sBeats + 0.5f) % 4f;
        static float DarkBeats => Mathf.Lerp(1f, 1f / 3f,
            Mathf.Clamp01((Imbalance - 1f) / Mathf.Max(0.01f, Plugin.ReactorMeltdownAt.Value - 1f)));

        static int LightStage(int injector = -1)
        {
            float c = injector < 0 ? LightCycle : (LightCycle + LightOffset(injector)) % 4f, dark = DarkBeats;
            if (c < dark) return 0;
            return Mathf.Clamp(1 + (int)((c - dark) / ((4f - dark) / 3f)), 1, 3);
        }
        static bool InSafeWindow(int injector = -1) => LightStage(injector) == 0;

        // The moment the player pulls a handle: its Activate completes (the game's ProgressComplete interaction event, on
        // the press itself; the console lets go of the injector about 5 s later). Judged then, and a miss punished at once.
        // Init comes from just looking at it, so it does not count.
        internal static void OnInteraction(InteractionEvent ev)
        {
            if (ev.InteractableObject == null || ev.Interaction != InteractionEvent.InteractionType.ProgressComplete) return;
            OnSwitchUsed(ev.InteractableObject);
        }

        internal static void OnSwitchUsed(Component used)
        {
            if (!Plugin.ReactorHandleTiming.Value || (sPhase != Phase.Running && sPhase != Phase.Meltdown) || sInjectors[0] == null || sCore == null || used == null) return;
            var at = used.transform.position;
            int best = -1; float bestDistance = 4f;     // within 2 m
            for (int i = 0; i < Count; i++)
            {
                var x = sInjectors[i];
                if (x == null || !x.Connected || !x.HasHandle) continue;
                float d = (sCore.transform.TransformPoint(x.LocalHandle) - at).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            if (best < 0) return;
            var injector = sInjectors[best];
            injector.PressAt = Time.time;
            injector.PressSafe = InSafeWindow(best);
            Plugin.Log.LogInfo($"[Reactor] injector {best} handle pulled with the lights at step {LightStage(best)} " +
                               $"({(injector.PressSafe ? "dark: safe" : "lit: flashback")}).");
            // The outcome lands as the handle finishes turning (it turns by itself after the press), not on the press.
            sPendingOutcome.Add((best, injector.PressSafe, Time.time + OutcomeDelay));
        }

        // A joint went somewhere in the game; if it was one of an injector's (its own parts, what its handle holds, or its
        // handle's console), note the moment for that injector: the first one since the last check. Joints going elsewhere
        // (burning parts fall apart all the time) do not count.
        static float sLastCheckAt;

        internal static void OnJointEvent(StructurePart? part)
        {
            if ((sPhase != Phase.Running && sPhase != Phase.Meltdown) || part == null || sInjectors[0] == null) return;
            var entity = part.EntityBlueprintComponent != null ? part.EntityBlueprintComponent.Entity : Entity.Null;
            for (int i = 0; i < Count; i++)
            {
                var x = sInjectors[i];
                if (x == null || !x.Connected) continue;
                bool mine = x.Parts.Contains(part) || part == x.HandlePart || (entity != Entity.Null && x.Released.Contains(entity));
                if (!mine || x.JointAt >= sLastCheckAt) continue;
                x.JointAt = Time.time;
                x.JointSafe = InSafeWindow(i);
            }
        }

        static void JudgePull(int i)
        {
            if (!Plugin.ReactorHandleTiming.Value || (sPhase != Phase.Running && sPhase != Phase.Meltdown)) return;
            var x = sInjectors[i];
            if (Time.time - x.PressAt < 10f) return;                 // already judged when its handle was pulled
            bool fromEvent = x.JointAt >= sLastCheckAt - LogicSeconds;
            bool safe = fromEvent ? x.JointSafe : sWindowSinceCheck;
            string how = fromEvent ? $"no handle pull seen; timed by its own joint event {Time.time - x.JointAt:0.00} s before the check"
                                   : "no handle pull or joint event of its own: judged by the window since the last check";
            Plugin.Log.LogInfo($"[Reactor] injector {i} let go {(safe ? "in" : "OUTSIDE")} the safe window ({how}).");
            Consequence(i, safe);
        }

        // A pulled handle's outcome: the breaker's "removed" sound if the lights were dark; otherwise a flashback: the
        // failed sound, fire at the handle, and (with PlayerDamage) heat at once and the game's fire.
        // A pulled handle's outcome waits for the handle to finish turning, about 2 s (the press is judged at once).
        const float OutcomeDelay = 2f;
        static readonly List<(int Injector, bool Safe, float At)> sPendingOutcome = new List<(int, bool, float)>();

        static void SettleOutcomes()
        {
            for (int k = sPendingOutcome.Count - 1; k >= 0; k--)
                if (Time.time >= sPendingOutcome[k].At)
                {
                    var (i, safe, _) = sPendingOutcome[k];
                    sPendingOutcome.RemoveAt(k);
                    Consequence(i, safe);
                }
        }

        static void Consequence(int i, bool safe)
        {
            if (safe) { ReactorSound.Play("sfx_powerGenerator_breakerRemoved"); return; }
            ReactorSound.Play("sfx_powerGenerator_breakerRemoved_fail");
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            var handle = sCore != null ? sCore.transform.TransformPoint(sInjectors[i].LocalHandle) : sWorldCenter;
            var prefab = FirePrefab();
            if (prefab != null)
            {
                var burst = Object.Instantiate(prefab, handle, Quaternion.identity);
                burst.name = "TheExpanse_HandleFlashback";
                burst.SetActive(true);
                Object.Destroy(burst, 3f);
            }
            if (!Plugin.ReactorPlayerDamage.Value) return;
            var player = PlayerProfileService.Instance?.Profile?.PlayerEntity ?? Entity.Null;
            if (player != Entity.Null && em.Exists(player) && em.HasComponent<HealthComponent>(player))
            {
                float health = em.GetComponentData<HealthComponent>(player).Max;
                ReactorFire.ScorchPlayer(em, player, 0.05f * health);
                ReactorFire.IgnitePlayer(em, player, 0.05f * health);
            }
        }

        // ---- touch ----------------------------------------------------------------------------------------------------
        // Holding the reactor, an injector or one of their fuel lines, the player hears the core through the suit, the
        // game's touch transfer: its sounds come through louder, and clearly in vacuum.
        static bool sTouching;

        internal static void OnGrab(GrabEvent ev)
        {
            if (ev.State == GrabEvent.GrabState.Release) { if (sTouching) Plugin.Log.LogInfo("[Reactor] let go of the reactor."); sTouching = false; return; }
            if (ev.State != GrabEvent.GrabState.Grab || ev.GrabbedPart == null || sInjectors[0] == null) return;
            var part = ev.GrabbedPart;
            var entity = part.EntityBlueprintComponent != null ? part.EntityBlueprintComponent.Entity : Entity.Null;
            bool reactor = part == sCore || (entity != Entity.Null && entity == sCoreEntity)
                || sInjectors.Any(x => x != null && (x.Parts.Contains(part) || (entity != Entity.Null && x.Line.Contains(entity))))
                || NearHandle(part);
            if (reactor && !sTouching) Plugin.Log.LogInfo($"[Reactor] holding '{part.name}': the core is heard through the suit.");
            sTouching = reactor;
        }

        // Any part of a handle's console (the handle itself or the panel it sits on) within 2 m of an injector's handle.
        static bool NearHandle(StructurePart part)
        {
            if (sCore == null || part.name.IndexOf("AirlockConsole", System.StringComparison.OrdinalIgnoreCase) < 0) return false;
            var at = part.transform.position;
            return sInjectors.Any(x => x != null && x.HasHandle && (sCore.transform.TransformPoint(x.LocalHandle) - at).sqrMagnitude < 4f);
        }

        static readonly bool[] sConnectedNow = new bool[Count];
        static void DrawScreens()
        {
            if (!TerminalScreens.Available || sCore == null) return;
            var center = BallCenter;
            for (int i = 0; i < Count; i++)
            {
                sConnectedNow[i] = sInjectors[i] != null && Connected(i);
                if (sInjectors[i] != null) TerminalScreens.SetInjector(i, sCore.transform.TransformPoint(sInjectors[i].LocalSlot) - center);
            }
            TerminalScreens.Tick(new TerminalScreens.State
            {
                Mode = CurrentMode, Connected = sConnectedNow, Order = Order, Owed = Owed, Mistake = MistakeInjector,
                MistakeAge = Time.time - MistakeAt, Live = sPhase == Phase.Running || sPhase == Phase.Meltdown,
                Level = ShownLevel(sShownImbalance), NextStep = NextStepIn(), Beats = sBeats,
                StepSeconds = Imbalance >= Plugin.ReactorMeltdownAt.Value ? Mathf.Max(1f, Plugin.ReactorMeltdownSeconds.Value) : EscalationDelay(0f),
            });
        }

        static void ShowHandleLights()
        {
            bool burning = sPhase == Phase.Running || sPhase == Phase.Meltdown;
            for (int i = 0; i < Count; i++)
            {
                int stage = burning ? LightStage(i) : 0;
                var x = sInjectors[i];
                bool live = burning && x != null && Connected(i);
                // Live panels: on the console, or floating above the handle without one. Dead ones (already disconnected,
                // or the plasma out): only on a console that is still there.
                bool visible = Plugin.ReactorHandleTiming.Value && x != null && x.HasHandle
                               && (live ? sCore != null : x.HandlePart != null);
                if (!visible) { HandleLights.Show(i, false, null, default, default, default, default, 0); continue; }
                if (sCore == null)
                {
                    HandleLights.Show(i, true, x!.HandlePart!.transform, default, Vector3.up, Vector3.right, Vector3.forward, 0, false);
                    continue;
                }
                var t = sCore.transform;
                var handle = t.TransformPoint(x!.LocalHandle);
                var up = -t.TransformDirection(sLocalDown).normalized;
                var radial = Vector3.ProjectOnPlane(handle - t.TransformPoint(sLocalCenter), up);
                var around = radial.sqrMagnitude > 1e-4f ? Vector3.Cross(up, radial.normalized) : Vector3.right;
                var outward = radial.sqrMagnitude > 1e-4f ? radial.normalized : Vector3.forward;
                HandleLights.Show(i, true, x.HandlePart != null ? x.HandlePart.transform : null, handle, up, around, outward, stage, live);
            }
        }

        // ---- the donut flare --------------------------------------------------------------------------------------------
        // Level 3 held for MeltdownSeconds: a flat ring of plasma bursts out in the plane of the injectors. With Ship
        // Damage on it destroys the eight injectors (not the big nozzle above, the fuel pipes or the reactor); with Player
        // Damage on it scorches you if you are in its plane (heat, no catching fire). Then, with Core Meltdown on, the
        // game's own meltdown follows (losing the injectors does not start it by itself).
        static bool sDonutDone;

        static void Donut(EntityManager em)
        {
            sDonutDone = true;
            var center = BallCenter;
            var normal = sCore != null ? -sCore.transform.TransformDirection(sLocalDown).normalized : Vector3.up;
            float reach = 0f;
            foreach (var x in sInjectors)
                if (x != null && sCore != null) reach = Mathf.Max(reach, (sCore.transform.TransformPoint(x.LocalSlot) - center).magnitude);
            reach += 1.5f;
            DonutFlare.Fire(center, normal, BallDiameter() * 0.5f, reach * 1.3f);

            int destroyed = 0;
            var prefab = FirePrefab();
            foreach (var x in sInjectors)
            {
                if (x == null) continue;
                if (prefab != null && sCore != null)
                {
                    var burst = Object.Instantiate(prefab, sCore.transform.TransformPoint(x.LocalSlot), Quaternion.identity);
                    burst.name = "TheExpanse_DonutBurst";
                    burst.SetActive(true);
                    Object.Destroy(burst, 4f);
                }
                if (DamageScale <= 0f) continue;
                foreach (var part in x.Parts)
                    if (part != null) { ReactorFire.HeatPart(part, 1000f); destroyed++; }
            }

            string player = "";
            var playerEntity = PlayerProfileService.Instance?.Profile?.PlayerEntity ?? Entity.Null;
            if (Plugin.ReactorPlayerDamage.Value && playerEntity != Entity.Null && em.Exists(playerEntity) && em.HasComponent<HealthComponent>(playerEntity)
                && GhostNodes.TryGetPosition(em, playerEntity, out var at))
            {
                var offset = at - center;
                float height = Mathf.Abs(Vector3.Dot(offset, normal));
                float across = Vector3.ProjectOnPlane(offset, normal).magnitude;
                if (height < 2.5f && across < reach * 1.3f)
                {
                    ReactorFire.ScorchPlayer(em, playerEntity, 0.25f * em.GetComponentData<HealthComponent>(playerEntity).Max);
                    player = "; it caught you";
                }
            }
            Plugin.Log.LogInfo($"[Reactor] THE DONUT FLARE: {(DamageScale > 0f ? $"{destroyed} injector part(s) destroyed" : "Ship Damage is off: nothing destroyed")}{player}.");
        }

        // The reactor pulled while the plasma burns: a real explosion at the ball, the game's own. A short-lived entity
        // at the ball carries a copy of the reactor core's explosive settings (what it would use in a meltdown), with
        // its reach doubled, and is marked as exploded; the game's ExplosionSystem then damages, shatters and pushes
        // the parts in reach, hurts and knocks back the player and spawns the explosion effect, just as for a reactor
        // that blows. The core itself is not touched. If the core has no explosive settings, the game's reactor core
        // explosion effect plays instead, and the player and parts nearby are set on fire. The plasma is gone within
        // a second either way.
        const string ExplosionGuid = "c620190dd56b9b64badf8501cd8939ef";    // FX_Explosion_ReactorCore
        const float ExplosionReach = 2f;                                   // times the reactor's own, at Damage 100
        const float ExplosionPlayerRadius = 12f, ExplosionPartRadius = 10f; // the fallback's
        static Entity sBlast = Entity.Null;

        // Damage (0..200 %): how hard the core hits the ship. 100 is the standard; 0 burns no parts, never melts down
        // and detonates as nothing; 200 burns twice as hard and detonates with twice the reach.
        internal static float DamageScale => Mathf.Clamp(Plugin.ReactorShipDamage.Value, 0f, 200f) / 100f;
        static bool MeltdownAllowed => Plugin.ReactorMeltdown.Value && DamageScale > 0f;
        static float Reach => ExplosionReach * DamageScale;

        // The reactor's explosive settings, copied while it is still whole: when it explodes it may be gone by the time
        // the core detonates.
        static bool sBlastCached, sHasDamage, sHasForce, sHasShatter;
        static ExplosiveComponent sExplosive;
        static ExplosiveDamageSettings sDamage;
        static ExplosiveForceSettings sForce;
        static ExplosiveShatterSettings sShatter;

        static void CacheBlast(EntityManager em)
        {
            if (sBlastCached || !em.Exists(sCoreEntity) || !em.HasComponent<ExplosiveComponent>(sCoreEntity)) return;
            try
            {
                sExplosive = em.GetComponentData<ExplosiveComponent>(sCoreEntity);
                if (sHasDamage = em.HasComponent<ExplosiveDamageSettings>(sCoreEntity)) sDamage = em.GetComponentData<ExplosiveDamageSettings>(sCoreEntity);
                if (sHasForce = em.HasComponent<ExplosiveForceSettings>(sCoreEntity)) sForce = em.GetComponentData<ExplosiveForceSettings>(sCoreEntity);
                if (sHasShatter = em.HasComponent<ExplosiveShatterSettings>(sCoreEntity)) sShatter = em.GetComponentData<ExplosiveShatterSettings>(sCoreEntity);
                sBlastCached = true;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Reactor] could not read the reactor's explosive settings: " + ex.Message); sBlastCached = true; }
        }

        static void Explode(EntityManager em, string why)
        {
            var at = BallCenter;
            if (DamageScale <= 0f) Plugin.Log.LogInfo($"[Reactor] {why}; Ship Damage is off, so the core goes out without a blast.");
            else
            {
                Plugin.Log.LogInfo($"[Reactor] CORE DETONATION: {why}.");
                if (!RealExplosion(em, at)) FakeExplosion(em, at);
            }
            BeginFade(why);
            sPhaseStarted = Time.time - FadeSeconds * 0.8f;     // gone within a second
        }

        static bool RealExplosion(EntityManager em, Vector3 at)
        {
            CacheBlast(em);
            if (!sBlastCached || sExplosive.ElementalBounds <= 0f && !sHasDamage)
            {
                Plugin.Log.LogInfo("[Reactor] the reactor has no explosive settings: a fire-only explosion instead.");
                return false;
            }
            try
            {
                if (sBlast != Entity.Null && em.Exists(sBlast)) em.DestroyEntity(sBlast);
                var explosive = sExplosive;
                explosive.ElementalBounds *= Reach;
                explosive.MaxFXScale *= Reach;
                var blast = em.CreateEntity();
                em.AddComponentData(blast, explosive);
                string what = $"reach {explosive.ElementalBounds:0.#} m, strength {explosive.ElementalStrength:0.#}";
                if (sHasDamage)
                {
                    var damage = sDamage;
                    damage.PlayerDamageRadius *= Reach;
                    if (!Plugin.ReactorPlayerDamage.Value) { damage.PlayerDamageRadius = 0f; damage.PlayerExplosionDamage = 0f; }
                    em.AddComponentData(blast, damage);
                    what += $", part damage {damage.ExplosionDamage:0.#}, player damage {damage.PlayerExplosionDamage:0.#} within {damage.PlayerDamageRadius:0.#} m";
                }
                if (sHasForce)
                {
                    var force = sForce;
                    force.PlayerForceRadius *= Reach;
                    em.AddComponentData(blast, force);
                    what += $", force {force.ExplosionForce:0.#} (player {force.PlayerExplosionForce:0.#} within {force.PlayerForceRadius:0.#} m)";
                }
                if (sHasShatter)
                {
                    em.AddComponentData(blast, sShatter);
                    what += ", shatters";
                }
                em.AddComponentData(blast, new Unity.Transforms.LocalToWorld
                {
                    Value = Unity.Mathematics.float4x4.TRS(new Unity.Mathematics.float3(at.x, at.y, at.z), Unity.Mathematics.quaternion.identity, new Unity.Mathematics.float3(1f, 1f, 1f))
                });
                em.AddComponentData(blast, new ExplodedComponent { Time = (float)World.DefaultGameObjectInjectionWorld.Time.ElapsedTime });
                sBlast = blast;
                Plugin.Log.LogInfo($"[Reactor] a real explosion at the core, from the reactor's own settings, reach x{Reach:0.##}: {what}.");
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
                    boom.transform.localScale = Vector3.one * Reach;
                    boom.SetActive(true);
                    foreach (var effect in boom.GetComponentsInChildren<UnityEngine.VFX.VisualEffect>(true))
                    { effect.enabled = true; effect.Reinit(); effect.Play(); effect.SendEvent("Start"); effect.SendEvent("OnPlay"); }
                    foreach (var system in boom.GetComponentsInChildren<ParticleSystem>(true)) system.Play(true);
                    Object.Destroy(boom, 15f);
                };
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Reactor] the reactor core explosion effect could not be loaded: " + ex.Message); }

            var player = PlayerProfileService.Instance?.Profile?.PlayerEntity ?? Entity.Null;
            if (Plugin.ReactorPlayerDamage.Value && player != Entity.Null && em.Exists(player) && em.HasComponent<HealthComponent>(player) && GhostNodes.TryGetPosition(em, player, out var where)
                && (where - at).sqrMagnitude < ExplosionPlayerRadius * ExplosionPlayerRadius * DamageScale * DamageScale)
                ReactorFire.IgnitePlayer(em, player, 0.1f * em.GetComponentData<HealthComponent>(player).Max);
            BurnAround(em, at, ExplosionPartRadius * DamageScale, null);
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
            // (not while the ship is flown: then the core really does move that far between frames)
            if (sTracked && !FlightModel.UnderWay && ((center - sWorldCenter).sqrMagnitude > JumpMeters * JumpMeters || Quaternion.Angle(rotation, sLastCoreRotation) > JumpDegrees))
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
            float period = PulsePeriod(m, feed);
            sCurrentPeriod = period;
            float depth = 0.5f;                            // 1x..2x
            float pulse = 1f + depth * (1f - Mathf.Cos(PulsePhase(period)));
            float grow = 1f + 0.1f * m + (sPhase == Phase.Meltdown ? 0.5f : 0f);
            return size * pulse * grow;
        }

        // How long a pulse takes: a steady PulseSeconds whatever the balance (the level shows in the core's color and
        // brightness and the helmet beep instead; fewer injectors make the core smaller, not slower). The beat is left
        // free for the shutdown modes to use.
        static float PulsePeriod(float m, float feed) => Mathf.Max(0.2f, Plugin.ReactorPulseSeconds.Value);

        // The pulse's phase advances at the current rate, so a change of rate speeds it up or slows it down smoothly.
        // sBeats counts the core's beats (the clock the handle lights read).
        static float sPulsePhase, sPulseAt = -1f, sBeats;
        static float PulsePhase(float period)
        {
            if (sPulseAt != Time.time)
            {
                if (sPulseAt >= 0f)
                {
                    float beats = (Time.time - sPulseAt) / period;
                    sBeats = (sBeats + beats) % 4000f;
                    sPulsePhase = (sPulsePhase + beats * Mathf.PI * 2f) % (Mathf.PI * 2f);
                }
                sPulseAt = Time.time;
            }
            return sPulsePhase;
        }

        // How much the injectors still feed the plasma: 1 with all eight connected, falling as they are disconnected.
        // Eased like the imbalance, so the ball settles down gradually.
        static float sShownFeed = 1f;

        // The color step: 0 in balance, 1 safe but off balance, 2 unstable, 3 badly unbalanced.
        static int ShownLevel(float m) => m < 0.05f ? 0 : m <= Plugin.ReactorUnstableAt.Value ? 1 : m < Plugin.ReactorMeltdownAt.Value ? 2 : 3;

        static void Draw()
        {
            Track();
            float m = Imbalance;
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            sShownImbalance = Mathf.MoveTowards(sShownImbalance, m, dt * 1.5f);   // the new rate is reached within a second
            sShownFeed = Mathf.MoveTowards(sShownFeed, sInjectors.Count(x => x != null && Connected(x.Index)) / (float)Count, dt * 0.05f);
            for (int i = 0; i < Count; i++)
            {
                var x = sInjectors[i];
                sFeeds[i] = new ReactorFeeds.Feed { Connected = Connected(i) && sPhase != Phase.Fading, LocalNozzle = x.LocalNozzle,
                    LocalSlot = x.LocalSlot, LocalHandle = x.LocalHandle, LocalDirection = x.LocalDirection };
            }
            ReactorFeeds.Show(sCore != null ? sCore.transform : null, sLocalCenter, sLocalDown, sLocalCoreTip, sFeeds, EscalatedLocalImbalance(),
                              sPhase == Phase.Fading ? sFade : 1f, sPulsePhase / (Mathf.PI * 2f));
            ReactorVisuals.Show(BallCenter, BallDiameter(), ShownLevel(sShownImbalance), Unstable && sPhase != Phase.Fading,
                                sPhase == Phase.Fading ? sFade : 1f);
            ReactorSound.Show(BallCenter, sPulsePhase / (Mathf.PI * 2f), sShownImbalance, Unstable && sPhase != Phase.Fading,
                              sPhase == Phase.Fading ? sFade : 1f, NextStepIn(), sTouching);
            SettleOutcomes();
            DrawScreens();
            if (InSafeWindow()) sWindowSinceCheckNext = true;
            ShowHandleLights();
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
            if (Plugin.ReactorPlayerDamage.Value && player != Entity.Null && em.Exists(player) && em.HasComponent<HealthComponent>(player) && GhostNodes.TryGetPosition(em, player, out var at))
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

                // Touching the ball sets the player on fire (the flares and the fire cloud are harmless).
                if (fromSurface < Mathf.Max(0f, Plugin.ReactorHeatRadius.Value))
                    ReactorFire.IgnitePlayer(em, player, 0.02f * health);
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
                    ReactorFire.HeatPart(part, Mathf.Max(0f, Plugin.ReactorPartDamage.Value) * DamageScale);
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
            if (best && Owed >= 0 && connected.Contains(Owed)) pick = Owed;                                         // Memorize, Resonance: the one owed
            if (worst && Owed >= 0 && pick == Owed && connected.Count > 1) pick = connected.First(i => i != Owed);
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
