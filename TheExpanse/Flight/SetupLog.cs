using System.Collections.Generic;
using System.Text;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace TheExpanse
{
    // Diagnostics for setup mode, all of it in one run's log ([Setup] lines), so a single test shows what happened:
    //   the ship's identity: every name the game has for it (the class shown in the ship selector, its own name, the
    //       maker, role, the construction asset, the root module and its prefab), the model picked, and how the entry
    //       in ships.json was matched (by model or by reference part, and how many parts carry that name);
    //   each press of the place button in setup mode: the controller's state, the part and surface it aims at (part,
    //       collider, layer, distance), the preview's state, whether the game can joint a charge to that part, and the
    //       charges in the world against the limit;
    //   each charge's life: made, deployed (jointed or free-floating), free-floating, and 0.5 s and 2 s later where it
    //       is against where it went down (drifted?), whether its body is held still, and whether it still exists;
    //   each key's result: for every charge, the surface used (the game's aim or a cast back) and what was saved; the
    //       file written (path, size, ships in it).
    internal static class SetupLog
    {
        static void Log(string text) => Plugin.Log.LogInfo("[Setup] " + text);

        static string V(Vector3 v) => $"({v.x:0.00}, {v.y:0.00}, {v.z:0.00})";

        // ---- the ship ------------------------------------------------------------------------------------------------
        internal static void Identity(ShipPreview? preview, string model, string match)
        {
            var sb = new StringBuilder("ship identity: ");
            if (preview == null) sb.Append("no ship preview; ");
            else
            {
                string Loc(string? id)
                {
                    if (string.IsNullOrEmpty(id)) return "(empty)";
                    try { if (Main.Instance.LocalizationService.TryLocalize(id, out var l)) return $"'{id}' = '{l}'"; } catch { }
                    return $"'{id}'";
                }
                sb.Append($"class (archetype) {Loc(preview.Archetype)}; name '{preview.ShipName}'; maker '{preview.CompanyName}'; ");
                sb.Append($"role {Loc(preview.Role)}; theme {Loc(preview.Theme)}; size {Loc(preview.Size)}; ");
                try
                {
                    var construction = preview.ConstructionAsset;
                    sb.Append($"construction asset '{(construction != null ? construction.name : "(not loaded)")}' ({preview.ConstructionAssetRef?.AssetGUID}); ");
                }
                catch { sb.Append("construction asset (unreadable); "); }
                var group = preview.ModuleGroup;
                var root = group?.RootModule;
                sb.Append($"root module '{(root?.ModuleDefinition != null ? root.ModuleDefinition.name : "(not loaded)")}' prefab {root?.ModulePrefabRef?.AssetGUID}; ");
                if (group != null)
                {
                    var names = new List<string>();
                    foreach (var m in group.AllModules) names.Add(m?.ModuleDefinition != null ? m.ModuleDefinition.name : (m?.ModulePrefabRef?.AssetGUID ?? "?"));
                    sb.Append($"{names.Count} module(s): {string.Join(", ", names.GetRange(0, Mathf.Min(12, names.Count)))}{(names.Count > 12 ? ", ..." : "")}; ");
                }
            }
            sb.Append($"model picked '{model}'; {match}");
            Log(sb.ToString());
        }

        // ---- a press of the place button ------------------------------------------------------------------------------------
        static readonly System.Reflection.FieldInfo? sState = AccessTools.Field(typeof(DemoChargeController), "mState");
        static readonly System.Reflection.FieldInfo? sTarget = AccessTools.Field(typeof(DemoChargeController), "mPlacementTarget");
        static readonly System.Reflection.FieldInfo? sHit = AccessTools.Field(typeof(DemoChargeController), "mPlacementHitInfo");
        static readonly System.Reflection.FieldInfo? sPreview = AccessTools.Field(typeof(DemoChargeController), "m_Preview");
        static readonly System.Reflection.FieldInfo? sData = AccessTools.Field(typeof(DemoChargeController), "mData");
        static readonly System.Reflection.FieldInfo? sQuery = AccessTools.Field(typeof(DemoChargeController), "mDemoChargeQuery");
        static readonly System.Reflection.FieldInfo? sUnlimited = AccessTools.Field(typeof(DemoChargeController), "mUnlimitedDemoCharge");
        static readonly System.Reflection.FieldInfo? sPreviewState = AccessTools.Field(typeof(DemoChargePreview), "mState");
        static readonly System.Reflection.FieldInfo? sPreviewJsa = AccessTools.Field(typeof(DemoChargePreview), "mDemoChargeJSA");

        [HarmonyPatch(typeof(DemoChargeController), "UpdatePlacementInputs")]
        static class PlacePress
        {
            static void Prefix(DemoChargeController __instance)
            {
                if (!ShipSetup.InSetup || LynxControls.Instance == null) return;
                if (!LynxControls.Instance.GetInputWasPressed(GameplayActions.GameplayActionSet.DemoChargeFire)) return;
                try { ShipSetup.NoteAim(sHit?.GetValue(__instance) is RaycastHit aim ? aim : default); Press(__instance); }
                catch (System.Exception ex) { Log("place press: could not read the controller: " + ex.GetBaseException().Message); }
            }
        }

        static void Press(DemoChargeController c)
        {
            var target = sTarget?.GetValue(c) as StructurePart;
            var hit = sHit?.GetValue(c) is RaycastHit h ? h : default;
            var preview = sPreview?.GetValue(c) as DemoChargePreview;
            object? previewState = preview != null ? sPreviewState?.GetValue(preview) : null;
            var chargeJsa = preview != null ? sPreviewJsa?.GetValue(preview) as JointSetupAsset : null;
            string canJoint = "?";
            try
            {
                var targetJsa = target?.StructurePartAsset?.Data?.JointSetupAsset;
                if (target != null) canJoint = targetJsa == null ? "no joint setup on the part" : JointingService.Instance.CanJoint(targetJsa, chargeJsa) ? "yes" : "NO";
            }
            catch (System.Exception ex) { canJoint = "unreadable: " + ex.GetBaseException().Message; }
            string count = "?";
            try
            {
                if (sQuery?.GetValue(c) is EntityQuery q && sData?.GetValue(c) is IDemoChargeControllerData data)
                    count = $"{q.CalculateEntityCount()} of {data.InWorldLimit}";
            }
            catch { }
            var camera = Camera.main;
            float distance = camera != null && hit.collider != null ? Vector3.Distance(camera.transform.position, hit.point) : -1f;
            Log($"place press: controller {sState?.GetValue(c)}, target part '{(target != null ? target.name : "none")}', " +
                $"aim at '{(hit.collider != null ? hit.collider.name : "nothing")}' (layer {(hit.collider != null ? LayerMask.LayerToName(hit.collider.gameObject.layer) : "-")}, " +
                $"{distance:0.0} m, point {V(hit.point)}, normal {V(hit.normal)}), preview {previewState ?? "?"}, can joint a charge to it: {canJoint}, " +
                $"charges in the world {count}, unlimited {sUnlimited?.GetValue(c)}.");
        }

        // ---- a charge's life ----------------------------------------------------------------------------------------------
        sealed class Watch { internal GameObject Go = null!; internal Vector3 At; internal float Since; internal int Checks; }
        static readonly List<Watch> sWatched = new List<Watch>();

        internal static void Instantiated(DemoChargeInstantiatedEvent ev)
        {
            if (!ShipSetup.InSetup || ev.DemoCharge == null) return;
            Log($"charge made: '{ev.DemoCharge.name}' at {V(ev.DemoCharge.transform.position)}{Body(ev.DemoCharge)}.");
        }

        internal static void Deployed(DemoChargeDeployedEvent ev)
        {
            if (!ShipSetup.InSetup || ev.DemoCharge == null) return;
            Log($"charge deployed: '{ev.DemoCharge.name}' at {V(ev.DemoCharge.transform.position)}, " +
                $"{(ev.WasFreefloating ? "FREE-FLOATING (not jointed to a part)" : "jointed to a part")}{Body(ev.DemoCharge)}.");
            sWatched.Add(new Watch { Go = ev.DemoCharge, At = ev.DemoCharge.transform.position, Since = Time.unscaledTime });
        }

        internal static void FreeFloating(DemoChargeFreeFloatingEvent ev)
        {
            if (!ShipSetup.InSetup || ev.DemoCharge == null) return;
            Log($"charge went free-floating: '{ev.DemoCharge.name}' at {V(ev.DemoCharge.transform.position)}{Body(ev.DemoCharge)}.");
        }

        static string Body(GameObject go)
        {
            var body = go.GetComponentInParent<Rigidbody>();
            if (body == null) return ", no body";
            int joints = body.GetComponents<Joint>().Length;
            return $", body '{body.name}' {(body.isKinematic ? "held still" : "free")} speed {body.velocity.magnitude:0.00} m/s, {joints} physics joint(s)";
        }

        // every frame (Update): the charges 0.5 s and 2 s after they went down
        internal static void Tick()
        {
            for (int i = sWatched.Count - 1; i >= 0; i--)
            {
                var w = sWatched[i];
                float age = Time.unscaledTime - w.Since;
                if ((w.Checks == 0 && age < 0.5f) || (w.Checks == 1 && age < 2f)) continue;
                if (w.Go == null) { Log($"charge {age:0.0} s after it went down: gone."); sWatched.RemoveAt(i); continue; }
                Log($"charge '{w.Go.name}' {age:0.0} s after it went down: moved {Vector3.Distance(w.Go.transform.position, w.At):0.00} m, " +
                    $"{(w.Go.activeInHierarchy ? "shown" : "HIDDEN")}{Body(w.Go)}.");
                if (++w.Checks >= 2) sWatched.RemoveAt(i); else sWatched[i] = w;
            }
        }

        internal static void Reset() => sWatched.Clear();

        // ---- results ------------------------------------------------------------------------------------------------------------
        internal static void Used(string key, int index, bool fromAim, Vector3 point, Vector3 normal, Collider? collider)
        {
            var part = collider != null ? collider.GetComponentInParent<StructurePart>() : null;
            Log($"{key}: charge {index + 1}: surface from {(fromAim ? "the game's aim as it went down" : "a cast back from the charge")}: point {V(point)}, " +
                $"normal {V(normal)}, collider '{(collider != null ? collider.name : "?")}', part '{(part != null ? part.name : "none")}'.");
        }

        internal static void Saved(string path, int ships, string summary)
        {
            long size = -1;
            try { size = new System.IO.FileInfo(path).Length; } catch { }
            Log($"saved {path}: {size} bytes, {ships} ship(s); this ship: {summary}.");
        }
    }
}
