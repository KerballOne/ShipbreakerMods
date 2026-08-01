using System.Linq;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NewtonianPhysics
{
    // Whole-scene renderer scan, F9-bound - originally built to find an unidentified "spire" shaped
    // flat billboard (turned out to be VillageWaystation - see project_flatcard_billboard_investigation
    // in memory), kept around as a general-purpose diagnostic since it's useful any time a distant
    // object needs identifying by name/path/bounds/angle. Queries EVERY Renderer type (not just
    // MeshFilter) so it doesn't repeat the earlier FlatCardDiagnostic scan's mistake of silently
    // missing SpriteRenderer objects. Gated behind Plugin.ConfigDebugPrint - F9 does nothing while
    // that's off, same as every other debug log line in this mod, so this key is inert by default
    // and doesn't need its own separate config toggle.
    internal static class SceneDiagnostic
    {
        // Matches the original FlatCardDiagnostic bands (see project_flatcard_billboard_investigation
        // in memory): >1000m from the bay excludes near-bay interior clutter (crate panels, cockpit
        // tiles, ship interiors - confirmed to flood the results otherwise), 10m-20000m from the
        // player excludes player-attached equipment (helmet visor, grapple gun FX, cutting tool -
        // these sit at effectively 0 distance from the player and otherwise dominate the front of
        // any distance-sorted list).
        private const float MinDistFromBayMeters = 1000f;
        private const float MinDistFromPlayerMeters = 10f;
        private const float MaxDistFromPlayerMeters = 20000f;

        public static void Tick()
        {
            if (!Plugin.ConfigDebugPrint.Value)
                return;

            if (!Input.GetKeyDown(KeyCode.F9))
                return;

            Transform? player = LynxCameraController.MainCameraTransform;
            if (player == null)
            {
                Plugin.Log.LogInfo("SceneDiagnostic: no player camera transform found.");
                return;
            }

            Transform? bayRoot = null;
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t != null && t.parent == null && t.name == BackgroundConstants.BayRootObjectName)
                {
                    bayRoot = t;
                    break;
                }
            }
            if (bayRoot == null)
            {
                Plugin.Log.LogInfo("SceneDiagnostic: bay root not found.");
                return;
            }

            Plugin.Log.LogInfo($"SceneDiagnostic: SCAN START playerWorldPos={player.position} bayRootPos={bayRoot.position}");

            var results = Resources.FindObjectsOfTypeAll<Renderer>()
                .Where(r => r != null && r.gameObject != null)
                .Select(r =>
                {
                    float distFromPlayer = Vector3.Distance(player.position, r.bounds.ClosestPoint(player.position));
                    float distFromBay = Vector3.Distance(bayRoot.position, r.transform.position);
                    return (renderer: r, distFromPlayer, distFromBay);
                })
                .Where(x => x.distFromBay >= MinDistFromBayMeters)
                .Where(x => x.distFromPlayer >= MinDistFromPlayerMeters && x.distFromPlayer <= MaxDistFromPlayerMeters)
                .OrderBy(x => x.distFromPlayer)
                .ToList();

            int count = 0;
            foreach (var (renderer, distFromPlayer, distFromBay) in results)
            {
                count++;
                Transform t = renderer.transform;
                Vector3 boundsSize = renderer.bounds.size;

                // Flag anything whose world-space bounds are thin along one axis relative to the
                // other two - the same "flat card" signature GateCard/Quad/VillageSalvageStation
                // all share, regardless of which Renderer subtype produced it.
                float minDim = Mathf.Min(boundsSize.x, boundsSize.y, boundsSize.z);
                float maxDim = Mathf.Max(boundsSize.x, boundsSize.y, boundsSize.z);
                bool looksFlat = maxDim > 0.01f && (minDim / maxDim) < 0.05f;

                // Local (unscaled/unrotated) mesh bounds - the original FlatCardDiagnostic scan
                // logged this alongside world renderer.bounds because a thin LOCAL mesh can still
                // look non-flat in world bounds once rotated (a flat card at 45 degrees has a wider
                // world AABB on its thin axis than its true thickness). This is what actually proved
                // flatness for GateCard/Quad/CardRing, not the world-bounds ratio alone.
                MeshFilter? meshFilter = renderer.GetComponent<MeshFilter>();
                Vector3 localMeshBounds = meshFilter != null && meshFilter.sharedMesh != null
                    ? meshFilter.sharedMesh.bounds.size
                    : Vector3.zero;

                // angleOffFaceOn: 0 = card's forward axis points straight at the player (face-on),
                // 90 = perpendicular (edge-on/invisible-thin). Same metric FlatCardDiagnostic used
                // to separate genuine billboard candidates from distant 3D clutter that merely has
                // a thin bounding box in one direction.
                Vector3 directionToPlayer = (player.position - t.position).normalized;
                float angleOffFaceOn = Vector3.Angle(t.forward, directionToPlayer);
                if (angleOffFaceOn > 90f)
                    angleOffFaceOn = 180f - angleOffFaceOn;

                Plugin.Log.LogInfo($"SceneDiagnostic: #{count} type={renderer.GetType().Name} name={renderer.name} path={GetFullPath(t)} active={renderer.gameObject.activeInHierarchy} isStaticBatch={(renderer is MeshRenderer mr && mr.isPartOfStaticBatch)} distToNearestSurface={distFromPlayer:F1} distFromBay={distFromBay:F1} worldPos={t.position} localScale={t.localScale} boundsSize={boundsSize} localMeshBounds={localMeshBounds} angleOffFaceOn={angleOffFaceOn:F1} looksFlat={looksFlat}");
            }

            Plugin.Log.LogInfo($"SceneDiagnostic: SCAN END totalMatchingRenderers={count}");

            // Second pass, added to look for a velocity-reactive star-streak effect the user
            // spotted at high speed (long white lines radiating from the direction of travel,
            // visible in a screenshot) - exhaustive keyword search of the decompiled assemblies
            // (streak/speedline/warp/blur/vignette) found no dedicated C# class, so this is most
            // likely either a ParticleSystem (with VelocityOverLifetime or a stretched-billboard
            // render mode) or a global postprocess Volume override, neither of which the renderer
            // scan above would usefully surface: a ParticleSystem's own Renderer would still show
            // up above, but without the module state that actually proves it's velocity-reactive;
            // a Volume has no meaningful Renderer/bounds at all. Deliberately has NO distance
            // filtering (unlike the renderer scan) since this effect is plausibly camera-attached
            // (would fail the >=10m player-distance floor above) or fully global (has no
            // meaningful world position at all).
            DumpParticleSystems();
            DumpVolumes();
            DumpWarpEffectProfiles();
            DumpAllObjectsNearPlayer(player);
        }

        // Fourth pass - genuinely unfiltered, matching the old NearestObjectsDump.cs's style (the
        // tool that originally found the Sun's LynxHDRISkySettings after similarly exhaustive
        // targeted searches came up empty). Warp_RLPRO confirmed a dead end (not attached to any
        // active Volume OR any loaded VolumeProfile at all - WARP DUMP above always reports
        // warpRlproFound=0), so the real streak effect must be something with a name/type not yet
        // guessed. Every Renderer/ParticleSystem/Volume type has already been covered by the passes
        // above; this instead dumps literally every UnityEngine.Object of ANY type close to the
        // player, unfiltered by type, to catch something like a Light, a Camera-attached
        // MonoBehaviour with no Renderer, or a type this file has no dedicated dump for. Also logs
        // player velocity (Rigidbody, if found on the player) since the prior full-object-dump
        // (reference_full_object_dump in memory) never recorded speed, making it impossible to know
        // whether that earlier capture was even taken while the effect would have been active.
        private const float NearPlayerRadiusMeters = 50f;

        private static void DumpAllObjectsNearPlayer(Transform player)
        {
            // PlayerMotion (a RigidbodyController) is the same type MovementTuning.cs patches for
            // MaxVelocity - more reliable than walking up the camera's parent chain hoping to find
            // a Rigidbody, since the camera rig's exact hierarchy relative to the physics body isn't
            // guaranteed.
            PlayerMotion? playerMotion = Resources.FindObjectsOfTypeAll<PlayerMotion>()
                .FirstOrDefault(p => p != null && p.gameObject.activeInHierarchy);
            Vector3 velocity = playerMotion != null ? playerMotion.GetComponent<Rigidbody>()?.velocity ?? Vector3.zero : Vector3.zero;
            float speed = velocity.magnitude;

            Plugin.Log.LogInfo($"SceneDiagnostic: ALLOBJ DUMP START playerVelocity={velocity} playerSpeed={speed:F1} radius={NearPlayerRadiusMeters}m (playerMotionFound={playerMotion != null})");

            int count = 0;
            foreach (Component c in Resources.FindObjectsOfTypeAll<Component>())
            {
                if (c == null || c.transform == null)
                    continue;
                float dist = Vector3.Distance(player.position, c.transform.position);
                if (dist > NearPlayerRadiusMeters)
                    continue;

                count++;
                Plugin.Log.LogInfo($"SceneDiagnostic: ALLOBJ#{count} type={c.GetType().Name} name={c.name} path={GetFullPath(c.transform)} active={c.gameObject.activeInHierarchy} dist={dist:F2} worldPos={c.transform.position}");
            }

            Plugin.Log.LogInfo($"SceneDiagnostic: ALLOBJ DUMP END totalComponentsWithinRadius={count}");
        }

        // Third pass, added after decompiling RetroLookProHDRP.Effects.dll and confirming
        // Warp_RLPRO (a CustomPostProcessVolumeComponent, the WarpEffect_RLPRO shader, matches the
        // user's velocity-direction star-streak effect) is a real loaded asset but wasn't attached
        // to any of the scene's currently-active Volumes in the VOL DUMP pass above - it must live
        // on a VolumeProfile that's either not currently assigned to a live Volume, or only wired
        // up via a Timeline/trigger not covered by DumpVolumes(). Scans EVERY loaded VolumeProfile
        // asset directly (not just ones referenced by an active Volume component) to find where
        // Warp_RLPRO is actually configured, and logs its live parameter values if found -
        // Warp_RLPRO.IsActive() only returns true when intensity.value > 0, so this also directly
        // answers whether/when the effect is currently switched on.
        private static void DumpWarpEffectProfiles()
        {
            int profileCount = 0;
            int warpFoundCount = 0;
            foreach (VolumeProfile profile in Resources.FindObjectsOfTypeAll<VolumeProfile>())
            {
                if (profile == null)
                    continue;
                profileCount++;

                if (!profile.TryGet(out Warp_RLPRO warp))
                    continue;

                warpFoundCount++;
                Plugin.Log.LogInfo($"SceneDiagnostic: WARP#{warpFoundCount} profileName={profile.name} intensity={warp.intensity.value} fade={warp.fade.value} warpMode={warp.warpMode.value} warp={warp.warp.value} scale={warp.scale.value} clampSampler={warp.clampSampler.value} isActive={warp.IsActive()}");
            }
            Plugin.Log.LogInfo($"SceneDiagnostic: WARP DUMP totalVolumeProfilesScanned={profileCount} warpRlproFound={warpFoundCount}");
        }

        private static void DumpParticleSystems()
        {
            int count = 0;
            foreach (ParticleSystem ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                if (ps == null)
                    continue;
                count++;

                var velocityOverLifetime = ps.velocityOverLifetime;
                var main = ps.main;
                Renderer? renderer = ps.GetComponent<Renderer>();

                Plugin.Log.LogInfo($"SceneDiagnostic: PS#{count} name={ps.name} path={GetFullPath(ps.transform)} active={ps.gameObject.activeInHierarchy} isPlaying={ps.isPlaying} simulationSpace={main.simulationSpace} velocityOverLifetimeEnabled={velocityOverLifetime.enabled} startSpeed={main.startSpeed.constant} startLifetime={main.startLifetime.constant} rendererType={(renderer != null ? renderer.GetType().Name : "none")} renderMode={(ps.GetComponent<ParticleSystemRenderer>() is ParticleSystemRenderer psr ? psr.renderMode.ToString() : "n/a")} worldPos={ps.transform.position}");
            }
            Plugin.Log.LogInfo($"SceneDiagnostic: PS DUMP totalParticleSystems={count}");
        }

        private static void DumpVolumes()
        {
            int count = 0;
            foreach (Volume volume in Resources.FindObjectsOfTypeAll<Volume>())
            {
                if (volume == null)
                    continue;
                count++;

                string profileName = volume.profile != null ? volume.profile.name : "none";
                string overrideNames = volume.profile != null
                    ? string.Join("+", volume.profile.components.Select(c => c.GetType().Name))
                    : "n/a";

                Plugin.Log.LogInfo($"SceneDiagnostic: VOL#{count} name={volume.name} path={GetFullPath(volume.transform)} active={volume.gameObject.activeInHierarchy} isGlobal={volume.isGlobal} priority={volume.priority} weight={volume.weight} profile={profileName} overrides={overrideNames} worldPos={volume.transform.position}");
            }
            Plugin.Log.LogInfo($"SceneDiagnostic: VOL DUMP totalVolumes={count}");
        }

        private static string GetFullPath(Transform t)
        {
            string path = t.name;
            Transform? parent = t.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
