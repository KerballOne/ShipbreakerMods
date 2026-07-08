using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace RoomProbeLogger
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static Plugin Instance = null!;

        static Type? s_roomProbeType;
        static Type? s_roomContainerType;
        static Type? s_roomSubVolumeType;

        float m_nextLiveLog = 0f;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            s_roomProbeType = AccessTools.TypeByName("BBI.Unity.Game.RoomProbe");
            s_roomContainerType = AccessTools.TypeByName("BBI.Unity.Game.RoomContainerDefinition");
            s_roomSubVolumeType = AccessTools.TypeByName("BBI.Unity.Game.RoomSubVolumeDefinition");

            Log.LogInfo($"RoomProbeLogger loaded. RoomProbe type: {s_roomProbeType?.FullName ?? "NOT FOUND"}");
            Log.LogInfo($"  RoomContainerDefinition: {s_roomContainerType?.FullName ?? "NOT FOUND"}");
            Log.LogInfo($"  RoomSubVolumeDefinition: {s_roomSubVolumeType?.FullName ?? "NOT FOUND"}");

            Main.EventSystem.AddHandler((GameStateChangedEvent ev) =>
            {
                if (ev.GameState == GameSession.GameState.Gameplay)
                    Instance.StartCoroutine(ScanDelayed());
            });
        }

        private void Update()
        {
            if (Time.time < m_nextLiveLog) return;
            m_nextLiveLog = Time.time + 1f;

            if (s_roomProbeType == null) return;

            var probes = GameObject.FindObjectsOfType(s_roomProbeType);
            foreach (var obj in probes)
            {
                var mb = obj as MonoBehaviour;
                if (mb == null) continue;
                string path = GetPath(mb.transform);
                var isRegField = obj.GetType().GetField("m_IsAtmosphereRegulator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!(isRegField?.GetValue(obj) is true)) continue;

                var sb = new StringBuilder();
                sb.Append($"[LIVE] RoomProbe '{path}' pos={mb.transform.position}");
                foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try { sb.Append($" | {f.Name}={f.GetValue(obj)}"); }
                    catch { }
                }
                Log.LogInfo(sb.ToString());
            }
        }

        IEnumerator ScanDelayed()
        {
            yield return new WaitForSeconds(2f);
            Log.LogInfo("=== RoomProbeLogger scan start ===");
            var output = new Dictionary<string, object>();
            output["roomProbes"] = ScanRoomProbes();
            output["roomContainers"] = ScanRoomContainers();
            output["regulators"] = ScanRegulators();
            string json = JsonConvert.SerializeObject(output, Formatting.Indented);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(Paths.GameRootPath, $"room_probe_debug_{timestamp}.json");
            File.WriteAllText(path, json);
            Log.LogInfo($"=== RoomProbeLogger scan complete. Written to: {path} ===");
        }

        static List<object> ScanRoomProbes()
        {
            var results = new List<object>();
            if (s_roomProbeType == null) { Log.LogWarning("RoomProbe type not found"); return results; }

            var probes = GameObject.FindObjectsOfType(s_roomProbeType);
            Log.LogInfo($"[PROBES] Found {probes.Length} RoomProbe(s)");
            foreach (var obj in probes)
            {
                var mb = obj as MonoBehaviour;
                if (mb == null) continue;
                string path = GetPath(mb.transform);
                var entry = new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["worldPos"] = Vec(mb.transform.position),
                };
                var fields = new Dictionary<string, string>();
                foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var val = f.GetValue(obj);
                        // Deep-dump IList fields (like m_Triggers)
                        if (val is System.Collections.IList list && list.Count > 0)
                        {
                            var items = new List<string>();
                            foreach (var item in list)
                            {
                                if (item == null) { items.Add("null"); continue; }
                                var sb2 = new StringBuilder();
                                sb2.Append($"[{item.GetType().Name}]");
                                foreach (var sf in item.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    try { sb2.Append($" {sf.Name}={sf.GetValue(item)}"); }
                                    catch { }
                                }
                                // Also check properties
                                foreach (var sp in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    try { sb2.Append($" {sp.Name}={sp.GetValue(item)}"); }
                                    catch { }
                                }
                                items.Add(sb2.ToString());
                            }
                            fields[f.Name] = string.Join(" | ", items);
                        }
                        else
                        {
                            fields[f.Name] = val?.ToString() ?? "null";
                        }
                    }
                    catch { fields[f.Name] = "ERROR"; }
                }
                // Also dump properties on RoomProbe itself
                foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try { fields["prop:" + p.Name] = p.GetValue(obj)?.ToString() ?? "null"; }
                    catch { fields["prop:" + p.Name] = "ERROR"; }
                }
                entry["fields"] = fields;
                results.Add(entry);
                Log.LogInfo($"[PROBE] {path} @ {mb.transform.position}");
                foreach (var kv in fields)
                    Log.LogInfo($"  .{kv.Key} = {kv.Value}");
            }
            return results;
        }

        static List<object> ScanRoomContainers()
        {
            var results = new List<object>();
            if (s_roomContainerType == null) { Log.LogWarning("RoomContainerDefinition type not found"); return results; }

            var rooms = GameObject.FindObjectsOfType(s_roomContainerType);
            Log.LogInfo($"[ROOMS] Found {rooms.Length} RoomContainerDefinition(s)");
            foreach (var obj in rooms)
            {
                var mb = obj as MonoBehaviour;
                if (mb == null) continue;
                string path = GetPath(mb.transform);

                // Dump all fields on RoomContainerDefinition
                var fields = new Dictionary<string, string>();
                foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try { fields[f.Name] = f.GetValue(obj)?.ToString() ?? "null"; }
                    catch { fields[f.Name] = "ERROR"; }
                }

                // Collect sub-volumes from children
                var subVolumes = new List<object>();
                if (s_roomSubVolumeType != null)
                {
                    foreach (var sv in mb.GetComponentsInChildren(s_roomSubVolumeType, true))
                    {
                        var svMb = sv as MonoBehaviour;
                        if (svMb == null) continue;
                        var svFields = new Dictionary<string, string>();
                        foreach (var f in sv.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            try { svFields[f.Name] = f.GetValue(sv)?.ToString() ?? "null"; }
                            catch { svFields[f.Name] = "ERROR"; }
                        }
                        // Compute world-space center by transforming local center through the volume's transform
                        Vector3 worldCenter = svMb.transform.position;
                        try
                        {
                            var centerField = sv.GetType().GetField("m_Center", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (centerField != null)
                            {
                                var localCenter = (Vector3)centerField.GetValue(sv);
                                worldCenter = svMb.transform.TransformPoint(localCenter);
                            }
                        }
                        catch { }

                        // Dump local-to-world matrix columns so we know which local axis maps to which world axis
                        var t = svMb.transform;
                        var matrix = t.localToWorldMatrix;
                        // Each column is where a local basis vector lands in world space
                        // col0=localX→world, col1=localY→world, col2=localZ→world
                        var matrixInfo = new Dictionary<string, object>
                        {
                            ["localX_inWorld"] = Vec(new Vector3(matrix.m00, matrix.m10, matrix.m20)),
                            ["localY_inWorld"] = Vec(new Vector3(matrix.m01, matrix.m11, matrix.m21)),
                            ["localZ_inWorld"] = Vec(new Vector3(matrix.m02, matrix.m12, matrix.m22)),
                            ["worldPos"] = Vec(new Vector3(matrix.m03, matrix.m13, matrix.m23)),
                            ["lossyScale"] = Vec(t.lossyScale),
                            ["eulerAngles"] = Vec(t.eulerAngles),
                        };
                        // Also compute world-space AABB corners from size (axis-aligned in local space, rotated to world)
                        Vector3 worldAABB_min = Vector3.zero, worldAABB_max = Vector3.zero;
                        try
                        {
                            var sizeField = sv.GetType().GetField("m_Size", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var centerField2 = sv.GetType().GetField("m_Center", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (sizeField != null && centerField2 != null)
                            {
                                var localSize = (Vector3)sizeField.GetValue(sv);
                                var localCenter2 = (Vector3)centerField2.GetValue(sv);
                                // Transform all 8 corners and get world AABB
                                var corners = new Vector3[8];
                                for (int ci = 0; ci < 8; ci++)
                                {
                                    corners[ci] = t.TransformPoint(localCenter2 + new Vector3(
                                        ((ci & 1) == 0 ? -1 : 1) * localSize.x * 0.5f,
                                        ((ci & 2) == 0 ? -1 : 1) * localSize.y * 0.5f,
                                        ((ci & 4) == 0 ? -1 : 1) * localSize.z * 0.5f));
                                }
                                worldAABB_min = corners[0]; worldAABB_max = corners[0];
                                foreach (var c in corners)
                                {
                                    worldAABB_min = Vector3.Min(worldAABB_min, c);
                                    worldAABB_max = Vector3.Max(worldAABB_max, c);
                                }
                                matrixInfo["worldAABB_min"] = Vec(worldAABB_min);
                                matrixInfo["worldAABB_max"] = Vec(worldAABB_max);
                            }
                        }
                        catch { }

                        subVolumes.Add(new Dictionary<string, object>
                        {
                            ["path"] = GetPath(svMb.transform),
                            ["active"] = svMb.gameObject.activeInHierarchy,
                            ["worldCenter"] = Vec(worldCenter),
                            ["matrix"] = matrixInfo,
                            ["fields"] = svFields,
                        });
                    }
                }

                var entry = new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["worldPos"] = Vec(mb.transform.position),
                    ["fields"] = fields,
                    ["subVolumes"] = subVolumes,
                };
                results.Add(entry);
                Log.LogInfo($"[ROOM] {path} @ {mb.transform.position}");
                foreach (var kv in fields)
                    Log.LogInfo($"  .{kv.Key} = {kv.Value}");
                foreach (var sv in subVolumes)
                {
                    var svd = sv as Dictionary<string, object>;
                    var mat = svd?["matrix"] as Dictionary<string, object>;
                    Log.LogInfo($"  SubVol {svd?["path"]} active={svd?["active"]} worldCenter={svd?["worldCenter"]}");
                    Log.LogInfo($"    localX→world={mat?["localX_inWorld"]} localY→world={mat?["localY_inWorld"]} localZ→world={mat?["localZ_inWorld"]}");
                    if (mat != null && mat.ContainsKey("worldAABB_min"))
                        Log.LogInfo($"    worldAABB: min={mat["worldAABB_min"]} max={mat["worldAABB_max"]}");
                }
            }
            return results;
        }

        static List<object> ScanRegulators()
        {
            var results = new List<object>();
            var allObjects = GameObject.FindObjectsOfType<GameObject>();
            Log.LogInfo($"[REGULATORS] Scanning {allObjects.Length} GameObjects for AtmosphereRegulator...");

            foreach (var go in allObjects)
            {
                if (go.name.IndexOf("AtmosphereRegulator", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string path = GetPath(go.transform);
                var compNames = new List<string>();
                var compFields = new Dictionary<string, Dictionary<string, string>>();

                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;
                    compNames.Add(typeName);

                    if (typeName == "MachinePartActionsComponent" || typeName == "InteractableObject" ||
                        typeName.Contains("Regulator") || typeName.Contains("Interactable") || typeName.Contains("Room"))
                    {
                        var fd = new Dictionary<string, string>();
                        foreach (var f in comp.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            try { fd[f.Name] = f.GetValue(comp)?.ToString() ?? "null"; }
                            catch { fd[f.Name] = "ERROR"; }
                        }
                        compFields[typeName] = fd;
                    }
                }

                var entry = new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["worldPos"] = Vec(go.transform.position),
                    ["active"] = go.activeInHierarchy,
                    ["components"] = compNames,
                    ["interestingFields"] = compFields,
                };
                results.Add(entry);
                Log.LogInfo($"[REG] {path} @ {go.transform.position} active={go.activeInHierarchy}");
                Log.LogInfo($"  Components: {string.Join(", ", compNames)}");
                foreach (var kv in compFields)
                {
                    Log.LogInfo($"  [{kv.Key}]");
                    foreach (var fv in kv.Value)
                        Log.LogInfo($"    .{fv.Key} = {fv.Value}");
                }
            }
            return results;
        }

        static string GetPath(Transform t)
        {
            var parts = new System.Collections.Generic.List<string>();
            while (t != null) { parts.Insert(0, t.name); t = t.parent; }
            return string.Join("/", parts);
        }

        static object Vec(Vector3 v) => new { x = v.x, y = v.y, z = v.z };
    }
}
