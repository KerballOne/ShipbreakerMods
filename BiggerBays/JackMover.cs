using System;
using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace BiggerBays
{
    // With ship centering on, measures the loaded ship and moves the bay's jacks clear of it.
    //
    // Stock bay layout this is built around (world space, from PartInfoLogger's bay_geometry.csv):
    //   ship           spawns at (-28.6, -3.5, -8.9) and runs along +Z
    //   Master Jack    root PRF_MasterJack_NoSpawner at (-6.3, 6.0, 104.0), i.e. past the ship's +Z end; its box starts at z = 90
    //   light jacks    five roots PRF_Prop_JaxLight_SingleObject above the ship: four at y = 23.1 (undersides at y = 16.8),
    //                  one at y = 30.9
    // So a ship that is too long runs into the Master Jack along +Z, and one that is too tall runs into the light jacks
    // along +Y. Those are the two directions things are moved in.
    internal static class JackMover
    {
        const string MasterJackName = "MasterJack";
        const string LightJackPrefix = "PRF_Prop_JaxLight";
        const string MasterJackLightsPath = "PRF_BayAnimatedEmmissiveLighting/HexEmissiveLights_MasterJack";

        // Where each moved object stood before this mod touched it, so a repeat pass starts from the stock position.
        static readonly Dictionary<int, Vector3> sOriginal = new Dictionary<int, Vector3>();
        static readonly HashSet<int> sPlayersMoved = new HashSet<int>();
        static readonly HashSet<int> sPlayersBaseMoved = new HashSet<int>();

        static string V(Vector3 v) => $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";
        static string B(Bounds b) => $"min {V(b.min)} max {V(b.max)}";

        // Jack rigidbodies whose collisions are switched off while the ship spawns.
        static readonly List<Rigidbody> sGhosted = new List<Rigidbody>();

        // An oversized ship spawns straight through the jacks, and they can only be moved once it has been measured. The
        // jacks are kinematic, so any physics step in between would shove the overlapping ship parts out of them. Take the
        // jacks out of collision until they are in their final place.
        internal static void OnShipSpawnStarting()
        {
            RestoreCollisions();
            FindJacks(out var masterJack, out var lightJacks);
            if (masterJack != null) lightJacks.Add(masterJack);
            foreach (var t in lightJacks)
            {
                var rb = t.GetComponent<Rigidbody>();
                if (rb == null || !rb.detectCollisions) continue;
                rb.detectCollisions = false;
                sGhosted.Add(rb);
            }
        }

        internal static void RestoreCollisions()
        {
            foreach (var rb in sGhosted) if (rb != null) rb.detectCollisions = true;
            sGhosted.Clear();
        }

        internal static void OnShipLoaded()
        {
            ClearRedraws();
            if (!MeasureShip(out var ship, out int parts)) return;      // no ship in this level (menu, hab)

            // Where the ship actually ended up, against where the centering aimed (it worked from an estimate of the box).
            var size = ship.size;
            Plugin.Log.LogInfo($"[JackMover] ship: {parts} parts, size {size.x:0.0} x {size.y:0.0} x {size.z:0.0} m, {B(ship)}; " +
                               $"off its target by {ship.center.x - ShipCentering.BayCenterX - Plugin.AcrossOffset.Value:0.0} m across and {ship.center.y - Plugin.BayCenterY.Value:0.0} m in height, " +
                               $"rear end {ship.min.z - ShipCentering.BackWallZ:0.0} m from the back wall.");

            FindJacks(out var masterJack, out var lightJacks);
            if (masterJack == null) Plugin.Log.LogWarning("[JackMover] Master Jack not found in this level.");
            if (lightJacks.Count != 5) Plugin.Log.LogWarning($"[JackMover] expected 5 light jacks, found {lightJacks.Count}.");

            float clearance = Plugin.Clearance.Value;

            // A ship posed entirely above a jack does not need that jack moved (a saved ship that was never centered).
            // Master Jack: along +Z until its near face is `clearance` past the ship's far end.
            if (masterJack != null && ship.min.y >= BoundsOf(masterJack, sBaseShift).max.y)
            {
                ResetToStock(masterJack);
                Plugin.Log.LogInfo("[JackMover] ship is above the Master Jack; left where it is.");
            }
            else if (masterJack != null)
            {
                var stock = ResetToStock(masterJack);
                float dz = Mathf.Max(0f, ship.max.z + clearance - BoundsOf(masterJack, sBaseShift).min.z);
                var delta = new Vector3(0f, 0f, dz);
                Move(masterJack, delta, "Master Jack");
                if (dz > 0f)
                {
                    var lights = GameObject.Find(MasterJackLightsPath);
                    if (lights != null) { ResetToStock(lights.transform); Move(lights.transform, delta, "Master Jack hex lights"); }
                    if (Plugin.MovePlayerSpawn.Value) MovePlayer(stock, delta, sPlayersMoved);
                }
            }

            // Light jacks: all five rise by the same amount (keeping their formation), enough for the lowest to clear the ship's top.
            if (lightJacks.Count > 0)
            {
                foreach (var j in lightJacks) ResetToStock(j);
                if (ship.min.y >= lightJacks.Max(j => BoundsOf(j).max.y))
                    Plugin.Log.LogInfo("[JackMover] ship is above the light jacks; left where they are.");
                else
                {
                    float dy = Mathf.Max(0f, ship.max.y + clearance - lightJacks.Min(j => BoundsOf(j).min.y));
                    for (int i = 0; i < lightJacks.Count; i++) Move(lightJacks[i], new Vector3(0f, dy, 0f), $"Light jack {i + 1}");
                }
            }

            Physics.SyncTransforms();
        }

        // ---- ship ---------------------------------------------------------------------------------------------------
        // Overall world-space box of every ship part's mesh renderers. Parts with no renderer count by their position.
        internal static bool MeasureShip(out Bounds ship, out int parts)
        {
            ship = default; parts = 0;
            bool any = false;

            // Bay props such as the barge are built from structure parts too; only parts tagged as ship members count.
            var world = World.DefaultGameObjectInjectionWorld;
            var all = new List<StructurePart>();
            var members = new List<StructurePart>();
            foreach (var sp in Resources.FindObjectsOfTypeAll<StructurePart>())
            {
                if (sp == null || !sp.gameObject.scene.IsValid() || !sp.gameObject.activeInHierarchy) continue;
                all.Add(sp);
                var e = sp.Entity;
                if (world != null && e != Entity.Null && world.EntityManager.Exists(e) && world.EntityManager.HasComponent<ShipMemberComponent>(e))
                    members.Add(sp);
            }
            if (members.Count == 0 && all.Count > 0)
                Plugin.Log.LogWarning($"[JackMover] none of the {all.Count} structure parts is tagged as a ship member; measuring all of them.");

            foreach (var sp in members.Count > 0 ? members : all)
            {
                parts++;
                bool hadRenderer = false;
                foreach (var r in sp.GetComponentsInChildren<MeshRenderer>())
                {
                    var b = r.bounds;
                    if (b.size == Vector3.zero) continue;
                    hadRenderer = true;
                    Add(ref ship, ref any, b);
                }
                if (!hadRenderer) Add(ref ship, ref any, new Bounds(sp.transform.position, Vector3.zero));
            }
            return any;
        }

        static void Add(ref Bounds total, ref bool any, Bounds b)
        {
            if (any) total.Encapsulate(b); else { total = b; any = true; }
        }

        // ---- jacks --------------------------------------------------------------------------------------------------
        static void FindJacks(out Transform? masterJack, out List<Transform> lightJacks)
        {
            masterJack = null;
            lightJacks = new List<Transform>();

            // The hab door script sits inside the Master Jack; its topmost "MasterJack" ancestor is the whole structure.
            foreach (var entry in Resources.FindObjectsOfTypeAll<ProcessMasterJackEntry>())
            {
                if (entry == null || !entry.gameObject.scene.IsValid()) continue;
                for (var t = entry.transform; t != null; t = t.parent)
                    if (t.name.IndexOf(MasterJackName, StringComparison.OrdinalIgnoreCase) >= 0) masterJack = t;
                if (masterJack != null) break;
            }

            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                string n = t.name;
                if (n.StartsWith(LightJackPrefix, StringComparison.Ordinal))
                {
                    if (t.parent == null || !t.parent.name.StartsWith(LightJackPrefix, StringComparison.Ordinal)) lightJacks.Add(t);
                }
                else if (masterJack == null && t.parent == null && n.StartsWith("PRF_" + MasterJackName, StringComparison.Ordinal))
                    masterJack = t;
            }
            // Stable order for the log: back to front, then left to right.
            lightJacks.Sort((a, b) => a.position.z != b.position.z ? a.position.z.CompareTo(b.position.z) : a.position.x.CompareTo(b.position.x));
        }

        // Statically batched renderers keep reporting the box they were built at; `batchedShift` is how far the object has
        // been moved since (the longer-bay shift), so their boxes can be brought along.
        static Bounds BoundsOf(Transform root, Vector3 batchedShift = default)
        {
            Bounds total = new Bounds(root.position, Vector3.zero);
            bool any = false;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                var b = r.bounds;
                if (b.size == Vector3.zero) continue;
                if (r.isPartOfStaticBatch) b.center += batchedShift;
                if (any) total.Encapsulate(b); else { total = b; any = true; }
            }
            return total;
        }

        static Vector3 ResetToStock(Transform t)
        {
            int id = t.GetInstanceID();
            if (sOriginal.TryGetValue(id, out var stock)) SetPosition(t, stock);
            else sOriginal[id] = stock = t.position;
            return stock;
        }

        static void SetPosition(Transform t, Vector3 pos)
        {
            t.position = pos;
            var rb = t.GetComponent<Rigidbody>();
            if (rb != null) rb.position = pos;
        }

        static void Move(Transform t, Vector3 delta, string label, bool remember = true)
        {
            var from = t.position;
            if (delta == Vector3.zero) return;
            // Statically batched renderers are baked into a combined mesh and do not follow their transform; they are
            // redrawn shifted instead.
            var renderers = t.GetComponentsInChildren<MeshRenderer>(true);
            int batched = 0, redrawn = 0;
            foreach (var r in renderers)
            {
                if (!r.isPartOfStaticBatch) continue;
                batched++;
                if (!BatchedRedraw.Shift(r, delta)) continue;
                redrawn++;
                if (remember) sShifted.Add(new KeyValuePair<MeshRenderer, Vector3>(r, delta));
            }

            var to = from + delta;
            SetPosition(t, to);
            Plugin.Log.LogInfo($"[JackMover] {label} '{t.name}' moved {V(from)} -> {V(to)} (delta {V(delta)})" +
                               (batched > 0 ? $"; {redrawn}/{batched} statically batched renderer(s) redrawn at the new position" : ""));
        }

        // ---- statically batched renderers ---------------------------------------------------------------------------
        // Batched renderers do not follow their transform; BatchedRedraw draws them shifted. What this class has shifted is
        // remembered so a repeat pass can take it back without disturbing shifts made by the bay reshaping.
        static readonly List<KeyValuePair<MeshRenderer, Vector3>> sShifted = new List<KeyValuePair<MeshRenderer, Vector3>>();

        static void ClearRedraws()
        {
            foreach (var kv in sShifted) if (kv.Key != null) BatchedRedraw.Shift(kv.Key, -kv.Value);
            sShifted.Clear();
        }

        // Renderers of a level that has been unloaded are gone; forget them.
        internal static void OnLevelLoadStart()
        {
            sShifted.RemoveAll(kv => kv.Key == null);
            sBaseShift = Vector3.zero;
        }

        // ---- lengthened bay -----------------------------------------------------------------------------------------
        // When the bay is made longer, the Master Jack, its hex lights and the player's start go out with the far end.
        // This happens before the ship is measured, so the clearance pass above treats the result as the stock position.
        static Vector3 sBaseShift;

        internal static void BaseShift(Vector3 delta)
        {
            if (delta == Vector3.zero) return;
            FindJacks(out var masterJack, out _);
            if (masterJack == null) { Plugin.Log.LogWarning("[JackMover] Master Jack not found; it stays where it is in the longer bay."); return; }
            var stock = masterJack.position;
            sBaseShift = delta;
            Move(masterJack, delta, "Master Jack (longer bay)", remember: false);
            var lights = GameObject.Find(MasterJackLightsPath);
            if (lights != null) Move(lights.transform, delta, "Master Jack hex lights (longer bay)", remember: false);
            if (Plugin.MovePlayerSpawn.Value) MovePlayer(stock, delta, sPlayersBaseMoved);
        }

        // ---- player -------------------------------------------------------------------------------------------------
        // The player object is placed in the scene at the Master Jack's door and respawns there; keep it at the door.
        static void MovePlayer(Vector3 masterJackStock, Vector3 delta, HashSet<int> done)
        {
            foreach (var player in Resources.FindObjectsOfTypeAll<Player>())
            {
                if (player == null || !player.gameObject.scene.IsValid()) continue;
                var t = player.transform;
                var from = t.position;
                if (!done.Add(player.GetInstanceID())) continue;
                if ((from - masterJackStock).magnitude > 30f)
                {
                    Plugin.Log.LogInfo($"[JackMover] player at {V(from)} is not at the Master Jack's stock position; start point left alone.");
                    continue;
                }
                var to = from + delta;
                SetPosition(t, to);
                Traverse.Create(player).Field("mSpawnedPosition").SetValue(to);

                // The respawn point is copied onto the player's entity when that is created; fix it up if that already happened.
                bool entityUpdated = false;
                var world = World.DefaultGameObjectInjectionWorld;
                var entity = player.Entity;
                if (world != null && entity != Entity.Null && world.EntityManager.Exists(entity) && world.EntityManager.HasComponent<RespawnLocation>(entity))
                {
                    var loc = world.EntityManager.GetComponentData<RespawnLocation>(entity);
                    loc.Position = to;
                    world.EntityManager.SetComponentData(entity, loc);
                    entityUpdated = true;
                }
                Plugin.Log.LogInfo($"[JackMover] player start moved {V(from)} -> {V(to)} (respawn entity {(entityUpdated ? "updated" : "not created yet, will pick up the new position")}).");
            }
        }
    }
}
