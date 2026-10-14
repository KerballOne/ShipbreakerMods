using System;
using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FreePlayPlus
{
    // FreePlay ship select: picking a ship that has saves opens a list of them (newest first) with
    // Load / Delete per save; the footer has New ship (launches as stock), Delete all and Close.
    //
    // Layout: its own screen-space overlay canvas scaled from a 1920x1080 reference, with the panel
    // anchored to a fraction of the screen, so it fits any resolution or aspect ratio.
    //
    // Input: the game's LynxInputModule is a plain StandaloneInputModule fed by legacy axes the game
    // doesn't drive, so UGUI navigation never moves. The game's own menus navigate via LynxControls
    // FrontEnd actions, and so does this popup: NavigateUp/Down/Left/Right move over a row/column
    // grid of the buttons (keyboard arrows, D-pad, left stick), Confirm clicks the selected one,
    // Back/Decline closes. Mouse clicks go through the normal Button path; a click is de-duplicated
    // per frame in case the module also submits. While open, the ship list behind is
    // non-interactable and the game's back button and the Ghost Ship hotkeys are suspended.
    internal static class SaveListUI
    {
        private static readonly Color kDim = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color kPanel = new Color(0.07f, 0.07f, 0.07f, 0.97f);
        private static readonly Color kAccent = new Color(1f, 0.55f, 0.1f, 1f);
        private static readonly Color kButton = new Color(0.16f, 0.16f, 0.16f, 1f);
        private static readonly Color kButtonDanger = new Color(0.35f, 0.08f, 0.06f, 1f);
        private const float kRowHeight = 52f;
        private const float kHeaderHeight = 64f;
        private const float kFooterHeight = 60f;
        private const float kPad = 24f;
        private const float kFooterButtonWidth = 250f;

        private static GameObject? sRoot;
        private static LevelSelectButton? sButton;
        private static LevelSelectController? sController;
        private static string sShipKey = "";
        private static string sShipName = "";
        private static ScrollRect? sScroll;
        private static bool sBackWasEnabled;
        private static bool sConfirmDeleteAll;
        private static readonly List<List<Button>> sGrid = new List<List<Button>>();
        private static int sRow, sCol;
        private static int sLastFireFrame = -1;
        private static int sOpenedFrame = -1;
        internal static bool BypassNextClick;
        internal static int ClosedFrame = -100;
        internal static TMP_FontAsset? Font;
        internal static Material? FontMaterial;

        internal static bool IsOpen => sRoot != null;

        // True from picking Load until the level load starts (priming a big custom ship can take a
        // while): the ship list, Back and further loads are blocked and a panel says what's going on.
        internal static bool Preparing { get; private set; }
        private static GameObject? sPreparingPanel;

        internal static void Open(LevelSelectButton button, string shipKey, string shipName)
        {
            Close(reselect: false);
            sButton = button;
            sShipKey = shipKey;
            sShipName = shipName;
            sController = button.GetComponentInParent<LevelSelectController>() ??
                          Traverse.Create(button).Field("m_levelSelectController").GetValue<LevelSelectController>();
            sController?.ToggleLevelSelectButtonInteractability(false);
            if (BackButtonService.Instance != null)
            {
                sBackWasEnabled = BackButtonService.Instance.Enabled;
                BackButtonService.Instance.Enabled = false;
            }
            sRow = 0;
            sCol = 0;
            sOpenedFrame = Time.frameCount;
            Build();
        }

        internal static void Close(bool reselect = true)
        {
            if (sRoot == null) return;
            Object.Destroy(sRoot);
            sRoot = null;
            sScroll = null;
            sGrid.Clear();
            sConfirmDeleteAll = false;
            ClosedFrame = Time.frameCount;
            sController?.ToggleLevelSelectButtonInteractability(true);
            if (BackButtonService.Instance != null) BackButtonService.Instance.Enabled = sBackWasEnabled;
            if (reselect && sButton != null)
            {
                var uiButton = sButton.GetComponentInChildren<Button>();
                if (uiButton != null && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(uiButton.gameObject);
            }
        }

        // Called from Plugin.Update.
        internal static void Tick()
        {
            if (sRoot == null) return;
            if (sButton == null || !sButton.isActiveAndEnabled) { Close(reselect: false); return; }

            SyncFromEventSystem();
            // The Confirm press that opened the popup must not also click its first button.
            if (Time.frameCount <= sOpenedFrame + 1) { KeepSelectionVisible(); return; }
            var controls = LynxControls.Instance;
            if (controls != null)
            {
                if (controls.GetInputWasPressed(FrontEndActions.FrontEndActionSet.Back) ||
                    controls.GetInputWasPressed(FrontEndActions.FrontEndActionSet.Decline))
                {
                    Close();
                    return;
                }
                if (controls.GetInputWasPressed(FrontEndActions.FrontEndActionSet.NavigateUp)) Move(-1, 0);
                else if (controls.GetInputWasPressed(FrontEndActions.FrontEndActionSet.NavigateDown)) Move(1, 0);
                else if (controls.GetInputWasPressed(FrontEndActions.FrontEndActionSet.NavigateLeft)) Move(0, -1);
                else if (controls.GetInputWasPressed(FrontEndActions.FrontEndActionSet.NavigateRight)) Move(0, 1);
                else if (controls.GetInputWasPressed(FrontEndActions.FrontEndActionSet.Confirm)) Selected()?.onClick.Invoke();
            }
            KeepSelectionVisible();
        }

        private static Button? Selected() =>
            sRow >= 0 && sRow < sGrid.Count && sCol >= 0 && sCol < sGrid[sRow].Count ? sGrid[sRow][sCol] : null;

        // Mouse clicks change EventSystem selection; follow it so keyboard/controller continue from there.
        private static void SyncFromEventSystem()
        {
            var current = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (current == null || Selected()?.gameObject == current) return;
            for (var r = 0; r < sGrid.Count; r++)
                for (var c = 0; c < sGrid[r].Count; c++)
                    if (sGrid[r][c].gameObject == current) { sRow = r; sCol = c; return; }
        }

        private static void Move(int rows, int cols)
        {
            if (sGrid.Count == 0) return;
            if (rows != 0)
            {
                // Keep the column position by fraction across rows with different button counts.
                var fraction = sGrid[sRow].Count > 1 ? (float)sCol / (sGrid[sRow].Count - 1) : 0f;
                sRow = Mathf.Clamp(sRow + rows, 0, sGrid.Count - 1);
                sCol = sGrid[sRow].Count > 1 ? Mathf.RoundToInt(fraction * (sGrid[sRow].Count - 1)) : 0;
            }
            if (cols != 0) sCol = Mathf.Clamp(sCol + cols, 0, sGrid[sRow].Count - 1);
            Select();
        }

        private static void Select()
        {
            var button = Selected();
            if (button != null && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(button.gameObject);
        }

        private static void Build()
        {
            sGrid.Clear();
            sRoot = new GameObject("FreePlayPlusSaveList", typeof(RectTransform));
            Object.DontDestroyOnLoad(sRoot);
            var canvas = sRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            var scaler = sRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            sRoot.AddComponent<GraphicRaycaster>();

            var dim = Child("Dim", (RectTransform)sRoot.transform);
            Stretch(dim);
            dim.gameObject.AddComponent<Image>().color = kDim; // blocks clicks to the menu behind

            var panel = Child("Panel", dim);
            panel.anchorMin = new Vector2(0.2f, 0.12f);
            panel.anchorMax = new Vector2(0.8f, 0.88f);
            panel.offsetMin = panel.offsetMax = Vector2.zero;
            panel.gameObject.AddComponent<Image>().color = kPanel;
            panel.gameObject.AddComponent<Outline>().effectColor = kAccent;

            // Header (top), footer (bottom), scrolling list (between).
            var header = Child("Header", panel);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.offsetMin = new Vector2(kPad, -kPad - kHeaderHeight);
            header.offsetMax = new Vector2(-kPad, -kPad);
            var title = Label(header, $"{sShipName}  <size=70%><color=#999999>saved runs</color></size>", 32f, TextAlignmentOptions.Left, kAccent);
            Stretch((RectTransform)title.transform);

            var footer = Child("Footer", panel);
            footer.anchorMin = new Vector2(0f, 0f);
            footer.anchorMax = new Vector2(1f, 0f);
            footer.pivot = new Vector2(0.5f, 0f);
            footer.offsetMin = new Vector2(kPad, kPad);
            footer.offsetMax = new Vector2(-kPad, kPad + kFooterHeight);
            RowLayout(footer);

            var list = Child("List", panel);
            list.anchorMin = Vector2.zero;
            list.anchorMax = Vector2.one;
            list.offsetMin = new Vector2(kPad, kPad + kFooterHeight + 16f);
            list.offsetMax = new Vector2(-kPad, -kPad - kHeaderHeight - 12f);
            sScroll = list.gameObject.AddComponent<ScrollRect>();
            sScroll.horizontal = false;
            sScroll.movementType = ScrollRect.MovementType.Clamped;
            sScroll.scrollSensitivity = 30f;
            var viewport = Child("Viewport", list);
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = Child("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var contentLayout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            contentLayout.spacing = 6f;
            contentLayout.childForceExpandHeight = false;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childControlHeight = true;
            contentLayout.childControlWidth = true;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            sScroll.viewport = viewport;
            sScroll.content = content;

            var saves = ShipSaveStore.List(sShipKey);
            if (saves.Count == 0)
                Label(Row(content), "<color=#999999>No saves for this ship.</color>", 22f, TextAlignmentOptions.Left, Color.white, flexible: true);
            foreach (var save in saves)
            {
                var row = Row(content);
                Label(row, $"{save.Time:yyyy-MM-dd  HH:mm}    {save.KindLabel}    <color=#FF8C1A>{save.Percent:0.0}%</color> salvaged", 22f, TextAlignmentOptions.Left, Color.white, flexible: true);
                var captured = save;
                sGrid.Add(new List<Button>
                {
                    ButtonUI(row, "Load", kButton, 150f, () => Load(captured)),
                    ButtonUI(row, "Delete", kButtonDanger, 150f, () => { ShipSaveStore.Delete(sShipKey, captured); AfterChange(); }),
                });
            }
            Label(footer, $"<color=#999999>{saves.Count} / {ShipSaveStore.MaxSavesPerShip} saves</color>", 20f, TextAlignmentOptions.Left, Color.white, flexible: true);
            sGrid.Add(new List<Button>
            {
                ButtonUI(footer, "New ship", kButton, kFooterButtonWidth, NewShip),
                ButtonUI(footer, sConfirmDeleteAll ? "Confirm delete all" : "Delete all", kButtonDanger, kFooterButtonWidth, DeleteAll),
                ButtonUI(footer, "Close", kButton, kFooterButtonWidth, () => Close()),
            });

            sRow = Mathf.Clamp(sRow, 0, sGrid.Count - 1);
            sCol = Mathf.Clamp(sCol, 0, sGrid[sRow].Count - 1);
            Select();
        }

        private static void Rebuild()
        {
            if (sRoot == null) return;
            Object.Destroy(sRoot);
            sRoot = null;
            Build();
        }

        private static void AfterChange() => Rebuild();

        private static void DeleteAll()
        {
            if (!sConfirmDeleteAll) { sConfirmDeleteAll = true; Rebuild(); return; }
            ShipSaveStore.DeleteAll(sShipKey);
            sConfirmDeleteAll = false;
            AfterChange();
        }

        private static void NewShip()
        {
            var button = sButton;
            Close(reselect: false);
            if (button == null) return;
            BypassNextClick = true;
            button.ForceClick();
        }

        private static async void Load(ShipSave save)
        {
            var button = sButton;
            if (button == null || Preparing) return;
            var level = Traverse.Create(button).Field("mLevelToLoad").GetValue<LevelAsset.LevelData>();
            var controller = sController;
            var shipName = sShipName;
            Close(reselect: false);
            controller?.ToggleLevelSelectButtonInteractability(false);
            BeginPreparing(shipName);
            Plugin.Log.LogInfo($"Loading FreePlay save {System.IO.Path.GetFileName(save.Path)}.");
            var preview = await ShipPreview.CreateFromFileAsync(save.Path, bArchiveInvalidFiles: false);
            if (preview == null)
            {
                Plugin.Log.LogError($"Couldn't read save {save.Path}; it may be from an incompatible version.");
                EndPreparing();
                controller?.ToggleLevelSelectButtonInteractability(true);
                return;
            }
            try { await SavedShipPrimer.PrimeAsync(preview); }
            catch (Exception ex) { Plugin.Log.LogError($"Preparing the saved ship failed: {ex}"); }
            EndPreparing();
            SceneLoader.Instance.TearDownAndLoadLevelWithShipPreviewAsync(level, preview);
        }

        private static void BeginPreparing(string shipName)
        {
            Preparing = true;
            if (BackButtonService.Instance != null) BackButtonService.Instance.Enabled = false;
            sPreparingPanel = new GameObject("FreePlayPlusPreparing", typeof(RectTransform));
            var canvas = sPreparingPanel.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            var scaler = sPreparingPanel.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            sPreparingPanel.AddComponent<GraphicRaycaster>();
            var dim = Child("Dim", (RectTransform)sPreparingPanel.transform);
            Stretch(dim);
            dim.gameObject.AddComponent<Image>().color = kDim; // blocks clicks on the menu behind
            var panel = Child("Panel", dim);
            panel.anchorMin = new Vector2(0.3f, 0.42f);
            panel.anchorMax = new Vector2(0.7f, 0.58f);
            panel.offsetMin = panel.offsetMax = Vector2.zero;
            panel.gameObject.AddComponent<Image>().color = kPanel;
            panel.gameObject.AddComponent<Outline>().effectColor = kAccent;
            var label = Label(panel, $"Preparing {shipName}...\n<size=70%><color=#999999>Large custom ships can take a minute.</color></size>", 30f, TextAlignmentOptions.Center, Color.white);
            label.enableWordWrapping = true;
            Stretch((RectTransform)label.transform);
        }

        private static void EndPreparing()
        {
            Preparing = false;
            if (sPreparingPanel != null) Object.Destroy(sPreparingPanel);
            sPreparingPanel = null;
            if (BackButtonService.Instance != null) BackButtonService.Instance.Enabled = true;
        }

        private static void KeepSelectionVisible()
        {
            if (sScroll == null) return;
            var selected = Selected();
            if (selected == null || !selected.transform.IsChildOf(sScroll.content)) return;
            var content = sScroll.content;
            var viewport = sScroll.viewport;
            var corners = new Vector3[4];
            ((RectTransform)selected.transform).GetWorldCorners(corners);
            var top = viewport.InverseTransformPoint(corners[1]).y;
            var bottom = viewport.InverseTransformPoint(corners[0]).y;
            var view = viewport.rect;
            if (top > view.yMax) content.anchoredPosition -= new Vector2(0f, top - view.yMax);
            else if (bottom < view.yMin) content.anchoredPosition += new Vector2(0f, view.yMin - bottom);
        }

        // ---- small UGUI builders --------------------------------------------------------------

        private static RectTransform Child(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        // Fixed-width parts keep their width; flexible parts share what's left (and may shrink to 0,
        // so a long label ellipsises instead of pushing buttons off the panel).
        private static void Size(GameObject go, float height, float width, bool flexible)
        {
            var element = go.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = height;
            if (flexible) { element.minWidth = 0f; element.preferredWidth = 0f; element.flexibleWidth = 1f; }
            else if (width > 0f) { element.minWidth = element.preferredWidth = width; element.flexibleWidth = 0f; }
        }

        private static void RowLayout(RectTransform row)
        {
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
        }

        private static RectTransform Row(RectTransform parent)
        {
            var row = Child("Row", parent);
            RowLayout(row);
            var element = row.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = kRowHeight;
            return row;
        }

        private static TextMeshProUGUI Label(RectTransform parent, string text, float size, TextAlignmentOptions align, Color color, bool flexible = false)
        {
            var rect = Child("Label", parent);
            var tmp = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) { tmp.font = Font; if (FontMaterial != null) tmp.fontSharedMaterial = FontMaterial; }
            tmp.fontSize = size;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = size * 0.6f;
            tmp.fontSizeMax = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            tmp.text = text;
            if (flexible) Size(rect.gameObject, kRowHeight, 0f, flexible: true);
            return tmp;
        }

        private static Button ButtonUI(RectTransform parent, string text, Color color, float width, UnityEngine.Events.UnityAction onClick, bool flexible = false)
        {
            var rect = Child("Button_" + text, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
            colors.selectedColor = new Color(2.2f, 1.6f, 0.9f, 1f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            colors.colorMultiplier = 2f;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None }; // navigation is ours
            button.onClick.AddListener(() =>
            {
                if (Time.frameCount == sLastFireFrame) return;
                sLastFireFrame = Time.frameCount;
                onClick();
            });
            rect.gameObject.AddComponent<Outline>().effectColor = new Color(kAccent.r, kAccent.g, kAccent.b, 0.35f);
            var label = Label(rect, text, 22f, TextAlignmentOptions.Center, Color.white);
            Stretch((RectTransform)label.transform);
            Size(rect.gameObject, kRowHeight, width, flexible);
            return button;
        }
    }

    // Picking a FreePlay ship that has saves opens the save list instead of launching.
    [HarmonyPatch(typeof(LevelSelectButton), nameof(LevelSelectButton.ForceClick))]
    internal static class LevelSelectButton_ForceClick_Patch
    {
        private static bool Prefix(LevelSelectButton __instance)
        {
            if (SaveListUI.Preparing) return false;
            if (SaveListUI.BypassNextClick) { SaveListUI.BypassNextClick = false; return true; }
            if (SaveListUI.IsOpen) return false;
            if (Time.frameCount <= SaveListUI.ClosedFrame + 2) return false; // same press that closed the popup
            var level = Traverse.Create(__instance).Field("mLevelToLoad").GetValue<LevelAsset.LevelData>();
            if (level.SessionType != GameSession.SessionType.FreeMode) return true;
            var key = ShipSaver.ShipKey(level);
            if (ShipSaveStore.List(key).Count == 0) return true;
            var name = Traverse.Create(__instance).Field("m_ShipTypeName").GetValue<LocalizedTextMeshProUGUI>()?.TMProText?.text ?? "Ship";
            var tag = Plugin.IndicatorText.Value;
            if (!string.IsNullOrEmpty(tag)) name = name.Replace("  " + tag, "");
            SaveListUI.Open(__instance, key!, name);
            return false;
        }
    }

    // "Saved 42.3%" under each FreePlay ship that has saves (latest save's salvage progress).
    internal static class ShipSaveLabels
    {
        private const string kLabelName = "FreePlayPlusSaveLabel";

        internal static void Refresh(LevelSelectButton button, LevelAsset.LevelData level)
        {
            var key = ShipSaver.ShipKey(level);
            var shown = Traverse.Create(button).Field("m_ShipTypeName").GetValue<LocalizedTextMeshProUGUI>()?.TMProText?.text;
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrWhiteSpace(shown))
            {
                var tag = Plugin.IndicatorText.Value;
                if (!string.IsNullOrEmpty(tag)) shown = shown!.Replace("  " + tag, "");
                ShipSaveStore.DisplayNames[key!] = shown!.Trim();
            }
            var saves = ShipSaveStore.List(key);
            var existing = button.transform.Find(kLabelName);
            if (saves.Count == 0)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }

            TextMeshProUGUI tmp;
            if (existing == null)
            {
                var rect = new GameObject(kLabelName, typeof(RectTransform)).GetComponent<RectTransform>();
                rect.SetParent(button.transform, false);
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.anchoredPosition = new Vector2(0f, 8f);
                rect.sizeDelta = new Vector2(-16f, 30f);
                tmp = rect.gameObject.AddComponent<TextMeshProUGUI>();
                if (SaveListUI.Font != null) { tmp.font = SaveListUI.Font; if (SaveListUI.FontMaterial != null) tmp.fontSharedMaterial = SaveListUI.FontMaterial; }
                tmp.fontSize = 20f;
                tmp.alignment = TextAlignmentOptions.Bottom;
                tmp.color = new Color(1f, 0.55f, 0.1f, 1f);
                tmp.raycastTarget = false;
                tmp.enableWordWrapping = false;
            }
            else
            {
                existing.gameObject.SetActive(true);
                tmp = existing.GetComponent<TextMeshProUGUI>();
            }
            var text = $"Saved {saves[0].Percent:0.0}%";
            if (tmp.text != text) tmp.text = text;
        }
    }
}
