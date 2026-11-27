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
    // L-Ctrl (or Q + E, or L3 + R3); throttle on the wheel / triggers, Z full, X / D-pad down cut; G / D-pad left flip
    // and burn (R / Y is the weapon wheel); T / D-pad up hold attitude.
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
        struct Carried
        {
            internal Rigidbody Body; internal Transform Transform; internal Vector3 Position; internal Quaternion Rotation; internal bool WasFree;
            // a loose thing lying aboard (not part of the ship): it falls under the drive's pull and is pinned again once at rest
            internal bool Loose, Falling;
            internal float Still;                   // s at rest against the ship while falling
            internal Vector3 SettledPull;           // the pull's direction (ship frame) it came to rest under; zero = none
            internal bool UntilFreed;               // a piece taken on while fixed: let go only when the game frees it
        }
        static readonly List<Carried> sBodies = new List<Carried>();
        static readonly List<Rigidbody> sInterpolated = new List<Rigidbody>();
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

        // ---- parked while the pilot is out of the seat ----------------------------------------------------------------
        // Out of the seat, the ship stops for real where it is: the game's physics then has a still ship, so decks,
        // grabbing, the grapple, the aim and everything aboard behave as in the bay. The flight model carries on (the
        // speed, a burn, a flip and burn): sPivot / sAttitude go on moving while the ship's bodies stay at the parked pose.
        // Back in the seat, the ship and everything aboard jump to where the flight model has the ship and take up its
        // motion again. While parked the drive's push is felt aboard as a plain pull toward the stern.
        static bool sParked;
        static Vector3 sParkPivot;
        static Quaternion sParkRotation = Quaternion.identity;
        internal static bool Parked => sParked;
        // where the flight has the ship (not where it is parked)
        internal static Vector3 ShipPosition => sPivot;
        // For the RCS jets: the side thrusters' push this step (world, 1 = full), the change of spin asked of them (world,
        // 1 = full turning acceleration), and half the ship's length.
        internal static Vector3 RcsCommand { get; private set; }
        internal static Vector3 SpinCommand { get; private set; }
        internal static float HalfLength => Mathf.Max(sLocalBox.extents.x, Mathf.Max(sLocalBox.extents.y, sLocalBox.extents.z));

        // where the ship's bodies are: the parked pose while parked, else the flight model's
        static Vector3 BodyPivot => sParked ? sParkPivot : sPivot;
        static Quaternion BodyRotation => sParked ? sParkRotation : ShipRotation;

        // Inside the ship's extent (plus a few meters), while it is under way.
        internal static bool Aboard(Vector3 point) => sMoving && sLocalBox.Contains(Quaternion.Inverse(BodyRotation) * (point - BodyPivot));

        // The drive's push felt aboard, as a pull in the world where the ship's bodies are (turned onto the parked ship).
        static Vector3 AboardPullFrom(Vector3 accel)
        {
            var pull = -accel * Mathf.Clamp01(Plugin.FlightAboardPull.Value);
            return sParked ? sParkRotation * (Quaternion.Inverse(ShipRotation) * pull) : pull;
        }

        const float StandardGravity = 9.80665f;
        static Vector3 sFelt;                               // m/s^2

        internal static bool Flying => sFlying;

        // For the flight readout.
        internal static float RcsLevel { get; private set; }
        internal static float ForwardSpeed => Vector3.Dot(sVelocity, Forward);
        // The ship's velocity in its own axes: x to starboard, y up, z ahead.
        internal static Vector3 LocalVelocity => Quaternion.Inverse(ShipRotation) * sVelocity;
        internal static float FeltAcceleration => sFelt.magnitude;
        internal static float FeltG => sFelt.magnitude / StandardGravity;
        internal static float TurnRate => sSpin.magnitude * Mathf.Rad2Deg;
        internal static bool Holding => sHold;
        internal static string AutoStatus => AutoLine().Trim();
        internal static bool AutoActive => sAuto != Auto.None;
        internal static bool UnderWay => sMoving;

        // The ship's long axis: a point on it (its middle) and its forward direction, while it is carried.
        internal static bool TryAxis(out Vector3 point, out Vector3 direction)
        {
            point = sPivot; direction = Forward;
            return sMoving;
        }
        internal static float Speed => sVelocity.magnitude;

        // How fast a point riding on the ship moves (for the pilot in the chair).
        // Parked, the ship's bodies are still: zero.
        internal static Vector3 PointVelocity(Vector3 point) => sParked ? Vector3.zero : MovingPointVelocity(point);
        static Vector3 MovingPointVelocity(Vector3 point) => sVelocity + Vector3.Cross(sSpin, point - sPivot);

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
                sStartPivot = sPivot;
                sStartRotation = ShipRotation;
                HookSplits();
                int held = Collect();
                CollectRiders(chair);
                sMoving = true;
                FlightJacks.Release();
                ShipWalls.Begin();
                Throttle = 0f;
                sHold = true;
                sAuto = Auto.None;
                Plugin.Log.LogInfo($"[Flight] at the controls: {sBodies.Count} body(ies) carried as the ship ({held} held fixed by the game); " +
                                   $"middle {center}, forward {forward}.");
            }
            else
            {
                Unpark();
                Plugin.Log.LogInfo($"[Flight] back at the controls at {Speed:0.0} m/s, throttle {Throttle * 100f:0}%.");
            }
            // a ship still under way keeps its throttle, attitude hold and any flip and burn: the pilot takes over as set
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
            Park();
        }

        // The game's physics only collides things inside a box around the bay (its broadphase world bounds, from the
        // game's physics settings: center (0, 0, 100), half-size (250, 125, 400)). Outside it rays still hit but nothing
        // collides: the suit and loose things went through every deck and wall of a ship parked 24 km out (and, before
        // parking, of the ship flying away from the bay). So a ship parked outside the box gets a box of its own: the
        // physics' collision regions are rebuilt around it (Physics.RebuildBroadphaseRegions), and put back around the
        // bay when it is under way again. Should that fail, it parks where it started its flight, by the bay.
        static readonly Bounds PhysicsBounds = new Bounds(new Vector3(0f, 0f, 100f), new Vector3(500f, 250f, 800f));
        const int PhysicsSubdivisions = 8;
        const float ParkedBoundsMargin = 250f;      // m around the parked ship (room for a walk outside)
        static bool sBoundsMoved;

        static bool MoveBoundsTo(Vector3 pivot, Quaternion rotation)
        {
            var box = new Bounds(pivot, Vector3.zero);
            var c = sLocalBox.center; var e = sLocalBox.extents;
            for (int i = 0; i < 8; i++)
                box.Encapsulate(pivot + rotation * (c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
            box.Expand(2f * ParkedBoundsMargin);
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Physics.RebuildBroadphaseRegions(box, PhysicsSubdivisions);
                sBoundsMoved = true;
                Plugin.Log.LogInfo($"[Flight] collisions now work around the parked ship: physics regions rebuilt around {box.center} " +
                                   $"({box.size.x:0} x {box.size.y:0} x {box.size.z:0} m) in {watch.Elapsed.TotalMilliseconds:0.0} ms.");
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Flight] could not move the physics regions: {ex.GetBaseException().Message}");
                return false;
            }
        }

        static void BoundsBack()
        {
            if (!sBoundsMoved) return;
            sBoundsMoved = false;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Physics.RebuildBroadphaseRegions(PhysicsBounds, PhysicsSubdivisions);
                Plugin.Log.LogInfo($"[Flight] physics regions back around the bay ({watch.Elapsed.TotalMilliseconds:0.0} ms).");
            }
            catch (System.Exception ex) { Plugin.Log.LogError($"[Flight] could not put the physics regions back: {ex.GetBaseException().Message}"); }
        }
        static Vector3 sStartPivot;
        static Quaternion sStartRotation = Quaternion.identity;

        static bool InsidePhysics(Vector3 pivot, Quaternion rotation)
        {
            var c = sLocalBox.center; var e = sLocalBox.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                if (!PhysicsBounds.Contains(pivot + rotation * corner)) return false;
            }
            return true;
        }

        // The view jumped (parking on the sphere, catching up): not a move toward or away from anything, so the cards that
        // follow the view jump with it.
        static void BackgroundJumped(Vector3 delta)
        {
            if (!SpaceLimits.PinningOn) return;
            BackgroundParallaxTuning.Teleported(delta);
            FlatCardBillboarding.Teleported(delta);
        }

        static void Park()
        {
            if (!sMoving || sParked) return;
            // The parking sphere: parked where it is, but no farther than [Flight] ParkingSphere km from the world's origin
            // (positions are kept to 2 mm inside 32.8 km, 4 mm beyond, and the gun near the eye shook from about 32 km).
            // Beyond it, on the sphere in the direction the ship has gone: the bay is too small to see from there either
            // way, and the background moves with the jump. The true position stays with the flight model.
            var target = sPivot;
            float radius = Plugin.FlightParkingSphere.Value * 1000f;
            if (radius > 0f && sPivot.magnitude > radius) target = sPivot.normalized * radius;
            bool inside = InsidePhysics(target, ShipRotation) || MoveBoundsTo(target, ShipRotation);
            sParkPivot = inside ? target : sStartPivot;
            sParkRotation = inside ? ShipRotation : sStartRotation;
            bool here = sParkPivot == sPivot && sParkRotation == ShipRotation;
            if (!here) BackgroundJumped(sParkPivot - sPivot);
            var turn = sParkRotation * Quaternion.Inverse(ShipRotation);
            int freed = 0;
            // free things aboard lose the ship's motion; the loose things are let go to the game's own physics; all of it
            // moved to the parking place
            for (int i = 0; i < sBodies.Count; i++)
            {
                var c = sBodies[i];
                var body = c.Body;
                if (body == null) continue;
                if (!body.isKinematic)
                {
                    body.velocity = turn * (body.velocity - MovingPointVelocity(body.position));
                    body.angularVelocity = turn * (body.angularVelocity - sSpin);
                }
                if (!here)
                {
                    var position = sParkPivot + turn * (body.position - sPivot);
                    var rotation = turn * body.rotation;
                    body.transform.SetPositionAndRotation(position, rotation);
                    body.position = position;
                    body.rotation = rotation;
                }
                if (c.Loose && body.isKinematic)
                {
                    // let go gently: pinned, some were touching or a little into the ship (nothing collided out there),
                    // and the physics pushed them apart at up to 10 m/s: hard enough for the game to count damage
                    // (sparks and fires all over the ship). For a few seconds they may only ease apart, and they are
                    // left asleep until something touches them or the drive pulls them.
                    body.isKinematic = false;
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    if (!sGentle.ContainsKey(body)) sGentle[body] = body.maxDepenetrationVelocity;
                    body.maxDepenetrationVelocity = GentleDepenetration;
                    body.Sleep();
                    freed++;
                }
                if (c.Loose) { c.Falling = true; sBodies[i] = c; }
            }
            var player = FlightSeat.PlayerBody;
            if (player != null && Aboard(player.position)) player.velocity -= MovingPointVelocity(player.position);
            sParked = true;
            sGentleUntil = Time.time + GentleSeconds;
            GuardDamage();
            Solid(null, false);
            NoDragAboard.RestoreAll();
            foreach (var body in sInterpolated) if (body != null) body.interpolation = RigidbodyInterpolation.Interpolate;
            if (sFrame != null) sFrame.SetPositionAndRotation(sParkPivot, sParkRotation);
            foreach (var rider in sRiders)
                if (rider.Body == null && rider.Transform != null)
                    rider.Transform.SetPositionAndRotation(sParkPivot + sParkRotation * rider.Position, sParkRotation * rider.Rotation);
            Plugin.Log.LogInfo($"[Flight] parked {(here ? "where it is" : inside ? $"on the parking sphere, {sParkPivot.magnitude / 1000f:0.0} km out ({(sPivot - sParkPivot).magnitude / 1000f:0.0} km in from where the flight has it)" : $"back where the flight started ({(sPivot - sParkPivot).magnitude:0} m away)")} " +
                               $"while you are out of the seat ({freed} loose thing(s) let go); the flight goes on at {Speed:0.0} m/s and it catches up when you sit down.");
        }

        static void Unpark()
        {
            if (!sParked) return;
            sParked = false;
            if (sParkPivot != sPivot) BackgroundJumped(sPivot - sParkPivot);
            GuardDamage();
            EndGentle();
            BoundsBack();
            // the physics' smoothing would draw each body sliding from where it was parked for a step: off for this step
            foreach (var body in sInterpolated) if (body != null) body.interpolation = RigidbodyInterpolation.None;
            sSmoothAgain = true;
            var turn = ShipRotation * Quaternion.Inverse(sParkRotation);
            var inverse = Quaternion.Inverse(ShipRotation);
            float distance = (sPivot - sParkPivot).magnitude;
            int moved = 0;
            for (int i = 0; i < sBodies.Count; i++)
            {
                var c = sBodies[i];
                var body = c.Body;
                if (body == null) continue;
                var position = sPivot + turn * (body.position - sParkPivot);
                var rotation = turn * body.rotation;
                if (c.Loose)
                {
                    // pinned again where it lies, until the pull swings away from the stern
                    body.isKinematic = true;
                    c.Position = inverse * (position - sPivot);
                    c.Rotation = inverse * rotation;
                    c.Falling = false;
                    c.SettledPull = Vector3.back;
                    c.Still = 0f;
                    sBodies[i] = c;
                }
                body.transform.SetPositionAndRotation(position, rotation);
                body.position = position;
                body.rotation = rotation;
                if (!body.isKinematic)
                {
                    body.velocity = turn * body.velocity + MovingPointVelocity(position);
                    body.angularVelocity = turn * body.angularVelocity + sSpin;
                }
                moved++;
            }
            if (sFrame != null) sFrame.SetPositionAndRotation(sPivot, ShipRotation);
            foreach (var rider in sRiders)
                if (rider.Body == null && rider.Transform != null)
                    rider.Transform.SetPositionAndRotation(sPivot + ShipRotation * rider.Position, ShipRotation * rider.Rotation);
            sPrevPivot = sPivot;
            sPrevRotation = ShipRotation;
            Plugin.Log.LogInfo($"[Flight] unparked: the ship caught up {distance:0} m to where the flight has it, {moved} body(ies) moved; " +
                               $"under way again at {Speed:0.0} m/s.");
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
            Solid(null, false);
            NoDragAboard.RestoreAll();
            sMoving = false;
            sParked = false;
            sLooseColliders.Clear();
            EndGentle();
            BoundsBack();
            sCarriedSet = null;
            DeckBoots.Reset("the ship stopped");
            foreach (var body in sInterpolated) if (body != null) body.interpolation = RigidbodyInterpolation.None;
            sInterpolated.Clear();
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
            bool moving = !sParked && (Speed > (sHiddenGraphs.Count > 0 ? GraphsShowSpeed : GraphsHideSpeed) || sSpin.magnitude > 0.02f);
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

        // ---- loose things falling aboard -----------------------------------------------------------------------------
        // Loose things lying aboard (crates, cut pieces, anything not part of the ship) are pinned in the ship's frame. With
        // the drive's pull on they are let go and fall toward the stern the same way the suit does: each step they gain
        // what the ship's motion gained where they are, plus the pull, with the fall capped (soft landings) and collisions
        // checked continuously; the hull never rams them. Once one has rested against the ship for half a second it is
        // pinned again (no cost, no jitter, no slow sinking through a thin plate) until the pull swings more than 20
        // degrees from the one it settled under, the suit comes within reach of it, or the grapple, push or pull is aimed
        // at it (a pinned thing would not budge); woken that way it is left free a second before it may settle again.
        const float LooseSettleSeconds = 0.5f, LooseStillSpeed = 0.05f, LooseWakeAngle = 20f;
        const float LooseWakeReach = 2f, LooseAimRange = 40f, LooseWokenGrace = 1f;

        // Diagnostic: what happens to a grappled thing aboard a ship under way, four times a second while grappled and for
        // four seconds after (grappled things were falling out of the ship).

        // ---- pieces coming loose during the flight --------------------------------------------------------------------
        // Pulling or cutting a piece off the ship makes the game give it a body of its own. Split from a carried (fixed)
        // body, whose own speed reads zero, the new piece started still in the bay and the ship left it behind (the
        // grappled things falling out). Each new body aboard is taken on at once: a fixed one is carried with the ship,
        // a free one becomes a loose thing with the ship's motion where it is (or the free body's it came from).
        static bool sSplitHooked;

        static void HookSplits()
        {
            if (sSplitHooked) return;
            sSplitHooked = true;
            Main.EventSystem.AddHandler<HierarchyJointReparentedEvent>(ev =>
            {
                if (!sMoving || ev.ReparentedInfo == null) return;
                foreach (var pair in ev.ReparentedInfo)
                    if (pair.Value != null) TakeOn(pair.Value.NewHierarchyParentRigidbody, pair.Value.OldHierarchyParentRigidbody, "reparented");
            });
            Main.EventSystem.AddHandler<HierarchySplitCompleteEvent>(ev =>
            {
                if (!sMoving || ev.NewParts == null) return;
                var old = ev.OldPart != null ? ev.OldPart.GetComponentInParent<Rigidbody>() : null;
                foreach (var part in ev.NewParts)
                    if (part != null) TakeOn(part.GetComponentInParent<Rigidbody>(), old, "split off");
            });
        }

        static void TakeOn(Rigidbody? body, Rigidbody? from, string how)
        {
            if (body == null || IsCarried(body) || !Aboard(body.worldCenterOfMass)) return;
            if (body.GetComponent<BBI.Unity.Game.Player>() != null) return;
            var inverse = Quaternion.Inverse(BodyRotation);
            bool fromFree = from != null && !from.isKinematic;
            // always a loose thing: a fixed one is pinned for now and let go when the game frees it (as it does when the
            // grapple pulls a piece loose)
            var c = new Carried { Body = body, Position = inverse * (body.position - BodyPivot), Rotation = inverse * body.rotation, WasFree = !body.isKinematic, Loose = true, UntilFreed = body.isKinematic };
            if (!body.isKinematic)
            {
                c.Loose = true;
                c.Falling = true;
                c.Still = -LooseWokenGrace;
                if (!sParked) NoDragAboard.Zero(body);
                if (!fromFree)
                {
                    body.velocity = PointVelocity(body.worldCenterOfMass);
                    body.angularVelocity = sSpin;
                }
                if (!sModes.ContainsKey(body)) sModes[body] = body.collisionDetectionMode;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
            if (sSolid) { sSolidSaved[body] = body.interpolation; body.interpolation = RigidbodyInterpolation.None; }
            sBodies.Add(c);
            sCarriedSet!.Add(body);
            Plugin.Log.LogInfo($"[Flight] piece {how} aboard: '{body.name}' ({body.mass:0} kg) taken on {(body.isKinematic ? "pinned, let go when the game frees it" : "as a loose thing")}" +
                               $"{(body.isKinematic || fromFree ? "" : ", given the ship's motion")}.");
        }

        static readonly RaycastHit[] sLandingHits = new RaycastHit[16];
        static readonly Dictionary<Rigidbody, Collider[]> sLooseColliders = new Dictionary<Rigidbody, Collider[]>();

        // The game caps the speed of some things (a RigidbodyController: 20 m/s) against the bay: aboard a ship under way
        // that held them back and the ship left them behind, falling through the decks. The cap is applied against the
        // ship's motion there instead.
        [HarmonyLib.HarmonyPatch(typeof(RigidbodyController), "FixedUpdate")]
        static class ShipFrameSpeedCap
        {
            static readonly System.Reflection.FieldInfo? sBody = HarmonyLib.AccessTools.Field(typeof(RigidbodyController), "mRigidbody");

            static void Prefix(RigidbodyController __instance, out Vector3 __state)
            {
                __state = Vector3.zero;
                if (!sMoving || !(sBody?.GetValue(__instance) is Rigidbody body) || body.isKinematic || !Aboard(body.position)) return;
                __state = PointVelocity(body.position);
                body.velocity -= __state;
            }

            static void Postfix(RigidbodyController __instance, Vector3 __state)
            {
                if (__state == Vector3.zero || !(sBody?.GetValue(__instance) is Rigidbody body)) return;
                body.velocity += __state;
            }
        }

        // The nearest of the ship's fixed surfaces the body's box would meet moving this far along this way.
        static bool LooseLanding(Rigidbody body, Vector3 direction, float distance, Vector3 down, out RaycastHit result)
        {
            result = default;
            var bounds = new Bounds(body.worldCenterOfMass, Vector3.zero);
            bool any = false;
            if (!sLooseColliders.TryGetValue(body, out var colliders))
                sLooseColliders[body] = colliders = body.GetComponentsInChildren<Collider>();
            foreach (var collider in colliders)
            {
                if (collider == null || !collider.enabled || collider.isTrigger || collider.attachedRigidbody != body) continue;
                if (!any) { bounds = collider.bounds; any = true; } else bounds.Encapsulate(collider.bounds);
            }
            if (!any) return false;
            // started a little back and a little smaller, so a deck it is already resting on (touching at the start,
            // which a sweep does not report) is still found
            const float Back = 0.1f;
            var half = bounds.extents * 0.8f;
            int count = Physics.BoxCastNonAlloc(bounds.center - direction * Back, half, direction, sLandingHits, Quaternion.identity, distance + Back + 0.02f, ~0, QueryTriggerInteraction.Ignore);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                var hit = sLandingHits[i];
                if (hit.collider == null || hit.distance <= 0f) continue;
                var other = hit.collider.attachedRigidbody;
                if (other == null || other == body || !other.isKinematic || !IsCarried(other)) continue;
                if (!found || hit.distance < result.distance) { result = hit; found = true; }
            }
            if (found) result.distance = Mathf.Max(0f, result.distance - Back);
            return found;
        }

        // The pinned loose thing the suit is aiming the grapple, push or pull at, if any.
        static Rigidbody? AimedAt(Rigidbody? player)
        {
            var controls = LynxControls.Instance;
            if (controls == null) return null;
            if (!controls.GetInputIsPressed(GameplayActions.GameplayActionSet.GrappleFire) &&
                !controls.GetInputIsPressed(GameplayActions.GameplayActionSet.Push) &&
                !controls.GetInputIsPressed(GameplayActions.GameplayActionSet.Pull)) return null;
            var camera = Camera.main;
            if (camera == null) return null;
            int count = Physics.RaycastNonAlloc(camera.transform.position, camera.transform.forward, sFloorHits, LooseAimRange, ~0, QueryTriggerInteraction.Ignore);
            Rigidbody? nearest = null;
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var body = sFloorHits[i].collider != null ? sFloorHits[i].collider.attachedRigidbody : null;
                if (body == null || body == player || sFloorHits[i].distance >= best) continue;
                best = sFloorHits[i].distance;
                nearest = body;
            }
            return nearest;
        }

        static void LooseFall(Vector3 velocityBefore, Vector3 spinBefore, Vector3 pivotBefore, Vector3 accel, float dt)
        {
            var pull = -accel * Mathf.Clamp01(Plugin.FlightAboardPull.Value);
            bool pulled = pull.sqrMagnitude > 0.01f;
            var pullLocal = pulled ? Quaternion.Inverse(ShipRotation) * pull.normalized : Vector3.zero;
            int falling = 0, woke = 0, settled = 0, touched = 0;
            var suit = FlightSeat.Seated ? null : FlightSeat.PlayerBody;
            var aimed = suit != null ? AimedAt(suit) : null;
            for (int i = 0; i < sBodies.Count; i++)
            {
                var c = sBodies[i];
                if (!c.Loose || c.Body == null) continue;
                var body = c.Body;
                if (!c.Falling)
                {
                    // freed by the game itself (a piece taken on while fixed, then pulled loose) counts as touched
                    if (c.UntilFreed && body.isKinematic) continue;
                    c.UntilFreed = false;
                    bool touch = !body.isKinematic ||
                                 (suit != null && (body == aimed || (body.ClosestPointOnBounds(suit.position) - suit.position).sqrMagnitude < LooseWakeReach * LooseWakeReach));
                    if (touch) touched++;
                    else
                    {
                        if (!pulled) continue;
                        if (c.SettledPull != Vector3.zero && Vector3.Angle(c.SettledPull, pullLocal) <= LooseWakeAngle) continue;
                    }
                    // let go: it starts with the ship's motion where it is, with no drag (NoDragAboard)
                    body.isKinematic = false;
                    NoDragAboard.Zero(body);
                    if (!sModes.ContainsKey(body)) sModes[body] = body.collisionDetectionMode;
                    body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                    body.velocity = velocityBefore + Vector3.Cross(spinBefore, body.position - pivotBefore);
                    body.angularVelocity = spinBefore;
                    body.WakeUp();
                    c.Falling = true;
                    c.Still = touch ? -LooseWokenGrace : 0f;
                    woke++;
                }
                var before = velocityBefore + Vector3.Cross(spinBefore, body.position - pivotBefore);
                var after = PointVelocity(body.position + body.velocity * dt);
                var velocity = body.velocity + (after - before) + pull * dt;
                var relative = velocity - after;
                if (pulled)
                {
                    var down = pull.normalized;
                    float fall = Vector3.Dot(relative, down);
                    if (fall > FallSpeedCap) { velocity -= down * (fall - FallSpeedCap); relative = velocity - after; }
                }
                // landing: the physics alone let them fall through the decks with the ship at speed, so each one's box
                // is swept along its fall this step; a deck in the way (one of the ship's own fixed surfaces, facing back
                // against the pull) and it is set down on it and pinned; a wall takes away its speed into the wall
                float move = relative.magnitude * dt;
                if (move > 1e-4f && LooseLanding(body, relative / relative.magnitude, move, pulled ? pull.normalized : Vector3.zero, out var hit))
                {
                    if (pulled && Vector3.Angle(hit.normal, -pull) <= 50f)
                    {
                        var at = body.position + relative.normalized * Mathf.Max(0f, hit.distance - 0.01f);
                        var inverse = Quaternion.Inverse(sPrevRotation);
                        c.Position = inverse * (at - sPrevPivot);
                        c.Rotation = inverse * body.rotation;
                        body.position = at;
                        body.isKinematic = true;
                        c.Falling = false;
                        c.SettledPull = pullLocal;
                        sBodies[i] = c;
                        settled++;
                        continue;
                    }
                    float into = Vector3.Dot(relative, hit.normal);
                    if (into < 0f) { relative -= hit.normal * into; velocity = after + relative; }
                }
                body.velocity = velocity;
                // at rest against the ship: pinned again where it lies, in the ship's frame (as it was at the last step)
                bool still = relative.magnitude < LooseStillSpeed && (body.angularVelocity - sSpin).magnitude < 0.1f;
                c.Still = still ? c.Still + dt : Mathf.Min(c.Still, 0f);
                // not pinned while the suit is right beside it (it may be about to push it)
                if (suit != null && (body.ClosestPointOnBounds(suit.position) - suit.position).sqrMagnitude < LooseWakeReach * LooseWakeReach)
                    c.Still = Mathf.Min(c.Still, 0f);
                if (c.Still >= LooseSettleSeconds)
                {
                    var inverse = Quaternion.Inverse(sPrevRotation);
                    c.Position = inverse * (body.position - sPrevPivot);
                    c.Rotation = inverse * body.rotation;
                    body.isKinematic = true;
                    c.Falling = false;
                    c.SettledPull = pullLocal;
                    settled++;
                }
                else falling++;
                sBodies[i] = c;
            }
        }

        // ---- loose things aboard -------------------------------------------------------------------------------------
        // Loose things aboard fall toward the stern while the drive burns and reach the deck fast; checked once a step
        // they can pass right through a thin plate. While the ship is under way they are checked continuously (the
        // speculative kind, the cheapest), and put back as they were when it stops.
        const float CollisionScanSeconds = 10f;     // pieces split off are taken on at once (TakeOn)
        static readonly Dictionary<Rigidbody, CollisionDetectionMode> sModes = new Dictionary<Rigidbody, CollisionDetectionMode>();
        static float sNextCollisionScan;

        static void ScanLooseAboard()
        {
            // parked, the ship is still: nothing to switch, the effects aboard drawn again
            if (sParked) { ShowGraphs(); return; }
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

        // ---- the suit aboard -----------------------------------------------------------------------------------------
        // Unseated aboard a ship under way, the suit is carried: each step it gains whatever the ship's motion gained
        // where it is, so the hull never has to ram it along (that ramming, 50 times a second, bounced the suit about
        // and pushed it through thin plates). The drive's push is then felt as a gentle, smooth pull toward the stern:
        // [Flight] AboardPull times the ship's acceleration, with the fall speed capped so landings stay soft. While
        // aboard, the suit's collisions are checked continuously.
        const float FallSpeedCap = 3f;                      // m/s, relative to the ship, along the pull
        static CollisionDetectionMode? sPlayerMode;

        static void CarryAboard(Rigidbody? player, bool aboard, Vector3 shipVelocityBefore, Vector3 accel, float dt)
        {
            if (player == null || !aboard)
            {
                Solid(null, false);
                DeckBoots.Reset(player == null ? "seated" : "not aboard");
                PullActive = false;
                if (player != null && sPlayerMode.HasValue) { player.collisionDetectionMode = sPlayerMode.Value; sPlayerMode = null; }
                return;
            }
            if (!sPlayerMode.HasValue)
            {
                sPlayerMode = player.collisionDetectionMode;
                player.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                Plugin.Log.LogInfo($"[Flight] the suit is aboard a ship under way ({player.mass:0} kg): carried, with a pull of {Plugin.FlightAboardPull.Value:0.##}x the ship's acceleration.");
            }
            if (sParked) Solid(null, false); else Solid(player, true);
            var shipVelocityAfter = PointVelocity(player.position + player.velocity * dt);
            var pull = AboardPullFrom(accel);
            PullActive = pull.sqrMagnitude > 0.01f;
            var carried = player.velocity + (shipVelocityAfter - shipVelocityBefore);
            // magnetic boots on a deck hold the suit (the pull just presses it onto the deck); otherwise the pull acts
            if (!DeckBoots.Step(player, carried, shipVelocityAfter, PullActive ? pull : Vector3.zero, dt, out var velocity))
            {
                velocity = carried + pull * dt;
                // soft landings: the fall along the pull, relative to the ship, is capped
                float pullSize = pull.magnitude;
                if (pullSize > 1e-4f)
                {
                    var down = pull / pullSize;
                    var relative = velocity - shipVelocityAfter;
                    float falling = Vector3.Dot(relative, down);
                    if (falling > FallSpeedCap) velocity -= down * (falling - FallSpeedCap);
                    velocity = KeepOffDeck(player, velocity, shipVelocityAfter, down, dt);
                }
            }
            player.velocity = velocity;

            // diagnostic, twice a second: the suit's speed against the ship's where it is
            if (Plugin.SpaceDebugPrint.Value && Time.time >= sNextAboardLog)
            {
                sNextAboardLog = Time.time + 0.5f;
                var relative = velocity - shipVelocityAfter;
                var local = Quaternion.Inverse(ShipRotation) * relative;
                Plugin.Log.LogInfo($"[Flight] aboard: suit {velocity.magnitude:0.00} m/s, ship there {shipVelocityAfter.magnitude:0.00} m/s, " +
                                   $"suit against ship {relative.magnitude:0.00} m/s (starboard {local.x:0.00}, up {local.y:0.00}, ahead {local.z:0.00}); " +
                                   $"ship accel {accel.magnitude:0.00} m/s2, pull {pull.magnitude:0.00}.");
            }
        }

        // A suit off its boots (jumping, or locked on nowhere) under the pull: the suit is a ball (its collider a sphere
        // around the head) and the decks are thin plates, which the physics alone let it sink through. It is never let
        // closer to a deck than the ball's own size. Only the ship's own (carried) surfaces count as decks.
        static readonly RaycastHit[] sDeckHits = new RaycastHit[16];
        static HashSet<Rigidbody>? sCarriedSet;

        internal static bool IsCarried(Rigidbody body)
        {
            if (sCarriedSet == null)
            {
                sCarriedSet = new HashSet<Rigidbody>();
                foreach (var c in sBodies) if (c.Body != null) sCarriedSet.Add(c.Body);
            }
            return sCarriedSet.Contains(body);
        }

        static Vector3 KeepOffDeck(Rigidbody player, Vector3 velocity, Vector3 shipVelocity, Vector3 down, float dt)
        {
            const float Reach = 0.6f;
            float stand = 0f;
            float radius = 0.2f;
            // cast from a little above the suit, so a deck it has already sunk into is still found
            var from = player.position - down * 0.6f;
            int count = Physics.SphereCastNonAlloc(from, radius, down, sDeckHits, stand + Reach + 0.6f, ~0, QueryTriggerInteraction.Ignore);
            float deck = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = sDeckHits[i];
                if (hit.collider == null || hit.distance <= 0f) continue;
                var body = hit.collider.attachedRigidbody;
                if (body == null || body == player || !IsCarried(body)) continue;
                deck = Mathf.Min(deck, hit.distance - 0.6f + radius);    // from the suit's middle to the deck's surface
            }
            var relative = velocity - shipVelocity;
            // never closer than the ball's own size: no speed into the deck there, and pushed back out
            float floor = 0.35f;
            if (deck < floor)
            {
                float into = Vector3.Dot(relative, down);
                if (into > 0f) relative -= down * into;
                relative -= down * Mathf.Min(2f, (floor - deck) / dt);
            }
            return shipVelocity + relative;
        }

        // Diagnostic, once a second under the pull: what is below the suit (toward the stern), and whether the suit can
        // collide with it at all (its collider on, the two layers set to collide), to find why it falls through decks.
        static readonly RaycastHit[] sFloorHits = new RaycastHit[16];

        // Out of the seat aboard a ship under way, the ship and the suit are drawn where the physics has them, not
        // smoothed between physics steps: the game aims the grapple, cutter and grab with rays from the camera against the
        // physics, and with the camera smoothed and the ship's colliders up to a step (over a meter at speed) ahead, the
        // aim jumped about. Seated (the outside views) the smoothing is kept.
        // Every carried body counts, not only the ones this mod set smoothing on: a few (the radiation containment doors)
        // came smoothed from the game and were drawn up to a step behind the rest, jumping about.
        static bool sSolid;
        static readonly Dictionary<Rigidbody, RigidbodyInterpolation> sSolidSaved = new Dictionary<Rigidbody, RigidbodyInterpolation>();

        static void Solid(Rigidbody? player, bool on)
        {
            if (on == sSolid)
            {
                // the game turns the suit's smoothing back on when it comes out of the pause menu: the camera then lagged
                // the plain-drawn ship and the whole ship seemed to bounce
                if (on && player != null && player.interpolation != RigidbodyInterpolation.None)
                {
                    player.interpolation = RigidbodyInterpolation.None;
                    Plugin.Log.LogInfo("[Flight] the suit's smoothing was turned back on (pause menu?): set plain again.");
                }
                return;
            }
            sSolid = on;
            if (on)
            {
                int smoothed = 0;
                if (player != null) { sSolidSaved[player] = player.interpolation; player.interpolation = RigidbodyInterpolation.None; }
                foreach (var c in sBodies)
                {
                    if (c.Body == null || sSolidSaved.ContainsKey(c.Body)) continue;
                    sSolidSaved[c.Body] = c.Body.interpolation;
                    if (c.Body.interpolation != RigidbodyInterpolation.None) { c.Body.interpolation = RigidbodyInterpolation.None; smoothed++; }
                }
                sAimPoseKept = false;
                Plugin.Log.LogInfo($"[Flight] aboard on foot: the ship and suit drawn as the physics has them ({smoothed} smoothed body(ies) set plain), " +
                                   "and the camera moved with the ship for the game's aim.");
            }
            else
            {
                foreach (var pair in sSolidSaved) if (pair.Key != null) pair.Key.interpolation = pair.Value;
                sSolidSaved.Clear();
                Plugin.Log.LogInfo("[Flight] the ship drawn smoothed again.");
            }
        }

        // The game's camera is placed by Cinemachine late in each frame; the game aims (grapple, cutter, grab, the
        // highlight) with rays from it earlier in the next frame, after that frame's physics steps have moved the ship
        // (1.3 m a step at 67 m/s). So the rays started from where the camera was against the ship a frame ago, one or
        // two steps behind: the aim jumped about. Each frame, before the game's own updates, the camera is moved with
        // the ship by however far the ship moved since the camera was placed (Cinemachine puts it back in its place
        // before drawing anyway).
        static bool sAimPoseKept;
        static Vector3 sAimPivot;
        static Quaternion sAimRotation = Quaternion.identity;

        internal static void AimCamera()
        {
            if (!sSolid || !sMoving) return;
            var camera = LynxCameraController.MainCameraTransform;
            if (camera == null || !sAimPoseKept) return;
            var turn = ShipRotation * Quaternion.Inverse(sAimRotation);
            camera.SetPositionAndRotation(sPivot + turn * (camera.position - sAimPivot), turn * camera.rotation);
        }

        // after the camera has been placed for the frame (LateUpdate): the ship's pose then
        static void KeepAimPose()
        {
            sAimPivot = sPivot;
            sAimRotation = ShipRotation;
            sAimPoseKept = true;
        }

        static float sNextAboardLog;
        // The ship's pose where the last physics step left it, and where this step takes it; its nose direction.
        internal static Vector3 PrevPivot => sParked ? sParkPivot : sPrevPivot;
        internal static Quaternion PrevRotation => sParked ? sParkRotation : sPrevRotation;
        internal static Quaternion Rotation => BodyRotation;
        internal static Vector3 ForwardDirection => sParked ? sParkRotation * Vector3.forward : Forward;
        // The suit aboard is being pulled toward the stern (the drive burning, AboardPull above 0).
        internal static bool PullActive { get; private set; }

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
            if (sStepSeenThisFrame)
            {
                sPhysicsWatch.Stop();
                sPhysicsMs += sPhysicsWatch.Elapsed.TotalMilliseconds;
                sStepSeenThisFrame = false;
            }
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
                if (sNextPerfLog > 0f && Plugin.SpaceDebugPrint.Value)
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
            // the weapon wheel has the mouse and left stick while open; aiming (PDC, torpedo) has the mouse and right stick
            bool wheelOpen = WeaponWheel.Open;
            if (wheelOpen || Weapons.Aiming) camera = true;

            // side thrusters: the suit's move keys
            float x = Axis(KeyCode.D, KeyCode.A), y = Axis(KeyCode.Space, KeyCode.C) - (Input.GetKey(KeyCode.LeftShift) ? 1f : 0f);
            float z = Axis(KeyCode.W, KeyCode.S);
            if (pad != null)
            {
                if (!wheelOpen) { x += pad.LeftStickX.Value; z += pad.LeftStickY.Value; }
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
            if (Plugin.FlightFlipKey.Value.IsDown() || (pad != null && pad.DPadLeft.WasPressed))
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

        static bool sSmoothAgain;

        internal static void FixedStep()
        {
            if (!sMoving) return;
            if (sSmoothAgain)
            {
                sSmoothAgain = false;
                foreach (var body in sInterpolated) if (body != null) body.interpolation = RigidbodyInterpolation.Interpolate;
            }
            sStepsThisFrame++;
            ScanLooseAboard();
            if (!sFlying && !sParked && Throttle <= 0f && Speed < 0.01f && sSpin.magnitude < 1e-4f) { Release(); Plugin.Log.LogInfo("[Flight] the ship has come to rest."); return; }
            float dt = Time.fixedDeltaTime;
            if (dt <= 0f) return;

            float drive = Mathf.Max(0f, Plugin.FlightDriveAcceleration.Value);
            float thrusters = Mathf.Max(0f, Plugin.FlightThrusterAcceleration.Value);
            float turnAccel = Mathf.Max(0f, Plugin.FlightTurnAcceleration.Value) * Mathf.Deg2Rad;
            float maxRate = Mathf.Max(1f, Plugin.FlightMaxTurnRate.Value) * Mathf.Deg2Rad;

            bool autoStop = AutoStep(drive);
            var player = FlightSeat.Seated ? null : FlightSeat.PlayerBody;
            bool aboard = player != null && Aboard(player.position);
            var playerShipVelocity = aboard ? PointVelocity(player!.position) : Vector3.zero;

            // speed: the drive pushes forward, the side thrusters any way; the brake fires them against the drift
            var rcs = (Right * sThrust.x + Up * sThrust.y + Forward * sThrust.z) * thrusters;
            var spinStart = sSpin;
            if (sBrake || autoStop) rcs += Vector3.ClampMagnitude(-sVelocity / dt, thrusters);
            var accel = Forward * (drive * Throttle) + rcs;
            // how hard the side thrusters are working, 0..1, for the gauge (turning counts too)
            float rcsLevel = thrusters > 0f ? Mathf.Clamp01(rcs.magnitude / thrusters) : 0f;
            if (sTurn.sqrMagnitude > 0.01f || sAuto == Auto.Flip) rcsLevel = Mathf.Max(rcsLevel, 0.5f);
            RcsLevel = Mathf.Lerp(RcsLevel, rcsLevel, 1f - Mathf.Exp(-dt / 0.15f));
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
            else if (sHold || sBrake || sAuto == Auto.Stop || Weapons.HoldAttitude)
            {
                // each axis not being turned is stopped by the thrusters
                if (Mathf.Abs(command.x) < 1e-4f) spinLocal.x = Mathf.MoveTowards(spinLocal.x, 0f, turnAccel * dt);
                if (Mathf.Abs(command.y) < 1e-4f) spinLocal.y = Mathf.MoveTowards(spinLocal.y, 0f, turnAccel * dt);
                if (Mathf.Abs(command.z) < 1e-4f) spinLocal.z = Mathf.MoveTowards(spinLocal.z, 0f, turnAccel * dt);
            }
            spinLocal = Vector3.ClampMagnitude(spinLocal, maxRate);
            sSpin = Quaternion.Inverse(local) * spinLocal;
            RcsCommand = thrusters > 0f ? rcs / thrusters : Vector3.zero;
            SpinCommand = turnAccel > 0f ? (sSpin - spinStart) / (turnAccel * dt) : Vector3.zero;

            if (sParked)
            {
                // the flight goes on in the model only; aboard the still ship the drive is a plain pull
                Integrate(dt);
                ParkedFall(accel, dt);
                CarryAboard(player, aboard, Vector3.zero, accel, dt);
                return;
            }
            var velocityBefore = sVelocity;
            var spinBefore = sSpin;
            var pivotBefore = sPivot;
            Move(dt);
            ShipWalls.Step(ShipBodies(), ref sPivot, ref sVelocity, ref sSpin);
            LooseFall(velocityBefore, spinBefore, pivotBefore, accel, dt);
            CarryAboard(player, aboard, playerShipVelocity, accel, dt);
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

        // The ship's pose at the previous physics step, for drawing between steps.
        static Vector3 sPrevPivot;
        static Quaternion sPrevRotation = Quaternion.identity;

        // Every frame, before the cameras (LateUpdate): the ship is drawn where it is between two physics steps, not
        // where the last step left it (the frame rate and the 50 steps a second do not line up, and the difference
        // showed as shaking). The ship's frame (cameras, seat) and its plain fittings follow it; its bodies are moved
        // smoothly by the physics engine itself (interpolation, set while carried).
        internal static void LateFrame()
        {
            if (sSolid) KeepAimPose();
            if (!sMoving || sParked || sFrame == null) return;
            float alpha = sSolid ? 1f : Time.fixedDeltaTime > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 1f;
            var pivot = Vector3.Lerp(sPrevPivot, sPivot, alpha);
            var rotation = Quaternion.Slerp(sPrevRotation, ShipRotation, alpha);
            sFrame.SetPositionAndRotation(pivot, rotation);
            foreach (var rider in sRiders)
                if (rider.Body == null && rider.Transform != null)
                    rider.Transform.SetPositionAndRotation(pivot + rotation * rider.Position, rotation * rider.Rotation);
        }

        // Diagnostic (the ship shaking at 250 m/s and up): how far the ship's bodies are drawn (the physics' smoothing)
        // from where the camera's frame has them this frame, as a share of one step's travel; once a second over 50 m/s.

        // the ship's own bodies (not the loose things lying aboard)
        static IEnumerable<Rigidbody> ShipBodies()
        {
            foreach (var c in sBodies) if (c.Body != null && !c.Loose) yield return c.Body;
        }

        static void Integrate(float dt)
        {
            sPrevPivot = sPivot;
            sPrevRotation = ShipRotation;
            float rate = sSpin.magnitude;
            var turn = rate > 1e-6f ? Quaternion.AngleAxis(rate * dt * Mathf.Rad2Deg, sSpin / rate) : Quaternion.identity;
            sPivot += sVelocity * dt;
            sAttitude = turn * sAttitude;
        }

        // Parked: the loose things aboard (all free now) feel the drive's pull, like gravity; the game's physics does the
        // rest (falling, landing, resting).
        const float GentleDepenetration = 0.3f, GentleSeconds = 3f;

        // No collision damage around a change of state: for a few seconds after parking, catching up, or the collision box
        // moving, the game's collision-triggered damage (parts shattering, fuel fires, electrical sparks) is not set off
        // by anything. Things aboard settle against each other then (released loose things, contacts that start all at
        // once when collisions come back on), and the game counted those as impacts all over the ship.
        const float NoDamageSeconds = 4f;
        static float sNoDamageUntil;

        static void GuardDamage() => sNoDamageUntil = Time.time + NoDamageSeconds;

        // The game's damage from bodies knocking together (impact damage, the knocks that break a fitting's joints, the
        // sparks and leaks): the same hold, and while parked no knock faster than 20 m/s counts (on a still ship nothing
        // can move that fast; such a knock is an artifact of the ship having just stopped).

        [HarmonyLib.HarmonyPatch(typeof(HierarchyCollisionListener), "HandleCollision")]
        static class NoKnockDamageAtChange
        {
            static bool Prefix(Collision collision)
            {
                if (!sMoving) return true;
                bool hold = Time.time < sNoDamageUntil || (sParked && collision != null && collision.relativeVelocity.sqrMagnitude > 20f * 20f);
                return !hold;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(TriggerableCollisionComponent), nameof(TriggerableCollisionComponent.MeetsCollisionConditions))]
        static class NoDamageAtChange
        {
            static bool Prefix(ref bool __result)
            {
                if (!sMoving || Time.time >= sNoDamageUntil) return true;
                __result = false;
                return false;
            }
        }
        const float LooseFallCap = 1.5f;            // m/s toward the stern, for loose things aboard the parked ship
        static readonly Dictionary<Rigidbody, float> sGentle = new Dictionary<Rigidbody, float>();
        static float sGentleUntil;

        static void EndGentle()
        {
            foreach (var pair in sGentle) if (pair.Key != null) pair.Key.maxDepenetrationVelocity = pair.Value;
            sGentle.Clear();
        }

        static void ParkedFall(Vector3 accel, float dt)
        {
            if (sGentle.Count > 0 && Time.time >= sGentleUntil) EndGentle();
            // no pull while they are let go gently: they may then only ease out of a deck at 0.3 m/s, and at full thrust
            // the pull pressed them into it faster than that, through it
            if (sGentle.Count > 0) return;
            var pull = AboardPullFrom(accel);
            if (pull.sqrMagnitude < 1e-6f) return;
            var down = pull.normalized;
            foreach (var c in sBodies)
            {
                var body = c.Body;
                if (body == null || !c.Loose || body.isKinematic) continue;
                body.velocity += pull * dt;
                // landing softly: the game counts a hard enough knock as damage (with the drive burning, things falling
                // a few meters set off sparks and fuel fires all over the ship)
                float falling = Vector3.Dot(body.velocity, down);
                if (falling > LooseFallCap) body.velocity -= down * (falling - LooseFallCap);
            }
        }

        // Diagnostic, for a few seconds after catching up: how far the ship's bodies are from where they are sent (a
        // shaking ship after catching up).

        static void Move(float dt)
        {
            Integrate(dt);
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
                if (body == null || carried.Falling) continue;
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
            // bodies held by a joint, from either end (a terminal on a wall is jointed from the wall's side): fittings,
            // not loose things, whatever their speed
            var jointed = new HashSet<Rigidbody>();
            foreach (var joint in Object.FindObjectsOfType<Joint>())
            {
                if (joint == null) continue;
                var own = joint.GetComponent<Rigidbody>();
                if (own != null) jointed.Add(own);
                if (joint.connectedBody != null) jointed.Add(joint.connectedBody);
            }
            int fittings = 0;
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
                string reason = "";
                if (!body.isKinematic)
                {
                    string why = unlisted ? "unregistered fittings aboard" : WhyCarried(body, world != null, em, main, haveMain, carriedSoFar);
                    reason = why;
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
                if (body.interpolation == RigidbodyInterpolation.None) { body.interpolation = RigidbodyInterpolation.Interpolate; sInterpolated.Add(body); }
                else
                {
                    held++;
                    Plugin.Log.LogInfo($"[Flight] held fixed by the game: '{body.name}', {body.mass / 1000f:0.#} t, " +
                                       $"{body.GetComponentsInChildren<StructurePart>().Length} part(s), at {body.position}.");
                }
                bool loose = free && reason == "lying still aboard" && !jointed.Contains(body);
                if (free && reason == "lying still aboard" && !loose) fittings++;
                sBodies.Add(new Carried { Body = body, Position = inverse * (body.position - sPivot), Rotation = inverse * body.rotation, WasFree = free, Loose = loose });
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
            Plugin.Log.LogInfo($"[Flight] {fittings} jointed fitting(s) lying still aboard kept with the ship (never let loose).");
            Plugin.Log.LogInfo($"[Flight] {freed} free body(ies) held fixed while carried; the ship spans {box.size.x:0} x {box.size.y:0} x {box.size.z:0} m (with margin).");
            return held;
        }

        // The top object the chair sits under, if no carried body is above the chair: everything under it that is
        // not a body of its own goes with the ship by its transform.
        // The chair sat in and every other command chair: each one's top object rides along. Only the first chair's deck
        // used to: the other decks' chair points stayed in the bay, and out there their sit prompt never came up.
        static void CollectRiders(Transform chair)
        {
            sRiders.Clear();
            var carried = new HashSet<Rigidbody>();
            foreach (var c in sBodies) if (c.Body != null) carried.Add(c.Body);
            var roots = new HashSet<Transform>();
            CollectRider(chair, carried, roots);
            foreach (var other in FlightSeat.Chairs)
                if (other != null && other != chair) CollectRider(other, carried, roots);
        }

        static void CollectRider(Transform chair, HashSet<Rigidbody> carried, HashSet<Transform> roots)
        {
            if (roots.Contains(chair.root)) return;
            var above = chair.GetComponentInParent<Rigidbody>();
            var root = chair.root;
            if (above != null && carried.Contains(above))
            {
                Plugin.Log.LogInfo($"[Flight] chair '{chair.parent?.name}/{chair.name}' rides on '{above.name}', a carried body.");
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
            if (!roots.Add(root)) return;
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
    }
}
