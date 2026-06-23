using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.Collections;
using System.Collections.Generic;
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
        private ConfigEntry<float>  _rangeMult            = null!;
        private ConfigEntry<float>  _intensityMult        = null!;
        private ConfigEntry<float>  _coneAngle            = null!;
        private ConfigEntry<float>  _maxAngle             = null!;
        private ConfigEntry<float>  _helmetRangeMult      = null!;
        private ConfigEntry<float>  _helmetIntensityMult  = null!;
        private ConfigEntry<float>  _fogMeanFreePath      = null!;
        private ConfigEntry<float>  _areaIntensityMult    = null!;
        private ConfigEntry<float>  _areaRangeMult        = null!;
        private ConfigEntry<float>  _pointIntensityMult   = null!;
        private ConfigEntry<float>  _pointRangeMult       = null!;
        private ConfigEntry<float>  _shipSpotIntensityMult = null!;
        private ConfigEntry<float>  _shipSpotRangeMult    = null!;
        private ConfigEntry<bool>   _shipLightsNoShadows  = null!;

        private readonly HashSet<int> _boosted = new HashSet<int>();

        private void Awake()
        {
            Log = Logger;

            _enabled              = Config.Bind("General",    "Enabled",                  true,    "Enable or disable BayLights.");

            _rangeMult            = Config.Bind("BayLights",  "RangeMultiplier",           2.0f,   "Multiply bay spotlight range by this factor.");
            _intensityMult        = Config.Bind("BayLights",  "IntensityMultiplier",       1.0f,   "Multiply bay spotlight intensity by this factor. 1.0 = unchanged.");
            _coneAngle            = Config.Bind("BayLights",  "ConeAngle",                -1.0f,   "Override bay spotlight cone angle in degrees (1-179). -1 = leave unchanged.");
            _maxAngle             = Config.Bind("BayLights",  "MaxAngle",                 60.0f,   "Only boost spotlights whose original cone angle is at or below this value.");

            _helmetRangeMult      = Config.Bind("Helmet",     "RangeMultiplier",           2.0f,   "Multiply helmet spotlight range by this factor.");
            _helmetIntensityMult  = Config.Bind("Helmet",     "IntensityMultiplier",       1.0f,   "Multiply helmet spotlight intensity by this factor. 1.0 = unchanged.");

            _areaIntensityMult    = Config.Bind("ShipLights", "AreaIntensityMultiplier",   5.0f,   "Multiply Area light (e.g. sodium) intensity by this factor. 1.0 = unchanged.");
            _areaRangeMult        = Config.Bind("ShipLights", "AreaRangeMultiplier",        2.0f,   "Multiply Area light range by this factor.");
            _pointIntensityMult   = Config.Bind("ShipLights", "PointIntensityMultiplier",  5.0f,   "Multiply Point light intensity by this factor. 1.0 = unchanged.");
            _pointRangeMult       = Config.Bind("ShipLights", "PointRangeMultiplier",       2.0f,   "Multiply Point light range by this factor.");
            _shipSpotIntensityMult= Config.Bind("ShipLights", "SpotIntensityMultiplier",   5.0f,   "Multiply ship Spot light intensity by this factor (excludes bay/helmet). 1.0 = unchanged.");
            _shipSpotRangeMult    = Config.Bind("ShipLights", "SpotRangeMultiplier",        2.0f,   "Multiply ship Spot light range by this factor.");
            _shipLightsNoShadows  = Config.Bind("ShipLights", "DisableShadows",            true,   "Disable shadows on all ship lights. Prevents flickering when range is boosted.");

            _fogMeanFreePath      = Config.Bind("Fog",        "MeanFreePathMultiplier",    1.0f,   "Multiply HDRP fog mean free path by this factor. Higher = clearer. 1.0 = unchanged.");

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
