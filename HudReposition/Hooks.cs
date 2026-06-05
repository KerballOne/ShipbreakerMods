using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace HudReposition
{
    static class CanvasHelper
    {
        // Walks up the transform hierarchy to find the root Canvas and returns its scaleFactor
        // and rect size (in canvas units). anchoredPosition is in canvas units, so to move by
        // a screen percentage we need: canvasUnits = (percent / 100) * screenSize / scaleFactor
        internal static bool TryGetRootCanvas(Transform t, out Canvas canvas, out Rect rect)
        {
            var current = t;
            while (current != null)
            {
                var c = current.GetComponent<Canvas>();
                if (c != null && c.isRootCanvas)
                {
                    canvas = c;
                    rect = (current as RectTransform)?.rect ?? Rect.zero;
                    return true;
                }
                current = current.parent;
            }
            canvas = null!;
            rect = Rect.zero;
            return false;
        }

        // Converts a percentage offset (e.g. -15 = -15%) to canvas units for this element's canvas.
        internal static Vector2 PercentToCanvasUnits(Vector2 percent, Transform t)
        {
            if (!TryGetRootCanvas(t, out var canvas, out var rect))
            {
                // Fallback: assume 1920x1080 at scale 1
                Plugin.Log.LogWarning("[HudReposition] Could not find root canvas, using fallback 1920x1080.");
                return new Vector2(percent.x / 100f * 1920f, percent.y / 100f * 1080f);
            }

            float scale = canvas.scaleFactor;
            // rect.width/height are in canvas units; multiply by scale to get screen pixels,
            // then divide back by scale to convert our screen-percentage target back to canvas units.
            // Simplified: canvas units = (percent / 100) * (screenPixels / scaleFactor)
            float screenW = rect.width * scale;
            float screenH = rect.height * scale;
            float cx = percent.x / 100f * screenW / scale;
            float cy = percent.y / 100f * screenH / scale;
            Plugin.Log.LogInfo($"[HudReposition] Canvas: scaleFactor={scale} screenSize={screenW}x{screenH} → canvasUnits=({cx:F1}, {cy:F1}) for {percent}%");
            return new Vector2(cx, cy);
        }
    }

    [HarmonyPatch(typeof(ObjectInfoUIController), "Awake")]
    static class PatchObjectInfoAwake
    {
        static void Postfix(ObjectInfoUIController __instance)
        {
            var percent = Plugin.ParseOffset(Plugin.PartInfoOffset);
            if (percent == Vector2.zero) return;

            var rt = __instance.GetComponent<RectTransform>();
            if (rt == null) return;

            var delta = CanvasHelper.PercentToCanvasUnits(percent, __instance.transform);
            rt.anchoredPosition += delta;
            Plugin.Log.LogInfo($"[HudReposition] PartInfo moved by {percent}% ({delta}) → {rt.anchoredPosition}");
        }
    }

    [HarmonyPatch(typeof(ObjectSalvageInfoUIController), "Awake")]
    static class PatchSalvageInfoAwake
    {
        static void Postfix(ObjectSalvageInfoUIController __instance)
        {
            var percent = Plugin.ParseOffset(Plugin.SalvageLabelOffset);
            if (percent == Vector2.zero) return;

            var rt = __instance.GetComponent<RectTransform>();
            if (rt == null) return;

            var delta = CanvasHelper.PercentToCanvasUnits(percent, __instance.transform);
            rt.anchoredPosition += delta;
            Plugin.Log.LogInfo($"[HudReposition] SalvageLabel moved by {percent}% ({delta}) → {rt.anchoredPosition}");
        }
    }
}
