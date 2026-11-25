using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace TheExpanse
{
    // Flight Control, step 1: sitting in one of the Corvette's command chairs (PRF_Command_Chair_A_Arm*).
    //   sit:   come within SitDistance of a chair and press Interact (F / X / Square);
    //   seated: the suit is held in the chair (it goes where the chair goes), every suit action is switched off except
    //           Interact and Pause, and the view goes outside the ship (FlightCamera);
    //   view:  V / D-pad right cycles outside, nose, seat;
    //   exit:  Interact again.
    // The flight controls themselves come later.
    internal static class FlightSeat
    {
        const string ChairPrefix = "PRF_Command_Chair_A_Arm";
        const float ExitGrace = 0.4f;           // after sitting, Interact is ignored this long (the same press sat you down)

        static readonly List<Transform> sChairs = new List<Transform>();
        static float sNextChairLook;
        static bool sChairsLogged;
        static int sChairTries;

        static Transform? sSeat;                // the chair sat in
        static float sSatAt;
        static Rigidbody? sBody;
        internal static Rigidbody? PlayerBody => FindBody() ? sBody : null;
        static Collider? sBodyCollider;
        static bool sColliderWasEnabled;
        static readonly Dictionary<PlayerAction, bool> sDisabled = new Dictionary<PlayerAction, bool>();
        static Transform? sNearest;             // the chair in reach, for the prompt

        static Vector3 sStandLocalPosition;     // where the player stood before sitting, in the ship's frame (or the chair's)
        static Quaternion sStandLocalRotation;
        static bool sStandOnFrame;
        static Vector3 sSeatLocalPosition;      // the seat in the ship's frame, while the flight model carries it
        static Quaternion sSeatLocalRotation;

        internal static bool Seated => sSeat != null;
        internal static Transform? Seat => sSeat;

        static bool Allowed => Plugin.Enabled.Value && Plugin.FlightControl.Value && PartSeparation.CustomShip;

        internal static void Reset()
        {
            if (Seated) Stand("level change");
            if (sGiveBackPending) GiveSuitBack();
            FlightModel.Reset();
            sChairs.Clear();
            sChairsLogged = false;
            sChairTries = 0;
            sNextChairLook = 0f;
            sNearest = null;
            sBody = null;
            sBodyCollider = null;
        }

        static bool sGiveBackPending;

        static void GiveSuitBack()
        {
            sGiveBackPending = false;
            Main.EventSystem.Post(ForceEquipEvent.GetEvent(ForceEquipEvent.EquipState.Equip));
            // each action's held state is cleared as it comes back: one held when the seat took it (the Interact press
            // that sat the player down is also the grapple's push) would otherwise come back "released" and fire
            foreach (var pair in sDisabled)
                if (pair.Key != null) { pair.Key.ClearInputState(); pair.Key.Enabled = pair.Value; }
            sDisabled.Clear();
        }

        static bool InteractHeld(LynxControls controls)
        {
            if (controls.GetInputIsPressed(GameplayActions.GameplayActionSet.Interact)) return true;
            var pad = InputManager.ActiveDevice;
            return Input.GetKey(KeyCode.F) || (pad != null && pad.Action3.IsPressed);
        }

        // Every frame (Update).
        internal static void Tick()
        {
            if (sGiveBackPending)
            {
                var c = LynxControls.Instance;
                if (c != null && InteractHeld(c)) { KeepActionsOff(); return; }
                GiveSuitBack();
            }
            if (!Allowed) { if (Seated) Stand("Flight Control turned off"); sNearest = null; return; }
            var controls = LynxControls.Instance;
            if (controls == null || !FindBody()) return;

            if (Seated)
            {
                if (sSeat == null || !sSeat.gameObject.activeInHierarchy) { Stand("the chair is gone"); return; }
                KeepActionsOff();
                if (Time.unscaledTime > sSatAt + ExitGrace && controls.GetInputWasPressed(GameplayActions.GameplayActionSet.Interact))
                {
                    Stand("Interact");
                    return;
                }
                if (ViewTogglePressed()) FlightCamera.CycleView();
                FlightModel.ReadInput();
                return;
            }

            FindChairs();
            sNearest = NearestChair(sBody!.position, Mathf.Max(0.5f, Plugin.FlightSitDistance.Value));
            if (sNearest != null && controls.GetInputWasPressed(GameplayActions.GameplayActionSet.Interact)) Sit(sNearest);
        }

        // Every physics step: the suit stays in the chair.
        internal static void FixedTick()
        {
            FlightModel.FixedStep();
            if (!Seated || sBody == null || sSeat == null) return;
            CurrentSeatPose(out var position, out var rotation);
            sBody.rotation = rotation;
            if (FlightModel.Frame != null)
            {
                // the frame is already where the ship will be after this step: arrive there with it
                if ((position - sBody.position).sqrMagnitude > 1f) sBody.position = position - FlightModel.PointVelocity(position) * Time.fixedDeltaTime;
                sBody.velocity = (position - sBody.position) / Time.fixedDeltaTime;
                sBody.angularVelocity = Vector3.zero;
                return;
            }
            sBody.position = position;
            var chairBody = sSeat.GetComponentInParent<Rigidbody>();
            sBody.velocity = chairBody != null ? chairBody.GetPointVelocity(position) : Vector3.zero;
            sBody.angularVelocity = chairBody != null ? chairBody.angularVelocity : Vector3.zero;
        }

        // A command chair in reach (FlightHud shows the prompt).
        internal static bool NearChair => !Seated && sNearest != null && Allowed;

        // ---- sitting -----------------------------------------------------------------------------------------------

        static void Sit(Transform chair)
        {
            // where the player stood, to put them back there on standing up
            var stoodAt = sBody != null ? sBody.position : chair.position;
            var stoodFacing = sBody != null ? sBody.rotation : chair.rotation;
            sSeat = chair;
            sSatAt = Time.unscaledTime;
            if (sBodyCollider != null) { sColliderWasEnabled = sBodyCollider.enabled; sBodyCollider.enabled = false; }
            sDisabled.Clear();
            KeepActionsOff();
            // put the tool away the way the game does for its own scenes; it comes back on standing up
            Main.EventSystem.Post(ForceEquipEvent.GetEvent(ForceEquipEvent.EquipState.Unequip));
            FlightCamera.Begin(chair);
            FlightModel.Begin(chair);
            FlightCamera.UseFrame(FlightModel.Frame);
            var frame = FlightModel.Frame;
            if (frame != null)
            {
                SeatPose(chair, out var seatPosition, out var seatRotation);
                sSeatLocalPosition = frame.InverseTransformPoint(seatPosition);
                sSeatLocalRotation = Quaternion.Inverse(frame.rotation) * seatRotation;
            }
            // kept in the ship's frame (it goes where the ship goes), or the chair's if the ship is not carried
            var keep = frame != null ? frame : chair;
            sStandLocalPosition = keep.InverseTransformPoint(stoodAt);
            sStandLocalRotation = Quaternion.Inverse(keep.rotation) * stoodFacing;
            sStandOnFrame = frame != null;
            Plugin.Log.LogInfo($"[Flight] seated in '{Path(chair)}' at {chair.position}; chair axes fwd {chair.forward} up {chair.up}; " +
                               $"{sDisabled.Count} suit action(s) switched off.");
        }

        static void Stand(string why)
        {
            var chair = sSeat;
            // where the seat is now: on the ship's frame if it has been flown (the chair itself may not have moved)
            // back where the player stood before sitting down (on the ship, wherever it has gone since)
            var outPosition = Vector3.zero; var outRotation = Quaternion.identity;
            var keep = sStandOnFrame ? FlightModel.Frame : chair;
            if (keep != null)
            {
                outPosition = keep.TransformPoint(sStandLocalPosition);
                outRotation = keep.rotation * sStandLocalRotation;
            }
            else if (chair != null)
            {
                CurrentSeatPose(out outPosition, out outRotation);
                outPosition += outRotation * Vector3.forward * 0.6f;
            }
            sSeat = null;
            FlightModel.End();
            FlightCamera.End();
            // the suit's actions and tool come back once the Interact button that stood the player up is let go: it is
            // also the grapple's push, and the held press fired it on the way out
            sGiveBackPending = true;
            if (sBodyCollider != null) sBodyCollider.enabled = sColliderWasEnabled;
            if (sBody != null && chair != null)
            {
                // step out in front of the seat, moving with the ship
                sBody.position = outPosition;
                sBody.rotation = outRotation;
                if (FlightModel.Frame != null) sBody.velocity = FlightModel.PointVelocity(outPosition);
            }
            Plugin.Log.LogInfo($"[Flight] left the seat ({why}).");
        }

        // Every gameplay action off but Interact and Pause; remembered so they go back exactly as they were. Checked
        // every frame because the game turns actions on and off itself.
        static void KeepActionsOff()
        {
            var set = LynxControls.Instance?.TryGetLoadedActionSet(LynxControls.PlayerActionSetTypes.GameplayActions);
            if (set == null) return;
            string interact = GameplayActions.GameplayActionSet.Interact.ToString();
            string pause = GameplayActions.GameplayActionSet.Pause.ToString();
            foreach (var action in set.Actions)
            {
                if (action == null || action.Name == interact || action.Name == pause) continue;
                if (!sDisabled.ContainsKey(action)) { sDisabled[action] = action.Enabled; action.ClearInputState(); }
                if (action.Enabled) action.Enabled = false;
            }
        }

        static bool ViewTogglePressed()
        {
            if (Input.GetKeyDown(KeyCode.V)) return true;
            var device = InputManager.ActiveDevice;
            return device != null && device.DPadRight.WasPressed;
        }

        // ---- where -------------------------------------------------------------------------------------------------

        static bool FindBody()
        {
            if (sBody != null) return true;
            var player = Object.FindObjectOfType<Player>();
            if (player == null) return false;
            sBody = Traverse.Create(player).Field("mPlayerRigidbody").GetValue<Rigidbody>() ?? player.GetComponent<Rigidbody>();
            sBodyCollider = player.PlayerCollider;
            return sBody != null;
        }

        static void FindChairs()
        {
            if (Time.unscaledTime < sNextChairLook) return;
            sNextChairLook = Time.unscaledTime + 2f;
            sChairs.RemoveAll(c => c == null);
            if (sChairs.Count > 0) return;
            // a search of every object in the scene: a ship with no command chairs is given up on after a few tries
            if (++sChairTries > 10) return;
            foreach (var t in Object.FindObjectsOfType<Transform>())
                if (t != null && t.name.StartsWith(ChairPrefix, System.StringComparison.Ordinal)
                    && (t.parent == null || !t.parent.name.StartsWith(ChairPrefix, System.StringComparison.Ordinal)))
                    sChairs.Add(t);
            if (!sChairsLogged && sChairs.Count > 0)
            {
                sChairsLogged = true;
                Plugin.Log.LogInfo($"[Flight] {sChairs.Count} command chair(s): " + string.Join("; ", sChairs.ConvertAll(c => $"{Path(c)} at {c.position}")));
            }
        }

        static Transform? NearestChair(Vector3 from, float within)
        {
            Transform? best = null;
            float bestDistance = within * within;
            foreach (var chair in sChairs)
            {
                if (chair == null || !chair.gameObject.activeInHierarchy) continue;
                SeatPose(chair, out var seat, out _);
                float d = (seat - from).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = chair; }
            }
            return best;
        }

        // The seat point and the way the suit faces, from the chair: [Flight] SeatOffset in the chair's own axes and
        // SeatYaw turning it about the chair's up.
        static void SeatPose(Transform chair, out Vector3 position, out Quaternion rotation)
        {
            position = chair.TransformPoint(Plugin.FlightSeatOffset.Value);
            rotation = chair.rotation * Quaternion.Euler(0f, Plugin.FlightSeatYaw.Value, 0f);
        }

        static void CurrentSeatPose(out Vector3 position, out Quaternion rotation)
        {
            var frame = FlightModel.Frame;
            if (frame == null) { SeatPose(sSeat!, out position, out rotation); return; }
            position = frame.TransformPoint(sSeatLocalPosition);
            rotation = frame.rotation * sSeatLocalRotation;
        }

        static Vector3 SeatForward() => Quaternion.Euler(0f, Plugin.FlightSeatYaw.Value, 0f) * Vector3.forward;

        static string Path(Transform t)
        {
            var names = new List<string>();
            for (int i = 0; t != null && i < 4; t = t.parent, i++) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
