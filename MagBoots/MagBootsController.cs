using System.Collections.Generic;
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

        // Splits a detected step into two phases instead of one combined diagonal move: _attachPoint holds
        // still (no further lateral advance, no new ahead-cast) the instant a real step is detected, and
        // the vertical/normal correction only applies once the player's ACTUAL position has physically
        // caught up to that held point (via the standoff spring), or Timeout_Step elapses, whichever comes
        // first - rather than applying the lateral move and a large vertical drop in the same tick.
        // Without this split, a steep staircase combined a full lateral stride with a large vertical drop
        // every tick, compounding downward velocity until it exceeded BreakawayVelocity - the fixed
        // horizontal Distance_MaxStride stride translates into a much bigger vertical change per tick on
        // steep stairs than on shallow ones, since it doesn't account for slope. Holding the anchor fixed
        // (rather than continuing to advance it every tick with no raycast validation at all) also avoids
        // it silently racing ahead of the player and skipping over an intermediate step or ledge.
        private bool _pendingStepCorrection;
        private float _pendingStepTimer;
        private Vector3 _pendingStepPoint;
        private Vector3 _pendingStepNormal;
        private Transform? _pendingStepHitTransform;

        // Mirror of the step-down pending fields above, but inverted: a significant step UP snaps the
        // anchor's height/normal onto the new surface immediately (no risk of compounding downward
        // velocity the way a step down has), then HOLDS lateral advance until the player's body has
        // physically caught up to the new standoff position (or Timeout_Step elapses) before resuming
        // forward movement. Without this, a steep step up combined a full lateral stride with an instant
        // vertical snap in the same tick, which visually looked like teleporting forward and up onto the
        // tread rather than climbing it.
        private bool _pendingStepUpCorrection;
        private float _pendingStepUpTimer;
        private Vector3 _pendingStepUpPoint;
        private Vector3 _pendingStepUpNormal;
        private Transform? _pendingStepUpHitTransform;

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

        // The stride distance actually used this tick, after both the analog-push and pitch scaling are
        // applied - shown in the HUD so the player can see their stride shrink as they pitch their view,
        // making the head-tilt control's effect on movement immediately legible rather than a hidden feel.
        public float CurrentStrideDistance { get; private set; }

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
            // step-down allowance (Height_StepDown) - not just Height_StepDown alone, since the viewpoint
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
            _pendingStepCorrection = false;

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
            PreciseRotation.SetActive(false);

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
        // drain scales proportionally with how much faster Speed_Run is than Speed_Walk. Both flags are
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

                // Only once fully Locked, not from the start of the snap tween (unlike ThrustSuppression) -
                // UpdateSnap itself is still driving rotation via MoveRotation toward _snapTargetRot above,
                // which precise rotation would otherwise immediately fight or override mid-tween.
                PreciseRotation.SetActive(true);

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
            // (clamped so the player can't tip past Pitch_MaxLookDown toward the surface) on top.
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
            float clampedPitch = 0f;
            if (flatForward.sqrMagnitude > 0.0001f)
            {
                flatForward.Normalize();
                Vector3 rightAxis = Vector3.Cross(_smoothedNormal, flatForward).normalized;
                float pitchAngle = Vector3.SignedAngle(flatForward, rawForward, rightAxis);
                // Only the look-down side was ever clamped (Pitch_MaxLookDown) - looking up had no
                // limit at all, and as pitch approaches +/-90 degrees rawForward becomes nearly parallel
                // to _smoothedNormal, degenerating flatForward's direction (and therefore rightAxis) into
                // near-arbitrary noise. That instability could spin the player out when looking straight
                // up. MaxLookUpAngle isn't exposed as a tunable since it exists purely to keep this
                // degenerate case out of reach, not as something players would want to adjust.
                const float MaxLookUpAngle = 85f;
                clampedPitch = Mathf.Clamp(pitchAngle, -MaxLookUpAngle, Plugin.ConfigMaxLookDownAngle.Value);

                Quaternion levelRot = Quaternion.LookRotation(flatForward, _smoothedNormal);
                Quaternion pitchRot = Quaternion.AngleAxis(clampedPitch, rightAxis);
                Quaternion targetRot = pitchRot * levelRot;

                isReorienting = Quaternion.Angle(playerTransform.rotation, targetRot) > Plugin.ConfigReorientSettledAngle.Value;
                rb.MoveRotation(Quaternion.Slerp(playerTransform.rotation, targetRot, Time.fixedDeltaTime * 10f));
            }

            Vector3 tangentialMove = ReadTangentialMoveInput(playerTransform, _smoothedNormal);
            _lastTangentialMoveWasActive = tangentialMove.sqrMagnitude > 0.0001f;

            float moveIntensity = Mathf.Clamp01(tangentialMove.magnitude);

            // Let the player intuitively shorten their stride by pitching their view up or down, rather
            // than always using the full configured distance regardless of where they're looking - pitching
            // down toward steep stairs (or up, symmetrically) naturally slows/shortens the stride, similar
            // to how a person takes smaller, more careful steps while looking down at uneven ground. No
            // change within +/-Pitch_MaxStride degrees of level (ordinary head movement while walking
            // shouldn't affect anything); falls off linearly toward a 0.01m floor at +/-Pitch_MinStride
            // degrees - never all the way to a literal 0m stride, since there's no real benefit to a search
            // distance that short and it would just stall the search entirely. The floor is an absolute
            // distance (not a fraction of Distance_MaxStride), so it stays meaningful even if that's
            // reconfigured away from its default.
            const float MinPitchStrideDistance = 0.01f;
            float pitchMagnitude = Mathf.Abs(clampedPitch);
            float pitchLerpAmount = Mathf.InverseLerp(Plugin.ConfigMaxStridePitch.Value, Plugin.ConfigMinStridePitch.Value, pitchMagnitude);
            float pitchAdjustedCastDistance = Mathf.Lerp(Plugin.ConfigAheadCastDistance.Value, MinPitchStrideDistance, pitchLerpAmount);

            // Preview value only - reflects pitch alone so it's meaningful even while standing still
            // (moveIntensity would otherwise be 0 and always show a 0m stride regardless of pitch). While
            // actually moving, this gets overwritten below with the real value the search used, which also
            // factors in moveIntensity (a gentle analog push shortens the search too).
            CurrentStrideDistance = pitchAdjustedCastDistance;

            // The player's actual current feet-plane position - rb.position (the helmet/POV) undoes the
            // standoff offset. The ahead-cast search (stride offset base, height-deviation reference)
            // reads from THIS, never from _attachPoint - _attachPoint could otherwise already be ahead of
            // where the player's real body was (advanced by a prior tick's committed step), so distances
            // and heights in the search would silently be measured from a stale point instead of from
            // where the player actually stands right now.
            Vector3 feetPosition = rb.position - _smoothedNormal * (Plugin.ConfigPlayerHeight.Value * _standoffFraction);

            // While a step's vertical correction is pending, _attachPoint holds still (no further lateral
            // advance, no new ahead-cast) - the queued height/normal snap applies once the player's ACTUAL
            // position has physically caught up to that held point (the standoff spring pulling them
            // there), or once Timeout_Step elapses, whichever happens first. The timeout exists because the
            // catch-up distance is measured as a fraction of Distance_MaxStride (not a fixed meters value,
            // so it scales with stride length), and continuous movement can keep that fraction from ever
            // fully closing - the timeout guarantees the player is never stuck waiting indefinitely.
            if (_pendingStepCorrection)
            {
                _pendingStepTimer += Time.fixedDeltaTime;

                // Lateral-only distance between the player's actual position and the held anchor, ignoring
                // any difference along the (still old, pre-step) normal - that along-normal gap is expected
                // and irrelevant here; only the tangential component tells us whether the spring has
                // actually caught the player up to where the step was detected.
                Vector3 lateralError = Vector3.ProjectOnPlane(_attachPoint - rb.position, _attachNormal);
                float settleDistance = Plugin.ConfigAheadCastDistance.Value * Plugin.ConfigStepLateralSettled.Value;
                bool lateralSettled = lateralError.magnitude <= settleDistance;
                bool timedOut = _pendingStepTimer >= Plugin.ConfigStepTimeout.Value;

                if (lateralSettled || timedOut)
                {
                    Vector3 offset = _attachPoint - _pendingStepPoint;
                    _attachPoint -= Vector3.Dot(offset, _pendingStepNormal) * _pendingStepNormal;
                    _attachNormal = _pendingStepNormal;
                    _attachHitTransform = _pendingStepHitTransform;
                    _pendingStepCorrection = false;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: deferred step correction applied - lateralError={lateralError.magnitude}, timedOut={timedOut}");
                }
            }
            // Mirror of the step-down pending block above, but height/normal already snapped the instant
            // the step up was detected (see below) - what's held here is LATERAL advance, released once
            // the player's body has physically caught up to the new standoff height (or Timeout_Step
            // elapses), so a steep step up doesn't look like teleporting forward and up in the same tick.
            else if (_pendingStepUpCorrection)
            {
                _pendingStepUpTimer += Time.fixedDeltaTime;

                // Along-normal-only distance between the player's actual position and the STANDOFF height
                // above the anchor (not the anchor/surface point itself - rb.position always sits roughly
                // PlayerHeight above the surface, so comparing it directly against _attachPoint always
                // showed a ~PlayerHeight gap that could never settle, making every step up wait out the
                // full Timeout_Step regardless of how quickly the player actually caught up). The tangential/
                // lateral component is expected and irrelevant here, only whether the player has physically
                // risen to meet the new standoff position.
                Vector3 standoffTarget = _attachPoint + _attachNormal * (Plugin.ConfigPlayerHeight.Value * _standoffFraction);
                float heightError = Mathf.Abs(Vector3.Dot(standoffTarget - rb.position, _attachNormal));
                float settleDistance = Plugin.ConfigAheadCastDistance.Value * Plugin.ConfigStepUpSettled.Value;
                bool heightSettled = heightError <= settleDistance;
                bool timedOut = _pendingStepUpTimer >= Plugin.ConfigStepTimeout.Value;

                if (heightSettled || timedOut)
                {
                    _pendingStepUpCorrection = false;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: deferred step-up lateral release - heightError={heightError}, timedOut={timedOut}");
                }
            }
            // Pause looking for the next surface while still catching up to the last normal change -
            // recasting mid-reorientation was casting at a still-rotating angle and could pick up a
            // slightly different/adjacent face before settling, producing visible jitter.
            else if (!isReorienting && tangentialMove.sqrMagnitude > 0.0001f)
            {
                // Scale the max cast distance down for a gentle analog push (e.g. a controller stick
                // barely tilted) rather than always probing the full Distance_MaxStride regardless of how
                // fast the player is actually moving - magnitude is clamped to 1 since a full diagonal
                // push can exceed unit length (two unit axis vectors summed). Also applies the same
                // pitch-based distance computed above (pitchAdjustedCastDistance), so a shortened-by-pitch
                // stride combines with a gentle analog push rather than either one overriding the other.
                float maxCastDistance = pitchAdjustedCastDistance * moveIntensity;
                // Reflects the REAL distance the search is about to use this tick (not just the pitch-only
                // preview set above), since the player is actually moving now.
                CurrentStrideDistance = maxCastDistance;

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"MagBoots: pitch stride scale - clampedPitch={clampedPitch}, pitchAdjustedCastDistance={pitchAdjustedCastDistance}, maxCastDistance={maxCastDistance}");

                // A foothold within Angle_FwdSweep of where the player is facing gets the looser
                // Angle_MaxNormalFwd/Height_StepUpFwd instead of Angle_MaxNormal/Height_StepUp, so you can
                // walk up a steeper ramp or step up onto a taller ledge you're actually heading toward;
                // anything off to the side or behind still needs the stricter values, which makes it
                // harder to accidentally step up onto something you didn't mean to. Reuses flatForward
                // (the player's own forward, already flattened onto the surface and pitch-free) rather
                // than the camera's raw forward - the camera's forward collapses toward _smoothedNormal
                // (and its plane-projection toward zero length) whenever the player pitches their view
                // down at the ground, which made this sweep check incorrectly fail - and thus fall back
                // to the strict values - any time you looked down while walking.
                float halfSweep = Plugin.ConfigFwdSweepAngle.Value * 0.5f;
                bool inFwdSweep = flatForward.sqrMagnitude > 0.0001f &&
                    Vector3.Angle(tangentialMove, flatForward) <= halfSweep;
                float maxNormalAngle = inFwdSweep ? Plugin.ConfigMaxNormalFwdAngle.Value : Plugin.ConfigMaxNormalAngle.Value;
                float stepUpHeight = inFwdSweep ? Plugin.ConfigStepUpFwdHeight.Value : Plugin.ConfigStepUpHeight.Value;

                // The probe starts stepUpHeight above the current feet-plane (so a slightly higher step
                // can still be found) and casts down through stepUpHeight + Height_StepDown total, reaching
                // that far below the current feet-plane too - one raycast covers both a step up (within
                // stepUpHeight) and a step down (within Height_StepDown) in a single pass. Stepping down
                // always uses the same Height_StepDown regardless of direction - only the step-up side
                // splits by forward vs. non-forward.
                Vector3 stepUpOffset = _smoothedNormal * stepUpHeight;
                float aheadDepth = stepUpHeight + Plugin.ConfigStepDownHeight.Value;

                bool foundSurface;
                RaycastHit aheadHit = default;
                float hitCastDistance = 0f;

                // Always run the full near-to-far ranked candidate search, every tick - the single
                // far-probe fast path (try just the far distance, only widen if it missed or was
                // significant) was cheaper but meant a tick that happened to land on a misleading nearby
                // surface never got the benefit of the full ranked comparison against farther candidates.
                // Paying for the extra raycasts every tick is worth it for the more reliable pick.
                List<StepCandidate> candidates = FindStepCandidates(feetPosition, tangentialMove, stepUpOffset, aheadDepth, maxNormalAngle, maxCastDistance, clampedPitch);
                foundSurface = candidates.Count > 0;

                if (foundSurface)
                {
                    StepCandidate winner = candidates[0];
                    aheadHit = winner.Hit;
                    hitCastDistance = winner.CastDistance;
                }

                if (foundSurface)
                {
                    // Advance the anchor only as far as the successful probe's step distance, not the
                    // full requested stride - so a shortened step (from reeling in near a ledge) actually
                    // moves the player a correspondingly shorter distance this tick, rather than the full
                    // Speed_Walk-paced amount regardless of how close the edge turned out to be. Scaled as
                    // a fraction of the full stride (hitCastDistance / maxCastDistance) so a step reeled
                    // in to e.g. half the max distance also only advances the anchor half as far.
                    float strideFraction = maxCastDistance > 0.0001f ? hitCastDistance / maxCastDistance : 0f;
                    float moveSpeed = IsRunning ? Plugin.ConfigRunSpeed.Value : Plugin.ConfigMoveSpeed.Value;
                    Vector3 fullStride = tangentialMove * moveSpeed * Time.fixedDeltaTime;
                    // Advance from feetPosition (the player's real current position), not _attachPoint -
                    // _attachPoint never leads the player laterally at all now; it's always within one
                    // tick's stride of where the body actually is. This used to cap effective walking speed
                    // well below Speed_Walk/Speed_Run (~13% observed), because the spring/damper could only
                    // sustain nonzero velocity by maintaining a standing lag between desiredPos and the
                    // real position - zero lead meant zero lag, which meant no sustained speed. That's fixed
                    // separately now: the damper's target velocity is fed forward from _attachPoint's own
                    // per-tick motion (see attachVelocity below, at the spring), so it no longer needs a
                    // standing lag to sustain speed - it only corrects the residual error.
                    Vector3 candidatePoint = feetPosition + fullStride * strideFraction;

                    // How far the new hit deviates vertically (along the CURRENT normal) from the plane the
                    // player is already standing on - positive means the hit is further along +normal, i.e.
                    // a step UP (hit.point sits higher, in the outward-normal direction, than feetPosition);
                    // negative means a step DOWN.
                    float signedHeightDeviation = Vector3.Dot(aheadHit.point - feetPosition, _attachNormal);
                    float heightDeviation = Mathf.Abs(signedHeightDeviation);

                    // A significant step DOWN goes through the hold-then-snap pending path - that's what
                    // prevents a full lateral stride and a large vertical drop compounding downward velocity
                    // past BreakawayVelocity on steep descending stairs, and it works well. A significant
                    // step UP is the mirror image: snap height/normal immediately (no downward-velocity risk
                    // to worry about), but HOLD lateral advance until the player's body catches up - without
                    // this, a steep step up combined an instant vertical snap with a full lateral stride in
                    // the same tick, which looked like teleporting forward and up onto the tread. Anything
                    // below Height_StepSignificant in either direction falls through to the ordinary eased
                    // branch, where lateral is immediate and height/plane transition is rate-limited.
                    if (signedHeightDeviation < -Plugin.ConfigStepSignificantHeight.Value)
                    {
                        // Real step: advance the anchor laterally only this tick (project the stride onto
                        // the CURRENT plane - i.e. feetPosition's height, not yet the new step's - so height
                        // doesn't change yet), and queue the vertical/normal correction to apply once the
                        // player's input eases off, instead of in the same tick as the lateral move. This is
                        // what keeps a steep staircase from combining a full lateral stride with a large
                        // vertical drop every single tick, which compounded downward velocity past
                        // BreakawayVelocity - the two now happen on separate ticks.
                        _attachPoint = candidatePoint - Vector3.Dot(candidatePoint - feetPosition, _attachNormal) * _attachNormal;

                        _pendingStepCorrection = true;
                        _pendingStepTimer = 0f;
                        _pendingStepPoint = aheadHit.point;
                        _pendingStepNormal = aheadHit.normal;
                        _pendingStepHitTransform = aheadHit.transform;

                        if (Plugin.ConfigDebugPrint.Value)
                            Plugin.Log.LogInfo($"MagBoots: step detected, deferring vertical correction - signedHeightDeviation={signedHeightDeviation}, moveIntensity={moveIntensity}, hitCastDistance={hitCastDistance}");
                    }
                    else if (signedHeightDeviation > Plugin.ConfigStepSignificantHeight.Value)
                    {
                        // Real step up: snap height/normal onto the new surface's plane immediately (no
                        // easing - there's no runaway-velocity risk going up the way there is going down),
                        // but hold lateral advance at the CURRENT position (not candidatePoint) so the
                        // player doesn't also jump forward the full stride in the same tick. Lateral catch-up
                        // resumes once the player's body has physically risen to meet the new standoff
                        // height, or Timeout_Step elapses - handled in the pending-state block above.
                        Vector3 currentLateral = feetPosition - Vector3.Dot(feetPosition, aheadHit.normal) * aheadHit.normal;
                        float targetHeightUp = Vector3.Dot(aheadHit.point, aheadHit.normal);
                        _attachPoint = currentLateral + targetHeightUp * aheadHit.normal;
                        _attachNormal = aheadHit.normal;
                        _attachHitTransform = aheadHit.transform;

                        _pendingStepUpCorrection = true;
                        _pendingStepUpTimer = 0f;
                        _pendingStepUpPoint = aheadHit.point;
                        _pendingStepUpNormal = aheadHit.normal;
                        _pendingStepUpHitTransform = aheadHit.transform;

                        if (Plugin.ConfigDebugPrint.Value)
                            Plugin.Log.LogInfo($"MagBoots: step up detected, snapping height and deferring lateral - signedHeightDeviation={signedHeightDeviation}, moveIntensity={moveIntensity}, hitCastDistance={hitCastDistance}");
                    }
                    else
                    {
                        // Re-snap the anchor onto the new surface's plane (height/orientation) rather than
                        // forward along it - standard point-onto-plane projection: subtract out the
                        // component of (candidatePoint - aheadHit.point) along the new normal, which keeps
                        // the anchor's incrementally-advanced position but adopts the new surface's height.
                        Vector3 offset = candidatePoint - aheadHit.point;
                        Vector3 targetAttachPoint = candidatePoint - Vector3.Dot(offset, aheadHit.normal) * aheadHit.normal;

                        // Ease the along-normal (height) component toward the new plane at a bounded rate,
                        // rather than snapping to it in the same tick - this is what actually moves gradually
                        // now, not a separate desiredPos layer. _attachPoint is still what the spring's
                        // desiredPos reads from every tick, so a large one-tick jump here (e.g. a floor
                        // seam/small ledge just under Height_StepSignificant, or transitioning onto a
                        // differently-angled surface) still yanked the spring hard even though it wasn't
                        // "significant" enough to defer through the step-down path. Lateral position (already
                        // eased via the bounded fullStride advance above) is applied immediately, same as
                        // always - only the height/plane transition is rate-limited here.
                        Vector3 targetLateral = targetAttachPoint - Vector3.Dot(targetAttachPoint, aheadHit.normal) * aheadHit.normal;
                        float currentHeight = Vector3.Dot(_attachPoint, aheadHit.normal);
                        float targetHeight = Vector3.Dot(targetAttachPoint, aheadHit.normal);
                        float easedHeight = Mathf.MoveTowards(currentHeight, targetHeight,
                            Plugin.ConfigAttachHeightFollowSpeed.Value * Time.fixedDeltaTime);
                        _attachPoint = targetLateral + easedHeight * aheadHit.normal;
                        _attachNormal = aheadHit.normal;
                        _attachHitTransform = aheadHit.transform;

                        if (Plugin.ConfigDebugPrint.Value)
                        {
                            Plugin.Log.LogInfo($"MagBoots: ahead-cast hit - moveIntensity={moveIntensity}, hitCastDistance={hitCastDistance}, normalAngle={Vector3.Angle(aheadHit.normal, _smoothedNormal)}, collider={(aheadHit.collider != null ? aheadHit.collider.name : "null")}, hitPoint={aheadHit.point}");
                            Plugin.Log.LogInfo($"MagBoots: attach height eased - currentHeight={currentHeight}, targetHeight={targetHeight}, easedHeight={easedHeight}");
                        }
                    }
                }
                else
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: ahead-cast MISSED at all step distances - moveIntensity={moveIntensity}, maxCastDistance={maxCastDistance}, position={rb.position}, dir={-_smoothedNormal}, maxDepth={aheadDepth}, moveDir={tangentialMove.normalized}, rawForward={rawForward}, feetPosition={feetPosition}, attachPoint={_attachPoint}, attachNormal={_attachNormal}, velocity={rb.velocity}, speed={rb.velocity.magnitude}");

                    // Recovery fallback: a total miss otherwise leaves _attachPoint (and therefore the
                    // spring's desiredPos) frozen forever, with no way for it to ever update again - the
                    // ordinary search above only updates _attachPoint on its own success, so once it starts
                    // missing, the spring keeps holding rb.position near the OLD surface's height/position
                    // indefinitely. Since feetPosition is built fresh from rb.position every tick (by
                    // design, see feetPosition's own comment), a body pinned at the old height means next
                    // tick's search is cast from that same wrong height too - a closed loop with no exit,
                    // confirmed from real stuck sessions (see project_magboots memory). This cast is
                    // deliberately anchored to rb.position directly (not feetPosition/stepUpOffset) and
                    // uses a generous depth, independent of the normal search's StepUp/StepDown window, so
                    // it can find literally whatever the player is currently resting on or near - it isn't
                    // trying to find the NEXT step, only to re-anchor to something real so the ordinary
                    // search gets a fresh, correct height to search from next tick.
                    float fallbackDepth = Plugin.ConfigPlayerHeight.Value + Plugin.ConfigStepDownHeight.Value;
                    bool fallbackHit = TryFindValidSurface(rb.position, -_smoothedNormal, fallbackDepth, maxNormalAngle, out RaycastHit recoveryHit, out string fallbackMissReason);

                    if (Plugin.ConfigDebugPrint.Value)
                    {
                        if (fallbackHit)
                            Plugin.Log.LogInfo($"MagBoots: stuck recovery - re-anchored to collider={(recoveryHit.collider != null ? recoveryHit.collider.name : "null")}, hitPoint={recoveryHit.point}, normal={recoveryHit.normal}, oldAttachPoint={_attachPoint}, oldAttachNormal={_attachNormal}");
                        else
                            Plugin.Log.LogInfo($"MagBoots: stuck recovery FAILED - no surface within {fallbackDepth}m straight below rb.position={rb.position}, reason={fallbackMissReason}");
                    }

                    if (fallbackHit)
                    {
                        _attachPoint = recoveryHit.point;
                        _attachNormal = recoveryHit.normal;
                        _attachHitTransform = recoveryHit.transform;
                    }
                    // else: truly nothing below the player at all even at this generous depth - hold last
                    // valid attach point/normal, same as before (a genuine full-width gap/edge).
                }
            }

            if (Plugin.ConfigDebugPrint.Value)
            {
                float attachHeight = Vector3.Dot(_attachPoint, _attachNormal);
                Plugin.Log.LogInfo($"MagBoots: chart - feetPosition={feetPosition}, rbPosition={rb.position}, attachHeight={attachHeight}, tangentialMove={tangentialMove}, rawForward={rawForward}, attachPoint={_attachPoint}, attachNormal={_attachNormal}, velocity={rb.velocity}, speed={rb.velocity.magnitude}");
            }

            // Crouch: while thrust-down is held, ease the standoff distance toward half its configured
            // value instead of snapping instantly, so ducking under a low obstacle feels deliberate
            // rather than a jarring pop. Eases back to full the same way on release.
            float verticalAxis = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveUpDownComposite);
            bool crouchHeld = verticalAxis < -0.0001f;
            float targetStandoffFraction = crouchHeld ? 0.5f : 1f;
            _standoffFraction = Mathf.MoveTowards(_standoffFraction, targetStandoffFraction, Time.fixedDeltaTime / CrouchTransitionDuration);

            Vector3 desiredPos = _attachPoint + _smoothedNormal * (Plugin.ConfigPlayerHeight.Value * _standoffFraction);

            // Feed-forward: target the damper at the INTENDED walking velocity (tangentialMove * moveSpeed)
            // rather than always at a standstill, so sustaining speed doesn't require a permanent lag
            // between desiredPos and the real position just to out-fight the damper's own braking.
            // Deliberately NOT derived from _attachPoint's own tick-to-tick delta - since _attachPoint now
            // rebuilds from feetPosition (= rb.position) every tick with zero lead, differencing it would
            // bake in any overshoot in the player's ALREADY-ACTUAL velocity, feeding an inflated target
            // back into the damper and pushing velocity even higher next tick - an unbounded runaway with
            // no ceiling (confirmed: this is exactly what let the player accelerate straight past
            // BreakawayVelocity). tangentialMove*moveSpeed is a fixed target independent of the player's
            // current motion, so it can't bootstrap a runaway - the damper is always correcting toward the
            // same bounded value, never toward "whatever the anchor happened to do because the player was
            // already moving."
            float desiredSpeed = IsRunning ? Plugin.ConfigRunSpeed.Value : Plugin.ConfigMoveSpeed.Value;
            Vector3 desiredVelocity = tangentialMove * desiredSpeed;

            Vector3 springForce = Plugin.ConfigSpring.Value * (desiredPos - rb.position)
                                   + Plugin.ConfigDamper.Value * (desiredVelocity - rb.velocity);
            rb.AddForce(springForce, ForceMode.Acceleration);

            if (Plugin.ConfigDebugPrint.Value)
            {
                Plugin.Log.LogInfo($"MagBoots: spring - desiredPos={desiredPos}, desiredVelocity={desiredVelocity}, springForce={springForce}, springForceMag={springForce.magnitude}, positionError={(desiredPos - rb.position).magnitude}");
            }
        }

        private static Vector3 ReadTangentialMoveInput(Transform playerTransform, Vector3 normal)
        {
            float rightAxis = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveLeftRightComposite);
            float forwardAxis = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveBackForwardComposite);

            Vector3 right = Vector3.ProjectOnPlane(playerTransform.right, normal).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(playerTransform.forward, normal).normalized;

            return right * rightAxis + forward * forwardAxis;
        }

        private readonly struct StepCandidate
        {
            public readonly RaycastHit Hit;
            public readonly float CastDistance;
            public readonly float RoundedHeight;

            public StepCandidate(RaycastHit hit, float castDistance, float roundedHeight)
            {
                Hit = hit;
                CastDistance = castDistance;
                RoundedHeight = roundedHeight;
            }
        }

        // Casts at every stride increment and builds the full list of every surface that actually
        // registered as valid (not just a running best), then sorts it - rather than whichever hit a
        // single far-probe/reel-in search happened to find first. On steep stairs, "first hit searching
        // from the far end" could land on a tread two or three steps down instead of the very next one, if
        // the nearer treads' horizontal extent was shorter than the probe's forward offset - silently
        // skipping steps the player never actually walked onto. Each candidate's height deviation is
        // rounded to the nearest 0.1m so near-equal heights (ordinary floor noise, a small threshold/bump)
        // sort as "the same tread" rather than distinct candidates. Sorted HIGHEST (physically:
        // smallest/most-negative rounded deviation, since positive means up) first, farthest-distance
        // second: stepping up, the highest reachable tread is also the nearest one, so it naturally sorts
        // first; stepping down, the highest candidate among the down-hits is the near edge of the current
        // landing - the very next step down, never a lower one skipped ahead of it. Only called when the
        // cheap single far-probe already found (or missed looking for) a significant height change, so
        // this cost is paid only on actual steps, not every tick of ordinary flat-ground walking.
        private List<StepCandidate> FindStepCandidates(Vector3 feetPosition, Vector3 tangentialMove, Vector3 stepUpOffset,
            float aheadDepth, float maxNormalAngle, float maxCastDistance, float clampedPitch)
        {
            var candidates = new List<StepCandidate>();
            var missLog = Plugin.ConfigDebugPrint.Value ? new List<string>() : null;
            Vector3 moveDir = tangentialMove.normalized;

            const int castSteps = 10;
            for (int i = 1; i <= castSteps; i++)
            {
                float castDistance = maxCastDistance * i / castSteps;
                Vector3 aheadOrigin = feetPosition + moveDir * castDistance + stepUpOffset;
                if (!TryFindValidSurface(aheadOrigin, -_smoothedNormal, aheadDepth, maxNormalAngle, out RaycastHit candidateHit, out string missReason))
                {
                    missLog?.Add($"({castDistance:F3}, {missReason}, origin={aheadOrigin})");
                    continue;
                }

                float candidateHeight = Vector3.Dot(candidateHit.point - feetPosition, _attachNormal);
                float roundedHeight = Mathf.Round(candidateHeight * 10f) / 10f;
                candidates.Add(new StepCandidate(candidateHit, castDistance, roundedHeight));
            }

            if (missLog != null && missLog.Count > 0)
                Plugin.Log.LogInfo("MagBoots: candidate misses (aheadDist, reason): " + string.Join(", ", missLog));

            // Direction is decided by where the player is looking, not by the candidates' own heights -
            // every height-based rule tried before this (majority-count, nearest-significant,
            // most-significant, angle, height+distance-weight) eventually got fooled by some nearby
            // artifact (typically a carpet/floor mesh under or behind the stairs) outscoring the real
            // tread, because the ranking had to infer intent purely from ambiguous geometry (see
            // project_magboots_direction_formula_investigation memory for the full history). The player's
            // own camera pitch is a direct, unambiguous signal of intent instead: looking up or level means
            // they're heading up a step, looking down means they're heading down one - exactly how a person
            // naturally tilts their head while climbing real stairs. clampedPitch is positive when looking
            // DOWN (see its own comment above), so it's negated here to match "up-or-level (>=0) -> step
            // up" in the more intuitive up-is-positive sense.
            bool isStepUpGroup = -clampedPitch >= 0f;

            if (isStepUpGroup)
            {
                // Highest (most-up) first; farthest distance breaks ties.
                candidates.Sort((a, b) =>
                {
                    int heightCompare = b.RoundedHeight.CompareTo(a.RoundedHeight);
                    return heightCompare != 0 ? heightCompare : b.CastDistance.CompareTo(a.CastDistance);
                });
            }
            else
            {
                // Highest (least-down/most-up) first; farthest distance breaks ties.
                candidates.Sort((a, b) =>
                {
                    int heightCompare = a.RoundedHeight.CompareTo(b.RoundedHeight);
                    return heightCompare != 0 ? heightCompare : b.CastDistance.CompareTo(a.CastDistance);
                });
            }

            if (Plugin.ConfigDebugPrint.Value && candidates.Count > 0 && candidates[0].RoundedHeight != 0f)
            {
                var rows = new List<StepCandidate>(candidates);
                rows.Sort((a, b) => a.CastDistance.CompareTo(b.CastDistance));

                var sb = new System.Text.StringBuilder();
                sb.Append("MagBoots: candidate table (aheadDist, heightAboveFeet, collider): ");
                for (int i = 0; i < rows.Count; i++)
                {
                    bool isWinner = rows[i].CastDistance == candidates[0].CastDistance && rows[i].RoundedHeight == candidates[0].RoundedHeight;
                    string colliderName = rows[i].Hit.collider != null ? rows[i].Hit.collider.name : "null";
                    string entry = $"({rows[i].CastDistance:F3}, {rows[i].RoundedHeight:F1}, {colliderName})";
                    if (isWinner)
                        entry = $"**{entry}**";
                    sb.Append(entry);
                    if (i < rows.Count - 1)
                        sb.Append(", ");
                }
                Plugin.Log.LogInfo(sb.ToString());
                Plugin.Log.LogInfo($"MagBoots: winner world position - hitPoint={candidates[0].Hit.point}, collider={(candidates[0].Hit.collider != null ? candidates[0].Hit.collider.name : "null")}");
            }

            return candidates;
        }

        private bool TryFindValidSurface(Vector3 origin, Vector3 direction, float maxDistance, float maxNormalAngle, out RaycastHit result)
        {
            return TryFindValidSurface(origin, direction, maxDistance, maxNormalAngle, out result, out _);
        }

        private bool TryFindValidSurface(Vector3 origin, Vector3 direction, float maxDistance, float maxNormalAngle, out RaycastHit result, out string missReason)
        {
            LayerMask mask = Main.Instance.MainSettings.RaycastSettings.GrabValidLayerMask;

            if (!Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, mask))
            {
                result = default;
                missReason = "no hit";
                return false;
            }

            float angle = Vector3.Angle(hit.normal, -direction);
            if (angle > maxNormalAngle)
            {
                result = default;
                missReason = $"angle {angle:F1} > {maxNormalAngle:F1}";
                return false;
            }

            if (!SurfaceGeometry.HasMinimumFaceArea(hit, Plugin.ConfigMinFaceArea.Value))
            {
                result = default;
                missReason = "face area too small";
                return false;
            }

            result = hit;
            missReason = null;
            return true;
        }
    }
}
