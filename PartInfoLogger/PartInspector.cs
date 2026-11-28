using System;
using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace PartInfoLogger
{
    // Hotkey (PartInspector.Hotkey, default F9): info dump of the part under the crosshair. Raycasts from the camera
    // (non-trigger colliders, up to PartInspector.Range), takes the first hit that belongs to a StructurePart and writes
    // name, display name, full hierarchy path, SP / joint type, mass (part and the body it is attached to), yankable,
    // cut rating, structure group, entity id and every jointed neighbour to the BepInEx log AND to a small text panel in
    // the upper left of the screen (PartInspector.OverlaySeconds; press the key again to refresh, or on empty space to
    // clear). Neighbours come from the live joint graph (same source as the F10 census), so this shows what the part is
    // attached to right now.
    internal static class PartInspector
    {
        static string s_text = "";
        static float s_until;
        static GUIStyle? s_style;

        public static void Tick()
        {
            if (Plugin.PartInspectorHotkey == null || Plugin.PartInspectorHotkey.Value == KeyCode.None) return;
            if (!Input.GetKeyDown(Plugin.PartInspectorHotkey.Value)) return;
            try { Inspect(); }
            catch (Exception e) { Show("[PartInspector] error: " + e.Message); Plugin.Log.LogError("[PartInspector] " + e); }
        }

        static void Show(string text)
        {
            s_text = text; s_until = Time.unscaledTime + Plugin.PartInspectorOverlaySeconds.Value;
            foreach (var line in text.Split('\n')) Plugin.Log.LogInfo("[PartInspector] " + line);
        }

        static void Inspect()
        {
            var cam = LynxCameraController.MainCameraTransform;
            if (cam == null) { Show("No player camera."); return; }
            var hits = Physics.RaycastAll(cam.position, cam.forward, Plugin.PartInspectorRange.Value, ~0, QueryTriggerInteraction.Ignore)
                              .OrderBy(h => h.distance).ToArray();
            RaycastHit hit = default; StructurePart? part = null;
            foreach (var h in hits)
            {
                part = h.collider.GetComponentInParent<StructurePart>();
                if (part != null) { hit = h; break; }
            }
            if (part == null) { s_text = ""; Plugin.Log.LogInfo($"[PartInspector] no part within {Plugin.PartInspectorRange.Value:0} m"); return; }

            var sb = new System.Text.StringBuilder();
            var display = "";
            try { display = part.ObjectInfoAsset?.Data.ObjectName ?? ""; } catch { }
            sb.AppendLine($"PART  {Clean(part.gameObject.name)}" + (display.Length > 0 ? $"   ({display})" : "") + $"   {hit.distance:0.0} m");
            if (hit.collider.gameObject != part.gameObject) sb.AppendLine($"hit collider  {Clean(hit.collider.gameObject.name)}");
            sb.AppendLine($"path  {FullPath(part.transform)}");
            var spAsset = part.StructurePartAsset;
            sb.AppendLine($"SP  {spAsset?.name ?? "-"}   joint  {spAsset?.Data?.JointSetupAsset?.name ?? "-"}");
            var ct = part.CuttingTargetable;
            bool yank = false;
            try { yank = StructureHelpers.TryGetBreakableJointComponent(part, BreakableJointComponent.ForceAppliedBy.GrappleThrow, out _); } catch { }
            sb.AppendLine((ct == null ? "cut  -" : $"cut  rating {ct.PowerRating}  cuttable {ct.IsCuttable}") + $"   yankable  {(yank ? "YES" : "no")}");

            if (!EntityBlueprintComponent.IsValid(part.EntityBlueprintComponent)) { sb.AppendLine("no entity (not simulated, cannot joint)"); Show(sb.ToString().TrimEnd()); return; }
            var em = part.EntityBlueprintComponent.EntityManager; var ent = part.Entity;
            string mass = "-";
            if (em.HasComponent<MassComponent>(ent))
            {
                mass = $"{em.GetComponentData<MassComponent>(ent).Mass:0} kg";
                if (em.HasComponent<HierarchyMember>(ent))
                {
                    var parent = em.GetComponentData<HierarchyMember>(ent).Parent;
                    if (em.Exists(parent) && em.HasComponent<MassComponent>(parent)) mass += $"   attached body {em.GetComponentData<MassComponent>(parent).Mass:0} kg";
                }
            }
            sb.AppendLine($"entity  {ent.Index}   mass  {mass}");
            if (em.HasComponent<StructureGroupMember>(ent))
            {
                var g = em.GetComponentData<StructureGroupMember>(ent).GroupEntity;
                string gname = "?";
                try { var gm = em.GetComponentObject<StructureGroup>(g); if (gm != null) gname = Clean(gm.gameObject.name); } catch { }
                sb.AppendLine($"structure group  {gname}  (entity {g.Index})");
            }

            using var connected = new NativeList<Entity>(Allocator.Temp);
            Plugin.GetConnectedEntities(em, ent, connected);
            var nb = new List<string>();
            foreach (var e in connected)
            {
                if (e == ent || !em.HasComponent<StructurePart>(e)) continue;
                var np = em.GetComponentObject<StructurePart>(e);
                if (np == null) { nb.Add($"[{e.Index}] ?"); continue; }
                bool ny = false;
                try { ny = StructureHelpers.TryGetBreakableJointComponent(np, BreakableJointComponent.ForceAppliedBy.GrappleThrow, out _); } catch { }
                var jsa = np.StructurePartAsset?.Data?.JointSetupAsset?.name ?? "";
                var tags = (jsa.IndexOf("CutPoint", StringComparison.OrdinalIgnoreCase) >= 0 || np.name.IndexOf("CutPoint", StringComparison.OrdinalIgnoreCase) >= 0 ? " [cut point]" : "") + (ny ? " [yankable]" : "");
                nb.Add($"[{e.Index}] {Clean(np.gameObject.name)}  ({jsa.Replace("JOINT_", "")}){tags}  {Vector3.Distance(np.transform.position, part.transform.position):0.0} m   in {Parent(np.transform)}");
            }
            sb.AppendLine($"joint neighbours  {nb.Count}");
            foreach (var n in nb) sb.AppendLine("  " + n);
            Show(sb.ToString().TrimEnd());
        }

        static string Clean(string s) => s.Replace("(Clone)", "").Trim();

        static string FullPath(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(Clean(t.name));
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string Parent(Transform t) => t.parent != null ? Clean(t.parent.name) : "-";

        public static void OnGUI()
        {
            if (string.IsNullOrEmpty(s_text) || Time.unscaledTime > s_until) return;
            if (s_style == null)
            {
                s_style = new GUIStyle(GUI.skin.label) { fontSize = Plugin.PartInspectorFontSize.Value, wordWrap = false, richText = false };
                s_style.normal.textColor = new Color(1f, 0.95f, 0.75f);
            }
            var content = new GUIContent(s_text);
            var size = s_style.CalcSize(content);
            var w = Mathf.Min(size.x + 12, Screen.width * 0.6f); var h = Mathf.Min(size.y + 8, Screen.height * 0.9f);
            var old = GUI.color; GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(8, 8, w, h), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(14, 12, w - 12, h - 8), content, s_style);
        }
    }
}
