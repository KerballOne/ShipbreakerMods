using System.Collections.Generic;
using System.Text.RegularExpressions;
using BBI.Unity.Game;
using Unity.Entities;
using UnityEngine;

namespace TheExpanse
{
    // Collectibles from wrecked stations: when a crate, the bay's frame (or a piece of it) or the stripped ship breaks,
    // there is a LootChance it spills 1-LootMax collectibles (the barge and the jacks spill nothing). Each is a credit
    // drive twice as often as any one other kind.
    //   the collectibles: the game's own, as the ship has them (credit and data drives, the bunny, truck parts, repair
    //            kits, oxygen, thruster fuel ...), found by name on the ship when it is first flown. Each loose one
    //            (its own body, not built into the ship) is a pattern;
    //   spilling: with LootCopies on (default), a copy of the pattern is made (the object with its game entity, as the
    //            game makes objects at run time): no limit. With it off, the ship's own loose ones are moved out instead
    //            (one each, so the ship's count is the limit). They drift out of the wreck at 0.2-0.8 m/s;
    //   finding: a small green marker with its distance on the HUD for each one within 3 km.
    internal static class StationLoot
    {
        sealed class Kind
        {
            internal string Label = "";
            internal readonly List<Transform> Patterns = new List<Transform>();      // the loose ones' bodies (their roots)
            internal float Weight = 1f;
        }

        internal sealed class Item
        {
            internal Transform T = null!;              // the holder (its body)
            internal StructurePart? Part;              // the collectible itself: gone or taken -> no longer counted
            internal string Label = "";
            internal Vector3 Position => Part != null ? Part.transform.position : T.position;
        }

        static readonly Regex Collectible = new Regex(
            @"CreditDrive|DataDrive|Data_Drive|Bunny|Plush|TruckPart|SpaceTruck|MedKit|Med_Kit|RepairKit|Repair_Kit|Oxygen_Can|Collectible_Oxygen|ThrusterFuel|Thruster_Fuel",
            RegexOptions.IgnoreCase);
        static readonly Dictionary<string, Kind> sKinds = new Dictionary<string, Kind>();
        static readonly List<Item> sItems = new List<Item>();
        static readonly HashSet<Transform> sSeenRoots = new HashSet<Transform>();
        static bool sLoggedCopy;

        internal static IReadOnlyList<Item> Items => sItems;
        internal static bool Any => sItems.Count > 0;

        // the HUD's prompt and message (not used with the game's own collectibles: they are taken the game's way)
        internal static string NearText => "";
        internal static string Toast => "";
        internal static float ToastUntil => 0f;

        internal static void Reset()
        {
            sKinds.Clear();
            sItems.Clear();
            sSeenRoots.Clear();
            sLoggedCopy = false;
        }

        // ---- the patterns, from WeaponMounts' scan of the ship's parts ---------------------------------------

        internal static void NoteShipPart(StructurePart part)
        {
            if (part == null || !Collectible.IsMatch(part.name)) return;
            if (part.name.Contains("PowerCell") || part.name.Contains("Coolant") || part.name.Contains("PlasmaFuel")) return;
            // loose: its body holds no other ship part (built into the ship, it would take the ship with it)
            var body = part.GetComponentInParent<Rigidbody>();
            if (body == null || !sSeenRoots.Add(body.transform)) return;
            var onBody = body.GetComponentsInChildren<StructurePart>(true);
            if (onBody.Length > 4) return;
            // every part on the body a collectible: one sitting on a prop (a drive on the coffee maker) shares the
            // prop's body, and copying it would copy the prop too
            foreach (var other in onBody)
                if (!Collectible.IsMatch(other.name))
                {
                    Plugin.Log.LogInfo($"[Stations] loot: '{part.name}' skipped, its body '{body.name}' also holds '{other.name}'.");
                    return;
                }
            Plugin.Log.LogInfo($"[Stations] loot pattern: '{body.name}' ({part.name}).");
            string label = Label(part.name);
            if (!sKinds.TryGetValue(label, out var kind))
                sKinds[label] = kind = new Kind { Label = label, Weight = label.StartsWith("CREDIT DRIVE") ? 2f : 1f };
            kind.Patterns.Add(body.transform);
        }

        internal static void ShipScanned()
        {
            var parts = new List<string>();
            foreach (var k in sKinds.Values) parts.Add($"{k.Label} x{k.Patterns.Count}{(k.Weight > 1f ? " (x2 odds)" : "")}");
            Plugin.Log.LogInfo(sKinds.Count == 0
                ? "[Stations] loot: no loose collectibles found on this ship; wrecks spill nothing."
                : $"[Stations] loot: {sKinds.Count} kind(s) of collectible on the ship: {string.Join(", ", parts)}; " +
                  (Plugin.StationLootCopies.Value ? "copies are spilled." : "the ship's own are moved out (each once)."));
        }

        static string Label(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("credit")) return "CREDIT DRIVE";
            if (n.Contains("data")) return "DATA DRIVE";
            if (n.Contains("bunny") || n.Contains("plush")) return "BUNNY";
            if (n.Contains("truck")) return "TRUCK PART";
            if (n.Contains("med") || n.Contains("repair")) return "REPAIR KIT";
            if (n.Contains("oxygen")) return "OXYGEN";
            if (n.Contains("thruster")) return "THRUSTER FUEL";
            return name.ToUpperInvariant();
        }

        // ---- spilling -------------------------------------------------------------------------------------------

        // Which destroyed station pieces can spill loot: crates, the bay's frame and its pieces, the stripped ship.
        internal static bool Drops(string pieceName) =>
            pieceName.Contains("Crate") || pieceName.StartsWith("Narrative_Beat_Ship")
            || (pieceName.StartsWith("SM_Bay_GenericBackground_V01") && !pieceName.Contains("Barge") && !pieceName.Contains("Jax"));

        internal static void OnBroken(string pieceName, Vector3 at, Vector3 away)
        {
            if (!Plugin.StationLoot.Value || !Drops(pieceName) || sKinds.Count == 0) return;
            if (Random.value >= Mathf.Clamp01(Plugin.StationLootChance.Value)) return;
            int count = Random.Range(1, Mathf.Max(1, Plugin.StationLootMax.Value) + 1);
            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var kind = Pick();
                if (kind == null) break;
                if (Spill(kind, at + Random.insideUnitSphere * 3f, (away + Random.insideUnitSphere * 0.5f).normalized)) names.Add(kind.Label);
            }
            if (names.Count > 0) Plugin.Log.LogInfo($"[Stations] '{pieceName}' spilled {names.Count} at {at}: {string.Join(", ", names)}.");
        }

        static Kind? Pick()
        {
            var live = new List<Kind>();
            float total = 0f;
            foreach (var k in sKinds.Values)
            {
                k.Patterns.RemoveAll(p => p == null);
                if (k.Patterns.Count == 0) continue;
                live.Add(k); total += k.Weight;
            }
            float r = Random.value * total;
            foreach (var k in live) { r -= k.Weight; if (r <= 0f) return k; }
            return live.Count > 0 ? live[live.Count - 1] : null;
        }

        static bool Spill(Kind kind, Vector3 at, Vector3 away)
        {
            Transform? obj;
            if (Plugin.StationLootCopies.Value)
            {
                var pattern = kind.Patterns[Random.Range(0, kind.Patterns.Count)];
                obj = Copy(pattern);
            }
            else
            {
                // the ship's own: used up as it goes
                obj = kind.Patterns[0];
                kind.Patterns.RemoveAt(0);
            }
            if (obj == null) return false;
            obj.SetPositionAndRotation(at, Random.rotation);
            var body = obj.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = at;
                body.isKinematic = false;
                body.velocity = away * Random.Range(0.2f, 0.8f);
                body.angularVelocity = Random.insideUnitSphere * 0.3f;
            }
            sItems.Add(new Item { T = obj, Part = obj.GetComponentInChildren<StructurePart>(), Label = kind.Label });
            return true;
        }

        // A copy of a loose collectible: its body object with its parts, made the way the game makes objects at run
        // time (the copy's entities come from their blueprints).
        static Transform? Copy(Transform pattern)
        {
            try
            {
                var ebc = EntityBlueprintComponent.Instantiate(pattern.gameObject);
                var go = ebc != null ? ebc.gameObject : Object.Instantiate(pattern.gameObject);
                go.name = pattern.name + "_Loot";
                go.SetActive(true);
                if (!sLoggedCopy)
                {
                    sLoggedCopy = true;
                    var world = World.DefaultGameObjectInjectionWorld;
                    var em = world != null ? world.EntityManager : default;
                    int parts = 0, withEntity = 0, members = 0;
                    foreach (var part in go.GetComponentsInChildren<StructurePart>(true))
                    {
                        parts++;
                        var e = part.Entity;
                        if (world == null || e == Entity.Null || !em.Exists(e)) continue;
                        withEntity++;
                        if (em.HasComponent<HierarchyMember>(e)) members++;
                    }
                    Plugin.Log.LogInfo($"[Stations] first loot copy '{go.name}': body entity {(ebc != null && ebc.Entity != Entity.Null ? "yes" : "NO")}, " +
                                       $"{parts} part(s), {withEntity} with an entity, {members} in a hierarchy, rigidbody {(go.GetComponent<Rigidbody>() != null ? "yes" : "no")}.");
                }
                return go.transform;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Stations] could not copy '{pattern.name}': {ex.Message}");
                return null;
            }
        }

        // Every frame: forget the ones taken or gone.
        // (the collectible leaves its holder when taken; the holder stays behind empty)
        internal static void Frame() => sItems.RemoveAll(i => !Present(i));

        static bool Present(Item i)
        {
            if (i.T == null || !i.T.gameObject.activeInHierarchy) return false;
            var part = i.Part;
            if (part == null || !part.gameObject.activeInHierarchy || !part.transform.IsChildOf(i.T)) return false;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return true;
            var em = world.EntityManager;
            var e = part.Entity;
            return e != Entity.Null && em.Exists(e) && !em.HasComponent<PendingDestruction>(e);
        }
    }
}
