using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace TheExpanse
{
    // Turns the background Earth into Mars.
    //
    // The Earth is a flat card (PRF_PlanetCard_Octane) with an unlit transparent material whose only image is the
    // 4096 x 4096 'PlanetCutout'; a second card (PRF_PlanetGlow) adds the blue atmosphere glow as one additive colour, and
    // a third (PRF_PlanetCard_Moon) is the Moon. All three exist twice, once for the bay and once for the hab view
    // (PRF_Base_Planets / PRF_Base_Planets_HAB), sharing the same materials. None of them is statically batched.
    //
    // So the swap is: put the replacement image on the Earth material, recolour the glow, and switch the Moon cards off.
    // Everything is remembered and put back if the swap is turned off or the image goes missing.
    internal static class PlanetSwap
    {
        const string RootPrefix = "PRF_Base_Planets";
        const string EarthCard = "PRF_PlanetCard_Octane";
        const string MoonCard = "PRF_PlanetCard_Moon";
        const string GlowCard = "PRF_PlanetGlow";
        const string GlowColourProperty = "Color_30913618aef8424ba15afd05c75a4bed";      // Shader Graph "Color" on RadialGradient
        static readonly string[] ImageProperties = { "_UnlitColorMap", "_MainTex" };

        static Texture2D? sImage;
        static string sImagePath = "";
        static DateTime sImageStamp;

        // What the game had, keyed by instance id, so it can be put back.
        static readonly Dictionary<int, KeyValuePair<Material, Texture>> sEarthOriginals = new Dictionary<int, KeyValuePair<Material, Texture>>();
        static readonly Dictionary<int, KeyValuePair<Material, Color>> sGlowOriginals = new Dictionary<int, KeyValuePair<Material, Color>>();
        static readonly List<Renderer> sHiddenMoons = new List<Renderer>();


        // Called after every level load. Safe to call repeatedly.
        internal static void Apply()
        {
            string path = Path.Combine(Path.GetDirectoryName(Plugin.Instance.Info.Location)!, Plugin.MarsImage.Value);
            if (!Plugin.MarsEnabled.Value) { Restore("switched off in the config"); return; }
            if (!File.Exists(path)) { Restore($"'{path}' not found"); return; }

            var image = LoadImage(path);
            if (image == null) { Restore("the image could not be loaded"); return; }

            int earths = 0, glows = 0, moons = 0;
            foreach (var r in FindCards())
            {
                switch (r.name)
                {
                    case EarthCard:
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) continue;
                            foreach (var prop in ImageProperties)
                            {
                                if (!m.HasProperty(prop)) continue;
                                var current = m.GetTexture(prop);
                                if (current == image) continue;
                                if (prop == ImageProperties[0] && current != null && !sEarthOriginals.ContainsKey(m.GetInstanceID()))
                                    sEarthOriginals[m.GetInstanceID()] = new KeyValuePair<Material, Texture>(m, current);
                                m.SetTexture(prop, image);
                            }
                        }
                        earths++;
                        break;

                    case GlowCard:
                        var gm = r.sharedMaterial;
                        if (gm != null && gm.HasProperty(GlowColourProperty))
                        {
                            if (!sGlowOriginals.ContainsKey(gm.GetInstanceID()))
                                sGlowOriginals[gm.GetInstanceID()] = new KeyValuePair<Material, Color>(gm, gm.GetColor(GlowColourProperty));
                            gm.SetColor(GlowColourProperty, Plugin.MarsGlow.Value);
                            glows++;
                        }
                        else Plugin.Log.LogWarning($"[PlanetSwap] glow card '{r.name}' has no '{GlowColourProperty}' colour; glow left as it is.");
                        break;

                    case MoonCard:
                        if (Plugin.MarsHideMoon.Value)
                        {
                            if (r.enabled) { r.enabled = false; sHiddenMoons.Add(r); }
                            moons++;
                        }
                        else if (sHiddenMoons.Remove(r)) r.enabled = true;
                        break;
                }
            }

            if (earths > 0) Plugin.Log.LogInfo($"[PlanetSwap] Mars applied: {earths} planet card(s) now show '{Path.GetFileName(path)}' ({image.width}x{image.height}, {image.format}), " +
                                    $"{glows} glow card(s) recoloured, {moons} moon card(s) hidden.");
        }

        static List<Renderer> FindCards()
        {
            var cards = new List<Renderer>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.IsValid() || !t.name.StartsWith(RootPrefix, StringComparison.Ordinal)) continue;
                if (t.parent != null && t.parent.name.StartsWith(RootPrefix, StringComparison.Ordinal)) continue;
                foreach (var r in t.GetComponentsInChildren<MeshRenderer>(true))
                {
                    cards.Add(r);
                }
            }
            return cards;
        }

        // Loads the PNG once and again whenever the file changes on disk, so new art only needs a level reload.
        static Texture2D? LoadImage(string path)
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (sImage != null && sImagePath == path && sImageStamp == stamp) return sImage;

            Texture2D? tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = "TheExpanse_" + Path.GetFileNameWithoutExtension(path) };
                if (!tex.LoadImage(File.ReadAllBytes(path), false)) throw new InvalidDataException("not a PNG or JPG Unity can decode");
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.anisoLevel = 4;
                // The game's own image is block-compressed; do the same so a 4096 px image costs ~21 MB of video memory, not ~85.
                string compressed = "left uncompressed";
                if (tex.width % 4 == 0 && tex.height % 4 == 0)
                {
                    try { tex.Compress(true); compressed = "compressed"; }
                    catch (Exception ex) { compressed = "left uncompressed (" + ex.Message + ")"; }
                }
                tex.Apply(true, true);
                Plugin.Log.LogInfo($"[PlanetSwap] loaded '{path}': {tex.width}x{tex.height}, {tex.format}, {compressed}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[PlanetSwap] could not load '{path}': {ex.Message}");
                if (tex != null) UnityEngine.Object.Destroy(tex);
                return null;
            }

            var old = sImage;
            sImage = tex; sImagePath = path; sImageStamp = stamp;
            // Materials still pointing at the previous image are repointed by the caller; only then is it safe to drop.
            if (old != null)
            {
                foreach (var kv in sEarthOriginals.Values)
                    if (kv.Key != null)
                        foreach (var prop in ImageProperties)
                            if (kv.Key.HasProperty(prop) && kv.Key.GetTexture(prop) == old) kv.Key.SetTexture(prop, tex);
                UnityEngine.Object.Destroy(old);
            }
            return tex;
        }

        static void Restore(string why)
        {
            int n = 0;
            foreach (var kv in sEarthOriginals.Values)
            {
                if (kv.Key == null) continue;
                foreach (var prop in ImageProperties) if (kv.Key.HasProperty(prop)) kv.Key.SetTexture(prop, kv.Value);
                n++;
            }
            foreach (var kv in sGlowOriginals.Values) if (kv.Key != null) { kv.Key.SetColor(GlowColourProperty, kv.Value); n++; }
            foreach (var r in sHiddenMoons) if (r != null) { r.enabled = true; n++; }
            sEarthOriginals.Clear(); sGlowOriginals.Clear(); sHiddenMoons.Clear();
            if (sImage != null) { UnityEngine.Object.Destroy(sImage); sImage = null; }
            Plugin.Log.LogInfo($"[PlanetSwap] Earth left as it is ({why})" + (n > 0 ? $"; {n} earlier change(s) put back." : "."));
        }
    }
}
