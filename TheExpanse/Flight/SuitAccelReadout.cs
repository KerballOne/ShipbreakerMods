using BBI.Unity.Game;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheExpanse
{
    // On the suit's own HUD, out of the seat aboard a ship under way: above the game's speed readout (the suit's own speed;
    // the ship is parked while you are on foot) the ship's speed, and under it the ship's acceleration in g and m/s² and
    // the distance from the ship to the Hab. Copies of the game's speed text (same font, color and place on the HUD), so
    // they fade and hide with the rest of the suit HUD.
    internal static class SuitAccelReadout
    {
        const float Gravity = 9.80665f;
        static readonly System.Reflection.FieldInfo? sTextField = AccessTools.Field(typeof(VelocityUIController), "m_VelocityTextField");
        static readonly System.Reflection.FieldInfo? sDecimalField = AccessTools.Field(typeof(VelocityUIController), "m_LabelDecimalFontSize");

        static TextMeshProUGUI? sSpeed, sText, sShipText, sHabText;
        static RectTransform? sPanel;
        static Transform? sHab;
        static float sNextHabLook;
        static int sDecimalSize = 12;
        static float sNextLook;
        static bool sLogged;

        internal static void Reset()
        {
            if (sText != null) Object.Destroy(sText.gameObject);
            if (sShipText != null) Object.Destroy(sShipText.gameObject);
            if (sPanel != null) Object.Destroy(sPanel.gameObject);
            sPanel = null;
            sText = sShipText = sHabText = null;
            sSpeed = null;
            sHab = null;
            sNextLook = 0f;
        }

        // Every frame (LateUpdate).
        internal static void Frame()
        {
            var player = FlightSeat.Seated ? null : FlightSeat.PlayerBody;
            bool show = Plugin.Enabled.Value && player != null && FlightModel.UnderWay && FlightModel.Aboard(player.position);
            if (!show)
            {
                if (sText != null && sText.gameObject.activeSelf) sText.gameObject.SetActive(false);
                if (sShipText != null && sShipText.gameObject.activeSelf) sShipText.gameObject.SetActive(false);
                if (sHabText != null && sHabText.gameObject.activeSelf) sHabText.gameObject.SetActive(false);
                return;
            }
            if ((sText == null || sShipText == null || sHabText == null) && !Build()) return;
            if (!sText!.gameObject.activeSelf) sText.gameObject.SetActive(true);
            if (!sShipText!.gameObject.activeSelf) sShipText.gameObject.SetActive(true);
            // the game's own format: the decimals and unit smaller
            sShipText.text = "SHIP " + FlightModel.Speed.ToString("F1").Replace(".", $"<size={sDecimalSize}>.") + " M/S</size>";
            float accel = FlightModel.FeltAcceleration;
            sText.text = $"{accel / Gravity:0.00} G   {accel:0.0} M/S<sup>2</sup>";
            var hab = FindHab();
            sHabText!.gameObject.SetActive(hab != null);
            if (hab != null)
            {
                float d = Vector3.Distance(FlightModel.ShipPosition, hab.position);
                sHabText.text = d < 1000f ? $"HAB {d:0} M" : $"HAB {d / 1000f:0.0} KM";
            }
        }

        // The Hab's entry (what the game's own Hab marker tracks), looked up by name now and then until found.
        internal static Transform? FindHab()
        {
            if (sHab != null) return sHab;
            if (Time.unscaledTime < sNextHabLook) return null;
            sNextHabLook = Time.unscaledTime + 5f;
            var go = GameObject.Find("PRF_Hab_Entry");
            sHab = go != null ? go.transform : null;
            return sHab;
        }

        static bool Build()
        {
            if (Time.unscaledTime < sNextLook) return false;
            sNextLook = Time.unscaledTime + 2f;
            if (sSpeed == null)
            {
                var controller = Object.FindObjectOfType<VelocityUIController>();
                sSpeed = controller != null ? sTextField?.GetValue(controller) as TextMeshProUGUI : null;
                if (controller != null && sDecimalField?.GetValue(controller) is int size) sDecimalSize = size;
                if (sSpeed == null)
                {
                    if (!sLogged) { sLogged = true; Plugin.Log.LogInfo("[Flight] the suit HUD's speed readout was not found yet; the ship speed and g readouts wait for it."); }
                    return false;
                }
            }
            var from = (RectTransform)sSpeed.transform;
            var canvas = sSpeed.canvas != null ? sSpeed.canvas.rootCanvas : null;
            if (canvas == null) return false;
            if (sPanel != null) Object.Destroy(sPanel.gameObject);
            // their own small block in the top left corner of the suit HUD (next to the speed readout they got clipped by
            // its panel and crossed its gauge lines)
            var go = new GameObject("TheExpanse_SuitShipPanel", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            sPanel = (RectTransform)go.transform;
            sPanel.anchorMin = sPanel.anchorMax = sPanel.pivot = new Vector2(0f, 1f);
            sPanel.anchoredPosition = new Vector2(40f, -40f);
            sPanel.sizeDelta = new Vector2(400f, 100f);
            sHabText = Copy("TheExpanse_SuitHab", from, 0f, 0.7f);
            sShipText = Copy("TheExpanse_SuitShipSpeed", from, -24f, 1f);
            sText = Copy("TheExpanse_SuitAccel", from, -54f, 0.7f);
            Plugin.Log.LogInfo($"[Flight] ship speed, acceleration and Hab distance placed in the top left corner of the suit HUD ('{canvas.name}', font from '{sSpeed.name}' {sSpeed.fontSize:0} pt).");
            return true;
        }

        // A copy of the speed text (its font and look), only the text itself (no theme or controller scripts, no children),
        // in the block at the given height from its top, right-aligned, scaled.
        static TextMeshProUGUI Copy(string name, RectTransform from, float y, float scale)
        {
            var copy = Object.Instantiate(from.gameObject, sPanel!, false);
            copy.name = name;
            foreach (Transform child in copy.transform) Object.Destroy(child.gameObject);
            foreach (var component in copy.GetComponents<Component>())
                if (!(component is RectTransform) && !(component is CanvasRenderer) && !(component is TextMeshProUGUI))
                    Object.Destroy(component);
            copy.AddComponent<LayoutElement>().ignoreLayout = true;
            var rect = (RectTransform)copy.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(400f, 30f);
            rect.anchoredPosition = new Vector2(0f, y);
            var text = copy.GetComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.TopLeft;
            text.fontSize = sSpeed!.fontSize * scale;
            text.enableAutoSizing = false;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            return text;
        }
    }
}
