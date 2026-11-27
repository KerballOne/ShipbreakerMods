using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace BiggerBays
{
    // Places a ship in the middle of the bay instead of wherever its own origin puts it. Large custom ships are posed to
    // spawn up above the jacks, so that they are playable without this mod; with it, they are brought down into the bay
    // and the jacks are moved out of their way (JackMover).
    //
    // A ship is spawned at the bay's spawn point: ShipRandomizationHelper.SpawnShipAsync is handed that point and
    // instantiates every module of the ship's module group at (spawn point x the module's own transform). So the ship can
    // be put anywhere by changing the point it is handed, as long as that is done BEFORE it is built: rooms, door
    // openings, joints and entities are all created at the positions the parts have while spawning, and moving a finished
    // ship would leave those behind.
    //
    // That means the ship's size has to be known before it exists. It is worked out from the module group: each module's
    // prefab is loaded (it is about to be needed anyway) and the box around its structure parts - their visible meshes,
    // or just their positions where a mesh only arrives when the part is instantiated - is carried through the module's
    // transform. This is an estimate of the finished ship, good to within the size of its outermost parts.
    //
    // The bay, in world space (ships run along +Z from the back wall; +Y is up):
    //   across (X)   centered between the two side walls
    //   up (Y)       centered on the bay's height
    //   along (Z)    the end nearest the back wall sits a set distance in front of it
    //
    // With StraightenShip on, the ship is first turned square to the bay, about the middle of its box (see Straighten).
    //
    // Which ships: custom ones only (their modules come from a mod's bundle). The game's own ships are left exactly where
    // the game puts them.
    //
    // Restoring a saved ship goes through the same spawn call: the saved parts go back to their saved positions, and
    // whatever is rebuilt is placed from the spawn point. So a restore must use the point the ship was saved with. That
    // is not recorded anywhere, but the saved parts show it: the middle of where they are, against the middle of where
    // the ship's parts would be as posed, is roughly the shift the ship was saved with, and it is matched to "as posed"
    // or "centered", whichever is nearer. (Structure parts themselves come back at their saved positions whatever point
    // is used; the point only matters for what a save does not hold.)
    internal static class ShipCentering
    {
        // The stock work bay, from its geometry: the middle between the two side walls (their inner faces are at
        // x = -45.2 and x = +34.4), and the face of the back wall. There is only the one bay, so these are not settings.
        internal const float BayCenterX = -5.5f, BackWallZ = -17.9f;

        [ThreadStatic] static bool sInside;     // true while this class is itself calling the game's spawn method

        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(ShipRandomizationHelper), nameof(ShipRandomizationHelper.SpawnShipAsync));
            if (target == null)
            {
                Plugin.Log.LogError("[Centering] ShipRandomizationHelper.SpawnShipAsync not found; ships spawn where the game puts them.");
                return;
            }
            try { harmony.Patch(target, prefix: new HarmonyMethod(typeof(ShipCentering), nameof(Prefix))); }
            catch (Exception ex) { Plugin.Log.LogError("[Centering] could not hook ship spawning; ships spawn where the game puts them: " + ex.Message); }
        }

        static bool Prefix(ShipPreview shipPreview, AssetReferenceJointabilityAsset jointingInfoAssetRef, RigidTransform spawnTransform,
                           ShipRandomizationHelper.SpawnModuleGroupCallback onComplete, Action<float> loadingProgressCallback, ref Task<GameObject> __result)
        {
            // Noted for the bay's sizing, whether or not the ship is centered.
            if (!sInside && shipPreview != null)
                BayMemory.OnShipSpawning(shipPreview.ShipName, ModuleService.Instance != null && ModuleService.Instance.ShipSpawningFromSaveInProgress);
            if (sInside || !Plugin.Enabled.Value || !Plugin.CenterShip.Value || shipPreview?.ModuleGroup == null) return true;
            __result = SpawnCentered(shipPreview, jointingInfoAssetRef, spawnTransform, onComplete, loadingProgressCallback);
            return false;
        }

        static async Task<GameObject> SpawnCentered(ShipPreview shipPreview, AssetReferenceJointabilityAsset jointingInfoAssetRef, RigidTransform spawnTransform,
                                                    ShipRandomizationHelper.SpawnModuleGroupCallback onComplete, Action<float> loadingProgressCallback)
        {
            var held = new List<AsyncOperationHandle<GameObject>>();
            try
            {
                var target = spawnTransform;
                try { target = await Place(shipPreview, spawnTransform, held); }
                catch (Exception ex) { Plugin.Log.LogError("[Centering] could not work out where to put the ship; it spawns where the game puts it: " + ex); }

                Task<GameObject> spawn;
                sInside = true;
                try { spawn = ShipRandomizationHelper.SpawnShipAsync(shipPreview, jointingInfoAssetRef, target, onComplete, loadingProgressCallback); }
                finally { sInside = false; }
                return await spawn;
            }
            finally
            {
                // The prefabs were kept loaded so the spawn did not have to load them a second time.
                foreach (var h in held) if (h.IsValid()) Addressables.Release(h);
            }
        }

        // The spawn point that puts the ship's estimated box where the settings ask for it.
        static async Task<RigidTransform> Place(ShipPreview shipPreview, RigidTransform spawn, List<AsyncOperationHandle<GameObject>> held)
        {
            // Only custom ships are placed. The game's own ships stay exactly where the game puts them, so nothing scripted
            // around them (tutorial, story events) is disturbed.
            if (!IsCustom(shipPreview)) return spawn;       // a stock ship: nothing to do, nothing to log
            Centered = true;

            var root = Matrix4x4.TRS(spawn.pos, spawn.rot, Vector3.one);
            int modules = 0, unloaded = 0;
            var points = new List<Vector3>();           // every corner of every part's mesh box: the ship's outline
            var partPositions = new List<Vector3>();
            var centers = new List<Vector3>();          // the middle of each mesh: where a part is, whatever its pivot

            foreach (var module in shipPreview.ModuleGroup.AllModules)
            {
                if (module == null) continue;
                GameObject? prefab = module.ModuleDefinition != null ? module.ModuleDefinition.gameObject : null;
                if (prefab == null && module.IsValid)
                {
                    var handle = Addressables.LoadAssetAsync<GameObject>(module.ModulePrefabRef);
                    held.Add(handle);
                    while (!handle.IsDone) await Task.Yield();
                    if (handle.Status == AsyncOperationStatus.Succeeded) prefab = handle.Result;
                }
                if (prefab == null) { unloaded++; continue; }
                modules++;
                Encapsulate(prefab, root * Matrix4x4.TRS(module.RigidTransform.pos, module.RigidTransform.rot, Vector3.one), points, partPositions, centers);
            }

            if (points.Count == 0)
            {
                Plugin.Log.LogWarning($"[Centering] '{shipPreview.ShipName}': nothing to measure in its {modules} module(s); it spawns where the game puts it.");
                return spawn;
            }

            // As posed, and centered without being turned (what versions before StraightenShip did).
            var posedBox = BoxOf(points, Matrix4x4.identity);
            var plainDelta = DeltaFor(posedBox);

            // Turned square to the bay first, if asked for: about the middle of its box, then centered like any other ship.
            var turn = Quaternion.identity;
            string turned = "";
            if (Plugin.StraightenShip.Value)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try { turn = Straighten(points, centers, out turned); }
                catch (Exception ex) { turn = Quaternion.identity; turned = ""; Plugin.Log.LogError("[Centering] could not straighten the ship; it keeps the angle it is posed at: " + ex); }
                if (turned.Length > 0) turned += $", worked out in {watch.ElapsedMilliseconds} ms";
            }
            bool isTurned = Quaternion.Angle(Quaternion.identity, turn) > 0f;
            var turnMatrix = Matrix4x4.TRS(posedBox.center, turn, Vector3.one) * Matrix4x4.Translate(-posedBox.center);
            var box = isTurned ? BoxOf(points, turnMatrix) : posedBox;
            var delta = DeltaFor(box);
            var turnedSpawn = isTurned
                ? new RigidTransform(turn * (Quaternion)spawn.rot, turnMatrix.MultiplyPoint3x4(spawn.pos))
                : spawn;
            var centeredSpawn = new RigidTransform(turnedSpawn.rot, turnedSpawn.pos + (float3)delta);

            string size = $"estimated {box.size.x:0.0} x {box.size.y:0.0} x {box.size.z:0.0} m" + turned + (unloaded > 0 ? $", {unloaded} module(s) could not be loaded and were left out" : "");

            // A saved ship: use the shift it was saved with (see the note at the top).
            bool restoring = ModuleService.Instance != null && ModuleService.Instance.ShipSpawningFromSaveInProgress;
            if (restoring && isTurned && partPositions.Count > 0 && SavedPartPositions(out var savedParts))
            {
                // Three places it can have been saved: as posed, centered, or straightened and centered. A turn barely moves
                // the middle of the parts, so they are told apart by how closely the saved parts sit to where each one
                // would put the ship's parts.
                float posedFit = Fit(partPositions, Matrix4x4.identity, savedParts);
                float plainFit = Fit(partPositions, Matrix4x4.Translate(plainDelta), savedParts);
                float turnedFit = Fit(partPositions, Matrix4x4.Translate(delta) * turnMatrix, savedParts);
                string from = $"'{shipPreview.ShipName}' (from a save, {savedParts.Count} saved part(s)): {size}; saved parts are within {posedFit:0.0} m of the ship as posed, {plainFit:0.0} m of it centered, {turnedFit:0.0} m of it straightened and centered";
                if (posedFit <= plainFit && posedFit <= turnedFit)
                {
                    Plugin.Log.LogInfo($"[Centering] {from}; it was saved where the game put it, so it is left there.");
                    PlacedBox = posedBox;
                    return spawn;
                }
                if (plainFit <= turnedFit)
                {
                    Plugin.Log.LogInfo($"[Centering] {from}; it was saved centered but not straightened, moved by ({plainDelta.x:0.0}, {plainDelta.y:0.0}, {plainDelta.z:0.0}) to match.");
                    PlacedBox = Shifted(posedBox, plainDelta);
                    return new RigidTransform(spawn.rot, spawn.pos + (float3)plainDelta);
                }
                Plugin.Log.LogInfo($"[Centering] {from}; it was saved straightened and centered, placed to match.");
            }
            else if (restoring && partPositions.Count > 0 && SavedPartPositions(out var saved))
            {
                var savedShift = Median(saved) - Median(partPositions);
                float asPosed = savedShift.magnitude, centered = (savedShift - delta).magnitude;
                string from = $"'{shipPreview.ShipName}' (from a save, {saved.Count} saved part(s)): {size}; saved parts sit ({savedShift.x:0.0}, {savedShift.y:0.0}, {savedShift.z:0.0}) from where the ship is posed";
                if (Mathf.Min(asPosed, centered) > Mathf.Max(FarFromBothMeters, delta.magnitude))
                {
                    // Clearly neither: it was saved under different settings. Follow the saved parts.
                    Plugin.Log.LogInfo($"[Centering] {from}; rebuilt there to match.");
                    PlacedBox = Shifted(posedBox, savedShift);
                    return new RigidTransform(spawn.rot, spawn.pos + (float3)savedShift);
                }
                if (asPosed <= centered)
                {
                    Plugin.Log.LogInfo($"[Centering] {from}; it was saved where the game put it, so it is left there.");
                    PlacedBox = posedBox;
                    return spawn;
                }
                Plugin.Log.LogInfo($"[Centering] {from}; it was saved centered, moved by ({delta.x:0.0}, {delta.y:0.0}, {delta.z:0.0}) to match.");
            }
            else
                Plugin.Log.LogInfo($"[Centering] '{shipPreview.ShipName}': {modules} module(s), {size}; moved by ({delta.x:0.0}, {delta.y:0.0}, {delta.z:0.0}) to center it.");

            PlacedBox = Shifted(box, delta);
            return centeredSpawn;
        }

        static Bounds Shifted(Bounds b, Vector3 by) => new Bounds(b.center + by, b.size);

        // The shift that puts a box in the middle of the bay, its rear end the set distance in front of the back wall.
        static Vector3 DeltaFor(Bounds box) =>
            new Vector3(BayCenterX - box.center.x,
                        Plugin.BayCenterY.Value - box.center.y,
                        BackWallZ + Plugin.BackWallGap.Value - box.min.z);

        static Bounds BoxOf(List<Vector3> points, Matrix4x4 through)
        {
            Vector3 min = through.MultiplyPoint3x4(points[0]), max = min;
            for (int i = 1; i < points.Count; i++)
            {
                var p = through.MultiplyPoint3x4(points[i]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var box = new Bounds();
            box.SetMinMax(min, max);
            return box;
        }

        // ---- straightening -------------------------------------------------------------------------------------------
        //
        // There is no record of which way a ship "should" face, so its own shape is the reference: the tightest box that
        // can be drawn around it, at any angle, has its sides along the ship's length, width and height. The ship is then
        // turned by the smallest amount that lays those sides along the bay's axes.
        //
        // That only means something for a side that is clearly longer or shorter than the others. On a ship that is about
        // as wide as it is long, the tightest box can sit at almost any angle. So an axis is only trusted when its length
        // differs from its neighbor's by StraightenRatio:
        //   long and short axes both clear   the ship is squared up completely
        //   only one of them clear           that axis is laid along the nearest bay axis; the ship is not rolled about it
        //   neither                          the ship keeps the angle it is posed at
        //
        // Only the outline counts (like centering), so a ship with more parts on one side is not pulled off true.

        // The turn (about any point) that squares the ship to the bay; identity when it is left alone.
        static Quaternion Straighten(List<Vector3> points, List<Vector3> centers, out string what)
        {
            what = "";
            float ratio = Mathf.Max(1.01f, Plugin.StraightenRatio.Value);
            var outline = Outline(points);

            // Coarse search over every angle a box can sit at (its sides repeat every 90 degrees), then close in.
            var best = Quaternion.identity;
            float bestVolume = Volume(outline, best);
            for (int x = -45; x <= 45; x += 9)
            for (int y = -45; y <= 45; y += 9)
            for (int z = -45; z <= 45; z += 9)
            {
                var q = Quaternion.Euler(x, y, z);
                float v = Volume(outline, q);
                if (v < bestVolume) { bestVolume = v; best = q; }
            }
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            foreach (float step in new[] { 4f, 2f, 1f, 0.5f, 0.25f, 0.1f, 0.05f, 0.02f, 0.01f })
            {
                bool better = true;
                for (int guard = 0; better && guard < 64; guard++)
                {
                    better = false;
                    foreach (var axis in axes)
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        var q = Quaternion.AngleAxis(sign * step, axis) * best;
                        float v = Volume(outline, q);
                        if (v < bestVolume) { bestVolume = v; best = q; better = true; }
                    }
                }
            }

            // With 'best' applied the ship lies along the world axes; its sides, longest first.
            var size = BoxOf(points, Matrix4x4.Rotate(best)).size;
            int longest = 0, shortest = 0;
            for (int a = 1; a < 3; a++)
            {
                if (size[a] > size[longest]) longest = a;
                if (size[a] < size[shortest]) shortest = a;
            }
            if (longest == shortest) return Quaternion.identity;        // a perfect cube
            int middle = 3 - longest - shortest;
            bool longClear = size[longest] >= size[middle] * ratio;
            bool shortClear = size[middle] >= Mathf.Max(size[shortest], 0.01f) * ratio;

            Quaternion turn;
            string how = "";
            if (longClear && shortClear)
            {
                // Any quarter-turn of 'best' squares the ship equally well; take the one that turns it least.
                turn = best;
                float least = Quaternion.Angle(Quaternion.identity, best);
                foreach (var forward in SixAxes)
                foreach (var up in SixAxes)
                {
                    if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.5f) continue;
                    var q = Quaternion.LookRotation(forward, up) * best;
                    float angle = Quaternion.Angle(Quaternion.identity, q);
                    if (angle < least) { least = angle; turn = q; }
                }
            }
            else if (longClear || shortClear)
            {
                // One clear axis: where it points now, and the bay axis nearest to that.
                var now = Quaternion.Inverse(best) * axes[longClear ? longest : shortest];
                int nearest = 0;
                for (int a = 1; a < 3; a++) if (Mathf.Abs(now[a]) > Mathf.Abs(now[nearest])) nearest = a;
                var bay = Vector3.zero;
                bay[nearest] = Mathf.Sign(now[nearest]);
                turn = Quaternion.FromToRotation(now, bay);
                how = longClear ? " (long axis only)" : " (short axis only)";

                // The box says nothing about the roll about that axis, but a ship built in two mirrored halves does.
                if (MirrorRoll(centers, turn, nearest, out var roll, out string mirror))
                {
                    turn = roll * turn;
                    how = " (roll from its mirror symmetry: " + mirror + ")";
                }
                else how = how.TrimEnd(')') + "; no roll: " + mirror + ")";
            }
            else
            {
                what = $", too close to square to straighten (tightest box {size[longest]:0.0} x {size[middle]:0.0} x {size[shortest]:0.0} m)";
                return Quaternion.identity;
            }

            float degrees = Quaternion.Angle(Quaternion.identity, turn);
            if (degrees < 0.05f) { what = ", already straight"; return Quaternion.identity; }
            what = $", straightened by {degrees:0.0} degrees" + how;
            return turn;
        }

        // The roll about one bay axis that stands a mirror-symmetric ship upright. Most ships are built as two mirrored
        // halves; the plane between them runs along the ship, and the ship is already laid along 'axis' by 'turn'.
        //
        // Trying planes one after another does not work: mirrored parts only land on their twins when the plane is
        // within a degree or so of the right one, so there is nothing to home in on from further away, and every plane
        // would have to be tried. Instead the parts point the plane out themselves. A part and its twin sit straight
        // across the plane from each other, so the line between them gives the plane's direction and their midpoint
        // lies on it. Every pair of parts (from a sample) casts a vote for the plane it would be twins across. Pairs
        // that are not twins vote all over the place; the real twins all vote for the same plane, and it wins by a
        // wide margin. Only planes that run along the ship are counted - any angle about its length, up to a few
        // degrees of lean (the box only finds the length to within a degree or so), a few meters off its middle.
        //
        // The winner is then closed in on by mirroring the parts in it and nudging it until they land best. The turn
        // returned stands that plane on the nearest bay axis. False when no plane stands out (a ship that is not
        // symmetric, or is equally so at every angle).
        static bool MirrorRoll(List<Vector3> centers, Quaternion turn, int axis, out Quaternion roll, out string what)
        {
            roll = Quaternion.identity;
            what = "too few parts to look for a mirror plane";
            if (centers.Count < 50) return false;

            int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;       // the plane across the ship; a1 x a2 = axis
            Vector3 e1 = Vector3.zero, e2 = Vector3.zero, along = Vector3.zero;
            e1[a1] = 1f; e2[a2] = 1f; along[axis] = 1f;

            // The parts, turned, in a grid of cells so the part nearest to a point is quick to find.
            const float Cell = 2f, Twin = 0.5f;
            var parts = new List<Vector3>(centers.Count);
            Vector3 min = turn * centers[0], max = min;
            foreach (var c in centers)
            {
                var p = turn * c;
                parts.Add(p);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var middle = (min + max) * 0.5f;
            int nx = Mathf.FloorToInt((max.x - min.x) / Cell) + 1, ny = Mathf.FloorToInt((max.y - min.y) / Cell) + 1, nz = Mathf.FloorToInt((max.z - min.z) / Cell) + 1;
            if ((long)nx * ny * nz > 4000000) { what = "too large to look for a mirror plane"; return false; }
            var cells = new List<Vector3>?[nx * ny * nz];
            foreach (var p in parts)
            {
                int i = (Mathf.FloorToInt((p.x - min.x) / Cell) * ny + Mathf.FloorToInt((p.y - min.y) / Cell)) * nz + Mathf.FloorToInt((p.z - min.z) / Cell);
                (cells[i] ??= new List<Vector3>()).Add(p);
            }

            // For the plane whose normal is 'degrees' from a1 towards a2, leaning 'lean' degrees towards the ship's length,
            // passing 'offset' meters from the middle of the ship: how far a mirrored part typically lands from the nearest
            // part (anything past a cell counts as a cell, so stray parts do not swamp the rest), and the share of parts
            // that land right on a twin.
            float Miss(float degrees, float offset, float lean, int samples, out float twins)
            {
                var normal = Normal(degrees, lean);
                var on = middle + normal * offset;
                int sampleEvery = Mathf.Max(1, parts.Count / samples);
                float total = 0f;
                int n = 0, twinned = 0;
                for (int i = 0; i < parts.Count; i += sampleEvery)
                {
                    var p = parts[i];
                    p -= 2f * Vector3.Dot(p - on, normal) * normal;
                    int cx = Mathf.FloorToInt((p.x - min.x) / Cell), cy = Mathf.FloorToInt((p.y - min.y) / Cell), cz = Mathf.FloorToInt((p.z - min.z) / Cell);
                    float least = Cell * Cell;
                    for (int x = cx - 1; x <= cx + 1; x++)
                    {
                        if (x < 0 || x >= nx) continue;
                        for (int y = cy - 1; y <= cy + 1; y++)
                        {
                            if (y < 0 || y >= ny) continue;
                            for (int z = cz - 1; z <= cz + 1; z++)
                            {
                                if (z < 0 || z >= nz) continue;
                                var list = cells[(x * ny + y) * nz + z];
                                if (list == null) continue;
                                for (int k = 0; k < list.Count; k++)
                                {
                                    float d = (list[k] - p).sqrMagnitude;
                                    if (d < least) least = d;
                                }
                            }
                        }
                    }
                    if (least <= Twin * Twin) twinned++;
                    total += Mathf.Sqrt(least);
                    n++;
                }
                twins = (float)twinned / n;
                return total / n;
            }
            Vector3 Normal(float degrees, float lean)
            {
                float r = degrees * Mathf.Deg2Rad, l = lean * Mathf.Deg2Rad;
                return Mathf.Cos(l) * (Mathf.Cos(r) * e1 + Mathf.Sin(r) * e2) + Mathf.Sin(l) * along;
            }

            // The vote. Planes are counted in steps of 1 degree about the ship's length, half a degree of lean (up to
            // MaxLean either way) and half a meter from the ship's middle (up to MaxOffset either way).
            const float MaxLean = 3f, MaxOffset = 4f;
            const int Angles = 180, Leans = 12, Offsets = 16;
            var votes = new int[Angles * Leans * Offsets];
            float leanLimit = Mathf.Sin((MaxLean + 0.5f) * Mathf.Deg2Rad);
            int voterEvery = Mathf.Max(1, parts.Count / 400);
            for (int i = 0; i < parts.Count; i += voterEvery)
            {
                var p = parts[i];
                for (int j = 0; j < parts.Count; j++)
                {
                    var d = parts[j] - p;
                    float length = d.magnitude;
                    if (length < 1f) continue;                      // too close together to give a direction
                    float lengthways = Vector3.Dot(d, along);
                    if (Mathf.Abs(lengthways) > length * leanLimit) continue;   // not across the ship
                    float c1 = Vector3.Dot(d, e1), c2 = Vector3.Dot(d, e2);
                    float degrees = Mathf.Atan2(c2, c1) * Mathf.Rad2Deg;
                    if (degrees < 0f) { degrees += 180f; lengthways = -lengthways; }     // the same plane, seen from its other side
                    if (degrees >= 180f) { degrees -= 180f; lengthways = -lengthways; }
                    float lean = Mathf.Asin(Mathf.Clamp(lengthways / length, -1f, 1f)) * Mathf.Rad2Deg;
                    float offset = Vector3.Dot((parts[j] + p) * 0.5f - middle, Normal(degrees, lean));
                    int a = Mathf.Min(Angles - 1, (int)degrees);
                    int l = Mathf.FloorToInt((lean + MaxLean) / 0.5f), o = Mathf.FloorToInt((offset + MaxOffset) / 0.5f);
                    if (l < 0 || l >= Leans || o < 0 || o >= Offsets) continue;
                    votes[(a * Leans + l) * Offsets + o]++;
                }
            }
            int winner = 0;
            for (int i = 1; i < votes.Length; i++) if (votes[i] > votes[winner]) winner = i;
            if (votes[winner] < 5) { what = "no mirror plane found"; return false; }
            float best = winner / (Leans * Offsets) + 0.5f;
            float bestLean = (winner / Offsets % Leans + 0.5f) * 0.5f - MaxLean;
            float bestOffset = (winner % Offsets + 0.5f) * 0.5f - MaxOffset;

            // Close in on it.
            float bestMiss = Miss(best, bestOffset, bestLean, 400, out _);
            foreach (float step in new[] { 1f, 0.5f, 0.25f, 0.1f, 0.05f, 0.02f })
            {
                bool better = true;
                for (int guard = 0; better && guard < 32; guard++)
                {
                    better = false;
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        float m = Miss(best + sign * step, bestOffset, bestLean, 400, out _);
                        if (m < bestMiss) { bestMiss = m; best += sign * step; better = true; }
                        m = Miss(best, bestOffset + sign * step * 0.25f, bestLean, 400, out _);
                        if (m < bestMiss) { bestMiss = m; bestOffset += sign * step * 0.25f; better = true; }
                        m = Miss(best, bestOffset, bestLean + sign * step * 0.5f, 400, out _);
                        if (m < bestMiss) { bestMiss = m; bestLean += sign * step * 0.5f; better = true; }
                    }
                }
            }

            // It has to stand out: a good share of the parts have a twin across it, and far fewer do at other angles.
            Miss(best, bestOffset, bestLean, 1000, out float twins);
            float elsewhere = 0f;
            foreach (float other in new[] { 30f, 45f, 60f })
            {
                Miss(best + other, bestOffset, bestLean, 1000, out float t);
                elsewhere = Mathf.Max(elsewhere, t);
            }

            // Stand the plane on the nearest of the two bay axes across the ship.
            var normal = Normal(best, bestLean);
            var bay = Mathf.Abs(Vector3.Dot(normal, e1)) >= Mathf.Abs(Vector3.Dot(normal, e2)) ? e1 * Mathf.Sign(Vector3.Dot(normal, e1)) : e2 * Mathf.Sign(Vector3.Dot(normal, e2));
            var stand = Quaternion.FromToRotation(normal, bay);
            what = $"mirror plane {Quaternion.Angle(Quaternion.identity, stand):0.0} degrees off the bay's and {bestOffset:0.0} m off the ship's middle, {twins * 100f:0}% of parts have a twin across it against {elsewhere * 100f:0}% at other angles";
            if (twins < 0.35f || twins < elsewhere * 2f) return false;

            roll = stand;
            return true;
        }

        static readonly Vector3[] SixAxes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        // The points that stick out furthest in a few hundred directions. A box around these is the box around them all,
        // near enough, and the search above has far fewer points to turn.
        static List<Vector3> Outline(List<Vector3> points)
        {
            const int Directions = 300;
            if (points.Count <= Directions * 2) return points;
            var keep = new HashSet<int>();
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int d = 0; d < Directions; d++)
            {
                float y = 1f - 2f * (d + 0.5f) / Directions, r = Mathf.Sqrt(1f - y * y), a = golden * d;
                float dx = Mathf.Cos(a) * r, dz = Mathf.Sin(a) * r;
                int furthest = 0;
                float most = float.NegativeInfinity;
                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    float along = p.x * dx + p.y * y + p.z * dz;
                    if (along > most) { most = along; furthest = i; }
                }
                keep.Add(furthest);
            }
            var outline = new List<Vector3>(keep.Count);
            foreach (int i in keep) outline.Add(points[i]);
            return outline;
        }

        static float Volume(List<Vector3> points, Quaternion turn)
        {
            Vector3 min = turn * points[0], max = min;
            for (int i = 1; i < points.Count; i++)
            {
                var p = turn * points[i];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var size = max - min;
            return size.x * size.y * size.z;
        }

        // How closely the saved parts sit to where a placement would put the ship's parts: the typical distance from a
        // part's place to the nearest saved part. Parts that have been cut away or have drifted do not matter while most
        // of the ship is still where it was.
        static float Fit(List<Vector3> partPositions, Matrix4x4 placement, List<Vector3> saved)
        {
            int stride = Mathf.Max(1, partPositions.Count / 200);
            var nearest = new List<float>();
            for (int i = 0; i < partPositions.Count; i += stride)
            {
                var p = placement.MultiplyPoint3x4(partPositions[i]);
                float least = float.PositiveInfinity;
                for (int k = 0; k < saved.Count; k++)
                {
                    float d = (saved[k] - p).sqrMagnitude;
                    if (d < least) least = d;
                }
                nearest.Add(Mathf.Sqrt(least));
            }
            nearest.Sort();
            return nearest[nearest.Count / 2];
        }

        // True once the ship now loading has been taken on by this class; the jacks are only moved for those ships.
        internal static bool Centered;

        // Where the whole ship's estimated box was put (null when it was not worked out). A ship loaded from a save is
        // sized by this rather than by measuring its parts, which may have been cut loose and scattered anywhere.
        internal static Bounds? PlacedBox;

        // A custom ship is one whose modules come out of a mod's bundle, i.e. a bundle file under BepInEx\plugins.
        static bool IsCustom(ShipPreview shipPreview)
        {
            string plugins = BepInEx.Paths.PluginPath.Replace('/', '\\').TrimEnd('\\');
            foreach (var module in shipPreview.ModuleGroup.AllModules)
            {
                string guid = module?.ModulePrefabRef?.AssetGUID ?? "";
                if (guid.Length == 0) continue;
                foreach (var locator in Addressables.ResourceLocators)
                {
                    // Real catalogs only: other mods' own locators can be slow to answer (see FreePlayPlus's).
                    if (!(locator is ResourceLocationMap) || !locator.Locate(guid, null, out var locations) || locations == null) continue;
                    foreach (var location in locations)
                    {
                        if (!location.HasDependencies) continue;
                        foreach (var bundle in location.Dependencies)
                            if (!string.IsNullOrEmpty(bundle.InternalId) &&
                                bundle.InternalId.Replace('/', '\\').StartsWith(plugins, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }

        // The shift read off the saved parts is rough: a save holds more than the parts the estimate counts, and parts
        // move during play (it read 5.6 m out on an intact ship). So it is only used to tell "as posed" from "centered",
        // whichever is nearer, and is followed directly only when it is far from both.
        const float FarFromBothMeters = 10f;

        // Positions of the ship parts a save has just brought back (their entities exist before the ship is rebuilt).
        static bool SavedPartPositions(out List<Vector3> positions)
        {
            positions = new List<Vector3>();
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;
            var em = world.EntityManager;
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<ShipMemberComponent>(), ComponentType.ReadOnly<LocalToWorld>());
            try
            {
                using var matrices = query.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
                for (int i = 0; i < matrices.Length; i++) positions.Add(matrices[i].Position);
                return positions.Count > 0;
            }
            finally { query.Dispose(); }
        }

        // The middle value on each axis: unlike the average, a few parts far from the rest do not move it.
        static Vector3 Median(List<Vector3> points)
        {
            var axis = new float[points.Count];
            var m = Vector3.zero;
            for (int a = 0; a < 3; a++)
            {
                for (int i = 0; i < points.Count; i++) axis[i] = points[i][a];
                Array.Sort(axis);
                m[a] = axis[axis.Length / 2];
            }
            return m;
        }

        // Adds one module prefab's ship parts to the box, carried through the module's placement in the world. Only what
        // the finished ship is measured by counts: the visible meshes of its structure parts. Helper objects in a prefab
        // (room volumes, triggers, effects, placeholders) can be far larger than the ship and would throw the box off.
        static void Encapsulate(GameObject prefab, Matrix4x4 moduleToWorld, List<Vector3> points, List<Vector3> partPositions, List<Vector3> centers)
        {
            var rootInverse = prefab.transform.worldToLocalMatrix;
            foreach (var part in prefab.GetComponentsInChildren<StructurePart>(true))
            {
                if (!ActiveUnder(part.transform, prefab.transform)) continue;
                partPositions.Add((moduleToWorld * rootInverse * part.transform.localToWorldMatrix).MultiplyPoint3x4(Vector3.zero));
                bool hadMesh = false;
                foreach (var renderer in part.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!renderer.enabled || !ActiveUnder(renderer.transform, prefab.transform)) continue;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    AddBox(filter.sharedMesh.bounds, moduleToWorld * rootInverse * renderer.transform.localToWorldMatrix, points);
                    centers.Add((moduleToWorld * rootInverse * renderer.transform.localToWorldMatrix).MultiplyPoint3x4(filter.sharedMesh.bounds.center));
                    hadMesh = true;
                }
                // A part whose mesh only arrives when it is instantiated still counts, by where it is.
                if (!hadMesh)
                {
                    var at = (moduleToWorld * rootInverse * part.transform.localToWorldMatrix).MultiplyPoint3x4(Vector3.zero);
                    points.Add(at);
                    centers.Add(at);
                }
            }
        }

        // A prefab is not in a scene, so "active" has to be read off each object up to the prefab's root.
        static bool ActiveUnder(Transform t, Transform root)
        {
            for (var p = t; p != null; p = p.parent)
            {
                if (!p.gameObject.activeSelf) return false;
                if (p == root) break;
            }
            return true;
        }

        static void AddBox(Bounds local, Matrix4x4 toWorld, List<Vector3> points)
        {
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
                points.Add(toWorld.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
        }
    }
}
