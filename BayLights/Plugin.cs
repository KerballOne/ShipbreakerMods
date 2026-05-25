using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.Collections;
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

        private ConfigEntry<bool>  _enabled            = null!;
        private ConfigEntry<float> _rangeMult          = null!;
        private ConfigEntry<float> _intensityMult      = null!;
        private ConfigEntry<float> _coneAngle          = null!;
        private ConfigEntry<float> _maxAngle           = null!;
        private ConfigEntry<float> _helmetRangeMult    = null!;
        private ConfigEntry<float> _helmetIntensityMult= null!;
        private ConfigEntry<float> _fogMeanFreePath    = null!;

        private void Awake()
        {
            Log = Logger;

            _enabled             = Config.Bind("General", "Enabled",                 true,   "Enable or disable BayLights.");
            _rangeMult           = Config.Bind("BayLights", "RangeMultiplier",       2.0f,  "Multiply bay spotlight range by this factor.");
            _intensityMult       = Config.Bind("BayLights", "IntensityMultiplier",   1.0f,  "Multiply bay spotlight intensity by this factor. 1.0 = unchanged.");
            _coneAngle           = Config.Bind("BayLights", "ConeAngle",            -1.0f,  "Override bay spotlight cone angle in degrees (1–179). -1 = leave unchanged.");
            _maxAngle            = Config.Bind("BayLights", "MaxAngle",             60.0f,  "Only boost spotlights whose original cone angle is at or below this value.");
            _helmetRangeMult     = Config.Bind("Helmet",    "RangeMultiplier",       2.0f,  "Multiply helmet spotlight range by this factor.");
            _helmetIntensityMult = Config.Bind("Helmet",    "IntensityMultiplier",   1.0f,  "Multiply helmet spotlight intensity by this factor. 1.0 = unchanged.");
            _fogMeanFreePath     = Config.Bind("Fog",       "MeanFreePathMultiplier",1.0f,  "Multiply HDRP fog mean free path by this factor. Higher = clearer (less fog). 1.0 = unchanged.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            Log.LogInfo("BayLights loaded.");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_enabled.Value) return;
            StartCoroutine(BoostLights(scene.name));
        }

        private IEnumerator BoostLights(string sceneName)
        {
            yield return null; // one frame — let scene scripts finish Start() before we read lights

            int bayCount = 0, helmetCount = 0;
            foreach (var light in FindObjectsOfType<Light>())
            {
                if (light.type != LightType.Spot) continue;

                if (light.spotAngle <= _maxAngle.Value
                    || light.name.StartsWith("Spot Light", System.StringComparison.OrdinalIgnoreCase)
                    || light.name.StartsWith("Point Light", System.StringComparison.OrdinalIgnoreCase))
                {
                    light.range     *= _rangeMult.Value;
                    light.intensity *= _intensityMult.Value;
                    if (_coneAngle.Value >= 1f && _coneAngle.Value <= 179f)
                        light.spotAngle = _coneAngle.Value;
                    bayCount++;
                }
                else if (light.name == "Helmet Spotlight")
                {
                    light.range     *= _helmetRangeMult.Value;
                    light.intensity *= _helmetIntensityMult.Value;
                    helmetCount++;
                }
            }

            if (bayCount > 0 || helmetCount > 0)
                Log.LogInfo($"[BayLights] '{sceneName}': {bayCount} bay lights, {helmetCount} helmet lights modified.");

            if (_fogMeanFreePath.Value != 1.0f)
            {
                int fogCount = 0;
                foreach (var volume in FindObjectsOfType<Volume>())
                {
                    if (volume.profile != null && volume.profile.TryGet<Fog>(out var fog))
                    {
                        fog.meanFreePath.value *= _fogMeanFreePath.Value;
                        fogCount++;
                    }
                }
                if (fogCount > 0)
                    Log.LogInfo($"[BayLights] Modified fog in {fogCount} volume(s) (meanFreePath ×{_fogMeanFreePath.Value}).");
            }
        }
    }
}
