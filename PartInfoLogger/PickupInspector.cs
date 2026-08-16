using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBI.Unity.Game;
using UnityEngine;

namespace PartInfoLogger
{
    // On-demand pickup/interaction diagnostics — built to compare a baked (non-addressable) pickup
    // part against a working addressable one component-for-component, without the cost of hooking
    // StructurePart.Start() (fires thousands of times per ship load; an earlier version of this
    // diagnostic lived there and was reverted for exactly that reason — see feedback in project
    // memory about PIL's EventDump patches previously flooding the log and tanking load performance).
    // The earlier whole-scene InteractableObject census (DumpAllInteractables) was removed entirely —
    // it polluted the log with unrelated objects sharing generic child names like "VOL_Interactable",
    // producing false diagnostic conclusions. F9's single-object targeted dump is the only entry point
    // now, gated by its own PickupInspectorEnabled config tunable rather than the broad
    // EnrichmentEnabled flag.
    internal static class PickupInspector
    {
        public static void Tick()
        {
            if (!Plugin.PickupInspectorEnabled.Value) return;
            if (!UnityEngine.Input.GetKeyDown(KeyCode.F9)) return;

            var camTransform = LynxCameraController.MainCameraTransform;
            if (camTransform == null)
            {
                Plugin.Log.LogInfo("[PickupInspector] F9: no player camera transform found.");
                return;
            }

            if (!Physics.Raycast(camTransform.position, camTransform.forward, out var hit, 5f))
            {
                Plugin.Log.LogInfo("[PickupInspector] F9: no raycast hit within 5m.");
                return;
            }

            Plugin.Log.LogInfo($"[PickupInspector] F9 hit: {hit.collider.gameObject.name} at dist={hit.distance:F2} " +
                $"colliderType={hit.collider.GetType().Name} isTrigger={hit.collider.isTrigger} " +
                $"layer={LayerMask.LayerToName(hit.collider.gameObject.layer)}({hit.collider.gameObject.layer})");

            DumpFullChain(hit.collider.gameObject);
        }

        // Component type names considered boilerplate mesh/render/physics plumbing — identical across
        // baked and addressable copies, excluded from full dumps to keep output readable.
        static readonly HashSet<string> s_boilerplateTypes = new HashSet<string> {
            "Transform", "MeshFilter", "MeshRenderer", "LODGroup", "ShatterableComponent",
            "HighlightComponent", "ModuleDefinition", "MeshCollider", "BoxCollider",
            "SphereCollider", "CapsuleCollider"
        };

        // Walks UP from start (ancestor context, same as before) AND DOWN into every descendant of
        // start's containing StructurePart/prefab-ish root — not just start itself. F9 raycasts hit
        // whatever collider is physically in front of the camera, which is often the solid SP mesh, not
        // a sibling trigger-collider child like "Interaction"/"VOL_Interactable" — an upward-only walk
        // could never see that child at all, which silently produced incomplete dumps (missing exactly
        // the components that matter most for interaction debugging) without any indication anything
        // was left out. Fixed after this cost real back-and-forth diagnosing a false "missing
        // components" conclusion that was actually just an unwalked sibling child.
        static void DumpFullChain(GameObject start)
        {
            var sb = new System.Text.StringBuilder();
            var visited = new HashSet<Transform>();

            // Up: start and its ancestors (ship hierarchy context).
            var tt = start.transform;
            for (int d = 0; d < 20 && tt != null; d++, tt = tt.parent)
                AppendNode(tt, sb, visited);

            // Down: every descendant of the nearest ancestor carrying a StructurePart (the part's real
            // root), or start itself if none found within the walked chain — covers sibling trigger-
            // collider children (Interaction/VOL_Interactable) that an upward-only walk would miss.
            var subtreeRoot = start.transform;
            var probe = start.transform;
            for (int d = 0; d < 20 && probe != null; d++, probe = probe.parent)
            {
                if (probe.GetComponent<StructurePart>() != null) { subtreeRoot = probe; break; }
            }
            AppendDescendants(subtreeRoot, sb, visited);

            Plugin.Log.LogInfo($"[PickupInspector] Chain from '{start.name}': {sb}");
        }

        static void AppendDescendants(Transform t, System.Text.StringBuilder sb, HashSet<Transform> visited)
        {
            AppendNode(t, sb, visited);
            foreach (Transform child in t)
                AppendDescendants(child, sb, visited);
        }

        static void AppendNode(Transform t, System.Text.StringBuilder sb, HashSet<Transform> visited)
        {
            if (!visited.Add(t)) return; // already dumped (up/down walks can overlap at start)
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                var tn = c.GetType().Name;
                if (s_boilerplateTypes.Contains(tn)) continue;
                sb.Append($"[{t.gameObject.name}.{tn}: {DumpObject(c)}] ");
            }
        }

        // Generic reflective dumper: every public property + every field (public/private, instance) on
        // an object, one level deep, as "name=value". Deliberately shallow to avoid runaway output /
        // reference cycles.
        //
        // IMPORTANT: walks the full base-type chain manually. Type.GetFields(BindingFlags.NonPublic)
        // WITHOUT FlattenHierarchy only returns fields declared directly on the runtime type — private
        // fields declared on a BASE class (e.g. TriggerableComponent.m_RequiredLevel, m_TriggerDelay,
        // m_OnlyTriggersOnce on every Triggerable* subclass) are silently invisible. This produced
        // incomplete dumps for the entire pickup/interaction investigation without any indication
        // anything was missing — e.g. m_RequiredLevel (which can silently gate QueueTrigger() from ever
        // firing) never once appeared in a TriggerableThrusterCharge dump despite being real,
        // functionally load-bearing state.
        internal static string DumpObject(object obj, int maxLen = 2000)
        {
            if (obj == null) return "null";
            try
            {
                var parts = new List<string>();
                var seenNames = new HashSet<string>();

                for (var type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
                {
                    foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        if (!seenNames.Add(p.Name)) continue; // an override/shadow already captured by the derived type
                        try { parts.Add($"{p.Name}={FormatVal(p.GetValue(obj))}"); } catch { }
                    }
                    foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (!seenNames.Add(f.Name)) continue;
                        try { parts.Add($"{f.Name}={FormatVal(f.GetValue(obj))}"); } catch { }
                    }
                }

                var result = string.Join(", ", parts);
                if (result.Length > maxLen) result = result.Substring(0, maxLen) + "...(truncated)";
                return result;
            }
            catch (Exception ex) { return $"(dump failed: {ex.Message})"; }
        }

        static string FormatVal(object v)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object uo) return uo != null ? $"'{uo.name}'" : "null(destroyed)";
            // UnityEvent listeners aren't exposed as normal fields -- use the persistent-listener API.
            if (v is UnityEngine.Events.UnityEventBase evt)
            {
                int count = evt.GetPersistentEventCount();
                if (count == 0) return "UnityEvent[0 listeners]";
                var listeners = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    var target = evt.GetPersistentTarget(i);
                    var method = evt.GetPersistentMethodName(i);
                    var targetName = target is UnityEngine.Object tuo ? (tuo != null ? tuo.name : "null") : target?.ToString() ?? "null";
                    var targetType = target?.GetType().Name ?? "?";
                    listeners.Add($"{targetType}('{targetName}').{method}");
                }
                return $"UnityEvent[{string.Join(", ", listeners)}]";
            }
            if (v is IEnumerable en && !(v is string))
            {
                var items = en.Cast<object>().Select(x => x is UnityEngine.Object xu ? (xu != null ? xu.name : "null") : x?.ToString() ?? "null");
                return "[" + string.Join(",", items) + "]";
            }
            return v.ToString();
        }
    }
}
