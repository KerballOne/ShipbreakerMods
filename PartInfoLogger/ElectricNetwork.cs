using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace PartInfoLogger
{
    // Hotkey (ElectricNetwork.Hotkey, default F9): dumps the live machine-element network (electricity, cryo, fuel)
    // to machine_network.csv, plus breaker / generator state to the BepInEx log.
    //
    // One row per machine part per element it takes part in: its role, whether it is flowing, which sources it is
    // receiving from RIGHT NOW (name + entity id, and whether that source still exists), its structure-graph key
    // (global sources only feed receivers with the same key), elemental hazard components, and the state of its
    // MachinePartActionsComponent (the stock "make safe on disconnect" script).
    //
    // Answers "what is still feeding this junction box / pipe after I pulled everything?".
    internal static class ElectricNetwork
    {
        static FieldInfo? s_ConnActs, s_DisActs, s_Triggered, s_Connected, s_Disconnected;

        internal static void Tick()
        {
            if (Plugin.NetworkHotkey == null || Plugin.NetworkHotkey.Value == KeyCode.None) return;
            if (!UnityEngine.Input.GetKeyDown(Plugin.NetworkHotkey.Value)) return;
            try { Dump(); PerfProbe.Log(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[ElectricNetwork] Dump failed: {ex}"); }
        }

        static string Csv(string? s)
        {
            s ??= "";
            return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 6; t = t.parent, i++) parts.Add(t.name.Replace("(Clone)", "").Trim());
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string NameOf(EntityManager em, Entity e, Dictionary<Entity, StructurePart> parts)
        {
            if (!em.Exists(e)) return $"<gone #{e.Index}>";
            if (parts.TryGetValue(e, out var p) && p != null) return $"{p.gameObject.name.Replace("(Clone)", "").Trim()} #{e.Index}";
            return $"<entity #{e.Index}>";
        }

        static string MpaState(StructurePart part)
        {
            var mpa = part.GetComponent<MachinePartActionsComponent>();
            if (mpa == null) return "none";
            s_ConnActs ??= AccessTools.Field(typeof(MachinePartActionsComponent), "m_OnElementConnectActions");
            s_DisActs ??= AccessTools.Field(typeof(MachinePartActionsComponent), "m_OnElementDisconnectActions");
            var baseT = typeof(MachinePartActionsComponent.MachinePartAction);
            s_Triggered ??= AccessTools.Field(baseT, "m_Triggered");
            s_Connected ??= AccessTools.Field(baseT, "m_ConnectedElements");
            s_Disconnected ??= AccessTools.Field(baseT, "m_DisconnectedElements");
            string Acts(FieldInfo? f, string tag)
            {
                if (f?.GetValue(mpa) is not Array arr) return tag + "?";
                var bits = new List<string>();
                foreach (var a in arr)
                    bits.Add($"{tag}[trig={s_Triggered?.GetValue(a)} conn={s_Connected?.GetValue(a)} disc={s_Disconnected?.GetValue(a)}]");
                return bits.Count == 0 ? tag + "[]" : string.Join(" ", bits);
            }
            return $"init={mpa.Initialized} {Acts(s_ConnActs, "C")} {Acts(s_DisActs, "D")}";
        }

        // One element type: which component types mark its roles, and the receiver->sources buffer to read.
        static void Rows<TGSrc, TLSrc, TGRecv, TLRecv, TR2S>(string element, EntityManager em,
            Dictionary<Entity, StructurePart> parts, System.Text.StringBuilder sb, Dictionary<string, int> summary)
            where TGSrc : struct, IComponentData where TLSrc : struct, IComponentData
            where TGRecv : struct, IComponentData where TLRecv : struct, IComponentData
            where TR2S : struct, IBufferElementData, IMachineElementalReceiverToSources
        {
            foreach (var kv in parts)
            {
                var e = kv.Key; var part = kv.Value;
                if (part == null || !em.Exists(e) || !em.HasComponent<MachinePartComponent>(e)) continue;
                var roles = new List<string>();
                if (em.HasComponent<TGSrc>(e)) roles.Add("GlobalSource");
                if (em.HasComponent<TLSrc>(e)) roles.Add("LocalSource");
                if (em.HasComponent<TGRecv>(e)) roles.Add("GlobalReceiver");
                if (em.HasComponent<TLRecv>(e)) roles.Add("LocalReceiver");
                if (roles.Count == 0) continue;

                var mp = em.GetComponentData<MachinePartComponent>(e);
                var sources = new List<string>();
                if (em.HasComponent<TR2S>(e))
                {
                    var buf = em.GetBuffer<TR2S>(e);
                    for (int i = 0; i < buf.Length; i++) sources.Add(NameOf(em, buf[i].Source, parts));
                }
                int graph = -1;
                if (em.HasComponent<StructureGraphNodeComponent>(e)) graph = em.GetComponentData<StructureGraphNodeComponent>(e).GraphData.GraphKey;

                var hz = new List<string>();
                if (em.HasComponent<ElectrifiableComponent>(e)) hz.Add("electrifiable");
                if (em.HasComponent<ElectrifiedComponent>(e)) hz.Add("ELECTRIFIED");
                if (em.HasComponent<ExplosiveComponent>(e)) hz.Add("explosive");
                if (em.HasComponent<FlammableComponent>(e)) hz.Add("flammable");
                if (em.HasComponent<BurningComponent>(e)) hz.Add("BURNING");
                if (em.HasComponent<FreezableComponent>(e)) hz.Add("freezable");
                if (em.HasComponent<DisableAuxiliaryElementalEffects>(e)) hz.Add("auxDISABLED");

                bool receiving = sources.Count > 0;
                var key = $"{element} {string.Join("+", roles)} receiving={receiving}";
                summary[key] = summary.TryGetValue(key, out var n) ? n + 1 : 1;

                sb.AppendLine(string.Join(",", new[]
                {
                    element, Csv(part.gameObject.name.Replace("(Clone)", "").Trim()), e.Index.ToString(),
                    string.Join("+", roles), mp.ElementFlowEnabled ? "1" : "0", receiving ? "1" : "0",
                    sources.Count.ToString(), Csv(string.Join(" | ", sources.Take(6))), graph.ToString(),
                    string.Join(" ", hz), Csv(MpaState(part)), part.gameObject.activeInHierarchy ? "1" : "0",
                    $"{part.transform.position.x:F2} {part.transform.position.y:F2} {part.transform.position.z:F2}",
                    Csv(PathOf(part.transform)),
                }));
            }
        }

        static void Dump()
        {
            var all = Resources.FindObjectsOfTypeAll<StructurePart>().Where(p => p != null && p.gameObject.scene.IsValid()).ToArray();
            var parts = new Dictionary<Entity, StructurePart>();
            EntityManager em = default; bool haveEm = false;
            foreach (var p in all)
            {
                if (!EntityBlueprintComponent.IsValid(p.EntityBlueprintComponent)) continue;
                if (!haveEm) { em = p.EntityBlueprintComponent.EntityManager; haveEm = true; }
                parts[p.Entity] = p;
            }
            if (!haveEm) { Plugin.Log.LogWarning("[ElectricNetwork] No live parts."); return; }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Element,PartName,Id,Roles,FlowEnabled,Receiving,SourceCount,Sources,GraphKey,Hazards,MachinePartActions,Active,WorldPos,Path");
            var summary = new Dictionary<string, int>();
            Rows<MachineGlobalElectricitySourceComponent, MachineLocalElectricitySourceComponent, MachineGlobalElectricityReceiverComponent, MachineLocalElectricityReceiverComponent, MachineElectricityReceiverToSources>("Electricity", em, parts, sb, summary);
            Rows<MachineGlobalCryoSourceComponent, MachineLocalCryoSourceComponent, MachineGlobalCryoReceiverComponent, MachineLocalCryoReceiverComponent, MachineCryoReceiverToSources>("Cryo", em, parts, sb, summary);
            Rows<MachineGlobalFuelSourceComponent, MachineLocalFuelSourceComponent, MachineGlobalFuelReceiverComponent, MachineLocalFuelReceiverComponent, MachineFuelReceiverToSources>("Fuel", em, parts, sb, summary);

            var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
            var path = Path.Combine(outDir, "machine_network.csv");
            File.WriteAllText(path, sb.ToString());

            // Breakers / generators: the generator only powers down safely when EVERY breaker in the world is inert.
            int breakers = 0, inert = 0;
            var live = new List<string>();
            foreach (var b in UnityEngine.Object.FindObjectsOfType<BreakerController>())
            {
                breakers++;
                if (b.IsInert) inert++;
                else live.Add($"{PathOf(b.transform)} @ {b.transform.position.x:F1} {b.transform.position.y:F1} {b.transform.position.z:F1}");
            }
            var gens = new List<string>();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PowerGeneratorComponent>()))
            using (var arr = q.ToEntityArray(Unity.Collections.Allocator.Temp))
                foreach (var g in arr)
                {
                    var c = em.GetComponentData<PowerGeneratorComponent>(g);
                    gens.Add($"{NameOf(em, g, parts)} inert={(bool)c.IsInert} reason={c.InertReason}");
                }

            Plugin.Log.LogInfo($"[ElectricNetwork] {path}");
            foreach (var kv in summary.OrderBy(k => k.Key)) Plugin.Log.LogInfo($"[ElectricNetwork]   {kv.Value,4} x {kv.Key}");
            Plugin.Log.LogInfo($"[ElectricNetwork] Breakers: {inert}/{breakers} inert");
            foreach (var l in live) Plugin.Log.LogInfo($"[ElectricNetwork]   LIVE breaker: {l}");
            Plugin.Log.LogInfo($"[ElectricNetwork] Generators with controller: {gens.Count}");
            foreach (var g in gens) Plugin.Log.LogInfo($"[ElectricNetwork]   {g}");
        }
    }
}
