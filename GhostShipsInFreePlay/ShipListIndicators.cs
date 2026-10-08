using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace GhostShipsInFreePlay
{
    // Tags ship names in the FreePlay list when the checkbox is on and the ship has a career ghost
    // variant. The name is a LocalizedTextMeshProUGUI that the game (re)writes asynchronously --
    // after LoadStartingShip completes, and again on RefreshText -- so instead of tagging once, the
    // suffix is reconciled every frame while the screen is open (~20 buttons, a string EndsWith each).
    internal static class ShipListIndicators
    {
        private const string kSpacer = "  ";
        private static readonly HashSet<int> sLogged = new HashSet<int>();

        public static void Refresh(LevelSelectController controller)
        {
            var buttons = Traverse.Create(controller).Field("mLevelSelectButtonList").GetValue<List<GameObject>>();
            if (buttons == null) return;

            // BepInEx trims config values, so the leading spacing lives here rather than in the tag.
            var suffix = string.IsNullOrEmpty(Plugin.IndicatorText.Value) ? "" : kSpacer + Plugin.IndicatorText.Value;
            var show = Plugin.ForceGhostShip.Value && GhostShipCatalog.IsResolved && !string.IsNullOrEmpty(suffix);
            foreach (var go in buttons)
            {
                if (go == null) continue;
                var button = go.GetComponentInChildren<LevelSelectButton>();
                if (button == null) continue;
                var nameText = Traverse.Create(button).Field("m_ShipTypeName").GetValue<LocalizedTextMeshProUGUI>()?.TMProText;
                if (nameText == null) continue;

                var text = nameText.text ?? "";
                var tagged = !string.IsNullOrEmpty(suffix) && text.EndsWith(suffix);
                var level = Traverse.Create(button).Field("mLevelToLoad").GetValue<LevelAsset.LevelData>();
                var want = show && GhostShipCatalog.HasVariant(level.StartingShipRef?.AssetGUID);

                if (want && !tagged)
                {
                    nameText.text = text + suffix;
                    if (sLogged.Add(button.GetInstanceID()))
                        Plugin.Debug($"Ghost variant available: '{text}' ({level.StartingShipRef?.AssetGUID})");
                }
                else if (!want && tagged)
                {
                    nameText.text = text.Substring(0, text.Length - suffix.Length);
                }
            }
        }
    }
}
