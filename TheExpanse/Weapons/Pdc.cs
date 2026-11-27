using System.Collections.Generic;
using BBI.Unity.Game;
using InControl;
using UnityEngine;

namespace TheExpanse
{
    // Point defense cannons, step 2: the turrets track the aim point (no firing yet).
    //   aim:     in PDC (and torpedo) mode the right stick / mouse moves an aim cursor over the view; the aim point is
    //            where the ray through it first hits something not of the ship, else a point at PdcRange;
    //   turrets: each PDC is a chain of three pieces, as built in the model:
    //              PDC_<name>_New_Truss  turns about its own long axis, through the middle of its mesh, carrying the
    //                                    body and the barrel;
    //              PDC_<name>_Body_01    turns through its pivot about its own axis square to both the truss axis and
    //                                    the barrel (the bottom plate's normal: local z on the side PDCs; the aft PDCs
    //                                    in Engineering Objects are turned differently), carrying the barrel;
    //              PDC_<name>_Barrel_01  points along its long axis, away from the body.
    //            Their transforms are moved; their joints are kept. Out of PDC mode they turn back to rest and are then
    //            left alone;
    //   sight:   a turret only turns while the aim point can see the middle of its body (a ray from the body to the aim
    //            point meets nothing but the turret's own PDC_<name>_* pieces); otherwise it stays at rest.
    internal enum TurretState { Rest, Tracking, OnTarget }

    internal static class Pdc
    {
        internal sealed class Turret
        {
            internal string Name = "", Prefix = "";                            // Prefix PDC_<name>_: its own pieces
            internal Transform Truss = null!, Body = null!, Barrel = null!, Frame = null!;   // Frame: the truss's parent
            // at rest, in Frame's space
            internal Vector3 TrussAxis, TrussCenter, BodyAxis, Pointing;
            internal Vector3 TrussPos, BodyPos, BarrelPos;
            internal Quaternion TrussRot, BodyRot, BarrelRot;
            internal float TrussAngle, BodyAngle;                               // degrees turned now
            internal bool Driven;
            internal TurretState State;
            internal Vector3 MuzzleWorld, PointingWorld;
            internal float BarrelHalf;
        }

        const float BodyLimit = 110f;                                           // the body's turn either way from rest
        static readonly List<Turret> sTurrets = new List<Turret>();
        static readonly RaycastHit[] sHits = new RaycastHit[32];
        static float sCursorYaw, sCursorPitch;                                  // degrees off the camera's forward
        static bool sLoggedNone;

        internal static IReadOnlyList<Turret> Turrets => sTurrets;
        internal static Vector3 AimPoint { get; private set; }
        internal static bool AimHit { get; private set; }
        internal static string AimName { get; private set; } = "";
        internal static int OnTarget { get; private set; }

        internal static void Reset()
        {
            sTurrets.Clear();
            sCursorYaw = sCursorPitch = 0f;
            sLoggedNone = false;
        }

        // From WeaponMounts' scan: the three pieces of one PDC, at rest (as built).
        internal static void Add(string name, Transform truss, Transform body, Transform barrel)
        {
            var frame = truss.parent;
            if (frame == null) return;
            if (!LongAxis(truss, out var trussAxis, out var trussCenter, out _)) { Plugin.Log.LogWarning($"[Weapons] PDC '{name}': its truss has no mesh; not driven."); return; }
            if (!LongAxis(barrel, out var barrelAxis, out var barrelCenter, out float barrelLength)) { Plugin.Log.LogWarning($"[Weapons] PDC '{name}': its barrel has no mesh; not driven."); return; }
            // the barrel points away from the body
            if (Vector3.Dot(barrelAxis, barrelCenter - body.position) < 0f) barrelAxis = -barrelAxis;
            // the body's own axis nearest square to the truss axis and the barrel
            var across = Vector3.Cross(trussAxis, barrelAxis);
            string bodyLocal = "z";
            var bodyAxis = body.forward;
            if (across.sqrMagnitude > 1e-4f)
            {
                across.Normalize();
                float bx = Mathf.Abs(Vector3.Dot(body.right, across)), by = Mathf.Abs(Vector3.Dot(body.up, across)), bz = Mathf.Abs(Vector3.Dot(body.forward, across));
                if (bx >= by && bx >= bz) { bodyAxis = body.right; bodyLocal = "x"; }
                else if (by >= bz) { bodyAxis = body.up; bodyLocal = "y"; }
            }
            var t = new Turret
            {
                Name = name, Prefix = "PDC_" + name + "_", Truss = truss, Body = body, Barrel = barrel, Frame = frame,
                TrussAxis = frame.InverseTransformDirection(trussAxis).normalized,
                TrussCenter = frame.InverseTransformPoint(trussCenter),
                BodyAxis = frame.InverseTransformDirection(bodyAxis).normalized,
                Pointing = frame.InverseTransformDirection(barrelAxis).normalized,
                TrussPos = frame.InverseTransformPoint(truss.position),
                BodyPos = frame.InverseTransformPoint(body.position),
                BarrelPos = frame.InverseTransformPoint(barrel.position),
                TrussRot = Quaternion.Inverse(frame.rotation) * truss.rotation,
                BodyRot = Quaternion.Inverse(frame.rotation) * body.rotation,
                BarrelRot = Quaternion.Inverse(frame.rotation) * barrel.rotation,
                BarrelHalf = barrelLength * 0.5f,
            };
            sTurrets.Add(t);
            var ship = FlightModel.Frame;
            Plugin.Log.LogInfo($"[Weapons] PDC '{name}': truss turns about {Describe(trussAxis, ship)} through {trussCenter}, body about its local {bodyLocal} " +
                               $"{Describe(bodyAxis, ship)} ({Vector3.Angle(trussAxis, bodyAxis):0} deg to the truss axis), barrel points " +
                               $"{Describe(barrelAxis, ship)}, {barrelLength:0.0} m long; parents: truss '{frame.name}', body '{(body.parent != null ? body.parent.name : "none")}', " +
                               $"barrel '{(barrel.parent != null ? barrel.parent.name : "none")}'{(body.parent != frame || barrel.parent != frame ? " (differ: placed in world space)" : "")}.");
        }

        // The longest side of a piece's mesh: its direction and middle in world space, and its length.
        static bool LongAxis(Transform t, out Vector3 axis, out Vector3 center, out float length)
        {
            axis = center = Vector3.zero; length = 0f;
            var filter = t.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;
            var b = filter.sharedMesh.bounds;
            var size = Vector3.Scale(b.size, t.lossyScale);
            int k = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            axis = (k == 0 ? t.right : k == 1 ? t.up : t.forward).normalized;
            center = t.TransformPoint(b.center);
            length = k == 0 ? size.x : k == 1 ? size.y : size.z;
            return true;
        }

        static string Describe(Vector3 dir, Transform? frame)
        {
            if (frame == null) return dir.ToString();
            float f = Vector3.Dot(dir, frame.forward), u = Vector3.Dot(dir, frame.up), r = Vector3.Dot(dir, frame.right);
            float m = Mathf.Max(Mathf.Abs(f), Mathf.Abs(u), Mathf.Abs(r));
            string name = m == Mathf.Abs(f) ? (f > 0 ? "fore" : "aft") : m == Mathf.Abs(u) ? (u > 0 ? "dorsal" : "ventral") : (r > 0 ? "starboard" : "port");
            return $"{name} ({f:0.00} fwd, {u:0.00} up, {r:0.00} right)";
        }

        // ---- aim (every frame while seated, from Weapons.Tick) -----------------------------------------------------

        internal static void ReadAim()
        {
            if (!Weapons.Aiming) return;
            var pad = InputManager.ActiveDevice;
            float rate = Mathf.Max(1f, Plugin.WeaponsAimSpeed.Value) * Time.unscaledDeltaTime;
            float invert = LynxControlsUserOptions.InvertYAxis ? -1f : 1f;
            if (pad != null) { sCursorYaw += pad.RightStickX.Value * rate; sCursorPitch += pad.RightStickY.Value * rate * invert; }
            float mouse = Mathf.Max(0f, Plugin.FlightMouseSensitivity.Value) * 0.5f;
            sCursorYaw += MouseAxis("mouse x", "Mouse X") * mouse;
            sCursorPitch += MouseAxis("mouse y", "Mouse Y") * mouse * invert;
            var camera = Camera.main;
            float halfV = camera != null ? camera.fieldOfView * 0.5f : 30f;
            float halfH = camera != null ? Mathf.Atan(Mathf.Tan(halfV * Mathf.Deg2Rad) * camera.aspect) * Mathf.Rad2Deg : 50f;
            sCursorYaw = Mathf.Clamp(sCursorYaw, -halfH * 0.95f, halfH * 0.95f);
            sCursorPitch = Mathf.Clamp(sCursorPitch, -halfV * 0.95f, halfV * 0.95f);
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

        // The view the player sees: the outside / nose camera, or the seat's.
        internal static bool View(out Vector3 position, out Quaternion rotation)
        {
            if (FlightCamera.OffSeat) { position = FlightCamera.CameraPosition; rotation = FlightCamera.CameraRotation; return true; }
            var camera = Camera.main;
            position = camera != null ? camera.transform.position : Vector3.zero;
            rotation = camera != null ? camera.transform.rotation : Quaternion.identity;
            return camera != null;
        }

        internal static Vector3 CursorDirection(Quaternion view) => view * Quaternion.Euler(-sCursorPitch, sCursorYaw, 0f) * Vector3.forward;

        // ---- turrets (every frame, LateUpdate after the ship's frame is placed) ------------------------------------

        internal static void Frame()
        {
            // the aim point: for the PDCs, and for the torpedoes (their lock) at their own range
            if (Weapons.Aiming) FindAimPoint();
            if (sTurrets.Count == 0) return;
            bool aiming = Weapons.Allowed && Weapons.Mode == WeaponMode.Pdc;
            float slew = Mathf.Max(1f, Plugin.WeaponsPdcSlewRate.Value) * Time.deltaTime;
            int onTarget = 0;
            for (int i = sTurrets.Count - 1; i >= 0; i--)
            {
                var t = sTurrets[i];
                // cut off or gone: no longer driven
                if (t.Truss == null || t.Body == null || t.Barrel == null || t.Frame == null || t.Truss.parent != t.Frame)
                {
                    Plugin.Log.LogInfo($"[Weapons] PDC '{t.Name}' is no longer on its mount; left alone.");
                    sTurrets.RemoveAt(i);
                    continue;
                }
                t.State = TurretState.Rest;
                float trussTo = 0f, bodyTo = 0f;
                var bodyWorld = t.Body.position;
                bool sees = aiming && Sees(t, bodyWorld) && Solve(t, t.Frame.InverseTransformPoint(AimPoint), out trussTo, out bodyTo);
                if (!sees)
                {
                    trussTo = bodyTo = 0f;
                    if (!t.Driven) continue;
                }
                t.TrussAngle = Mathf.MoveTowardsAngle(t.TrussAngle, trussTo, slew);
                t.BodyAngle = Mathf.MoveTowards(t.BodyAngle, bodyTo, slew);
                Place(t);
                bool atRest = Mathf.Abs(t.TrussAngle) < 0.01f && Mathf.Abs(t.BodyAngle) < 0.01f;
                t.Driven = sees || !atRest;
                if (sees)
                {
                    bool on = Vector3.Angle(t.PointingWorld, AimPoint - t.MuzzleWorld) < 1.5f;
                    t.State = on ? TurretState.OnTarget : TurretState.Tracking;
                    if (on) onTarget++;
                }
            }
            OnTarget = onTarget;
        }

        // The truss and body angles that point the barrel at a point (all in Frame's space). The truss turn decides the
        // plane the barrel can sweep (the plane across the turned body axis); the body turn then points it in that plane.
        // Of the two truss turns that put the point in that plane, the one needing the smaller body turn (then the nearer)
        // is taken. False when the point can't be reached within the body's limit.
        static bool Solve(Turret t, Vector3 target, out float truss, out float body)
        {
            truss = body = 0f;
            var a = t.TrussAxis;
            var b = t.BodyAxis;
            var bPar = Vector3.Dot(b, a) * a;
            var bPerp = b - bPar;
            float bPerpLen = bPerp.magnitude;
            if (bPerpLen < 1e-3f) return false;
            var u = bPerp / bPerpLen;
            var w = Vector3.Cross(a, u);
            bool found = false;
            // the body's pivot moves with the truss turn: solved twice, from the current pose then from the first answer
            float guess = t.TrussAngle;
            for (int pass = 0; pass < 2; pass++)
            {
                var pivot = t.TrussCenter + Quaternion.AngleAxis(guess, a) * (t.BodyPos - t.TrussCenter);
                var d = (target - pivot).normalized;
                // the turned body axis must be square to the direction: cos(alpha) x + sin(alpha) y = c
                float c = -Vector3.Dot(bPar, d) / bPerpLen;
                float x = Vector3.Dot(u, d), y = Vector3.Dot(w, d);
                float rho = Mathf.Sqrt(x * x + y * y);
                if (rho < 1e-4f || Mathf.Abs(c) > rho) return false;
                float phi = Mathf.Atan2(y, x), spread = Mathf.Acos(Mathf.Clamp(c / rho, -1f, 1f));
                found = false;
                float best = float.MaxValue;
                for (int s = -1; s <= 1; s += 2)
                {
                    float alpha = Mathf.DeltaAngle(0f, (phi + s * spread) * Mathf.Rad2Deg);
                    var q = Quaternion.AngleAxis(alpha, a);
                    var bNow = q * b;
                    var fNow = q * t.Pointing;
                    float beta = Mathf.Atan2(Vector3.Dot(d, Vector3.Cross(bNow, fNow)), Vector3.Dot(d, fNow)) * Mathf.Rad2Deg;
                    if (Mathf.Abs(beta) > BodyLimit) continue;
                    float cost = Mathf.Abs(beta) + 0.25f * Mathf.Abs(Mathf.DeltaAngle(t.TrussAngle, alpha));
                    if (cost < best) { best = cost; truss = alpha; body = beta; found = true; }
                }
                if (!found) return false;
                guess = truss;
            }
            return found;
        }

        static void Place(Turret t)
        {
            var qT = Quaternion.AngleAxis(t.TrussAngle, t.TrussAxis);
            var c = t.TrussCenter;
            // the truss turns about its long axis, carrying the body and barrel
            var bodyPos = c + qT * (t.BodyPos - c);
            var barrelPos = c + qT * (t.BarrelPos - c);
            // the body turns about its own (turned) axis through its pivot, carrying the barrel
            var qB = Quaternion.AngleAxis(t.BodyAngle, qT * t.BodyAxis);
            barrelPos = bodyPos + qB * (barrelPos - bodyPos);
            var frameRot = t.Frame.rotation;
            t.Truss.SetPositionAndRotation(t.Frame.TransformPoint(c + qT * (t.TrussPos - c)), frameRot * (qT * t.TrussRot));
            t.Body.SetPositionAndRotation(t.Frame.TransformPoint(bodyPos), frameRot * (qB * qT * t.BodyRot));
            t.Barrel.SetPositionAndRotation(t.Frame.TransformPoint(barrelPos), frameRot * (qB * qT * t.BarrelRot));
            t.PointingWorld = t.Frame.TransformDirection(qB * qT * t.Pointing);
            t.MuzzleWorld = t.Barrel.position + t.PointingWorld * t.BarrelHalf;
        }

        // Nothing between the turret's body and the aim point but the turret's own pieces (and the thing aimed at).
        static bool Sees(Turret t, Vector3 from)
        {
            var to = AimPoint - from;
            float distance = to.magnitude - 0.5f;
            if (distance <= 0f) return true;
            int n = Physics.RaycastNonAlloc(from, to / (distance + 0.5f), sHits, distance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = sHits[i].collider;
                if (c == null) continue;
                if (FlightSeat.PlayerCollider != null && c == FlightSeat.PlayerCollider) continue;
                if (c.name.StartsWith(t.Prefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                var part = c.GetComponentInParent<StructurePart>();
                if (part != null && part.name.StartsWith(t.Prefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                return false;
            }
            return true;
        }

        static void FindAimPoint()
        {
            float range = Mathf.Max(50f, Weapons.Mode == WeaponMode.Torpedo ? Plugin.WeaponsTorpedoRange.Value : Plugin.WeaponsPdcRange.Value);
            if (!View(out var position, out var rotation)) return;
            var dir = CursorDirection(rotation);
            AimHit = false;
            AimPoint = position + dir * range;
            int n = Physics.RaycastNonAlloc(position, dir, sHits, range, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var hit = sHits[i];
                if (hit.distance >= best || hit.collider == null) continue;
                var part = hit.collider.GetComponentInParent<StructurePart>();
                if (part != null && FlightModel.IsShipPart(part)) continue;        // our own ship
                if (FlightSeat.PlayerCollider != null && hit.collider == FlightSeat.PlayerCollider) continue;
                best = hit.distance;
                AimPoint = hit.point;
                AimHit = true;
                AimName = part != null ? part.name : hit.collider.name;
            }
        }

        internal static void LogNoneOnce()
        {
            if (sLoggedNone || sTurrets.Count > 0) return;
            sLoggedNone = true;
            Plugin.Log.LogInfo("[Weapons] no PDC turrets found on this ship (looked for PDC_<name>_New_Truss, _Body_01 and _Barrel_01).");
        }
    }
}
