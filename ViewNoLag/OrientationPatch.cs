using BBI;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace ViewNoLag
{
    // Vanilla OrientationController.HandleAxisRotation reads mouse/controller input each frame and
    // accumulates it into persistent "axis velocity" fields (mAxisVelocityX/Y/Z) that are clamped and
    // then decayed by a drag divisor every frame - the rotation actually applied is the accumulated
    // velocity, not the raw input. That accumulate-then-decay model is what produces the laggy,
    // inertia-heavy zero-g look-feel: input doesn't fully take effect the frame it happens, and past
    // input keeps rotating the player after the mouse has stopped moving.
    //
    // This Prefix fully replaces that method (return false skips the original) with a version that
    // applies each frame's input directly to rotation with no persisted velocity state - standard FPS
    // mouselook math - while still reading input through the same LynxControls action enums and
    // sensitivity settings vanilla uses, so bindings and the game's own sensitivity sliders keep working.
    [HarmonyPatch(typeof(OrientationController), "HandleAxisRotation")]
    internal static class OrientationController_HandleAxisRotation_Instant
    {
        private static bool Prefix(OrientationController __instance)
        {
            if (!Plugin.ConfigEnabled.Value)
                return true;

            var traverse = Traverse.Create(__instance);
            var rigidbody = traverse.Field("mRigidbodyReference").GetValue<Rigidbody>();
            if (rigidbody == null)
                return true;

            bool rebootShutdown = traverse.Field("mRebootShutdown").GetValue<bool>();

            Vector3 input = Vector3.zero;
            Vector3 sensitivity;
            AnimationCurve sensitivityCurve;

            BindingSourceType lastInputType = LynxControls.Instance.LastInputType;
            bool isMouseOrKeyboard = lastInputType == BindingSourceType.MouseBindingSource || lastInputType == BindingSourceType.KeyBindingSource;

            if (isMouseOrKeyboard)
            {
                sensitivity = traverse.Property("MouseSensitivity").GetValue<Vector3>();
                sensitivityCurve = traverse.Field("m_MouseSensitivityCurve").GetValue<AnimationCurve>();

                if (!LynxControls.Instance.GetInputIsPressed(GameplayActions.GameplayActionSet.ModifiedRoll))
                {
                    Vector2 axis = LynxControls.Instance.GetTwoAxisInputControlVector(GameplayActions.GameplayActionSet.RotateBodyComposite);
                    input.x = axis.x;
                    input.y = axis.y;
                }
                else
                {
                    input.z += LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ModifiedRollBodyLeftRightComposite);
                }
            }
            else
            {
                sensitivity = traverse.Property("ControllerSensitivity").GetValue<Vector3>();
                sensitivityCurve = traverse.Field("m_ControllerSensitivityCurve").GetValue<AnimationCurve>();

                var deadzone = traverse.Field("m_OrientationDeadzone").GetValue<LynxControls.DeadZoneDefinition>();
                if (!LynxControls.Instance.GetInputIsPressed(GameplayActions.GameplayActionSet.ModifiedRoll))
                {
                    Vector2 axis = LynxControls.GetDeadzoneVector(LynxControls.Instance.GetTwoAxisInputControlVector(GameplayActions.GameplayActionSet.RotateBodyComposite), deadzone);
                    input.x = axis.x;
                    input.y = axis.y;
                }
                else
                {
                    input.z += LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ModifiedRollBodyLeftRightComposite);
                }

                if (LynxControls.Instance.GetInputIsPressed(GameplayActions.GameplayActionSet.ThrustBrakeLeft) || LynxControls.Instance.GetInputIsPressed(GameplayActions.GameplayActionSet.ThrustBrakeRight))
                {
                    var rollingDeadzone = traverse.Field("m_DeadZoneWhileRolling").GetValue<LynxControls.DeadZoneDefinition>();
                    Vector2 axis = LynxControls.GetDeadzoneVector(LynxControls.Instance.GetTwoAxisInputControlVector(GameplayActions.GameplayActionSet.RotateBodyComposite), rollingDeadzone);
                    input.x = axis.x;
                    input.y = axis.y;
                }
            }

            if (LynxControls.Instance.GetInputIsPressed(GameplayActions.GameplayActionSet.RollBodyLeft) != LynxControls.Instance.GetInputIsPressed(GameplayActions.GameplayActionSet.RollBodyRight) && !rebootShutdown)
            {
                float rollSensitivity = LynxControlsUserOptions.RollSensitivity;
                input.z += LynxControls.Instance.GetInputIsPressed(GameplayActions.GameplayActionSet.RollBodyLeft) ? -rollSensitivity : rollSensitivity;
            }

            // Vanilla calls HandleRollAudio()/HandleRotationAudio() here, driven by the accumulated
            // velocity state this patch no longer maintains - skipped rather than faked, since the
            // roll audio cues aren't essential to the instant-look behavior being added.

            if (input.sqrMagnitude <= 0f)
                return false;

            input *= sensitivityCurve.Evaluate(input.magnitude);

            float userSensitivity = Plugin.ConfigSensitivity.Value;
            input.x *= sensitivity.x * userSensitivity;
            input.y *= sensitivity.y * userSensitivity;
            input.z *= sensitivity.z * userSensitivity;

            float deltaTime = Time.deltaTime;
            Quaternion rotation = rigidbody.rotation;
            rotation *= Quaternion.Euler(Vector3.up * input.x * deltaTime);
            rotation *= Quaternion.Euler(Vector3.right * -input.y * deltaTime);
            rotation *= Quaternion.Euler(Vector3.forward * -input.z * deltaTime);
            rigidbody.MoveRotation(rotation);
            rigidbody.angularDrag = 0f;

            if (Plugin.ConfigDebugPrint.Value)
                Plugin.Log.LogInfo($"ViewNoLag: input={input}, deltaTime={deltaTime}");

            return false;
        }
    }
}
