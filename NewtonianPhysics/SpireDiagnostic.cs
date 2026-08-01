using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBI.Unity.Game;
using BepInEx;
using UnityEngine;

namespace NewtonianPhysics
{
    // TEMPORARY diagnostic, F9-bound - looking for an unidentified "spire" shaped flat billboard
    // the user has spotted in-game that was missed by both the earlier FlatCardDiagnostic scans
    // and its texture-export pass (see project_flatcard_billboard_investigation /
    // project_newtonianphysics_todo in memory). Queries EVERY Renderer type (not just MeshFilter)
    // with no early distance/name filtering beyond a broad radius, to catch whatever gap let this
    // object slip past the earlier, narrower scans. Delete once the object is identified.
    internal static class SpireDiagnostic
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
            if (Input.GetKeyDown(KeyCode.F8))
                ExportVillageWaystationTextures();

            if (!Input.GetKeyDown(KeyCode.F9))
                return;

            Transform? player = LynxCameraController.MainCameraTransform;
            if (player == null)
            {
                Plugin.Log.LogInfo("SpireDiagnostic: no player camera transform found.");
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
                Plugin.Log.LogInfo("SpireDiagnostic: bay root not found.");
                return;
            }

            Plugin.Log.LogInfo($"SpireDiagnostic: SCAN START playerWorldPos={player.position} bayRootPos={bayRoot.position}");

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

                Plugin.Log.LogInfo($"SpireDiagnostic: #{count} type={renderer.GetType().Name} name={renderer.name} path={GetFullPath(t)} active={renderer.gameObject.activeInHierarchy} isStaticBatch={(renderer is MeshRenderer mr && mr.isPartOfStaticBatch)} distToNearestSurface={distFromPlayer:F1} distFromBay={distFromBay:F1} worldPos={t.position} localScale={t.localScale} boundsSize={boundsSize} localMeshBounds={localMeshBounds} angleOffFaceOn={angleOffFaceOn:F1} looksFlat={looksFlat}");
            }

            Plugin.Log.LogInfo($"SpireDiagnostic: SCAN END totalMatchingRenderers={count}");
        }

        // F8-bound, separate from the F9 scan - exports each unique SpriteRenderer.sprite.texture
        // used by active VillageWaystation instances to PNG, via the same GPU-blit technique proven
        // in the Sun/FlatCard investigations (Graphics.Blit into a temporary RenderTexture, then
        // ReadPixels/EncodeToPNG - required because sprite atlas textures are often not marked
        // Read/Write enabled, which EncodeToPNG needs directly on the source texture). Always
        // overwrites on every press (no on-disk or in-memory "already exported" guard) so repeated
        // F8 presses reliably produce fresh output - an earlier version of this same technique had a
        // bug where an in-memory HashSet guard silently no-opped re-export even after the user
        // deleted the output files, which a File.Exists check only half-fixes (still stale after the
        // first successful export unless manually deleted each time).
        private static void ExportVillageWaystationTextures()
        {
            string outputDir = Path.Combine(Paths.PluginPath, "NewtonianPhysics", "SpireDiagnosticExports");
            Directory.CreateDirectory(outputDir);

            var exportedTextureNames = new HashSet<string>();
            int spriteCount = 0;
            int exportedCount = 0;

            foreach (SpriteRenderer sr in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
            {
                if (sr == null || sr.sprite == null || sr.sprite.texture == null)
                    continue;
                if (!sr.name.StartsWith("VillageWaystation"))
                    continue;

                spriteCount++;
                Texture2D sourceTexture = sr.sprite.texture;
                string textureName = string.IsNullOrEmpty(sourceTexture.name) ? $"UnnamedTexture_{sourceTexture.GetInstanceID()}" : sourceTexture.name;
                if (!exportedTextureNames.Add(textureName))
                    continue;

                string outputPath = Path.Combine(outputDir, SanitizeFileName(textureName) + ".png");

                RenderTexture temp = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(sourceTexture, temp);
                RenderTexture previousActive = RenderTexture.active;
                RenderTexture.active = temp;

                Texture2D readable = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, temp.width, temp.height), 0, 0);
                readable.Apply();

                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(temp);

                File.WriteAllBytes(outputPath, readable.EncodeToPNG());
                Object.Destroy(readable);
                exportedCount++;

                Plugin.Log.LogInfo($"SpireDiagnostic: EXPORT wrote texture={textureName} size=({sourceTexture.width}x{sourceTexture.height}) path={outputPath}");
            }

            Plugin.Log.LogInfo($"SpireDiagnostic: EXPORT DONE spritesScanned={spriteCount} uniqueTexturesExported={exportedCount} outputDir={outputDir}");
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
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
