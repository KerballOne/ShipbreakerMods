using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace NewtonianPhysics
{
    // Forces the rail gate laser-fire sequence to start on demand, bypassing RailGateBehaviour's
    // normal gating (random per-shift chance/delay rolls - see project_gatefx_investigation in
    // memory). Bound to Plugin.ConfigFireGateBeamKey (default F7) or, while a controller is the
    // active input device, Plugin.ConfigFireGateBeamBtn (default None/unbound) - useful for
    // testing how GateCard/the beam VFX (FlatCardBillboarding's angle/distance pinning) look
    // without waiting for a real, rare gate visit.
    internal static class GateFXDebugTrigger
    {
        private const float DebugStepIntervalSeconds = 2f;

        // Same controller-vs-keyboard detection MagBoots uses - only reads the controller binding
        // while a controller is actually the last-used input device, so the keyboard binding still
        // works normally otherwise even if a controller button happens to be configured.
        private static bool IsControllerActive =>
            LynxControls.Instance != null &&
            LynxControls.Instance.LastInputType == BindingSourceType.DeviceBindingSource;

        private static bool ShouldFire()
        {
            if (IsControllerActive && Plugin.ConfigFireGateBeamBtn.Value != InputControlType.None)
                return InputManager.ActiveDevice?[Plugin.ConfigFireGateBeamBtn.Value].WasPressed ?? false;
            return Plugin.ConfigFireGateBeamKey.Value.IsDown();
        }

        public static void Tick()
        {
            if (!ShouldFire())
                return;

            // Same _HAB-duplicate-aware whole-scene search pattern used throughout this mod
            // (FlatCardBillboarding, BackgroundParallaxTuning) - GateCard has a confirmed inactive
            // duplicate under PRF_Village_HAB, so name + activeInHierarchy alone isn't reliable.
            RailGateBehaviour? railGate = Resources.FindObjectsOfTypeAll<RailGateBehaviour>()
                .FirstOrDefault(r => r != null && r.gameObject.activeInHierarchy && !GetFullPath(r.transform).Contains("_HAB"));

            if (railGate == null)
            {
                Plugin.Log.LogInfo("GateFXDebugTrigger: no active (non-_HAB) RailGateBehaviour found.");
                return;
            }

            // RailGateBehaviour.Start() sets base.enabled=false whenever the normal random
            // per-shift roll decides the gate won't visit this shift (mAllowRailGateSpawn=false or
            // mRailGateWillVisit=false) - once disabled, Unity never calls Update() on this
            // component again, so StartRailGateAnimation() alone (which only flips internal fields,
            // never touches `enabled`) is silently inert if the component is currently disabled.
            // CRITICALLY, EndRailGateAnimation() (called automatically by Update() once the
            // sequence's last step plays) does the SAME thing again UNLESS
            // m_AllowMultipleTimesInOneShift is true for this level's GateCard instance - confirmed
            // via testing that a second F7 press after a successful first fire produced no visible
            // effect and no "was disabled" log line (proving `enabled` silently went false again
            // from the sequence's own natural end, not from the original spawn-roll gate). So this
            // must force `enabled=true` on EVERY press, not just the first.
            if (!railGate.enabled)
            {
                Plugin.Log.LogInfo("GateFXDebugTrigger: RailGateBehaviour was disabled (spawn roll or a previous sequence's own EndRailGateAnimation) - force re-enabling.");
                railGate.enabled = true;
            }

            var traverse = Traverse.Create(railGate);

            // mCurrentSequenceStep must be back at 0 before a repeat fire - EndRailGateAnimation()
            // does reset this normally, but force it defensively in case a prior forced fire was
            // interrupted (e.g. by a shift change) before it could complete naturally.
            traverse.Field("mCurrentSequenceStep").SetValue(0);

            // SequenceStepDelay values are CUMULATIVE time-from-sequence-start, not per-step
            // durations (see Update(): each step's own SequenceStepDelay is compared directly
            // against mSequenceDuration - mSequenceDurationRemianing, the elapsed time since the
            // sequence began) - so they must stay monotonically non-decreasing across the list for
            // Update()'s step-by-step comparisons to ever succeed. An earlier version of this fix
            // only capped delays ABOVE 2s and left smaller ones untouched, which could break that
            // ordering (e.g. step[2]'s original 1.5s delay left alone while step[3]'s was clamped
            // down to 2s, isn't actually broken, but step[2] AFTER a clamped-down step[1] could end
            // up numerically ahead of where it should be) - confirmed via testing that this silently
            // prevented the sequence from ever completing a step. Rewriting ALL delays to a clean,
            // strictly increasing schedule (DebugStepIntervalSeconds apart) guarantees correctness
            // regardless of the original authored values. SequenceStep is a private nested struct
            // inside a List<SequenceStep> - structs in a List aren't addressable in place via
            // reflection, so each element must be read, modified, and written back by index.
            var sequenceSteps = traverse.Field("m_SequenceSteps").GetValue<System.Collections.IList>();
            if (sequenceSteps != null)
            {
                for (int i = 0; i < sequenceSteps.Count; i++)
                {
                    object step = sequenceSteps[i]!;
                    var stepTraverse = Traverse.Create(step);
                    stepTraverse.Field("SequenceStepDelay").SetValue((i + 1) * DebugStepIntervalSeconds);
                    sequenceSteps[i] = step;
                }
                // mSequenceDuration is derived from the LAST step's delay (see RailGateBehaviour.Start())
                // - recompute it the same way so Update()'s own math
                // (mSequenceDurationRemianing <= mSequenceDuration - stepDelay) stays internally
                // consistent with the now-rewritten delays.
                object lastStep = sequenceSteps[sequenceSteps.Count - 1]!;
                float newDuration = Traverse.Create(lastStep).Field("SequenceStepDelay").GetValue<float>();
                traverse.Field("mSequenceDuration").SetValue(newDuration);
            }

            Plugin.Log.LogInfo($"GateFXDebugTrigger: forcing StartRailGateAnimation() on path={GetFullPath(railGate.transform)} (step delays rewritten to {DebugStepIntervalSeconds}s apart)");
            railGate.StartRailGateAnimation();
        }

        private static string GetFullPath(Transform t)
        {
            string path = t.name;
            Transform? parent = t.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
