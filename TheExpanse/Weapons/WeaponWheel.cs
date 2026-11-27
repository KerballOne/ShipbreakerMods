using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using BBI.Unity.Game.UI;
using HarmonyLib;
using InControl;
using TMPro;
using UnityEngine;

namespace TheExpanse
{
    // The weapon wheel is the suit's own tool wheel (ToolSelectionUIController), driven by the mod while seated:
    //   hold R (Y on a pad): each segment's label (CUTR, GRPL, DEMO, SCAN) is swapped for a weapon's, the durability
    //       rings are hidden, and the wheel is shown by the game's own event (ToolSelectMenuEvent), with its own look,
    //       sounds and highlighting; it is shown over the faded suit HUD (outside and nose views) by its own canvas group
    //       ignoring the HUD's;
    //   while held: the four ToolNav actions (mouse movement / left stick) stay on, so the wheel's own Update highlights
    //       the segment pointed at, the same way as in the suit;
    //   release: the segment highlighted (its mSelect) picks the mode; the wheel is closed "without swap", so the suit's
    //       tool never changes, and the labels are put back.
    // Segments clockwise from the top: Helm, PDC, Torpedo, Rail gun (as many as the wheel has, up to four).
    internal static class WeaponWheel
    {
        static ToolSelectionUIController? sWheel;
        static List<ToolSelectionUIController.ToolHighlight>? sHighlights;
        static float sNextLook;
        static bool sLogged, sMissingLogged;

        static readonly FieldInfo? fHighlights = AccessTools.Field(typeof(ToolSelectionUIController), "m_ToolHighlights");
        static readonly FieldInfo? fSelect = AccessTools.Field(typeof(ToolSelectionUIController), "mSelect");

        static readonly string[] NavActions =
        {
            GameplayActions.GameplayActionSet.ToolNavUp.ToString(), GameplayActions.GameplayActionSet.ToolNavDown.ToString(),
            GameplayActions.GameplayActionSet.ToolNavRight.ToString(), GameplayActions.GameplayActionSet.ToolNavLeft.ToString(),
        };

        // what was changed on opening, put back on closing
        static readonly List<(TMP_Text text, string was, int slot)> sTexts = new List<(TMP_Text, string, int)>();
        static readonly List<(GameObject go, bool active)> sHidden = new List<(GameObject, bool)>();
        static readonly List<GameObject> sShown = new List<GameObject>();     // switched on to show a label; off again on closing
        // the wheel and its dark backdrop, each seen through the faded suit HUD (outside and nose views) by a canvas
        // group ignoring the HUD's: (group, added by the mod, its ignoreParentGroups before)
        static readonly List<(CanvasGroup group, bool added, bool ignored)> sGroups = new List<(CanvasGroup, bool, bool)>();
        static readonly FieldInfo? fOverlay = AccessTools.Field(typeof(ToolSelectionUIController), "m_BackgroundOverlay");
        static bool[] sWasDisabled = new bool[0];

        internal static bool Open { get; private set; }

        // FlightSeat leaves these on while the wheel is open
        internal static bool KeepsOn(string actionName)
        {
            if (!Open) return false;
            foreach (var name in NavActions) if (name == actionName) return true;
            return false;
        }

        internal static void Reset()
        {
            Close(cancel: true);
            sWheel = null;
            sHighlights = null;
            sNextLook = 0f;
            sLogged = sMissingLogged = false;
        }

        internal static void Tick()
        {
            var pad = InputManager.ActiveDevice;
            if (!Open)
            {
                if (Input.GetKeyDown(KeyCode.R) || (pad != null && pad.Action4.WasPressed)) OpenWheel();
                return;
            }
            bool held = Input.GetKey(KeyCode.R) || (pad != null && pad.Action4.IsPressed);
            if (!held) Close(cancel: false);
        }

        internal static void LateFrame()
        {
            if (!Open) return;
            // the wheel's Highlight turns the selected segment's durability ring back on; the labels are set again in case
            // the game's localization puts its own back when they are shown
            foreach (var (go, _) in sHidden) if (go != null && go.activeSelf) go.SetActive(false);
            foreach (var (text, _, slot) in sTexts)
            {
                if (text == null) continue;
                if (text.text != Labels[slot]) text.text = Labels[slot];
                // a slice the suit greys out (the scanner's before it is unlocked) hides its label: shown here, in the
                // look of the first slice's label
                if (!text.enabled) text.enabled = true;
                for (var t = text.transform; t != null && t != sWheel!.transform; t = t.parent)
                    if (!t.gameObject.activeSelf) { sShown.Add(t.gameObject); t.gameObject.SetActive(true); }
                if (sTexts.Count > 0 && sTexts[0].text != null && sTexts[0].text != text && text.color.a < 0.3f)
                    text.color = sTexts[0].text.color;
            }
        }

        static bool FindWheel()
        {
            if (sWheel != null && sHighlights != null) return true;
            if (Time.unscaledTime < sNextLook) return false;
            sNextLook = Time.unscaledTime + 2f;
            sWheel = Object.FindObjectOfType<ToolSelectionUIController>();
            if (sWheel == null)
                foreach (var w in Resources.FindObjectsOfTypeAll<ToolSelectionUIController>())
                    if (w != null && w.gameObject.scene.IsValid()) { sWheel = w; break; }
            sHighlights = sWheel != null ? fHighlights?.GetValue(sWheel) as List<ToolSelectionUIController.ToolHighlight> : null;
            if (sWheel == null || sHighlights == null || sHighlights.Count == 0)
            {
                if (!sMissingLogged) Plugin.Log.LogWarning("[Weapons] the suit's tool wheel was not found; the wheel can't open (1-4 still pick the mode).");
                sMissingLogged = true;
                sWheel = null; sHighlights = null;
                return false;
            }
            return true;
        }

        static void OpenWheel()
        {
            if (!FindWheel()) return;
            var wheel = sWheel!;
            var highlights = sHighlights!;
            if (!sLogged) { sLogged = true; LogLayout(wheel, highlights); }

            sWasDisabled = new bool[highlights.Count];
            for (int i = 0; i < highlights.Count; i++)
            {
                var h = highlights[i];
                sWasDisabled[i] = h.Disabled;
                // a segment past the four modes stays greyed out
                h.Disabled = i >= 4;
                Hide(h.DurabilityCircle);
                Hide(h.DurabilityBar);
                Hide(h.DurabilityMirrorText);
                if (h.ProgressBar != null) Hide(h.ProgressBar.gameObject);
                if (i < 4) SwapLabels(h, i);
            }
            // seen through the faded suit HUD
            SeeThrough(wheel.gameObject);
            if (fOverlay?.GetValue(wheel) is GameObject overlay && overlay != null && !overlay.transform.IsChildOf(wheel.transform)) SeeThrough(overlay);

            SetNav(true);
            Open = true;
            Main.EventSystem.Post(ToolSelectMenuEvent.GetEvent(EquipmentController.Equipment.None, visible: true));
            // opened on the mode in use
            int slot = Mathf.Min((int)Weapons.Mode, highlights.Count - 1);
            fSelect?.SetValue(wheel, slot);
            wheel.Highlight(slot);
            LateFrame();
        }

        internal static void Close(bool cancel)
        {
            if (!Open) return;
            Open = false;
            int chosen = -1;
            if (sWheel != null && fSelect != null) chosen = (int)fSelect.GetValue(sWheel);
            Main.EventSystem.Post(ToolSelectMenuEvent.GetEvent(EquipmentController.Equipment.None, visible: false, closeWithoutSwap: true));
            SetNav(false);
            foreach (var (text, was, _) in sTexts) if (text != null) text.text = was;
            sTexts.Clear();
            foreach (var go in sShown) if (go != null) go.SetActive(false);
            sShown.Clear();
            foreach (var (go, active) in sHidden) if (go != null) go.SetActive(active);
            sHidden.Clear();
            foreach (var (group, added, ignored) in sGroups)
            {
                if (group == null) continue;
                if (added) Object.Destroy(group);
                else group.ignoreParentGroups = ignored;
            }
            sGroups.Clear();
            if (sHighlights != null)
                for (int i = 0; i < sHighlights.Count && i < sWasDisabled.Length; i++) sHighlights[i].Disabled = sWasDisabled[i];
            if (!cancel && chosen >= 0 && chosen < 4) Weapons.SetMode((WeaponMode)chosen, "wheel");
        }

        static void SeeThrough(GameObject go)
        {
            var group = go.GetComponent<CanvasGroup>();
            bool added = group == null;
            if (group == null) group = go.AddComponent<CanvasGroup>();
            sGroups.Add((group, added, group.ignoreParentGroups));
            group.ignoreParentGroups = true;
        }

        static void Hide(GameObject? go)
        {
            if (go == null) return;
            sHidden.Add((go, go.activeSelf));
            go.SetActive(false);
        }

        // ToolNav* on (wheel open) or back off (FlightSeat keeps every suit action off while seated).
        static void SetNav(bool on)
        {
            var set = LynxControls.Instance?.TryGetLoadedActionSet(LynxControls.PlayerActionSetTypes.GameplayActions);
            if (set == null) return;
            foreach (var action in set.Actions)
            {
                if (action == null || System.Array.IndexOf(NavActions, action.Name) < 0) continue;
                action.ClearInputState();
                action.Enabled = on;
            }
        }

        // ---- labels ------------------------------------------------------------------------------------------------

        // The segment's own object (Radial0 - Cutter): its highlight's parent's parent ("R0 buttons" is the parent).
        static Transform SegmentRoot(ToolSelectionUIController.ToolHighlight h)
        {
            if (h.SelectionImage == null) return sWheel!.transform;
            var buttons = h.SelectionImage.transform.parent;
            return buttons != null && buttons.parent != null ? buttons.parent : buttons ?? sWheel!.transform;
        }

        // The game's four-letter labels (CUTR, GRPL, DEMO, SCAN), in the same style.
        static readonly string[] Labels = { "HELM", "PDC", "TORP", "RAIL" };

        static IEnumerable<TMP_Text> Texts(ToolSelectionUIController.ToolHighlight h)
        {
            foreach (var text in SegmentRoot(h).GetComponentsInChildren<TMP_Text>(true))
                if (text != null && !Under(text.transform, h)) yield return text;
        }

        static void SwapLabels(ToolSelectionUIController.ToolHighlight h, int slot)
        {
            foreach (var text in Texts(h))
            {
                sTexts.Add((text, text.text, slot));
                text.text = Labels[slot];
            }
        }

        static bool Under(Transform t, ToolSelectionUIController.ToolHighlight h)
        {
            foreach (var go in new[] { h.DurabilityCircle, h.DurabilityBar, h.DurabilityMirrorText, h.ProgressBar != null ? h.ProgressBar.gameObject : null })
                if (go != null && t.IsChildOf(go.transform)) return true;
            return false;
        }

        // ---- the one-time layout log -------------------------------------------------------------------------------

        static void LogLayout(ToolSelectionUIController wheel, List<ToolSelectionUIController.ToolHighlight> highlights)
        {
            var canvas = wheel.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            var lines = new List<string>
            {
                $"[Weapons] tool wheel '{Path(wheel.transform, 8)}', {highlights.Count} segment(s); canvas '{(root != null ? root.name : "none")}' " +
                $"({(root != null ? root.renderMode.ToString() : "-")}); canvas groups above: " +
                string.Join(", ", System.Array.ConvertAll(wheel.GetComponentsInParent<CanvasGroup>(true), g => $"{g.name} {g.alpha:0.##}")),
            };
            for (int i = 0; i < highlights.Count; i++)
            {
                var h = highlights[i];
                var seg = SegmentRoot(h);
                lines.Add($"[Weapons]  segment {i} -> {(i < 4 ? Weapons.Names[i] : "unused")}: tool {h.Tool}, disabled {h.Disabled}, " +
                          $"highlight '{(h.SelectionImage != null ? Path(h.SelectionImage.transform, 3) : "none")}' " +
                          $"segment '{Path(seg, 3)}'");
                foreach (var text in seg.GetComponentsInChildren<TMP_Text>(true))
                    if (text != null)
                        lines.Add($"[Weapons]    label '{Path(text.transform, 3)}' \"{text.text}\" size {text.fontSize:0}" +
                                  $"{(Under(text.transform, h) ? " (durability, left alone)" : "")}");
            }
            foreach (var line in lines) Plugin.Log.LogInfo(line);
        }

        static string Path(Transform t, int depth)
        {
            var names = new List<string>();
            for (int i = 0; t != null && i < depth; t = t.parent, i++) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
