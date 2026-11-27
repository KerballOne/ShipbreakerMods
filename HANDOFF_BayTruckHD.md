# Handoff: show the full-res Space Truck in the salvage bay

Written 2026-11-27 in the ShipbreakerShipbuilder session, for a ShipbreakerMods session. No code has been written yet.

## Goal
Lou's Space Truck, docked on the claw outside the bay, is a low-poly mesh. The game has a full-res model, but it only shows on the Hab's Space Truck restoration screen. Show the full-res truck on the claw instead of the low-poly one.

## Open question for the user
Make it a new mod (working name `BayTruckHD`), or add it to `BayLights`? Ask before creating the project. Whichever it is, it gets its own code (no shared files between mods).

## Facts (read from the game's bundles with UnityPy)

### The bay truck (low-poly)
- It is only in the **small bay** scene, in `scenes-gameplay_scenes_all_*.bundle` (scene CAB-b564305bec46b2eb14e0b61149418d31).
- Hierarchy (world-root object; local positions):
  ```
  PRF_SpaceTruckAndDockingArm   pos (403.50, 444.43, -62.00)  rotY quat (0,-0.384,0,0.923) ≈ -45°  scale 2
    Scripting/OnShiftStart                       (MonoBehaviour)
    GRP_ClawAndTruck
      SM_Claw          pos (1.13, 6.29, -7.34)    mesh SM_Claw
      SM_DockingArm    pos (-5.50, 12.68, -14.44) mesh SM_DockingArm
      SM_SpaceTruck    pos (1.82, 3.01, -7.42)    rot identity, scale 1, mesh SM_SpaceTruck
      SpaceTruckFillLight  pos (-18.55, -11.00, 2.41)  (Light)
  ```
- The claw and the arm are **separate objects**, so turning off `SM_SpaceTruck`'s renderer leaves the claw.
- Each of the three meshes also has a MonoBehaviour of unknown type. Check what it is before touching the object.
- `SM_SpaceTruck` mesh bounds: centre 0, half-extent **(13.78, 3.17, 5.49)**. X is the long side.
- In Act 3, the trigger `PRF_CMP_Scripting_SmBay/OnShiftStart/PATCon Trigger - Scenes/Act 3/PRF_CMP_SmBay_A3_SC01_CrewInDisarray/Hide Space Truck` hides the truck. We don't know yet which object it turns off. The new truck must disappear when it fires.
- The prefab itself (`Assets/Content/Art/Structures/Kitbash/Bay/Models/PRF_SpaceTruckAndDockingArm.prefab`) is in `portmorrigan_assets_all_*.bundle`.

### The full-res truck
- Addressable `Assets/Content/Art/Ships/Kitbash/LynxTruck/Prefabs/MAS_LynxTruck_Base.prefab`, GUID **`11b26986c9545a543955bca2ac08e05b`**, in its own bundle `hab-spacetruck_assets_all_*.bundle` (6 MB). The bundle also holds the `SpaceTruckSectionAsset`s.
- Prefab hierarchy:
  ```
  MAS_LynxTruck_Base           pos (6.44,-2.35,0.79) in the prefab asset; (0,0,0) where the Hab places it
    SM_LynxTruck_Base          pos (0.01,-0.04,-0.06)  rot quat (0,0.301,0,-0.954) ≈ 35° about Y  scale 0.1
    SM_LynxTruck_Wireframe     (wireframe copy for the restoration screen)
    SpaceTruckComponents/PRF_SpaceTruckComponents_* (16 interior parts: Engine, Thrusters, Arms, Bunny, ...)
  ```
- `SM_LynxTruck_Base` mesh bounds: centre 0, half-extent **(13.93, 5.21, 6.08)**. The length matches `SM_SpaceTruck` (13.78), so it fits at **scale 1**. It's taller because it has wheels, an antenna and handles.
- The Hab places it at `Root/Space Truck Turntable/MAS_LynxTruck_Base` in the Hab scene. The rig is `Hab Container/PRF_Hab_Interior/PRF_HAB_SpaceTruck`.
- `SpaceTruckController` (in `C:\Users\user\.claude\decomp\bbi_full.decompiled.cs` around line 42640) tints the truck, the wireframe and the parts with MaterialPropertyBlocks (`_BaseColor`, `_Color`, `_V_WIRE_Color`). Its starting colours are `Color.clear`. Our copy has no controller, so it shows the material's own colours. That looked fine in a Unity editor preview of the prefab.

## Plan
1. **Hook:** when the small bay scene is loaded, find `PRF_SpaceTruckAndDockingArm/GRP_ClawAndTruck/SM_SpaceTruck`. Look it up by path with `GameObject.Find` or a walk from the scene roots. Retry for a few frames if it isn't loaded yet.
   - Follow the known Harmony traps: don't patch `Update` methods directly, and a `TargetMethod` that throws aborts every patch in the plugin.
2. **Load:** `Addressables.LoadAssetAsync<GameObject>("11b26986c9545a543955bca2ac08e05b")`.
   - If the GUID key is ambiguous, use `LoadResourceLocationsAsync(key, typeof(GameObject))` first.
   - Keep the handle; don't release it while the copy is alive.
3. **Create:** make a copy as a child of `GRP_ClawAndTruck`.
   - Give the copy root `SM_SpaceTruck`'s local position, rotation and scale.
   - Set its child `SM_LynxTruck_Base` to local position 0, no rotation, scale 1.
4. **Strip:** turn off `SM_LynxTruck_Wireframe` and `SpaceTruckComponents`.
   - Remove colliders from the copy if it has any. It's set dressing, and the arm is 400+ m out, but don't add physics.
5. **Hide the old truck:** set `SM_SpaceTruck`'s `MeshRenderer.enabled = false`.
   - Don't deactivate the object: the Act 3 trigger or its MonoBehaviour may expect it active.
   - Static batching: per NewtonianPhysics notes, `Instantiate` doesn't escape static batching on 2020.3.35. Turning off the renderer still works on batched renderers.
6. **Act 3:** the copy sits under `GRP_ClawAndTruck`. If "Hide Space Truck" turns off that group or the arm root, the copy goes with it. If it turns off only `SM_SpaceTruck`, mirror that state onto the copy, for example by checking `SM_SpaceTruck.activeInHierarchy` each frame or from a small helper component.
7. **Config:** an on/off toggle. Offsets for position, rotation and scale make in-game tuning easier, with a config watcher for live changes.

## Unknowns to settle in-game (log them; keep the logging until the user confirms)
- **Facing:** the full-res model may point the other way along X. If so, the fix is 180° about Y.
- **Height:** it may need a small Y offset so the claw grips the roof instead of cutting into it. Compare top-of-mesh Y: `SM_SpaceTruck` 3.01+3.17 = 6.18 vs full-res 3.01+5.21 = 8.22 in arm space. The claw is at Y 6.29, so the full-res roof probably needs to drop about 2 units, about 4 m in the world because the arm has scale 2. Antenna and handles may poke through the claw.
- **Materials:** whether the HDRP materials render correctly outside the Hab, and whether emissive needs a power state.
- **Hide Space Truck:** what this trigger turns off. Log it.
- **Big bay:** whether it has its own docking arm. None was found in the scene scan, which only found the small-bay instance.

## Tools in ShipbreakerShipbuilder/.claude/
- `find_truck_users.py <asset-name>`: looks up an asset's GUID and bundle dependencies in the catalog.
- `truck_bounds.py <bundles...>`: transforms and mesh AABBs of the truck objects.
- `truck_arm_tree.py <bundles...>`: dumps the docking-arm hierarchy.
- `truck_scene_paths.py <scene bundle>`: scene paths of truck objects.
