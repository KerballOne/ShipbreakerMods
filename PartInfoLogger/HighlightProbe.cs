using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace PartInfoLogger
{
    // The scanner throws "NullReferenceException at HighlightComponent.ApplyMaterial" when one highlighted part is in a
    // state the method does not expect (for example a StructurePart whose MeshCache is null). The game's log does not
    // say WHICH part. This finalizer runs only when ApplyMaterial throws: it names the HighlightComponent's object and
    // lists every renderer entry with what is null on it, to BepInEx's log and to scanner_errors.txt next to OutputPath.
    //
    // Logging only: the exception is passed through unchanged, so game behaviour is the same with or without this.
    // Patched manually inside a try/catch so a missing/renamed target can never abort the rest of the plugin's patches.
    internal static class HighlightProbe
    {
        static readonly HashSet<int> _reported = new HashSet<int>();

        internal static void Install(Harmony harmony)
        {
            try
            {
                var m = AccessTools.GetDeclaredMethods(typeof(HighlightComponent)).Where(x => x.Name == "ApplyMaterial").ToList();
                if (m.Count != 1) { Plugin.Log.LogWarning($"[HighlightProbe] expected one ApplyMaterial, found {m.Count}; probe disabled."); return; }
                harmony.Patch(m[0], finalizer: new HarmonyMethod(typeof(HighlightProbe), nameof(Finalizer)));
                Plugin.Log.LogInfo("[HighlightProbe] hooked.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[HighlightProbe] could not hook: " + e.Message);
            }
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 12; t = t.parent, i++) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static Exception Finalizer(Exception __exception, HighlightComponent __instance)
        {
            if (__exception == null) return null;
            try
            {
                if (__instance == null || !_reported.Add(__instance.GetInstanceID())) return __exception;      // once per component
                var sb = new StringBuilder();
                sb.AppendLine($"[HighlightProbe] {DateTime.Now:yyyy-MM-dd HH:mm:ss} {__exception.GetType().Name} in HighlightComponent.ApplyMaterial on: {PathOf(__instance.transform)}");
                sb.AppendLine($"   world position {__instance.transform.position}, active {__instance.gameObject.activeInHierarchy}");
                var list = Traverse.Create(__instance).Field("mMaterialData").GetValue() as IList;
                if (list == null) sb.AppendLine("   mMaterialData is null");
                else
                {
                    sb.AppendLine($"   {list.Count} renderer entr{(list.Count == 1 ? "y" : "ies")}:");
                    for (int i = 0; i < list.Count; i++)
                    {
                        var tr = Traverse.Create(list[i]);
                        var mr = tr.Field("MeshRenderer").GetValue() as MeshRenderer;
                        var sp = tr.Field("StructurePart").GetValue() as StructurePart;
                        string line = $"   [{i}] renderer {(mr == null ? "NULL" : mr.name)}";
                        if (mr != null)
                        {
                            var mats = mr.sharedMaterials;
                            line += $" ({mats.Length} material slot{(mats.Length == 1 ? "" : "s")}: {string.Join(", ", mats.Select(x => x == null ? "NULL" : x.name).ToArray())})";
                        }
                        if (sp == null) line += " | no StructurePart";
                        else
                        {
                            line += $" | StructurePart {sp.name}";
                            object cache = null;
                            try { cache = Traverse.Create(sp).Property("MeshCache").GetValue(); } catch { }
                            line += cache == null ? " | MeshCache NULL  <-- this is what ApplyMaterial dereferences" : " | MeshCache ok";
                        }
                        sb.AppendLine(line);
                    }
                }
                string text = sb.ToString().TrimEnd();
                Plugin.Log.LogWarning(text);
                try
                {
                    var dir = Path.GetDirectoryName(Plugin.OutputPath.Value);
                    if (!string.IsNullOrEmpty(dir)) File.AppendAllText(Path.Combine(dir, "scanner_errors.txt"), text + Environment.NewLine);
                }
                catch { /* file is best effort */ }
            }
            catch (Exception e)
            {
                try { Plugin.Log.LogWarning("[HighlightProbe] could not describe the failing component: " + e.Message); } catch { }
            }
            return __exception;
        }
    }
}
