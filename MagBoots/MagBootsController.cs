using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace MagBoots
{
    internal enum MagBootsState
    {
        Off,
        Locking,
        Locked,
        Error,
        NoPower,
    }

    internal class MagBootsController
    {
        private GrabController? _grabController;
        private Rigidbody? _playerRigidbody;
        private Camera? _playerCamera;

        private MagBootsState _state = MagBootsState.Off;

        // _attachNormal is the raw target normal, updated instantly whenever the ahead-cast finds a new
        // surface (e.g. crossing a sharp corner, where it can swing ~45 degrees in a single tick).
        // _smoothedNormal eases toward it instead of being used directly, and is what every other
        // calculation reads from - rotation, the standoff position offset, AND the ahead-cast's own
        // direction/origin. Smoothing only the standoff offset while leaving rotation and the ahead-cast
        // on the raw normal caused the two to fall out of sync for several ticks after a corner: the
        // raycast would fire from a position/direction pair that no longer matched where the player
        // physically was yet, intermittently failing or hitting inconsistently and producing jitter.
        // Keeping everything derived from the same smoothed value keeps position, orientation, and the
        // next raycast all transitioning through a corner together, at the same pace.
        private Vector3 _attachNormal;
        private Vector3 _smoothedNormal;
        private Vector3 _attachPoint;
        private Transform? _attachHitTransform;

        // Eases between 1.0 (standing) and 0.5 (crouched) while the thrust-down input is held, rather
        // than snapping instantly, so crouching in/out of a low gap or under an obstacle feels like a
        // deliberate crouch rather than a jarring pop.
        private float _standoffFraction = 1f;
        private const float CrouchTransitionDuration = 0.25f;

        private float _snapTimer;
        private Vector3 _snapStartPos;
        private Quaternion _snapStartRot;
        private Vector3 _snapTargetPos;
        private Quaternion _snapTargetRot;

        private float _errorTimer;

        private float _batteryMinutesRemaining;

        // Run: Toggle mode flips _runToggledOn on each OnRunTogglePressed() call; Hold mode instead
        // drives _runHeld directly every frame from Plugin.Update(). IsRunning reads whichever one is
        // relevant for the configured mode, so UpdateAttached doesn't need to know which mode is active.
        // Both setters are gated on IsAttached - without that, toggling/holding Run while detached would
        // silently "arm" it with no visible feedback (the HUD chip only shows Run's state while attached
        // too), and the player would start running the instant they attached with no separate action.
        private bool _runToggledOn;
        private bool _runHeld;
        public bool IsRunning => IsAttached && (Plugin.ConfigRunActivationMode.Value == RunActivationMode.Toggle ? _runToggledOn : _runHeld);

        public void OnRunTogglePressed()
        {
            if (!IsAttached)
                return;

            _runToggledOn = !_runToggledOn;
        }

        public void SetRunHeld(bool held) => _runHeld = IsAttached && held;

        public MagBootsState State => _state;
        public bool IsAttached => _state == MagBootsState.Locked;
        public float BatteryFraction => Plugin.ConfigBatteryCapacityMinutes.Value <= 0f
            ? 1f
            : Mathf.Clamp01(_batteryMinutesRemaining / Plugin.ConfigBatteryCapacityMinutes.Value);
        public float BatteryMinutesRemaining => _batteryMinutesRemaining;

        private bool TryCacheReferences()
        {
            if (_playerRigidbody != null)
                return true;

            _grabController = Object.FindObjectOfType<GrabController>();
            if (_grabController == null)
                return false;

            var traverse = Traverse.Create(_grabController);
            _playerRigidbody = traverse.Field("m_PlayerRigidbodyReference").GetValue<Rigidbody>();
            _playerCamera = traverse.Field("m_PlayerCamera").GetValue<Camera>();
            return _playerRigidbody != null;
        }

        public void OnTogglePressed()
        {
            if (!TryCacheReferences())
            {
                Plugin.Log.LogWarning("MagBoots: could not find player rigidbody, ignoring toggle.");
                return;
            }

            if (_state == MagBootsState.Locked || _state == MagBootsState.Locking)
            {
                Detach();
                return;
            }

            if (BatteryDepleted())
            {
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: battery depleted, cannot attach.");
                return; // Stays in/enters NoPower - handled continuously in FixedUpdate.
            }

            TryAttach();
        }

        private bool BatteryDepleted() =>
            Plugin.ConfigBatteryCapacityMinutes.Value > 0f && _batteryMinutesRemaining <= 0f;

        private void TryAttach()
        {
            Transform playerTransform = _playerRigidbody!.transform;
            Vector3 origin = playerTransform.position;
            Vector3 castDir = -playerTransform.up;

            // "How far below your feet mag boots will look for something to attach to" is measured from
            // the viewpoint down through the player's full standing height (PlayerHeight) plus the
            // step-down allowance (StepDownHeight) - not just StepDownHeight alone, since the viewpoint
            // itself sits PlayerHeight above where the feet would actually land.
            float castDistance = Plugin.ConfigPlayerHeight.Value + Plugin.ConfigStepDownHeight.Value;

            if (!TryFindValidSurface(origin, castDir, castDistance, Plugin.ConfigMaxNormalAngle.Value, out RaycastHit hit))
            {
                EnterError();
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: no valid surface found below player.");
                return;
            }

            BeginSnap(hit.point, hit.normal, hit.transform);
        }

        private void EnterError()
        {
            _state = MagBootsState.Error;
            _errorTimer = 0f;
        }

        private void BeginSnap(Vector3 point, Vector3 normal, Transform hitTransform)
        {
            _attachPoint = point;
            _attachNormal = normal;
            _smoothedNormal = normal;
            _attachHitTransform = hitTransform;
            _standoffFraction = 1f;

            _snapStartPos = _playerRigidbody!.position;
            _snapStartRot = _playerRigidbody.rotation;
            _snapTargetPos = point + normal * Plugin.ConfigPlayerHeight.Value;
            Vector3 currentUp = _snapStartRot * Vector3.up;
            _snapTargetRot = Quaternion.FromToRotation(currentUp, normal) * _snapStartRot;

            _snapTimer = 0f;
            _state = MagBootsState.Locking;

            _playerRigidbody.velocity = Vector3.zero;
            _playerRigidbody.angularVelocity = Vector3.zero;

            // Suppress from the start of the snap tween, not just once fully Locked - thrust fighting the
            // snap-in would be just as pointless as thrust fighting the standoff spring once attached.
            ThrustSuppression.SetSuppressed(true);

            if (Plugin.ConfigDebugPrint.Value)
                Plugin.Log.LogInfo($"MagBoots: attaching to surface at {point}, normal {normal}.");
        }

        private void Detach()
        {
            _state = MagBootsState.Off;
            _attachHitTransform = null;
            ThrustSuppression.SetSuppressed(false);

            // Run never carries over to the next attach - it always starts back off, so running is
            // always a deliberate action taken after attaching, never something left primed from before.
            _runToggledOn = false;
            _runHeld = false;

            if (Plugin.ConfigDebugPrint.Value)
                Plugin.Log.LogInfo("MagBoots: detached.");
        }

        // Called on the Gameplay-after-LoadingComplete transition (start of a new shift), the same
        // hook GrabController itself uses for its own per-shift resets.
        public void OnShiftStart()
        {
            _batteryMinutesRemaining = Plugin.ConfigBatteryCapacityMinutes.Value;
            if (_state != MagBootsState.Off)
                Detach();
        }

        // Called whenever the game state leaves flight Gameplay (pause, Hab, loading, NIS, etc.), since
        // Plugin stops calling Update/FixedUpdate outside Gameplay - without this, a player who paused
        // mid-snap or mid-attach would stay stuck in that state with stale forces/tween data when they
        // resumed, even though nothing was ticking to progress or clean it up.
        public void OnLeaveGameplay()
        {
            if (_state == MagBootsState.Locking || _state == MagBootsState.Locked)
                Detach();
        }

        public void FixedUpdate()
        {
            if (_state == MagBootsState.Error)
            {
                _errorTimer += Time.fixedDeltaTime;
                if (_errorTimer >= Mathf.Max(Plugin.ConfigSnapDuration.Value, 0.01f))
                    _state = BatteryDepleted() ? MagBootsState.NoPower : MagBootsState.Off;
                return;
            }

            if (_state == MagBootsState.Off || _state == MagBootsState.NoPower)
            {
                // Idle: continuously reflect current battery state so NoPower shows the instant the
                // battery empties, and clears automatically if it's ever topped up (e.g. OnShiftStart).
                _state = BatteryDepleted() ? MagBootsState.NoPower : MagBootsState.Off;
                return;
            }

            if (_state != MagBootsState.Locking && _state != MagBootsState.Locked)
                return;

            if (!TryCacheReferences())
            {
                Detach();
                return;
            }

            if (_state == MagBootsState.Locking)
            {
                UpdateSnap();
                DrainBattery(Plugin.ConfigLockingPowerMultiplier.Value);
                return;
            }

            bool wasStandingStill = !_lastTangentialMoveWasActive;
            UpdateAttached();

            float movementMultiplier = wasStandingStill
                ? Plugin.ConfigIdlePowerMultiplier.Value
                : (IsRunning ? Plugin.ConfigRunSpeed.Value / Plugin.ConfigMoveSpeed.Value : 1f);
            DrainBattery(movementMultiplier);
        }

        // The initial snap (Locking) drains at LockingPowerMultiplier - at the default SnapDuration of 1
        // second and multiplier of 10, that's equivalent to 10 seconds of normal attached drain, so
        // frequent attach/detach cycling isn't a free way to dodge battery cost. While actually attached,
        // standing still (no tangential move input) drains at only IdlePowerMultiplier; while running,
        // drain scales proportionally with how much faster RunSpeed is than MoveSpeed. Both flags are
        // captured from the previous tick's UpdateAttached rather than recomputed here, since tangential
        // input is only read inside that method.
        private bool _lastTangentialMoveWasActive;

        private void DrainBattery(float multiplier)
        {
            if (Plugin.ConfigBatteryCapacityMinutes.Value <= 0f)
                return; // 0 or negative = unlimited/disabled battery.

            _batteryMinutesRemaining -= Time.fixedDeltaTime / 60f * multiplier;
            if (_batteryMinutesRemaining <= 0f)
            {
                _batteryMinutesRemaining = 0f;
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: battery depleted, detaching.");
                Detach();
            }
        }

        private void UpdateSnap()
        {
            _snapTimer += Time.fixedDeltaTime;
            float duration = Mathf.Max(Plugin.ConfigSnapDuration.Value, 0.01f);
            float t = Mathf.Clamp01(_snapTimer / duration);
            float eased = Mathf.SmoothStep(0f, 1f, t);

            Vector3 pos = Vector3.Lerp(_snapStartPos, _snapTargetPos, eased);
            Quaternion rot = Quaternion.Slerp(_snapStartRot, _snapTargetRot, eased);

            _playerRigidbody!.MovePosition(pos);
            _playerRigidbody.MoveRotation(rot);

            if (t >= 1f)
            {
                _state = MagBootsState.Locked;
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: snap complete, now attached.");
            }
        }

        private void UpdateAttached()
        {
            Rigidbody rb = _playerRigidbody!;
            Transform playerTransform = rb.transform;

            // A strong enough impact (e.g. recoil) should be able to knock the player free instead of
            // the standoff spring always winning and snapping them back - the spring's restoring force
            // scales unboundedly with displacement/velocity, so without this check nothing could ever
            // pull the player away from an attached surface.
            if (rb.velocity.magnitude > Plugin.ConfigBreakawayVelocity.Value)
            {
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"MagBoots: breakaway - velocity={rb.velocity.magnitude} exceeded {Plugin.ConfigBreakawayVelocity.Value}, detaching.");
                Detach();
                return;
            }

            // Ease _smoothedNormal toward the raw target (_attachNormal, updated by the ahead-cast below)
            // rather than any of the rest of this method reading _attachNormal directly - rotation, the
            // tangential move direction, the ahead-cast's own origin/direction, and the standoff offset
            // all derive from _smoothedNormal instead, so they all transition through a sharp corner
            // together at the same pace. Using the raw normal for some of these (e.g. the ahead-cast)
            // while smoothing only the standoff offset let the raycast fire from a position/direction
            // pair that no longer matched where the player physically was yet, causing it to intermittently
            // fail or hit inconsistently around corners and produce jitter.
            _smoothedNormal = Vector3.Slerp(_smoothedNormal, _attachNormal, Time.fixedDeltaTime * Plugin.ConfigCornerSmoothingSpeed.Value).normalized;

            // Re-orient roll/yaw so "down" stays aligned with the smoothed normal, without touching
            // pitch: rebuild a target rotation from the player's own *flattened* forward (its pitch
            // component discarded) plus the smoothed normal as up, then re-apply the original pitch
            // (clamped so the player can't tip past ConfigMaxLookDownAngle toward the surface) on top.
            // This way pitch (torque already applied by the game's own OrientationController) is fully
            // preserved except for the clamp, and only roll/yaw drift relative to the surface - e.g. from
            // walking onto a new face - gets corrected.
            Vector3 rawForward = playerTransform.forward;
            Vector3 flatForward = Vector3.ProjectOnPlane(rawForward, _smoothedNormal);
            // Settle check compares against the *level* (pitch-free) orientation, not the player's full
            // rotation - pitch alone also tilts playerTransform.up away from the smoothed normal, so
            // comparing raw up vectors falsely read "still reorienting" forever whenever the player
            // looked up/down, blocking all forward movement even on a flat surface.
            bool isReorienting = false;
            if (flatForward.sqrMagnitude > 0.0001f)
            {
                flatForward.Normalize();
                Vector3 rightAxis = Vector3.Cross(_smoothedNormal, flatForward).normalized;
                float pitchAngle = Vector3.SignedAngle(flatForward, rawForward, rightAxis);
                float clampedPitch = Mathf.Min(pitchAngle, Plugin.ConfigMaxLookDownAngle.Value);

                Quaternion levelRot = Quaternion.LookRotation(flatForward, _smoothedNormal);
                Quaternion pitchRot = Quaternion.AngleAxis(clampedPitch, rightAxis);
                Quaternion targetRot = pitchRot * levelRot;

                isReorienting = Quaternion.Angle(playerTransform.rotation, targetRot) > Plugin.ConfigReorientSettledAngle.Value;
                rb.MoveRotation(Quaternion.Slerp(playerTransform.rotation, targetRot, Time.fixedDeltaTime * 10f));
            }

            Vector3 tangentialMove = ReadTangentialMoveInput(playerTransform, _smoothedNormal);
            _lastTangentialMoveWasActive = tangentialMove.sqrMagnitude > 0.0001f;

            // Pause looking for the next surface while still catching up to the last normal change -
            // recasting mid-reorientation was casting at a still-rotating angle and could pick up a
            // slightly different/adjacent face before settling, producing visible jitter.
            if (!isReorienting && tangentialMove.sqrMagnitude > 0.0001f)
            {
                // Scale the max cast distance down for a gentle analog push (e.g. a controller stick
                // barely tilted) rather than always probing the full AheadCastDistance regardless of how
                // fast the player is actually moving - magnitude is clamped to 1 since a full diagonal
                // push can exceed unit length (two unit axis vectors summed).
                float moveIntensity = Mathf.Clamp01(tangentialMove.magnitude);
                float maxCastDistance = Plugin.ConfigAheadCastDistance.Value * moveIntensity;

                // The probe starts StepUpHeight above the current feet-plane (so a slightly higher step
                // can still be found) and casts down through StepUpHeight + StepDownHeight total, reaching
                // that far below the current feet-plane too - one raycast covers both a step up (within
                // StepUpHeight) and a step down (within StepDownHeight) in a single pass.
                Vector3 stepUpOffset = _smoothedNormal * Plugin.ConfigStepUpHeight.Value;
                float aheadDepth = Plugin.ConfigStepUpHeight.Value + Plugin.ConfigStepDownHeight.Value;

                // A foothold within a 60 degree sweep of where the player is facing gets the looser
                // MaxNormalFwdAngle instead of MaxNormalAngle, so you can walk up a steeper ramp you're
                // heading toward; anything off to the side or behind still needs the stricter
                // MaxNormalAngle. Reuses flatForward (the player's own forward, already flattened onto
                // the surface and pitch-free) rather than the camera's raw forward - the camera's forward
                // collapses toward _smoothedNormal (and its plane-projection toward zero length) whenever
                // the player pitches their view down at the ground, which made this sweep check
                // incorrectly fail - and thus fall back to the strict angle - any time you looked down
                // while walking.
                bool inFwdSweep = flatForward.sqrMagnitude > 0.0001f &&
                    Vector3.Angle(tangentialMove, flatForward) <= 30f; // 30 either side = 60 degree sweep.
                float maxNormalAngle = inFwdSweep ? Plugin.ConfigMaxNormalFwdAngle.Value : Plugin.ConfigMaxNormalAngle.Value;

                // Mimics an actual stride: try the full step first, and if that lands over a gap/narrow
                // lip with nothing below it, reel the probe back in tenth-increments toward the player's
                // current position instead of giving up outright. This means a narrow gap with solid
                // ground just beyond it still gets crossed in one step (the far probe hits), while a real
                // ledge lets the player creep forward with progressively shorter steps right up to the
                // edge, rather than freezing dead the instant the full-length probe first comes up empty.
                bool foundSurface = false;
                RaycastHit aheadHit = default;
                float hitCastDistance = 0f;
                const int castSteps = 10;
                for (int i = castSteps; i >= 1; i--)
                {
                    float castDistance = maxCastDistance * i / castSteps;
                    // Anchored to _attachPoint (the actual feet-plane on the surface), not rb.position -
                    // the player's body floats PlayerHeight above _attachPoint, so building the origin from
                    // rb.position stacked PlayerHeight on top of StepUpHeight (e.g. 1.5 + 0.75 = 2.25m above
                    // the surface by default) while aheadDepth was only StepUpHeight + StepDownHeight
                    // (2.25m) - the cast landed exactly AT surface height with zero clearance, so it missed
                    // constantly from spring-settling jitter alone, even standing on a dead-flat floor.
                    Vector3 aheadOrigin = _attachPoint + tangentialMove.normalized * castDistance + stepUpOffset;
                    if (TryFindValidSurface(aheadOrigin, -_smoothedNormal, aheadDepth, maxNormalAngle, out aheadHit))
                    {
                        foundSurface = true;
                        hitCastDistance = castDistance;
                        break;
                    }
                }

                if (foundSurface)
                {
                    // Advance the anchor only as far as the successful probe's step distance, not the
                    // full requested stride - so a shortened step (from reeling in near a ledge) actually
                    // moves the player a correspondingly shorter distance this tick, rather than the full
                    // MoveSpeed-paced amount regardless of how close the edge turned out to be. Scaled as
                    // a fraction of the full stride (hitCastDistance / maxCastDistance) so a step reeled
                    // in to e.g. half the max distance also only advances the anchor half as far.
                    float strideFraction = maxCastDistance > 0.0001f ? hitCastDistance / maxCastDistance : 0f;
                    float moveSpeed = IsRunning ? Plugin.ConfigRunSpeed.Value : Plugin.ConfigMoveSpeed.Value;
                    Vector3 fullStride = tangentialMove * moveSpeed * Time.fixedDeltaTime;
                    Vector3 candidatePoint = _attachPoint + fullStride * strideFraction;

                    // Re-snap the anchor onto the new surface's plane (height/orientation) rather than
                    // forward along it - standard point-onto-plane projection: subtract out the component
                    // of (candidatePoint - aheadHit.point) along the new normal, which keeps the anchor's
                    // incrementally-advanced position but adopts the new surface's height, e.g. when
                    // stepping onto a lower/higher/angled face.
                    Vector3 offset = candidatePoint - aheadHit.point;
                    _attachPoint = candidatePoint - Vector3.Dot(offset, aheadHit.normal) * aheadHit.normal;
                    _attachNormal = aheadHit.normal;
                    _attachHitTransform = aheadHit.transform;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: ahead-cast hit - moveIntensity={moveIntensity}, hitCastDistance={hitCastDistance}, normalAngle={Vector3.Angle(aheadHit.normal, _smoothedNormal)}");
                }
                else if (Plugin.ConfigDebugPrint.Value)
                {
                    Plugin.Log.LogInfo($"MagBoots: ahead-cast MISSED at all step distances - moveIntensity={moveIntensity}, maxCastDistance={maxCastDistance}, position={rb.position}, dir={-_smoothedNormal}, maxDepth={aheadDepth}");
                }
                // else: no surface at any step distance, all the way down to the player's own position
                // (e.g. a genuine full-width gap/edge) - hold the last valid attach point/normal entirely
                // (per design), so the player doesn't keep sliding forward over open space.
            }

            // Crouch: while thrust-down is held, ease the standoff distance toward half its configured
            // value instead of snapping instantly, so ducking under a low obstacle feels deliberate
            // rather than a jarring pop. Eases back to full the same way on release.
            float verticalAxis = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveUpDownComposite);
            bool crouchHeld = verticalAxis < -0.0001f;
            float targetStandoffFraction = crouchHeld ? 0.5f : 1f;
            _standoffFraction = Mathf.MoveTowards(_standoffFraction, targetStandoffFraction, Time.fixedDeltaTime / CrouchTransitionDuration);

            Vector3 desiredPos = _attachPoint + _smoothedNormal * (Plugin.ConfigPlayerHeight.Value * _standoffFraction);

            Vector3 springForce = Plugin.ConfigSpring.Value * (desiredPos - rb.position)
                                   + Plugin.ConfigDamper.Value * (Vector3.zero - rb.velocity);
            rb.AddForce(springForce, ForceMode.Acceleration);
        }

        private static Vector3 ReadTangentialMoveInput(Transform playerTransform, Vector3 normal)
        {
            float rightAxis = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveLeftRightComposite);
            float forwardAxis = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveBackForwardComposite);

            Vector3 right = Vector3.ProjectOnPlane(playerTransform.right, normal).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(playerTransform.forward, normal).normalized;

            return right * rightAxis + forward * forwardAxis;
        }

        private bool TryFindValidSurface(Vector3 origin, Vector3 direction, float maxDistance, float maxNormalAngle, out RaycastHit result)
        {
            LayerMask mask = Main.Instance.MainSettings.RaycastSettings.GrabValidLayerMask;

            if (!Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, mask))
            {
                result = default;
                return false;
            }

            float angle = Vector3.Angle(hit.normal, -direction);
            if (angle > maxNormalAngle)
            {
                result = default;
                return false;
            }

            if (!SurfaceGeometry.HasMinimumFaceArea(hit, Plugin.ConfigMinFaceArea.Value))
            {
                result = default;
                return false;
            }

            result = hit;
            return true;
        }
    }
}
