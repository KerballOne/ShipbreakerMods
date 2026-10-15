using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BBI.Unity.Game;
using BBI.Unity.Game.UI.Config.Controllers;
using BepInEx.Configuration;
using Doozy.Engine.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FreePlayPlus
{
    // A FREEPLAY+ tab in the game's own Options menu (title screen and pause menu). Built from clones
    // of the stock Video page, toggle/slider rows and Audio tab button, appended to
    // UnifiedOptionsScreen's tab arrays so stock tab switching, the unsaved-changes prompt and
    // Save/Reset all work on it. Cloned rows carry an OptionBinding; the row patches below only take
    // over rows that have one, so stock rows (and other mods' rows) are left alone.
    internal static class OptionsTab
    {
        private const string kTabLabel = "FREEPLAY+";
        private const string kNextShip = " (next ship)";

        internal sealed class Setting
        {
            public readonly string Heading;
            public readonly string Label;
            public readonly ConfigEntryBase Entry;
            public readonly bool IsSlider;
            public readonly float Min, Max, ControllerStep;

            public Setting(string heading, string label, ConfigEntryBase entry, bool isSlider = false, float min = 0f, float max = 1f, float controllerStep = 1f)
            {
                Heading = heading; Label = label; Entry = entry; IsSlider = isSlider; Min = min; Max = max; ControllerStep = controllerStep;
            }
        }

        // Settings that are only read when a ship spawns are labelled "(next ship)": ghost ship
        // settings (level load), HealthDamage (god mode removed at spawn), demo charges
        // (DemoChargeController.Start) and tethers (TetherController.Initialize).
        private static Setting[]? sSettings;
        internal static Setting[] Settings => sSettings ??= new[]
        {
            new Setting("GHOST SHIPS", "Ghost Ship" + kNextShip, Plugin.ForceGhostShip),
            new Setting("GHOST SHIPS", "Include Career-Only Modules" + kNextShip, Plugin.IncludeCareerOnlyModules),
            new Setting("SURVIVAL", "Health Damage" + kNextShip, Plugin.HealthDamage),
            new Setting("SURVIVAL", "Exit to Menu on Death", Plugin.ExitToMenuOnDeath),
            new Setting("SURVIVAL", "Consume Oxygen", Plugin.ConsumeOxygen),
            new Setting("SURVIVAL", "Consume Fuel", Plugin.ConsumeFuel),
            new Setting("SURVIVAL", "Consume Demo Charges" + kNextShip, Plugin.ConsumeDemoCharges),
            new Setting("SURVIVAL", "Consume Tethers" + kNextShip, Plugin.ConsumeTethers),
            new Setting("SURVIVAL", "No Credit", Plugin.NoCredit),
            new Setting("SURVIVAL", "Kiosk Refill Seconds", Plugin.RefillSeconds, true, 0f, 60f, 1f),
            new Setting("SAVES", "Autosave Every N Percent", Plugin.AutosavePoints, true, 0f, 100f, 5f),
            new Setting("SAVES", "Autosaves Kept", Plugin.Autosaves, true, 0f, 12f, 1f),
        };

        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, OptionsScreenController[]> sScreens =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, OptionsScreenController[]>("m_OptionScreens");
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, UIView[]> sViews =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, UIView[]>("m_OptionScreenUIViews");
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, GenericTab[]> sTabs =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, GenericTab[]>("m_OptionTabs");
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, int> sActiveIndex =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, int>("mActiveOptionScreenIndex");
        private static readonly AccessTools.FieldRef<OptionsScreenController, GameObject> sOptionsContainer =
            AccessTools.FieldRefAccess<OptionsScreenController, GameObject>("m_OptionsContainer");
        private static readonly AccessTools.FieldRef<OptionEntry, TextMeshProUGUI> sOptionName =
            AccessTools.FieldRefAccess<OptionEntry, TextMeshProUGUI>("m_OptionNameText");
        private static readonly AccessTools.FieldRef<OptionEntry, bool> sRequiresRestart =
            AccessTools.FieldRefAccess<OptionEntry, bool>("m_RequiresRestart");
        private static readonly AccessTools.FieldRef<OptionEntry, bool> sTitleScreenOnly =
            AccessTools.FieldRefAccess<OptionEntry, bool>("m_TitleScreenOnly");
        private static readonly AccessTools.FieldRef<OptionEntry, bool> sDesktopOnly =
            AccessTools.FieldRefAccess<OptionEntry, bool>("m_DesktopOnly");
        private static readonly AccessTools.FieldRef<OptionEntry, bool> sIsTopItem =
            AccessTools.FieldRefAccess<OptionEntry, bool>("m_IsTopItem");
        private static readonly AccessTools.FieldRef<OptionEntry, UIButton> sBackgroundButton =
            AccessTools.FieldRefAccess<OptionEntry, UIButton>("m_BackgroundButton");
        private static readonly AccessTools.FieldRef<GenericTab, TextMeshProUGUI> sTabText =
            AccessTools.FieldRefAccess<GenericTab, TextMeshProUGUI>("m_TabText");
        private static readonly AccessTools.FieldRef<GenericTab, int> sTabIndex =
            AccessTools.FieldRefAccess<GenericTab, int>("m_TabIndex");
        internal static readonly AccessTools.FieldRef<OptionEntrySlider, LynxSlider> SliderControl =
            AccessTools.FieldRefAccess<OptionEntrySlider, LynxSlider>("m_Slider");
        internal static readonly AccessTools.FieldRef<OptionEntrySlider, TextMeshProUGUI> SliderValueText =
            AccessTools.FieldRefAccess<OptionEntrySlider, TextMeshProUGUI>("m_ValueText");
        private static readonly AccessTools.FieldRef<OptionEntrySlider, int> sSliderDecimals =
            AccessTools.FieldRefAccess<OptionEntrySlider, int>("m_ValueTextDecimalPlaces");
        private static readonly AccessTools.FieldRef<OptionEntrySlider, float> sSliderScalar =
            AccessTools.FieldRefAccess<OptionEntrySlider, float>("m_ValueTextScalar");
        private static readonly AccessTools.FieldRef<OptionEntrySlider, float> sSliderStep =
            AccessTools.FieldRefAccess<OptionEntrySlider, float>("m_ControllerSliderStep");

        private static int OurIndex(UnifiedOptionsScreen screen) =>
            Array.FindIndex(sScreens(screen) ?? new OptionsScreenController[0], s => s is FreePlayOptionsScreen);

        // Called from every UnifiedOptionsScreen entry point; a no-op once the tab exists.
        internal static void TryInstall(UnifiedOptionsScreen screen)
        {
            try { Install(screen); }
            catch (Exception ex) { Plugin.Log.LogError($"Could not add the {kTabLabel} options tab: {ex}"); }
        }

        private static void Install(UnifiedOptionsScreen unified)
        {
            if (unified == null || OurIndex(unified) >= 0) return;
            var screens = sScreens(unified);
            var views = sViews(unified);
            var tabs = sTabs(unified);
            if (screens == null || views == null || tabs == null || screens.Length != views.Length || screens.Length != tabs.Length)
                throw new InvalidOperationException("The options tab arrays have an unexpected layout.");
            var video = Array.FindIndex(screens, s => s is VideoOptionsScreenController);
            var audio = Array.FindIndex(screens, s => s is AudioOptionsScreenController);
            if (video < 0 || audio < 0) throw new InvalidOperationException("Could not find the Video and Audio options screens.");

            var boolTemplate = FindTemplate<OptionEntryBool>(screens, video);
            var sliderTemplate = FindTemplate<OptionEntrySlider>(screens, video);
            if (boolTemplate == null || sliderTemplate == null) throw new InvalidOperationException("Could not find the stock toggle and slider rows.");

            // Clones are built under an inactive object so their Awake runs only once configured.
            var staging = new GameObject("FreePlayPlus Options Staging");
            staging.SetActive(false);
            staging.transform.SetParent(unified.transform, false);
            GameObject? viewObject = null, tabObject = null;
            var done = false;
            try
            {
                var tabIndex = screens.Length;

                viewObject = Object.Instantiate(views[video].gameObject, staging.transform, false);
                viewObject.name = "FreePlayPlus Options View";
                viewObject.SetActive(false);
                var view = viewObject.GetComponent<UIView>();
                view.ViewName = $"FreePlayPlus Options {unified.GetInstanceID()}";
                view.Visibility = VisibilityState.NotVisible;
                view.BehaviorAtStart = UIViewStartBehavior.DoNothing;

                var stockController = viewObject.GetComponent<OptionsScreenController>();
                var content = sOptionsContainer(stockController).transform;
                var headingTemplate = Object.Instantiate(FindHeadingTemplate(content), staging.transform, false);
                foreach (var c in viewObject.GetComponents<OptionsScreenController>()) Object.DestroyImmediate(c);
                foreach (var c in viewObject.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name == "ConditionalUIConfiguratorContainer") Object.DestroyImmediate(c);
                for (var i = content.childCount - 1; i >= 0; i--) Object.DestroyImmediate(content.GetChild(i).gameObject);

                var controller = viewObject.AddComponent<FreePlayOptionsScreen>();
                var entries = CreateRows(content, boolTemplate, sliderTemplate, headingTemplate);
                controller.Entries = entries;
                var firstButton = entries.Length > 0 ? sBackgroundButton(entries[0]) : null;
                view.SelectedButton = firstButton != null ? firstButton.gameObject : null;

                tabObject = Object.Instantiate(tabs[audio].gameObject, staging.transform, false);
                tabObject.name = "FreePlayPlus Options Tab";
                var tab = tabObject.GetComponent<GenericTab>();
                ConfigureTab(tab, unified, tabIndex);

                viewObject.transform.SetParent(views[video].transform.parent, false);
                viewObject.transform.SetSiblingIndex(views[views.Length - 1].transform.GetSiblingIndex() + 1);
                tabObject.transform.SetParent(tabs[audio].transform.parent, false);
                tabObject.transform.SetSiblingIndex(tabs[tabs.Length - 1].transform.GetSiblingIndex() + 1);
                ResizeTabs(tabs.Append(tab).ToArray());

                sScreens(unified) = screens.Append(controller).ToArray();
                sViews(unified) = views.Append(view).ToArray();
                sTabs(unified) = tabs.Append(tab).ToArray();
                done = true;
                Plugin.Log.LogInfo($"Added the {kTabLabel} options tab with {entries.Length} settings.");
            }
            finally
            {
                if (!done)
                {
                    if (viewObject != null) Object.Destroy(viewObject);
                    if (tabObject != null) Object.Destroy(tabObject);
                }
                Object.Destroy(staging);
            }
        }

        private static T? FindTemplate<T>(OptionsScreenController[] screens, int preferred) where T : Component =>
            screens[preferred].GetComponentInChildren<T>(true) ??
            screens.Select(s => s.GetComponentInChildren<T>(true)).FirstOrDefault(t => t != null);

        // A content child with text but no option row: the stock section headings ("QUALITY" preferred).
        private static GameObject FindHeadingTemplate(Transform content)
        {
            GameObject? first = null;
            for (var i = 0; i < content.childCount; i++)
            {
                var child = content.GetChild(i).gameObject;
                if (child.GetComponentInChildren<OptionEntry>(true) != null) continue;
                var text = child.GetComponentInChildren<TextMeshProUGUI>(true);
                if (text == null) continue;
                first ??= child;
                if (string.Equals(text.text?.Trim(), "QUALITY", StringComparison.OrdinalIgnoreCase)) return child;
            }
            return first ?? throw new InvalidOperationException("Could not find a stock section heading.");
        }

        private static OptionEntry[] CreateRows(Transform content, OptionEntryBool boolTemplate, OptionEntrySlider sliderTemplate, GameObject headingTemplate)
        {
            var entries = new List<OptionEntry>();
            string? heading = null;
            for (var i = 0; i < Settings.Length; i++)
            {
                var setting = Settings[i];
                if (setting.Heading != heading)
                {
                    heading = setting.Heading;
                    CreateHeading(content, headingTemplate, heading);
                }

                var row = Object.Instantiate(setting.IsSlider ? sliderTemplate.gameObject : boolTemplate.gameObject, content, false);
                row.name = "FreePlayPlus " + setting.Entry.Definition.Key;
                OptionEntry entry = setting.IsSlider ? row.GetComponent<OptionEntrySlider>() : row.GetComponent<OptionEntryBool>();
                row.AddComponent<OptionBinding>().Index = i;

                var name = sOptionName(entry);
                if (name != null)
                {
                    var loc = name.GetComponent<LocalizedTextMeshProUGUI>();
                    if (loc != null) loc.enabled = false;
                    name.text = setting.Label.ToUpperInvariant();
                }
                sRequiresRestart(entry) = false;
                sTitleScreenOnly(entry) = false;
                sDesktopOnly(entry) = false;
                sIsTopItem(entry) = entries.Count == 0;

                if (entry is OptionEntrySlider slider)
                {
                    sSliderDecimals(slider) = 0;
                    sSliderScalar(slider) = 1f;
                    sSliderStep(slider) = setting.ControllerStep;
                    var control = SliderControl(slider);
                    if (control != null) control.wholeNumbers = true;
                }
                entries.Add(entry);
            }
            LinkNavigation(entries);
            return entries.ToArray();
        }

        private static void CreateHeading(Transform content, GameObject template, string text)
        {
            var heading = Object.Instantiate(template, content, false);
            heading.name = text + " Heading";
            foreach (var loc in heading.GetComponentsInChildren<LocalizedTextMeshProUGUI>(true)) loc.enabled = false;
            foreach (var tmp in heading.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                tmp.text = text;
                tmp.raycastTarget = false;
            }
        }

        // Explicit up/down between our rows, wrapping at both ends.
        private static void LinkNavigation(List<OptionEntry> entries)
        {
            var buttons = entries.Select(e => sBackgroundButton(e)?.Button).ToArray();
            if (buttons.Length == 0 || buttons.Any(b => b == null)) return;
            for (var i = 0; i < buttons.Length; i++)
            {
                var nav = buttons[i]!.navigation;
                nav.mode = Navigation.Mode.Explicit;
                nav.selectOnUp = buttons[(i + buttons.Length - 1) % buttons.Length];
                nav.selectOnDown = buttons[(i + 1) % buttons.Length];
                nav.selectOnLeft = null;
                nav.selectOnRight = null;
                buttons[i]!.navigation = nav;
            }
        }

        private static void ConfigureTab(GenericTab tab, UnifiedOptionsScreen unified, int tabIndex)
        {
            sTabIndex(tab) = tabIndex;
            var text = sTabText(tab);
            if (text != null)
            {
                var loc = text.GetComponent<LocalizedTextMeshProUGUI>();
                if (loc != null) loc.enabled = false;
                text.text = kTabLabel;
            }
            var click = tab.GetComponent<UIButton>()?.OnClick?.OnTrigger?.Event
                ?? throw new InvalidOperationException("The cloned Audio tab has no click event.");
            // The clone still calls ShowTab(<Audio's index>); swap that for ours.
            for (var i = 0; i < click.GetPersistentEventCount(); i++)
                if (click.GetPersistentTarget(i) == unified && click.GetPersistentMethodName(i) == nameof(UnifiedOptionsScreen.ShowTab))
                    click.SetPersistentListenerState(i, UnityEventCallState.Off);
            click.AddListener(() => unified.ShowTab(tabIndex));
        }

        // Shares the width the existing tabs had between all of them, ours included.
        private static void ResizeTabs(GenericTab[] tabs)
        {
            var total = 0f;
            for (var i = 0; i < tabs.Length - 1; i++) total += ((RectTransform)tabs[i].transform).rect.width;
            var layout = tabs[0].GetComponentInParent<HorizontalLayoutGroup>();
            var spacing = layout != null ? layout.spacing : 0f;
            var width = Math.Max(100f, (total - spacing) / tabs.Length);
            foreach (var tab in tabs)
            {
                ((RectTransform)tab.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                var element = tab.GetComponent<LayoutElement>();
                if (element != null)
                {
                    element.preferredWidth = width;
                    element.flexibleWidth = 1f;
                }
            }
        }

        // Our cloned view isn't in the stock views' show/hide group, so switching to or from it has
        // to hide the outgoing page explicitly.
        internal static void HideOutgoingView(UnifiedOptionsScreen unified, int target)
        {
            var ours = OurIndex(unified);
            var active = sActiveIndex(unified);
            if (ours < 0 || active == target || (active != ours && target != ours)) return;
            var views = sViews(unified);
            if (active >= 0 && active < views.Length && views[active] != null) views[active].Hide();
        }

        internal static void HideOutgoingViewStep(UnifiedOptionsScreen unified, int direction)
        {
            var count = sScreens(unified)?.Length ?? 0;
            if (count == 0) return;
            HideOutgoingView(unified, ((sActiveIndex(unified) + direction) % count + count) % count);
        }

        // LB/RB step through m_OptionTabs in array order, but a mod may place its button elsewhere in
        // the bar (ShipbreakerTweaks always puts its button right after Audio, even when it was
        // appended to the array after another mod's tab). Reorders the tab buttons to match the array,
        // a no-op when they already do; any mod doing the same pass gets the same result, so it
        // doesn't depend on load order. Run from postfixes, after every mod's install prefix.
        internal static void TrySyncTabOrder(UnifiedOptionsScreen unified)
        {
            try { SyncTabOrder(unified); }
            catch (Exception ex) { Plugin.Log.LogError($"Could not order the {kTabLabel} tab: {ex}"); }
        }

        private static void SyncTabOrder(UnifiedOptionsScreen unified)
        {
            if (OurIndex(unified) < 0) return;
            var tabs = sTabs(unified).Select(t => t != null ? t.transform : null).ToArray();
            if (tabs.Length == 0 || tabs.Any(t => t == null || t.parent != tabs[0]!.parent)) return;
            var first = tabs.Min(t => t!.GetSiblingIndex());
            // Only when the buttons sit next to each other, so nothing else in the bar gets moved.
            if (tabs.Max(t => t!.GetSiblingIndex()) - first != tabs.Length - 1) return;
            for (var i = 0; i < tabs.Length; i++)
                if (tabs[i]!.GetSiblingIndex() != first + i) tabs[i]!.SetSiblingIndex(first + i);
        }

        internal static void OnOptionsClosed(UnifiedOptionsScreen unified)
        {
            if (OurIndex(unified) >= 0) EditSession.Close();
        }

        // ---- values ----

        internal static float GetFloat(int index) => Convert.ToSingle(Settings[index].Entry.BoxedValue, CultureInfo.InvariantCulture);
        internal static bool GetBool(int index) => (bool)Settings[index].Entry.BoxedValue;

        internal static void SetFloat(int index, float value)
        {
            var setting = Settings[index];
            value = Mathf.Clamp(value, setting.Min, setting.Max);
            EditSession.Set(index, setting.Entry.SettingType == typeof(int) ? (object)Mathf.RoundToInt(value) : Mathf.Round(value));
        }

        internal static void SetBool(int index, bool value) => EditSession.Set(index, value);
    }

    // Changes made on the tab apply to the ConfigEntries right away (so FreePlayPlus reacts live),
    // but the .cfg is only written on Save. Leaving the menu without saving restores the values the
    // tab opened with.
    internal static class EditSession
    {
        private static object[]? sSnapshot;
        private static bool sSaveOnSetBefore;

        internal static bool IsDirty { get; private set; }

        internal static void Begin()
        {
            if (sSnapshot != null) return;
            sSnapshot = OptionsTab.Settings.Select(s => s.Entry.BoxedValue).ToArray();
            sSaveOnSetBefore = Plugin.Instance.Config.SaveOnConfigSet;
            Plugin.Instance.Config.SaveOnConfigSet = false;
            IsDirty = false;
        }

        internal static void Set(int index, object value)
        {
            Begin();
            OptionsTab.Settings[index].Entry.BoxedValue = value;
            IsDirty = true;
        }

        internal static void ResetToDefaults()
        {
            Begin();
            foreach (var s in OptionsTab.Settings) s.Entry.BoxedValue = s.Entry.DefaultValue;
            IsDirty = true;
        }

        internal static void Commit()
        {
            if (sSnapshot == null) return;
            try { Plugin.Instance.Config.Save(); }
            catch (Exception ex) { Plugin.Log.LogError($"Could not save FreePlayPlus settings: {ex}"); return; }
            End();
        }

        internal static void Rollback()
        {
            if (sSnapshot == null) return;
            var snapshot = sSnapshot;
            try
            {
                for (var i = 0; i < snapshot.Length; i++)
                    if (!Equals(OptionsTab.Settings[i].Entry.BoxedValue, snapshot[i])) OptionsTab.Settings[i].Entry.BoxedValue = snapshot[i];
            }
            finally { End(); }
        }

        internal static void Close()
        {
            if (IsDirty) Rollback();
            else End();
        }

        private static void End()
        {
            if (sSnapshot == null) return;
            Plugin.Instance.Config.SaveOnConfigSet = sSaveOnSetBefore;
            sSnapshot = null;
            IsDirty = false;
        }
    }

    internal sealed class OptionBinding : MonoBehaviour
    {
        internal int Index;
        internal bool Refreshing;
    }

    internal sealed class FreePlayOptionsScreen : OptionsScreenController
    {
        internal OptionEntry[] Entries = new OptionEntry[0];

        public override bool OptionsAreDirty => EditSession.IsDirty;

        // The base Awake reads m_OptionsContainer, which this added component doesn't have.
        protected override void Awake() { }

        protected override void OnEnable() => EditSession.Begin();

        public override void Refresh() => EditSession.Begin();

        public override void ResetOptionsToDefaults()
        {
            EditSession.ResetToDefaults();
            RefreshEntries();
        }

        public override void CancelAndReloadOptions()
        {
            EditSession.Rollback();
            RefreshEntries();
        }

        public override void SaveAndApplyOptions()
        {
            EditSession.Commit();
            RefreshEntries();
        }

        private void RefreshEntries()
        {
            foreach (var entry in Entries)
                if (entry != null) entry.RefreshOptionData();
        }
    }

    // ---- UnifiedOptionsScreen hooks: install the tab, hide our page on tab switches, end the edit session on close ----

    [HarmonyPatch(typeof(UnifiedOptionsScreen), "OnEnable")]
    internal static class UnifiedOptionsScreen_OnEnable_Patch
    {
        private static void Prefix(UnifiedOptionsScreen __instance) => OptionsTab.TryInstall(__instance);
        private static void Postfix(UnifiedOptionsScreen __instance) => OptionsTab.TrySyncTabOrder(__instance);
    }

    [HarmonyPatch(typeof(UnifiedOptionsScreen), nameof(UnifiedOptionsScreen.ShowCorrectTabOnStart))]
    internal static class UnifiedOptionsScreen_ShowCorrectTabOnStart_Patch
    {
        private static void Prefix(UnifiedOptionsScreen __instance) => OptionsTab.TryInstall(__instance);
        private static void Postfix(UnifiedOptionsScreen __instance) => OptionsTab.TrySyncTabOrder(__instance);
    }

    [HarmonyPatch(typeof(UnifiedOptionsScreen), nameof(UnifiedOptionsScreen.ShowTab))]
    internal static class UnifiedOptionsScreen_ShowTab_Patch
    {
        private static void Prefix(UnifiedOptionsScreen __instance, int index)
        {
            OptionsTab.TryInstall(__instance);
            try { OptionsTab.HideOutgoingView(__instance, index); }
            catch (Exception ex) { Plugin.Log.LogError($"FREEPLAY+ tab switch: {ex}"); }
        }
        private static void Postfix(UnifiedOptionsScreen __instance) => OptionsTab.TrySyncTabOrder(__instance);
    }

    [HarmonyPatch(typeof(UnifiedOptionsScreen), nameof(UnifiedOptionsScreen.NextOptionTab))]
    internal static class UnifiedOptionsScreen_NextOptionTab_Patch
    {
        private static void Prefix(UnifiedOptionsScreen __instance)
        {
            OptionsTab.TryInstall(__instance);
            try { OptionsTab.HideOutgoingViewStep(__instance, 1); }
            catch (Exception ex) { Plugin.Log.LogError($"FREEPLAY+ tab switch: {ex}"); }
        }
        private static void Postfix(UnifiedOptionsScreen __instance) => OptionsTab.TrySyncTabOrder(__instance);
    }

    [HarmonyPatch(typeof(UnifiedOptionsScreen), nameof(UnifiedOptionsScreen.PreviousOptionTab))]
    internal static class UnifiedOptionsScreen_PreviousOptionTab_Patch
    {
        private static void Prefix(UnifiedOptionsScreen __instance)
        {
            OptionsTab.TryInstall(__instance);
            try { OptionsTab.HideOutgoingViewStep(__instance, -1); }
            catch (Exception ex) { Plugin.Log.LogError($"FREEPLAY+ tab switch: {ex}"); }
        }
        private static void Postfix(UnifiedOptionsScreen __instance) => OptionsTab.TrySyncTabOrder(__instance);
    }

    [HarmonyPatch(typeof(UnifiedOptionsScreen), "OnDisable")]
    internal static class UnifiedOptionsScreen_OnDisable_Patch
    {
        private static void Postfix(UnifiedOptionsScreen __instance)
        {
            try { OptionsTab.OnOptionsClosed(__instance); }
            catch (Exception ex) { Plugin.Log.LogError($"Could not close the FREEPLAY+ settings cleanly: {ex}"); }
        }
    }

    // ---- row hooks: only rows carrying an OptionBinding are ours; everything else runs stock ----

    [HarmonyPatch(typeof(OptionEntryBool), nameof(OptionEntryBool.RefreshOptionData))]
    internal static class OptionEntryBool_RefreshOptionData_Patch
    {
        private static readonly AccessTools.FieldRef<OptionEntryBool, LocalizedTextMeshProUGUI> sLocValueText =
            AccessTools.FieldRefAccess<OptionEntryBool, LocalizedTextMeshProUGUI>("m_LocEnabledDisabledText");
        private static readonly AccessTools.FieldRef<OptionEntryBool, TextMeshProUGUI> sValueText =
            AccessTools.FieldRefAccess<OptionEntryBool, TextMeshProUGUI>("m_EnabledDisabledText");
        private static readonly AccessTools.FieldRef<OptionEntryBool, Image> sToggleFill =
            AccessTools.FieldRefAccess<OptionEntryBool, Image>("m_ToggleFill");
        private const int kLocEnabled = 481, kLocDisabled = 482;

        private static bool Prefix(OptionEntryBool __instance)
        {
            var binding = __instance.GetComponent<OptionBinding>();
            if (binding == null) return true;
            var on = OptionsTab.GetBool(binding.Index);
            var loc = sLocValueText(__instance);
            if (loc != null) loc.ChangeFieldByIntID(on ? kLocEnabled : kLocDisabled);
            else if (sValueText(__instance) != null) sValueText(__instance).text = on ? "ENABLED" : "DISABLED";
            var fill = sToggleFill(__instance);
            if (fill != null) fill.gameObject.SetActive(on);
            return false;
        }
    }

    [HarmonyPatch(typeof(OptionEntryBool), "ToggleOption")]
    internal static class OptionEntryBool_ToggleOption_Patch
    {
        private static bool Prefix(OptionEntryBool __instance)
        {
            var binding = __instance.GetComponent<OptionBinding>();
            if (binding == null) return true;
            OptionsTab.SetBool(binding.Index, !OptionsTab.GetBool(binding.Index));
            __instance.RefreshOptionData();
            __instance.OnDataChanged?.Invoke(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(OptionEntrySlider), nameof(OptionEntrySlider.RefreshOptionData))]
    internal static class OptionEntrySlider_RefreshOptionData_Patch
    {
        private static bool Prefix(OptionEntrySlider __instance)
        {
            var binding = __instance.GetComponent<OptionBinding>();
            if (binding == null) return true;
            var setting = OptionsTab.Settings[binding.Index];
            var value = OptionsTab.GetFloat(binding.Index);
            binding.Refreshing = true;
            try
            {
                var slider = OptionsTab.SliderControl(__instance);
                if (slider != null)
                {
                    slider.minValue = setting.Min;
                    slider.maxValue = setting.Max;
                    slider.value = value;
                }
                var text = OptionsTab.SliderValueText(__instance);
                if (text != null) text.text = value.ToString("F0", CultureInfo.InvariantCulture);
            }
            finally { binding.Refreshing = false; }
            return false;
        }
    }

    [HarmonyPatch(typeof(OptionEntrySlider), "SliderValueChanged")]
    internal static class OptionEntrySlider_SliderValueChanged_Patch
    {
        private static bool Prefix(OptionEntrySlider __instance, float newValue)
        {
            var binding = __instance.GetComponent<OptionBinding>();
            if (binding == null) return true;
            if (!binding.Refreshing)
            {
                OptionsTab.SetFloat(binding.Index, newValue);
                __instance.RefreshOptionData();
                __instance.OnDataChanged?.Invoke(__instance);
            }
            return false;
        }
    }
}
