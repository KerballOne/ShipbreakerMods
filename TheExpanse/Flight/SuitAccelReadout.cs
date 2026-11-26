using BBI.Unity.Game;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheExpanse
{
    // On the suit's own HUD, out of the seat aboard a ship under way: above the game's speed readout (which is against
    // the bay) the suit's speed against the ship, and under it the pull felt aboard in g and m/s². Copies of the game's
    // speed text (same font, color and place on the HUD), so they fade and hide with the rest of the suit HUD.
    internal static class SuitAccelReadout
    {
        const float Gravity = 9.80665f;
        static readonly System.Reflection.FieldInfo? sTextField = AccessTools.Field(typeof(VelocityUIController), "m_VelocityTextField");
        static readonly System.Reflection.FieldInfo? sDecimalField = AccessTools.Field(typeof(VelocityUIController), "m_LabelDecimalFontSize");

        static TextMeshProUGUI? sSpeed, sText, sShipText;
        static int sDecimalSize = 12;
        static float sNextLook;
        static bool sLogged;

        internal static void Reset()
        {
            if (sText != null) Object.Destroy(sText.gameObject);
            if (sShipText != null) Object.Destroy(sShipText.gameObject);
            sText = sShipText = null;
            sSpeed = null;
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
                return;
            }
            if ((sText == null || sShipText == null) && !Build()) return;
            if (!sText!.gameObject.activeSelf) sText.gameObject.SetActive(true);
            if (!sShipText!.gameObject.activeSelf) sShipText.gameObject.SetActive(true);
            // the game's own format: the decimals and unit smaller
            float ship = (player!.velocity - FlightModel.PointVelocity(player.position)).magnitude;
            sShipText.text = ship.ToString("F2").Replace(".", $"<size={sDecimalSize}>.") + " M/S SHIP</size>";
            float accel = FlightModel.FeltAcceleration * Mathf.Clamp01(Plugin.FlightAboardPull.Value);
            sText.text = $"{accel / Gravity:0.00} G   {accel:0.0} M/S<sup>2</sup>";
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
            float line = from.rect.height;
            if (sText != null) Object.Destroy(sText.gameObject);
            if (sShipText != null) Object.Destroy(sShipText.gameObject);
            sShipText = Copy("TheExpanse_SuitShipSpeed", from, line * 0.9f + 2f, 1f);
            sText = Copy("TheExpanse_SuitAccel", from, -(line * 0.6f + 2f), 0.5f);
            Plugin.Log.LogInfo($"[Flight] ship speed and g readouts placed around the suit HUD's speed ('{sSpeed.name}', {sSpeed.fontSize:0} pt, line {line:0} px, under '{sSpeed.transform.parent.name}').");
            return true;
        }

        // A copy of the speed text, only the text itself (no theme or controller scripts, no children), raised (or lowered)
        // by the given amount and scaled.
        static TextMeshProUGUI Copy(string name, RectTransform from, float raise, float scale)
        {
            var copy = Object.Instantiate(from.gameObject, from.parent, false);
            copy.name = name;
            foreach (Transform child in copy.transform) Object.Destroy(child.gameObject);
            foreach (var component in copy.GetComponents<Component>())
                if (!(component is RectTransform) && !(component is CanvasRenderer) && !(component is TextMeshProUGUI))
                    Object.Destroy(component);
            copy.AddComponent<LayoutElement>().ignoreLayout = true;
            var rect = (RectTransform)copy.transform;
            rect.anchorMin = from.anchorMin;
            rect.anchorMax = from.anchorMax;
            rect.pivot = from.pivot;
            rect.sizeDelta = from.sizeDelta;
            rect.anchoredPosition = from.anchoredPosition + new Vector2(0f, raise);
            var text = copy.GetComponent<TextMeshProUGUI>();
            text.fontSize = sSpeed!.fontSize * scale;
            text.enableAutoSizing = false;
            text.raycastTarget = false;
            return text;
        }
    }
}
