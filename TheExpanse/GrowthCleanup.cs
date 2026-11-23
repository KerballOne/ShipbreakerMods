using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;

namespace TheExpanse
{
    // The ghost ship's growth goes away with its last node.
    //
    // The growth patches are not objects of their own. When the ship spawns, the game bakes each patch onto the parts
    // under it (BakedDecalProjector -> DecalBakerSystem): the part's mesh gets the patch's coordinates, and the part's
    // material gets the patch's texture in one of two decal slots and has its baked-decal switch turned on. The
    // patches stay with the parts after cutting.
    //
    // So once no node is left, every ship material carrying a growth texture is cleaned: if growth is all it carries,
    // its baked-decal switch is turned off; if it carries another decal too, only the growth slot is emptied. The
    // growth textures are told apart by name (Proto_Growth_*).
    //
    // "No node left" is either the last one destroyed during the shift, or a ship that loads from a save with none
    // left; the latter is only judged a while after the ship has finished spawning, when its nodes would have shown up.
    internal static class GrowthCleanup
    {
        const string Keyword = "_BAKED_DECALS_ON";
        const string GrowthPrefix = "Proto_Growth";
        const float SettleSeconds = 10f;
        static readonly int[] sSlots = { Shader.PropertyToID("_BakedDecalTexture01"), Shader.PropertyToID("_BakedDecalTexture02") };
        static readonly float[] sPassDelays = { 0f, 3f, 15f };     // again a little later, for parts still being set up

        static float sSpawnedAt = -1f;
        static float sClearedAt = -1f;
        static int sPass;
        static Texture2D? sClear;

        internal static void Reset()
        {
            sSpawnedAt = -1f;
            sClearedAt = -1f;
            sPass = 0;
        }

        internal static void OnShipSpawned() => sSpawnedAt = Time.time;

        // Called a couple of times a second, after GhostNodes.Refresh.
        internal static void Tick()
        {
            if (!Plugin.RemoveGrowthAfterLastNode.Value) return;
            if (sClearedAt < 0f)
            {
                bool lastKilled = GhostNodes.Original > 0f && GhostNodes.Total <= 0f;
                bool loadedClean = GhostNodes.Original <= 0f && sSpawnedAt >= 0f && Time.time - sSpawnedAt >= SettleSeconds;
                if (!lastKilled && !loadedClean) return;
                sClearedAt = Time.time;
                sPass = 0;
            }
            if (sPass >= sPassDelays.Length || Time.time - sClearedAt < sPassDelays[sPass]) return;
            int pass = sPass++;
            int cleaned = Clean();
            bool lastKill = GhostNodes.Original > 0f;
            if (cleaned > 0 || (pass == 0 && lastKill))       // a normal ship (no nodes, no growth) logs nothing
                Plugin.Log.LogInfo(lastKill
                    ? $"[Haunting] last node destroyed: growth removed from {cleaned} material(s)."
                    : $"[Haunting] no nodes on this ship: growth removed from {cleaned} material(s).");
        }

        static int Clean()
        {
            int cleaned = 0;
            var seen = new HashSet<Material>();
            foreach (var renderer in Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || renderer.GetComponentInParent<StructurePart>() == null) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || !seen.Add(material) || !material.IsKeywordEnabled(Keyword)) continue;
                    int growth = 0, other = 0;
                    foreach (int slot in sSlots)
                    {
                        if (!material.HasProperty(slot)) continue;
                        var texture = material.GetTexture(slot);
                        if (texture == null || texture == sClear) continue;
                        if (texture.name.StartsWith(GrowthPrefix, System.StringComparison.OrdinalIgnoreCase)) growth++;
                        else other++;
                    }
                    if (growth == 0) continue;
                    if (other == 0) material.DisableKeyword(Keyword);
                    else
                    {
                        foreach (int slot in sSlots)
                        {
                            var texture = material.HasProperty(slot) ? material.GetTexture(slot) : null;
                            if (texture != null && texture.name.StartsWith(GrowthPrefix, System.StringComparison.OrdinalIgnoreCase))
                                material.SetTexture(slot, Clear());
                        }
                    }
                    cleaned++;
                }
            }
            return cleaned;
        }

        // A fully see-through texture, for emptying one decal slot while keeping the other.
        static Texture2D Clear()
        {
            if (sClear != null) return sClear;
            sClear = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "TheExpanse_NoGrowth" };
            sClear.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
            sClear.Apply();
            return sClear;
        }
    }
}
