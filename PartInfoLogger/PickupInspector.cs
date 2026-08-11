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
    //
    // Two entry points, both cheap:
    //   - Tick(): F9-bound, one raycast from the camera on keypress, dumps whatever's under the
    //     reticle. Zero cost when not pressed.
    //   - DumpAllInteractables(): one-shot census of every InteractableObject in the loaded scene via
    //     Resources.FindObjectsOfTypeAll — bounded to the handful of real interactables per ship
    //     (doors, switches, pickups), not every StructurePart, so it's cheap to run unconditionally
    //     once at gameplay start.
    internal static class PickupInspector
    {
        public static void Tick()
        {
            if (!Plugin.EnrichmentEnabled.Value) return;
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

        public static IEnumerator DumpAllInteractables()
        {
            yield return new WaitForSeconds(2f);
            if (!Plugin.EnrichmentEnabled.Value) yield break;

            var all = Resources.FindObjectsOfTypeAll<InteractableObject>()
                .Where(io => io != null && io.gameObject.scene.IsValid()) // exclude prefab assets, only live scene instances
                .ToList();

            Plugin.Log.LogInfo($"[PickupInspector] InteractableObject census: {all.Count} found in scene.");
            foreach (var io in all)
            {
                Plugin.Log.LogInfo($"[PickupInspector] Interactable on '{io.gameObject.name}': {DumpObject(io)}");
                if (io.Asset != null)
                    Plugin.Log.LogInfo($"[PickupInspector]   Asset('{io.Asset.name}').Data: {DumpObject(io.Asset.Data)}");
                DumpFullChain(io.gameObject, labelOnly: true);
            }
        }

        // Component type names considered boilerplate mesh/render/physics plumbing — identical across
        // baked and addressable copies, excluded from full dumps to keep output readable.
        static readonly HashSet<string> s_boilerplateTypes = new HashSet<string> {
            "Transform", "MeshFilter", "MeshRenderer", "LODGroup", "ShatterableComponent",
            "HighlightComponent", "ModuleDefinition", "MeshCollider", "BoxCollider",
            "SphereCollider", "CapsuleCollider"
        };

        static void DumpFullChain(GameObject start, bool labelOnly = false)
        {
            var sb = new System.Text.StringBuilder();
            var tt = start.transform;
            for (int d = 0; d < 20 && tt != null; d++, tt = tt.parent)
            {
                foreach (var c in tt.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var tn = c.GetType().Name;
                    if (labelOnly)
                    {
                        sb.Append($"[{tt.gameObject.name}.{tn}] ");
                        continue;
                    }
                    if (s_boilerplateTypes.Contains(tn)) continue;
                    sb.Append($"[{tt.gameObject.name}.{tn}: {DumpObject(c)}] ");
                }
            }
            Plugin.Log.LogInfo($"[PickupInspector] Chain from '{start.name}': {sb}");
        }

        // Generic reflective dumper: every public property + every field (public/private, instance) on
        // an object, one level deep, as "name=value". Deliberately shallow to avoid runaway output /
        // reference cycles.
        internal static string DumpObject(object obj, int maxLen = 2000)
        {
            if (obj == null) return "null";
            try
            {
                var type = obj.GetType();
                var parts = new List<string>();

                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    try { parts.Add($"{p.Name}={FormatVal(p.GetValue(obj))}"); } catch { }
                }
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try { parts.Add($"{f.Name}={FormatVal(f.GetValue(obj))}"); } catch { }
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
