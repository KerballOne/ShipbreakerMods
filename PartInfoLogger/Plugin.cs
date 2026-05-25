using System.Collections.Generic;
using System.IO;
using BBI.Unity.Game;
using BBI.Unity.Game.Utilities;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Carbon.Localization.Core;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace PartInfoLogger
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static ConfigEntry<string> OutputPath = null!;

        private void Awake()
        {
            Log = Logger;
            OutputPath = Config.Bind(
                "General", "OutputPath",
                Path.Combine(Paths.GameRootPath, "known_assets_enriched.json"),
                "Full path to write the enriched asset JSON. " +
                "Point this to your ShipbreakerShipbuilder project root to use it directly in the editor.");

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            State.LoadExisting();
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} loaded. Writing to: {OutputPath.Value}");
        }

        private void OnApplicationQuit() => State.Flush(force: true);
        private void OnDestroy() => State.Flush(force: true);
    }

    [HarmonyPatch(typeof(RigidbodyUtils), nameof(RigidbodyUtils.CalculateMassFromVolume))]
    static class Patch_Mass
    {
        static void Postfix(GameObject obj, float __result)
        {
            if (obj == null || __result <= 0f) return;
            // Store by the raw name as a fallback
            State.PendingMass[obj.name.Replace("(Clone)", "").Trim()] = __result;

            // Walk up to the PRF_ root so mass is keyed by the same name StructurePart uses.
            // Also updates any already-captured entry if StructurePart.Start fired first.
            var t = obj.transform;
            for (int depth = 0; depth < 20 && t != null; depth++, t = t.parent)
            {
                var candidate = t.gameObject.name.Replace("(Clone)", "").Trim();
                var guid = State.LookupGuid(candidate);
                if (string.IsNullOrEmpty(guid)) continue;
                State.PendingMass[candidate] = __result;
                State.TrySetMass(guid!, __result);
                break;
            }
        }
    }

    [HarmonyPatch(typeof(StructurePart), "Start")]
    static class Patch_StructurePart
    {
        static void Postfix(StructurePart __instance)
        {
            var go = __instance.gameObject;

            // StructurePart fires on SM_ mesh children, not on the PRF_ prefab root.
            // Walk up the hierarchy to find the nearest ancestor whose name is a known prefab.
            GameObject? root = null;
            string? guid = null;
            var t = go.transform;
            for (int depth = 0; depth < 20 && t != null; depth++, t = t.parent)
            {
                var candidate = t.gameObject.name.Replace("(Clone)", "").Trim();
                guid = State.LookupGuid(candidate);
                if (!string.IsNullOrEmpty(guid))
                {
                    root = t.gameObject;
                    break;
                }
            }
            if (root == null || string.IsNullOrEmpty(guid)) return;

            // Only capture each prefab once — first StructurePart child wins
            if (State.AlreadyCaptured(guid!)) return;

            // Get display name from this StructurePart's ObjectInfoAsset (or root's if available).
            // Generic category labels (Small Part, Medium Part, etc.) are not useful — leave blank.
            string? displayName = null;
            var rootSP = root.GetComponent<StructurePart>() ?? __instance;
            string? rawName = rootSP.ObjectInfoAsset?.Data.ObjectName;
            if (!string.IsNullOrEmpty(rawName))
            {
                try
                {
                    var loc = Main.Instance?.LocalizationService;
                    string? resolved = null;
                    if (loc != null && loc.TryLocalize(rawName, out var localized) && !string.IsNullOrEmpty(localized))
                        resolved = localized;
                    else
                        resolved = rawName;

                    if (!string.IsNullOrEmpty(resolved) && !State.IsGenericLabel(resolved!))
                        displayName = resolved;
                }
                catch { }
            }

            var rootName = root.name.Replace("(Clone)", "").Trim();
            float mass = State.PendingMass.TryGetValue(rootName, out var m) ? m : 0f;

            // Get aggregate bounds from the prefab root — covers all child meshes at once
            float[]? dims = null;
            float volume = 0f;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    b.Encapsulate(renderers[i].bounds);
                float x = (float)System.Math.Round(b.size.x, 2);
                float y = (float)System.Math.Round(b.size.y, 2);
                float z = (float)System.Math.Round(b.size.z, 2);
                dims = new[] { x, y, z };
                volume = (float)System.Math.Round(x * y * z, 3);
            }

            State.Upsert(guid!, rootName, displayName, dims, volume, mass);
        }
    }

    static class State
    {
        internal static readonly Dictionary<string, float> PendingMass = new();
        private static Dictionary<string, PartData> _data = new();
        private static Dictionary<string, string> _nameToGuid = new();
        private static int _newSinceFlush;
        private const int FlushEvery = 20;

        internal static void LoadExisting()
        {
            var path = Plugin.OutputPath.Value;
            if (File.Exists(path))
            {
                try
                {
                    _data = JsonConvert.DeserializeObject<Dictionary<string, PartData>>(
                        File.ReadAllText(path)) ?? new Dictionary<string, PartData>();
                    Plugin.Log.LogInfo($"Loaded {_data.Count} existing enriched entries.");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Could not read existing enriched data: {e.Message}");
                }
            }

            // Build reverse map: prefab name → guid, from known_assets.json in same directory
            var knownPath = Path.Combine(Path.GetDirectoryName(path)!, "known_assets.json");
            if (File.Exists(knownPath))
            {
                try
                {
                    var raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(knownPath));
                    if (raw != null)
                    {
                        foreach (var kv in raw)
                        {
                            var name = Path.GetFileNameWithoutExtension(kv.Value);
                            if (!string.IsNullOrEmpty(name))
                                _nameToGuid[name] = kv.Key;
                        }
                        Plugin.Log.LogInfo($"Built GUID reverse map: {_nameToGuid.Count} entries.");
                    }
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Could not build GUID map from {knownPath}: {e.Message}");
                }
            }
            else
            {
                Plugin.Log.LogWarning($"known_assets.json not found at {knownPath} — GUID lookups will fail.");
            }
        }

        internal static bool AlreadyCaptured(string guid) => _data.ContainsKey(guid);

        internal static void TrySetMass(string guid, float mass)
        {
            if (!_data.TryGetValue(guid, out var entry) || entry.Mass == mass) return;
            entry.Mass = mass;
            _data[guid] = entry;
            if (++_newSinceFlush >= FlushEvery) Flush();
        }

        private static readonly HashSet<string> s_GenericLabels = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { "Small Part", "Medium Part", "Large Part", "Part", "Structure" };
        internal static bool IsGenericLabel(string name) => s_GenericLabels.Contains(name);

        internal static string? LookupGuid(string goName)
        {
            _nameToGuid.TryGetValue(goName, out var g);
            return g;
        }

        internal static void Upsert(string guid, string partName, string? displayName, float[]? dims, float volume, float mass)
        {
            if (!_data.TryGetValue(guid, out var entry))
                entry = new PartData();

            entry.PartName = partName;
            if (!string.IsNullOrEmpty(displayName)) entry.DisplayName = displayName!;
            if (dims != null) { entry.Dims = dims; entry.Volume = volume; }
            if (mass > 0f) entry.Mass = mass;

            _data[guid] = entry;
            if (++_newSinceFlush >= FlushEvery)
                Flush();
        }

        internal static void Flush(bool force = false)
        {
            if (_newSinceFlush == 0 && !force) return;
            try
            {
                var path = Plugin.OutputPath.Value;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                bool first = true;
                foreach (var kv in _data)
                {
                    if (!first) sb.AppendLine(",");
                    sb.Append($"  {JsonConvert.SerializeObject(kv.Key)}: {JsonConvert.SerializeObject(kv.Value, Formatting.None)}");
                    first = false;
                }
                sb.AppendLine();
                sb.Append("}");
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"Saved {_data.Count} enriched entries to {path}");
                _newSinceFlush = 0;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Failed to save enriched data: {e.Message}");
            }
        }
    }

    class PartData
    {
        [JsonProperty("partName")]    public string  PartName    = null!;
        [JsonProperty("displayName")] public string  DisplayName = null!;
        [JsonProperty("dims")]        public float[] Dims        = null!;
        [JsonProperty("volume")]      public float   Volume;
        [JsonProperty("mass")]        public float   Mass;
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID    = "me.kerballone.PartInfoLogger";
        public const string PLUGIN_NAME    = "PartInfoLogger";
        public const string PLUGIN_VERSION = "1.0.0";
    }
}
