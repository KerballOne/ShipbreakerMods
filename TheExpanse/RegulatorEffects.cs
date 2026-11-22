using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Events;

namespace TheExpanse
{
    // Keeps an atmosphere regulator's hiss and vent effect going for as long as a slowed pressure change lasts.
    //
    // A regulator does not know how long a change takes. When its room starts a controlled pressurization or
    // decompression, its room probe fires a list of actions set up in the prefab: play a sound ('atmos_pressurize'),
    // speak a line, and send an event to the vent's visual effect. The sound and the effect are fixed lengths, made
    // for the game's own few seconds, so on a room that RoomPressure has slowed down they finish long before the
    // pressure does.
    //
    // Neither can be stretched, but both can be started again. While a slowed change runs, RoomPressure asks for the
    // sound and the effect to be fired again at even intervals, the last one timed to end with the change. The spoken
    // line is not repeated.
    internal static class RegulatorEffects
    {
        static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Fires the sound and effect actions of every regulator in these rooms again. Returns how many were fired.
        internal static int Repeat(HashSet<Entity> rooms, int direction)
        {
            int fired = 0;
            var wanted = direction > 0 ? RoomPressurization.ControlledPressurizing : RoomPressurization.ControlledDecompressing;
            foreach (var probe in UnityEngine.Object.FindObjectsOfType<RoomProbe>())
            {
                if (probe == null || !probe.IsAtmosphereRegulator || !rooms.Contains(probe.RoomEntity)) continue;
                if (!(Traverse.Create(probe).Field("m_Triggers").GetValue() is IList triggers)) continue;
                foreach (var trigger in triggers)
                {
                    if (trigger == null) continue;
                    var t = Traverse.Create(trigger);
                    if (!(t.Field("m_Pressurization").GetValue() is RoomPressurization state) || state != wanted) continue;
                    if (t.Field("m_OnTrigger").GetValue() is UnityEvent actions) fired += FireSoundAndEffect(actions);
                }
            }
            return fired;
        }

        // Of the actions on a trigger, calls again only the ones that play a sound or drive a visual effect.
        static int FireSoundAndEffect(UnityEvent actions)
        {
            int fired = 0;
            var persistent = typeof(UnityEventBase).GetField("m_PersistentCalls", Any)?.GetValue(actions);
            if (!(persistent?.GetType().GetField("m_Calls", Any)?.GetValue(persistent) is IList calls)) return 0;
            for (int i = 0; i < actions.GetPersistentEventCount() && i < calls.Count; i++)
            {
                var target = actions.GetPersistentTarget(i);
                string method = actions.GetPersistentMethodName(i);
                if (target == null || calls[i] == null) continue;
                bool sound = target.GetType().Name == "TriggerableAudioTableAction" && method == "TriggerAudioEvent";
                bool effect = target.GetType().Name == "VisualEffect" && method == "SendEvent";
                if (!sound && !effect) continue;
                if (target is Behaviour b && !b.isActiveAndEnabled) continue;      // a regulator that has been cut out or switched off

                var arguments = calls[i].GetType().GetField("m_Arguments", Any)?.GetValue(calls[i]);
                if (!(arguments?.GetType().GetField("m_StringArgument", Any)?.GetValue(arguments) is string name) || name.Length == 0) continue;
                var call = target.GetType().GetMethod(method, Any, null, new[] { typeof(string) }, null);
                if (call == null) continue;
                try { call.Invoke(target, new object[] { name }); fired++; }
                catch (Exception ex) { Plugin.Log.LogWarning($"[RoomPressure] could not repeat '{name}' on a regulator: {(ex.InnerException ?? ex).Message}"); }
            }
            return fired;
        }
    }
}
