using System.Linq;
using BBI.Unity.Game;
using UnityEngine;

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
