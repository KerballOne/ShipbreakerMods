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
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace PartInfoLogger
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static ConfigEntry<string> OutputPath = null!;
        internal static ConfigEntry<bool> EnrichmentEnabled = null!;
        internal static ConfigEntry<bool> JointCensusEnabled = null!;
        internal static ConfigEntry<float> JointCensusDelay = null!;
        internal static ConfigEntry<bool> CampaignProgressEnabled = null!;
        internal static ConfigEntry<bool> SkipDiagnosticsEnabled = null!;
        internal static ConfigEntry<bool> MeshDiagnosticsEnabled = null!;
        internal static ConfigEntry<string> MeshDiagnosticsNamePrefixes = null!;
        internal static ConfigEntry<bool> PositionDriftEnabled = null!;
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

            EnrichmentEnabled = Config.Bind(
                "General", "EnrichmentEnabled", false,
                "If true, captures per-part enrichment data (known_assets_enriched.json, sp_jsa_map.json, " +
                "sp_bp_fields.json) via per-instantiation Harmony patches and gameplay-start reflection scans. " +
                "Adds real load-time cost. Leave false once those files are already populated for your project — " +
                "re-enable only when you need to refresh them (e.g. after new parts are added to the game).");

            JointCensusEnabled = Config.Bind(
                "JointCensus", "Enabled", true,
                "If true, dumps a CSV of every part's name and its jointed-neighbor count (excluding InvisibleJoint markers) on gameplay start.");
            JointCensusDelay = Config.Bind(
                "JointCensus", "DelaySeconds", 10f,
                "How many seconds after gameplay start to run the full-ship joint census.");

            CampaignProgressEnabled = Config.Bind(
                "CampaignProgress", "Enabled", false,
                "If true, dumps campaign_progress.json on gameplay start: every PlayerActionTrackerAsset the " +
                "current save has recorded (name -> count), plus every IndustrialActionShipAsset's PATConditionAsset " +
                "requirements evaluated against that history (met/unmet), and whether each ship has already been " +
                "generated in PlayerProfile.IndustrialActionShips. QC/progression-investigation tooling -- leave " +
                "off for normal use.");

            SkipDiagnosticsEnabled = Config.Bind(
                "SkipDiagnostics", "Enabled", false,
                "If true, independently logs every PlayerActionTrackerEvent posted by the game (name, op, value, " +
                "IsResetAction) and every Hab3DController/HabCustomGreetingController after-shift-scene state " +
                "transition, regardless of whether QuickCutscene's own debug logging fires. Built to verify " +
                "QuickCutscene skip behavior without relying on QC's own (possibly incomplete) log output — " +
                "see project_industrial_action_investigation memory. Adds per-frame overhead while an after-shift " +
                "scene or greeting is active; leave off otherwise.");

            MeshDiagnosticsEnabled = Config.Bind(
                "MeshDiagnostics", "Enabled", true,
                "If true, dumps mesh_diagnostics.csv on gameplay start: vertex/triangle count, render bounds, " +
                "and collider type/convexity/bounds for every StructurePart matching MeshDiagnosticsNamePrefixes. " +
                "Built to isolate mesh-only differences (e.g. a degenerate collider hull) between otherwise-" +
                "identical part instances where some joint in-game and others don't. Leave off for normal use.");
            MeshDiagnosticsNamePrefixes = Config.Bind(
                "MeshDiagnostics", "NamePrefixes", "Thermal_Couple,Thermal_Radiator",
                "Comma-separated StructurePart name prefixes to include in mesh_diagnostics.csv.");

            PositionDriftEnabled = Config.Bind(
                "PositionDrift", "Enabled", true,
                "If true, dumps position_drift.csv on gameplay start: world position at spawn vs. after " +
                "JointCensusDelay, and the displacement, for every StructurePart matching MeshDiagnosticsNamePrefixes. " +
                "Built to detect whether a part that fails to auto-joint was pushed away by a physics separation " +
                "impulse from excess mesh-collider overlap at spawn. Leave off for normal use.");

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
                    if (EnrichmentEnabled.Value)
                    {
                        Instance.StartCoroutine(TryDumpAllSpAssets());
                        Instance.StartCoroutine(TryDumpSpBpFields());
                    }
                    Instance.StartCoroutine(DumpMaterialProperties());
                    Instance.StartCoroutine(DumpJointCensus());
                    Instance.StartCoroutine(PickupInspector.DumpAllInteractables());
                    if (MeshDiagnosticsEnabled.Value || PositionDriftEnabled.Value)
                    {
                        var prefixes = MeshDiagnosticsNamePrefixes.Value
                            .Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                        if (MeshDiagnosticsEnabled.Value)
                            Instance.StartCoroutine(DumpMeshDiagnostics(prefixes, JointCensusDelay.Value));
                        if (PositionDriftEnabled.Value)
                            Instance.StartCoroutine(DumpPositionDrift(prefixes, JointCensusDelay.Value));
                    }
                }
                else if (ev.GameState == GameSession.GameState.Hab)
                {
                    if (CampaignProgressEnabled.Value)
                    {
                        Log.LogInfo($"[CampaignProgress] Hab state reached, CampaignProgressEnabled={CampaignProgressEnabled.Value}");
                        Instance.StartCoroutine(DumpCampaignProgress());
                        Instance.StartCoroutine(DumpCertificationLevels());
                        Instance.StartCoroutine(DumpTriggerConditions());
                    }
                }
            });

            // Independent, QC-agnostic ground truth for every PAT the game actually posts. Registered
            // via reflection (PlayerActionTrackerEvent is BBI.Unity.Game, not referenced by this project)
            // against Main.EventSystem the same way GameStateChangedEvent is handled above — NOT a Harmony
            // patch on any Update method, to avoid the PatchAll-abort footgun documented in
            // feedback_harmony_patch_update memory.
            TryHookPlayerActionTrackerEvents();

            // Try dumping JSA table immediately via reflection on any loaded JointabilityAsset
            Instance.StartCoroutine(TryDumpJsaFromResources());
        }

        private void OnApplicationQuit() { State.Flush(force: true); JsaCompatState.Flush(); }
        private void OnDestroy()         { State.Flush(force: true); JsaCompatState.Flush(); }

        // Prefix every [SkipDiag] line with a wall-clock timestamp (to correlate against player-reported
        // "I pressed skip at roughly X") AND the current GameSession.GameState (to remove the exact
        // ambiguity that bit us earlier this session: a Hab3DController field read during
        // GameState.LoadingInProgress can show leftover/stale controller state from a previous save,
        // easily mistaken for a live scene if you only look at line-number proximity in the log).
        static string SkipDiagPrefix()
        {
            string state;
            try { state = GameSession.CurrentGameState.ToString(); }
            catch { state = "?"; }
            return $"[SkipDiag {DateTime.Now:HH:mm:ss.fff} state={state}]";
        }

        static void TryHookPlayerActionTrackerEvents()
        {
            try
            {
                Main.EventSystem.AddHandler((PlayerActionTrackerEvent ev) =>
                {
                    if (!SkipDiagnosticsEnabled.Value) return;
                    var assetName = ev.TrackingAsset != null ? ev.TrackingAsset.name : "null";
                    Log.LogInfo($"{SkipDiagPrefix()} PAT event: {assetName} op={ev.TrackingOperationType} " +
                        $"value={ev.TrackingOperationValue} isReset={ev.IsResetAction}");
                });
                if (SkipDiagnosticsEnabled.Value)
                    Log.LogInfo($"{SkipDiagPrefix()} PlayerActionTrackerEvent hook registered.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[SkipDiag] Could not register PlayerActionTrackerEvent hook: {ex.Message}");
            }
        }

        // Independent, QC-agnostic polling of Hab3DController/HabCustomGreetingController state via the
        // BepInEx plugin's own Update() (a MonoBehaviour method BepInEx calls directly — NOT a Harmony
        // patch on the game's Hab3DController.Update, which is known to silently abort PatchAll(), see
        // feedback_harmony_patch_update memory). Logs only on state CHANGE, not every frame, to keep
        // output readable. Reflection-based since these are private fields on BBI.Unity.Game types.
        static bool s_lastIsInAfterShift;
        static string? s_lastSceneName;
        static bool s_lastGreetingShowing;
        static string? s_lastGreetingNarrativeAsset;

        private void Update()
        {
            PickupInspector.Tick();

            if (SkipDiagnosticsEnabled == null || !SkipDiagnosticsEnabled.Value) return;

            var habController = Resources.FindObjectsOfTypeAll<Hab3DController>().FirstOrDefault();
            if (habController != null)
            {
                var t = HarmonyLib.Traverse.Create(habController);
                bool isInAfterShift = t.Field("mIsInAfterShiftHab").GetValue<bool>();
                var currentData = t.Field("mCurrentAfterShiftData").GetValue<HabAfterShiftAsset>();
                string? sceneName = currentData != null ? currentData.name : null;

                if (isInAfterShift != s_lastIsInAfterShift || sceneName != s_lastSceneName)
                {
                    Log.LogInfo($"{SkipDiagPrefix()} Hab3DController: mIsInAfterShiftHab={isInAfterShift} " +
                        $"mCurrentAfterShiftData={sceneName ?? "null"} type={currentData?.SceneType}");
                    s_lastIsInAfterShift = isInAfterShift;
                    s_lastSceneName = sceneName;
                }
            }

            bool greetingShowing = HabCustomGreetingController.HabGreetingShowing;
            if (greetingShowing != s_lastGreetingShowing)
            {
                Log.LogInfo($"{SkipDiagPrefix()} HabCustomGreetingController.HabGreetingShowing={greetingShowing}");
                s_lastGreetingShowing = greetingShowing;
                if (!greetingShowing) s_lastGreetingNarrativeAsset = null;
            }

            // Independent of skip -- QC only logs mNarrativeAsset at the moment skip is pressed, which
            // misses (a) no-skip runs entirely and (b) mid-queue asset swaps if HabGreetingShowing stays
            // true across several queued messages. Polling every frame here catches every message in the
            // queue, logging only on CHANGE so it doesn't spam.
            if (greetingShowing)
            {
                var greetingController = Resources.FindObjectsOfTypeAll<HabCustomGreetingController>().FirstOrDefault();
                if (greetingController != null)
                {
                    var narrativeAsset = HarmonyLib.Traverse.Create(greetingController).Field("mNarrativeAsset").GetValue<NarrativeMessageAsset>();
                    string? assetName = narrativeAsset != null ? narrativeAsset.name : null;
                    if (assetName != s_lastGreetingNarrativeAsset)
                    {
                        Log.LogInfo($"{SkipDiagPrefix()} HabCustomGreetingController.mNarrativeAsset={assetName ?? "null"}");
                        s_lastGreetingNarrativeAsset = assetName;
                    }
                }
            }
        }

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

        // Dump sp_jsa_map.json: StructurePartAsset name → JSA name for every loaded SP asset
        internal static IEnumerator TryDumpAllSpAssets()
        {
            yield return new WaitForSeconds(1f);
            var all = Resources.FindObjectsOfTypeAll<StructurePartAsset>();
            Plugin.Log.LogInfo($"[SpJsa] Found {all.Length} StructurePartAsset instance(s) at gameplay+1s");
            var map = new Dictionary<string, string>();
            foreach (var spa in all)
            {
                if (spa == null) continue;
                var jsa = spa.Data?.JointSetupAsset;
                if (jsa == null) continue;
                map[spa.name] = jsa.name;
            }
            Plugin.Log.LogInfo($"[SpJsa] {map.Count} SP→JSA mappings found");
            if (map.Count == 0) yield break;
            try
            {
                var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
                var path = Path.Combine(outDir, "sp_jsa_map.json");
                // Merge with existing file so entries accumulate across sessions
                Dictionary<string, string> existing = new();
                if (File.Exists(path))
                {
                    try { existing = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(); }
                    catch { }
                }
                foreach (var kv in map) existing[kv.Key] = kv.Value;
                File.WriteAllText(path, JsonConvert.SerializeObject(existing, Formatting.Indented));
                Plugin.Log.LogInfo($"[SpJsa] Wrote {existing.Count} entries to {path}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[SpJsa] Write error: {ex.Message}"); }
        }

        // Dumps sp_bp_fields.json: for every loaded StructurePartAsset and blueprint asset,
        // reflects over all public fields/properties (including the nested "Data" struct
        // StructurePartAsset uses) and records primitive/string/enum values. This exists so
        // the editor-side tooling can preview cut level / salvage destination / grapplePullable /
        // density / etc. without needing the compiled BBI.Unity.Game types or a live asset file —
        // those assets only exist inside the shipped game bundles, never as loose .asset files
        // in the mod project.
        internal static IEnumerator TryDumpSpBpFields()
        {
            yield return new WaitForSeconds(1f);

            var result = new Dictionary<string, Dictionary<string, object>>();

            var spAssets = Resources.FindObjectsOfTypeAll<StructurePartAsset>();
            foreach (var spa in spAssets)
            {
                if (spa == null || result.ContainsKey(spa.name)) continue;
                var fields = new Dictionary<string, object>();
                DumpReflectedFields(spa, fields, "");
                if (fields.Count > 0) result[spa.name] = fields;
            }
            Plugin.Log.LogInfo($"[SpBpFields] Reflected {result.Count} StructurePartAsset instance(s)");

            var bpType = AccessTools.TypeByName("BBI.Unity.Game.BlueprintAsset");
            if (bpType == null)
            {
                // Type name guess failed — resolve the real type off a live EntityBlueprintComponent
                // instance instead of assuming the name.
                var bpField = AccessTools.Field(typeof(EntityBlueprintComponent), "m_BlueprintAsset");
                if (bpField != null)
                {
                    foreach (var ebc in Resources.FindObjectsOfTypeAll<EntityBlueprintComponent>())
                    {
                        if (ebc == null) continue;
                        var val = bpField.GetValue(ebc);
                        if (val == null) continue;
                        bpType = val.GetType();
                        Plugin.Log.LogInfo($"[SpBpFields] Resolved blueprint asset type via live instance: {bpType.FullName}");
                        break;
                    }
                }
                if (bpType == null)
                    Plugin.Log.LogWarning("[SpBpFields] Could not resolve blueprint asset type by name or live instance — skipping blueprint dump");
            }
            if (bpType != null)
            {
                LogTypeSchema(bpType, "EntityBlueprintAsset");
                var bpAssets = Resources.FindObjectsOfTypeAll(bpType);
                int bpCount = 0;
                foreach (var bpa in bpAssets)
                {
                    if (bpa == null) continue;
                    var uObj = bpa as UnityEngine.Object;
                    var key = uObj != null ? uObj.name : bpa.ToString();
                    if (string.IsNullOrEmpty(key) || result.ContainsKey(key)) continue;
                    var fields = new Dictionary<string, object>();
                    DumpReflectedFields(bpa, fields, "");
                    if (fields.Count > 0) { result[key] = fields; bpCount++; }
                }
                Plugin.Log.LogInfo($"[SpBpFields] Reflected {bpCount} blueprint asset instance(s)");
            }

            if (result.Count == 0) yield break;
            try
            {
                var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
                var path = Path.Combine(outDir, "sp_bp_fields.json");
                Dictionary<string, Dictionary<string, object>> existing = new();
                if (File.Exists(path))
                {
                    try { existing = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(File.ReadAllText(path)) ?? new(); }
                    catch { }
                }
                foreach (var kv in result) existing[kv.Key] = kv.Value;
                File.WriteAllText(path, JsonConvert.SerializeObject(existing, Formatting.Indented));
                Plugin.Log.LogInfo($"[SpBpFields] Wrote {existing.Count} entries to {path}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[SpBpFields] Write error: {ex.Message}"); }
        }

        // Reflects over instance fields and properties of obj (public AND private/[SerializeField] —
        // Unity serializes most gameplay data as private backing fields), writing primitive/string/enum
        // values into dest keyed by prefix+memberName. Recurses into nested non-Unity struct/class
        // members (e.g. StructurePartAsset.Data) AND into referenced ScriptableObject assets (e.g. a
        // "SalvageableAsset" hung off the SP asset) since real gameplay data like cut level / salvage
        // destination / grapplePullable can live one hop away via an asset reference, not inline.
        // `visited` guards against reference cycles / re-walking the same asset twice.
        // Known-noisy key patterns: GUID/asset-identity plumbing, tutorial/analytics/PAT-history
        // bookkeeping, and audio event wiring. These carry no gameplay-relevant information for
        // the "what does this SP/BP actually do" use case (cut level, salvage, grapple, freeze,
        // etc.) and just add volume.
        static readonly string[] s_NoisyKeyContains =
        {
            "AssetGUID", "AssetID", "ID.IsValid", "IsDone", "SubObjectName",
            "hideFlags", "OnlineEventNameTag", "OnlineTypeTag", "IsTutorialEntry",
            "ShouldRecordInPATHistory", "WwiseGuid", "SuppressDestroyedSalvageNotifications",
            "m_Ptr", "AssetBasis", "AssetCloneRef",
        };

        static bool IsNoisyKey(string key)
        {
            foreach (var pattern in s_NoisyKeyContains)
                if (key.IndexOf(pattern, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Unity backs most [SerializeField] data with a private "m_Foo" field and a public "Foo"
        // property that just returns it — walking both doubles every value. Track member names
        // already captured at this prefix level (case/underscore-insensitive) and skip the second.
        static string NormalizeMemberName(string name)
        {
            if (name.Length > 2 && name[0] == 'm' && name[1] == '_') name = name.Substring(2);
            return name;
        }

        static void DumpReflectedFields(object obj, Dictionary<string, object> dest, string prefix, int depth = 0, HashSet<object>? visited = null)
        {
            if (obj == null || depth > 3) return;
            if (visited == null) visited = new HashSet<object>(RefEqualityComparer.Instance);
            if (!visited.Add(obj)) return;

            var type = obj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var seenMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in type.GetFields(flags))
            {
                if (f.IsStatic) continue;
                if (!seenMembers.Add(NormalizeMemberName(f.Name))) continue;
                object val;
                try { val = f.GetValue(obj); } catch { continue; }
                AddOrRecurse(dest, prefix + f.Name, val, f.FieldType, depth, visited);
            }

            foreach (var p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                if (!seenMembers.Add(NormalizeMemberName(p.Name))) continue;
                object val;
                try { val = p.GetValue(obj); } catch { continue; }
                AddOrRecurse(dest, prefix + p.Name, val, p.PropertyType, depth, visited);
            }
        }

        static void AddOrRecurse(Dictionary<string, object> dest, string key, object val, Type declaredType, int depth, HashSet<object> visited)
        {
            if (IsNoisyKey(key)) return;

            if (IsSimpleType(declaredType))
            {
                dest[key] = val is Enum ? val.ToString() : val;
                return;
            }
            if (val == null) return;
            if (val is System.Delegate) return;

            // Referenced ScriptableObject assets (e.g. a SalvageableAsset hung off an SP asset) are
            // where cross-referenced gameplay data tends to live — record the reference and recurse
            // into it. Other UnityEngine.Object types (GameObject/Component/Texture/etc.) are skipped
            // to avoid walking into the scene graph.
            if (val is ScriptableObject so)
            {
                dest[key + "@ref"] = so.name;
                DumpReflectedFields(so, dest, key + ".", depth + 1, visited);
                return;
            }
            if (val is UnityEngine.Object) return;

            // Arrays/lists of ScriptableObject-derived elements (e.g. EntityBlueprintAsset's
            // m_ComponentDataAssets — one small asset per ECS component config) carry real
            // gameplay data per element. Walk each element by index; skip other enumerables
            // (native collections, primitive arrays) to avoid dumping raw buffers.
            if (val is System.Collections.IEnumerable seq && declaredType != typeof(string))
            {
                var elemType = declaredType.IsArray ? declaredType.GetElementType() : null;
                if (elemType == null) return;

                if (typeof(ScriptableObject).IsAssignableFrom(elemType))
                {
                    int i = 0;
                    foreach (var item in seq)
                    {
                        if (item is ScriptableObject itemSo)
                        {
                            var elemKey = $"{key}[{i}]";
                            dest[elemKey + "@type"] = itemSo.GetType().Name;
                            dest[elemKey + "@ref"] = itemSo.name;
                            DumpReflectedFields(itemSo, dest, elemKey + ".", depth + 1, visited);
                        }
                        i++;
                    }
                    return;
                }

                // Arrays of plain [Serializable] structs/classes (e.g. SalvageableComponentAsset's
                // SalvageableCurrencyBlock[] m_AwardedCurrencies) carry real per-element gameplay
                // data too, but have no ScriptableObject identity to key off of — walk them by
                // index the same way, just without the @type/@ref name lookup.
                if (!IsSimpleType(elemType) && elemType.Namespace != null && !elemType.Namespace.StartsWith("System")
                    && !elemType.Namespace.StartsWith("Unity.Collections") && !typeof(UnityEngine.Object).IsAssignableFrom(elemType))
                {
                    int i = 0;
                    foreach (var item in seq)
                    {
                        if (item != null)
                        {
                            var elemKey = $"{key}[{i}]";
                            DumpReflectedFields(item, dest, elemKey + ".", depth + 1, visited);
                        }
                        i++;
                    }
                }
                return;
            }
            if (declaredType.Namespace != null && declaredType.Namespace.StartsWith("System")) return;
            DumpReflectedFields(val, dest, key + ".", depth + 1, visited);
        }

        static bool IsSimpleType(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);
        }

        // One-time diagnostic: logs every field/property name + declared type across the full
        // inheritance chain of `type`, regardless of whether DumpReflectedFields can extract a
        // value from it. Use this when a type reflects to almost nothing useful — it reveals
        // what's actually there (e.g. DOTS component collections, blob references) so the
        // extraction logic can be pointed at the right member.
        static readonly HashSet<Type> s_SchemaLogged = new HashSet<Type>();
        static void LogTypeSchema(Type type, string label)
        {
            if (!s_SchemaLogged.Add(type)) return;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            Plugin.Log.LogInfo($"[SpBpFields] --- Schema for {label} ({type.FullName}) ---");
            for (var t = type; t != null && t != typeof(UnityEngine.Object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(flags))
                    Plugin.Log.LogInfo($"[SpBpFields]   field  {t.Name}.{f.Name} : {f.FieldType.FullName}");
                foreach (var p in t.GetProperties(flags))
                    Plugin.Log.LogInfo($"[SpBpFields]   prop   {t.Name}.{p.Name} : {p.PropertyType.FullName}");
            }
            Plugin.Log.LogInfo($"[SpBpFields] --- end schema ---");
        }

        // net472 has no built-in ReferenceEqualityComparer — used to guard DumpReflectedFields
        // against reference cycles when recursing into ScriptableObject asset references.
        class RefEqualityComparer : IEqualityComparer<object>
        {
            public static readonly RefEqualityComparer Instance = new RefEqualityComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        static IEnumerator DumpMaterialProperties()
        {
            yield return new WaitForSeconds(3f);
            var targets = new[] { "HeatExchanger", "CornerTriangle_3Cx2xA" };
            foreach (var keyword in targets)
            {
                var gos = Resources.FindObjectsOfTypeAll<MeshRenderer>()
                    .Where(r => r.gameObject.name.Contains(keyword))
                    .Take(1)
                    .ToArray();
                foreach (var mr in gos)
                {
                    for (int si = 0; si < mr.sharedMaterials.Length; si++)
                    {
                        var mat = mr.sharedMaterials[si];
                        if (mat == null) continue;
                        Plugin.Log.LogInfo($"[MatDump] GO={mr.gameObject.name} slot={si} mat={mat.name} shader={mat.shader?.name}");
                        Plugin.Log.LogInfo($"[MatDump]   keywords={string.Join(" ", mat.shaderKeywords)}");
                        var shader = mat.shader;
                        if (shader == null) continue;
                        int count = shader.GetPropertyCount();
                        for (int pi = 0; pi < count; pi++)
                        {
                            var pname = shader.GetPropertyName(pi);
                            var ptype = shader.GetPropertyType(pi);
                            string val;
                            try
                            {
                                switch (ptype)
                                {
                                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                                        val = mat.GetFloat(pname).ToString("G4");
                                        break;
                                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                                        var c = mat.GetColor(pname);
                                        val = $"r={c.r:G3} g={c.g:G3} b={c.b:G3} a={c.a:G3}";
                                        break;
                                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                                        var v = mat.GetVector(pname);
                                        val = $"({v.x:G3},{v.y:G3},{v.z:G3},{v.w:G3})";
                                        break;
                                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                        val = mat.GetTexture(pname)?.name ?? "null";
                                        break;
                                    default: val = ptype.ToString(); break;
                                }
                            }
                            catch { val = "?"; }
                            Plugin.Log.LogInfo($"[MatDump]   {pname} ({ptype}): {val}");
                        }
                    }
                }
            }
        }

        static IEnumerator FlushJsaDelayed()
        {
            yield return new WaitForSeconds(5f);
            Plugin.Log.LogInfo($"[JsaCompat] Delayed flush triggered, pairs so far: {JsaCompatState.PairCount}");
            JsaCompatState.Flush();
        }

        // Fills connectedNodes with every entity reachable in the runtime ECS structure graph
        // from the given entity, mirroring BreakableJointComponent.TryGetConnections().
        static void GetConnectedEntities(EntityManager entityManager, Entity entity, NativeList<Entity> connectedNodes)
        {
            var graphSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<StructureGraphSystem>();
            if (entityManager.TryGetComponent<StructureGroupMember>(entity, out var groupMember) &&
                entityManager.TryGetBuffer<PartConnection>(groupMember.GroupEntity, out var partConnections))
            {
                foreach (var conn in partConnections)
                    graphSystem.GetAllConnectedNodesFor(conn.Value, connectedNodes, clearBuffer: false);
            }
            else
            {
                graphSystem.GetAllConnectedNodesFor(entity, connectedNodes);
            }
        }

        // Dumps a CSV of every StructurePart's name and its count of jointed neighbor parts,
        // excluding neighbors that are InvisibleJoint bridge markers rather than real parts.
        internal static IEnumerator DumpJointCensus()
        {
            if (!JointCensusEnabled.Value) yield break;

            yield return new WaitForSeconds(JointCensusDelay.Value);

            var ijMarkerType = AccessTools.TypeByName("InvisibleJointMarker");
            if (ijMarkerType == null)
                Plugin.Log.LogWarning("[JointCensus] InvisibleJointMarker type not found — IJ markers will not be excluded from neighbor counts.");

            var allParts = Resources.FindObjectsOfTypeAll<StructurePart>()
                .Where(p => p != null && p.gameObject.scene.IsValid())
                .ToArray();
            Plugin.Log.LogInfo($"[JointCensus] Scanning {allParts.Length} StructurePart(s)...");

            // Pre-compute the set of entities belonging to InvisibleJoint markers so we can
            // exclude them from neighbor counts without re-checking components per-pair.
            var ijEntities = new HashSet<Entity>();
            if (ijMarkerType != null)
            {
                foreach (var p in allParts)
                {
                    if (p.GetComponent(ijMarkerType) != null && EntityBlueprintComponent.IsValid(p.EntityBlueprintComponent))
                        ijEntities.Add(p.Entity);
                }
                Plugin.Log.LogInfo($"[JointCensus] Found {ijEntities.Count} InvisibleJoint marker part(s), excluded from neighbor counts.");
            }

            var rows = new List<(string Name, string JsaName, int NeighborCount, Entity Entity)>();
            var unionFind = new Dictionary<Entity, Entity>();
            using var connectedNodes = new NativeList<Entity>(Allocator.Temp);

            foreach (var part in allParts)
            {
                if (part == null) continue;
                var name = part.gameObject.name.Replace("(Clone)", "").Trim();
                // Real JSA per StructurePart.StructurePartAsset.Data.JointSetupAsset — the same
                // live object reference JointabilityAsset.CanJoint reads at jointing time, not a
                // name/GUID-based guess. See project_jsa_compat_isactive_bug memory for why this
                // ground truth matters (jsa_compat.json's pairing table was found to be wrong for
                // inactive pairings; this per-part JSA identity itself was never in question).
                var jsaName = part.StructurePartAsset?.Data?.JointSetupAsset?.name ?? "";
                if (ijEntities.Count > 0 && ijMarkerType != null && part.GetComponent(ijMarkerType) != null)
                    continue; // don't list IJ markers themselves as parts

                if (!EntityBlueprintComponent.IsValid(part.EntityBlueprintComponent))
                {
                    rows.Add((name, jsaName, 0, Entity.Null));
                    continue;
                }

                var entity = part.Entity;
                UnionFind_MakeSet(unionFind, entity);
                var entityManager = part.EntityBlueprintComponent.EntityManager;
                connectedNodes.Clear();
                GetConnectedEntities(entityManager, entity, connectedNodes);

                int neighborCount = 0;
                foreach (var e in connectedNodes)
                {
                    if (e == entity) continue;
                    if (ijEntities.Contains(e)) continue;
                    if (!entityManager.HasComponent<StructurePart>(e)) continue;
                    neighborCount++;
                    UnionFind_MakeSet(unionFind, e);
                    UnionFind_Union(unionFind, entity, e);
                }

                rows.Add((name, jsaName, neighborCount, entity));
            }

            // Assign cluster IDs, ordered by ascending cluster size (smallest/most-isolated first).
            var clusterMembers = new Dictionary<Entity, List<int>>(); // root -> row indices
            for (int i = 0; i < rows.Count; i++)
            {
                var e = rows[i].Entity;
                if (e == Entity.Null) continue;
                var root = UnionFind_Find(unionFind, e);
                if (!clusterMembers.TryGetValue(root, out var list))
                    clusterMembers[root] = list = new List<int>();
                list.Add(i);
            }
            var orderedClusters = clusterMembers.Values.OrderBy(l => l.Count).ToList();
            var rowToCluster = new int[rows.Count];
            var clusterSize = new int[orderedClusters.Count];
            for (int c = 0; c < orderedClusters.Count; c++)
            {
                clusterSize[c] = orderedClusters[c].Count;
                foreach (var i in orderedClusters[c]) rowToCluster[i] = c;
            }

            try
            {
                var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
                var path = Path.Combine(outDir, "joint_census.csv");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("PartName,JsaName,JointedNeighborCount,ClusterId,ClusterSize");
                var indices = Enumerable.Range(0, rows.Count).OrderBy(i => rows[i].Name, StringComparer.Ordinal);
                foreach (var i in indices)
                {
                    var row = rows[i];
                    var hasCluster = row.Entity != Entity.Null;
                    var clusterId = hasCluster ? rowToCluster[i].ToString() : "";
                    var size = hasCluster ? clusterSize[rowToCluster[i]].ToString() : "";
                    sb.AppendLine($"{CsvEscape(row.Name)},{CsvEscape(row.JsaName)},{row.NeighborCount},{clusterId},{size}");
                }
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[JointCensus] Wrote {rows.Count} part(s) in {orderedClusters.Count} cluster(s) to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[JointCensus] Write error: {ex.Message}");
            }
        }

        static Entity UnionFind_Find(Dictionary<Entity, Entity> parent, Entity e)
        {
            if (!parent.TryGetValue(e, out var p)) { parent[e] = e; return e; }
            if (p == e) return e;
            var root = UnionFind_Find(parent, p);
            parent[e] = root;
            return root;
        }

        static void UnionFind_MakeSet(Dictionary<Entity, Entity> parent, Entity e)
        {
            if (!parent.ContainsKey(e)) parent[e] = e;
        }

        static void UnionFind_Union(Dictionary<Entity, Entity> parent, Entity a, Entity b)
        {
            var ra = UnionFind_Find(parent, a);
            var rb = UnionFind_Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }

        static string CsvEscape(string s)
        {
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // Samples world position of every StructurePart matching namePrefixes at two points in
        // time (as early as possible after spawn, then again after delaySeconds) and logs the
        // displacement. Built to test whether parts that fail to auto-joint are being pushed
        // apart by a physics separation impulse from excess mesh-collider overlap at spawn —
        // a part that started out overlapping its intended joint partner but ends up drifted
        // away confirms an impulse, distinct from a part that simply never had enough overlap
        // to begin with (which would show near-zero displacement from its spawn position).
        internal static IEnumerator DumpPositionDrift(string[] namePrefixes, float delaySeconds = 10f)
        {
            // Sample as early as possible — one frame after Gameplay state fires — so the first
            // reading is as close to pre-physics-resolution spawn position as this hook allows.
            yield return null;

            var parts = Resources.FindObjectsOfTypeAll<StructurePart>()
                .Where(p => p != null && p.gameObject.scene.IsValid())
                .Where(p => namePrefixes.Any(prefix => p.gameObject.name.Replace("(Clone)", "").Trim()
                    .StartsWith(prefix, StringComparison.Ordinal)))
                .ToArray();
            Plugin.Log.LogInfo($"[PosDrift] Sampling {parts.Length} matching StructurePart(s) at spawn...");

            var startPositions = parts.ToDictionary(p => p, p => p.transform.position);

            yield return new WaitForSeconds(delaySeconds);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("PartName,SpawnPos,LaterPos,DisplacementMeters");
            foreach (var part in parts.OrderBy(p => p.gameObject.name, StringComparer.Ordinal))
            {
                if (part == null) continue; // could have been destroyed/deposited in the meantime
                var name = part.gameObject.name.Replace("(Clone)", "").Trim();
                var startPos = startPositions[part];
                var laterPos = part.transform.position;
                var displacement = Vector3.Distance(startPos, laterPos);
                sb.AppendLine($"{CsvEscape(name)},{CsvEscape(startPos.ToString("F4"))}," +
                    $"{CsvEscape(laterPos.ToString("F4"))},{displacement:F4}");
            }

            try
            {
                var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
                var path = Path.Combine(outDir, "position_drift.csv");
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[PosDrift] Wrote {parts.Length} part(s) to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[PosDrift] Write error: {ex.Message}");
            }
        }

        // Dumps mesh-geometry stats (vertex count, render bounds vs. collider bounds, collider
        // convexity/triangle count) for every StructurePart whose name matches namePrefixes.
        // Built to isolate a mesh-only difference between two sets of otherwise componentwise-
        // identical part instances (same prefab, same transforms, same MJC/ACL wiring) where one
        // set fails to auto-joint in-game and the other doesn't — every non-mesh property has
        // already been ruled out by static prefab comparison, so this checks the one thing that
        // can't be read from YAML: the actual baked vertex/collider data per submesh.
        internal static IEnumerator DumpMeshDiagnostics(string[] namePrefixes, float delaySeconds = 10f)
        {
            yield return new WaitForSeconds(delaySeconds);

            var allParts = Resources.FindObjectsOfTypeAll<StructurePart>()
                .Where(p => p != null && p.gameObject.scene.IsValid())
                .Where(p => namePrefixes.Any(prefix => p.gameObject.name.Replace("(Clone)", "").Trim()
                    .StartsWith(prefix, StringComparison.Ordinal)))
                .ToArray();
            Plugin.Log.LogInfo($"[MeshDiag] Scanning {allParts.Length} matching StructurePart(s)...");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("PartName,MeshVertCount,MeshTriCount,RenderBoundsSize,ColliderType,ColliderConvex,ColliderIsTrigger,ColliderBoundsSize,ColliderSharedMesh");
            foreach (var part in allParts.OrderBy(p => p.gameObject.name, StringComparer.Ordinal))
            {
                var name = part.gameObject.name.Replace("(Clone)", "").Trim();
                var mf = part.GetComponent<MeshFilter>();
                var mr = part.GetComponent<MeshRenderer>();
                var col = part.GetComponent<Collider>();

                int vertCount = mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount : -1;
                int triCount = mf != null && mf.sharedMesh != null ? mf.sharedMesh.triangles.Length / 3 : -1;
                string renderBounds = mr != null ? mr.bounds.size.ToString("F4") : "none";

                string colliderType = col != null ? col.GetType().Name : "none";
                string colliderConvex = col is MeshCollider meshCol ? meshCol.convex.ToString() : "n/a";
                string colliderIsTrigger = col != null ? col.isTrigger.ToString() : "n/a";
                string colliderBounds = col != null ? col.bounds.size.ToString("F4") : "none";
                string colliderMesh = col is MeshCollider mc && mc.sharedMesh != null
                    ? $"{mc.sharedMesh.name} (verts={mc.sharedMesh.vertexCount})" : "n/a";

                sb.AppendLine($"{CsvEscape(name)},{vertCount},{triCount},{CsvEscape(renderBounds)}," +
                    $"{colliderType},{colliderConvex},{colliderIsTrigger},{CsvEscape(colliderBounds)},{CsvEscape(colliderMesh)}");
            }

            try
            {
                var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
                var path = Path.Combine(outDir, "mesh_diagnostics.csv");
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[MeshDiag] Wrote {allParts.Length} part(s) to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[MeshDiag] Write error: {ex.Message}");
            }
        }

        // Dumps campaign_progress.json: every PlayerActionTrackerAsset recorded in the current save's
        // PlayerProfile.PlayerActionTrackerHistory (name -> count), plus every IndustrialActionShipAsset
        // known to JobBoardSettings with its PATConditionAsset requirements evaluated against that
        // history (met/unmet per PATCounter, and overall AreConditionsMet-equivalent), and whether the
        // ship has already been generated into PlayerProfile.IndustrialActionShips. All via reflection
        // since PartInfoLogger doesn't compile against these BBI.Unity.Game types directly.
        internal static IEnumerator DumpCampaignProgress()
        {
            Plugin.Log.LogInfo("[CampaignProgress] Coroutine started, waiting 1s...");
            // Use realtime rather than WaitForSeconds: the Hab can sit at Time.timeScale = 0 (paused/menu)
            // right after a state transition, which would make a scaled-time wait never elapse.
            yield return new WaitForSecondsRealtime(1f);
            Plugin.Log.LogInfo("[CampaignProgress] Wait complete, running dump...");

            try
            {
                RunCampaignProgressDump();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CampaignProgress] Unhandled exception: {ex}");
            }
        }

        static void RunCampaignProgressDump()
        {
            var profileServiceType = AccessTools.TypeByName("BBI.Unity.Game.PlayerProfileService");
            if (profileServiceType == null) { Plugin.Log.LogWarning("[CampaignProgress] PlayerProfileService type not found — skipping."); return; }

            var instanceProp = AccessTools.Property(profileServiceType, "Instance");
            var profileService = instanceProp?.GetValue(null);
            var profileProp = profileService != null ? AccessTools.Property(profileServiceType, "Profile") : null;
            var profile = profileProp?.GetValue(profileService);
            if (profile == null) { Plugin.Log.LogWarning("[CampaignProgress] PlayerProfileService.Instance.Profile not available — skipping."); return; }

            var profileName = AccessTools.Property(profile.GetType(), "ProfileName")?.GetValue(profile) as string;
            if (string.IsNullOrEmpty(profileName)) profileName = "UnknownProfile";
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
                profileName = profileName!.Replace(invalidChar, '_');

            var patHistory = AccessTools.Field(profile.GetType(), "PlayerActionTrackerHistory")?.GetValue(profile) as IDictionary;
            if (patHistory == null) { Plugin.Log.LogWarning("[CampaignProgress] PlayerActionTrackerHistory field not found — skipping."); return; }

            var patHistoryByName = new Dictionary<string, int>();
            foreach (DictionaryEntry entry in patHistory)
            {
                var patName = (entry.Key as UnityEngine.Object)?.name ?? entry.Key?.ToString() ?? "?";
                patHistoryByName[patName] = (int)entry.Value!;
            }
            Plugin.Log.LogInfo($"[CampaignProgress] PlayerActionTrackerHistory has {patHistoryByName.Count} entries.");

            var industrialShipsProfileField = AccessTools.Field(profile.GetType(), "IndustrialActionShips");
            var industrialShipsGenerated = industrialShipsProfileField?.GetValue(profile) as IDictionary;
            var generatedShipIds = new HashSet<string>();
            if (industrialShipsGenerated != null)
            {
                foreach (var key in industrialShipsGenerated.Keys)
                    generatedShipIds.Add(key?.ToString() ?? "?");
            }

            var iaShipList = new List<Dictionary<string, object>>();
            var mainType = AccessTools.TypeByName("BBI.Unity.Game.Main");
            var mainInstance = AccessTools.Property(mainType, "Instance")?.GetValue(null);
            var mainSettings = mainInstance != null ? AccessTools.Property(mainType, "MainSettings")?.GetValue(mainInstance) : null;
            var habSettings = mainSettings != null ? AccessTools.Property(mainSettings.GetType(), "HabSettings")?.GetValue(mainSettings) : null;
            var jobBoardSettings = habSettings != null ? AccessTools.Property(habSettings.GetType(), "JobBoardSettings")?.GetValue(habSettings) : null;
            var iaShips = jobBoardSettings != null ? AccessTools.Property(jobBoardSettings.GetType(), "IndustrialActionShips")?.GetValue(jobBoardSettings) as IEnumerable : null;

            if (iaShips == null)
            {
                Plugin.Log.LogWarning("[CampaignProgress] Could not resolve JobBoardSettings.IndustrialActionShips — falling back to a Resources scan.");
                var iaType = AccessTools.TypeByName("BBI.Unity.Game.IndustrialActionShipAsset");
                iaShips = iaType != null ? Resources.FindObjectsOfTypeAll(iaType) : null;
            }

            if (iaShips != null)
            {
                foreach (var shipAsset in iaShips)
                {
                    if (shipAsset == null) continue;
                    var shipName = (shipAsset as UnityEngine.Object)?.name ?? shipAsset.ToString();
                    var shipEntry = new Dictionary<string, object> { ["name"] = shipName };

                    var idVal = AccessTools.Property(shipAsset.GetType(), "ID")?.GetValue(shipAsset);
                    if (idVal != null) shipEntry["alreadyGenerated"] = generatedShipIds.Contains(idVal.ToString() ?? "?");

                    var dataField = AccessTools.Field(shipAsset.GetType(), "Data");
                    var data = dataField?.GetValue(shipAsset);
                    var conditionAsset = data != null ? AccessTools.Field(data.GetType(), "ConditionAsset")?.GetValue(data) : null;

                    if (conditionAsset == null)
                    {
                        shipEntry["conditionAsset"] = null!;
                        iaShipList.Add(shipEntry);
                        continue;
                    }

                    shipEntry["conditionAsset"] = (conditionAsset as UnityEngine.Object)?.name ?? "?";
                    bool allMet = true;
                    var conditions = new List<Dictionary<string, object>>();
                    foreach (var listFieldName in new[] { "m_All", "m_Any", "m_None" })
                    {
                        var listField = AccessTools.Field(conditionAsset.GetType(), listFieldName);
                        var list = listField?.GetValue(conditionAsset) as IEnumerable;
                        if (list == null) continue;
                        foreach (var counter in list)
                        {
                            var patObj = AccessTools.Property(counter.GetType(), "PAT")?.GetValue(counter);
                            var patName = (patObj as UnityEngine.Object)?.name ?? "?";
                            var specificCount = (int)(AccessTools.Property(counter.GetType(), "SpecificCount")?.GetValue(counter) ?? 0);
                            var countIrrelevant = (bool)(AccessTools.Property(counter.GetType(), "CountIrrelevant")?.GetValue(counter) ?? false);
                            var orGreater = (bool)(AccessTools.Property(counter.GetType(), "SpecificCountOrGreater")?.GetValue(counter) ?? false);

                            patHistoryByName.TryGetValue(patName, out var actualCount);
                            bool has = patHistoryByName.ContainsKey(patName);
                            bool met = has && (countIrrelevant || actualCount == specificCount || (orGreater && actualCount > specificCount));
                            if (listFieldName == "m_All" && !met) allMet = false;

                            conditions.Add(new Dictionary<string, object>
                            {
                                ["list"] = listFieldName,
                                ["pat"] = patName,
                                ["requiredCount"] = specificCount,
                                ["countIrrelevant"] = countIrrelevant,
                                ["orGreater"] = orGreater,
                                ["actualCount"] = actualCount,
                                ["met"] = met,
                            });
                        }
                    }
                    shipEntry["conditions"] = conditions;
                    shipEntry["allListSatisfied"] = allMet;
                    iaShipList.Add(shipEntry);
                }
            }

            // Scan every loaded NarrativeMessageAsset (the terminal/inbox + Hab-greeting-popup email
            // system) and report which PAT(s) each one fires when read, so we can identify — by subject
            // line and sender, not just PAT name — exactly which in-game message corresponds to a given
            // PAT (e.g. PAT_CMP_A3_SC04_MessageFromLou_Read), and whether that PAT is present in this
            // save's history (i.e. actually marked read).
            //
            // IMPORTANT: NarrativeMessageAsset instances loaded via Resources.FindObjectsOfTypeAll are
            // GLOBAL — an asset shows up there once anything in the game references it, regardless of
            // whether THIS profile's inbox has ever received it. To know whether a specific save has
            // actually been delivered a given message (as opposed to merely having the asset resident in
            // memory), we cross-reference PlayerProfile.NarrativeInventory.UnreadMessages/ReadMessages —
            // the two dictionaries NarrativeInventory.HasMessage() actually checks — keyed by the
            // message's AssetTypeID. A message present in the global scan but absent from BOTH of these
            // dictionaries has never been delivered to this player's inbox at all.
            var narrativeInventory = AccessTools.Property(profile.GetType(), "NarrativeInventory")?.GetValue(profile);
            var unreadMessages = narrativeInventory != null ? AccessTools.Property(narrativeInventory.GetType(), "UnreadMessages")?.GetValue(narrativeInventory) as IDictionary : null;
            var readMessagesByCategory = narrativeInventory != null ? AccessTools.Property(narrativeInventory.GetType(), "ReadMessages")?.GetValue(narrativeInventory) as IDictionary : null;

            var unreadIds = new HashSet<string>();
            if (unreadMessages != null)
                foreach (var key in unreadMessages.Keys)
                    unreadIds.Add(key?.ToString() ?? "?");

            var readIds = new HashSet<string>();
            if (readMessagesByCategory != null)
            {
                foreach (var categoryDict in readMessagesByCategory.Values)
                {
                    if (categoryDict is IDictionary catDict)
                        foreach (var key in catDict.Keys)
                            readIds.Add(key?.ToString() ?? "?");
                }
            }
            Plugin.Log.LogInfo($"[CampaignProgress] NarrativeInventory: {unreadIds.Count} unread, {readIds.Count} read message ID(s).");

            var narrativeMessages = new List<Dictionary<string, object>>();
            var nmaType = AccessTools.TypeByName("BBI.Unity.Game.NarrativeMessageAsset");
            if (nmaType != null)
            {
                foreach (var nma in Resources.FindObjectsOfTypeAll(nmaType))
                {
                    if (nma == null) continue;
                    var subjectLocID = AccessTools.Property(nmaType, "SubjectLineLocID")?.GetValue(nma) as string ?? "";
                    var senderLocID = AccessTools.Property(nmaType, "SenderLocID")?.GetValue(nma) as string ?? "";
                    var loc = Main.Instance?.LocalizationService;
                    string? subjectText = null, senderText = null;
                    if (loc != null)
                    {
                        if (!string.IsNullOrEmpty(subjectLocID) && loc.TryLocalize(subjectLocID, out var s) && !string.IsNullOrEmpty(s)) subjectText = s;
                        if (!string.IsNullOrEmpty(senderLocID) && loc.TryLocalize(senderLocID, out var r) && !string.IsNullOrEmpty(r)) senderText = r;
                    }

                    var idVal = AccessTools.Property(nmaType, "ID")?.GetValue(nma);
                    var idStr = idVal?.ToString() ?? "?";
                    string inboxState = unreadIds.Contains(idStr) ? "unread"
                        : readIds.Contains(idStr) ? "read"
                        : "notDeliveredToInbox";

                    var entry = new Dictionary<string, object>
                    {
                        ["name"] = (nma as UnityEngine.Object)?.name ?? "?",
                        ["inboxState"] = inboxState,
                        ["subjectLineLocID"] = subjectLocID,
                        ["subjectText"] = subjectText ?? "",
                        ["senderLocID"] = senderLocID,
                        ["senderText"] = senderText ?? "",
                        ["isHabGreeting"] = AccessTools.Property(nmaType, "IsHabGreeting")?.GetValue(nma) ?? false,
                    };

                    var greetingPat = AccessTools.Property(nmaType, "HABGreetingPopupReadPAT")?.GetValue(nma);
                    var greetingPatName = (greetingPat as UnityEngine.Object)?.name;
                    if (!string.IsNullOrEmpty(greetingPatName))
                    {
                        entry["habGreetingPopupReadPAT"] = greetingPatName!;
                        entry["habGreetingPopupReadPAT_actualCount"] = patHistoryByName.TryGetValue(greetingPatName!, out var gc) ? gc : 0;
                    }

                    var inboxPat = AccessTools.Property(nmaType, "InboxMessageReadPAT")?.GetValue(nma);
                    var inboxPatName = (inboxPat as UnityEngine.Object)?.name;
                    if (!string.IsNullOrEmpty(inboxPatName))
                    {
                        entry["inboxMessageReadPAT"] = inboxPatName!;
                        entry["inboxMessageReadPAT_actualCount"] = patHistoryByName.TryGetValue(inboxPatName!, out var ic) ? ic : 0;
                    }

                    if (entry.ContainsKey("habGreetingPopupReadPAT") || entry.ContainsKey("inboxMessageReadPAT"))
                        narrativeMessages.Add(entry);
                }
                Plugin.Log.LogInfo($"[CampaignProgress] Found {narrativeMessages.Count} NarrativeMessageAsset(s) with a read-PAT wired.");
            }
            else
            {
                Plugin.Log.LogWarning("[CampaignProgress] NarrativeMessageAsset type not found — skipping message scan.");
            }

            // Dump the live asset-save-key hash table: AssetSaveKeyMapService.Instance holds the exact
            // FNV-1a32(SaveKey) -> ScriptableObject mapping the game uses to serialize PlayerActionTracker
            // (and other) asset references into .lpw save files as (uint hash, int value) pairs — see
            // PlayerActionTrackerDataReaderWriterV1.Write/Read and AssetSaveKeyMapService.
            // TryGetHashedAssetKeyFromAsset (hash = FNV1a32(SaveKey, 2166136261)). Reflecting the live
            // dictionary sidesteps needing to know whether SaveKey always equals the asset name, and lets
            // an offline script reverse-hash any .lpw file without the game running, by name AND type —
            // useful for inspecting archived/backup saves (e.g. a .zip pulled from the Saves/Profiles
            // folder) that were never loaded through PIL directly.
            var assetSaveKeys = new Dictionary<string, object>();
            var mapServiceType = AccessTools.TypeByName("BBI.Unity.Game.AssetSaveKeyMapService");
            var mapServiceInstance = AccessTools.Property(mapServiceType, "Instance")?.GetValue(null);
            if (mapServiceInstance != null)
            {
                var hashMap = AccessTools.Field(mapServiceType, "mAssetToHashedAssetKeyMap")?.GetValue(mapServiceInstance) as IDictionary;
                if (hashMap != null)
                {
                    foreach (DictionaryEntry entry in hashMap)
                    {
                        var asset = entry.Key as UnityEngine.Object;
                        if (asset == null) continue;
                        var hash = (uint)entry.Value!;
                        assetSaveKeys[asset.name] = new Dictionary<string, object>
                        {
                            ["hash"] = hash,
                            ["type"] = entry.Key!.GetType().Name,
                        };
                    }
                }
                Plugin.Log.LogInfo($"[CampaignProgress] AssetSaveKeyMapService: {assetSaveKeys.Count} asset->hash entries.");
            }
            else
            {
                Plugin.Log.LogWarning("[CampaignProgress] AssetSaveKeyMapService.Instance not available — skipping asset-save-key dump.");
            }

            var result = new Dictionary<string, object>
            {
                ["profileName"] = profileName,
                ["capturedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["playerActionTrackerHistory"] = patHistoryByName,
                ["industrialActionShips"] = iaShipList,
                ["narrativeMessages"] = narrativeMessages,
            };

            try
            {
                var outDir = Path.Combine(Path.GetDirectoryName(Plugin.OutputPath.Value)!, "campaign_progress");
                Directory.CreateDirectory(outDir);

                // The asset-save-key table is game-global (identical across every profile/session), so
                // it's written once to a shared file rather than duplicated inside every per-profile JSON.
                if (assetSaveKeys.Count > 0)
                {
                    var keysPath = Path.Combine(outDir, "asset_save_keys.json");
                    File.WriteAllText(keysPath, JsonConvert.SerializeObject(assetSaveKeys, Formatting.Indented));
                    Plugin.Log.LogInfo($"[CampaignProgress] Wrote {assetSaveKeys.Count} asset-save-key entries to {keysPath}");
                }
                var path = Path.Combine(outDir, $"{profileName}.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(result, Formatting.Indented));
                Plugin.Log.LogInfo($"[CampaignProgress] Wrote {iaShipList.Count} Industrial Action ship condition set(s) and {patHistoryByName.Count} PAT entries to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CampaignProgress] Write error: {ex.Message}");
            }
        }

        // Dumps certification_levels.json: RequiredXP (cumulative XP threshold to reach that rank) and
        // CertificationName for every entry in CertificationSettings.CertificationLevelAssets, indexed by
        // rank (CertificationLevelAssets[i] corresponds to CurrentCertificationRank == i + 1, per
        // TrySetCertification/RequiresLevelUp in the decompiled source). This is fixed game-config data,
        // identical across every profile/session, so it's written once to a shared file rather than
        // duplicated per-profile like campaign_progress.json.
        internal static IEnumerator DumpCertificationLevels()
        {
            yield return new WaitForSecondsRealtime(1f);

            try
            {
                RunCertificationLevelsDump();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CertificationLevels] Unhandled exception: {ex}");
            }
        }

        static void RunCertificationLevelsDump()
        {
            var levelAssets = Main.Instance?.MainSettings?.CertificationSettings?.CertificationLevelAssets;
            if (levelAssets == null) { Plugin.Log.LogWarning("[CertificationLevels] CertificationLevelAssets not available — skipping."); return; }

            var levels = new List<Dictionary<string, object>>();
            for (int i = 0; i < levelAssets.Length; i++)
            {
                var data = levelAssets[i]?.Data;
                if (data == null) continue;
                levels.Add(new Dictionary<string, object>
                {
                    ["rank"] = i + 1,
                    ["certificationName"] = data.CertificationName ?? "",
                    ["requiredXP"] = data.RequiredXP,
                });
            }
            Plugin.Log.LogInfo($"[CertificationLevels] {levels.Count} certification level entries found.");

            try
            {
                var outDir = Path.Combine(Path.GetDirectoryName(Plugin.OutputPath.Value)!, "campaign_progress");
                Directory.CreateDirectory(outDir);
                var path = Path.Combine(outDir, "certification_levels.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(levels, Formatting.Indented));
                Plugin.Log.LogInfo($"[CertificationLevels] Wrote {levels.Count} entries to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CertificationLevels] Write error: {ex.Message}");
            }
        }

        // Dumps trigger_conditions.json: for every currently-loaded TriggerableBase-derived component
        // (scans Resources.FindObjectsOfTypeAll, so only assets Unity has actually instantiated/loaded
        // this session will appear — a scene/cutscene must have been touched for its triggers to show up),
        // reports m_RequiredLevel / m_RequiredLevelComparison / m_RequirePlayerLevel / m_OnlyTriggersOnce /
        // HasBeenTriggered, plus (for PATConditionalTriggerComponent specifically) each conditional
        // trigger's PATConditionAsset name and its m_All/m_Any/m_None PAT-count requirements. Built to
        // answer whether a milestone PAT's numeric name prefix (e.g. "17" in
        // PAT_CMP_17_X1_LouRecruitsCrew_Complete) actually matches its trigger's real rank requirement --
        // that's an asset-data fact not visible anywhere in decompiled IL, only readable live via
        // reflection against loaded instances. All via reflection since PartInfoLogger doesn't compile
        // against these BBI.Unity.Game types directly.
        internal static IEnumerator DumpTriggerConditions()
        {
            yield return new WaitForSecondsRealtime(1f);

            try
            {
                RunTriggerConditionsDump();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[TriggerConditions] Unhandled exception: {ex}");
            }
        }

        static void RunTriggerConditionsDump()
        {
            var triggerableBaseType = AccessTools.TypeByName("BBI.Unity.Game.TriggerableBase");
            if (triggerableBaseType == null) { Plugin.Log.LogWarning("[TriggerConditions] TriggerableBase type not found — skipping."); return; }

            var patConditionalType = AccessTools.TypeByName("BBI.Unity.Game.PATConditionalTriggerComponent");
            var patConditionAssetType = AccessTools.TypeByName("BBI.Unity.Game.PATConditionAsset");

            var requiredLevelField = AccessTools.Field(triggerableBaseType, "m_RequiredLevel");
            var requiredLevelComparisonField = AccessTools.Field(triggerableBaseType, "m_RequiredLevelComparison");
            var requirePlayerLevelField = AccessTools.Field(triggerableBaseType, "m_RequirePlayerLevel");
            var onlyTriggersOnceField = AccessTools.Field(triggerableBaseType, "m_OnlyTriggersOnce");
            var hasBeenTriggeredProp = AccessTools.Property(triggerableBaseType, "HasBeenTriggered");

            var conditionalTriggersField = patConditionalType != null ? AccessTools.Field(patConditionalType, "m_ConditionalTriggers") : null;
            var conditionAssetField = AccessTools.Field(AccessTools.Inner(patConditionalType, "PATConditionEventPair"), "m_ConditionAsset");

            var allField = patConditionAssetType != null ? AccessTools.Field(patConditionAssetType, "m_All") : null;
            var anyField = patConditionAssetType != null ? AccessTools.Field(patConditionAssetType, "m_Any") : null;
            var noneField = patConditionAssetType != null ? AccessTools.Field(patConditionAssetType, "m_None") : null;
            var patCounterType = patConditionAssetType != null ? AccessTools.Inner(patConditionAssetType, "PATCounter") : null;
            var counterPatField = patCounterType != null ? AccessTools.Field(patCounterType, "m_PAT") : null;
            var counterSpecificCountField = patCounterType != null ? AccessTools.Field(patCounterType, "m_SpecificCount") : null;
            var counterSpecificCountOrGreaterField = patCounterType != null ? AccessTools.Field(patCounterType, "m_SpecificCountOrGreater") : null;
            var counterCountIrrelevantField = patCounterType != null ? AccessTools.Field(patCounterType, "m_CountIrrelevant") : null;

            List<Dictionary<string, object>> DumpPatCounters(IEnumerable? counters)
            {
                var list = new List<Dictionary<string, object>>();
                if (counters == null) return list;
                foreach (var counter in counters)
                {
                    if (counter == null) continue;
                    var patAsset = counterPatField?.GetValue(counter) as UnityEngine.Object;
                    list.Add(new Dictionary<string, object>
                    {
                        ["pat"] = patAsset?.name ?? "<null>",
                        ["specificCount"] = counterSpecificCountField?.GetValue(counter) ?? 0,
                        ["specificCountOrGreater"] = counterSpecificCountOrGreaterField?.GetValue(counter) ?? false,
                        ["countIrrelevant"] = counterCountIrrelevantField?.GetValue(counter) ?? false,
                    });
                }
                return list;
            }

            var triggers = new List<Dictionary<string, object>>();
            var instances = Resources.FindObjectsOfTypeAll(triggerableBaseType);
            foreach (var instance in instances)
            {
                if (instance == null) continue;
                var unityObj = instance as UnityEngine.Object;
                var entry = new Dictionary<string, object>
                {
                    ["name"] = unityObj?.name ?? "?",
                    ["type"] = instance.GetType().Name,
                    ["requirePlayerLevel"] = requirePlayerLevelField?.GetValue(instance) ?? false,
                    ["requiredLevel"] = requiredLevelField?.GetValue(instance) ?? -1,
                    ["requiredLevelComparison"] = requiredLevelComparisonField?.GetValue(instance)?.ToString() ?? "?",
                    ["onlyTriggersOnce"] = onlyTriggersOnceField?.GetValue(instance) ?? false,
                    ["hasBeenTriggered"] = hasBeenTriggeredProp?.GetValue(instance) ?? false,
                };

                if (patConditionalType != null && patConditionalType.IsInstanceOfType(instance) && conditionalTriggersField != null)
                {
                    var conditionalList = conditionalTriggersField.GetValue(instance) as IEnumerable;
                    var conditions = new List<Dictionary<string, object>>();
                    if (conditionalList != null)
                    {
                        foreach (var pair in conditionalList)
                        {
                            var conditionAsset = conditionAssetField?.GetValue(pair) as UnityEngine.Object;
                            if (conditionAsset == null) continue;
                            conditions.Add(new Dictionary<string, object>
                            {
                                ["conditionAsset"] = conditionAsset.name,
                                ["all"] = DumpPatCounters(allField?.GetValue(conditionAsset) as IEnumerable),
                                ["any"] = DumpPatCounters(anyField?.GetValue(conditionAsset) as IEnumerable),
                                ["none"] = DumpPatCounters(noneField?.GetValue(conditionAsset) as IEnumerable),
                            });
                        }
                    }
                    entry["conditionalTriggers"] = conditions;
                }

                triggers.Add(entry);
            }
            Plugin.Log.LogInfo($"[TriggerConditions] {triggers.Count} loaded TriggerableBase instance(s) found.");

            try
            {
                var outDir = Path.Combine(Path.GetDirectoryName(Plugin.OutputPath.Value)!, "campaign_progress");
                Directory.CreateDirectory(outDir);
                var path = Path.Combine(outDir, "trigger_conditions.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(triggers, Formatting.Indented));
                Plugin.Log.LogInfo($"[TriggerConditions] Wrote {triggers.Count} entries to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[TriggerConditions] Write error: {ex.Message}");
            }
        }
    }

    // ── Existing patches ─────────────────────────────────────────────────────

    [HarmonyPatch(typeof(RigidbodyUtils), nameof(RigidbodyUtils.CalculateMassFromVolume))]
    static class Patch_Mass
    {
        static void Postfix(GameObject obj, float __result)
        {
            if (!Plugin.EnrichmentEnabled.Value) return;
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
            if (!Plugin.EnrichmentEnabled.Value) return;
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
            if (State.AlreadyCaptured(guid!) && State.HasJsaName(guid!) && State.HasSpName(guid!)) return;

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

            // Capture JSA name and SP asset name from any StructurePart in this prefab's hierarchy
            string? jsaName = null;
            string? spMatName = null;
            foreach (var sp in root.GetComponentsInChildren<StructurePart>(true))
            {
                var spa = sp.StructurePartAsset;
                var jsa = spa?.Data?.JointSetupAsset;
                if (jsa != null) { jsaName = jsa.name; spMatName = spa!.name; break; }
            }

            // Authoritative SP/BP: read directly off __instance (the StructurePart whose Start()
            // fired this postfix), not the child-walk above which can grab a Ghost Variant fallback
            // panel's StructurePart instead of the real prop's. EntityBlueprintComponent is looked up
            // on the same GameObject as __instance for the same reason.
            var spAsset = __instance.StructurePartAsset;
            var bpComp = go.GetComponent<EntityBlueprintComponent>();
            var bpAsset = bpComp != null ? (UnityEngine.Object)AccessTools.Field(typeof(EntityBlueprintComponent), "m_BlueprintAsset")?.GetValue(bpComp) : null;
            Plugin.Log.LogInfo($"[SP.Start] {go.name}: SP={spAsset?.name ?? "null"} BP={bpAsset?.name ?? "null"}");

            State.Upsert(guid!, rootName, displayName, dims, volume, mass, jsaName, spMatName,
                spAsset?.name, bpAsset?.name);
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
                        // A pairing entry only actually allows jointing if its own IsActive flag is
                        // true — JointabilityAsset.CanJoint checks this (checkIsActive: true) in
                        // addition to the A/B match. Read it via reflection (m_IsActive backing field,
                        // matching JointPairingAsset's real serialized layout) rather than assuming
                        // every reflected pairing entry is compatible — an inactive/excluded pairing
                        // (e.g. JOINT_*_Exclude_* naming) must be recorded as false, not skipped or
                        // defaulted to true, or JCC will report false "Will Auto-Joint" verdicts for
                        // pairs the real game rejects.
                        bool isActive = true;
                        var activeField = fields.FirstOrDefault(ff => ff.Name.IndexOf("IsActive", StringComparison.OrdinalIgnoreCase) >= 0
                            && ff.FieldType == typeof(bool));
                        if (activeField != null)
                            isActive = (bool)activeField.GetValue(item)!;

                        string nameA = JsaName(a), nameB = JsaName(b);
                        var key = string.Compare(nameA, nameB, StringComparison.Ordinal) <= 0 ? (nameA, nameB) : (nameB, nameA);
                        _pairs[key] = isActive;
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
        internal static bool HasSpName(string guid) => _data.TryGetValue(guid, out var e) && !string.IsNullOrEmpty(e.SpName);

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

        internal static void Upsert(string guid, string partName, string? displayName, float[]? dims, float volume, float mass, string? jsaName = null, string? spMatName = null, string? spName = null, string? bpName = null)
        {
            if (!_data.TryGetValue(guid, out var entry))
                entry = new PartData();

            entry.PartName = partName;
            if (!string.IsNullOrEmpty(displayName)) entry.DisplayName = displayName!;
            if (dims != null) { entry.Dims = dims; entry.Volume = volume; }
            if (mass > 0f) entry.Mass = mass;
            if (!string.IsNullOrEmpty(jsaName)) entry.JsaName = jsaName!;
            if (!string.IsNullOrEmpty(spMatName)) entry.SpMatName = spMatName!;
            if (!string.IsNullOrEmpty(spName)) entry.SpName = spName!;
            if (!string.IsNullOrEmpty(bpName)) entry.BpName = bpName!;

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
        [JsonProperty("spMatName", NullValueHandling = NullValueHandling.Ignore)]
                                      public string? SpMatName;
        // spName/bpName come straight off the StructurePart/EntityBlueprintComponent whose Start()
        // fired the capture, unlike spMatName which can come from a child-walk that grabs a Ghost
        // Variant fallback panel's StructurePart instead of the real prop's.
        [JsonProperty("spName", NullValueHandling = NullValueHandling.Ignore)]
                                      public string? SpName;
        [JsonProperty("bpName", NullValueHandling = NullValueHandling.Ignore)]
                                      public string? BpName;
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID    = "me.kerballone.PartInfoLogger";
        public const string PLUGIN_NAME    = "PartInfoLogger";
        public const string PLUGIN_VERSION = "1.0.0";
    }
}
