using System;
using System.Collections.Generic;
using System.IO;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace TheExpanse
{
    // Setup mode ([Flight] SetupMode, also in the options page): fitting a ship for flight by hand, saved in ships.json
    // next to the mod's DLL, one entry per ship model (players can add ships, or edit, add and remove by hand).
    //   RCS thrusters: place demo charges where the blocks go (unlimited charges, no detonation in setup mode), then
    //       SetupRcsKey (Alt+1): every placed charge becomes an RCS block, flat on the surface it is stuck to.
    //   Drive: place a demo charge on the face the exhaust leaves from, SetupDriveKey (Alt+2): the plume burns from
    //       there, along the face's outward normal, as wide as the face.
    //   Seat: stand where the pilot sits, facing the way they face, SetupSeatKey (Alt+3): a flight seat there.
    //   SetupRemoveKey (Alt+Backspace): removes the RCS block or seat nearest to where you aim (within 3 m).
    // The charges used are removed.
    //
    // Positions are kept in the frame of one reference part of the ship's main structure (a part whose name is unique on
    // the ship, near its middle, picked at the first save and named in the file), so they fit the ship wherever it is:
    // freshly spawned, or resumed from a save (career or FreePlayPlus), moved, or flown and parked far out. The fittings
    // ride on that part. An entry is found by its model (the ship's spawned root object's name), or, when that object is
    // missing (some resumed saves), by its reference part being on the ship.
    //
    // A ship with no drive set keeps its own (the MCRN Corvette's engine bell, found by its parts); with no seats set,
    // the command chairs found by name. "noBlocks" (the ship) or "noBlock" (one thruster): no block is drawn, and the jets
    // come straight out of the hull panel that is there.
    internal static class ShipSetup
    {
        // ---- the file ------------------------------------------------------------------------------------------------
        [Serializable] internal class ShipsFile { public List<ShipEntry> ships = new List<ShipEntry>(); }

        [Serializable]
        internal class ShipEntry
        {
            public string model = "";
            public string reference = "";           // the part whose frame the positions are in
            public float rcsSize;                   // m, the RCS blocks' base; 0 = 1% of the ship's length
            public bool noBlocks;                   // no blocks drawn: the jets come out of the hull
            public List<DriveEntry> drives = new List<DriveEntry>();   // one plume each
            // the single drive of older files: read into drives, not written again
            [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
            public DriveEntry? drive;
            public List<SeatEntry> seats = new List<SeatEntry>();
            public List<RcsEntry> rcs = new List<RcsEntry>();
        }

        [Serializable] internal class DriveEntry { public bool set; public V3 position; public V3 direction; public float diameter; }
        [Serializable] internal class SeatEntry { public V3 position; public V3 forward; public V3 up; }
        [Serializable] internal class RcsEntry { public V3 position; public V3 up; public V3 forward; public bool noBlock; }

        // a point or direction in the file: just x, y, z (rounded to the millimeter)
        [Serializable]
        internal struct V3
        {
            public float x, y, z;
            public static implicit operator Vector3(V3 v) => new Vector3(v.x, v.y, v.z);
            public static implicit operator V3(Vector3 v) => new V3 { x = Round(v.x), y = Round(v.y), z = Round(v.z) };
            static float Round(float f) => Mathf.Round(f * 1000f) / 1000f;
        }

        static string FilePath => Path.Combine(Path.GetDirectoryName(typeof(ShipSetup).Assembly.Location) ?? ".", "ships.json");
        static ShipsFile? sFile;

        static ShipsFile File
        {
            get
            {
                if (sFile != null) return sFile;
                try
                {
                    sFile = System.IO.File.Exists(FilePath) ? Newtonsoft.Json.JsonConvert.DeserializeObject<ShipsFile>(System.IO.File.ReadAllText(FilePath)) : null;
                }
                catch (Exception ex) { Plugin.Log.LogError($"[Setup] {FilePath} could not be read ({ex.Message}); starting an empty one in memory."); }
                sFile ??= new ShipsFile();
                foreach (var e in sFile.ships)
                {
                    e.drives ??= new List<DriveEntry>();
                    if (e.drive != null && e.drive.set && e.drives.Count == 0) e.drives.Add(e.drive);
                    e.drive = null;
                }
                return sFile;
            }
        }

        static void Save()
        {
            try
            {
                System.IO.File.WriteAllText(FilePath, Newtonsoft.Json.JsonConvert.SerializeObject(File, Newtonsoft.Json.Formatting.Indented));
                SetupLog.Saved(FilePath, File.ships.Count, sEntry == null ? "no entry" :
                    $"'{sEntry.model}' ref '{sEntry.reference}', {sEntry.rcs.Count} RCS, {DrivesText(sEntry)}, {sEntry.seats.Count} seat(s)");
            }
            catch (Exception ex) { Plugin.Log.LogError($"[Setup] could not save {FilePath}: {ex.Message}"); }
        }

        // ---- the ship's frame: a reference part -----------------------------------------------------------------------
        static string sModel = "";
        static ShipEntry? sEntry;                   // this ship's entry, once matched (or made in setup)
        static Transform? sRef;                     // the reference part
        static Transform? sMarkers;                 // under the reference part: the seat and drive markers and the RCS blocks
        static readonly List<Transform> sSeats = new List<Transform>();
        static readonly List<(Transform mount, float diameter)> sDrives = new List<(Transform, float)>();

        static string DrivesText(ShipEntry e) => e.drives.Count == 0 ? "stock drive" :
            $"{e.drives.Count} drive(s) of {string.Join(", ", e.drives.ConvertAll(d => $"{d.diameter:0.0} m"))}";
        static Dictionary<string, List<StructurePart>>? sPartsByName;
        internal static string Model => sModel;

        static bool sSpawnSeen;
        static float sLevelLoadedAt;

        // The ship being spawned (set as it starts to spawn, and from the spawn event).
        internal static ShipPreview? Preview;

        // The model: the name of the ship's root module (its prefab), or its asset id if that is not loaded.
        static string ModelOf(ShipPreview? preview)
        {
            // the ship's class ("Mackerel", ...): its archetype, localized as the ship selector and the job board show it
            var archetype = preview?.Archetype ?? "";
            if (archetype.Length > 0)
            {
                try
                {
                    if (Main.Instance.LocalizationService.TryLocalize(archetype, out var localized) && !string.IsNullOrWhiteSpace(localized))
                        return localized.Trim();
                }
                catch { }
                return archetype;
            }
            var root = preview?.ModuleGroup?.RootModule;
            if (root == null) return "";
            var name = root.ModuleDefinition != null ? root.ModuleDefinition.name : "";
            if (name.EndsWith("(Clone)", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "(Clone)".Length);
            if (name.Trim().Length > 0) return name.Trim();
            var guid = root.ModulePrefabRef?.AssetGUID ?? "";
            if (guid.Length == 0) return "";
            // the prefab's address in the game's (or the mod's) asset catalog: ".../Tachi_Rocinante.prefab" -> "Tachi_Rocinante"
            foreach (var locator in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
            {
                if (!(locator is UnityEngine.AddressableAssets.ResourceLocators.ResourceLocationMap)) continue;
                if (!locator.Locate(guid, null, out var locations) || locations == null) continue;
                foreach (var location in locations)
                {
                    var key = location?.PrimaryKey ?? "";
                    if (key.Length == 0 || key == guid) continue;
                    var file = Path.GetFileNameWithoutExtension(key.Replace("\\", "/"));
                    if (file.Length > 0) return file;
                }
            }
            return guid;
        }

        internal static void OnShipSpawned(ShipPreview? preview = null)
        {
            if (preview != null) Preview = preview;
            sSpawnSeen = true;
            Clear();
            sFile = null;                           // read again: it may have been edited by hand
            sShipLength = 0f;
            sPartsByName = null;
            sEntry = null; sRef = null;
            sModel = ModelOf(Preview);
            Match();
            SetupLog.Identity(Preview, sModel, sMatchHow);
            Plugin.Log.LogInfo($"[Setup] ship model '{(sModel.Length > 0 ? sModel : "(unknown)")}': " +
                               (sEntry == null ? "not in ships.json." : sRef == null ? $"in ships.json, but its reference part '{sEntry.reference}' is not on the ship." :
                                $"fitted from ships.json (entry '{sEntry.model}', reference part '{sRef.name}')."));
            Apply();
        }

        // The ship's parts by name (built once per ship).
        static Dictionary<string, List<StructurePart>> PartsByName()
        {
            if (sPartsByName != null) return sPartsByName;
            sPartsByName = new Dictionary<string, List<StructurePart>>();
            foreach (var part in UnityEngine.Object.FindObjectsOfType<StructurePart>())
            {
                if (!FlightModel.IsShipPart(part)) continue;
                if (!sPartsByName.TryGetValue(part.name, out var list)) sPartsByName[part.name] = list = new List<StructurePart>();
                list.Add(part);
            }
            return sPartsByName;
        }

        static Transform? UniquePart(string name)
        {
            if (string.IsNullOrEmpty(name) || !PartsByName().TryGetValue(name, out var list)) return null;
            list.RemoveAll(p => p == null);
            if (list.Count == 1) return list[0].transform;
            foreach (var p in list) if (FlightModel.IsMainShipPart(p)) return p.transform;    // several: the one in the main structure
            return list.Count > 0 ? list[0].transform : null;
        }

        // This ship's entry: by model, else by its reference part being on the ship.
        static string sMatchHow = "";

        static void Match()
        {
            sEntry = null; sRef = null;
            sMatchHow = $"ships.json has {File.ships.Count} ship(s) [{string.Join(", ", File.ships.ConvertAll(e => $"'{e.model}' ref '{e.reference}'"))}]; ";
            if (sModel.Length > 0)
                foreach (var e in File.ships)
                    if (string.Equals(e.model, sModel, StringComparison.OrdinalIgnoreCase)) { sEntry = e; sMatchHow += $"matched '{e.model}' by model; "; break; }
            if (sEntry == null)
                foreach (var e in File.ships)
                    if (!string.IsNullOrEmpty(e.reference) && PartsByName().TryGetValue(e.reference, out var l) && l.Count == 1)
                    {
                        sEntry = e;
                        sMatchHow += $"matched '{e.model}' by its reference part{(sModel.Length > 0 && e.model != sModel ? $", renamed to '{sModel}' (saved with the next change)" : "")}; ";
                        if (sModel.Length > 0) e.model = sModel;
                        break;
                    }
            if (sEntry == null) { sMatchHow += "no entry for this ship."; return; }
            int named = PartsByName().TryGetValue(sEntry.reference, out var parts) ? parts.Count : 0;
            sRef = UniquePart(sEntry.reference);
            sMatchHow += $"reference part '{sEntry.reference}': {named} on the ship{(sRef != null ? $", using the one at {sRef.position}" : ", NONE found: fittings not placed")}.";
        }

        // A part to be the reference: one whose name is unique on the ship, in its main structure, nearest the middle.
        static Transform? PickReference()
        {
            var parts = PartsByName();
            var center = Vector3.zero; int n = 0;
            foreach (var list in parts.Values) foreach (var p in list) if (p != null) { center += p.transform.position; n++; }
            if (n == 0) return null;
            center /= n;
            Transform? best = null; float bestDistance = float.MaxValue;
            foreach (var pair in parts)
            {
                if (pair.Value.Count != 1 || pair.Value[0] == null || !FlightModel.IsMainShipPart(pair.Value[0])) continue;
                float d = (pair.Value[0].transform.position - center).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = pair.Value[0].transform; }
            }
            return best;
        }

        // reference frame <-> world (position and rotation only)
        static bool Ready => sRef != null;
        static Vector3 ToWorld(Vector3 p) => sRef!.position + sRef.rotation * p;
        static Vector3 ToWorldDir(Vector3 d) => sRef!.rotation * d;
        static Vector3 ToShip(Vector3 w) => Quaternion.Inverse(sRef!.rotation) * (w - sRef.position);
        static Vector3 ToShipDir(Vector3 w) => Quaternion.Inverse(sRef!.rotation) * w;

        // the entry to write to in setup mode (made, with a reference part, if the ship has none)
        static ShipEntry? Entry(bool create)
        {
            if (sEntry != null || !create) return sEntry;
            var reference = PickReference();
            if (reference == null) { Plugin.Log.LogWarning("[Setup] no part with a unique name in the ship's main structure: cannot save fittings for it."); return null; }
            sEntry = new ShipEntry { model = sModel.Length > 0 ? sModel : reference.name, reference = reference.name };
            sRef = reference;
            File.ships.Add(sEntry);
            Plugin.Log.LogInfo($"[Setup] new entry '{sEntry.model}' in ships.json, positions in the frame of part '{reference.name}'.");
            return sEntry;
        }

        // ---- what the rest of the mod reads ------------------------------------------------------------------------------
        // The flight seats from the file (empty: the command chairs are found by name).
        internal static IReadOnlyList<Transform> Seats => sSeats;

        // The drive's mouth from the file: where, which way the exhaust goes, how wide.
        internal static bool TryDrive(out Transform mount, out float diameter)
        {
            mount = sDrives.Count > 0 ? sDrives[0].mount : null!; diameter = sDrives.Count > 0 ? sDrives[0].diameter : 0f;
            return mount != null;
        }

        // every drive from the file (the first is the one TryDrive gives); a plume burns from each
        internal static IReadOnlyList<(Transform mount, float diameter)> Drives => sDrives;

        // ---- building the markers and blocks from the file ------------------------------------------------------------------
        static void Clear()
        {
            if (sMarkers != null) UnityEngine.Object.Destroy(sMarkers.gameObject);
            sMarkers = null;
            sSeats.Clear();
            sDrives.Clear();
            RcsBlocks.Clear();
        }

        internal static void Reset()
        {
            Clear();
            sModel = "";
            sSpawnSeen = false;
            sLevelLoadedAt = Time.unscaledTime;
            sEntry = null; sRef = null;
            sPartsByName = null;
            sCharges.Clear();
            SetupLog.Reset();
            LeaveSetup();
        }

        static void Apply()
        {
            Clear();
            var entry = Entry(false);
            if (entry == null || !Ready) return;
            sMarkers = new GameObject("TheExpanse_ShipFittings").transform;
            sMarkers.SetParent(sRef!, false);
            foreach (var seat in entry.seats)
            {
                var t = new GameObject("TheExpanse_Seat").transform;
                t.SetParent(sMarkers, false);
                t.SetPositionAndRotation(ToWorld(seat.position), Quaternion.LookRotation(ToWorldDir(seat.forward), ToWorldDir(seat.up)));
                sSeats.Add(t);
            }
            foreach (var drive in entry.drives)
            {
                var mount = new GameObject("TheExpanse_DriveMount").transform;
                mount.SetParent(sMarkers, false);
                var dir = ToWorldDir(drive.direction).normalized;
                mount.SetPositionAndRotation(ToWorld(drive.position), Quaternion.LookRotation(dir, Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up));
                sDrives.Add((mount, drive.diameter));
            }
            float size = entry.rcsSize > 0f ? entry.rcsSize : 0.01f * ShipLength();
            foreach (var rcs in entry.rcs)
                RcsBlocks.Add(sMarkers, ToWorld(rcs.position), ToWorldDir(rcs.up).normalized, ToWorldDir(rcs.forward).normalized, size,
                              withBlock: !(entry.noBlocks || rcs.noBlock));
            Plugin.Log.LogInfo($"[Setup] '{sModel}': {entry.seats.Count} seat(s), {(entry.drives.Count > 0 ? DrivesText(entry) : "drive found by its parts")}, " +
                               $"{entry.rcs.Count} RCS block(s) of {size:0.00} m.");
        }

        static float sShipLength;

        static float ShipLength()
        {
            if (sShipLength > 0f) return sShipLength;
            var bounds = new Bounds(); bool any = false;
            foreach (var part in UnityEngine.Object.FindObjectsOfType<StructurePart>())
            {
                if (!FlightModel.IsShipPart(part)) continue;
                if (!any) { bounds = new Bounds(part.transform.position, Vector3.zero); any = true; } else bounds.Encapsulate(part.transform.position);
            }
            sShipLength = any ? Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) : 100f;
            return sShipLength;
        }

        // ---- setup mode ----------------------------------------------------------------------------------------------------
        // A placed charge: the surface it was aimed at as it went down (the game's own aim), and the charge itself, held
        // still where it was put (a charge the game cannot joint to a part, as on thruster nozzles, drifted off).
        sealed class Placed { internal GameObject Go = null!; internal bool HasHit; internal Vector3 Point, Normal; internal Collider? Collider; }
        static readonly List<Placed> sCharges = new List<Placed>();
        static readonly System.Reflection.FieldInfo? sAimField = AccessTools.Field(typeof(DemoChargeController), "mPlacementHitInfo");
        static DemoChargeController? sController;
        static bool sInSetup, sUnlimitedSet;
        internal static bool InSetup => sInSetup;

        // The aim when the place button was pressed (the game's own: the part and surface it targets). The charge made
        // right after takes it. On parts the game cannot joint a charge to (thruster nozzles) the charge is made free-
        // floating and no "deployed" event comes, so the charge is recorded when it is made, not when it is deployed.
        static bool sAimPending;
        static RaycastHit sAim;
        static float sAimAt;

        internal static void NoteAim(RaycastHit aim)
        {
            if (!sInSetup || aim.collider == null) { sAimPending = false; return; }
            sAim = aim; sAimAt = Time.unscaledTime; sAimPending = true;
        }

        internal static void OnChargeMade(DemoChargeInstantiatedEvent ev)
        {
            if (!sInSetup || ev.DemoCharge == null) return;
            var placed = new Placed { Go = ev.DemoCharge };
            if (sAimPending && Time.unscaledTime - sAimAt < 2f)
            {
                placed.HasHit = true; placed.Point = sAim.point; placed.Normal = sAim.normal; placed.Collider = sAim.collider;
            }
            sAimPending = false;
            sCharges.Add(placed);
        }

        // deployed (jointed) or free-floating: held still where it went down, if it is a body of its own
        internal static void OnChargeDeployed(DemoChargeDeployedEvent ev) => Hold(ev.DemoCharge);
        internal static void OnChargeFreeFloating(DemoChargeFreeFloatingEvent ev) => Hold(ev.DemoCharge);

        static void Hold(GameObject? charge)
        {
            if (!sInSetup || charge == null) return;
            if (!sCharges.Exists(c => c.Go == charge)) sCharges.Add(new Placed { Go = charge });   // made unseen
            var body = charge.GetComponentInParent<Rigidbody>();
            if (body == null || body.GetComponentsInChildren<StructurePart>().Length > 1) return;    // jointed into a ship body: leave it
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }

        static void EnterSetup()
        {
            sInSetup = true;
            sFile = null;
            Match();
            Apply();
            sCharges.Clear();
            Main.EventSystem.Post(EnableUnlimitedDemoChargesEvent.GetEvent(true));
            sUnlimitedSet = true;
            Plugin.Log.LogInfo($"[Setup] setup mode on for '{sModel}': place demo charges, then {Key(Plugin.SetupRcsKey)} for RCS blocks, " +
                               $"{Key(Plugin.SetupDriveKey)} for the drive; {Key(Plugin.SetupSeatKey)} makes a seat where you stand; " +
                               $"{Key(Plugin.SetupRemoveKey)} removes the block or seat you aim at.");
        }

        static void LeaveSetup()
        {
            if (!sInSetup) return;
            sInSetup = false;
            if (sUnlimitedSet && !IsFreePlay()) Main.EventSystem.Post(EnableUnlimitedDemoChargesEvent.GetEvent(false));
            sUnlimitedSet = false;
            Plugin.Log.LogInfo("[Setup] setup mode off.");
        }

        static bool IsFreePlay()
        {
            try { return SceneLoader.Instance.CurrentLevelData.SessionType == GameSession.SessionType.FreeMode; }
            catch { return false; }
        }

        // every frame (Update)
        internal static void Tick()
        {
            // a ship that came without the game's spawn event (some resumed saves): fitted a few seconds into the level
            if (!sSpawnSeen && Preview != null && Time.unscaledTime > sLevelLoadedAt + 8f) OnShipSpawned();
            bool want = Plugin.Enabled.Value && Plugin.FlightSetupMode.Value && sSpawnSeen && !FlightSeat.Seated;
            if (want && !sInSetup) EnterSetup();
            else if (!want && sInSetup) LeaveSetup();
            if (!sInSetup) return;
            sCharges.RemoveAll(c => c.Go == null);
            if (FlightModel.UnderWay) return;           // fitting in the bay (or with the ship at rest)
            if (Plugin.SetupRcsKey.Value.IsDown()) MakeRcs();
            if (Plugin.SetupDriveKey.Value.IsDown()) MakeDrive();
            if (Plugin.SetupSeatKey.Value.IsDown()) MakeSeat();
            if (Plugin.SetupRemoveKey.Value.IsDown()) RemoveAimed();
        }

        const string Gap = "\n";          // one item per line
        internal static string Status => !sInSetup ? "" :
            $"SETUP: {sModel}{Gap}CHARGES PLACED = {sCharges.Count}{Gap}" +
            $"RCS: ({Key(Plugin.SetupRcsKey)}) = {sEntry?.rcs.Count ?? 0}{Gap}" +
            $"DRIVE: ({Key(Plugin.SetupDriveKey)}) = {(sEntry != null && sEntry.drives.Count > 0 ? sEntry.drives.Count.ToString() : "STOCK")}{Gap}" +
            $"SEAT: ({Key(Plugin.SetupSeatKey)}) = {(sEntry != null && sEntry.seats.Count > 0 ? $"{sEntry.seats.Count} SET" : "COMMAND CHAIRS")}{Gap}" +
            $"REMOVE: ({Key(Plugin.SetupRemoveKey)})";

        // "Alt+1" for LeftAlt + Alpha1: modifiers first, short names
        static string Key(BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> entry)
        {
            var shortcut = entry.Value;
            if (shortcut.MainKey == KeyCode.None) return "(none)";
            var parts = new List<string>();
            foreach (var m in shortcut.Modifiers) parts.Add(KeyName(m));
            parts.Add(KeyName(shortcut.MainKey));
            return string.Join("+", parts);
        }

        static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return "Alt";
                case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
            }
            var name = key.ToString();
            if (name.StartsWith("Alpha", StringComparison.Ordinal) && name.Length == 6) return name.Substring(5);
            if (name.StartsWith("Keypad", StringComparison.Ordinal)) return "Num" + name.Substring(6);
            return name;
        }

        // The surface a charge is stuck to: a short cast back from the charge along each of its axes; the nearest hit on
        // something that is not the charge itself.
        static bool ChargeSurface(Placed placed, out Vector3 point, out Vector3 normal, out Collider? collider)
        {
            point = placed.Point; normal = placed.Normal; collider = placed.Collider;
            if (placed.HasHit) return true;
            if (placed.Go == null || !ChargeSurface(placed.Go, out var hit)) return false;
            point = hit.point; normal = hit.normal; collider = hit.collider;
            return true;
        }

        static bool ChargeSurface(GameObject charge, out RaycastHit surface)
        {
            surface = default;
            var own = new HashSet<Collider>(charge.GetComponentsInChildren<Collider>(true));
            var t = charge.transform;
            bool found = false;
            foreach (var axis in new[] { t.forward, -t.forward, t.up, -t.up, t.right, -t.right })
            {
                var hits = Physics.RaycastAll(t.position + axis * 0.4f, -axis, 1.2f, ~0, QueryTriggerInteraction.Ignore);
                foreach (var hit in hits)
                {
                    if (hit.collider == null || own.Contains(hit.collider)) continue;
                    if (!found || hit.distance < surface.distance) { surface = hit; found = true; }
                }
            }
            return found;
        }

        static void RemoveCharge(GameObject charge)
        {
            var ebc = charge.GetComponentInParent<EntityBlueprintComponent>();
            // destroyed only (its own OnDestroy shuts its entity down): shut down first, the game's info panel still showing
            // it read the dead entity and threw
            UnityEngine.Object.Destroy(ebc != null ? ebc.gameObject : charge);
        }

        static void MakeRcs()
        {
            var entry = Entry(true);
            if (entry == null) return;
            if (entry.rcsSize <= 0f) entry.rcsSize = Mathf.Round(0.01f * ShipLength() * 100f) / 100f;
            int made = 0;
            var shipForward = FlightCamera.TryShipFrame(out _, out var fwd, out _) ? fwd : (DrivePlume.TryDriveAxis(out var aft) ? -aft : Vector3.forward);
            int index = 0;
            foreach (var placed in sCharges.ToArray())
            {
                if (!ChargeSurface(placed, out var point, out var normal, out var hitCollider))
                {
                    Plugin.Log.LogInfo($"[Setup] RCS: charge {index + 1}: no surface found (neither the aim nor a cast back); skipped.");
                    index++;
                    continue;
                }
                SetupLog.Used("RCS", index++, placed.HasHit, point, normal, hitCollider);
                var up = normal.normalized;
                var forward = Vector3.ProjectOnPlane(shipForward, up);
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(Vector3.up, up);
                entry.rcs.Add(new RcsEntry { position = ToShip(point), up = ToShipDir(up), forward = ToShipDir(forward.normalized) });
                if (placed.Go != null) RemoveCharge(placed.Go);
                made++;
            }
            sCharges.Clear();
            if (made == 0) { Plugin.Log.LogInfo("[Setup] no placed charges to make RCS blocks from."); return; }
            Save();
            Apply();
            Plugin.Log.LogInfo($"[Setup] {made} RCS block(s) added ({entry.rcs.Count} in all).");
        }

        // One drive per placed charge, each sized to the face it is on; they replace the drives set before.
        static void MakeDrive()
        {
            var drives = new List<DriveEntry>();
            int index = 0;
            foreach (var placed in sCharges.ToArray())
            {
                if (!ChargeSurface(placed, out var point, out var hitNormal, out var collider) || collider == null)
                {
                    Plugin.Log.LogInfo($"[Setup] DRIVE: charge {index + 1}: no surface found; skipped.");
                    index++;
                    continue;
                }
                SetupLog.Used("DRIVE", index++, placed.HasHit, point, hitNormal, collider);
                var normal = hitNormal.normalized;
                float diameter = FaceWidth(collider, normal);
                drives.Add(new DriveEntry { set = true, position = ToShip(point), direction = ToShipDir(normal), diameter = diameter });
                Plugin.Log.LogInfo($"[Setup] drive {drives.Count} set on '{collider.name}': {diameter:0.0} m across, exhaust along {normal}.");
            }
            if (drives.Count == 0) { Plugin.Log.LogInfo("[Setup] place a demo charge on each drive's face first."); return; }
            var entry = Entry(true);
            if (entry == null) return;
            entry.drives = drives;
            foreach (var c in sCharges.ToArray()) if (c.Go != null) RemoveCharge(c.Go);
            sCharges.Clear();
            Save();
            Apply();
            DrivePlume.Reset();
        }

        // How wide the part's face is across the normal: its mesh's box, the narrower of the two spans across the normal.
        static float FaceWidth(Collider collider, Vector3 normal)
        {
            var filter = collider.GetComponentInParent<MeshFilter>();
            Bounds local; Transform t;
            if (filter != null && filter.sharedMesh != null) { local = filter.sharedMesh.bounds; t = filter.transform; }
            else if (collider is MeshCollider mc && mc.sharedMesh != null) { local = mc.sharedMesh.bounds; t = collider.transform; }
            else return Mathf.Max(0.5f, Mathf.Min(collider.bounds.size.x, collider.bounds.size.z));
            var a = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var b = Vector3.Cross(normal, a);
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = t.TransformPoint(local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                float pa = Vector3.Dot(corner, a), pb = Vector3.Dot(corner, b);
                minA = Mathf.Min(minA, pa); maxA = Mathf.Max(maxA, pa); minB = Mathf.Min(minB, pb); maxB = Mathf.Max(maxB, pb);
            }
            return Mathf.Max(0.5f, Mathf.Min(maxA - minA, maxB - minB));
        }

        static void MakeSeat()
        {
            var body = FlightSeat.PlayerBody;
            if (body == null) return;
            var entry = Entry(true);
            if (entry == null) return;
            var forward = body.rotation * Vector3.forward;
            var up = body.rotation * Vector3.up;
            entry.seats.Add(new SeatEntry { position = ToShip(body.position), forward = ToShipDir(forward), up = ToShipDir(up) });
            Save();
            Apply();
            Plugin.Log.LogInfo($"[Setup] seat added where you are ({entry.seats.Count} in all).");
        }

        static void RemoveAimed()
        {
            var entry = Entry(false);
            var camera = Camera.main;
            if (entry == null || camera == null || !Ready) return;
            var ray = new Ray(camera.transform.position, camera.transform.forward);
            float best = 3f; int bestRcs = -1, bestSeat = -1, bestDrive = -1;
            for (int i = 0; i < entry.rcs.Count; i++)
            {
                float d = Vector3.Cross(ray.direction, ToWorld(entry.rcs[i].position) - ray.origin).magnitude;
                if (d < best) { best = d; bestRcs = i; bestSeat = -1; }
            }
            for (int i = 0; i < entry.seats.Count; i++)
            {
                float d = Vector3.Cross(ray.direction, ToWorld(entry.seats[i].position) - ray.origin).magnitude;
                if (d < best) { best = d; bestSeat = i; bestRcs = -1; }
            }
            for (int i = 0; i < entry.drives.Count; i++)
            {
                float d = Vector3.Cross(ray.direction, ToWorld(entry.drives[i].position) - ray.origin).magnitude;
                if (d < best) { best = d; bestDrive = i; bestSeat = -1; bestRcs = -1; }
            }
            if (bestRcs >= 0) { entry.rcs.RemoveAt(bestRcs); Plugin.Log.LogInfo("[Setup] RCS block removed."); }
            else if (bestSeat >= 0) { entry.seats.RemoveAt(bestSeat); Plugin.Log.LogInfo("[Setup] seat removed."); }
            else if (bestDrive >= 0) { entry.drives.RemoveAt(bestDrive); DrivePlume.Reset(); Plugin.Log.LogInfo("[Setup] drive removed."); }
            else { Plugin.Log.LogInfo("[Setup] nothing to remove where you aim (within 3 m of the line)."); return; }
            Save();
            Apply();
        }

        // In setup mode a charge goes anywhere on the ship: on parts the game keeps charges off (thruster nozzles and the
        // like: the spot "blocked" by overlapping parts, a collider with no part of its own, or a layer the charge's aim
        // skips), since the charges are only markers here.
        [HarmonyPatch(typeof(DemoChargePreview), "SetState")]
        static class NeverBlockedInSetup
        {
            static void Prefix(ref DemoChargePreviewState newState)
            {
                if (sInSetup && newState == DemoChargePreviewState.InRangeBlocked) newState = DemoChargePreviewState.InRangeUnblocked;
            }
        }

        [HarmonyPatch(typeof(DemoChargeController), nameof(DemoChargeController.TryGetNewTarget))]
        static class AnyPartInSetup
        {
            static void Postfix(ref bool __result, ref StructurePart target, ref RaycastHit hitInfo)
            {
                if (!sInSetup || __result) return;
                var camera = Camera.main;
                if (camera == null) return;
                if (!Physics.Raycast(camera.transform.position, camera.transform.forward, out var hit, 30f, ~0, QueryTriggerInteraction.Ignore)) return;
                var part = hit.collider != null ? hit.collider.GetComponentInParent<StructurePart>() : null;
                if (part == null) return;
                target = part;
                hitInfo = hit;
                __result = true;
            }
        }

        // No detonation in setup mode: the charges are markers.
        [HarmonyPatch(typeof(DemoChargeController), "UpdateDetonationState")]
        static class NoDetonationInSetup
        {
            static bool Prefix() => !sInSetup;
        }

        // The settings file reloaded (by hand edits): read ships.json again too.
        internal static void Reload()
        {
            sFile = null;
            Match();
            Apply();
        }
    }
}
