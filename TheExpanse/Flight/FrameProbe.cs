using System.Text;
using BBI.Unity.Game;
using UnityEngine;

namespace TheExpanse
{
    // Diagnostic for the slow shaking on foot aboard the parked ship (the grapple gun shifting frame by frame): from 4 s
    // after standing up, 120 frames of what moved, logged in one block: the frame time, the physics steps in the frame,
    // and, just before drawing, how far the camera, the suit's drawn position and the suit's physics position moved since
    // the frame before, and how the camera sits against the suit (changes there are the shaking). Also how the game's
    // camera director (Cinemachine) updates and how the suit is smoothed.
    internal static class FrameProbe
    {
        const int Frames = 120;
        static float sStartAt = -1f;
        static int sCount;
        static bool sHooked;
        static Vector3 sLastCamera, sLastDrawn, sLastBody, sLastOffset;
        static Quaternion sLastCameraRot;
        static readonly StringBuilder sOut = new StringBuilder();
        internal static int Steps;          // physics steps since the last frame was drawn (counted by FlightModel)

        internal static void Arm()
        {
            sStartAt = Time.unscaledTime + 4f;
            sCount = 0;
            sOut.Clear();
            if (!sHooked) { sHooked = true; Application.onBeforeRender += Sample; }
        }

        static void Sample()
        {
            int steps = Steps;
            Steps = 0;
            if (sStartAt < 0f || Time.unscaledTime < sStartAt) return;
            var camera = LynxCameraController.MainCameraTransform;
            var body = FlightSeat.PlayerBody;
            if (camera == null || body == null) return;
            var offset = Quaternion.Inverse(body.transform.rotation) * (camera.position - body.transform.position);
            if (sCount == 0)
            {
                object? brain = typeof(LynxCameraController).GetProperty("MainBrain")?.GetValue(null);
                object? method = brain != null ? brain.GetType().GetField("m_UpdateMethod")?.GetValue(brain) : null;
                object? blend = brain != null ? brain.GetType().GetField("m_BlendUpdateMethod")?.GetValue(brain) : null;
                sOut.AppendLine($"[Flight] frame probe: camera director update {method ?? "?"}, blend update {blend ?? "?"}; suit smoothing {body.interpolation}, " +
                                $"kinematic {body.isKinematic}; fixed step {Time.fixedDeltaTime * 1000f:0} ms. Per frame: ms, steps, camera moved mm, " +
                                "suit drawn moved mm, suit physics moved mm, camera-against-suit change mm, camera turned deg");
            }
            else
            {
                sOut.AppendLine($"  {Time.unscaledDeltaTime * 1000f:0.0} ms {steps} | cam {(camera.position - sLastCamera).magnitude * 1000f:0.0} " +
                                $"drawn {(body.transform.position - sLastDrawn).magnitude * 1000f:0.0} phys {(body.position - sLastBody).magnitude * 1000f:0.0} " +
                                $"| offset {(offset - sLastOffset).magnitude * 1000f:0.0} | turn {Quaternion.Angle(camera.rotation, sLastCameraRot):0.000}");
            }
            sLastCamera = camera.position; sLastCameraRot = camera.rotation;
            sLastDrawn = body.transform.position; sLastBody = body.position; sLastOffset = offset;
            if (++sCount > Frames)
            {
                Plugin.Log.LogInfo(sOut.ToString());
                sStartAt = -1f;
            }
        }
    }
}
