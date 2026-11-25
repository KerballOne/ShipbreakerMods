using System.Collections.Generic;
using BBI.Unity.Game;
using System.Linq;
using InControl;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace TheExpanse
{
    // Flight Control, step 2: flying the ship from the seat. The inputs follow the suit's own layout (see the flight
    // controls doc): side thrusters on the suit's move keys, turning on the mouse / right stick, roll on Q/E, brake on
    // L-Ctrl (or Q + E, or L3 + R3); throttle on the wheel / triggers, Z full, X / D-pad down cut; R / Y flip and
    // burn; T / D-pad up hold attitude.
    //
    // How the ship moves: the game holds every ship body of 50 tonnes or more fixed (kinematic), so the hull ignores
    // forces. The mod keeps the ship's speed and spin itself and moves those fixed bodies together each physics step,
    // as one rigid piece about the ship's middle (as ShipRoll does). Lighter pieces are free bodies: jointed ones are
    // carried along, loose ones are pushed. The suit actions are off while seated, so the keys are read directly.
    internal static class FlightModel
    {
        const float FlipDoneDegrees = 2f;

        // Every body of the ship at the start, with where it sits in the ship's frame: all of them are carried as one
        // piece. Only the heaviest are held fixed by the game; the rest (the chairs, the PDCs, fittings) are free
        // bodies jointed on. Steering those by speed made the joints fight it (shaking PDCs, things working loose) and
        // cost frame rate, so while carried they are held fixed too, and set free again with the ship's motion when
        // the ship is let go.
        struct Carried { internal Rigidbody Body; internal Transform Transform; internal Vector3 Position; internal Quaternion Rotation; internal bool WasFree; }
        static readonly List<Carried> sBodies = new List<Carried>();
        // Parts of the ship with no body of their own (the command chairs and other fittings, under the ship's own root
        // object): moved by their transform.
        static readonly List<Carried> sRiders = new List<Carried>();
        static bool sFlying, sMoving;                       // a pilot at the controls / the ship being carried
        static Transform? sFrame;                           // the ship's frame, moved with it: the camera and the seat ride on it

        // the ship's state, in world space
        static Vector3 sPivot, sVelocity, sSpin;           // m, m/s, rad/s (axis * rate)
        static Quaternion sAttitude;                        // turns the ship's frame at the start to its frame now
        static Vector3 sForward0, sUp0;

        // the controls
        internal static float Throttle { get; private set; }    // 0..1
        static bool sHold;
        // Flip and burn, flown by the ship: turn tail-first, burn the drive down to BurnStopSpeed, then the side
        // thrusters take it to a full stop.
        enum Auto { None, Flip, Burn, Stop }
        static Auto sAuto;
        static string sAutoNote = "";                       // how the last one ended, shown for a few seconds
        static float sAutoNoteUntil;
        const float BurnStopSpeed = 1f;                     // m/s: the drive burns down to this, the thrusters do the rest
        const float BurnAimDegrees = 5f;                    // the drive only burns pointed this close to the target
        static Vector3 sFlipTarget;
        static Vector3 sThrust, sTurn;                      // ship axes, -1..1: x right, y up, z forward / x pitch, y yaw, z roll
        static bool sBrake;
        static Vector3 sMouseTurn;                          // degrees/s of spin added by the mouse since the last step

        const float AboardMargin = 4f;
        static Bounds sLocalBox;

        // Inside the ship's extent (plus a few meters), while it is under way.
        internal static bool Aboard(Vector3 point) => sMoving && sLocalBox.Contains(Quaternion.Inverse(ShipRotation) * (point - sPivot));

        const float StandardGravity = 9.80665f;
        static Vector3 sFelt;                               // m/s^2

        internal static bool Flying => sFlying;
        internal static bool UnderWay => sMoving;

        // The ship's long axis: a point on it (its middle) and its forward direction, while it is carried.
        internal static bool TryAxis(out Vector3 point, out Vector3 direction)
        {
            point = sPivot; direction = Forward;
            return sMoving;
        }
        internal static float Speed => sVelocity.magnitude;

        // How fast a point riding on the ship moves (for the pilot in the chair).
        internal static Vector3 PointVelocity(Vector3 point) => sVelocity + Vector3.Cross(sSpin, point - sPivot);

        // At the controls. If the ship is still drifting from the last time, it carries on from there.
        internal static void Begin(Transform chair)
        {
            if (!sMoving)
            {
                if (!FlightCamera.TryShipFrame(out var center, out var forward, out var up)) return;
                sPivot = center;
                sForward0 = forward;
                sUp0 = up;
                sAttitude = Quaternion.identity;
                sVelocity = sSpin = sFelt = Vector3.zero;
                if (sFrame == null) sFrame = new GameObject("TheExpanse_ShipFrame").transform;
                sFrame.SetPositionAndRotation(sPivot, ShipRotation);
                int held = Collect();
                CollectRiders(chair);
                sMoving = true;
                FlightJacks.Release();
                Plugin.Log.LogInfo($"[Flight] at the controls: {sBodies.Count} body(ies) carried as the ship ({held} held fixed by the game); " +
                                   $"middle {center}, forward {forward}.");
            }
            else Plugin.Log.LogInfo($"[Flight] back at the controls, drifting at {Speed:0.0} m/s.");
            Throttle = 0f;
            sHold = true;
            sAuto = Auto.None;
            sThrust = sTurn = sMouseTurn = Vector3.zero;
            sBrake = false;
            sFlying = true;
        }

        // Leaving the controls: the ship carries on as set (throttle, hold attitude, a flip under way) until the pilot is
        // back, or it has come to rest with the drive off.
        internal static void End()
        {
            if (!sFlying) return;
            sFlying = false;
            sThrust = sTurn = sMouseTurn = Vector3.zero;
            sBrake = false;
            Plugin.Log.LogInfo($"[Flight] left the controls at {Speed:0.0} m/s, throttle {Throttle * 100f:0}%" +
                               (Throttle > 0f ? "; the drive keeps burning." : Speed > 0.01f ? "; the ship keeps drifting." : "."));
        }

        // Level change: everything stops being carried.
        internal static void Reset()
        {
            sFlying = false;
            Release();
        }

        // The ship is let go: free bodies keep the ship's motion; fixed ones stay where they are (the game holds them).
        static void Release()
        {
            if (sMoving)
                foreach (var carried in sBodies)
                    if (carried.Body != null && carried.WasFree)
                    {
                        carried.Body.isKinematic = false;
                        carried.Body.velocity = PointVelocity(carried.Body.position);
                        carried.Body.angularVelocity = sSpin;
                    }
            sMoving = false;
            sBodies.Clear();
            sRiders.Clear();
            Throttle = 0f;
            RestoreCollisionModes();
            RestoreEffects();
            ShowGraphs();
        }

        // The effect graphs aboard (door air flows, cryo mist, generators...) cannot be moved into the ship's space, so
        // their particles would stream out behind it: while it moves they are not drawn, and come back when it is
        // still. The reactor's own effects are handled by the reactor.
        const float GraphsHideSpeed = 1f, GraphsShowSpeed = 0.5f;
        static readonly List<Renderer> sHiddenGraphs = new List<Renderer>();

        static void HideGraphs()
        {
            bool moving = Speed > (sHiddenGraphs.Count > 0 ? GraphsShowSpeed : GraphsHideSpeed) || sSpin.magnitude > 0.02f;
            if (!moving) { ShowGraphs(); return; }
            int added = 0;
            foreach (var effect in Object.FindObjectsOfType<UnityEngine.VFX.VisualEffect>())
            {
                if (effect == null || !effect.isActiveAndEnabled || effect.name.StartsWith("TheExpanse_") || !Aboard(effect.transform.position)) continue;
                var renderer = effect.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled) continue;
                renderer.enabled = false;
                sHiddenGraphs.Add(renderer);
                added++;
            }
            if (added > 0) Plugin.Log.LogInfo($"[Flight] under way: {added} effect(s) aboard not drawn until the ship is still ({sHiddenGraphs.Count} in all).");
        }

        static void ShowGraphs()
        {
            if (sHiddenGraphs.Count == 0) return;
            foreach (var renderer in sHiddenGraphs) if (renderer != null) renderer.enabled = true;
            Plugin.Log.LogInfo($"[Flight] the ship is still: {sHiddenGraphs.Count} effect(s) aboard drawn again.");
            sHiddenGraphs.Clear();
        }

        // ---- effects aboard ------------------------------------------------------------------------------------------
        // Particle effects leave their particles in world space, so with the ship under way they stream out behind it.
        // The plain particle systems aboard are switched to the ship's own space while it moves (and back after); the
        // effect graphs cannot be, so those aboard are listed once, to find which ones streak.
        static readonly Dictionary<ParticleSystem, ParticleSystemSimulationSpace> sSpaces = new Dictionary<ParticleSystem, ParticleSystemSimulationSpace>();
        static bool sEffectsListed;

        static void TakeEffects()
        {
            if (sFrame == null) return;
            int switched = 0;
            var graphs = new List<string>();
            foreach (var system in Object.FindObjectsOfType<ParticleSystem>())
            {
                if (system == null || sSpaces.ContainsKey(system) || !Aboard(system.transform.position)) continue;
                var main = system.main;
                if (main.simulationSpace != ParticleSystemSimulationSpace.World) continue;
                sSpaces[system] = main.simulationSpace;
                main.simulationSpace = ParticleSystemSimulationSpace.Custom;
                main.customSimulationSpace = sFrame;
                switched++;
            }
            if (!sEffectsListed)
            {
                sEffectsListed = true;
                foreach (var effect in Object.FindObjectsOfType<UnityEngine.VFX.VisualEffect>())
                    if (effect != null && effect.isActiveAndEnabled && Aboard(effect.transform.position))
                        graphs.Add($"{effect.name} ({(effect.visualEffectAsset != null ? effect.visualEffectAsset.name : "?")}) at {effect.transform.position}");
                Plugin.Log.LogInfo($"[Flight] {switched} particle system(s) aboard now ride in the ship's space; effect graphs aboard " +
                                   $"({graphs.Count}): " + string.Join("; ", graphs.Take(40)));
            }
        }

        static void RestoreEffects()
        {
            foreach (var pair in sSpaces)
                if (pair.Key != null) { var main = pair.Key.main; main.simulationSpace = pair.Value; }
            sSpaces.Clear();
            sEffectsListed = false;
        }

        // ---- loose things aboard -------------------------------------------------------------------------------------
        // Loose things aboard fall toward the stern while the drive burns and reach the deck fast; checked once a step
        // they can pass right through a thin plate. While the ship is under way they are checked continuously (the
        // speculative kind, the cheapest), and put back as they were when it stops.
        const float CollisionScanSeconds = 2f;
        static readonly Dictionary<Rigidbody, CollisionDetectionMode> sModes = new Dictionary<Rigidbody, CollisionDetectionMode>();
        static float sNextCollisionScan;

        static void ScanLooseAboard()
        {
            if (Time.time < sNextCollisionScan) return;
            sNextCollisionScan = Time.time + CollisionScanSeconds;
            int added = 0;
            foreach (var body in Object.FindObjectsOfType<Rigidbody>())
            {
                if (body == null || body.isKinematic || sModes.ContainsKey(body)) continue;
                if (body.collisionDetectionMode != CollisionDetectionMode.Discrete || !Aboard(body.position)) continue;
                sModes[body] = body.collisionDetectionMode;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                added++;
            }
            if (added > 0) Plugin.Log.LogInfo($"[Flight] {added} loose body(ies) aboard checked continuously ({sModes.Count} in all).");
            TakeEffects();
            HideGraphs();
        }

        static void RestoreCollisionModes()
        {
            foreach (var pair in sModes)
                if (pair.Key != null && !pair.Key.isKinematic) pair.Key.collisionDetectionMode = pair.Value;
            sModes.Clear();
        }

        // ---- frame time around a burn --------------------------------------------------------------------------------
        // Frame and physics-step times for 3 s before and 3 s after the drive lights, to tell whether a slowdown comes
        // with the burn starting or with speed.
        static readonly Queue<(float at, float frame)> sFrames = new Queue<(float, float)>();
        static int sStepsThisFrame, sMaxStepsPerFrame;
        static float sNextPerfLog;

        // Physics time per frame, roughly: from the first physics step of a frame to the frame's Update (the steps run
        // in between). Summed over the 3 s windows.
        static readonly System.Diagnostics.Stopwatch sPhysicsWatch = new System.Diagnostics.Stopwatch();
        static double sPhysicsMs;
        static int sPhysicsFrames;
        static bool sStepSeenThisFrame;

        internal static void MarkStep()
        {
            if (!sMoving || sStepSeenThisFrame) return;
            sStepSeenThisFrame = true;
            sPhysicsWatch.Restart();
        }

        internal static void SampleFrame()
        {
            if (sStepSeenThisFrame) { sPhysicsWatch.Stop(); sPhysicsMs += sPhysicsWatch.Elapsed.TotalMilliseconds; sStepSeenThisFrame = false; }
            sPhysicsFrames++;
            if (!sMoving) { sFrames.Clear(); sNextPerfLog = 0f; return; }
            float now = Time.unscaledTime;
            sFrames.Enqueue((now, Time.unscaledDeltaTime));
            sMaxStepsPerFrame = Mathf.Max(sMaxStepsPerFrame, sStepsThisFrame);
            sStepsThisFrame = 0;
            while (sFrames.Count > 0 && sFrames.Peek().at < now - 3f) sFrames.Dequeue();
            // every 3 s under way: the frame rate there, and where the ship is
            if (now >= sNextPerfLog)
            {
                if (sNextPerfLog > 0f)
                    Plugin.Log.LogInfo($"[Flight] perf: {Stats()}; up to {sMaxStepsPerFrame} physics step(s) in a frame; " +
                                       $"ship middle at {sPivot}, {Speed:0.0} m/s, throttle {Throttle * 100f:0}%; view {FlightCamera.ViewName}" +
                                       (FlightCamera.ViewName == "outside" ? $" from {FlightCamera.CameraPosition}" : "") + ".");
                sMaxStepsPerFrame = 0;
                sNextPerfLog = now + 3f;
            }
        }

        static string Stats()
        {
            if (sFrames.Count == 0) return "none";
            float sum = 0f, max = 0f;
            foreach (var f in sFrames) { sum += f.frame; max = Mathf.Max(max, f.frame); }
            float avg = sum / sFrames.Count;
            string physics = sPhysicsFrames > 0 ? $", physics about {sPhysicsMs / sPhysicsFrames:0.0} ms a frame" : "";
            sPhysicsMs = 0; sPhysicsFrames = 0;
            string paint = DrivePlume.PaintCount > 0 ? $", plume painting {DrivePlume.PaintMs / DrivePlume.PaintCount:0.0} ms a frame" : ", plume off";
            DrivePlume.PaintMs = 0f; DrivePlume.PaintCount = 0;
            return $"{1f / Mathf.Max(1e-4f, avg):0} fps average ({avg * 1000f:0.0} ms), worst {max * 1000f:0.0} ms{physics}{paint}";
        }

        internal static Transform? Frame => sMoving ? sFrame : null;
        static Quaternion ShipRotation => Quaternion.LookRotation(Forward, Up);
        static Vector3 Forward => (sAttitude * sForward0).normalized;
        static Vector3 Up => (sAttitude * sUp0).normalized;
        static Vector3 Right => Vector3.Cross(Up, Forward).normalized;

        // ---- input (every frame) -------------------------------------------------------------------------------------

        internal static void ReadInput()
        {
            if (!sFlying) return;
            var pad = InputManager.ActiveDevice;
            bool camera = Input.GetMouseButton(1) || (pad != null && pad.CommandIsPressed);   // the camera has these

            // side thrusters: the suit's move keys
            float x = Axis(KeyCode.D, KeyCode.A), y = Axis(KeyCode.Space, KeyCode.C) - (Input.GetKey(KeyCode.LeftShift) ? 1f : 0f);
            float z = Axis(KeyCode.W, KeyCode.S);
            if (pad != null)
            {
                x += pad.LeftStickX.Value;
                z += pad.LeftStickY.Value;
                y += (pad.Action1.IsPressed ? 1f : 0f) - (pad.Action2.IsPressed ? 1f : 0f);
            }
            sThrust = Vector3.ClampMagnitude(new Vector3(x, Mathf.Clamp(y, -1f, 1f), z), 1f);

            // turning: mouse (spin added per movement) and right stick (turning acceleration)
            bool q = Input.GetKey(KeyCode.Q), e = Input.GetKey(KeyCode.E);
            float roll = (e ? 1f : 0f) - (q ? 1f : 0f), pitch = 0f, yaw = 0f;
            bool l3 = pad != null && pad.LeftStickButton.IsPressed, r3 = pad != null && pad.RightStickButton.IsPressed;
            if (pad != null)
            {
                roll += (pad.RightBumper.IsPressed || r3 ? 1f : 0f) - (pad.LeftBumper.IsPressed || l3 ? 1f : 0f);
                if (!camera) { yaw += pad.RightStickX.Value; pitch += pad.RightStickY.Value * (LynxControlsUserOptions.InvertYAxis ? -1f : 1f); }
            }
            if (!camera)
            {
                float sensitivity = Mathf.Max(0f, Plugin.FlightMouseSensitivity.Value);
                float invert = LynxControlsUserOptions.InvertYAxis ? -1f : 1f;
                sMouseTurn += new Vector3(MouseAxis("mouse y", "Mouse Y") * invert, MouseAxis("mouse x", "Mouse X"), 0f) * sensitivity;
            }
            sTurn = new Vector3(Mathf.Clamp(pitch, -1f, 1f), Mathf.Clamp(yaw, -1f, 1f), Mathf.Clamp(roll, -1f, 1f));
            sBrake = Input.GetKey(KeyCode.LeftControl) || (q && e) || (l3 && r3);
            if (sBrake) sTurn.z = 0f;

            // throttle
            if (!camera)
            {
                float wheel = MouseAxis("mouse z", "Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.01f) PilotThrottle(Mathf.Clamp01(Throttle + Mathf.Sign(wheel) * 0.05f));
                if (pad != null && Mathf.Abs(pad.RightTrigger.Value - pad.LeftTrigger.Value) > 0.05f)
                    PilotThrottle(Mathf.Clamp01(Throttle + (pad.RightTrigger.Value - pad.LeftTrigger.Value) * 0.5f * Time.unscaledDeltaTime));
            }
            if (Input.GetKeyDown(KeyCode.Z)) PilotThrottle(1f);
            if (Input.GetKeyDown(KeyCode.X) || (pad != null && pad.DPadDown.WasPressed)) PilotThrottle(0f);

            if (Input.GetKeyDown(KeyCode.T) || (pad != null && pad.DPadUp.WasPressed))
            {
                sHold = !sHold;
                Plugin.Log.LogInfo(sHold ? "[Flight] hold attitude on." : "[Flight] hold attitude off.");
            }
            if (Input.GetKeyDown(KeyCode.R) || (pad != null && pad.Action4.WasPressed))
            {
                if (sAuto != Auto.None) CancelAuto("flip and burn pressed again");
                else StartFlip();
            }
        }

        // The pilot's hand on the throttle takes over from flip and burn.
        static void PilotThrottle(float value)
        {
            if (sAuto != Auto.None) CancelAuto("throttle moved");
            Throttle = value;
        }

        static void Note(string text)
        {
            sAutoNote = text;
            sAutoNoteUntil = Time.unscaledTime + 4f;
        }

        static void CancelAuto(string why)
        {
            sAuto = Auto.None;
            Throttle = 0f;
            Note("CANCELLED");
            Plugin.Log.LogInfo($"[Flight] flip and burn handed back to the pilot ({why}) at {Speed:0.0} m/s.");
        }

        static float Axis(KeyCode positive, KeyCode negative) => (Input.GetKey(positive) ? 1f : 0f) - (Input.GetKey(negative) ? 1f : 0f);

        static float MouseAxis(string name, string fallback)
        {
            try { return Input.GetAxisRaw(name); }
            catch (System.ArgumentException)
            {
                try { return Input.GetAxisRaw(fallback); }
                catch (System.ArgumentException) { return 0f; }
            }
        }

        // Flip and burn: turn tail-first to the way the ship is moving (or straight round if it is still), burn the
        // drive against the motion, and finish on the side thrusters.
        static void StartFlip()
        {
            sFlipTarget = Speed > 0.5f ? -sVelocity.normalized : -Forward;
            sAuto = Auto.Flip;
            sAutoNoteUntil = 0f;
            Throttle = 0f;
            Plugin.Log.LogInfo($"[Flight] flip and burn: turning to {sFlipTarget} ({Speed:0.0} m/s).");
        }

        // ---- physics (every step) ------------------------------------------------------------------------------------

        internal static void FixedStep()
        {
            if (!sMoving) return;
            sStepsThisFrame++;
            ScanLooseAboard();
            if (!sFlying && Throttle <= 0f && Speed < 0.01f && sSpin.magnitude < 1e-4f) { Release(); Plugin.Log.LogInfo("[Flight] the ship has come to rest."); return; }
            float dt = Time.fixedDeltaTime;
            if (dt <= 0f) return;

            float drive = Mathf.Max(0f, Plugin.FlightDriveAcceleration.Value);
            float thrusters = Mathf.Max(0f, Plugin.FlightThrusterAcceleration.Value);
            float turnAccel = Mathf.Max(0f, Plugin.FlightTurnAcceleration.Value) * Mathf.Deg2Rad;
            float maxRate = Mathf.Max(1f, Plugin.FlightMaxTurnRate.Value) * Mathf.Deg2Rad;

            bool autoStop = AutoStep(drive);

            // speed: the drive pushes forward, the side thrusters any way; the brake fires them against the drift
            var accel = Forward * (drive * Throttle) + (Right * sThrust.x + Up * sThrust.y + Forward * sThrust.z) * thrusters;
            if (sBrake || autoStop) accel += Vector3.ClampMagnitude(-sVelocity / dt, thrusters);
            sVelocity += accel * dt;
            // what the crew feels: the ship's change of speed, eased over about a quarter second for the readout
            sFelt = Vector3.Lerp(sFelt, accel, 1f - Mathf.Exp(-dt / 0.25f));

            // spin: commanded turning, the mouse, flipping; held (or braked) the thrusters cancel any spin not asked for
            var local = Quaternion.Inverse(Quaternion.LookRotation(Forward, Up));
            var spinLocal = local * sSpin;                  // x pitch (about right), y yaw (about up), z roll (about forward)
            var command = sAuto != Auto.None ? Vector3.zero : new Vector3(-sTurn.x, sTurn.y, -sTurn.z) * turnAccel;
            spinLocal += command * dt + new Vector3(-sMouseTurn.x, sMouseTurn.y, 0f) * Mathf.Deg2Rad;
            sMouseTurn = Vector3.zero;
            if (sAuto == Auto.Flip || sAuto == Auto.Burn) spinLocal = FlipSpin(spinLocal, turnAccel, maxRate, dt, local);
            else if (sHold || sBrake || sAuto == Auto.Stop)
            {
                // each axis not being turned is stopped by the thrusters
                if (Mathf.Abs(command.x) < 1e-4f) spinLocal.x = Mathf.MoveTowards(spinLocal.x, 0f, turnAccel * dt);
                if (Mathf.Abs(command.y) < 1e-4f) spinLocal.y = Mathf.MoveTowards(spinLocal.y, 0f, turnAccel * dt);
                if (Mathf.Abs(command.z) < 1e-4f) spinLocal.z = Mathf.MoveTowards(spinLocal.z, 0f, turnAccel * dt);
            }
            spinLocal = Vector3.ClampMagnitude(spinLocal, maxRate);
            sSpin = Quaternion.Inverse(local) * spinLocal;

            Move(dt);
        }

        // Flip and burn, one step. Keeps the target on the way the ship is going; sets the throttle while burning;
        // true while the side thrusters should be stopping the ship.
        static bool AutoStep(float drive)
        {
            if (sAuto == Auto.None) return false;
            if (Speed > 0.5f) sFlipTarget = -sVelocity.normalized;
            switch (sAuto)
            {
                case Auto.Flip:
                    Throttle = 0f;
                    return false;
                case Auto.Burn:
                    if (Speed <= BurnStopSpeed)
                    {
                        Throttle = 0f;
                        sAuto = Auto.Stop;
                        Plugin.Log.LogInfo($"[Flight] burn done at {Speed:0.0} m/s; side thrusters to a full stop.");
                        return true;
                    }
                    // full burn while far off, easing down as the speed nears the stop so the drive does not overshoot
                    bool aimed = Vector3.Angle(Forward, sFlipTarget) <= BurnAimDegrees;
                    Throttle = aimed && drive > 0f ? Mathf.Clamp((Speed - BurnStopSpeed) / (drive * 2f), 0.05f, 1f) : 0f;
                    return false;
                case Auto.Stop:
                    Throttle = 0f;
                    if (Speed < 0.05f)
                    {
                        sVelocity = Vector3.zero;
                        sAuto = Auto.None;
                        Note("COMPLETE: FULL STOP");
                        Plugin.Log.LogInfo("[Flight] flip and burn complete: full stop.");
                        return false;
                    }
                    return true;
            }
            return false;
        }

        // Turns toward the flip target at up to the full turning rate, easing in so it stops on it.
        static Vector3 FlipSpin(Vector3 spinLocal, float turnAccel, float maxRate, float dt, Quaternion local)
        {
            var target = local * sFlipTarget;                // the target in the ship's axes
            float angle = Vector3.Angle(Vector3.forward, target);
            if (angle < FlipDoneDegrees && spinLocal.magnitude < 0.02f)
            {
                if (sAuto == Auto.Flip)
                {
                    sAuto = Speed > BurnStopSpeed ? Auto.Burn : Auto.Stop;
                    Plugin.Log.LogInfo(sAuto == Auto.Burn ? $"[Flight] flip done: tail-first at {Speed:0.0} m/s; burning."
                                                         : $"[Flight] flip done at {Speed:0.0} m/s; side thrusters to a full stop.");
                }
                return Vector3.zero;
            }
            // the turn axis: from forward to the target (any axis at right angles if they are opposite)
            var axis = Vector3.Cross(Vector3.forward, target);
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.up;
            axis.Normalize();
            // the rate that still stops in time: v^2 = 2 a s
            float rate = Mathf.Min(maxRate, Mathf.Sqrt(2f * turnAccel * angle * Mathf.Deg2Rad));
            var want = axis * rate;
            // in the ship's axes pitch/yaw/roll spin about x/y/z: a turn of forward toward +y is about -x
            return Vector3.MoveTowards(spinLocal, want, turnAccel * dt);
        }

        static void Move(float dt)
        {
            float rate = sSpin.magnitude;
            var turn = rate > 1e-6f ? Quaternion.AngleAxis(rate * dt * Mathf.Rad2Deg, sSpin / rate) : Quaternion.identity;
            sPivot += sVelocity * dt;
            sAttitude = turn * sAttitude;
            var frame = ShipRotation;
            if (sFrame != null) sFrame.SetPositionAndRotation(sPivot, frame);

            foreach (var rider in sRiders)
                if (rider.Body == null && rider.Transform != null)
                    rider.Transform.SetPositionAndRotation(sPivot + frame * rider.Position, frame * rider.Rotation);

            // each body is sent to its place in the moved frame: fixed ones by the game's own move, free ones by the
            // speed and spin that take them there in this step
            foreach (var carried in sBodies)
            {
                var body = carried.Body;
                if (body == null) continue;
                var position = sPivot + frame * carried.Position;
                var rotation = frame * carried.Rotation;
                if (body.isKinematic)
                {
                    body.MovePosition(position);
                    body.MoveRotation(rotation);
                    continue;
                }
                // set free by the game meanwhile (cut off, say): steered by speed
                body.velocity = (position - body.position) / dt;
                (rotation * Quaternion.Inverse(body.rotation)).ToAngleAxis(out float angle, out var axis);
                if (angle > 180f) angle -= 360f;
                body.angularVelocity = float.IsNaN(axis.x) || Mathf.Abs(angle) < 1e-4f ? Vector3.zero : axis * (angle * Mathf.Deg2Rad / dt);
            }
        }

        // The ship's bodies (the bay's barge and anything already drifting left out), and where each sits in the
        // ship's frame. Returns how many are held fixed by the game.
        static int Collect()
        {
            sBodies.Clear();
            int held = 0, freed = 0;
            var inverse = Quaternion.Inverse(ShipRotation);
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world != null ? world.EntityManager : default;
            bool haveMain = world != null && TryMainKey(em, out mainKey);
            int main = mainKey;
            var carriedSoFar = new HashSet<Rigidbody>();
            var reasons = new Dictionary<string, int>();
            var skipped = new List<string>();
            int skippedCount = 0;
            // the fixed bodies first, so joints to them can be recognized
            var all = Object.FindObjectsOfType<Rigidbody>();
            foreach (var body in all) if (body != null && body.isKinematic && IsShip(body, world != null, em)) carriedSoFar.Add(body);
            // the hull's extent (world box), for telling a fitting lying still aboard from a cut piece lying in the bay
            bool anyHull = false;
            foreach (var hull in carriedSoFar)
                foreach (var part in hull.GetComponentsInChildren<StructurePart>())
                {
                    if (!anyHull) { sHullBox = new Bounds(part.transform.position, Vector3.zero); anyHull = true; }
                    else sHullBox.Encapsulate(part.transform.position);
                }
            if (anyHull) sHullBox.Expand(10f); else sHullBox = new Bounds(sPivot, Vector3.one * 1e6f);
            foreach (var body in all)
            {
                if (body == null) continue;
                bool unlisted = false;
                if (!IsShip(body, world != null, em))
                {
                    // parts the game never registered (no entity: on the Corvette, the PDC housings and collars) are
                    // never jointed; they only sit in place. Those lying still aboard go with the ship too.
                    if (body.isKinematic || !Unregistered(body) || !sHullBox.Contains(body.position) || body.velocity.magnitude >= StillSpeed) continue;
                    unlisted = true;
                }
                if (!body.isKinematic)
                {
                    string why = unlisted ? "unregistered fittings aboard" : WhyCarried(body, world != null, em, main, haveMain, carriedSoFar);
                    if (why == "")
                    {
                        if (skipped.Count < 12) skipped.Add($"{body.name} ({body.mass / 1000f:0.#} t, {body.velocity.magnitude:0.00} m/s)");
                        skippedCount++;
                        continue;
                    }
                    reasons[why] = reasons.TryGetValue(why, out int r) ? r + 1 : 1;
                }
                bool free = !body.isKinematic;
                if (free) { body.isKinematic = true; freed++; }
                else
                {
                    held++;
                    Plugin.Log.LogInfo($"[Flight] held fixed by the game: '{body.name}', {body.mass / 1000f:0.#} t, " +
                                       $"{body.GetComponentsInChildren<StructurePart>().Length} part(s), at {body.position}.");
                }
                sBodies.Add(new Carried { Body = body, Position = inverse * (body.position - sPivot), Rotation = inverse * body.rotation, WasFree = free });
                carriedSoFar.Add(body);
            }
            Plugin.Log.LogInfo("[Flight] free bodies carried: " + string.Join(", ", reasons.Select(kv => $"{kv.Value} {kv.Key}")) +
                               $"; {skippedCount} ship piece(s) left loose" + (skipped.Count > 0 ? ": " + string.Join("; ", skipped) : "."));
            // the ship's extent in its own frame, for telling whether the player is aboard
            bool any = false;
            var box = new Bounds();
            foreach (var c in sBodies)
                if (c.Body != null)
                    foreach (var part in c.Body.GetComponentsInChildren<StructurePart>())
                    {
                        var local = inverse * (part.transform.position - sPivot);
                        if (!any) { box = new Bounds(local, Vector3.zero); any = true; } else box.Encapsulate(local);
                    }
            box.Expand(2f * AboardMargin);
            sLocalBox = box;
            Plugin.Log.LogInfo($"[Flight] {freed} free body(ies) held fixed while carried; the ship spans {box.size.x:0} x {box.size.y:0} x {box.size.z:0} m (with margin).");
            return held;
        }

        // The top object the chair sits under, if no carried body is above the chair: everything under it that is
        // not a body of its own goes with the ship by its transform.
        static void CollectRiders(Transform chair)
        {
            sRiders.Clear();
            var carried = new HashSet<Rigidbody>();
            foreach (var c in sBodies) if (c.Body != null) carried.Add(c.Body);
            var above = chair.GetComponentInParent<Rigidbody>();
            var root = chair.root;
            if (above != null && carried.Contains(above))
            {
                Plugin.Log.LogInfo($"[Flight] the chair rides on '{above.name}', a carried body.");
                return;
            }
            if (root.GetComponent<Rigidbody>() != null)
            {
                Plugin.Log.LogInfo($"[Flight] the chair's top object '{root.name}' is a body of its own; not moved by transform.");
                return;
            }
            foreach (var part in root.GetComponentsInChildren<StructurePart>(true))
                if (part != null && !IsShipPart(part))
                {
                    Plugin.Log.LogWarning($"[Flight] the chair's top object '{root.name}' also holds '{part.name}', which is not part of the ship; " +
                                          "not moved by transform (the chairs and fittings stay behind).");
                    return;
                }
            var inverse = Quaternion.Inverse(ShipRotation);
            sRiders.Add(new Carried { Transform = root, Position = inverse * (root.position - sPivot), Rotation = inverse * root.rotation });
            int bodies = root.GetComponentsInChildren<Rigidbody>(true).Length;
            Plugin.Log.LogInfo($"[Flight] '{root.name}' ({root.GetComponentsInChildren<Transform>(true).Length} objects, {bodies} bodies under it) " +
                               "moves with the ship by its transform; the chair is under it.");
        }

        // A part of the ship, not of the bay.
        internal static bool IsShipPart(StructurePart? part)
        {
            if (part == null) return false;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return part.name.IndexOf("Barge", System.StringComparison.OrdinalIgnoreCase) < 0;
            var em = world.EntityManager;
            var e = part.Entity;
            return e != Entity.Null && em.Exists(e) && em.HasComponent<ShipMemberComponent>(e);
        }

        // Still joined to the ship: one of its parts is in the ship's main structure graph (the one most of the ship's
        // parts are in). Without the graph, a body at rest counts as joined.
        static int mainKey;

        // Why a free body of the ship goes with it ("" = it does not): it is in the ship's main structure, or a
        // joint ties it to something carried, or it is lying still (fittings such as the PDCs are their own structure,
        // held on by joints, and some shiver a little after a stop).
        const float StillSpeed = 1f;                        // m/s
        static Bounds sHullBox;

        static string WhyCarried(Rigidbody body, bool haveWorld, EntityManager em, int main, bool haveMain, HashSet<Rigidbody> carried)
        {
            if (haveWorld && haveMain)
                foreach (var part in body.GetComponentsInChildren<StructurePart>())
                    if (part != null && TryGraphKey(em, part.Entity, out int key) && key == main) return "in the main structure";
            foreach (var joint in body.GetComponents<Joint>())
                if (joint != null && joint.connectedBody != null && carried.Contains(joint.connectedBody)) return "jointed on";
            if (body.velocity.magnitude < StillSpeed && sHullBox.Contains(body.position)) return "lying still aboard";
            return "";
        }

        // A part in the ship's main structure (not a piece cut off, nor the bay). Falls back to any ship part.
        internal static bool IsMainShipPart(StructurePart? part)
        {
            if (!IsShipPart(part)) return false;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return true;
            var em = world.EntityManager;
            if (Time.frameCount != sMainKeyFrame) { sMainKeyFrame = Time.frameCount; sHaveMainKey = TryMainKey(em, out sMainKeyCached); }
            if (!sHaveMainKey) return true;
            return TryGraphKey(em, part!.Entity, out int key) && key == sMainKeyCached;
        }

        static int sMainKeyFrame = -1, sMainKeyCached;
        static bool sHaveMainKey;

        static bool TryGraphKey(EntityManager em, Entity e, out int key)
        {
            key = 0;
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<PendingDestruction>(e)) return false;
            if (em.HasComponent<StructureGraphNodeComponent>(e)) { key = em.GetComponentData<StructureGraphNodeComponent>(e).GraphData.GraphKey; return true; }
            if (em.HasComponent<StructureGroupMember>(e))
            {
                var group = em.GetComponentData<StructureGroupMember>(e).GroupEntity;
                if (em.Exists(group) && em.HasComponent<StructureGraphNodeComponent>(group))
                { key = em.GetComponentData<StructureGraphNodeComponent>(group).GraphData.GraphKey; return true; }
            }
            return false;
        }

        static bool TryMainKey(EntityManager em, out int main)
        {
            main = 0;
            var counts = new Dictionary<int, int>();
            using (var query = em.CreateEntityQuery(ComponentType.ReadOnly<StructureGraphNodeComponent>(), ComponentType.ReadOnly<ShipMemberComponent>()))
            using (var nodes = query.ToComponentDataArray<StructureGraphNodeComponent>(Allocator.TempJob))
                foreach (var node in nodes)
                {
                    int k = node.GraphData.GraphKey;
                    counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
                }
            if (counts.Count == 0) return false;
            main = counts.OrderByDescending(x => x.Value).First().Key;
            return true;
        }

        // A piece of the ship: one of its parts is tagged as a ship member (the bay's barge and holo field are built from
        // structure parts too, and are not).
        static bool Unregistered(Rigidbody body)
        {
            bool any = false;
            foreach (var part in body.GetComponentsInChildren<StructurePart>())
            {
                if (part == null) continue;
                if (part.Entity != Entity.Null) return false;
                any = true;
            }
            return any;
        }

        static bool IsShip(Rigidbody body, bool haveWorld, EntityManager em)
        {
            foreach (var part in body.GetComponentsInChildren<StructurePart>())
            {
                if (part == null) continue;
                if (!haveWorld) return part.name.IndexOf("Barge", System.StringComparison.OrdinalIgnoreCase) < 0;
                var e = part.Entity;
                if (e != Entity.Null && em.Exists(e) && em.HasComponent<ShipMemberComponent>(e)) return true;
            }
            return false;
        }

        // ---- readout ---------------------------------------------------------------------------------------------

        static GUIStyle? sStyle;

        // The flip and burn line: what it is doing and how to stop it, or briefly how it ended.
        static string AutoLine()
        {
            string cancel = LynxControls.Instance != null && LynxControls.Instance.LastInputType == InControl.BindingSourceType.DeviceBindingSource
                ? "Y" : "R";
            switch (sAuto)
            {
                case Auto.Flip: return $"\nFLIP AND BURN: FLIPPING ({cancel} to cancel)";
                case Auto.Burn: return $"\nFLIP AND BURN: BURNING to {BurnStopSpeed:0} m/s ({cancel} to cancel)";
                case Auto.Stop: return $"\nFLIP AND BURN: STOPPING on thrusters ({cancel} to cancel)";
            }
            return Time.unscaledTime < sAutoNoteUntil ? "\nFLIP AND BURN: " + sAutoNote : "";
        }

        internal static void Draw()
        {
            if (!sFlying) return;
            sStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 18 };
            sStyle.normal.textColor = new Color(1f, 0.8f, 0.35f);
            float closing = Vector3.Dot(sVelocity, Forward);
            string text = $"THROTTLE {Throttle * 100f:0}%\nSPEED {Speed:0.0} m/s ({(closing >= 0f ? "fwd" : "aft")} {Mathf.Abs(closing):0.0})\n" +
                          $"ACCEL {sFelt.magnitude:0.00} m/s² ({sFelt.magnitude / StandardGravity:0.00} g)\n" +
                          $"TURN {sSpin.magnitude * Mathf.Rad2Deg:0.0} deg/s\n" +
                          (sHold ? "HOLD ATTITUDE" : "FREE") + AutoLine();
            GUI.Label(new Rect(24, Screen.height * 0.55f, 460, 150), text, sStyle);
        }
    }
}
