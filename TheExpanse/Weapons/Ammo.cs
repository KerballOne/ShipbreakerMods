using System.Collections.Generic;
using System.Text.RegularExpressions;
using BBI.Unity.Game;
using UnityEngine;

namespace TheExpanse
{
    // Ammunition and what each weapon needs on board to fire.
    //   PDC:      each cannon is fed from its own three crates (PDC_Ammo_Crate_<side>_1..3, matched to the cannon by its
    //             name: Port_Fore -> Fore_Port, Dorsal -> Aft_Dorsal ...). A crate holds a third of six minutes of
    //             continuous fire at PdcRate: 20 rounds/s x 360 s / 3 = 2,400 rounds. Crates are emptied in order;
    //             a crate counts only while it is on board. It fires only while its feed is complete: every
    //             AmmoBelt_/AmmoFeed_/AmmoReceiver_<mount>* piece on board (the truss, body and barrel are the turret's own);
    //   rail gun: RailGunSlugs (20), all in Railgun_Loader_Crate (it feeds the breech directly); it fires only while
    //             the crate and every Railgun_* piece (barrels, breech, housing, muzzle, base plate) are on board;
    //   torpedoes: the torpedoes still in each tube's tray and tube (Torpedo).
    // "On board": the piece is there and still part of the ship's main structure (cut loose or salvaged: gone).
    // The crates are the Corvette's loaders (ModdedShipLoader fills each with a stock crate, which the game then moves
    // under its own physics object): each is matched to the ship part standing where its loader is.
    // Counts last until the next level load.
    internal static class Ammo
    {
        sealed class Piece
        {
            internal string Name = "";
            internal Transform T = null!;
            internal StructurePart? Part;
            internal Vector3 At;                      // where it stood when found (the loader may be gone later)
            internal Vector3 Position => T != null ? T.position : At;
        }

        static string Clean(string name) => name.Replace("(Clone)", "").Trim();

        static readonly Regex CrateName = new Regex(@"^PDC_Ammo_Crate_(.+)_(\d+)$", RegexOptions.IgnoreCase);
        static readonly Regex FeedName = new Regex(@"^Ammo(Belt|Feed|Receiver)_([A-Za-z]+?)(A|B|Link)?(_|$)", RegexOptions.IgnoreCase);
        static readonly Dictionary<string, Piece> sNamed = new Dictionary<string, Piece>();              // every piece by name
        static readonly Dictionary<string, List<Piece>> sCrates = new Dictionary<string, List<Piece>>();  // PDC -> its crates
        static readonly Dictionary<string, List<Piece>> sFeeds = new Dictionary<string, List<Piece>>();   // PDC -> its feed
        static readonly List<Piece> sRailParts = new List<Piece>();
        static Piece? sRailCrate;
        static readonly Dictionary<string, float> sLeft = new Dictionary<string, float>();                // crate name -> rounds left
        static int sRail = -1;

        internal static float CrateRounds => Mathf.Max(1f, Plugin.WeaponsPdcRate.Value) * 360f / 3f;

        internal static void Reset()
        {
            sNamed.Clear(); sCrates.Clear(); sFeeds.Clear(); sRailParts.Clear();
            sRailCrate = null;
            sLeft.Clear();
            sRail = -1;
        }

        static bool Wanted(string name) =>
            CrateName.IsMatch(name) || name.StartsWith("AmmoBelt_") || name.StartsWith("AmmoFeed_") || name.StartsWith("AmmoReceiver_")
            || name.StartsWith("Railgun_");

        // From WeaponMounts' scan of the ship's parts.
        internal static void Note(Transform t)
        {
            string name = Clean(t.name);
            if (!Wanted(name) || sNamed.ContainsKey(name)) return;
            sNamed[name] = new Piece { Name = name, T = t, Part = t.GetComponentInParent<StructurePart>(), At = t.position };
        }

        // When the ship has finished spawning, while every loader still stands under its own name (by the first sit
        // the side cannons' loaders were no longer found): all loaded objects, inactive ones too, searched once by
        // name, and where each stands kept. A short hitch at the end of the ship's loading.
        internal static void Capture()
        {
            Reset();
            var odd = new List<string>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                string n = t.name;
                if (n.IndexOf("Ammo_Crate", System.StringComparison.OrdinalIgnoreCase) < 0 && !n.StartsWith("Railgun_Loader")) continue;
                if (Wanted(Clean(n))) Note(t);
                else if (odd.Count < 20) odd.Add(n);
            }
            int crates = 0;
            foreach (var k in sNamed.Keys) if (CrateName.IsMatch(k)) crates++;
            Plugin.Log.LogInfo($"[Weapons] ammo at ship spawn: {crates} PDC crate loader(s), rail gun loader {(sNamed.ContainsKey("Railgun_Loader_Crate") ? "found" : "NOT found")}" +
                               (odd.Count > 0 ? $"; other crate-like names: {string.Join(", ", odd)}" : "") + ".");
        }

        internal static void Setup(IEnumerable<string> pdcNames, List<StructurePart> shipParts)
        {
            // the loaders: not inside any part (their stock part was moved away). Found at ship spawn (Capture); if
            // that missed any, looked for again by name among all loaded objects
            int before = sNamed.Count;
            if (!sNamed.ContainsKey("Railgun_Loader_Crate") || CrateCount() < 3 * new List<string>(pdcNames).Count)
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                    if (t != null && t.gameObject.scene.IsValid() && Wanted(Clean(t.name))) Note(t);
            Plugin.Log.LogInfo($"[Weapons] ammo: {sNamed.Count - before} more piece(s) found among all loaded objects on the first sit; crate loaders known: " +
                               string.Join(", ", new List<string>(sNamed.Keys).FindAll(n => n.StartsWith("PDC_Ammo") || n.Contains("Loader"))) + ".");
            // a loader's part: the ship part standing where it is
            // (a crate or ammo part within 6 m first, else any part within 2 m)
            int matched = 0;
            var missed = new List<string>();
            foreach (var p in sNamed.Values)
            {
                if (p.Part != null) continue;
                StructurePart? best = null, crate = null;
                float bestD = 2f * 2f, crateD = 6f * 6f, nearest = float.MaxValue;
                foreach (var part in shipParts)
                {
                    if (part == null) continue;
                    float d = (part.transform.position - p.Position).sqrMagnitude;
                    if (d < nearest) nearest = d;
                    if (d < bestD) { bestD = d; best = part; }
                    if (d < crateD && (part.name.Contains("Crate") || part.name.Contains("Ammo"))) { crateD = d; crate = part; }
                }
                p.Part = crate ?? best;
                if (p.Part != null) matched++;
                else missed.Add($"{p.Name} (nearest part {Mathf.Sqrt(nearest):0.0} m)");
            }
            if (missed.Count > 0) Plugin.Log.LogInfo($"[Weapons] ammo: no part where these stand (counted as on board): {string.Join(", ", missed)}.");

            sCrates.Clear(); sFeeds.Clear(); sRailParts.Clear();
            var log = new List<string>();
            foreach (var pdc in pdcNames)
            {
                var tokens = Tokens(pdc);
                string mount = pdc.Replace("_", "");
                var crates = new List<Piece>();
                var feeds = new List<Piece>();
                foreach (var p in sNamed.Values)
                {
                    var c = CrateName.Match(p.Name);
                    if (c.Success && Tokens(c.Groups[1].Value).SetEquals(tokens)) crates.Add(p);
                    var f = FeedName.Match(p.Name);
                    if (f.Success && f.Groups[2].Value.Equals(mount, System.StringComparison.OrdinalIgnoreCase)) feeds.Add(p);
                }
                crates.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                sCrates[pdc] = crates;
                sFeeds[pdc] = feeds;
                foreach (var c in crates) if (!sLeft.ContainsKey(c.Name)) sLeft[c.Name] = CrateRounds;
                log.Add($"{pdc} {crates.Count} crate(s) + {feeds.Count} feed piece(s)");
            }
            foreach (var p in sNamed.Values)
                if (p.Name.StartsWith("Railgun_")) { if (p.Name == "Railgun_Loader_Crate") sRailCrate = p; else sRailParts.Add(p); }
            if (sRail < 0) sRail = Mathf.Max(0, Plugin.WeaponsRailGunSlugs.Value);
            int unmatched = 0;
            foreach (var p in sNamed.Values) if (p.Part == null) unmatched++;
            Plugin.Log.LogInfo($"[Weapons] ammo: {CrateRounds:N0} rounds a crate; {string.Join("; ", log)}; rail gun {sRail} slug(s) " +
                               $"{(sRailCrate != null ? "in 'Railgun_Loader_Crate'" : "(no loader crate: not tied to one)")}, {sRailParts.Count} rail gun part(s); " +
                               $"{matched} loader(s) matched to their parts{(unmatched > 0 ? $", {unmatched} piece(s) without a part" : "")}.");
        }

        static int CrateCount()
        {
            int c = 0;
            foreach (var n in sNamed.Keys) if (CrateName.IsMatch(n)) c++;
            return c;
        }

        // "Aft_Dorsal" and "Dorsal", "Fore_Port" and "Port_Fore" are the same mount
        static HashSet<string> Tokens(string name)
        {
            var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var t in name.Split('_')) if (t.Length > 0 && !t.Equals("Aft", System.StringComparison.OrdinalIgnoreCase)) set.Add(t);
            return set;
        }

        static bool OnBoard(Piece p)
        {
            if (p.Part != null) return p.Part.gameObject.activeInHierarchy && FlightModel.IsMainShipPart(p.Part);
            // no part found for it: its loader object while it lasts; once that is gone it can't be told, so it counts
            return p.T == null || p.T.gameObject.activeInHierarchy;
        }

        // ---- PDC ----------------------------------------------------------------------------------------------

        // Its feed complete: every belt, feed and receiver piece of its mount on board.
        internal static bool PdcReady(string pdc)
        {
            if (!sFeeds.TryGetValue(pdc, out var feeds)) return true;
            foreach (var p in feeds) if (!OnBoard(p)) return false;
            return true;
        }

        // One round for a cannon; false when its feed is broken or its crates on board are empty.
        internal static bool TakePdc(string pdc)
        {
            if (!PdcReady(pdc)) return false;
            if (!sCrates.TryGetValue(pdc, out var crates) || crates.Count == 0) return true;      // no crates known: not limited
            foreach (var c in crates)
            {
                if (!OnBoard(c) || !sLeft.TryGetValue(c.Name, out float left) || left < 1f) continue;
                sLeft[c.Name] = left - 1f;
                return true;
            }
            return false;
        }

        internal static int PdcLeft(string pdc)
        {
            if (!sCrates.TryGetValue(pdc, out var crates)) return 0;
            float sum = 0f;
            foreach (var c in crates) if (OnBoard(c) && sLeft.TryGetValue(c.Name, out float left)) sum += left;
            return Mathf.FloorToInt(sum);
        }

        // A cannon's rounds left as a share of what its crates held (1 when it has no crates known).
        internal static float PdcFraction(string pdc)
        {
            if (!sCrates.TryGetValue(pdc, out var crates) || crates.Count == 0) return 1f;
            return Mathf.Clamp01(PdcLeft(pdc) / (crates.Count * CrateRounds));
        }

        internal static int PdcTotal
        {
            get
            {
                int sum = 0;
                foreach (var pdc in sCrates.Keys) sum += PdcLeft(pdc);
                return sum;
            }
        }

        // ---- rail gun -----------------------------------------------------------------------------------------

        // Every rail gun piece on board.
        internal static bool RailReady
        {
            get
            {
                foreach (var p in sRailParts) if (!OnBoard(p)) return false;
                return true;
            }
        }

        // Slugs left in the loader crate, while it is on board (no crate found: not tied to one).
        internal static int Rail => sRailCrate != null && !OnBoard(sRailCrate) ? 0 : Mathf.Max(0, sRail);

        internal static bool TakeRail()
        {
            if (sRail < 0) sRail = Mathf.Max(0, Plugin.WeaponsRailGunSlugs.Value);
            if (!RailReady || Rail <= 0) return false;
            sRail--;
            return true;
        }

        // For the wheel and HUD: 14400 -> "14K"
        internal static string Short(int n) => n >= 10000 ? $"{n / 1000f:0}K" : n >= 1000 ? $"{n / 1000f:0.#}K" : n.ToString();
    }
}
