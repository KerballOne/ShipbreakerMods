using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BiggerBays
{
    // The bay's spotlights, taken over from the BayLights mod's "bay" group and made adjustable while the game runs.
    //
    // Which lights: Spot lights that are not part of a ship (no ModuleDefinition above them) and have a narrow cone or a
    // default name ("Spot Light...", "Point Light..."), and, with its own two multipliers, the helmet light. Every other
    // light is left alone; ship lights and fog stay with BayLights.
    //
    // Each light's own range, intensity and cone are remembered the first time it is seen, and what is set is always
    // worked out from those, so settings can be changed any number of times without the changes piling up.
    //
    // Keeping coverage: a wider bay puts the wall lights further from the middle. A light that is k times further away
    // needs k times the range to reach as far, and k squared times the intensity to light the same spot as brightly.
    internal static class BayLighting
    {
        sealed class Entry
        {
            public Light Light = null!;
            public float Range, Intensity, Angle;
            public bool InShip, Helmet, Changed;
        }

        const string HelmetLightName = "Helmet Spotlight";

        // A spotlight with a cone no wider than this (or a default name) and not part of a ship is a bay spotlight.
        const float MaxAngle = 60f;

        static readonly Dictionary<int, Entry> sSeen = new Dictionary<int, Entry>();
        static bool sActive;
        static float sNextScan;

        // Nothing is changed between a level starting to load and the bay being reshaped, so the copies made of bay
        // lights start from the game's own values.
        internal static void OnLevelLoadStart() => sActive = false;

        internal static void OnLevelLoaded()
        {
            var gone = new List<int>();
            foreach (var kv in sSeen) if (kv.Value.Light == null) gone.Add(kv.Key);
            foreach (var id in gone) sSeen.Remove(id);
            sActive = true;
            Scan();
            Apply(true);
            sNextScan = Time.unscaledTime + 3f;
        }

        internal static void OnSettingsChanged()
        {
            if (sActive) Apply(true);
        }

        // Lights that appear later (a ship's, a cloned effect's) are picked up every few seconds.
        internal static void Tick()
        {
            if (!sActive || Time.unscaledTime < sNextScan) return;
            sNextScan = Time.unscaledTime + 3f;
            if (Scan() > 0) Apply(false);
        }

        static int Scan()
        {
            int added = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var light in root.GetComponentsInChildren<Light>(true))
                    {
                        if (light == null || light.type != LightType.Spot) continue;
                        int id = light.GetInstanceID();
                        if (sSeen.ContainsKey(id)) continue;
                        sSeen[id] = new Entry
                        {
                            Light = light, Range = light.range, Intensity = light.intensity, Angle = light.spotAngle,
                            Helmet = light.name == HelmetLightName,
                        };
                        if (!sSeen[id].Helmet) sSeen[id].InShip = IsInsideShip(light.transform);
                        added++;
                    }
            }
            return added;
        }

        // A light belongs to a ship when some object above it carries the game's ModuleDefinition (every ship, and every
        // module spawned into one, has it on its root).
        static bool IsInsideShip(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                foreach (var c in p.GetComponents<MonoBehaviour>())
                {
                    if (c == null) continue;
                    var n = c.GetType().Name;
                    if (n == "ModuleDefinition" || n == "RootModuleDefinition") return true;
                }
            return false;
        }

        static bool IsBay(Entry e)
        {
            if (e.InShip || e.Helmet) return false;
            var name = e.Light.name;
            return e.Angle <= MaxAngle
                   || name.StartsWith("Spot Light", System.StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("Point Light", System.StringComparison.OrdinalIgnoreCase);
        }

        internal static float CoverageFactor => (BayReshaper.StockHalfWidth + BayReshaper.AppliedWidth) / BayReshaper.StockHalfWidth;

        static void Apply(bool log)
        {
            bool on = Plugin.Enabled.Value && Plugin.LightsEnabled.Value;
            float k = CoverageFactor;
            float range = Mathf.Max(0f, Plugin.LightRange.Value) * k;
            float intensity = Mathf.Max(0f, Plugin.LightIntensity.Value) * k * k;

            int changed = 0, restored = 0;
            foreach (var e in sSeen.Values)
            {
                if (e.Light == null) continue;
                if (e.Helmet)
                {
                    // The helmet light has its own two multipliers (settings file only) and is not counted below.
                    float hr = Mathf.Max(0f, Plugin.HelmetRange.Value), hi = Mathf.Max(0f, Plugin.HelmetIntensity.Value);
                    bool helmetOn = Plugin.Enabled.Value && (hr != 1f || hi != 1f);
                    if (helmetOn || e.Changed)
                    {
                        e.Light.range = e.Range * (helmetOn ? hr : 1f);
                        e.Light.intensity = e.Intensity * (helmetOn ? hi : 1f);
                        e.Changed = helmetOn;
                    }
                    continue;
                }
                if (on && IsBay(e))
                {
                    e.Light.range = e.Range * range;
                    e.Light.intensity = e.Intensity * intensity;
                    e.Changed = true;
                    changed++;
                }
                else if (e.Changed)
                {
                    e.Light.range = e.Range;
                    e.Light.intensity = e.Intensity;
                    e.Changed = false;
                    restored++;
                }
            }
            if (log && (changed > 0 || restored > 0))
                Plugin.Log.LogInfo($"[Lights] {changed} bay spotlight(s) set to range x{range:0.00}, intensity x{intensity:0.00}" +
                                   $" (coverage factor {k:0.00} for {BayReshaper.AppliedWidth:0.0} m of extra width)" +
                                   $"{(restored > 0 ? $"; {restored} put back to the game's values" : "")}.");
        }
    }
}
