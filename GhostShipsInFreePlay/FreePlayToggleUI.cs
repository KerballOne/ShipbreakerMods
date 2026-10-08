using BBI.Unity.Game;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GhostShipsInFreePlay
{
    // Runtime-built UGUI checkbox parented under the FreePlay LevelSelectController, so it inherits
    // that view's canvas, scaling and show/hide. The controller's own rect is only 100x100, so the
    // checkbox is positioned against the root canvas instead (bottom center, re-applied each frame
    // to follow resolution changes). Navigation is None: a pointer click won't steal EventSystem
    // selection from the level buttons, and controller users use the controller button.
    internal static class FreePlayToggleUI
    {
        private static readonly Color kBoxColor = new Color(0.08f, 0.08f, 0.08f, 0.85f);
        private static readonly Color kAccentColor = new Color(1f, 0.55f, 0.1f, 1f);
        private const float kBoxSize = 30f;
        private const float kCheckSize = 18f;
        private const float kLabelGap = 14f;
        private const float kHeight = 44f;

        private static Toggle? sToggle;
        private static LevelSelectController? sController;
        private static TextMeshProUGUI? sLabel;
        private static string? sKeyLabel;

        public static bool IsVisible => sToggle != null && sToggle.gameObject.activeInHierarchy;

        public static void Attach(LevelSelectController controller)
        {
            if (sToggle != null && sToggle.transform.parent == controller.transform) return;
            if (sToggle != null) Object.Destroy(sToggle.gameObject);
            sKeyLabel = null;

            var root = new GameObject("GhostShipToggle", typeof(RectTransform));
            var rootRect = (RectTransform)root.transform;
            rootRect.SetParent(controller.transform, false);
            rootRect.SetAsLastSibling();
            rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0f);
            rootRect.sizeDelta = new Vector2(360f, kHeight);

            var box = CreateChild("Box", rootRect, new Vector2(0f, 0.5f), new Vector2(kBoxSize / 2f, 0f), new Vector2(kBoxSize, kBoxSize));
            var boxImage = box.gameObject.AddComponent<Image>();
            boxImage.color = kBoxColor;
            var outline = box.gameObject.AddComponent<Outline>();
            outline.effectColor = kAccentColor;
            outline.effectDistance = new Vector2(2f, 2f);

            var check = CreateChild("Checkmark", box, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(kCheckSize, kCheckSize));
            var checkImage = check.gameObject.AddComponent<Image>();
            checkImage.color = kAccentColor;
            checkImage.raycastTarget = false;

            var labelRect = CreateChild("Label", rootRect, new Vector2(0f, 0.5f), new Vector2(kBoxSize + kLabelGap, 0f), new Vector2(320f, kHeight));
            labelRect.pivot = new Vector2(0f, 0.5f);
            var label = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
            var gameText = Traverse.Create(controller).Field("m_LocMissionNameField").GetValue<LocalizedTextMeshProUGUI>()?.TMProText;
            if (gameText != null)
            {
                label.font = gameText.font;
                label.fontSharedMaterial = gameText.fontSharedMaterial;
            }
            label.fontSize = 24f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.enableWordWrapping = false;

            var toggle = root.AddComponent<Toggle>();
            toggle.targetGraphic = boxImage;
            toggle.graphic = checkImage;
            toggle.navigation = new Navigation { mode = Navigation.Mode.None };
            toggle.isOn = Plugin.ForceGhostShip.Value;
            toggle.onValueChanged.AddListener(on =>
            {
                if (Plugin.ForceGhostShip.Value != on) Plugin.ForceGhostShip.Value = on;
            });

            sToggle = toggle;
            sLabel = label;
            sController = controller;
            Plugin.Debug($"Ghost Ship checkbox attached under '{controller.name}'.");
        }

        // Called every frame while visible: refreshes the key hint (keyboard vs controller) and
        // keeps the checkbox at the canvas's bottom center.
        public static void Refresh(string keyLabel)
        {
            if (sToggle == null || sLabel == null) return;
            if (sController != null) ShipListIndicators.Refresh(sController);
            var rootRect = (RectTransform)sToggle.transform;

            if (keyLabel != sKeyLabel)
            {
                sKeyLabel = keyLabel;
                sLabel.text = $"Ghost Ship  <size=70%><color=#999999>[{keyLabel}]</color></size>";
                var labelWidth = sLabel.GetPreferredValues(sLabel.text).x;
                ((RectTransform)sLabel.transform).sizeDelta = new Vector2(labelWidth, kHeight);
                rootRect.sizeDelta = new Vector2(kBoxSize + kLabelGap + labelWidth, kHeight);
            }

            var canvas = sToggle.GetComponentInParent<Canvas>()?.rootCanvas;
            if (canvas == null) return;
            var corners = new Vector3[4];
            ((RectTransform)canvas.transform).GetWorldCorners(corners);
            // corners: 0 = bottom-left, 3 = bottom-right.
            var bottomCenter = (corners[0] + corners[3]) * 0.5f;
            var offset = new Vector3(Plugin.ToggleOffsetX.Value, Plugin.ToggleOffsetY.Value, 0f) * canvas.transform.lossyScale.x;
            rootRect.position = bottomCenter + canvas.transform.rotation * offset;
        }

        static FreePlayToggleUI()
        {
            // Keeps the checkbox in sync when the hotkey (or a config manager) changes the value.
            Plugin.ForceGhostShip.SettingChanged += (_, _) =>
            {
                if (sToggle != null) sToggle.SetIsOnWithoutNotify(Plugin.ForceGhostShip.Value);
                Plugin.Log.LogInfo($"Ghost Ship {(Plugin.ForceGhostShip.Value ? "enabled" : "disabled")} for FreePlay.");
            };
        }

        private static RectTransform CreateChild(string name, RectTransform parent, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }
    }
}
