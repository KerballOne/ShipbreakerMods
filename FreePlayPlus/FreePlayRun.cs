using BBI.Unity.Game;
using HarmonyLib;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace FreePlayPlus
{
    // Per-run stats that ship saves carry: the shift clock (FreePlay's count-up timer,
    // GameSessionTimerData.CurrentTime) and kiosk expenses (the FreePlay profile's credits
    // SpentAmount, which only purchases add to). A fresh FreePlay session starts both at 0, so a
    // loaded save sets them back to what was saved once the shift starts.
    internal static class FreePlayRun
    {
        private static float? sPendingRuntime;
        private static float sPendingExpenses;

        // Seconds on the FreePlay shift clock, or null outside a running FreePlay shift.
        internal static float? Runtime
        {
            get
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null || !world.EntityManager.TryGetSingletonComponent<GameSessionTimerData>(out var timer)) return null;
                return timer.TimerCountsUp ? (float)timer.CurrentTime : (float?)null;
            }
        }

        internal static float Expenses => Credits()?.SpentAmount ?? 0f;

        private static CurrencyInstance? Credits()
        {
            var profile = PlayerProfileService.Instance?.Profile;
            var mode = profile?.DifficultyMode;
            var debt = mode != null ? mode.DebtInterestAsset : null;
            var currency = debt != null ? debt.Data.Currency : null;
            return currency != null ? profile!.CurrencyController?.GetCurrency(currency.ID) : null;
        }

        // Called right before a save is loaded; applied when that shift starts.
        internal static void RestoreOnNextShift(ShipSave save)
        {
            sPendingRuntime = save.Runtime ?? 0f;
            sPendingExpenses = save.Expenses;
        }

        // A ship launched any other way starts fresh.
        internal static void ClearPending() => sPendingRuntime = null;

        // On entering gameplay (the shift clock has just been created for the level).
        internal static void OnGameplayStarted()
        {
            if (sPendingRuntime == null) return;
            if (!FreePlayDamage.IsFreePlay) { sPendingRuntime = null; return; }
            var runtime = sPendingRuntime.Value;
            sPendingRuntime = null;

            var entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (entityManager.TryGetSingletonComponent<GameSessionTimerData>(out var timer) && timer.TimerCountsUp)
            {
                timer.CurrentTime = runtime;
                timer.PreviousTime = runtime;
                // The count-up clock ends the shift at MaxTime (10 h); keep a loaded run clear of it.
                if ((float)timer.MaxTime < runtime + 36000f) timer.MaxTime = runtime + 36000f;
                entityManager.TrySetSingletonComponent(timer);
            }
            var credits = Credits();
            if (credits != null && sPendingExpenses > 0f && credits.SpentAmount < sPendingExpenses)
            {
                credits.Subtract(sPendingExpenses - credits.SpentAmount); // same effect as having bought it here
                RefreshCurrencyDisplays(credits.CurrencyAssetID);
            }
            Plugin.Log.LogInfo($"Restored FreePlay run: clock {FormatRuntime(runtime)}, expenses ${sPendingExpenses:N2}.");
        }

        // Currency displays (the kiosk header among them) only redraw on a currency event or when they
        // start; the kiosk's has already started, so redraw it without posting a fake purchase.
        private static void RefreshCurrencyDisplays(AssetTypeID<CurrencyAsset> currencyId)
        {
            var update = AccessTools.Method(typeof(CurrencyUIController), "UpdateElement");
            if (update == null) return;
            foreach (var display in Resources.FindObjectsOfTypeAll<CurrencyUIController>())
            {
                if (!display.gameObject.scene.IsValid()) continue; // prefab assets
                try { update.Invoke(display, new object[] { currencyId }); }
                catch (System.Exception ex) { Plugin.Debug($"Currency display refresh failed: {ex.GetBaseException().Message}"); }
            }
        }

        // 07m 05s, 59m 59s, 1h 02m 03s (letters, so it doesn't read like the save timestamp's HH:mm).
        internal static string FormatRuntime(float seconds)
        {
            var total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            var h = total / 3600;
            var m = total / 60 % 60;
            var s = total % 60;
            return h > 0 ? $"{h}h {m:00}m {s:00}s" : $"{m:00}m {s:00}s";
        }
    }

    // NoCredit: in FreePlay a kiosk purchase is refused when it would take the run's expenses past
    // the credit value salvaged from this ship so far (ShipCurrentSalvageProgress, which a loaded
    // save restores along with the expenses). Refused items show as stock "insufficient currency"
    // and clicking them plays the stock denied sound.
    internal static class NoCredit
    {
        private static readonly System.Reflection.FieldInfo? sState = AccessTools.Field(typeof(VendingMachineItemUI), "mButtonState");
        private const int kNormal = 0, kInsufficientCurrency = 1; // VendingButtonState

        internal static float SalvagedValue
        {
            get
            {
                var world = World.DefaultGameObjectInjectionWorld;
                var manifest = ModuleService.Instance != null ? ModuleService.Instance.CurrentManifest : Entity.Null;
                if (world == null || manifest == Entity.Null || !world.EntityManager.Exists(manifest) ||
                    !world.EntityManager.HasComponent<ShipCurrentSalvageProgress>(manifest))
                    return 0f;
                return world.EntityManager.GetComponentData<ShipCurrentSalvageProgress>(manifest).CurrentSalvagedCreditValue;
            }
        }

        internal static bool Refuses(VendingMachineItemUI item)
        {
            if (!Plugin.NoCredit.Value || !FreePlayDamage.IsFreePlay) return false;
            var data = Traverse.Create(item).Field("mData").GetValue<VendingMachineItemData>();
            if (data == null || data.Cost <= 0f) return false;
            return FreePlayRun.Expenses + data.Cost > SalvagedValue + 0.005f;
        }

        internal static void Deny(VendingMachineItemUI item)
        {
            var data = Traverse.Create(item).Field("mData").GetValue<VendingMachineItemData>();
            Plugin.Debug($"Kiosk purchase refused (NoCredit): cost {data?.Cost:N2}, expenses {FreePlayRun.Expenses:N2}, salvaged {SalvagedValue:N2}.");
            try
            {
                if (data?.Currency != null) Main.EventSystem.Post(new CurrencyActionFailedEvent(data.Currency));
                var sound = Traverse.Create(item).Field("m_OnPurchaseDeniedAudio").Property("WwiseObjectIDWrapper").Property("ShortID").GetValue();
                if (sound != null && AccessTools.Method(typeof(MasterSFXEvent), "GetEvent").Invoke(null, new[] { sound }) is MasterSFXEvent ev)
                    Main.EventSystem.Post(ev);
            }
            catch (System.Exception ex) { Plugin.Debug($"NoCredit denied feedback failed: {ex.Message}"); }
        }

        // After stock UpdateState: show a refused item the way stock shows one you can't afford.
        internal static void ApplyState(VendingMachineItemUI item)
        {
            if (sState == null || System.Convert.ToInt32(sState.GetValue(item)) != kNormal || !Refuses(item)) return;
            sState.SetValue(item, System.Enum.ToObject(sState.FieldType, kInsufficientCurrency));
            var t = Traverse.Create(item);
            var selected = t.Field("m_SelectedColors").GetValue<Color[]>();
            var deselected = t.Field("m_DeselectedColors").GetValue<Color[]>();
            if (deselected == null || deselected.Length <= kInsufficientCurrency) return;
            var cost = t.Field("m_CostLabel").GetValue<TextMeshProUGUI>();
            if (cost != null) cost.color = deselected[kInsufficientCurrency];
            var name = t.Field("m_NameLabel").GetValue<LocalizedTextMeshProUGUI>();
            if (name?.TMProText != null)
                name.TMProText.color = item.IsSelected && selected != null && selected.Length > kInsufficientCurrency
                    ? selected[kInsufficientCurrency] : deselected[kInsufficientCurrency];
        }
    }

    [HarmonyPatch(typeof(VendingMachineItemUI), nameof(VendingMachineItemUI.UpdateState))]
    internal static class VendingMachineItemUI_UpdateState_Patch
    {
        private static void Postfix(VendingMachineItemUI __instance) => NoCredit.ApplyState(__instance);
    }

    [HarmonyPatch(typeof(GameSession), "UpdateCurrentGameState")]
    internal static class GameSession_UpdateCurrentGameState_Patch
    {
        private static void Postfix(GameSession.GameState newGameState)
        {
            if (newGameState == GameSession.GameState.Gameplay) FreePlayRun.OnGameplayStarted();
        }
    }

    // FreePlay's Shift Summary: an "EXPENSES" total at the bottom right, on the Credits Earned line.
    // It's a copy of the Credits Earned value text (same font and style), placed at the right edge of
    // the first ancestor wide enough to span the line.
    [HarmonyPatch(typeof(PostMissionScreen), nameof(PostMissionScreen.ActivatePostMissionScreen))]
    internal static class PostMissionScreen_ActivatePostMissionScreen_Patch
    {
        private const string kName = "FreePlayPlusExpenses";

        private static void Postfix(PostMissionScreen __instance)
        {
            if (!FreePlayDamage.IsFreePlay) return;
            try
            {
                var earned = Traverse.Create(__instance).Field("m_ValueEarned").GetValue<TextMeshProUGUI>();
                if (earned == null) return;
                var earnedRect = (RectTransform)earned.transform;
                var canvas = earned.canvas != null ? earned.canvas.rootCanvas : null;
                var canvasWidth = canvas != null ? ((RectTransform)canvas.transform).rect.width : 0f;

                var line = earnedRect.parent as RectTransform;
                while (line != null && line.parent is RectTransform up && line.rect.width < canvasWidth * 0.5f) line = up;
                if (line == null) return;
                if (Plugin.DebugLogging.Value)
                {
                    for (var t = earnedRect; t != null; t = t.parent as RectTransform)
                        Plugin.Log.LogInfo($"[Expenses] {t.name} rect={t.rect} anchors={t.anchorMin}-{t.anchorMax}{(t == line ? "  <- placed here" : "")}");
                }

                var existing = line.Find(kName);
                TextMeshProUGUI label;
                if (existing != null) label = existing.GetComponent<TextMeshProUGUI>();
                else
                {
                    label = Object.Instantiate(earned, line);
                    label.name = kName;
                    var element = label.GetComponent<LayoutElement>() ?? label.gameObject.AddComponent<LayoutElement>();
                    element.ignoreLayout = true;
                    var rect = (RectTransform)label.transform;
                    rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
                    rect.pivot = new Vector2(1f, 0.5f);
                    rect.sizeDelta = new Vector2(520f, Mathf.Max(earnedRect.rect.height, 30f));
                    var y = line.InverseTransformPoint(earnedRect.TransformPoint(earnedRect.rect.center)).y;
                    rect.localPosition = new Vector3(line.rect.xMax - 24f, y, 0f);
                    label.alignment = TextAlignmentOptions.Right;
                    label.enableWordWrapping = false;
                    label.color = new Color(0.92f, 0.78f, 0.3f, 1f);
                }
                label.text = $"EXPENSES   <color=#FF5A4A>-${FreePlayRun.Expenses:N2}</color>";
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"Couldn't add Expenses to the shift summary: {ex.Message}"); }
        }
    }
}
