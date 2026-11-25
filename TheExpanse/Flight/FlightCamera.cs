using System.Collections.Generic;
using BBI.Unity.Game;
using InControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace TheExpanse
{
    // Flight Control: the views while seated, cycled with the tool mode button (V / D-pad right):
    //   Outside: orbiting the ship's middle, turning with the ship. Starts behind and 45 degrees above; the bay's walls
    //            are kept out of the way (the camera is pulled in to stay in front of anything that is not the ship).
    //   Nose:    on the ship's nose, looking ahead.
    //   Seat:    the game's own view.
    // Look around: hold RMB + mouse / hold View + right stick; zoom (Outside): hold RMB + wheel / hold View + RT/LT.
    // A Cinemachine camera of the mod's own was ignored (the game's ECS camera system places its camera on the player
    // itself), and moving the camera at beginCameraRendering was too late (only the flashlight, a child of the camera,
    // went with it). So the main camera is moved in onBeforeRender, after all the game's camera moves, and put back
    // when the frame is done: everything else in the game still sees it at the player's head. The helmet is taken off
    // the screen in the two outside views.
    internal static class FlightCamera
    {
        enum View { Outside, Nose, Seat }

        const float StartPitch = 45f;
        const float WallMargin = 1.5f;          // m kept between the camera and a wall in the way

        static bool sActive, sHooked, sMoved, sLoggedFirstDraw, sHelmetOff, sHudOff;
        static View sView;
        static Vector3 sPosition, sSavedPosition;
        static Quaternion sRotation, sSavedRotation;
        static float sSavedFar;

        static Transform? sFrame;               // the chair: everything is kept in its axes so it turns with the ship
        static Vector3 sLocalCenter, sLocalForward, sLocalUp, sLocalNose;
        static float sShipLength;
        static float sYaw, sPitch, sDistance;   // Outside: degrees about the ship's up / above its long axis; meters
        static float sBoom;                     // Outside: the distance actually used, shortened by walls
        static float sLookYaw, sLookPitch;      // Nose: looking around from straight ahead
        static readonly RaycastHit[] sHits = new RaycastHit[64];

        internal static void Begin(Transform chair)
        {
            End();
            sFrame = chair;
            MeasureShip(chair);
            sDistance = Plugin.FlightCameraDistance.Value > 0f ? Plugin.FlightCameraDistance.Value : sShipLength * 1.3f;
            PickStart();
            sLookYaw = sLookPitch = 0f;

            sActive = true;
            sLoggedFirstDraw = false;
            if (!sHooked)
            {
                Application.onBeforeRender += BeforeRender;
                RenderPipelineManager.endFrameRendering += AfterFrame;
                sHooked = true;
            }
            SetView(View.Outside);
            Place();
            Plugin.Log.LogInfo($"[Flight] outside view: ship {sShipLength:0} m long, camera {sDistance:0} m out " +
                               $"(clear to {sBoom:0} m) at {sPitch:0} degrees up, {sYaw:0} around.");
        }

        // From here on the views ride on the ship's own frame (moved by the flight model), not the chair.
        internal static void UseFrame(Transform? frame)
        {
            if (!sActive || sFrame == null || frame == null || frame == sFrame) return;
            Axes(out var center, out var forward, out var up);
            var nose = sFrame.TransformPoint(sLocalNose);
            sFrame = frame;
            sLocalCenter = frame.InverseTransformPoint(center);
            sLocalForward = frame.InverseTransformDirection(forward);
            sLocalUp = frame.InverseTransformDirection(up);
            sLocalNose = frame.InverseTransformPoint(nose);
        }

        // The ship's middle and axes as they are now (they turn with the chair).
        internal static bool TryShipFrame(out Vector3 center, out Vector3 forward, out Vector3 up)
        {
            center = forward = up = Vector3.zero;
            if (!sActive || sFrame == null) return false;
            Axes(out center, out forward, out up);
            return true;
        }

        internal static void End()
        {
            if (sActive) { Helmet(true); Hud(true); }
            sActive = false;
            sFrame = null;
        }

        internal static string ViewName => !sActive ? "none" : sView == View.Outside ? "outside" : sView == View.Nose ? "nose" : "seat";
        internal static Vector3 CameraPosition => sPosition;

        internal static void CycleView()
        {
            if (!sActive) return;
            SetView(sView == View.Outside ? View.Nose : sView == View.Nose ? View.Seat : View.Outside);
        }

        static void SetView(View view)
        {
            sView = view;
            Helmet(view == View.Seat);
            Hud(view == View.Seat);
            if (view == View.Nose) sLookYaw = sLookPitch = 0f;
            Plugin.Log.LogInfo(view == View.Outside ? "[Flight] view: outside the ship."
                             : view == View.Nose ? "[Flight] view: the nose." : "[Flight] view: the seat.");
        }

        // The helmet's mesh in front of the camera, through the game's own event.
        static void Helmet(bool on)
        {
            if (on != sHelmetOff) return;       // already as wanted
            Main.EventSystem.Post(on ? ToggleHelmetEvent.On() : ToggleHelmetEvent.Off());
            sHelmetOff = !on;
        }

        // The HUD (gauges, prompts, the reticle) and the markers on objects, through the game's own events: the fade it
        // uses for its own scenes, and the marker switch.
        static void Hud(bool on)
        {
            if (on != sHudOff) return;
            Main.EventSystem.Post(SetHUDAlphaEvent.GetEvent(on ? 1f : 0f, 0.25f, SetHUDAlphaEvent.FadeType.All));
            Main.EventSystem.Post(SetHudMarkerVisibilityEvent.GetEvent(on));
            sHudOff = !on;
        }

        // Every frame, after the ship has moved (LateUpdate).
        internal static void Frame()
        {
            if (!sActive || sView == View.Seat) return;
            if (sFrame == null) { End(); return; }
            Steer();
            Place();
        }

        static void Steer()
        {
            var device = InputManager.ActiveDevice;
            bool padHeld = device != null && device.CommandIsPressed;   // View / Create (Share)
            float yaw = 0f, pitch = 0f, zoom = 0f;
            if (Input.GetMouseButton(1))
            {
                yaw = MouseAxis("mouse x", "Mouse X") * 3f;
                pitch = -MouseAxis("mouse y", "Mouse Y") * 3f;
                zoom = Mathf.Clamp(MouseAxis("mouse z", "Mouse ScrollWheel"), -1f, 1f) * 0.15f;
            }
            else if (padHeld)
            {
                yaw = device!.RightStickX.Value * 120f * Time.unscaledDeltaTime;
                pitch = device.RightStickY.Value * 90f * Time.unscaledDeltaTime;
                zoom = (device.RightTrigger.Value - device.LeftTrigger.Value) * Time.unscaledDeltaTime;
            }
            if (sView == View.Nose)
            {
                sLookYaw = Mathf.Clamp(sLookYaw + yaw, -170f, 170f);
                sLookPitch = Mathf.Clamp(sLookPitch + pitch, -85f, 85f);
                return;
            }
            sYaw += yaw;
            sPitch = Mathf.Clamp(sPitch + pitch, -85f, 85f);
            sDistance = Mathf.Clamp(sDistance * (1f - zoom), Mathf.Max(5f, sShipLength * 0.3f), Mathf.Max(50f, sShipLength * 8f));
        }

        static float MouseAxis(string name, string fallback)
        {
            try { return Input.GetAxisRaw(name); }
            catch (System.ArgumentException)
            {
                try { return Input.GetAxisRaw(fallback); }
                catch (System.ArgumentException) { return 0f; }
            }
        }

        static void Axes(out Vector3 center, out Vector3 forward, out Vector3 up)
        {
            center = sFrame!.TransformPoint(sLocalCenter);
            forward = sFrame.TransformDirection(sLocalForward).normalized;
            up = sFrame.TransformDirection(sLocalUp).normalized;
        }

        static Vector3 OrbitDirection(float yaw, float pitch, Vector3 forward, Vector3 up)
        {
            var turn = Quaternion.AngleAxis(yaw, up) * Quaternion.AngleAxis(pitch, Vector3.Cross(up, forward).normalized);
            return turn * -forward;
        }

        static void Place()
        {
            if (!sActive || sFrame == null) return;
            Axes(out var center, out var forward, out var up);
            if (sView == View.Nose)
            {
                sPosition = sFrame.TransformPoint(sLocalNose);
                sRotation = Quaternion.LookRotation(forward, up) * Quaternion.Euler(sLookPitch, sLookYaw, 0f);
                return;
            }
            var direction = OrbitDirection(sYaw, sPitch, forward, up);
            float clear = ClearDistance(center, direction, sDistance);
            // pulled in at once when something comes in the way, let back out gently
            sBoom = clear < sBoom || sBoom <= 0f ? clear : Mathf.MoveTowards(sBoom, clear, Mathf.Max(20f, sBoom) * 2f * Time.unscaledDeltaTime);
            sPosition = center + direction * sBoom;
            sRotation = Quaternion.LookRotation(center - sPosition, up);
        }

        // How far out from the ship's middle the camera can go along a direction before something that is not the
        // ship is in the way (the bay's walls, the elevator, the jacks).
        static float ClearDistance(Vector3 center, Vector3 direction, float wanted)
        {
            int count = Physics.RaycastNonAlloc(center, direction, sHits, wanted + WallMargin, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float clear = wanted;
            for (int i = 0; i < count; i++)
            {
                var hit = sHits[i];
                if (hit.collider == null || IsShip(hit.collider)) continue;
                clear = Mathf.Min(clear, hit.distance - WallMargin);
            }
            return Mathf.Max(2f, clear);
        }

        static readonly Dictionary<Collider, bool> sShipCache = new Dictionary<Collider, bool>();

        static bool IsShip(Collider collider)
        {
            if (sShipCache.TryGetValue(collider, out bool ship)) return ship;
            var part = collider.GetComponentInParent<StructurePart>();
            ship = FlightModel.IsShipPart(part);
            if (sShipCache.Count > 4096) sShipCache.Clear();
            sShipCache[collider] = ship;
            return ship;
        }

        // The start: behind the ship and 45 degrees above, or the first of a few other angles with a clear view, or
        // failing that, the one that gets furthest out.
        static void PickStart()
        {
            Axes(out var center, out var forward, out var up);
            float bestClear = -1f, bestYaw = 0f, bestPitch = StartPitch;
            foreach (float pitch in new[] { StartPitch, 60f, 75f })
                foreach (float yaw in new[] { 0f, 30f, -30f, 60f, -60f })
                {
                    float clear = ClearDistance(center, OrbitDirection(yaw, pitch, forward, up), sDistance);
                    if (clear > bestClear + 0.5f) { bestClear = clear; bestYaw = yaw; bestPitch = pitch; }
                    if (clear >= sDistance * 0.9f) { sYaw = yaw; sPitch = pitch; sBoom = clear; return; }
                }
            sYaw = bestYaw;
            sPitch = bestPitch;
            sBoom = bestClear;
        }

        static void BeforeRender()
        {
            if (sMoved) Restore();
            var camera = LynxCameraController.MainCamera;
            if (!sActive || sView == View.Seat || camera == null) return;
            var t = camera.transform;
            sSavedPosition = t.position;
            sSavedRotation = t.rotation;
            sSavedFar = camera.farClipPlane;
            t.SetPositionAndRotation(sPosition, sRotation);
            camera.farClipPlane = Mathf.Max(sSavedFar, 20000f);
            sMoved = true;
            if (!sLoggedFirstDraw)
            {
                sLoggedFirstDraw = true;
                Plugin.Log.LogInfo($"[Flight] outside view drawn by '{camera.name}' from {sPosition} (the game had it at {sSavedPosition}).");
            }
        }

        static void AfterFrame(ScriptableRenderContext context, Camera[] cameras) => Restore();

        static void Restore()
        {
            var camera = LynxCameraController.MainCamera;
            if (!sMoved || camera == null) return;
            sMoved = false;
            camera.transform.SetPositionAndRotation(sSavedPosition, sSavedRotation);
            camera.farClipPlane = sSavedFar;
        }

        // The ship's middle and length from its parts (the bay's barge left out); its forward from the drive (the
        // plume goes aft), else the chair's facing; up from the chair, else the world's. The nose camera sits on the
        // ship's axis a meter past the furthest forward point of any part's mesh.
        static void MeasureShip(Transform chair)
        {
            bool any = false;
            var bounds = new Bounds();
            var parts = new List<StructurePart>();
            // the ship's main structure only: pieces cut off earlier (still tagged as the ship's) may lie anywhere
            var all = Object.FindObjectsOfType<StructurePart>();
            foreach (var part in all)
                if (FlightModel.IsMainShipPart(part)) parts.Add(part);
            if (parts.Count == 0)
                foreach (var part in all)
                    if (FlightModel.IsShipPart(part)) parts.Add(part);
            foreach (var part in parts)
            {
                var p = part.transform.position;
                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
            }
            var center = any ? bounds.center : chair.position;
            var forward = DrivePlume.TryDriveAxis(out var exhaust) ? -exhaust : chair.forward;
            var up = Vector3.ProjectOnPlane(chair.up, forward);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(Vector3.up, forward);
            sShipLength = any ? Mathf.Max(10f, Mathf.Abs(Vector3.Dot(bounds.size, new Vector3(Mathf.Abs(forward.x), Mathf.Abs(forward.y), Mathf.Abs(forward.z))))) : 60f;

            float front = Vector3.Dot(center, forward) + sShipLength * 0.5f;
            foreach (var part in parts)
                foreach (var renderer in part.GetComponentsInChildren<Renderer>(false))
                {
                    var b = renderer.bounds;
                    front = Mathf.Max(front, Vector3.Dot(b.center, forward) + Vector3.Dot(b.extents, new Vector3(Mathf.Abs(forward.x), Mathf.Abs(forward.y), Mathf.Abs(forward.z))));
                }
            var nose = center + forward * (front - Vector3.Dot(center, forward) + 1f);

            sLocalCenter = chair.InverseTransformPoint(center);
            sLocalForward = chair.InverseTransformDirection(forward.normalized);
            sLocalUp = chair.InverseTransformDirection(up.normalized);
            sLocalNose = chair.InverseTransformPoint(nose);
            Plugin.Log.LogInfo($"[Flight] ship: middle {center}, forward {forward}, up {up.normalized}, nose camera at {nose}.");
        }
    }
}
