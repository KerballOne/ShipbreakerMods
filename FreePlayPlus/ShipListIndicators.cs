using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace FreePlayPlus
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
                CustomLevelFix.Apply(button, ref level);
                ShipSaveLabels.Refresh(button, level);
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

    // Custom FreePlay levels (ship mods mark them with a LevelDescriptionFull starting "CUSTOM"): the
    // ship mod normally renames their cards to the level's own name/role and gives the level the
    // standard FreePlay starting upgrades (FreeMode_UpgradeListAsset) when it has none - but only when
    // its count of list-loading events lines up with the number of buttons, which it doesn't always.
    // When it misses, the card shows the ship class ("Javelin / Survey") and the level starts with no
    // FreePlay upgrades (e.g. demo charges stay locked). Same result, applied whenever it's missing;
    // a no-op when the ship mod already did it.
    internal static class CustomLevelFix
    {
        private static UpgradeListAsset? sFreeModeUpgrades;
        private static readonly HashSet<int> sLogged = new HashSet<int>();

        internal static void Apply(LevelSelectButton button, ref LevelAsset.LevelData level)
        {
            if (level.LevelDescriptionFull == null || !level.LevelDescriptionFull.StartsWith("CUSTOM")) return;

            foreach (var text in button.GetComponentsInChildren<LocalizedTextMeshProUGUI>())
            {
                if (text == null || text.TMProText == null) continue;
                var want = text.name == "Ship name" ? level.LevelDisplayName : text.name == "Ship role" ? level.LevelDescriptionShort : null;
                if (string.IsNullOrEmpty(want)) continue;
                var current = text.TMProText.text ?? "";
                var tag = Plugin.IndicatorText.Value;
                if (!string.IsNullOrEmpty(tag)) current = current.Replace("  " + tag, "");
                if (current != want) text.ChangeFieldByStringContent(want);
            }

            if (level.StartingUpgrades == null)
            {
                sFreeModeUpgrades ??= FindFreeModeUpgrades();
                if (sFreeModeUpgrades != null)
                {
                    level.StartingUpgrades = sFreeModeUpgrades;
                    Traverse.Create(button).Field("mLevelToLoad").SetValue(level);
                    if (sLogged.Add(button.GetInstanceID()))
                        Plugin.Debug($"Custom level '{level.LevelDisplayName}': gave it the FreePlay starting upgrades.");
                }
            }
        }

        private static UpgradeListAsset? FindFreeModeUpgrades()
        {
            foreach (var asset in Resources.FindObjectsOfTypeAll<UpgradeListAsset>())
                if (asset != null && asset.name == "FreeMode_UpgradeListAsset") return asset;
            return null;
        }
    }
}
