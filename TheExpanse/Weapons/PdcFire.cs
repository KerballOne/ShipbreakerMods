using System.Collections.Generic;
using BBI.Unity.Game;
using InControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace TheExpanse
{
    // PDC fire: hold RT (or the left mouse button) in PDC mode. Each turret on target (bright on the bar: it sees the aim
    // point and its barrel is on it) fires PdcRate rounds a second from its barrel's tip.
    //   rounds:  carry the ship's own velocity at the muzzle plus PdcMuzzleSpeed along the barrel; moved each frame by a
    //            ray from where they were to where they are (outside the bay's physics area nothing collides, but rays
    //            still hit); they pass through the own ship and are gone past PdcRange;
    //   tracers: every third round draws a glowing streak; a short flash at the muzzle while firing;
    //   hits:    a part of anything not the own ship (a loose piece, another ship) takes PdcHeatPerRound of heat damage
    //            the game's way (as the reactor's fire); any hit makes a spark.
    // All drawn with the drive plume's additive material on LineRenderers facing the camera.
    internal static class PdcFire
    {
        sealed class Round
        {
            internal Vector3 Position, Velocity;
            internal float Travelled, Range;
            internal LineRenderer? Tracer;
        }

        sealed class Spark
        {
            internal LineRenderer Line = null!;
            internal float Until;
        }

        const int TracerEvery = 3, MaxRounds = 600;
        const float TracerLength = 45f, TracerWidth = 0.7f, FlashTime = 0.04f, SparkTime = 0.12f;

        static readonly List<Round> sRounds = new List<Round>();
        static readonly Stack<Round> sFreeRounds = new Stack<Round>();
        static readonly Stack<LineRenderer> sFreeTracers = new Stack<LineRenderer>();
        static readonly List<Spark> sSparks = new List<Spark>();
        static readonly Dictionary<Pdc.Turret, (LineRenderer flash, float due, int count)> sGuns = new Dictionary<Pdc.Turret, (LineRenderer, float, int)>();
        static readonly RaycastHit[] sHits = new RaycastHit[16];
        static Material? sTracerMaterial, sFlashMaterial, sSparkMaterial;
        static GameObject? sRoot;
        static bool sFiring, sWasFiring;
        static int sShots, sHitsCount, sPartHits;

        internal static bool Firing => sFiring;

        internal static void Reset()
        {
            if (sRoot != null) Object.Destroy(sRoot);
            sRoot = null;
            sRounds.Clear(); sFreeRounds.Clear(); sFreeTracers.Clear(); sSparks.Clear(); sGuns.Clear();
            sFiring = sWasFiring = false;
        }

        // Every frame while seated (from Weapons.Tick).
        internal static void ReadInput()
        {
            var pad = InputManager.ActiveDevice;
            sFiring = Weapons.Allowed && Weapons.Mode == WeaponMode.Pdc && !Weapons.Looking
                      && (Input.GetMouseButton(0) || (pad != null && pad.RightTrigger.Value > 0.5f));
        }

        // Every frame, LateUpdate after the turrets are placed.
        internal static void Frame()
        {
            bool firing = sFiring && Weapons.Allowed && Weapons.Mode == WeaponMode.Pdc;
            if (!firing) sFiring = false;
            if (firing != sWasFiring)
            {
                if (firing) { sShots = sHitsCount = sPartHits = 0; Plugin.Log.LogInfo($"[Weapons] PDCs firing ({Pdc.OnTarget} of {Pdc.Turrets.Count} on target)."); }
                else Plugin.Log.LogInfo($"[Weapons] PDCs ceased fire: {sShots} round(s), {sHitsCount} hit(s), {sPartHits} on parts.");
                sWasFiring = firing;
            }
            if (sRounds.Count == 0 && sSparks.Count == 0 && !firing && sGuns.Count == 0) return;
            if (!Ensure()) return;
            float dt = Time.deltaTime;
            Pdc.View(out var eye, out _);
            Shoot(firing, dt, eye);
            Fly(dt, eye);
            Fade();
        }

        static void Shoot(bool firing, float dt, Vector3 eye)
        {
            float interval = 1f / Mathf.Max(1f, Plugin.WeaponsPdcRate.Value);
            float speed = Mathf.Max(50f, Plugin.WeaponsPdcMuzzleSpeed.Value);
            float range = Mathf.Max(50f, Plugin.WeaponsPdcRange.Value);
            foreach (var t in Pdc.Turrets)
            {
                if (!sGuns.TryGetValue(t, out var gun)) { gun = (NewLine("PdcFlash", sFlashMaterial!), 0f, 0); sGuns[t] = gun; }
                bool shooting = firing && t.State == TurretState.OnTarget && dt > 0f;
                if (!shooting)
                {
                    if (gun.flash.enabled) gun.flash.enabled = false;
                    gun.due = 0f;
                    sGuns[t] = gun;
                    continue;
                }
                gun.due += dt;
                while (gun.due >= interval && sRounds.Count < MaxRounds)
                {
                    gun.due -= interval;
                    gun.count++;
                    // spread along this frame so a burst isn't bunched
                    float back = gun.due;
                    var round = sFreeRounds.Count > 0 ? sFreeRounds.Pop() : new Round();
                    round.Velocity = FlightModel.PointVelocity(t.MuzzleWorld) + t.PointingWorld * speed;
                    round.Position = t.MuzzleWorld + round.Velocity * back;
                    round.Travelled = 0f;
                    round.Range = range;
                    round.Tracer = gun.count % TracerEvery == 0 ? TakeTracer() : null;
                    sRounds.Add(round);
                    sShots++;
                }
                // the muzzle flash: on alternate frames, a short bright puff along the barrel
                bool flash = (Time.frameCount & 1) == 0;
                gun.flash.enabled = flash;
                if (flash)
                {
                    float w = Mathf.Max(1.2f, Vector3.Distance(eye, t.MuzzleWorld) * 0.004f);
                    gun.flash.SetPosition(0, t.MuzzleWorld - t.PointingWorld * 0.2f);
                    gun.flash.SetPosition(1, t.MuzzleWorld + t.PointingWorld * w * 1.5f);
                    gun.flash.startWidth = gun.flash.endWidth = w;
                }
                sGuns[t] = gun;
            }
        }

        static void Fly(float dt, Vector3 eye)
        {
            if (dt <= 0f) return;
            for (int i = sRounds.Count - 1; i >= 0; i--)
            {
                var r = sRounds[i];
                var step = r.Velocity * dt;
                float length = step.magnitude;
                bool done = false;
                if (length > 0f && Hit(r.Position, step / length, length, out var hit))
                {
                    OnHit(hit, r.Velocity);
                    done = true;
                }
                r.Position += step;
                r.Travelled += length;
                if (r.Travelled >= r.Range) done = true;
                if (done)
                {
                    if (r.Tracer != null) { r.Tracer.enabled = false; sFreeTracers.Push(r.Tracer); r.Tracer = null; }
                    sRounds.RemoveAt(i);
                    sFreeRounds.Push(r);
                    continue;
                }
                if (r.Tracer != null)
                {
                    var dir = r.Velocity.normalized;
                    float w = Mathf.Max(TracerWidth, Vector3.Distance(eye, r.Position) * 0.003f);
                    r.Tracer.SetPosition(0, r.Position - dir * Mathf.Min(TracerLength, r.Travelled + 1f));
                    r.Tracer.SetPosition(1, r.Position);
                    r.Tracer.startWidth = r.Tracer.endWidth = w;
                    r.Tracer.enabled = true;
                }
            }
        }

        // The first thing along a round's step that is not the own ship or the player.
        static bool Hit(Vector3 from, Vector3 dir, float length, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(from, dir, sHits, length, ~0, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = sHits[i];
                if (h.collider == null || h.distance >= nearest) continue;
                if (FlightSeat.PlayerCollider != null && h.collider == FlightSeat.PlayerCollider) continue;
                var part = h.collider.GetComponentInParent<StructurePart>();
                if (part != null && FlightModel.IsShipPart(part) && FlightModel.IsMainShipPart(part)) continue;   // the own ship
                nearest = h.distance; best = h; found = true;
            }
            return found;
        }

        static void OnHit(RaycastHit hit, Vector3 velocity)
        {
            sHitsCount++;
            var part = hit.collider.GetComponentInParent<StructurePart>();
            if (part != null)
            {
                sPartHits++;
                try { ReactorFire.HeatPart(part, Mathf.Max(0f, Plugin.WeaponsPdcHeatPerRound.Value)); }
                catch (System.Exception ex) { Plugin.Log.LogWarning("[Weapons] PDC hit damage failed: " + ex.Message); }
            }
            // a spark: a short bright streak bounced off the surface
            Spark? spark = null;
            foreach (var s in sSparks) if (!s.Line.enabled) { spark = s; break; }
            if (spark == null)
            {
                if (sSparks.Count >= 40) return;
                spark = new Spark { Line = NewLine("PdcSpark", sSparkMaterial!) };
                sSparks.Add(spark);
            }
            var bounce = Vector3.Reflect(velocity.normalized, hit.normal);
            float size = Random.Range(0.8f, 1.6f);
            spark.Line.SetPosition(0, hit.point);
            spark.Line.SetPosition(1, hit.point + (bounce + Random.insideUnitSphere * 0.5f).normalized * size * 2f);
            spark.Line.startWidth = size; spark.Line.endWidth = size * 0.3f;
            spark.Line.enabled = true;
            spark.Until = Time.time + SparkTime;
        }

        static void Fade()
        {
            foreach (var s in sSparks) if (s.Line.enabled && Time.time >= s.Until) s.Line.enabled = false;
        }

        // ---- drawing -----------------------------------------------------------------------------------------------

        static bool Ensure()
        {
            if (sRoot != null) return true;
            sRoot = new GameObject("TheExpanse_PdcFire");
            Object.DontDestroyOnLoad(sRoot);
            sTracerMaterial = DrivePlume.AdditiveMaterial(Paint(true, new Color(8f, 2.6f, 0.9f), core: 10f));
            sFlashMaterial = DrivePlume.AdditiveMaterial(Paint(false, new Color(3f, 2.1f, 1.2f)));
            sSparkMaterial = DrivePlume.AdditiveMaterial(Paint(true, new Color(3f, 1.6f, 0.6f)));
            return true;
        }

        static LineRenderer TakeTracer()
        {
            if (sFreeTracers.Count > 0) return sFreeTracers.Pop();
            return NewLine("PdcTracer", sTracerMaterial!);
        }

        static LineRenderer NewLine(string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(sRoot!.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.sharedMaterial = material;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.numCapVertices = 0;
            line.enabled = false;
            return line;
        }

        // streak: bright head at the end (u = 1) fading back to the tail, soft across; glow: a round soft spot.
        static Texture2D Paint(bool streak, Color color, float core = 0f)
        {
            const int W = 64, H = 16;
            int w = streak ? W : 32, h = streak ? H : 32;
            var tex = new Texture2D(w, h, TextureFormat.RGBAHalf, false) { wrapMode = TextureWrapMode.Clamp, name = streak ? "TheExpanse_PdcStreak" : "TheExpanse_PdcGlow" };
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w, v = (y + 0.5f) / h * 2f - 1f;
                    float a;
                    if (streak) a = Mathf.Pow(u, 1.5f) * Mathf.Exp(-v * v * 6f);
                    else { float uu = u * 2f - 1f; a = Mathf.Exp(-(uu * uu + v * v) * 4f); }
                    // a white-hot core along the middle of a streak
                    float hot = streak && core > 0f ? core * Mathf.Pow(u, 1.5f) * Mathf.Exp(-v * v * 40f) : 0f;
                    pixels[y * w + x] = new Color(color.r * a + hot, color.g * a + hot, color.b * a + hot, Mathf.Min(1f, a + hot));
                }
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
