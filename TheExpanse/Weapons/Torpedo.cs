using System.Collections.Generic;
using System.Text.RegularExpressions;
using BBI.Unity.Game;
using InControl;
using Unity.Entities;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.VFX;

namespace TheExpanse
{
    // Torpedoes: aim with the right stick (as the PDCs) and press RT (left mouse button) in torpedo mode. Each press
    // launches one, locked on the aim point at that moment, from the tube on the target's side of the ship (the top
    // tube for a target above, the bottom one below; the other when that one is empty or still launching).
    //   tubes:   A_Torpedo_Tube_Hatch_<Top|Bottom> (the door), Torpedo_<Top|Bottom>_Tube (the one in the tube),
    //            Torpedo_<Top|Bottom>_Stack1-4 (its feed tray);
    //   doors:   both tubes' doors swing open on their hinges when torpedo mode is selected (the parts are turned,
    //            their joints kept) and close when it is left, or when the tube is empty;
    //   launch:  (once its door is open) a drawn copy of the tube's
    //            torpedo (no collider) eases out of the tube with puffs of its thrusters, burns briefly to clear the
    //            ship, turns on its thrusters to the locked point, then lights its drive (a blue plume) and flies there. The real torpedo last in that tube's tray is removed (vaporized, the game's
    //            way); when the tray is empty, the one in the tube goes, and the tube is then empty;
    //   impact:  on whatever it meets first (not the own ship) or at the locked point: a real explosion, the game's
    //            ExplosionSystem with the torpedo's own explosive settings when it has them (it damages, shatters and
    //            pushes), else the reactor core's explosion effect with heat damage around.
    internal static class Torpedo
    {
        enum Phase { Opening, Eject, Clear, Turn, Drive }

        sealed class Tube
        {
            internal string Side = "";
            internal Transform Hatch = null!, Frame = null!;
            internal Transform? Loaded;
            internal readonly List<Transform> Tray = new List<Transform>();
            internal Vector3 HatchPos, HingePoint, HingeAxis;        // in Frame's space
            internal Quaternion HatchRot;
            internal float OpenSign = 1f, Open, OpenTarget;
            internal Vector3 LaunchLocal;                            // launch direction, Frame's space
            internal bool Busy;
            internal int Capacity;
            internal Vector3 OutwardLocal;                           // the way its door faces out of the ship, Frame's space
            internal Vector3 MiddleLocal;                            // the door's middle at rest, Frame's space
            internal float PinHalf;                                  // half the door's width along the pin
            internal LineRenderer? HingeLine, ArmLine;
            internal int Count => Tray.FindAll(t => t != null && t.gameObject.activeInHierarchy).Count + (Loaded != null && Loaded.gameObject.activeInHierarchy ? 1 : 0);
        }

        sealed class Torp
        {
            internal Tube Tube = null!;
            internal GameObject Visual = null!;
            internal Phase Phase;
            internal float PhaseTime, Age, Length, Speed;
            internal Vector3 Position, Velocity, LocalStart, Lock;
            internal Quaternion Rotation, LocalRotation;
            internal Vector3 NoseLocal;                              // the torpedo's nose in its own axes
            internal Blast? Warhead;
            internal LineRenderer Plume = null!, Glow = null!;
            internal readonly List<LineRenderer> Puffs = new List<LineRenderer>();
            internal Vector3 Nose => Rotation * NoseLocal;
        }

        struct Blast
        {
            internal ExplosiveComponent Explosive;
            internal bool HasDamage, HasForce, HasShatter;
            internal ExplosiveDamageSettings Damage;
            internal ExplosiveForceSettings Force;
            internal ExplosiveShatterSettings Shatter;
        }

        const string ExplosionGuid = "c620190dd56b9b64badf8501cd8939ef";    // FX_Explosion_ReactorCore
        const float PinRadius = 0.25f;
        // door twice the first speed; thruster moves (easing out, turning) five times; the drive lights within 20 deg
        const float OpenSeconds = 0.6f, EjectSpeed = 10f, ClearAccel = 8f, ClearSeconds = 1.5f, TurnRate = 450f, SteerRate = 60f, DriveCone = 20f;
        static readonly Regex HatchName = new Regex(@"^(?:A_|Fore_Section_)Torpedo_Tube_Hatch_(Top|Bottom)$", RegexOptions.IgnoreCase);
        static readonly Regex LoadedName = new Regex(@"^Torpedo_(Top|Bottom)_Tube$", RegexOptions.IgnoreCase);
        static readonly Regex TrayName = new Regex(@"^Torpedo_(Top|Bottom)_Stack(\d+)$", RegexOptions.IgnoreCase);

        static readonly Dictionary<string, Tube> sTubes = new Dictionary<string, Tube>();
        static readonly List<Torp> sFlying = new List<Torp>();
        static readonly RaycastHit[] sHits = new RaycastHit[32];
        static readonly Collider[] sNear = new Collider[256];
        static bool sPressed;
        static Material? sPlumeMaterial, sGlowMaterial, sPuffMaterial, sHingeMaterial, sArmMaterial;
        static GameObject? sExplosionPrefab;
        static bool sExplosionLoading;

        internal static Vector3? LastLock { get; private set; }

        // Each tube, top first: how many it held and how many are left (the HUD's two rows).
        internal static bool HasTubes => sTubes.Count > 0;

        // Torpedoes left in all tubes and trays.
        internal static int Left
        {
            get
            {
                int n = 0;
                foreach (var tube in sTubes.Values) n += tube.Count;
                return n;
            }
        }

        internal static List<(int capacity, int left)> Rows
        {
            get
            {
                var rows = new List<(int, int)>();
                foreach (var side in new[] { "Top", "Bottom" })
                    if (sTubes.TryGetValue(side, out var tube)) rows.Add((tube.Capacity, tube.Count));
                return rows;
            }
        }

        internal static string Status
        {
            get
            {
                if (sTubes.Count == 0) return "NO TUBES";
                var parts = new List<string>();
                foreach (var tube in sTubes.Values) parts.Add($"{(tube.Side == "Top" ? "TOP" : "BTM")} {tube.Count}");
                return string.Join("  ", parts);
            }
        }

        internal static void Reset()
        {
            foreach (var t in sFlying) if (t.Visual != null) Object.Destroy(t.Visual);
            foreach (var tube in sTubes.Values) if (tube.HingeLine != null) Object.Destroy(tube.HingeLine.transform.parent.gameObject);
            sFlying.Clear();
            sTubes.Clear();
            sPressed = false;
            LastLock = null;
        }

        // The tubes only (found again after the ship's structure was rebuilt); torpedoes in flight fly on.
        internal static void ResetTubes()
        {
            foreach (var tube in sTubes.Values) if (tube.HingeLine != null) Object.Destroy(tube.HingeLine.transform.parent.gameObject);
            foreach (var t in sFlying) t.Tube.Busy = false;
            sTubes.Clear();
        }

        // ---- the tubes, from WeaponMounts' scan ---------------------------------------------------------------------

        internal static void Note(Transform t)
        {
            var m = HatchName.Match(t.name);
            if (m.Success) { Get(m.Groups[1].Value).Hatch = t; return; }
            m = LoadedName.Match(t.name);
            if (m.Success) { Get(m.Groups[1].Value).Loaded = t; return; }
            m = TrayName.Match(t.name);
            if (m.Success) Get(m.Groups[1].Value).Tray.Add(t);
        }

        static Tube Get(string side)
        {
            side = char.ToUpperInvariant(side[0]) + side.Substring(1).ToLowerInvariant();
            if (!sTubes.TryGetValue(side, out var tube)) sTubes[side] = tube = new Tube { Side = side };
            return tube;
        }

        // After the scan: each tube's launch direction and door hinge.
        internal static void Setup()
        {
            var ship = FlightModel.Frame;
            foreach (var side in new List<string>(sTubes.Keys))
            {
                var tube = sTubes[side];
                if (tube.Hatch == null || tube.Hatch.parent == null || (tube.Loaded == null && tube.Tray.Count == 0) || ship == null)
                {
                    Plugin.Log.LogWarning($"[Weapons] torpedo tube {side}: {(tube.Hatch == null ? "no hatch" : tube.Loaded == null && tube.Tray.Count == 0 ? "no torpedoes" : "no ship frame")}; not used.");
                    sTubes.Remove(side);
                    continue;
                }
                tube.Tray.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                tube.Frame = tube.Hatch.parent;
                var sample = tube.Loaded != null ? tube.Loaded : tube.Tray[0];
                // launch: along the torpedo's long axis, the way the ship's nose points
                var launch = LongAxis(sample, out _, out _);
                if (Vector3.Dot(launch, ship.forward) < 0f) launch = -launch;
                tube.LaunchLocal = tube.Frame.InverseTransformDirection(launch);
                tube.Capacity = tube.Count;
                // the door's hinge (from the model): a pin across each end of its long side, along the door's own x
                // (red, TorpedoHatchPin); it turns on the pin farther from the ship's middle, TorpedoHatchAngle,
                // swinging out of the ship: the way its face (its third axis) points away from the ship's long axis.
                // All from the door's own axes, so each door gets its own (they are turned differently in the model).
                var bounds = WorldBox(tube.Hatch, out var center);
                HingeEnd(tube.Hatch, center, bounds, ship, out var endDir, out var axis, out var outward, out string axes);
                float reach = Extent(bounds, tube.Hatch, endDir);
                var hinge = center + endDir * Mathf.Max(0f, reach - PinRadius);
                // the pin itself, measured from the mesh: the knuckles at the hinge end are its outermost part; the pin
                // runs through their middle, one radius in from the end (the box middle is not on the pin)
                string measured = "box estimate (mesh not readable)";
                if (PinFromMesh(tube.Hatch, center, endDir, outward, out var pinPoint, out float pinRadius, out float depth))
                {
                    hinge = pinPoint;
                    measured = $"measured on the mesh: knuckle radius {pinRadius:0.00} m, {depth:+0.00;-0.00} m off the box middle toward its face";
                }
                axes += "; pin " + measured;
                tube.HingeAxis = tube.Frame.InverseTransformDirection(axis);
                tube.OutwardLocal = tube.Frame.InverseTransformDirection(outward);
                tube.HingePoint = tube.Frame.InverseTransformPoint(hinge);
                tube.MiddleLocal = tube.Frame.InverseTransformPoint(center);
                tube.PinHalf = Extent(bounds, tube.Hatch, axis);
                tube.HatchPos = tube.Frame.InverseTransformPoint(tube.Hatch.position);
                tube.HatchRot = Quaternion.Inverse(tube.Frame.rotation) * tube.Hatch.rotation;
                // which way round swings it out of the ship: halfway, its middle must be farther out than it was
                var mid = center - hinge;
                tube.OpenSign = Vector3.Dot(Quaternion.AngleAxis(90f, axis) * mid, outward) >= Vector3.Dot(Quaternion.AngleAxis(-90f, axis) * mid, outward) ? 1f : -1f;
                Plugin.Log.LogInfo($"[Weapons] torpedo tube {side}: hatch '{tube.Hatch.name}', {tube.Count} torpedo(es) ('{(tube.Loaded != null ? tube.Loaded.name : "-")}' + " +
                                   $"{tube.Tray.Count} in the tray), launch {Describe(launch, ship)}, door faces {Describe(outward, ship)}, {axes}, hinge axis {Describe(axis, ship)} " +
                                   $"at {hinge} ({Vector3.Distance(hinge, center):0.0} m from the door's middle {center}), opens {(tube.OpenSign > 0f ? "+" : "-")}{Plugin.WeaponsTorpedoHatchAngle.Value:0} deg.");
            }
        }

        // ---- input and launching ------------------------------------------------------------------------------------

        // Every frame while seated (from Weapons.Tick).
        internal static void ReadInput()
        {
            var pad = InputManager.ActiveDevice;
            bool down = Weapons.Allowed && Weapons.Mode == WeaponMode.Torpedo && !Weapons.Looking
                        && (Input.GetMouseButton(0) || (pad != null && pad.RightTrigger.Value > 0.5f));
            if (down && !sPressed) Launch();
            sPressed = down;
        }

        static void Launch()
        {
            if (sTubes.Count == 0) { Plugin.Log.LogInfo("[Weapons] no torpedo tubes on this ship."); return; }
            // the tube on the target's side of the ship (its door facing the target most), else the other
            var tubes = new List<Tube>(sTubes.Values);
            var target = Pdc.AimPoint;
            tubes.Sort((a, b) => Facing(b, target).CompareTo(Facing(a, target)));
            Tube? tube = tubes.Find(t => !t.Busy && t.Count > 0);
            if (tube == null) { Plugin.Log.LogInfo("[Weapons] no torpedo ready (tubes busy or empty)."); return; }
            var source = tube.Loaded != null && tube.Loaded.gameObject.activeInHierarchy ? tube.Loaded : null;
            if (source == null) foreach (var t in tube.Tray) if (t != null && t.gameObject.activeInHierarchy) { source = t; break; }
            if (source == null) return;

            // the drawn copy, at the tube's torpedo
            var torp = new Torp { Tube = tube, Lock = Pdc.AimPoint };
            torp.Visual = Copy(source, out torp.Length);
            torp.Rotation = source.rotation;
            torp.Position = source.position;
            torp.LocalStart = tube.Frame.InverseTransformPoint(source.position);
            torp.LocalRotation = Quaternion.Inverse(tube.Frame.rotation) * source.rotation;
            torp.NoseLocal = Quaternion.Inverse(source.rotation) * tube.Frame.TransformDirection(tube.LaunchLocal);
            torp.Warhead = ReadBlast(source);
            MakeEffects(torp);

            // the real one goes: the last in the tray, else the one in the tube
            Transform? spent = null;
            for (int i = tube.Tray.Count - 1; i >= 0; i--)
                if (tube.Tray[i] != null && tube.Tray[i].gameObject.activeInHierarchy) { spent = tube.Tray[i]; break; }
            if (spent == null) spent = tube.Loaded;
            if (spent != null) Remove(spent);

            tube.Busy = true;
            sFlying.Add(torp);
            LastLock = torp.Lock;
            Plugin.Log.LogInfo($"[Weapons] TORPEDO from the {tube.Side} tube, locked on {torp.Lock} ({Vector3.Distance(torp.Position, torp.Lock):0} m), " +
                               $"aimed at {(Pdc.AimHit ? $"'{Pdc.AimName}'" : "nothing (a point at range)")}, warhead {DescribeBlast(torp.Warhead)}; " +
                               $"removed '{(spent != null ? spent.name : "-")}', {tube.Count} left in the tube.");
        }

        static float Facing(Tube tube, Vector3 target) =>
            Vector3.Dot(tube.Frame.TransformDirection(tube.OutwardLocal), (target - tube.Hatch.position).normalized);

        static void Remove(Transform torpedo)
        {
            // taken out of sight, quietly: disarmed (with its explosive settings it would detonate) and switched off,
            // not vaporized (that plays the game's "salvage destroyed" sound and flash). Its entity stays, so it still
            // counts in the ship's salvage total.
            var parts = torpedo.GetComponentsInChildren<StructurePart>(true);
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null)
            {
                var em = world.EntityManager;
                foreach (var part in parts)
                {
                    Disarm(em, part.Entity);
                    if (part.Group != null && part.Group.TryGetEntity(out _, out var groupEntity)) Disarm(em, groupEntity);
                }
            }
            torpedo.gameObject.SetActive(false);
        }

        static string DescribeBlast(Blast? blast)
        {
            if (!blast.HasValue) return "none (effect and heat instead)";
            var b = blast.Value;
            var g = b.Explosive.FXPrefabGuid;
            return $"its own: {b.Explosive.ExplosiveType}, reach {b.Explosive.ElementalBounds:0.#} m, strength {b.Explosive.ElementalStrength:0.#}, " +
                   $"effect {g.x:x8}{g.y:x8}{g.z:x8}{g.w:x8} x{b.Explosive.MaxFXScale:0.##}" +
                   (b.HasDamage ? $", part damage {b.Damage.ExplosionDamage:0.#}, player {b.Damage.PlayerExplosionDamage:0.#} within {b.Damage.PlayerDamageRadius:0.#} m" : "") +
                   (b.HasForce ? $", force {b.Force.ExplosionForce:0.#}" : "") + (b.HasShatter ? ", shatters" : "");
        }

        static void Disarm(EntityManager em, Entity e)
        {
            if (e == Entity.Null || !em.Exists(e)) return;
            if (em.HasComponent<ExplosiveComponent>(e)) em.RemoveComponent<ExplosiveComponent>(e);
            if (em.HasComponent<ExplosiveDamageSettings>(e)) em.RemoveComponent<ExplosiveDamageSettings>(e);
            if (em.HasComponent<ExplosiveForceSettings>(e)) em.RemoveComponent<ExplosiveForceSettings>(e);
            if (em.HasComponent<ExplosiveShatterSettings>(e)) em.RemoveComponent<ExplosiveShatterSettings>(e);
        }

        // ---- every frame: doors and torpedoes -----------------------------------------------------------------------

        internal static void Frame()
        {
            float dt = Time.deltaTime;
            // the doors stand open while torpedo mode is selected (on tubes with torpedoes left), and while one is
            // leaving; they close otherwise
            bool armed = Weapons.Allowed && Weapons.Mode == WeaponMode.Torpedo;
            foreach (var tube in sTubes.Values)
            {
                tube.OpenTarget = tube.Busy || (armed && tube.Count > 0) ? 1f : 0f;
                MoveDoor(tube, dt);
                ShowHinge(tube, armed && Plugin.WeaponsTorpedoShowHinges.Value);
            }
            for (int i = sFlying.Count - 1; i >= 0; i--)
            {
                var t = sFlying[i];
                if (t.Visual == null) { sFlying.RemoveAt(i); continue; }
                if (dt > 0f && Fly(t, dt)) { Detonate(t, t.Position); sFlying.RemoveAt(i); continue; }
                t.Visual.transform.SetPositionAndRotation(t.Position, t.Rotation);
                DrawEffects(t);
            }
        }

        // In torpedo mode with TorpedoShowHinges on: the hinge the door turns on drawn in green (along the pin, past
        // both sides of the door), and a yellow arm from it to the door's middle (it swings round with the door).
        static void ShowHinge(Tube tube, bool show)
        {
            if (tube.Frame == null) return;
            if (!show)
            {
                if (tube.HingeLine != null) tube.HingeLine.enabled = tube.ArmLine!.enabled = false;
                return;
            }
            if (tube.HingeLine == null)
            {
                sHingeMaterial ??= DrivePlume.AdditiveMaterial(Spot(new Color(0.2f, 3f, 0.4f)));
                sArmMaterial ??= DrivePlume.AdditiveMaterial(Spot(new Color(3f, 2.4f, 0.2f)));
                var holder = new GameObject("TheExpanse_TorpedoHinge_" + tube.Side).transform;
                tube.HingeLine = Line(holder, "Hinge", sHingeMaterial);
                tube.ArmLine = Line(holder, "Arm", sArmMaterial);
                tube.HingeLine.textureMode = tube.ArmLine.textureMode = LineTextureMode.Tile;
            }
            var hinge = tube.Frame.TransformPoint(tube.HingePoint);
            var axis = tube.Frame.TransformDirection(tube.HingeAxis).normalized;
            var q = Quaternion.AngleAxis(tube.OpenSign * Plugin.WeaponsTorpedoHatchAngle.Value * Mathf.SmoothStep(0f, 1f, tube.Open), tube.HingeAxis);
            var middle = tube.Frame.TransformPoint(tube.HingePoint + q * (tube.MiddleLocal - tube.HingePoint));
            tube.HingeLine.SetPosition(0, hinge - axis * (tube.PinHalf + 1f));
            tube.HingeLine.SetPosition(1, hinge + axis * (tube.PinHalf + 1f));
            tube.HingeLine.startWidth = tube.HingeLine.endWidth = 0.25f;
            tube.ArmLine!.SetPosition(0, hinge);
            tube.ArmLine.SetPosition(1, middle);
            tube.ArmLine.startWidth = tube.ArmLine.endWidth = 0.15f;
            tube.HingeLine.enabled = tube.ArmLine.enabled = true;
        }

        static void MoveDoor(Tube tube, float dt)
        {
            if (tube.Hatch == null || tube.Frame == null) return;
            if (Mathf.Approximately(tube.Open, tube.OpenTarget) && tube.Open == 0f) return;
            tube.Open = Mathf.MoveTowards(tube.Open, tube.OpenTarget, dt / OpenSeconds);
            float eased = Mathf.SmoothStep(0f, 1f, tube.Open);
            var q = Quaternion.AngleAxis(tube.OpenSign * Plugin.WeaponsTorpedoHatchAngle.Value * eased, tube.HingeAxis);
            var pos = tube.HingePoint + q * (tube.HatchPos - tube.HingePoint);
            tube.Hatch.SetPositionAndRotation(tube.Frame.TransformPoint(pos), tube.Frame.rotation * (q * tube.HatchRot));
        }

        // One step of a torpedo's flight; true when it detonates.
        static bool Fly(Torp t, float dt)
        {
            t.Age += dt;
            t.PhaseTime += dt;
            var tube = t.Tube;
            switch (t.Phase)
            {
                case Phase.Opening:     // in the tube, riding the ship, while the door opens
                    Ride(t, 0f);
                    if (tube.Open >= 0.98f) Next(t, Phase.Eject);
                    return false;
                case Phase.Eject:       // eased out of the tube on its thrusters, still riding the ship
                {
                    float out_ = EjectSpeed * t.PhaseTime;
                    Ride(t, out_);
                    if (out_ >= t.Length + 1.5f)
                    {
                        t.Velocity = FlightModel.PointVelocity(t.Position) + t.Nose * EjectSpeed;
                        Next(t, Phase.Clear);
                    }
                    return false;
                }
                case Phase.Clear:       // a short burn to clear the ship; the door closes behind it
                    t.Velocity += t.Nose * ClearAccel * dt;
                    if (t.PhaseTime >= ClearSeconds) { Next(t, Phase.Turn); tube.Busy = false; }
                    break;
                case Phase.Turn:        // on its thrusters, nose to the locked point
                {
                    var aim = Steer(t);
                    var want = Quaternion.FromToRotation(t.Nose, (aim - t.Position).normalized) * t.Rotation;
                    t.Rotation = Quaternion.RotateTowards(t.Rotation, want, TurnRate * dt);
                    if (Vector3.Angle(t.Nose, aim - t.Position) < DriveCone) { Next(t, Phase.Drive); t.Speed = 0f; }
                    break;
                }
                case Phase.Drive:       // the drive: faster and faster, steering onto the point
                {
                    var want = Quaternion.FromToRotation(t.Nose, (Steer(t) - t.Position).normalized) * t.Rotation;
                    t.Rotation = Quaternion.RotateTowards(t.Rotation, want, SteerRate * dt);
                    float accel = Mathf.Max(1f, Plugin.WeaponsTorpedoAcceleration.Value);
                    float top = Mathf.Max(10f, Plugin.WeaponsTorpedoSpeed.Value);
                    // the drive turns the drift onto the nose
                    var along = t.Nose * Vector3.Dot(t.Velocity, t.Nose);
                    var across = t.Velocity - along;
                    across = Vector3.MoveTowards(across, Vector3.zero, accel * 0.5f * dt);
                    float speed = Mathf.Min(top, along.magnitude * Mathf.Sign(Vector3.Dot(t.Velocity, t.Nose)) + accel * dt);
                    t.Velocity = t.Nose * speed + across;
                    break;
                }
            }
            // free flight: move, and meet whatever is in the way (not the own ship)
            var step = t.Velocity * dt;
            float length = step.magnitude;
            if (length > 1e-4f)
            {
                int n = Physics.SphereCastNonAlloc(t.Position, 0.8f, step / length, sHits, length, ~0, QueryTriggerInteraction.Ignore);
                float nearest = float.MaxValue;
                Vector3? at = null;
                for (int i = 0; i < n; i++)
                {
                    var h = sHits[i];
                    if (h.collider == null || h.distance >= nearest) continue;
                    if (FlightSeat.PlayerCollider != null && h.collider == FlightSeat.PlayerCollider) continue;
                    var part = h.collider.GetComponentInParent<StructurePart>();
                    if (part != null && FlightModel.IsShipPart(part) && FlightModel.IsMainShipPart(part)) continue;
                    if (h.distance <= 0f && h.point == Vector3.zero) continue;      // started inside: not a hit
                    nearest = h.distance; at = h.point;
                }
                if (at.HasValue) { t.Position = at.Value; return true; }
            }
            t.Position += step;
            if (t.Phase == Phase.Drive && (Vector3.Distance(t.Position, t.Lock) < Mathf.Max(3f, length))) { t.Position = t.Lock; return true; }
            return t.Age > Mathf.Max(5f, Plugin.WeaponsTorpedoLife.Value);
        }

        // Where to head: the locked point, unless the straight way there passes through the own ship (a lock behind
        // it): then a point beside the hull, out from its long axis on the side the torpedo is on, so it arcs round.
        static Vector3 Steer(Torp t)
        {
            var frame = FlightModel.Frame;
            if (frame == null) return t.Lock;
            var a = frame.position - frame.forward * FlightModel.HalfLength;
            var b = frame.position + frame.forward * FlightModel.HalfLength;
            float clear = FlightModel.HalfWidth + 15f;
            float best = float.MaxValue;
            Vector3 onLine = t.Position, onAxis = frame.position;
            // only the way ahead of the torpedo, and only beside the hull's length (not off its ends): a torpedo just
            // out of the tube at the nose, heading on forward, is not "through the ship"
            var ab = b - a;
            for (int i = 2; i <= 24; i++)
            {
                var p = Vector3.Lerp(t.Position, t.Lock, i / 24f);
                float along = Vector3.Dot(p - a, ab) / ab.sqrMagnitude;
                if (along <= 0f || along >= 1f) continue;
                var q = a + ab * along;
                float d = (p - q).sqrMagnitude;
                if (d < best) { best = d; onLine = p; onAxis = q; }
            }
            if (best >= clear * clear) return t.Lock;
            var outward = onLine - onAxis;
            outward -= Vector3.Project(outward, frame.forward);
            if (outward.sqrMagnitude < 0.01f)
            {
                outward = t.Position - Closest(a, b, t.Position);
                outward -= Vector3.Project(outward, frame.forward);
            }
            if (outward.sqrMagnitude < 0.01f) outward = frame.up;
            return onAxis + outward.normalized * (clear + 10f);
        }

        static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
        {
            var ab = b - a;
            return a + ab * Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
        }

        // In the tube (moving with the ship), pushed out along the launch by a distance.
        static void Ride(Torp t, float distance)
        {
            var frame = t.Tube.Frame;
            t.Position = frame.TransformPoint(t.LocalStart + t.Tube.LaunchLocal.normalized * distance);
            t.Rotation = frame.rotation * t.LocalRotation;
        }

        static void Next(Torp t, Phase phase)
        {
            t.Phase = phase;
            t.PhaseTime = 0f;
        }

        // ---- the explosion --------------------------------------------------------------------------------------

        // The torpedo's own explosive settings, from any of its parts (its warhead), read before it is removed.
        static Blast? ReadBlast(Transform torpedo)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return null;
            var em = world.EntityManager;
            foreach (var part in torpedo.GetComponentsInChildren<StructurePart>(true))
            {
                var e = part.Entity;
                if (e == Entity.Null || !em.Exists(e) || !em.HasComponent<ExplosiveComponent>(e)) continue;
                var b = new Blast { Explosive = em.GetComponentData<ExplosiveComponent>(e) };
                if (b.HasDamage = em.HasComponent<ExplosiveDamageSettings>(e)) b.Damage = em.GetComponentData<ExplosiveDamageSettings>(e);
                if (b.HasForce = em.HasComponent<ExplosiveForceSettings>(e)) b.Force = em.GetComponentData<ExplosiveForceSettings>(e);
                if (b.HasShatter = em.HasComponent<ExplosiveShatterSettings>(e)) b.Shatter = em.GetComponentData<ExplosiveShatterSettings>(e);
                return b;
            }
            return null;
        }

        static void Detonate(Torp t, Vector3 at)
        {
            float scale = Mathf.Max(0.1f, Plugin.WeaponsTorpedoBlast.Value);
            bool real = false;
            var world = World.DefaultGameObjectInjectionWorld;
            if (t.Warhead.HasValue && world != null)
            {
                try
                {
                    var em = world.EntityManager;
                    var b = t.Warhead.Value;
                    var explosive = b.Explosive;
                    explosive.ElementalBounds *= scale;
                    explosive.MaxFXScale *= scale;
                    var blast = em.CreateEntity();
                    em.AddComponentData(blast, explosive);
                    if (b.HasDamage) { var d = b.Damage; d.PlayerDamageRadius *= scale; em.AddComponentData(blast, d); }
                    if (b.HasForce) { var f = b.Force; f.PlayerForceRadius *= scale; em.AddComponentData(blast, f); }
                    if (b.HasShatter) em.AddComponentData(blast, b.Shatter);
                    em.AddComponentData(blast, new Unity.Transforms.LocalToWorld
                    {
                        Value = Unity.Mathematics.float4x4.TRS(new Unity.Mathematics.float3(at.x, at.y, at.z), Unity.Mathematics.quaternion.identity, new Unity.Mathematics.float3(1f, 1f, 1f))
                    });
                    em.AddComponentData(blast, new ExplodedComponent { Time = (float)world.Time.ElapsedTime });
                    real = true;
                }
                catch (System.Exception ex) { Plugin.Log.LogWarning("[Weapons] the torpedo's real explosion could not be set off: " + ex.Message); }
            }
            if (!real)
            {
                Boom(at, scale);
                float radius = 12f * scale;
                int n = Physics.OverlapSphereNonAlloc(at, radius, sNear, ~0, QueryTriggerInteraction.Ignore);
                var done = new HashSet<StructurePart>();
                for (int i = 0; i < n; i++)
                {
                    var part = sNear[i] != null ? sNear[i].GetComponentInParent<StructurePart>() : null;
                    if (part == null || !done.Add(part) || (FlightModel.IsShipPart(part) && FlightModel.IsMainShipPart(part))) continue;
                    try { ReactorFire.HeatPart(part, Mathf.Max(0f, Plugin.WeaponsTorpedoHeat.Value)); } catch { }
                    var body = sNear[i].attachedRigidbody;
                    if (body != null && !body.isKinematic) body.AddExplosionForce(Plugin.WeaponsTorpedoHeat.Value * 300f, at, radius, 0.5f, ForceMode.Impulse);
                }
            }
            // a station: what it strikes breaks outright (as the rail gun's slug), the rest around takes the blast
            int struck = Physics.OverlapSphereNonAlloc(at, 6f, sNear, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < struck; i++) StationDamage.Pierce(sNear[i], at, t.Nose);
            StationDamage.Blast(at, Plugin.StationTorpedoRadius.Value * scale, Plugin.StationTorpedoDamage.Value);
            Plugin.Log.LogInfo($"[Weapons] torpedo detonated at {at} after {t.Age:0.0} s ({t.Phase}), {Vector3.Distance(at, t.Lock):0.0} m from its lock, " +
                               $"{(real ? "a real explosion from its warhead" : "explosion effect and heat")}.");
            Object.Destroy(t.Visual);
        }

        static void Boom(Vector3 at, float scale)
        {
            if (sExplosionPrefab != null) { Spawn(at, scale); return; }
            if (sExplosionLoading) return;
            sExplosionLoading = true;
            Addressables.LoadAssetAsync<GameObject>(ExplosionGuid).Completed += handle =>
            {
                sExplosionLoading = false;
                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null) return;
                sExplosionPrefab = handle.Result;
                Spawn(at, scale);
            };
        }

        static void Spawn(Vector3 at, float scale)
        {
            var boom = Object.Instantiate(sExplosionPrefab!, at, Quaternion.identity);
            boom.name = "TheExpanse_TorpedoBlast";
            boom.transform.localScale = Vector3.one * scale;
            boom.SetActive(true);
            foreach (var effect in boom.GetComponentsInChildren<VisualEffect>(true))
            { effect.enabled = true; effect.Reinit(); effect.Play(); effect.SendEvent("Start"); effect.SendEvent("OnPlay"); }
            foreach (var system in boom.GetComponentsInChildren<ParticleSystem>(true)) system.Play(true);
            Object.Destroy(boom, 10f);
        }

        // ---- the drawn copy and its effects ---------------------------------------------------------------------

        static GameObject Copy(Transform source, out float length)
        {
            var root = new GameObject("TheExpanse_Torpedo");
            root.transform.SetPositionAndRotation(source.position, source.rotation);
            var box = new Bounds();
            bool any = false;
            foreach (var filter in source.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (filter.sharedMesh == null || renderer == null) continue;
                var go = new GameObject(filter.name);
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = Quaternion.Inverse(source.rotation) * (filter.transform.position - source.position);
                go.transform.localRotation = Quaternion.Inverse(source.rotation) * filter.transform.rotation;
                go.transform.localScale = filter.transform.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var copy = go.AddComponent<MeshRenderer>();
                copy.sharedMaterials = renderer.sharedMaterials;
                copy.shadowCastingMode = ShadowCastingMode.On;
                var b = renderer.bounds;
                if (!any) { box = b; any = true; } else box.Encapsulate(b);
            }
            LongAxis(source, out _, out length);
            if (length <= 0f) length = any ? box.size.magnitude : 4f;
            return root;
        }

        static void MakeEffects(Torp t)
        {
            if (sPlumeMaterial == null)
            {
                sPlumeMaterial = DrivePlume.AdditiveMaterial(Streak(new Color(0.5f, 0.9f, 3.5f), 12f));
                sGlowMaterial = DrivePlume.AdditiveMaterial(Spot(new Color(1.5f, 2.2f, 4f)));
                sPuffMaterial = DrivePlume.AdditiveMaterial(Streak(new Color(1.2f, 1.2f, 1.3f), 8f));
            }
            t.Plume = Line(t.Visual.transform, "Plume", sPlumeMaterial);
            t.Glow = Line(t.Visual.transform, "Glow", sGlowMaterial!);
            for (int i = 0; i < 4; i++) t.Puffs.Add(Line(t.Visual.transform, "Puff", sPuffMaterial!));
        }

        static LineRenderer Line(Transform parent, string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.sharedMaterial = material;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            return line;
        }

        static void DrawEffects(Torp t)
        {
            var nose = t.Nose;
            var tail = t.Position - nose * t.Length * 0.5f;
            bool drive = t.Phase == Phase.Drive;
            float flicker = 0.85f + 0.15f * Mathf.PerlinNoise(Time.time * 20f, t.Age);
            t.Plume.enabled = t.Glow.enabled = drive || t.Phase == Phase.Clear;
            if (t.Plume.enabled)
            {
                float power = drive ? 1f : 0.35f;
                float len = (drive ? 14f : 4f) * flicker;
                t.Plume.SetPosition(0, tail - nose * len);
                t.Plume.SetPosition(1, tail);
                t.Plume.startWidth = 0.2f * power; t.Plume.endWidth = 1.1f * power;
                t.Glow.SetPosition(0, tail - nose * 0.6f);
                t.Glow.SetPosition(1, tail + nose * 0.6f);
                t.Glow.startWidth = t.Glow.endWidth = 2.6f * power * flicker;
            }
            // thruster puffs while easing out and turning: short white jets off the nose and tail
            bool puffs = t.Phase == Phase.Eject || t.Phase == Phase.Turn || (t.Phase == Phase.Clear && t.PhaseTime < 0.4f);
            var side = Vector3.Cross(nose, t.Rotation * Vector3.up).normalized;
            if (side.sqrMagnitude < 0.1f) side = Vector3.Cross(nose, Vector3.right).normalized;
            var up = Vector3.Cross(side, nose);
            for (int i = 0; i < t.Puffs.Count; i++)
            {
                var p = t.Puffs[i];
                bool on = puffs && Random.value < 0.6f;
                p.enabled = on;
                if (!on) continue;
                var at = t.Position + nose * (i < 2 ? 0.4f : -0.4f) * t.Length;
                var dir = t.Phase == Phase.Eject ? -nose : (i % 2 == 0 ? side : -side) * (i < 2 ? 1f : -1f) + up * 0.2f;
                p.SetPosition(0, at);
                p.SetPosition(1, at + dir.normalized * Random.Range(0.8f, 1.6f));
                p.startWidth = 0.35f; p.endWidth = 0.05f;
            }
        }

        static Texture2D Streak(Color color, float sharpness)
        {
            const int W = 64, H = 16;
            var tex = new Texture2D(W, H, TextureFormat.RGBAHalf, false) { wrapMode = TextureWrapMode.Clamp, name = "TheExpanse_TorpedoStreak" };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W, v = (y + 0.5f) / H * 2f - 1f;
                    float a = Mathf.Pow(u, 1.2f) * Mathf.Exp(-v * v * sharpness * 0.4f);
                    float core = Mathf.Pow(u, 2f) * Mathf.Exp(-v * v * sharpness * 3f) * 3f;
                    px[y * W + x] = new Color(color.r * a + core, color.g * a + core, color.b * a + core, Mathf.Min(1f, a + core));
                }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        static Texture2D Spot(Color color)
        {
            const int N = 32;
            var tex = new Texture2D(N, N, TextureFormat.RGBAHalf, false) { wrapMode = TextureWrapMode.Clamp, name = "TheExpanse_TorpedoGlow" };
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float a = Mathf.Exp(-(u * u + v * v) * 4f);
                    px[y * N + x] = new Color(color.r * a, color.g * a, color.b * a, a);
                }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        // ---- geometry ---------------------------------------------------------------------------------------------

        // The longest side of a piece (its mesh, else the box of all its meshes): direction in world space and length.
        static Vector3 LongAxis(Transform t, out Vector3 center, out float length)
        {
            center = t.position; length = 0f;
            var filter = t.GetComponent<MeshFilter>();
            Bounds b;
            Transform space = t;
            if (filter != null && filter.sharedMesh != null) b = filter.sharedMesh.bounds;
            else
            {
                // the box of its meshes in its own axes
                b = new Bounds();
                bool any = false;
                foreach (var f in t.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (f.sharedMesh == null) continue;
                    var mb = f.sharedMesh.bounds;
                    foreach (var corner in Corners(mb))
                    {
                        var p = t.InverseTransformPoint(f.transform.TransformPoint(corner));
                        if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                    }
                }
                if (!any) return t.forward;
            }
            var size = Vector3.Scale(b.size, space.lossyScale);
            int k = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            center = space.TransformPoint(b.center);
            length = k == 0 ? size.x : k == 1 ? size.y : size.z;
            return (k == 0 ? space.right : k == 1 ? space.up : space.forward).normalized;
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        }

        // A piece's mesh box (own axes) and its middle in world space.
        static Bounds WorldBox(Transform t, out Vector3 center)
        {
            var filter = t.GetComponent<MeshFilter>();
            var b = filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            center = t.TransformPoint(b.center);
            return b;
        }

        // The door's hinge, from its own three axes: the pin (TorpedoHatchPin, its x from the model), its length (the
        // longer of the other two), its face (the third, signed to point away from the ship's long axis). The pin used
        // is at the end of the length farther from the ship's middle.
        static void HingeEnd(Transform hatch, Vector3 center, Bounds bounds, Transform ship,
                             out Vector3 endDir, out Vector3 axis, out Vector3 outward, out string axes)
        {
            var e = Vector3.Scale(bounds.extents, hatch.lossyScale);
            var dirs = new[] { hatch.right, hatch.up, hatch.forward };
            var names = new[] { "x", "y", "z" };
            float[] ext = { e.x, e.y, e.z };
            string p = (Plugin.WeaponsTorpedoHatchPin.Value ?? "x").Trim().ToLowerInvariant();
            int pin = p.EndsWith("y") ? 1 : p.EndsWith("z") ? 2 : 0;
            int a = (pin + 1) % 3, b = (pin + 2) % 3;
            int len = ext[a] >= ext[b] ? a : b;
            int face = 3 - pin - len;
            var along = dirs[len];
            endDir = (center + along * ext[len] - ship.position).sqrMagnitude >= (center - along * ext[len] - ship.position).sqrMagnitude ? along : -along;
            axis = dirs[pin];
            // out of the ship: the doors cover the tube mouths, which face the way the torpedoes leave (the ship's nose)
            outward = Vector3.Dot(dirs[face], ship.forward) >= 0f ? dirs[face] : -dirs[face];
            axes = $"pin its {names[pin]}, length its {names[len]} ({ext[len] * 2f:0.0} m), face its {(Vector3.Dot(outward, dirs[face]) > 0f ? "+" : "-")}{names[face]}, " +
                   $"hinge at its {(Vector3.Dot(endDir, along) > 0f ? "+" : "-")}{names[len]} end";
        }

        // The hinge pin's middle from the door's vertices: the ones within 0.6 m of its hinge end are the knuckles;
        // their middle along the face is the pin's depth, half their spread along the face its radius, and the pin
        // sits one radius in from the end. False when the mesh can't be read.
        static bool PinFromMesh(Transform hatch, Vector3 center, Vector3 endDir, Vector3 face, out Vector3 pin, out float radius, out float depth)
        {
            pin = center; radius = 0f; depth = 0f;
            var filter = hatch.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) return false;
            var verts = mesh.vertices;
            if (verts.Length == 0) return false;
            float lmax = float.MinValue;
            var world = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                world[i] = hatch.TransformPoint(verts[i]);
                lmax = Mathf.Max(lmax, Vector3.Dot(world[i] - center, endDir));
            }
            float fmin = float.MaxValue, fmax = float.MinValue;
            foreach (var p in world)
            {
                if (Vector3.Dot(p - center, endDir) < lmax - 0.6f) continue;
                float f = Vector3.Dot(p - center, face);
                fmin = Mathf.Min(fmin, f); fmax = Mathf.Max(fmax, f);
            }
            if (fmin > fmax) return false;
            depth = (fmin + fmax) * 0.5f;
            radius = Mathf.Clamp((fmax - fmin) * 0.5f, 0.05f, 0.6f);
            pin = center + endDir * (lmax - radius) + face * depth;
            return true;
        }

        // How far a piece's box reaches from its middle along a world direction.
        static float Extent(Bounds b, Transform t, Vector3 dir)
        {
            var e = Vector3.Scale(b.extents, t.lossyScale);
            return Mathf.Abs(Vector3.Dot(t.right, dir)) * e.x + Mathf.Abs(Vector3.Dot(t.up, dir)) * e.y + Mathf.Abs(Vector3.Dot(t.forward, dir)) * e.z;
        }

        static string Describe(Vector3 dir, Transform frame)
        {
            float f = Vector3.Dot(dir, frame.forward), u = Vector3.Dot(dir, frame.up), r = Vector3.Dot(dir, frame.right);
            return $"({f:0.00} fwd, {u:0.00} up, {r:0.00} right)";
        }
    }
}
