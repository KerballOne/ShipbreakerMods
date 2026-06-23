using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace BayLights
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        private ConfigEntry<bool>   _enabled              = null!;
        private ConfigEntry<bool>   _logLights            = null!;
        private ConfigEntry<float>  _rangeMult            = null!;
        private ConfigEntry<float>  _intensityMult        = null!;
        private ConfigEntry<float>  _coneAngle            = null!;
        private ConfigEntry<float>  _maxAngle             = null!;
        private ConfigEntry<float>  _helmetRangeMult      = null!;
        private ConfigEntry<float>  _helmetIntensityMult  = null!;
        private ConfigEntry<float>  _fogMeanFreePath      = null!;
        private ConfigEntry<float>  _areaIntensityMult    = null!;
        private ConfigEntry<float>  _areaRangeMult        = null!;
        private ConfigEntry<string> _areaColor            = null!;
        private ConfigEntry<float>  _pointIntensityMult   = null!;
        private ConfigEntry<float>  _pointRangeMult       = null!;
        private ConfigEntry<string> _pointColor           = null!;
        private ConfigEntry<float>  _shipSpotIntensityMult = null!;
        private ConfigEntry<float>  _shipSpotRangeMult    = null!;
        private ConfigEntry<string> _shipSpotColor        = null!;
        private ConfigEntry<bool>   _shipLightsNoShadows  = null!;

        private readonly HashSet<int> _boosted = new HashSet<int>();
        private readonly Dictionary<Light, (MeshRenderer renderer, Color color)> _colorEnforced = new Dictionary<Light, (MeshRenderer, Color)>();
        private Coroutine _enforceCoroutine = null;

        private void Awake()
        {
            Log = Logger;

            _enabled              = Config.Bind("General",    "Enabled",                  true,    "Enable or disable BayLights.");
            _logLights            = Config.Bind("General",    "LogLights",                true,    "Log all Light components found across all scenes.");

            _rangeMult            = Config.Bind("BayLights",  "RangeMultiplier",           2.0f,   "Multiply bay spotlight range by this factor.");
            _intensityMult        = Config.Bind("BayLights",  "IntensityMultiplier",       1.0f,   "Multiply bay spotlight intensity by this factor. 1.0 = unchanged.");
            _coneAngle            = Config.Bind("BayLights",  "ConeAngle",                -1.0f,   "Override bay spotlight cone angle in degrees (1-179). -1 = leave unchanged.");
            _maxAngle             = Config.Bind("BayLights",  "MaxAngle",                 60.0f,   "Only boost spotlights whose original cone angle is at or below this value.");

            _helmetRangeMult      = Config.Bind("Helmet",     "RangeMultiplier",           2.0f,   "Multiply helmet spotlight range by this factor.");
            _helmetIntensityMult  = Config.Bind("Helmet",     "IntensityMultiplier",       1.0f,   "Multiply helmet spotlight intensity by this factor. 1.0 = unchanged.");

            const string colorDesc = "Light color override. Use R,G,B floats (e.g. 1.0,0.85,0.6) or a color name (white, red, green, blue, yellow, cyan, magenta, orange, sodium). Empty = leave unchanged.";
            _areaIntensityMult    = Config.Bind("ShipLights", "AreaIntensityMultiplier",   5.0f,   "Multiply Area light (e.g. sodium) intensity by this factor. 1.0 = unchanged.");
            _areaRangeMult        = Config.Bind("ShipLights", "AreaRangeMultiplier",        2.0f,   "Multiply Area light range by this factor.");
            _areaColor            = Config.Bind("ShipLights", "AreaColor",                 "sodium", colorDesc);
            _pointIntensityMult   = Config.Bind("ShipLights", "PointIntensityMultiplier",  5.0f,   "Multiply Point light intensity by this factor. 1.0 = unchanged.");
            _pointRangeMult       = Config.Bind("ShipLights", "PointRangeMultiplier",       2.0f,   "Multiply Point light range by this factor.");
            _pointColor           = Config.Bind("ShipLights", "PointColor",                "sodium", colorDesc);
            _shipSpotIntensityMult= Config.Bind("ShipLights", "SpotIntensityMultiplier",   5.0f,   "Multiply ship Spot light intensity by this factor (excludes bay/helmet). 1.0 = unchanged.");
            _shipSpotRangeMult    = Config.Bind("ShipLights", "SpotRangeMultiplier",        2.0f,   "Multiply ship Spot light range by this factor.");
            _shipSpotColor        = Config.Bind("ShipLights", "SpotColor",                 "sodium", colorDesc);
            _shipLightsNoShadows  = Config.Bind("ShipLights", "DisableShadows",            true,   "Disable shadows on all ship lights. Prevents flickering when range is boosted.");

            _fogMeanFreePath      = Config.Bind("Fog",        "MeanFreePathMultiplier",    1.0f,   "Multiply HDRP fog mean free path by this factor. Higher = clearer. 1.0 = unchanged.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            Log.LogInfo("BayLights loaded.");
        }

        // Warm sodium-yellow matching vanilla ship lights (~0.50, 0.38, 0.25 observed in-game)
        private static readonly Color SodiumColor = new Color(0.50f, 0.38f, 0.25f);

        private static bool TryParseColor(string value, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var v = value.Trim().ToLowerInvariant();
            switch (v)
            {
                case "white":   color = Color.white;                        return true;
                case "red":     color = Color.red;                          return true;
                case "green":   color = Color.green;                        return true;
                case "blue":    color = Color.blue;                         return true;
                case "yellow":  color = Color.yellow;                       return true;
                case "cyan":    color = Color.cyan;                         return true;
                case "magenta": color = Color.magenta;                      return true;
                case "orange":  color = new Color(1.0f, 0.5f, 0.0f);       return true;
                case "sodium":  color = SodiumColor;                        return true;
            }

            var parts = v.Split(',');
            if (parts.Length == 3
                && float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r)
                && float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g)
                && float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
            {
                color = new Color(r, g, b);
                return true;
            }

            Log.LogWarning($"[BayLights] Could not parse color '{value}'. Use R,G,B floats or a name like 'sodium', 'white', 'orange'.");
            return false;
        }

        private static readonly int EmissiveColorID = Shader.PropertyToID("_EmissiveColor");

        // Applies color to light.color + emissive MPB, but only if the light's parent has a
        // MeshRenderer (fixture light).
        private void ApplyLightColor(Light light, Color col)
        {
            var parent = light.transform.parent;
            if (parent == null) return;
            var renderer = parent.GetComponent<MeshRenderer>();
            if (renderer == null) return;
            light.color = col;
            ApplyEmissiveToRenderer(renderer, col);
            _colorEnforced[light] = (renderer, col);
        }

        private static void ApplyEmissiveToRenderer(MeshRenderer r, Color targetHue)
        {
            // The Lynx shader reads _EmissiveColor from the MPB, not the material property.
            // Read the MPB first to preserve the original in-game brightness; fall back to
            // the shared material if the MPB value is black (unset).
            var mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            var mpbEmissive = mpb.GetColor(EmissiveColorID);
            float brightness = Mathf.Max(mpbEmissive.r, mpbEmissive.g, mpbEmissive.b, 0.0001f);
            if (brightness < 0.01f)
            {
                var mat = r.sharedMaterials.Length > 0 ? r.sharedMaterials[0] : null;
                if (mat == null || !mat.HasProperty(EmissiveColorID)) return;
                mpbEmissive = mat.GetColor(EmissiveColorID);
                brightness = Mathf.Max(mpbEmissive.r, mpbEmissive.g, mpbEmissive.b, 0.0001f);
            }
            float hueMax = Mathf.Max(targetHue.r, targetHue.g, targetHue.b, 0.0001f);
            float scale = brightness / hueMax;
            var scaled = new Color(targetHue.r * scale, targetHue.g * scale, targetHue.b * scale, mpbEmissive.a);
            mpb.SetColor(EmissiveColorID, scaled);
            r.SetPropertyBlock(mpb);
        }

        private static string GetFullPath(Component c)
        {
            var t = c.transform;
            var path = t.name;
            while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
            return path;
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
            {
                _boosted.Clear();
                _colorEnforced.Clear();
                if (_enforceCoroutine != null) { StopCoroutine(_enforceCoroutine); _enforceCoroutine = null; }
            }
            StartCoroutine(BoostLights(scene.name));
            StartCoroutine(WatchLowSodiumColors());
        }

        private static string SnapshotLight(Light light)
        {
            var parent = light.transform.parent;
            var renderer = parent != null ? parent.GetComponent<MeshRenderer>() : null;
            var c = light.color;
            var sb = new System.Text.StringBuilder();
            sb.Append($"light=({c.r:F3},{c.g:F3},{c.b:F3})");
            if (renderer != null)
            {
                var mpb = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(mpb);
                var mpbE = mpb.GetColor(EmissiveColorID);
                sb.Append($" mpb=({mpbE.r:F3},{mpbE.g:F3},{mpbE.b:F3})");
                for (int mi = 0; mi < renderer.sharedMaterials.Length; mi++)
                {
                    var mat = renderer.sharedMaterials[mi];
                    if (mat == null) continue;
                    if (mat.HasProperty("_EmissiveColor")) { var e = mat.GetColor("_EmissiveColor"); sb.Append($" mat[{mi}]E=({e.r:F3},{e.g:F3},{e.b:F3})"); }
                    if (mat.HasProperty("_BaseColor"))     { var e = mat.GetColor("_BaseColor");     sb.Append($" mat[{mi}]B=({e.r:F3},{e.g:F3},{e.b:F3})"); }
                    if (mat.HasProperty("_Color"))         { var e = mat.GetColor("_Color");         sb.Append($" mat[{mi}]C=({e.r:F3},{e.g:F3},{e.b:F3})"); }
                }
            }
            return sb.ToString();
        }

        private IEnumerator WatchLowSodiumColors()
        {
            var lastSnapshot = new Dictionary<int, string>();
            var seenSnapshots = new HashSet<string>();
            float elapsed = 0f;
            while (elapsed < 60f)
            {
                foreach (var light in GetAllSceneLights())
                {
                    if (light == null || light.type != LightType.Spot) continue;
                    if (GetFullPath(light).IndexOf("SM_Light_LowSodium", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int id = light.GetInstanceID();
                    var snap = SnapshotLight(light);
                    if (lastSnapshot.TryGetValue(id, out string prev) && prev == snap) continue;
                    lastSnapshot[id] = snap;
                    if (!seenSnapshots.Add(snap)) continue;
                    Log.LogInfo($"[LowSodium t={elapsed:F2}s] {snap} from '{GetFullPath(light)}'");
                }
                yield return null;
                elapsed += Time.deltaTime;
            }
            Log.LogInfo($"[LowSodium] Watch complete. {seenSnapshots.Count} unique snapshot(s) observed.");
        }

        private IEnumerator EnforceColors()
        {
            var snapshot = new List<KeyValuePair<Light, (MeshRenderer renderer, Color color)>>();
            while (true)
            {
                yield return null;
                snapshot.Clear();
                snapshot.AddRange(_colorEnforced);
                foreach (var kv in snapshot)
                {
                    if (kv.Key == null) continue;
                    var (renderer, color) = kv.Value;
                    if (kv.Key.color != color)
                        kv.Key.color = color;
                    if (renderer != null)
                        ApplyEmissiveToRenderer(renderer, color);
                }
            }
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
            Log.LogInfo($"[BayLights] '{sceneName}': stable at {lastCount} lights after {elapsed:F0}s");

            var allLights = GetAllSceneLights();

            if (_logLights.Value)
            {
                Log.LogInfo($"[BayLights] '{sceneName}' (all scenes): found {allLights.Count} Light(s) total:");
                foreach (var l in allLights)
                {
                    var pos = l.transform.position;
                    var c = l.color;
                    Log.LogInfo($"  [{l.type}] '{GetFullPath(l)}' | intensity={l.intensity} range={l.range} spotAngle={l.spotAngle} color=({c.r:F2},{c.g:F2},{c.b:F2}) pos=({pos.x:F1},{pos.y:F1},{pos.z:F1}) active={l.gameObject.activeInHierarchy}");
                }
            }

            int bayCount = 0, helmetCount = 0, shipCount = 0;
            foreach (var light in allLights)
            {
                int id = light.GetInstanceID();
                bool alreadyBoosted = _boosted.Contains(id);

                if (light.type == LightType.Spot)
                {
                    if (light.name == "Helmet Spotlight")
                    {
                        if (!alreadyBoosted)
                        {
                            light.range     *= _helmetRangeMult.Value;
                            light.intensity *= _helmetIntensityMult.Value;
                            _boosted.Add(id);
                            helmetCount++;
                        }
                    }
                    else if (light.spotAngle <= _maxAngle.Value
                        || light.name.StartsWith("Spot Light",  System.StringComparison.OrdinalIgnoreCase)
                        || light.name.StartsWith("Point Light", System.StringComparison.OrdinalIgnoreCase))
                    {
                        if (!alreadyBoosted)
                        {
                            light.range     *= _rangeMult.Value;
                            light.intensity *= _intensityMult.Value;
                            if (_coneAngle.Value >= 1f && _coneAngle.Value <= 179f)
                                light.spotAngle = _coneAngle.Value;
                            _boosted.Add(id);
                            bayCount++;
                        }
                    }
                    else
                    {
                        if (!alreadyBoosted)
                        {
                            light.intensity *= _shipSpotIntensityMult.Value;
                            light.range     *= _shipSpotRangeMult.Value;
                            if (_shipLightsNoShadows.Value) light.shadows = LightShadows.None;
                            _boosted.Add(id);
                        }
                        shipCount++;
                    }
                }
                else if (light.type == LightType.Area)
                {
                    if (!alreadyBoosted)
                    {
                        light.intensity *= _areaIntensityMult.Value;
                        light.range     *= _areaRangeMult.Value;
                        if (_shipLightsNoShadows.Value) light.shadows = LightShadows.None;
                        _boosted.Add(id);
                    }
                    shipCount++;
                }
                else if (light.type == LightType.Point)
                {
                    if (!alreadyBoosted)
                    {
                        light.intensity *= _pointIntensityMult.Value;
                        light.range     *= _pointRangeMult.Value;
                        if (_shipLightsNoShadows.Value) light.shadows = LightShadows.None;
                        _boosted.Add(id);
                    }
                    shipCount++;
                }
            }

            if (bayCount > 0 || helmetCount > 0 || shipCount > 0)
                Log.LogInfo($"[BayLights] '{sceneName}': {bayCount} bay lights, {helmetCount} helmet lights, {shipCount} ship lights modified.");


            if (_colorEnforced.Count > 0)
            {
                if (_enforceCoroutine != null) StopCoroutine(_enforceCoroutine);
                _enforceCoroutine = StartCoroutine(EnforceColors());
            }

            if (_fogMeanFreePath.Value != 1.0f)
            {
                int fogCount = 0;
                foreach (var volume in GetAllSceneVolumes())
                {
                    if (volume.profile != null && volume.profile.TryGet<Fog>(out var fog))
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
