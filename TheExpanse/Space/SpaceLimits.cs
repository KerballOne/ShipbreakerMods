using System.Collections.Generic;
using BBI;
using BBI.Unity.Game;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace TheExpanse
{
    // Room to fly: the bay's limits lifted for the pilot and for the ship, and the sky kept right far from the bay.
    //
    //   The ship: the game destroys any piece that stays outside its object limit (130 m from the work-area nodes) for
    //             a few seconds, which is what took the whole ship apart at the boundary. That limit is scaled by
    //             [Space] WorkAreaRadiusMultiplier (0 = none), and lifted entirely while the ship is under way. It
    //             covers every loose piece too, not just the ship.
    //   The pilot: the work-area warning and danger zone around the player, scaled the same way, and while seated the
    //             player's speed cap is lifted (the pilot rides the ship at whatever speed it goes).
    //   The sky:  the celestial bodies and the flat background cards pinned as the player leaves the bay, and the far
    //             clip plane pushed out (BackgroundParallaxTuning, PlanetGlowFadeTuning, FlatCardBillboarding).
    //
    // The pilot's zone and the sky are what the NewtonianPhysics mod already does; copies of its code are here so the
    // flight controls work without it, and they stand down when it is installed (its own settings then apply). The
    // ship's limit and the seated speed cap are TheExpanse's own and always apply.
    internal static class SpaceLimits
    {
        const string NewtonianGuid = "me.kerballone.NewtonianPhysics";
        const float NoLimit = 1e9f;
        const float FarClipPlaneMeters = 99999f;

        static bool? sNewtonian;

        // Asked only after every plugin has loaded (from Start or later).
        internal static bool NewtonianInstalled => sNewtonian ??= Chainloader.PluginInfos.ContainsKey(NewtonianGuid);

        static bool On => Plugin.Enabled.Value && Plugin.SpaceLimits.Value;
        internal static bool PinningOn => On && !NewtonianInstalled;

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(SeatedSpeed));
            Patch(harmony, typeof(NoCrushAboard));
            Patch(harmony, typeof(SeatedStill));
            Patch(harmony, typeof(ShipFrame));
            if (NewtonianInstalled)
                Plugin.Log.LogInfo("[Space] NewtonianPhysics is installed: it handles the player's work area and the sky; TheExpanse " +
                                   "handles the ship's limit and the pilot's speed.");
            else Patch(harmony, typeof(PlayerArea));
        }

        static void Patch(Harmony harmony, System.Type type)
        {
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (System.Exception ex) { Plugin.Log.LogError($"[Space] {type.Name} could not be patched; that part is off: {ex.GetBaseException().Message}"); }
        }

        // New shift: the copied pinning code holds on to the last shift's objects otherwise.
        internal static void OnGameStateChanged(GameStateChangedEvent ev)
        {
            if (ev.GameState == GameSession.GameState.Gameplay && ev.PrevGameState == GameSession.GameState.LoadingComplete)
            {
                BackgroundParallaxTuning.ResetState();
                PlanetGlowFadeTuning.ResetState();
                FlatCardBillboarding.ResetState();
            }
        }

        // Every frame (Update).
        internal static void Tick()
        {
            ObjectLimit();
            if (!PinningOn) return;
            var camera = LynxCameraController.MainCamera;
            if (camera != null && camera.farClipPlane != FarClipPlaneMeters) camera.farClipPlane = FarClipPlaneMeters;
        }

        // Every frame, after the camera has moved (LateUpdate).
        internal static void LateTick()
        {
            if (!PinningOn) return;
            BackgroundParallaxTuning.Tick();
            PlanetGlowFadeTuning.Tick();
            FlatCardBillboarding.Tick();
        }

        // How far pieces of salvage may stray from the work area before the game destroys them. The game reads the
        // field straight from its playable-area system, so the field itself is set (from its stock value, kept the
        // first time it is seen).
        static PlayableArea? sArea;
        static float sStockObjectRadius;
        static readonly System.Reflection.FieldInfo sRadiusField = AccessTools.Field(typeof(PlayableArea), "m_ObjectDangerRadius");

        static void ObjectLimit()
        {
            var area = GameSession.PlayableArea;
            if (area == null) return;
            if (area != sArea) { sArea = area; sStockObjectRadius = (float)sRadiusField.GetValue(area); }
            float multiplier = Plugin.SpaceWorkAreaMultiplier.Value;
            float want = FlightModel.Frame != null ? NoLimit                       // the ship under way
                       : !On ? sStockObjectRadius
                       : multiplier <= 0f ? NoLimit : sStockObjectRadius * multiplier;
            if ((float)sRadiusField.GetValue(area) != want) sRadiusField.SetValue(area, want);
        }

        // No speed cap on the player (20 m/s stock), after any other mod has set one: the pilot rides the ship at any
        // speed, and so does anyone walking about in it.
        [HarmonyPatch(typeof(PlayerMotion), "FixedUpdate")]
        static class SeatedSpeed
        {
            [HarmonyPriority(Priority.Last)]
            static void Prefix(PlayerMotion __instance)
            {
                if (FlightSeat.Seated || On) __instance.MaxVelocity = float.MaxValue;
            }
        }

        // Seated, the suit is held in the chair by the flight model; its own step (auto-brake, collision push-back, the
        // speed limit) pulled against that every physics step and shook the seat view. It is skipped while seated.
        [HarmonyPatch(typeof(PlayerMotion), "FixedUpdate")]
        static class SeatedStill
        {
            static bool Prefix() => !FlightSeat.Seated;
        }

        // The game's crushing check calls the player crushed when two moving bodies close in on them; with the ship
        // under way every piece of it is moving, so the pilot (and anyone aboard) was warned and hurt while nothing was
        // closing in. Seated, or aboard a ship under way, the check is skipped and the state kept safe.
        [HarmonyPatch(typeof(PlayerMotion), "HandleCrushing")]
        static class NoCrushAboard
        {
            static readonly System.Reflection.FieldInfo? sBodyField = AccessTools.Field(typeof(PlayerMotion), "mRigidbody");
            static readonly System.Reflection.MethodInfo? sSetState = AccessTools.Method(typeof(PlayerMotion), "SetCrushState");

            static bool Prefix(PlayerMotion __instance)
            {
                bool skip = FlightSeat.Seated;
                if (!skip && FlightModel.UnderWay && sBodyField != null && sBodyField.GetValue(__instance) is Rigidbody body)
                    skip = FlightModel.Aboard(body.position);
                if (!skip) return true;
                sSetState?.Invoke(__instance, new object[] { CrushingThreatPair.Safe });
                return false;
            }
        }

        // Aboard a ship under way, the suit moves relative to the ship: its thrusters, brake, drag and speed limit all
        // work against the ship's motion instead of against the bay. The ship's own speed is taken off before the
        // suit's step and put back after, so whatever the ship gains in that step (the drive burning, a turn) the suit
        // does not: the ship's deck comes up to meet it, which is gravity while the drive burns.
        [HarmonyPatch(typeof(PlayerMotion), "FixedUpdate")]
        static class ShipFrame
        {
            static readonly System.Reflection.FieldInfo? sBodyField = AccessTools.Field(typeof(PlayerMotion), "mRigidbody");

            [HarmonyPriority(Priority.First)]
            static void Prefix(PlayerMotion __instance, out Vector3 __state)
            {
                __state = Vector3.zero;
                if (FlightSeat.Seated || sBodyField == null) return;
                var body = sBodyField.GetValue(__instance) as Rigidbody;
                if (body == null || !FlightModel.Aboard(body.position)) return;
                __state = FlightModel.PointVelocity(body.position);
                body.velocity -= __state;
            }

            [HarmonyPriority(Priority.Last)]
            static void Postfix(PlayerMotion __instance, Vector3 __state)
            {
                if (__state == Vector3.zero || sBodyField == null) return;
                if (sBodyField.GetValue(__instance) is Rigidbody body) body.velocity += __state;
            }
        }

        // The player's own warning and danger zone (copied from NewtonianPhysics; only patched without it).
        [HarmonyPatch(typeof(PlayableArea), "GetPlayableAreaState")]
        static class PlayerArea
        {
            static readonly Dictionary<PlayableArea, (float warning, float danger)> sStock = new Dictionary<PlayableArea, (float, float)>();

            static bool Prefix(PlayableArea __instance, ref PlayableArea.PlayableAreaState __result)
            {
                if (!On) return true;
                var traverse = Traverse.Create(__instance);
                if (!sStock.TryGetValue(__instance, out var stock))
                {
                    stock = (traverse.Field("m_WarningRadius").GetValue<float>(), traverse.Field("m_DangerRadius").GetValue<float>());
                    sStock[__instance] = stock;
                }
                float multiplier = Plugin.SpaceWorkAreaMultiplier.Value;
                if (multiplier <= 0f || FlightSeat.Seated)
                {
                    __result = PlayableArea.PlayableAreaState.Safe;
                    return false;
                }
                traverse.Field("m_WarningRadius").SetValue(stock.warning * multiplier);
                traverse.Field("m_DangerRadius").SetValue(stock.danger * multiplier);
                return true;
            }
        }
    }
}
