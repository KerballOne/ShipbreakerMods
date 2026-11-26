using System.Collections.Generic;
using BBI;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace TheExpanse
{
    // Magnetic boots aboard a ship under way, copied from the MagBoots mod (its walking, stepping and standing hold),
    // without its power button, battery and HUD. They lock on by themselves, and only to decks: surfaces facing the
    // bow, that the drive's pull presses you onto. Walk with the suit's move keys (Shift or L3 toggles running), crouch
    // with thrust down; thrust up (Space / A) jumps: the boots let go for a second, held it lifts you gently (at most
    // JumpSpeed off the deck), and they lock on again as you come back down. The suit's thrusters stay off through the
    // jump (they shot the suit out through the hull before); after it, if no deck is below, they work again.
    //
    // Everything is held in the ship's own frame: the deck point is kept in ship coordinates, so the ship's motion and
    // turning carry the hold along, and the suit's speed is worked out against the ship's speed where it stands.
    // Ray casts see the ship where the last physics step left it (FlightModel.PrevPivot / PrevRotation), the same
    // place the suit's own position is from.
    internal static class DeckBoots
    {
        // MagBoots' defaults
        const float MaxNormalAngle = 20f;           // a deck: its face within this of the bow (stricter when not walking ahead)
        const float MaxNormalFwdAngle = 50f;        // stepping ahead may take steeper treads
        const float FwdSweepAngle = 60f;
        const float MinFaceArea = 3f;               // m^2 of the hit triangle
        const float AheadCastDistance = 0.85f;      // the longest stride probed, m
        const float MaxLookDown = 45f, MaxLookUp = 85f;
        const float MaxStridePitch = 10f, MinStridePitch = 60f;
        const float CornerSmoothing = 3f;
        const float ReorientSettledAngle = 1f;
        const float WalkSpeed = 2f, RunSpeed = 4f;
        // MagBoots steps down as far as 1.5 m (hull ledges in zero g); under the drive's pull a drop deeper than a stair
        // is a fall: walking off it the boots let go and the pull brings the suit down onto whatever is below. (Looking
        // down the stairs, MagBoots' search picked a floor seen through the gaps two meters down, and the hold then flung
        // the suit at it.)
        const float StepDownHeight = 0.6f, StepSignificantHeight = 0.15f, StepFollowSpeed = 2f;
        const float MaxHoldSpeed = RunSpeed + 2f;   // m/s against the ship the hold may give the suit
        const float StepUpHeight = 0.25f, StepUpFwdHeight = 0.95f;
        const float StepLateralSettled = 0.1f, StepUpSettled = 0.1f, StepTimeout = 0.3f;
        const float AttachMaxVelocity = 6f, BreakawayVelocity = 12f;
        const float Spring = 200f, Damper = 30f;
        const float CrouchTransition = 0.25f;
        const float LookSensitivity = 0.25f;
        // this mod's own
        const float AttachReach = 0.6f;             // m past standing height within which a falling suit locks on
        const float JumpSpeed = 2f;                 // m/s off the deck, at most
        const float JumpLift = 6f;                  // m/s^2 of lift (beyond the pull) while thrust up is held in a jump
        const float JumpRelease = 1f;               // s the jump lasts at most
        const float JumpCatchAfter = 0.3f;          // s into a jump after which coming down onto a deck locks on again

        static bool sAttached;
        // the deck point and its normal, in the ship's frame, and the normal eased toward it
        static Vector3 sLocalPoint, sLocalNormal, sSmoothedLocal;
        // the same, in the world where the last physics step left the ship (refreshed each step)
        static Vector3 sPoint, sNormal;

        static bool sPendingDown, sPendingUp;
        static float sPendingTimer;
        static Vector3 sPendingPoint, sPendingNormal;

        static float sStandoff = 1f;
        static bool sRun, sJumpAsked, sUpWasHeld;
        static float sJumpStart = -10f;
        static Vector3 sJumpUp;
        static bool sWasMoving;
        static float sNextLog;

        static readonly RaycastHit[] sHits = new RaycastHit[16];
        static readonly Dictionary<Mesh, (int[] triangles, Vector3[] vertices)> sMeshes = new Dictionary<Mesh, (int[], Vector3[])>();

        internal static bool Attached => sAttached;
        static bool Jumping => !sAttached && Time.time < sJumpStart + JumpRelease;
        // Off the boots for a moment under the pull (walked off a step, came down from a jump): the suit falls back onto
        // the deck with the move keys still steering it at walking speed, and its thrusters stay off. Held W had the
        // thrusters take the suit to 15 m/s in the moment it was off a stair, and out of the ship.
        const float AirborneHold = 3f;              // s after the boots last held
        static float sLastHeld = -10f;
        static bool Airborne => !sAttached && FlightModel.PullActive && Time.time < sLastHeld + AirborneHold;
        // the suit's own thrusters are off: locked on, in a jump, or briefly off the deck under the pull
        internal static bool HoldsThrusters => sAttached || Jumping || Airborne;
        internal static bool Running => sAttached && sRun;

        // ---- every frame: the run toggle and the jump (pressed between physics steps) --------------------------------
        internal static void Tick()
        {
            if (!sAttached) { sUpWasHeld = UpHeld(); return; }
            var device = InputManager.ActiveDevice;
            if (Input.GetKeyDown(KeyCode.LeftShift) || (device != null && device.LeftStickButton.WasPressed)) sRun = !sRun;
            bool up = UpHeld();
            if (up && !sUpWasHeld) sJumpAsked = true;
            sUpWasHeld = up;
        }

        static bool UpHeld() =>
            LynxControls.Instance != null &&
            LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveUpDownComposite) > 0.5f;

        internal static void Reset(string? why = null)
        {
            if (sAttached && why != null) Plugin.Log.LogInfo($"[Flight] deck boots off ({why}).");
            sAttached = false;
            sPendingDown = sPendingUp = false;
            sJumpAsked = false;
            sRun = false;
            sStandoff = 1f;
            sJumpStart = -10f;
            sLastHeld = -10f;
        }

        static void Detach(string why, Rigidbody player)
        {
            Plugin.Log.LogInfo($"[Flight] deck boots off ({why}).");
            sAttached = false;
            sPendingDown = sPendingUp = false;
            sJumpAsked = false;
            sStandoff = 1f;
        }

        // ---- ship frame ---------------------------------------------------------------------------------------------
        static Vector3 ToLocal(Vector3 world) => Quaternion.Inverse(FlightModel.PrevRotation) * (world - FlightModel.PrevPivot);
        static Vector3 ToLocalDir(Vector3 world) => Quaternion.Inverse(FlightModel.PrevRotation) * world;
        static Vector3 ToWorld(Vector3 local) => FlightModel.PrevPivot + FlightModel.PrevRotation * local;
        static Vector3 ToWorldDir(Vector3 local) => FlightModel.PrevRotation * local;

        static void SetAttach(Vector3 point, Vector3 normal)
        {
            sPoint = point;
            sNormal = normal.normalized;
            sLocalPoint = ToLocal(point);
            sLocalNormal = ToLocalDir(sNormal);
        }

        // ---- each physics step, aboard a ship under way, unseated ----------------------------------------------------
        // velocity: the suit's speed carried with the ship, without the drive's pull. Returns true with the new speed
        // while the boots hold; false leaves the suit to the pull (and its thrusters).
        internal static bool Step(Rigidbody player, Vector3 velocity, Vector3 shipVelocity, Vector3 pull, float dt, out Vector3 result)
        {
            result = velocity;
            var relative = velocity - shipVelocity;
            float height = Mathf.Max(0.5f, Plugin.FlightStandHeight.Value);
            bool pulled = pull.sqrMagnitude > 0.01f;

            if (!sAttached)
            {
                if (Jumping)
                {
                    // in the jump: the pull acts; thrust up held lifts, up to JumpSpeed off the deck
                    float since = Time.time - sJumpStart;
                    float rising = Vector3.Dot(relative, sJumpUp);
                    if (!(since >= JumpCatchAfter && rising < 0f && TryCatch(player, relative, height)))
                    {
                        relative += pull * dt;
                        relative = AirSteer(player, relative, sJumpUp, dt);
                        if (UpHeld() && rising < JumpSpeed) relative += sJumpUp * (Mathf.Min(JumpLift * dt, JumpSpeed - rising) + Vector3.Dot(-pull, sJumpUp) * dt);
                        float up = Vector3.Dot(relative, sJumpUp);
                        if (up > JumpSpeed) relative -= sJumpUp * (up - JumpSpeed);
                        result = shipVelocity + relative;
                        return true;
                    }
                }
                else if (!pulled || !TryCatch(player, relative, height))
                {
                    if (!Airborne) return false;
                    // briefly off the deck: the pull brings the suit down, the move keys steer it, the fall stays soft
                    var down = pull.normalized;
                    relative += pull * dt;
                    relative = AirSteer(player, relative, -down, dt);
                    float falling = Vector3.Dot(relative, down);
                    if (falling > AirFallCap) relative -= down * (falling - AirFallCap);
                    result = shipVelocity + relative;
                    return true;
                }
            }
            sLastHeld = Time.time;

            if (relative.magnitude > BreakawayVelocity) { Detach($"knocked free at {relative.magnitude:0.0} m/s", player); return false; }

            if (sJumpAsked)
            {
                sJumpAsked = false;
                sJumpUp = ToWorldDir(sSmoothedLocal).normalized;
                Detach("jump", player);
                sJumpStart = Time.time;
                float off = Vector3.Dot(relative, sJumpUp);
                result = shipVelocity + relative + sJumpUp * Mathf.Max(0f, JumpSpeed - off);
                return true;
            }
            return Hold(player, relative, shipVelocity, height, pulled, dt, out result);
        }

        const float AirFallCap = 4f;                // m/s toward the deck while briefly off it

        // Steering in the air: the speed across the deck eased toward walking (running) speed in the move keys' direction.
        static Vector3 AirSteer(Rigidbody player, Vector3 relative, Vector3 up, float dt)
        {
            var move = MoveInput(player.rotation, up);
            var across = Vector3.ProjectOnPlane(relative, up);
            var want = move * (sRun ? RunSpeed : WalkSpeed);
            return relative - across + Vector3.MoveTowards(across, want, 8f * dt);
        }

        // a deck below toward the stern, near enough, and the suit not coming at it too fast: lock on
        static bool TryCatch(Rigidbody player, Vector3 relative, float height)
        {
            {
                // a deck below toward the stern, near enough, and the suit not coming at it too fast
                var down = -FlightModel.ForwardDirection;
                if (!TryFindValidSurface(player, player.position, down, height + AttachReach, MaxNormalAngle, out var hit, out _)) return false;
                if (Jumping) Plugin.Log.LogInfo($"[Flight] deck boots: caught the deck again {Time.time - sJumpStart:0.00} s into the jump.");
                if (relative.magnitude > AttachMaxVelocity)
                {
                    if (Time.time >= sNextLog) { sNextLog = Time.time + 1f; Plugin.Log.LogInfo($"[Flight] deck boots: too fast to lock on ({relative.magnitude:0.0} m/s against the ship)."); }
                    return false;
                }
                SetAttach(hit.point, hit.normal);
                sSmoothedLocal = sLocalNormal;
                sStandoff = 1f;
                sPendingDown = sPendingUp = false;
                sJumpAsked = false;
                sAttached = true;
                player.angularVelocity = Vector3.zero;
                Plugin.Log.LogInfo($"[Flight] deck boots on: '{hit.collider.name}' {hit.distance:0.00} m below the suit's middle, " +
                                   $"{Vector3.Angle(hit.normal, -down):0} degrees off the bow, coming in at {relative.magnitude:0.0} m/s.");
                sJumpStart = -10f;
                return true;
            }
        }

        // locked on: walking, stepping and the standing hold
        static bool Hold(Rigidbody player, Vector3 relative, Vector3 shipVelocity, float height, bool pulled, float dt, out Vector3 result)
        {
            result = shipVelocity + relative;

            // the deck point and normal where the ship is now (in the last step's world)
            sPoint = ToWorld(sLocalPoint);
            sNormal = ToWorldDir(sLocalNormal).normalized;
            sSmoothedLocal = Vector3.Slerp(sSmoothedLocal, sLocalNormal, dt * CornerSmoothing).normalized;
            var smoothed = ToWorldDir(sSmoothedLocal).normalized;

            // stand upright on the deck: the ship's turn this step first, then roll and yaw eased onto the deck's normal,
            // keeping the look pitch (limited looking down)
            var turn = FlightModel.Rotation * Quaternion.Inverse(FlightModel.PrevRotation);
            var rotation = turn * player.rotation;
            var upNow = (FlightModel.Rotation * sSmoothedLocal).normalized;
            var rawForward = rotation * Vector3.forward;
            var flatForward = Vector3.ProjectOnPlane(rawForward, upNow);
            bool reorienting = false;
            float pitch = 0f;
            if (flatForward.sqrMagnitude > 1e-4f)
            {
                flatForward.Normalize();
                var rightAxis = Vector3.Cross(upNow, flatForward).normalized;
                pitch = Mathf.Clamp(Vector3.SignedAngle(flatForward, rawForward, rightAxis), -MaxLookUp, MaxLookDown);
                var target = Quaternion.AngleAxis(pitch, rightAxis) * Quaternion.LookRotation(flatForward, upNow);
                reorienting = Quaternion.Angle(rotation, target) > ReorientSettledAngle;
                rotation = Quaternion.Slerp(rotation, target, dt * 10f);
            }
            player.MoveRotation(rotation);
            flatForward = Vector3.ProjectOnPlane(player.rotation * Vector3.forward, smoothed);

            var move = MoveInput(player.rotation, smoothed);
            float intensity = Mathf.Clamp01(move.magnitude);
            float pitchLerp = Mathf.InverseLerp(MaxStridePitch, MinStridePitch, Mathf.Abs(pitch));
            float stride = Mathf.Lerp(AheadCastDistance, 0.01f, pitchLerp);
            var feet = player.position - smoothed * (height * sStandoff);
            float speed = sRun ? RunSpeed : WalkSpeed;

            if (sPendingDown)
            {
                sPendingTimer += dt;
                var lateral = Vector3.ProjectOnPlane(sPoint - player.position, sNormal);
                if (lateral.magnitude <= AheadCastDistance * StepLateralSettled || sPendingTimer >= StepTimeout)
                {
                    var offset = sPoint - sPendingPoint;
                    SetAttach(sPoint - Vector3.Dot(offset, sPendingNormal) * sPendingNormal, sPendingNormal);
                    sPendingDown = false;
                }
            }
            else if (sPendingUp)
            {
                sPendingTimer += dt;
                var standAt = sPoint + sNormal * (height * sStandoff);
                if (Mathf.Abs(Vector3.Dot(standAt - player.position, sNormal)) <= AheadCastDistance * StepUpSettled || sPendingTimer >= StepTimeout)
                    sPendingUp = false;
            }
            else if (!reorienting && move.sqrMagnitude > 1e-4f)
            {
                float maxCast = stride * intensity;
                bool ahead = flatForward.sqrMagnitude > 1e-4f && Vector3.Angle(move, flatForward) <= FwdSweepAngle * 0.5f;
                float maxAngle = ahead ? MaxNormalFwdAngle : MaxNormalAngle;
                float stepUp = ahead ? StepUpFwdHeight : StepUpHeight;
                var candidates = FindStepCandidates(player, feet, smoothed, move, smoothed * stepUp, stepUp + StepDownHeight, maxAngle, maxCast, pitch);
                if (candidates.Count > 0)
                {
                    var winner = candidates[0];
                    var hit = winner.Hit;
                    float fraction = maxCast > 1e-4f ? winner.CastDistance / maxCast : 0f;
                    var candidatePoint = feet + move * speed * dt * fraction;
                    float rise = Vector3.Dot(hit.point - feet, sNormal);
                    if (rise < -StepSignificantHeight)
                    {
                        // a step down: move ahead on the current level now, drop once the body has caught up
                        SetAttach(candidatePoint - Vector3.Dot(candidatePoint - feet, sNormal) * sNormal, sNormal);
                        sPendingDown = true;
                        sPendingTimer = 0f;
                        sPendingPoint = hit.point;
                        sPendingNormal = hit.normal;
                    }
                    else if (rise > StepSignificantHeight)
                    {
                        // a step up: rise now, move ahead once the body has caught up
                        var currentLateral = feet - Vector3.Dot(feet, hit.normal) * hit.normal;
                        SetAttach(currentLateral + Vector3.Dot(hit.point, hit.normal) * hit.normal, hit.normal);
                        sPendingUp = true;
                        sPendingTimer = 0f;
                    }
                    else
                    {
                        var target = candidatePoint - Vector3.Dot(candidatePoint - hit.point, hit.normal) * hit.normal;
                        var targetLateral = target - Vector3.Dot(target, hit.normal) * hit.normal;
                        float eased = Mathf.MoveTowards(Vector3.Dot(sPoint, hit.normal), Vector3.Dot(target, hit.normal), StepFollowSpeed * dt);
                        SetAttach(targetLateral + eased * hit.normal, hit.normal);
                    }
                }
                else if (TryFindValidSurface(player, player.position, -smoothed, height + StepDownHeight, maxAngle, out var recovery, out _))
                    SetAttach(recovery.point, recovery.normal);      // nothing ahead: re-anchor to what is underfoot
                else if (pulled)
                {
                    Detach("stepped off an edge", player);
                    return false;
                }
            }
            else if (!sWasMoving && move.sqrMagnitude <= 1e-4f && Time.time >= sNextRecheck)
            {
                // standing still: now and then make sure the deck is still there (a plate cut away underfoot)
                sNextRecheck = Time.time + 0.5f;
                if (!TryFindValidSurface(player, player.position, -smoothed, height + StepDownHeight, MaxNormalFwdAngle, out _, out var why))
                {
                    Detach($"no deck underfoot: {why}", player);
                    return false;
                }
            }
            sWasMoving = move.sqrMagnitude > 1e-4f;

            // crouch while thrust down is held
            float vertical = LynxControls.Instance != null
                ? LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveUpDownComposite) : 0f;
            sStandoff = Mathf.MoveTowards(sStandoff, vertical < -1e-4f ? 0.5f : 1f, dt / CrouchTransition);

            // the standing hold: a stiff spring to standing height over the deck point, its damper aimed at walking speed
            var desired = sPoint + smoothed * (height * sStandoff);
            var error = desired - player.position;
            var wantRelative = move * speed;
            relative += (Spring * error + Damper * (wantRelative - relative)) * dt;
            relative = Vector3.ClampMagnitude(relative, MaxHoldSpeed);
            result = shipVelocity + relative;

            return true;
        }

        static float sNextRecheck;

        static Vector3 MoveInput(Quaternion rotation, Vector3 normal)
        {
            if (LynxControls.Instance == null) return Vector3.zero;
            float right = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveLeftRightComposite);
            float forward = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveBackForwardComposite);
            var r = Vector3.ProjectOnPlane(rotation * Vector3.right, normal).normalized;
            var f = Vector3.ProjectOnPlane(rotation * Vector3.forward, normal).normalized;
            return r * right + f * forward;
        }

        readonly struct StepCandidate
        {
            internal readonly RaycastHit Hit;
            internal readonly float CastDistance, RoundedHeight;
            internal StepCandidate(RaycastHit hit, float castDistance, float roundedHeight) { Hit = hit; CastDistance = castDistance; RoundedHeight = roundedHeight; }
        }

        static readonly List<StepCandidate> sCandidates = new List<StepCandidate>();

        // MagBoots' stride search: ten probes along the way, each cast down from step-up height through step-down depth;
        // looking up or level the highest tread wins, looking down the lowest, the farthest breaking ties.
        static List<StepCandidate> FindStepCandidates(Rigidbody player, Vector3 feet, Vector3 normal, Vector3 move, Vector3 stepUpOffset,
            float depth, float maxAngle, float maxCast, float pitch)
        {
            sCandidates.Clear();
            var dir = move.normalized;
            const int Steps = 10;
            for (int i = 1; i <= Steps; i++)
            {
                float distance = maxCast * i / Steps;
                var origin = feet + dir * distance + stepUpOffset;
                if (!TryFindValidSurface(player, origin, -normal, depth, maxAngle, out var hit, out _)) continue;
                float h = Vector3.Dot(hit.point - feet, sNormal);
                sCandidates.Add(new StepCandidate(hit, distance, Mathf.Round(h * 10f) / 10f));
            }
            if (-pitch >= 0f)
                sCandidates.Sort((a, b) => { int c = b.RoundedHeight.CompareTo(a.RoundedHeight); return c != 0 ? c : b.CastDistance.CompareTo(a.CastDistance); });
            else
                sCandidates.Sort((a, b) => { int c = a.RoundedHeight.CompareTo(b.RoundedHeight); return c != 0 ? c : b.CastDistance.CompareTo(a.CastDistance); });
            return sCandidates;
        }

        // The nearest of the ship's own (carried) surfaces along the cast, if it faces back along it closely enough and its
        // face is big enough to stand on.
        static bool TryFindValidSurface(Rigidbody player, Vector3 origin, Vector3 direction, float maxDistance, float maxAngle,
            out RaycastHit result, out string why)
        {
            result = default;
            int count = Physics.RaycastNonAlloc(origin, direction, sHits, maxDistance, ~0, QueryTriggerInteraction.Ignore);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                var hit = sHits[i];
                if (hit.collider == null || hit.distance <= 0f) continue;
                var body = hit.collider.attachedRigidbody;
                if (body == null || body == player || !FlightModel.IsCarried(body)) continue;
                if (!found || hit.distance < result.distance) { result = hit; found = true; }
            }
            if (!found) { why = "no hit"; return false; }
            float angle = Vector3.Angle(result.normal, -direction);
            if (angle > maxAngle) { why = $"angle {angle:0.0} > {maxAngle:0.0}"; return false; }
            if (!HasMinimumFaceArea(result, MinFaceArea)) { why = "face area too small"; return false; }
            why = "";
            return true;
        }

        static bool HasMinimumFaceArea(RaycastHit hit, float min)
        {
            if (!(hit.collider is MeshCollider meshCollider) || meshCollider.sharedMesh == null) return true;
            var mesh = meshCollider.sharedMesh;
            if (!sMeshes.TryGetValue(mesh, out var data))
            {
                data = mesh.isReadable ? (mesh.triangles, mesh.vertices) : (new int[0], new Vector3[0]);
                sMeshes[mesh] = data;
            }
            int tri = hit.triangleIndex;
            if (tri < 0 || tri * 3 + 2 >= data.triangles.Length) return true;
            var t = hit.collider.transform;
            var a = t.TransformPoint(data.vertices[data.triangles[tri * 3]]);
            var b = t.TransformPoint(data.vertices[data.triangles[tri * 3 + 1]]);
            var c = t.TransformPoint(data.vertices[data.triangles[tri * 3 + 2]]);
            return Vector3.Cross(b - a, c - a).magnitude * 0.5f >= min;
        }

        // MagBoots' precise look while locked on: each frame's look input turns the view directly, with none of the
        // floating suit's lingering spin.
        [HarmonyPatch(typeof(OrientationController), "HandleAxisRotation")]
        static class PreciseLook
        {
            static readonly System.Reflection.FieldInfo? sBody = AccessTools.Field(typeof(OrientationController), "mRigidbodyReference");
            static readonly System.Reflection.PropertyInfo? sMouse = AccessTools.Property(typeof(OrientationController), "MouseSensitivity");
            static readonly System.Reflection.PropertyInfo? sPad = AccessTools.Property(typeof(OrientationController), "ControllerSensitivity");
            static readonly System.Reflection.FieldInfo? sMouseCurve = AccessTools.Field(typeof(OrientationController), "m_MouseSensitivityCurve");
            static readonly System.Reflection.FieldInfo? sPadCurve = AccessTools.Field(typeof(OrientationController), "m_ControllerSensitivityCurve");
            static readonly System.Reflection.FieldInfo? sDeadzone = AccessTools.Field(typeof(OrientationController), "m_OrientationDeadzone");

            static bool Prefix(OrientationController __instance)
            {
                if (!sAttached || !Plugin.Enabled.Value || LynxControls.Instance == null) return true;
                if (!(sBody?.GetValue(__instance) is Rigidbody body)) return true;
                var type = LynxControls.Instance.LastInputType;
                bool mouse = type == BindingSourceType.MouseBindingSource || type == BindingSourceType.KeyBindingSource;
                var axis = LynxControls.Instance.GetTwoAxisInputControlVector(GameplayActions.GameplayActionSet.RotateBodyComposite);
                Vector3 sensitivity;
                AnimationCurve? curve;
                if (mouse)
                {
                    sensitivity = sMouse?.GetValue(__instance) is Vector3 m ? m : Vector3.one;
                    curve = sMouseCurve?.GetValue(__instance) as AnimationCurve;
                }
                else
                {
                    sensitivity = sPad?.GetValue(__instance) is Vector3 p ? p : Vector3.one;
                    curve = sPadCurve?.GetValue(__instance) as AnimationCurve;
                    if (sDeadzone?.GetValue(__instance) is LynxControls.DeadZoneDefinition deadzone) axis = LynxControls.GetDeadzoneVector(axis, deadzone);
                }
                if (axis.sqrMagnitude <= 0f) return false;
                if (curve != null) axis *= curve.Evaluate(axis.magnitude);
                axis.x *= sensitivity.x * LookSensitivity;
                axis.y *= sensitivity.y * LookSensitivity;
                var rotation = body.rotation;
                rotation *= Quaternion.Euler(Vector3.up * axis.x * Time.deltaTime);
                rotation *= Quaternion.Euler(Vector3.right * -axis.y * Time.deltaTime);
                body.MoveRotation(rotation);
                body.angularDrag = 0f;
                return false;
            }
        }
    }
}
