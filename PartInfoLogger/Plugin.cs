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
        internal static ConfigEntry<string> JointCheckPartA = null!;
        internal static ConfigEntry<string> JointCheckPartB = null!;
        internal static ConfigEntry<float> JointCheckDelay = null!;
        internal static ConfigEntry<bool> JointCensusEnabled = null!;
        internal static ConfigEntry<float> JointCensusDelay = null!;
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

            JointCheckPartA = Config.Bind(
                "JointCheck", "PartAName", "",
                "GameObject name of the first part to check joint connectivity for. Leave blank to disable the check.");
            JointCheckPartB = Config.Bind(
                "JointCheck", "PartBName", "",
                "GameObject name of the second part to check joint connectivity for. Leave blank to disable the check.");
            JointCheckDelay = Config.Bind(
                "JointCheck", "DelaySeconds", 10f,
                "How many seconds after gameplay start to run the joint connectivity check.");

            JointCensusEnabled = Config.Bind(
                "JointCensus", "Enabled", true,
                "If true, dumps a CSV of every part's name and its jointed-neighbor count (excluding InvisibleJoint markers) on gameplay start.");
            JointCensusDelay = Config.Bind(
                "JointCensus", "DelaySeconds", 10f,
                "How many seconds after gameplay start to run the full-ship joint census.");

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
                    Instance.StartCoroutine(TryDumpAllSpAssets());
                    Instance.StartCoroutine(TryDumpSpBpFields());
                    Instance.StartCoroutine(DumpMaterialProperties());
                    Instance.StartCoroutine(CheckJointConnectivity());
                    Instance.StartCoroutine(DumpJointCensus());
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
                if (!typeof(ScriptableObject).IsAssignableFrom(declaredType.IsArray ? declaredType.GetElementType() : null))
                    return;
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

        // Reports whether two named GameObjects (StructureParts) are connected in the
        // runtime ECS structure graph, mirroring BreakableJointComponent.TryGetConnections().
        internal static IEnumerator CheckJointConnectivity()
        {
            var nameA = JointCheckPartA.Value;
            var nameB = JointCheckPartB.Value;
            if (string.IsNullOrWhiteSpace(nameA) || string.IsNullOrWhiteSpace(nameB))
                yield break;

            yield return new WaitForSeconds(JointCheckDelay.Value);

            var allParts = Resources.FindObjectsOfTypeAll<StructurePart>();
            StructurePart? partA = allParts.FirstOrDefault(p => p != null && p.gameObject.name.Replace("(Clone)", "").Trim() == nameA);
            StructurePart? partB = allParts.FirstOrDefault(p => p != null && p.gameObject.name.Replace("(Clone)", "").Trim() == nameB);

            if (partA == null) { Plugin.Log.LogWarning($"[JointCheck] Could not find StructurePart named '{nameA}'"); yield break; }
            if (partB == null) { Plugin.Log.LogWarning($"[JointCheck] Could not find StructurePart named '{nameB}'"); yield break; }

            if (!EntityBlueprintComponent.IsValid(partA.EntityBlueprintComponent) ||
                !EntityBlueprintComponent.IsValid(partB.EntityBlueprintComponent))
            {
                Plugin.Log.LogWarning($"[JointCheck] '{nameA}' or '{nameB}' has no valid EntityBlueprintComponent yet.");
                yield break;
            }

            var entityA = partA.Entity;
            var entityB = partB.Entity;
            var entityManager = partA.EntityBlueprintComponent.EntityManager;

            using var connectedNodes = new NativeList<Entity>(Allocator.Temp);
            GetConnectedEntities(entityManager, entityA, connectedNodes);

            bool connected = false;
            foreach (var e in connectedNodes)
            {
                if (e == entityB) { connected = true; break; }
            }

            Plugin.Log.LogInfo($"[JointCheck] '{nameA}' (Entity {entityA.Index}) is " +
                $"{(connected ? "CONNECTED" : "NOT connected")} to '{nameB}' (Entity {entityB.Index}) " +
                $"in the structure graph. Graph size checked: {connectedNodes.Length} nodes.");
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

            var rows = new List<(string Name, int NeighborCount, Entity Entity)>();
            var unionFind = new Dictionary<Entity, Entity>();
            using var connectedNodes = new NativeList<Entity>(Allocator.Temp);

            foreach (var part in allParts)
            {
                if (part == null) continue;
                var name = part.gameObject.name.Replace("(Clone)", "").Trim();
                if (ijEntities.Count > 0 && ijMarkerType != null && part.GetComponent(ijMarkerType) != null)
                    continue; // don't list IJ markers themselves as parts

                if (!EntityBlueprintComponent.IsValid(part.EntityBlueprintComponent))
                {
                    rows.Add((name, 0, Entity.Null));
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

                rows.Add((name, neighborCount, entity));
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
                sb.AppendLine("PartName,JointedNeighborCount,ClusterId,ClusterSize");
                var indices = Enumerable.Range(0, rows.Count).OrderBy(i => rows[i].Name, StringComparer.Ordinal);
                foreach (var i in indices)
                {
                    var row = rows[i];
                    var hasCluster = row.Entity != Entity.Null;
                    var clusterId = hasCluster ? rowToCluster[i].ToString() : "";
                    var size = hasCluster ? clusterSize[rowToCluster[i]].ToString() : "";
                    sb.AppendLine($"{CsvEscape(row.Name)},{row.NeighborCount},{clusterId},{size}");
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

            // Capture JSA name and SP asset name from any StructurePart in this prefab's hierarchy
            string? jsaName = null;
            string? spMatName = null;
            foreach (var sp in root.GetComponentsInChildren<StructurePart>(true))
            {
                var spa = sp.StructurePartAsset;
                var jsa = spa?.Data?.JointSetupAsset;
                if (jsa != null) { jsaName = jsa.name; spMatName = spa!.name; break; }
            }

            // Log SP and BP asset names to confirm whether ACL has fired by the time Start() runs
            var spAsset = __instance.StructurePartAsset;
            var bpComp = go.GetComponent<EntityBlueprintComponent>();
            var bpAsset = bpComp != null ? (UnityEngine.Object)AccessTools.Field(typeof(EntityBlueprintComponent), "m_BlueprintAsset")?.GetValue(bpComp) : null;
            Plugin.Log.LogInfo($"[SP.Start] {go.name}: SP={spAsset?.name ?? "null"} BP={bpAsset?.name ?? "null"}");

            State.Upsert(guid!, rootName, displayName, dims, volume, mass, jsaName, spMatName);
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

        internal static void Upsert(string guid, string partName, string? displayName, float[]? dims, float volume, float mass, string? jsaName = null, string? spMatName = null)
        {
            if (!_data.TryGetValue(guid, out var entry))
                entry = new PartData();

            entry.PartName = partName;
            if (!string.IsNullOrEmpty(displayName)) entry.DisplayName = displayName!;
            if (dims != null) { entry.Dims = dims; entry.Volume = volume; }
            if (mass > 0f) entry.Mass = mass;
            if (!string.IsNullOrEmpty(jsaName)) entry.JsaName = jsaName!;
            if (!string.IsNullOrEmpty(spMatName)) entry.SpMatName = spMatName!;

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
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID    = "me.kerballone.PartInfoLogger";
        public const string PLUGIN_NAME    = "PartInfoLogger";
        public const string PLUGIN_VERSION = "1.0.0";
    }
}
