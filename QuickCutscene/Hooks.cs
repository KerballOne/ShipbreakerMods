using BBI.Unity.Game;
using Doozy.Engine.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Playables;

namespace QuickCutscene
{
    public class Hooks
    {
        [HarmonyPatch(typeof(GameSession), "UpdateCurrentGameState")]
        public class GameSession_UpdateCurrentGameState
        {
            public static void Postfix(GameSession.GameState newGameState)
            {
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"QuickCutscene: GameState → {newGameState} (prev={GameSession.PrevGameState})");
                else if (newGameState == GameSession.GameState.NIS || GameSession.PrevGameState == GameSession.GameState.NIS)
                    Plugin.Log.LogInfo($"QuickCutscene: NIS {(newGameState == GameSession.GameState.NIS ? "started" : "ended → " + newGameState)}");
            }
        }

        // Patching ProcessAutomationControls rather than Update because:
        //   1. It's called at the end of Update, so the timer logic has already run for this frame.
        //   2. It's empty in the shipped binary — clearly designed as an extension point.
        //   3. Setting mWaitTimeForFade = float.MaxValue here means the NEXT Update() frame sees the
        //      timer as expired and fires all normal completion logic (SceneWatchedPAT, transitions, etc.)
        //      without us having to duplicate any of that logic ourselves.
        [HarmonyPatch(typeof(HabCustomGreetingController), "Update")]
        public class HabCustomGreetingController_Update
        {
            public static void Postfix(HabCustomGreetingController __instance)
            {
                if (!HabCustomGreetingController.HabGreetingShowing) return;

                if (Plugin.ConfigDebugPrint.Value)
                {
                    var t2 = Traverse.Create(__instance);
                    var btn = t2.Field("m_ContinueButton").GetValue<UIButton>();
                    float rem = t2.Field("mRemainingTime").GetValue<float>();
                    Plugin.Log.LogInfo($"QuickCutscene: greeting — remainingTime={rem:F2} btnActive={btn?.gameObject.activeInHierarchy} btnInteractable={btn?.Interactable} shouldSkip={Plugin.ShouldSkip()}");
                }

                if (!Plugin.ShouldSkip()) return;

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("QuickCutscene: skip pressed — calling Hide() + unblocking all blocked actions");
                __instance.Hide();
                var absT = Traverse.Create(ActionBlockerService.Instance);
                var blocked = absT.Field("mSavedBlockedBindings")
                                  .GetValue<System.Collections.Generic.HashSet<LynxControls.ActionSetAndId>>();
                if (blocked != null)
                {
                    var toUnblock = new System.Collections.Generic.List<LynxControls.ActionSetAndId>(blocked);
                    foreach (var action in toUnblock)
                    {
                        if (Plugin.ConfigDebugPrint.Value)
                            Plugin.Log.LogInfo($"QuickCutscene: unblocking action {action.ActionId}");
                        ActionBlockerService.Instance.UnBlockAction(action, removeDoubleBlock: true);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(Hab3DController), "ProcessAutomationControls")]
        public class Hab3DController_ProcessAutomationControls
        {
            public static void Postfix(Hab3DController __instance)
            {
                var t = Traverse.Create(__instance);

                bool isInAfterShift = t.Field("mIsInAfterShiftHab").GetValue<bool>();
                bool hasFinished    = t.Field("mHasFinsishedAfterShiftSequence").GetValue<bool>();
                var  currentData    = t.Field("mCurrentAfterShiftData").GetValue<HabAfterShiftAsset>();

                Plugin.IsSkippable = isInAfterShift && currentData != null && !hasFinished;

                if (!Plugin.IsSkippable || !Plugin.ShouldSkip())
                    return;

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"QuickCutscene: skipping '{currentData!.name}' (type={currentData.SceneType})");

                // Advance the wait timer past the sequence duration + TimeAfterSpeechPlays.
                // Update() will see this on the next frame and run its normal completion block:
                // posting SceneWatchedPAT, firing GoToFinishAfterShift, clearing mCurrentAfterShiftData.
                t.Field("mWaitTimeForFade").SetValue(float.MaxValue);

                // For UnityEvent-type scenes Update() returns early while waiting for EndScenePAT.
                // Clear the flag so it doesn't stall.
                t.Field("mPendingEndScenePAT").SetValue(false);

                // Stop a running PlayableDirector timeline so it doesn't keep playing in the background.
                var directorEBC = t.Field("mAttachedPlayableDirectorEBC").GetValue<Component>();
                if (directorEBC != null)
                {
                    var director = directorEBC.gameObject.GetComponent<PlayableDirector>();
                    director?.Stop();
                }
            }
        }
    }
}
