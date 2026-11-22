using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace BayLights
{
    // Whitelist: only four groups of lights are ever changed. Every other light keeps the values it was authored with.
    //
    //   Helmet   the Spot light named "Helmet Spotlight"
    //   Bay      Spot lights that are NOT part of a ship (no ModuleDefinition above them) and have a narrow cone or a
    //            default name ("Spot Light...", "Point Light...")
    //   Sodium   lights inside a ship whose own name or a parent's name matches SodiumLights/NamePatterns
    //   Custom   lights matching a rule in Custom/Rules, each rule with its own multipliers
    //
    // Set General/LogLights to list every light with the group it fell into.
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        private ConfigEntry<bool>   _enabled              = null!;
        private ConfigEntry<bool>   _logLights            = null!;
        private ConfigEntry<bool>   _bayEnabled           = null!;
        private ConfigEntry<float>  _rangeMult            = null!;
        private ConfigEntry<float>  _intensityMult        = null!;
        private ConfigEntry<float>  _coneAngle            = null!;
        private ConfigEntry<float>  _maxAngle             = null!;
        private ConfigEntry<bool>   _helmetEnabled        = null!;
        private ConfigEntry<float>  _helmetRangeMult      = null!;
        private ConfigEntry<float>  _helmetIntensityMult  = null!;
        private ConfigEntry<bool>   _sodiumEnabled        = null!;
        private ConfigEntry<string> _sodiumPatterns       = null!;
        private ConfigEntry<float>  _sodiumIntensityMult  = null!;
        private ConfigEntry<float>  _sodiumRangeMult      = null!;
        private ConfigEntry<bool>   _sodiumNoShadows      = null!;
        private ConfigEntry<string> _customRules          = null!;
        private ConfigEntry<bool>   _customNoShadows      = null!;
        private ConfigEntry<float>  _fogMeanFreePath      = null!;

        private readonly HashSet<int> _boosted = new HashSet<int>();

        private class Rule
        {
            public string Pattern = "";
            public Regex Regex = null!;
            public float Intensity = 1f, Range = 1f;
        }

        private void Awake()
        {
            Log = Logger;

            _enabled              = Config.Bind("General",      "Enabled",             true,   "Enable or disable BayLights.");
            _logLights            = Config.Bind("General",      "LogLights",           false,  "List every light in the log after each scene load: its type, name, parents, range, intensity and the group it fell into (bay, helmet, sodium, custom rule, or untouched).");

            _bayEnabled           = Config.Bind("BayLights",    "Enabled",             true,   "Change the salvage bay's spotlights.");
            _rangeMult            = Config.Bind("BayLights",    "RangeMultiplier",     2.0f,   "Multiply bay spotlight range by this factor.");
            _intensityMult        = Config.Bind("BayLights",    "IntensityMultiplier", 1.0f,   "Multiply bay spotlight intensity by this factor. 1.0 = unchanged.");
            _coneAngle            = Config.Bind("BayLights",    "ConeAngle",           -1.0f,  "Override bay spotlight cone angle in degrees (1-179). -1 = leave unchanged.");
            _maxAngle             = Config.Bind("BayLights",    "MaxAngle",            60.0f,  "Only boost bay spotlights whose original cone angle is at or below this value (spotlights with a default name are boosted regardless).");

            _helmetEnabled        = Config.Bind("Helmet",       "Enabled",             true,   "Change the helmet spotlight.");
            _helmetRangeMult      = Config.Bind("Helmet",       "RangeMultiplier",     2.0f,   "Multiply helmet spotlight range by this factor.");
            _helmetIntensityMult  = Config.Bind("Helmet",       "IntensityMultiplier", 1.0f,   "Multiply helmet spotlight intensity by this factor. 1.0 = unchanged.");

            _sodiumEnabled        = Config.Bind("SodiumLights", "Enabled",             true,   "Change the interior sodium lights of ships.");
            _sodiumPatterns       = Config.Bind("SodiumLights", "NamePatterns",        "*Sodium*", "Comma-separated name patterns (* = any text, not case sensitive). A light inside a ship belongs to this group when its own name or the name of any object above it matches one.");
            _sodiumIntensityMult  = Config.Bind("SodiumLights", "IntensityMultiplier", 5.0f,   "Multiply sodium light intensity by this factor. 1.0 = unchanged.");
            _sodiumRangeMult      = Config.Bind("SodiumLights", "RangeMultiplier",     2.0f,   "Multiply sodium light range by this factor.");
            _sodiumNoShadows      = Config.Bind("SodiumLights", "DisableShadows",      true,   "Disable shadows on the sodium lights. Prevents flickering when range is boosted.");

            _customRules          = Config.Bind("Custom",       "Rules",               "",     "Other lights to change, as rules separated by semicolons: NamePattern|IntensityMultiplier|RangeMultiplier. * = any text, not case sensitive. A light matches when its own name or the name of any object above it matches. Example: *Console*|2|1.5; PRF_Light_Emergency*|3|2. The first matching rule wins. Lights that match no rule and are not bay, helmet or sodium lights are never changed.");
            _customNoShadows      = Config.Bind("Custom",       "DisableShadows",      false,  "Disable shadows on lights changed by a custom rule.");

            _fogMeanFreePath      = Config.Bind("Fog",          "MeanFreePathMultiplier", 1.0f, "Multiply HDRP fog mean free path by this factor. Higher = clearer. 1.0 = unchanged.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            Log.LogInfo("BayLights loaded.");
        }

        private static List<Light> GetAllSceneLights()
        {
            var result = new List<Light>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    result.AddRange(root.GetComponentsInChildren<Light>(true));
            }
            return result;
        }

        private static List<Volume> GetAllSceneVolumes()
        {
            var result = new List<Volume>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    result.AddRange(root.GetComponentsInChildren<Volume>(true));
            }
            return result;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_enabled.Value) return;
            if (mode != LoadSceneMode.Additive)
                _boosted.Clear();
            StartCoroutine(BoostLights(scene.name));
        }

        // "*Sodium*" -> a regex for the whole name, not case sensitive. A pattern without * must match the whole name.
        private static Regex ToRegex(string pattern)
        {
            return new Regex("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static List<Regex> ParsePatterns(string text)
        {
            var list = new List<Regex>();
            foreach (var part in text.Split(','))
                if (part.Trim().Length > 0) list.Add(ToRegex(part));
            return list;
        }

        private List<Rule> ParseRules(string text)
        {
            var list = new List<Rule>();
            foreach (var raw in text.Split(';'))
            {
                var item = raw.Trim();
                if (item.Length == 0) continue;
                var f = item.Split('|');
                var rule = new Rule { Pattern = f[0].Trim() };
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var num = System.Globalization.NumberStyles.Float;
                bool ok = rule.Pattern.Length > 0
                          && (f.Length < 2 || float.TryParse(f[1].Trim(), num, inv, out rule.Intensity))
                          && (f.Length < 3 || float.TryParse(f[2].Trim(), num, inv, out rule.Range));
                if (!ok || f.Length > 3)
                {
                    Log.LogWarning($"[BayLights] Custom rule ignored, expected NamePattern|IntensityMultiplier|RangeMultiplier: '{item}'");
                    continue;
                }
                rule.Regex = ToRegex(rule.Pattern);
                list.Add(rule);
            }
            return list;
        }

        // True when the object's own name or any ancestor's name matches.
        private static bool NameMatches(Transform t, Regex regex)
        {
            for (var p = t; p != null; p = p.parent)
                if (regex.IsMatch(p.name)) return true;
            return false;
        }

        // A light belongs to a ship when some object above it carries the game's ModuleDefinition (every ship, and every
        // module spawned into one, has it on its root). Checked by type name so the mod needs no reference to the game DLL.
        private static bool IsInsideShip(Transform t)
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

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null && parts.Count < 5; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private IEnumerator BoostLights(string sceneName)
        {
            float elapsed = 0f;
            int lastCount = -1;
            int stableFrames = 0;

            while (elapsed < 30f)
            {
                yield return new WaitForSeconds(1f);
                elapsed += 1f;

                int count = GetAllSceneLights().Count;
                if (count == lastCount)
                {
                    stableFrames++;
                    if (stableFrames >= 3) break;
                }
                else
                {
                    stableFrames = 0;
                    lastCount = count;
                }
            }

            var allLights = GetAllSceneLights();
            int bayCount = 0, helmetCount = 0, sodiumCount = 0, customCount = 0, untouched = 0;

            var sodium = ParsePatterns(_sodiumPatterns.Value);
            var rules = ParseRules(_customRules.Value);

            foreach (var light in allLights)
            {
                if (light == null) continue;
                int id = light.GetInstanceID();
                bool alreadyBoosted = _boosted.Contains(id);
                float range0 = light.range, intensity0 = light.intensity;
                string group = "untouched";

                // 1. custom rules: explicitly named, so they win over the built-in groups
                Rule? rule = null;
                foreach (var r in rules)
                    if (NameMatches(light.transform, r.Regex)) { rule = r; break; }

                if (rule != null)
                {
                    group = "custom '" + rule.Pattern + "'";
                    if (!alreadyBoosted)
                    {
                        light.intensity *= rule.Intensity;
                        light.range     *= rule.Range;
                        if (_customNoShadows.Value) light.shadows = LightShadows.None;
                        _boosted.Add(id);
                    }
                    customCount++;
                }
                // 2. helmet
                else if (light.type == LightType.Spot && light.name == "Helmet Spotlight")
                {
                    group = "helmet";
                    if (_helmetEnabled.Value)
                    {
                        if (!alreadyBoosted)
                        {
                            light.range     *= _helmetRangeMult.Value;
                            light.intensity *= _helmetIntensityMult.Value;
                            _boosted.Add(id);
                        }
                        helmetCount++;
                    }
                }
                else
                {
                    bool inShip = IsInsideShip(light.transform);
                    // 3. sodium lights: inside a ship, named
                    bool isSodium = false;
                    if (inShip)
                        foreach (var rx in sodium)
                            if (NameMatches(light.transform, rx)) { isSodium = true; break; }

                    if (isSodium)
                    {
                        group = "sodium";
                        if (_sodiumEnabled.Value)
                        {
                            if (!alreadyBoosted)
                            {
                                light.intensity *= _sodiumIntensityMult.Value;
                                light.range     *= _sodiumRangeMult.Value;
                                if (_sodiumNoShadows.Value) light.shadows = LightShadows.None;
                                _boosted.Add(id);
                            }
                            sodiumCount++;
                        }
                    }
                    // 4. bay spotlights: not part of a ship
                    else if (!inShip && light.type == LightType.Spot
                             && (light.spotAngle <= _maxAngle.Value
                                 || light.name.StartsWith("Spot Light",  System.StringComparison.OrdinalIgnoreCase)
                                 || light.name.StartsWith("Point Light", System.StringComparison.OrdinalIgnoreCase)))
                    {
                        group = "bay";
                        if (_bayEnabled.Value)
                        {
                            if (!alreadyBoosted)
                            {
                                light.range     *= _rangeMult.Value;
                                light.intensity *= _intensityMult.Value;
                                if (_coneAngle.Value >= 1f && _coneAngle.Value <= 179f)
                                    light.spotAngle = _coneAngle.Value;
                                _boosted.Add(id);
                            }
                            bayCount++;
                        }
                    }
                    else
                    {
                        group = inShip ? "untouched (ship)" : "untouched";
                        untouched++;
                    }
                }

                if (_logLights.Value)
                    Log.LogInfo($"[BayLights] light {light.type} '{PathOf(light.transform)}' scene={light.gameObject.scene.name} active={light.isActiveAndEnabled} " +
                                $"range {range0:0.##} -> {light.range:0.##}, intensity {intensity0:0.##} -> {light.intensity:0.##} | {group}");
            }

            if (bayCount > 0 || helmetCount > 0 || sodiumCount > 0 || customCount > 0)
                Log.LogInfo($"[BayLights] '{sceneName}': {bayCount} bay, {helmetCount} helmet, {sodiumCount} sodium, {customCount} custom-rule lights changed; {untouched} lights left untouched.");

            if (_fogMeanFreePath.Value != 1.0f)
            {
                int fogCount = 0;
                foreach (var volume in GetAllSceneVolumes())
                {
                    if (volume.profile != null && volume.profile.TryGet<UnityEngine.Rendering.HighDefinition.Fog>(out var fog))
                    {
                        fog.meanFreePath.value *= _fogMeanFreePath.Value;
                        fogCount++;
                    }
                }
                if (fogCount > 0)
                    Log.LogInfo($"[BayLights] Modified fog in {fogCount} volume(s) (meanFreePath x{_fogMeanFreePath.Value}).");
            }
        }
    }
}
