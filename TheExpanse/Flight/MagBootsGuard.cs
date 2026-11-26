using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace TheExpanse
{
    // With the MagBoots mod also installed: while the drive pulls aboard (or the deck boots hold), MagBoots is kept from
    // locking on, so the two never hold the suit at once. Nothing of MagBoots is patched or changed: its plugin is just
    // paused (not run each frame) while the pull lasts, and only when its boots are off at that moment; it runs again
    // as soon as the pull stops. If its boots are on when the drive lights, it is left alone.
    internal static class MagBootsGuard
    {
        const string MagBootsGuid = "me.kerballone.MagBoots";

        static bool sLooked;
        static Behaviour? sPlugin;
        static System.Reflection.FieldInfo? sController;
        static System.Reflection.PropertyInfo? sState;
        static bool sPaused;

        internal static void Tick()
        {
            if (!sLooked)
            {
                sLooked = true;
                if (Chainloader.PluginInfos.TryGetValue(MagBootsGuid, out var info) && info.Instance != null)
                {
                    sPlugin = info.Instance;
                    sController = AccessTools.Field(sPlugin.GetType(), "_controller");
                    var controllerType = sController?.FieldType;
                    sState = controllerType != null ? AccessTools.Property(controllerType, "State") : null;
                    Plugin.Log.LogInfo($"[Flight] MagBoots is installed: it is paused while the drive pulls aboard{(sState == null ? " (its state could not be read: it is paused only while it is off)" : "")}.");
                }
            }
            if (sPlugin == null) return;

            bool pull = Plugin.Enabled.Value && (FlightModel.PullActive || DeckBoots.Attached);
            if (pull && !sPaused)
            {
                if (!IsOff()) return;          // locked on (or locking): leave it be
                sPlugin.enabled = false;
                sPaused = true;
                Plugin.Log.LogInfo("[Flight] MagBoots paused while the drive pulls aboard.");
            }
            else if (!pull && sPaused) Resume();
        }

        static bool IsOff()
        {
            if (sController == null || sState == null) return true;
            var controller = sController.GetValue(sPlugin);
            if (controller == null) return true;
            var state = sState.GetValue(controller)?.ToString();
            return state == null || state == "Off" || state == "NoPower" || state == "Error";
        }

        internal static void Resume()
        {
            if (!sPaused) return;
            sPaused = false;
            if (sPlugin != null) sPlugin.enabled = true;
            Plugin.Log.LogInfo("[Flight] MagBoots running again.");
        }
    }
}
