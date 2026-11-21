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
using UnityEngine.ResourceManagement.AsyncOperations;

namespace TheExpanse
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
    //   across (X)   centred between the two side walls
    //   up (Y)       centred on the bay's height
    //   along (Z)    the end nearest the back wall sits a set distance in front of it
    //
    // Restoring a saved ship goes through the same spawn call: the saved parts go back to their saved positions, and
    // whatever is rebuilt is placed from the spawn point. So a restore must use the point the ship was saved with. That
    // is not recorded anywhere, but the saved parts show it: they sit either where the centred ship would be or where
    // the uncentred one would, and the spawn point is chosen to match.
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
            var root = Matrix4x4.TRS(spawn.pos, spawn.rot, Vector3.one);
            Bounds box = default;
            bool any = false;
            int modules = 0, unloaded = 0;

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
                Encapsulate(prefab, root * Matrix4x4.TRS(module.RigidTransform.pos, module.RigidTransform.rot, Vector3.one), ref box, ref any);
            }

            if (!any)
            {
                Plugin.Log.LogWarning($"[Centering] '{shipPreview.ShipName}': nothing to measure in its {modules} module(s); it spawns where the game puts it.");
                return spawn;
            }

            var delta = new Vector3(BayCenterX + Plugin.AcrossOffset.Value - box.center.x,
                                    Plugin.BayCenterY.Value - box.center.y,
                                    BackWallZ + Plugin.BackWallGap.Value - box.min.z);
            string size = $"estimated {box.size.x:0.0} x {box.size.y:0.0} x {box.size.z:0.0} m" + (unloaded > 0 ? $", {unloaded} module(s) could not be loaded and were left out" : "");

            // A saved ship: use the point it was saved with (see the note at the top).
            bool restoring = ModuleService.Instance != null && ModuleService.Instance.ShipSpawningFromSaveInProgress;
            if (restoring && SavedPartsCentre(out var saved, out int savedParts))
            {
                float asPosed = (saved - box.center).magnitude, centred = (saved - (box.center + delta)).magnitude;
                if (asPosed <= centred)
                {
                    Plugin.Log.LogInfo($"[Centering] '{shipPreview.ShipName}' (from a save, {savedParts} saved part(s)): {size}; it was saved where the game put it, so it is left there.");
                    return spawn;
                }
                Plugin.Log.LogInfo($"[Centering] '{shipPreview.ShipName}' (from a save, {savedParts} saved part(s)): {size}; it was saved centred, moved by ({delta.x:0.0}, {delta.y:0.0}, {delta.z:0.0}) to match.");
            }
            else
                Plugin.Log.LogInfo($"[Centering] '{shipPreview.ShipName}': {modules} module(s), {size}; moved by ({delta.x:0.0}, {delta.y:0.0}, {delta.z:0.0}) to centre it.");

            return new RigidTransform(spawn.rot, spawn.pos + (float3)delta);
        }

        // Average position of the ship parts a save has just brought back (their entities exist before the ship is rebuilt).
        static bool SavedPartsCentre(out Vector3 centre, out int count)
        {
            centre = default; count = 0;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;
            var em = world.EntityManager;
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<ShipMemberComponent>(), ComponentType.ReadOnly<LocalToWorld>());
            try
            {
                using var matrices = query.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
                if (matrices.Length == 0) return false;
                double x = 0, y = 0, z = 0;
                for (int i = 0; i < matrices.Length; i++) { var p = matrices[i].Position; x += p.x; y += p.y; z += p.z; }
                count = matrices.Length;
                centre = new Vector3((float)(x / count), (float)(y / count), (float)(z / count));
                return true;
            }
            finally { query.Dispose(); }
        }

        // Adds one module prefab's ship parts to the box, carried through the module's placement in the world. Only what
        // the finished ship is measured by counts: the visible meshes of its structure parts. Helper objects in a prefab
        // (room volumes, triggers, effects, placeholders) can be far larger than the ship and would throw the box off.
        static void Encapsulate(GameObject prefab, Matrix4x4 moduleToWorld, ref Bounds box, ref bool any)
        {
            var rootInverse = prefab.transform.worldToLocalMatrix;
            foreach (var part in prefab.GetComponentsInChildren<StructurePart>(true))
            {
                if (!ActiveUnder(part.transform, prefab.transform)) continue;
                bool hadMesh = false;
                foreach (var renderer in part.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!renderer.enabled || !ActiveUnder(renderer.transform, prefab.transform)) continue;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    AddBox(filter.sharedMesh.bounds, moduleToWorld * rootInverse * renderer.transform.localToWorldMatrix, ref box, ref any);
                    hadMesh = true;
                }
                // A part whose mesh only arrives when it is instantiated still counts, by where it is.
                if (!hadMesh) Add((moduleToWorld * rootInverse * part.transform.localToWorldMatrix).MultiplyPoint3x4(Vector3.zero), ref box, ref any);
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

        static void AddBox(Bounds local, Matrix4x4 toWorld, ref Bounds box, ref bool any)
        {
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
                Add(toWorld.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)), ref box, ref any);
        }

        static void Add(Vector3 point, ref Bounds box, ref bool any)
        {
            if (any) box.Encapsulate(point);
            else { box = new Bounds(point, Vector3.zero); any = true; }
        }
    }
}
