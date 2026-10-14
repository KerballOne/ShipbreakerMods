using System.Collections;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace FreePlayPlus
{
    // In career a death costs a clone fee, then you respawn on the same ship. FreePlay has no
    // currency, so with ExitToMenuOnDeath the death ends the run instead: acknowledging the
    // game-over report goes back to the FreePlay ship select rather than respawning.
    //
    // Same route the game uses for a last-life death (LoadFrontEndEvent from the GameOver state ->
    // SceneLoader.TearDownAndLoadFrontEndAsync), minus the profile deletion. Loading the front end
    // also restores the career profile FreePlay stashed (PlayerProfileService.mStoredProfile), whose
    // profile button FrontEndAutoNavigator.NavigateToFreePlay then clicks, followed by FreePlay.
    // TeleportUtils.Teleport(FreeModeScreen) isn't used: it refuses while the session is FreePlay.
    [HarmonyPatch(typeof(GameOverMessageController), nameof(GameOverMessageController.RespawnPlayer))]
    internal static class GameOverMessageController_RespawnPlayer_Patch
    {
        private const float kTimeoutSeconds = 60f;

        private static bool Prefix(GameOverMessageController __instance)
        {
            if (!Plugin.ExitToMenuOnDeath.Value || !FreePlayDamage.IsFreePlay) return true;

            Traverse.Create(__instance).Field("mGameOverIsActive").SetValue(false);
            Plugin.Log.LogInfo("FreePlay death: returning to the FreePlay ship select.");
            Main.EventSystem.Post(LoadFrontEndEvent.GetEvent());
            Plugin.Instance.StartCoroutine(NavigateToFreePlayWhenReady());
            return false;
        }

        private static IEnumerator NavigateToFreePlayWhenReady()
        {
            // Realtime: timeScale can be 0 around the game-over screen and loading.
            var waited = 0f;
            yield return new WaitForSecondsRealtime(0.5f);
            while (!FrontEndReady())
            {
                if (waited > kTimeoutSeconds)
                {
                    Plugin.Log.LogWarning("Timed out waiting for the main menu; staying on it.");
                    yield break;
                }
                waited += 0.25f;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            // Matches TeleportUtils' settle delay before navigating.
            yield return new WaitForSecondsRealtime(1f);

            var profile = PlayerProfileService.Instance?.Profile;
            if (profile == null || profile.PlayerProfileType != PlayerProfileType.Career)
            {
                Plugin.Log.LogWarning($"No career profile active after returning to the menu ({profile?.PlayerProfileType}); staying on the main menu.");
                yield break;
            }

            var navigate = SingletonMonoBehaviour<FrontEndAutoNavigator>.Instance.NavigateToFreePlay(profile);
            while (!navigate.IsCompleted) yield return null;
            if (navigate.IsFaulted || !navigate.Result)
                Plugin.Log.LogWarning($"Couldn't open the FreePlay ship select automatically ({(navigate.IsFaulted ? navigate.Exception?.GetBaseException().Message : "navigation failed")}); staying on the main menu.");
        }

        private static bool FrontEndReady() =>
            SceneLoader.Instance != null &&
            !SceneLoader.Instance.IsExecutingTearDownAndLoad &&
            GameSession.CurrentGameState == GameSession.GameState.None &&
            SingletonMonoBehaviour<FrontEndAutoNavigator>.HasInstance;
    }
}
