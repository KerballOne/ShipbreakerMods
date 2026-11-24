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

namespace TheExpanse
{
    // An EXPANSE tab in the game's own Options menu (title screen and pause menu). Built from clones
    // of the stock Video page, toggle/slider rows and Audio tab button, appended to
    // UnifiedOptionsScreen's tab arrays so stock tab switching, the unsaved-changes prompt and
    // Save/Reset all work on it. Cloned rows carry an OptionBinding; the row patches below only take
    // over rows that have one, so stock rows (and other mods' rows) are left alone.
    internal static class OptionsTab
    {
        private const string kTabLabel = "EXPANSE";

        internal sealed class Setting
        {
            public readonly string Heading;
            public readonly string Label;
            public readonly ConfigEntryBase Entry;
            public readonly bool IsSlider;
            public readonly float Min, Max, ControllerStep;
            // A slider with an OFF stop one step past Max: there it turns this toggle off (and on again below it).
            public ConfigEntry<bool>? OffSwitch;
            // A toggle that switches these on and off together with Entry (on when any of them is).
            public ConfigEntry<bool>[] AlsoSwitches = new ConfigEntry<bool>[0];
            // Grayed out, and left alone, while this is false.
            public Func<bool>? ActiveWhen;
            // Shown instead of 0.
            public string? ZeroLabel;

            public Setting(string heading, string label, ConfigEntryBase entry, bool isSlider = false, float min = 0f, float max = 1f, float controllerStep = 1f)
            {
                Heading = heading; Label = label; Entry = entry; IsSlider = isSlider; Min = min; Max = max; ControllerStep = controllerStep;
            }

            public float SliderMax => OffSwitch != null ? Max + ControllerStep : Max;

            public IEnumerable<ConfigEntryBase> Entries
            {
                get
                {
                    yield return Entry;
                    if (OffSwitch != null) yield return OffSwitch;
                    foreach (var e in AlsoSwitches) yield return e;
                }
            }
        }

        // A few switches people reach for most; everything else is in the settings file, which is re-read as soon as it is
        // saved. All of it applies at once. Sliders move in whole numbers; the settings file takes decimals.
        private static Setting[]? sSettings;
        internal static Setting[] Settings => sSettings ??= new[]
        {
            new Setting("GENERAL", "The Expanse", Plugin.Enabled),
            new Setting("LIVE REACTOR", "Live Reactor", Plugin.ReactorControl),
            new Setting("LIVE REACTOR", "Escalate Max Seconds", Plugin.ReactorEscalateMaxSeconds, true, 5f, 120f, 5f)
                { OffSwitch = Plugin.ReactorEscalation },
            new Setting("LIVE REACTOR", "Escalate Min Seconds", Plugin.ReactorEscalateMinSeconds, true, 1f, 60f, 1f)
                { ActiveWhen = () => Plugin.ReactorEscalation.Value },
            new Setting("LIVE REACTOR", "Core Meltdown", Plugin.ReactorMeltdown),
            new Setting("LIVE REACTOR", "Ship Damage", Plugin.ReactorShipDamage, true, 0f, 200f, 10f) { ZeroLabel = "OFF" },
            new Setting("LIVE REACTOR", "Player Damage", Plugin.ReactorPlayerDamage),
            new Setting("HAUNTING", "Ship Roll", Plugin.RollShip),
            new Setting("HAUNTING", "Haunted Lights", Plugin.LightFlicker)
                { AlsoSwitches = new[] { Plugin.LightBlackout, Plugin.HelmetFlicker } },
        };

        // Our rows show what they depend on: after any change, every row is refreshed.
        internal static void RefreshAll(Component row)
        {
            if (row.transform.parent == null) return;
            foreach (var other in row.transform.parent.GetComponentsInChildren<OptionEntry>(true))
                if (other.GetComponent<OptionBinding>() != null && other != row) other.RefreshOptionData();
        }

        internal static void Gray(OptionEntry entry, bool active)
        {
            var group = entry.GetComponent<CanvasGroup>() ?? entry.gameObject.AddComponent<CanvasGroup>();
            group.alpha = active ? 1f : 0.35f;
            if (entry is OptionEntrySlider slider && SliderControl(slider) != null) SliderControl(slider).interactable = active;
        }

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

        private static readonly AccessTools.FieldRef<ScrollController, bool> sAutoSelectVisible =
            AccessTools.FieldRefAccess<ScrollController, bool>("m_AutoSelectVisibleWhenOutOfView");

        private static int OurIndex(UnifiedOptionsScreen screen) =>
            Array.FindIndex(sScreens(screen) ?? new OptionsScreenController[0], s => s is ExpanseOptionsScreen);

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
            var staging = new GameObject("TheExpanse Options Staging");
            staging.SetActive(false);
            staging.transform.SetParent(unified.transform, false);
            GameObject? viewObject = null, tabObject = null;
            var done = false;
            try
            {
                var tabIndex = screens.Length;

                viewObject = Object.Instantiate(views[video].gameObject, staging.transform, false);
                viewObject.name = "TheExpanse Options View";
                viewObject.SetActive(false);
                var view = viewObject.GetComponent<UIView>();
                view.ViewName = $"TheExpanse Options {unified.GetInstanceID()}";
                view.Visibility = VisibilityState.NotVisible;
                view.BehaviorAtStart = UIViewStartBehavior.DoNothing;

                // The scroll snap-back fix, part 1 (see EnsureVisible). The stock scroll view re-selects a row that is in
                // view whenever it decides the selected one is not. That is meant for free scrolling with the mouse wheel
                // or right stick, but it also fires when stepping row by row: at the edge of the view the selected row is
                // lined up exactly with that edge, the check there is an exact float comparison, and when it goes the wrong
                // way the selection is thrown back to a row further up. Switched off for this page, before the copy's
                // Awake hooks it up.
                foreach (var scroll in viewObject.GetComponentsInChildren<ScrollController>(true)) sAutoSelectVisible(scroll) = false;

                var stockController = viewObject.GetComponent<OptionsScreenController>();
                var content = sOptionsContainer(stockController).transform;
                var headingTemplate = Object.Instantiate(FindHeadingTemplate(content), staging.transform, false);
                foreach (var c in viewObject.GetComponents<OptionsScreenController>()) Object.DestroyImmediate(c);
                foreach (var c in viewObject.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name == "ConditionalUIConfiguratorContainer") Object.DestroyImmediate(c);
                for (var i = content.childCount - 1; i >= 0; i--) Object.DestroyImmediate(content.GetChild(i).gameObject);

                var controller = viewObject.AddComponent<ExpanseOptionsScreen>();
                var entries = CreateRows(content, boolTemplate, sliderTemplate, headingTemplate);
                controller.Entries = entries;
                var firstButton = entries.Length > 0 ? sBackgroundButton(entries[0]) : null;
                view.SelectedButton = firstButton != null ? firstButton.gameObject : null;

                tabObject = Object.Instantiate(tabs[audio].gameObject, staging.transform, false);
                tabObject.name = "TheExpanse Options Tab";
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
                row.name = "TheExpanse " + setting.Entry.Definition.Key;
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

        // The scroll snap-back fix, part 2. With the stock re-selection off, this page keeps the selected row in view
        // itself, measured from where the row and the view really are on screen instead of from layout arithmetic, and
        // with a little room to spare so the row never sits exactly on the edge. The first row of a section brings its
        // heading into view with it.
        internal static void EnsureVisible(RectTransform row)
        {
            var scroll = row.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.viewport == null || scroll.content == null) return;
            Canvas.ForceUpdateCanvases();
            var viewport = scroll.viewport;
            var content = scroll.content;

            var top = row;
            int index = row.GetSiblingIndex();
            if (row.parent == content && index > 0)
            {
                var above = content.GetChild(index - 1) as RectTransform;
                if (above != null && above.GetComponentInChildren<OptionEntry>(true) == null) top = above;
            }

            var corners = new Vector3[4];
            top.GetWorldCorners(corners);
            float rowTop = viewport.InverseTransformPoint(corners[1]).y;
            row.GetWorldCorners(corners);
            float rowBottom = viewport.InverseTransformPoint(corners[0]).y;
            const float margin = 6f;

            float y = content.anchoredPosition.y;
            if (rowTop > viewport.rect.yMax - margin) y -= rowTop - (viewport.rect.yMax - margin);
            else if (rowBottom < viewport.rect.yMin + margin) y += (viewport.rect.yMin + margin) - rowBottom;
            else return;
            float max = Mathf.Max(0f, content.rect.height - viewport.rect.height);
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, Mathf.Clamp(y, 0f, max));
            scroll.velocity = Vector2.zero;
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
        internal static bool GetBool(int index) => Settings[index].Entries.Any(e => e.BoxedValue is bool b && b);
        internal static bool IsOff(int index) => Settings[index].OffSwitch is { } off && !off.Value;
        internal static bool IsActive(int index) => Settings[index].ActiveWhen?.Invoke() ?? true;

        internal static void SetFloat(int index, float value)
        {
            var setting = Settings[index];
            if (setting.OffSwitch != null)
            {
                bool off = value > setting.Max + 0.001f;
                if (setting.OffSwitch.Value == off) EditSession.Set(setting.OffSwitch, !off);
                if (off) return;            // the time itself is kept for when it is switched back on
            }
            value = Mathf.Clamp(value, setting.Min, setting.Max);
            EditSession.Set(setting.Entry, setting.Entry.SettingType == typeof(int) ? (object)Mathf.RoundToInt(value) : Mathf.Round(value));
        }

        internal static void SetBool(int index, bool value)
        {
            foreach (var e in Settings[index].Entries) EditSession.Set(e, value);
        }
    }

    // Changes made on the tab apply to the ConfigEntries right away (the bay lights react at once),
    // but the .cfg is only written on Save. Leaving the menu without saving restores the values the
    // tab opened with.
    internal static class EditSession
    {
        private static object[]? sSnapshot;
        private static ConfigEntryBase[] sEntries = new ConfigEntryBase[0];
        private static bool sSaveOnSetBefore;

        internal static bool IsDirty { get; private set; }
        internal static bool IsOpen => sSnapshot != null;

        internal static void Begin()
        {
            if (sSnapshot != null) return;
            sEntries = OptionsTab.Settings.SelectMany(s => s.Entries).Distinct().ToArray();
            sSnapshot = sEntries.Select(e => e.BoxedValue).ToArray();
            sSaveOnSetBefore = Plugin.Instance.Config.SaveOnConfigSet;
            Plugin.Instance.Config.SaveOnConfigSet = false;
            IsDirty = false;
        }

        internal static void Set(ConfigEntryBase entry, object value)
        {
            Begin();
            entry.BoxedValue = value;
            IsDirty = true;
            Plugin.OnSettingsChanged();
        }

        internal static void ResetToDefaults()
        {
            Begin();
            foreach (var e in sEntries) e.BoxedValue = e.DefaultValue;
            IsDirty = true;
            Plugin.OnSettingsChanged();
        }

        internal static void Commit()
        {
            if (sSnapshot == null) return;
            try
            {
                Plugin.Instance.Config.Save();
                Plugin.OwnWriteTimeUtc = System.IO.File.GetLastWriteTimeUtc(Plugin.Instance.Config.ConfigFilePath);
            }
            catch (Exception ex) { Plugin.Log.LogError($"Could not save TheExpanse settings: {ex}"); return; }
            End();
        }

        internal static void Rollback()
        {
            if (sSnapshot == null) return;
            var snapshot = sSnapshot;
            try
            {
                for (var i = 0; i < snapshot.Length; i++)
                    if (!Equals(sEntries[i].BoxedValue, snapshot[i])) sEntries[i].BoxedValue = snapshot[i];
            }
            finally { End(); Plugin.OnSettingsChanged(); }
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

    internal sealed class ExpanseOptionsScreen : OptionsScreenController
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
            catch (Exception ex) { Plugin.Log.LogError($"EXPANSE tab switch: {ex}"); }
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
            catch (Exception ex) { Plugin.Log.LogError($"EXPANSE tab switch: {ex}"); }
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
            catch (Exception ex) { Plugin.Log.LogError($"EXPANSE tab switch: {ex}"); }
        }
        private static void Postfix(UnifiedOptionsScreen __instance) => OptionsTab.TrySyncTabOrder(__instance);
    }

    [HarmonyPatch(typeof(UnifiedOptionsScreen), "OnDisable")]
    internal static class UnifiedOptionsScreen_OnDisable_Patch
    {
        private static void Postfix(UnifiedOptionsScreen __instance)
        {
            try { OptionsTab.OnOptionsClosed(__instance); }
            catch (Exception ex) { Plugin.Log.LogError($"Could not close the EXPANSE settings cleanly: {ex}"); }
        }
    }

    // ---- row hooks: only rows carrying an OptionBinding are ours; everything else runs stock ----

    [HarmonyPatch(typeof(OptionEntry), nameof(OptionEntry.OnSelect))]
    internal static class OptionEntry_OnSelect_Patch
    {
        private static void Postfix(OptionEntry __instance)
        {
            if (__instance.GetComponent<OptionBinding>() == null) return;
            try { OptionsTab.EnsureVisible((RectTransform)__instance.transform); }
            catch (Exception ex) { Plugin.Log.LogError($"EXPANSE tab scrolling: {ex}"); }
        }
    }

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
            OptionsTab.Gray(__instance, OptionsTab.IsActive(binding.Index));
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
            OptionsTab.RefreshAll(__instance);
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
                    slider.maxValue = setting.SliderMax;
                    slider.value = OptionsTab.IsOff(binding.Index) ? setting.SliderMax : value;
                }
                var text = OptionsTab.SliderValueText(__instance);
                if (text != null) text.text = OptionsTab.IsOff(binding.Index) ? "OFF"
                    : setting.ZeroLabel != null && value < 0.5f ? setting.ZeroLabel : value.ToString("F0", CultureInfo.InvariantCulture);
                OptionsTab.Gray(__instance, OptionsTab.IsActive(binding.Index));
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
                OptionsTab.RefreshAll(__instance);
                __instance.OnDataChanged?.Invoke(__instance);
            }
            return false;
        }
    }
}
