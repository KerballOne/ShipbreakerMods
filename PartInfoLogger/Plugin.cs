using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        internal static Plugin Instance = null!;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            OutputPath = Config.Bind(
                "General", "OutputPath",
                Path.Combine(Paths.GameRootPath, "known_assets_enriched.json"),
                "Full path to write the enriched asset JSON. " +
                "Point this to your ShipbreakerShipbuilder project root to use it directly in the editor.");

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();

            State.LoadExisting();
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} loaded. Writing to: {OutputPath.Value}");

            Main.EventSystem.AddHandler((GameStateChangedEvent ev) =>
            {
                if (ev.GameState == GameSession.GameState.Gameplay)
                {
                    Instance.StartCoroutine(FlushJsaDelayed());
                    Instance.StartCoroutine(TryDumpJsaOnGameplay());
                }
            });

            // Try dumping JSA table immediately via reflection on any loaded JointabilityAsset
            Instance.StartCoroutine(TryDumpJsaFromResources());
        }

        private void OnApplicationQuit() { State.Flush(force: true); JsaCompatState.Flush(); }
        private void OnDestroy()         { State.Flush(force: true); JsaCompatState.Flush(); }

        static IEnumerator TryDumpJsaFromResources()
        {
            // Wait a few frames for addressables to load assets
            yield return new WaitForSeconds(2f);
            var jsaType = AccessTools.TypeByName("BBI.Unity.Game.JointabilityAsset");
            if (jsaType == null) { Plugin.Log.LogWarning("[JsaCompat] JointabilityAsset type not found for resource scan"); yield break; }
            var all = Resources.FindObjectsOfTypeAll(jsaType);
            Plugin.Log.LogInfo($"[JsaCompat] Found {all.Length} JointabilityAsset instance(s) via Resources at t=2s");
            foreach (var obj in all)
                JsaCompatState.TryDumpFull(obj);
        }

        internal static IEnumerator TryDumpJsaOnGameplay()
        {
            yield return new WaitForSeconds(1f);
            var jsaType = AccessTools.TypeByName("BBI.Unity.Game.JointabilityAsset");
            if (jsaType == null) { Plugin.Log.LogWarning("[JsaCompat] JointabilityAsset type not found for gameplay scan"); yield break; }
            var all = Resources.FindObjectsOfTypeAll(jsaType);
            Plugin.Log.LogInfo($"[JsaCompat] Found {all.Length} JointabilityAsset instance(s) via Resources at gameplay+1s");
            foreach (var obj in all)
                JsaCompatState.TryDumpFull(obj);
        }

        static IEnumerator FlushJsaDelayed()
        {
            yield return new WaitForSeconds(5f);
            Plugin.Log.LogInfo($"[JsaCompat] Delayed flush triggered, pairs so far: {JsaCompatState.PairCount}");
            JsaCompatState.Flush();
        }
    }

    // ── Existing patches ─────────────────────────────────────────────────────

    [HarmonyPatch(typeof(RigidbodyUtils), nameof(RigidbodyUtils.CalculateMassFromVolume))]
    static class Patch_Mass
    {
        static void Postfix(GameObject obj, float __result)
        {
            if (obj == null || __result <= 0f) return;
            State.PendingMass[obj.name.Replace("(Clone)", "").Trim()] = __result;

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
            if (State.AlreadyCaptured(guid!) && State.HasJsaName(guid!)) return;

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

            float[]? dims = null;
            float volume = 0f;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    b.Encapsulate(renderers[i].bounds);
                float x = (float)Math.Round(b.size.x, 2);
                float y = (float)Math.Round(b.size.y, 2);
                float z = (float)Math.Round(b.size.z, 2);
                dims = new[] { x, y, z };
                volume = (float)Math.Round(x * y * z, 3);
            }

            // Capture JSA name from any StructurePart in this prefab's hierarchy
            string? jsaName = null;
            foreach (var sp in root.GetComponentsInChildren<StructurePart>(true))
            {
                var jsa = sp.StructurePartAsset?.Data?.JointSetupAsset;
                if (jsa != null) { jsaName = jsa.name; break; }
            }

            // Log SP and BP asset names to confirm whether ACL has fired by the time Start() runs
            var spAsset = __instance.StructurePartAsset;
            var bpComp = go.GetComponent<EntityBlueprintComponent>();
            var bpAsset = bpComp != null ? (UnityEngine.Object)AccessTools.Field(typeof(EntityBlueprintComponent), "m_BlueprintAsset")?.GetValue(bpComp) : null;
            Plugin.Log.LogInfo($"[SP.Start] {go.name}: SP={spAsset?.name ?? "null"} BP={bpAsset?.name ?? "null"}");

            State.Upsert(guid!, rootName, displayName, dims, volume, mass, jsaName);
        }
    }

    // ── Cryo flag diagnostic ──────────────────────────────────────────────────

    [HarmonyPatch]
    static class Patch_MachinePartAsset_SetComponentData
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("BBI.Unity.Game.MachinePartAsset");
            if (t == null) { Plugin.Log.LogWarning("[CryoFlags] MachinePartAsset type not found"); return null!; }
            var m = AccessTools.Method(t, "SetComponentData");
            if (m == null) { Plugin.Log.LogWarning("[CryoFlags] SetComponentData not found"); return null!; }
            Plugin.Log.LogInfo($"[CryoFlags] Patching {t.FullName}.{m.Name}");
            return m;
        }

        static void Postfix(object __instance)
        {
            try
            {
                var assetType = __instance.GetType();
                var dataField = assetType.GetField("Data", BindingFlags.Public | BindingFlags.Instance);
                if (dataField == null) return;
                var data = dataField.GetValue(__instance);
                if (data == null) return;
                var dataType = data.GetType();
                var cryoProp = dataType.GetProperty("CryoControl", BindingFlags.Public | BindingFlags.Instance);
                if (cryoProp == null) return;
                var cryoVal = (int)cryoProp.GetValue(data);
                var name = (__instance as UnityEngine.Object)?.name ?? "?";
                Plugin.Log.LogInfo($"[CryoFlags] {name}: CryoControl={cryoVal} " +
                    $"(AllowsFlow={(cryoVal & 1) != 0}, ReceivesGlobally={(cryoVal & 2) != 0}, " +
                    $"ReceivesLocally={(cryoVal & 4) != 0}, SourcesGlobally={(cryoVal & 8) != 0}, " +
                    $"SourcesLocally={(cryoVal & 0x10) != 0})");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[CryoFlags] ex: {ex.Message}"); }
        }
    }

    // ── JSA compatibility dump ────────────────────────────────────────────────

    // Patches JointabilityAsset.CanJoint to record every (jsa1, jsa2) → bool pair
    // seen during the ship's jointing pass, then writes jsa_compat.json.
    [HarmonyPatch]
    static class Patch_JointabilityAsset_CanJoint
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("BBI.Unity.Game.JointabilityAsset");
            if (t == null) { Plugin.Log.LogWarning("[JsaCompat] BBI.Unity.Game.JointabilityAsset type not found!"); return null!; }
            var m = AccessTools.Method(t, "CanJoint");
            if (m == null) { Plugin.Log.LogWarning("[JsaCompat] JointabilityAsset.CanJoint method not found!"); return null!; }
            Plugin.Log.LogInfo($"[JsaCompat] Patching {t.FullName}.{m.Name}");
            return m;
        }

        static void Postfix(object __instance, object[] __args, bool __result)
        {
            var jsa1 = __args?.Length > 0 ? __args[0] : null;
            var jsa2 = __args?.Length > 1 ? __args[1] : null;
            if (JsaCompatState.PairCount == 0)
                Plugin.Log.LogInfo($"[JsaCompat] First CanJoint call: {jsa1?.GetType().Name ?? "null"} vs {jsa2?.GetType().Name ?? "null"} = {__result}");
            JsaCompatState.Record(jsa1, jsa2, __result);
        }
    }

    static class JsaCompatState
    {
        // (sortedName1, sortedName2) -> compatible
        static readonly Dictionary<(string, string), bool> _pairs = new();
        public static int PairCount => _pairs.Count;
        static bool _fullDumped = false;
        static int _newSinceFlush = 0;
        const int FlushEvery = 50;

        static string JsaName(object jsa)
        {
            if (jsa == null) return "null";
            // JointSetupAsset is a ScriptableObject — name field is reliable
            var nameField = jsa.GetType().GetProperty("name",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            return nameField?.GetValue(jsa) as string ?? jsa.GetType().Name;
        }

        public static void Record(object jsa1, object jsa2, bool result)
        {
            string a = JsaName(jsa1), b = JsaName(jsa2);
            var key = string.Compare(a, b, StringComparison.Ordinal) <= 0 ? (a, b) : (b, a);
            if (_pairs.TryGetValue(key, out var existing) && existing == result) return;
            _pairs[key] = result;
            if (++_newSinceFlush >= FlushEvery) Flush();
        }

        // Try to extract the full table via reflection rather than waiting for CanJoint calls
        public static void TryDumpFull(object asset)
        {
            if (_fullDumped) return;
            try
            {
                var t = asset.GetType();
                // Look for a list/array field that holds pairs of JointSetupAssets
                foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                {
                    var val = f.GetValue(asset);
                    if (val == null) continue;
                    var ft = val.GetType();
                    if (!ft.IsGenericType) continue;
                    var args = ft.GetGenericArguments();
                    if (args.Length != 1) continue;
                    // Each element should have two JSA references
                    var list = val as System.Collections.IEnumerable;
                    if (list == null) continue;

                    bool anyPair = false;
                    foreach (var item in list)
                    {
                        if (item == null) continue;
                        var fields = item.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        var jsaFields = fields.Where(ff =>
                        {
                            var fval = ff.GetValue(item);
                            return fval != null && ff.FieldType.Name.Contains("JointSetupAsset");
                        }).ToList();

                        if (jsaFields.Count < 2) continue;
                        anyPair = true;

                        var a = jsaFields[0].GetValue(item);
                        var b = jsaFields[1].GetValue(item);
                        // Assume all entries in the list are compatible pairs
                        string nameA = JsaName(a), nameB = JsaName(b);
                        var key = string.Compare(nameA, nameB, StringComparison.Ordinal) <= 0 ? (nameA, nameB) : (nameB, nameA);
                        _pairs[key] = true;
                    }

                    if (anyPair)
                    {
                        Plugin.Log.LogInfo($"[JsaCompat] Full dump via reflection: field '{f.Name}', {_pairs.Count} pairs");
                        _fullDumped = true;
                        Flush();
                        return;
                    }
                }
                Plugin.Log.LogInfo("[JsaCompat] Could not find pairing list via reflection — will accumulate from CanJoint calls.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[JsaCompat] TryDumpFull error: {ex.Message}");
            }
        }

        public static void Flush()
        {
            if (_pairs.Count == 0) return;
            try
            {
                var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
                var path = Path.Combine(outDir, "jsa_compat.json");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                bool first = true;
                foreach (var kv in _pairs)
                {
                    if (!first) sb.AppendLine(",");
                    var key = $"{kv.Key.Item1}|{kv.Key.Item2}";
                    sb.Append($"  {JsonConvert.SerializeObject(key)}: {(kv.Value ? "true" : "false")}");
                    first = false;
                }
                sb.AppendLine();
                sb.Append("}");
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[JsaCompat] Wrote {_pairs.Count} pairs to {path}");
                _newSinceFlush = 0;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[JsaCompat] Flush error: {ex.Message}");
            }
        }
    }

    // ── State ─────────────────────────────────────────────────────────────────

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
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Could not read existing enriched data: {e.Message}");
                }
            }

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
                catch (Exception e)
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
        internal static bool HasJsaName(string guid) => _data.TryGetValue(guid, out var e) && !string.IsNullOrEmpty(e.JsaName);

        internal static void TrySetMass(string guid, float mass)
        {
            if (!_data.TryGetValue(guid, out var entry) || entry.Mass == mass) return;
            entry.Mass = mass;
            _data[guid] = entry;
            if (++_newSinceFlush >= FlushEvery) Flush();
        }

        private static readonly HashSet<string> s_GenericLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Small Part", "Medium Part", "Large Part", "Part", "Structure" };
        internal static bool IsGenericLabel(string name) => s_GenericLabels.Contains(name);

        internal static string? LookupGuid(string goName)
        {
            _nameToGuid.TryGetValue(goName, out var g);
            return g;
        }

        internal static void Upsert(string guid, string partName, string? displayName, float[]? dims, float volume, float mass, string? jsaName = null)
        {
            if (!_data.TryGetValue(guid, out var entry))
                entry = new PartData();

            entry.PartName = partName;
            if (!string.IsNullOrEmpty(displayName)) entry.DisplayName = displayName!;
            if (dims != null) { entry.Dims = dims; entry.Volume = volume; }
            if (mass > 0f) entry.Mass = mass;
            if (!string.IsNullOrEmpty(jsaName)) entry.JsaName = jsaName!;

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
            catch (Exception e)
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
        [JsonProperty("jsaName", NullValueHandling = NullValueHandling.Ignore)]
                                      public string? JsaName;
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID    = "me.kerballone.PartInfoLogger";
        public const string PLUGIN_NAME    = "PartInfoLogger";
        public const string PLUGIN_VERSION = "1.0.0";
    }
}
