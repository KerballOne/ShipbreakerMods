using BBI.Unity.Game;
using Doozy.Engine.UI;
using HarmonyLib;
using System.Collections.Generic;
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

        // Direct fallback for greeting-gated completion PATs that skip can silently drop.
        //
        // NARCON_Message_LynxInternal_UnionClampDown -> PAT_CMP_17_X2_LYNXUnionClampDown_Complete:
        // investigated extensively 2026-07-08, found no code path, timer, or UI-state signal that
        // posts this PAT -- it's not HABGreetingPopupReadPAT (null on this asset), not tied to
        // mRemainingTime/btnInteractable in any way safely hookable, and not owned by the
        // speech/subtitle systems (entirely separate from HabCustomGreetingController). Skipping via
        // Hide() permanently loses whatever normally posts it (reproduced heidi's real save gap).
        //
        // NARCON_Message_LynxInternal_IncomingTransmission -> PAT_CMP_07_00_CalyssiaAntiUnion_Complete:
        // also confirmed 2026-07-08. Different mechanism than 17_X2 -- Calyssia's dialogue is a
        // separate speech/subtitle sequence (LynxSpeechEventController) layered UNDER this greeting
        // popup; mNarrativeAsset never changes during her speech, and the PAT posts mid-sequence while
        // the IncomingTransmission greeting is still showing (confirmed live via continuous
        // mNarrativeAsset polling in PartInfoLogger, independent of skip). If skip closes this
        // greeting via Hide() before Calyssia's speech finishes, the same class of loss as 17_X2
        // applies -- so gate on this greeting's asset the same way, even though the PAT's real trigger
        // is technically a different subsystem.
        //
        // Both looked up once via Resources.FindObjectsOfTypeAll and cached, since
        // PlayerActionTrackerAsset instances are static game data, not per-greeting.
        static readonly (string MessageName, string PatName)[] s_directPostGreetings =
        {
            ("NARCON_Message_LynxInternal_UnionClampDown", "PAT_CMP_17_X2_LYNXUnionClampDown_Complete"),
            ("NARCON_Message_LynxInternal_IncomingTransmission", "PAT_CMP_07_00_CalyssiaAntiUnion_Complete"),
        };
        static readonly Dictionary<string, PlayerActionTrackerAsset?> s_cachedDirectPostPats = new();

        private static void PostDirectFallbackPatIfNeeded(NarrativeMessageAsset? narrativeAsset)
        {
            if (narrativeAsset == null) return;

            foreach (var (messageName, patName) in s_directPostGreetings)
            {
                if (narrativeAsset.name != messageName) continue;

                if (!s_cachedDirectPostPats.TryGetValue(patName, out var pat) || pat == null)
                {
                    pat = null;
                    foreach (var candidate in Resources.FindObjectsOfTypeAll<PlayerActionTrackerAsset>())
                    {
                        if (candidate.name == patName) { pat = candidate; break; }
                    }
                    s_cachedDirectPostPats[patName] = pat;
                }

                if (pat == null)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogWarning($"QuickCutscene: skip pressed — could not find PlayerActionTrackerAsset '{patName}'");
                    return;
                }

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"QuickCutscene: skip pressed — directly posting {patName}");
                Main.EventSystem.Post(PlayerActionTrackerEvent.GetEvent(pat, MathUtility.OperationType.Add, 1));
                return;
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

                var t3 = Traverse.Create(__instance);
                var narrativeAsset = t3.Field("mNarrativeAsset").GetValue<NarrativeMessageAsset>();
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"QuickCutscene: skip pressed — mNarrativeAsset={(narrativeAsset != null ? narrativeAsset.name : "null")}");
                PostDirectFallbackPatIfNeeded(narrativeAsset);

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

        // Postfix on TryStartAfterShiftSequence — fires once when a scene begins.
        // For UnityEvent scenes, mPendingEndScenePAT is set to true here, which blocks Update()
        // from reaching ProcessAutomationControls. We set IsSkippable directly so the hint appears.
        // ProcessAutomationControls clears IsSkippable naturally once the scene ends (currentData→null).
        [HarmonyPatch(typeof(Hab3DController), "TryStartAfterShiftSequence")]
        public class Hab3DController_TryStartAfterShiftSequence
        {
            public static void Postfix(Hab3DController __instance)
            {
                var t           = Traverse.Create(__instance);
                var currentData = t.Field("mCurrentAfterShiftData").GetValue<HabAfterShiftAsset>();
                bool pending    = t.Field("mPendingEndScenePAT").GetValue<bool>();

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"QuickCutscene: [TryStart Postfix] scene={currentData?.name} type={currentData?.SceneType} pending={pending}");

                if (pending && currentData?.SceneType == HabAfterShiftAsset.AfterShiftSceneType.UnityEvent)
                {
                    Plugin.IsSkippable = true;
                    Plugin.PendingUnityEventController = __instance;
                }
            }
        }

        [HarmonyPatch(typeof(Hab3DController), "ProcessAutomationControls")]
        public class Hab3DController_ProcessAutomationControls
        {
            private static int s_logThrottle;

            public static void Postfix(Hab3DController __instance)
            {
                var t = Traverse.Create(__instance);

                bool isInAfterShift = t.Field("mIsInAfterShiftHab").GetValue<bool>();
                bool hasFinished    = t.Field("mHasFinsishedAfterShiftSequence").GetValue<bool>();
                var  currentData    = t.Field("mCurrentAfterShiftData").GetValue<HabAfterShiftAsset>();

                Plugin.IsSkippable = isInAfterShift && currentData != null && !hasFinished;

                if (Plugin.ConfigDebugPrint.Value && isInAfterShift && ++s_logThrottle % 60 == 0)
                    Plugin.Log.LogInfo($"QuickCutscene: [PAC Postfix] scene={currentData?.name} type={currentData?.SceneType} hasFinished={hasFinished} IsSkippable={Plugin.IsSkippable}");

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
