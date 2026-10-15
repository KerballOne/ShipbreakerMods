using System;
using System.Collections.Generic;
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
        internal static Vector2 PercentToCanvasUnits(Vector2 percent, Transform t, bool log)
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
            if (log) Plugin.Log.LogInfo($"[HudReposition] Canvas: scaleFactor={scale} screenSize={screenW}x{screenH} → canvasUnits=({cx:F1}, {cy:F1}) for {percent}%");
            return new Vector2(cx, cy);
        }
    }

    // Remembers a panel's original position so its offset can be re-applied whenever the settings
    // change (HUD options tab), instead of only once in Awake.
    internal sealed class HudOffset : MonoBehaviour
    {
        private static readonly List<HudOffset> sAll = new List<HudOffset>();

        private RectTransform mRect = null!;
        private Vector2 mBase;
        private Func<Vector2> mPercent = null!;
        private string mName = "";

        internal static void Attach(Component panel, string name, Func<Vector2> percent)
        {
            var rect = panel.GetComponent<RectTransform>();
            if (rect == null) return;
            var offset = panel.gameObject.AddComponent<HudOffset>();
            offset.mRect = rect;
            offset.mBase = rect.anchoredPosition;
            offset.mPercent = percent;
            offset.mName = name;
            sAll.Add(offset);
            offset.Apply(log: true);
        }

        internal static void ApplyAll()
        {
            foreach (var offset in sAll)
                if (offset != null) offset.Apply(log: false);
        }

        private void Apply(bool log)
        {
            var percent = mPercent();
            var delta = percent == Vector2.zero ? Vector2.zero : CanvasHelper.PercentToCanvasUnits(percent, transform, log);
            mRect.anchoredPosition = mBase + delta;
            if (log && percent != Vector2.zero)
                Plugin.Log.LogInfo($"[HudReposition] {mName} moved by {percent}% ({delta}) → {mRect.anchoredPosition}");
        }

        private void OnDestroy() => sAll.Remove(this);
    }

    [HarmonyPatch(typeof(ObjectInfoUIController), "Awake")]
    static class PatchObjectInfoAwake
    {
        static void Postfix(ObjectInfoUIController __instance) =>
            HudOffset.Attach(__instance, "PartInfo", () => Plugin.PartInfoOffset);
    }

    [HarmonyPatch(typeof(ObjectSalvageInfoUIController), "Awake")]
    static class PatchSalvageInfoAwake
    {
        static void Postfix(ObjectSalvageInfoUIController __instance) =>
            HudOffset.Attach(__instance, "SalvageLabel", () => Plugin.SalvageLabelOffset);
    }
}
