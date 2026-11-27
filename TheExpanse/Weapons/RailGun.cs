using System.Collections.Generic;
using BBI.Unity.Game;
using InControl;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.VFX;

namespace TheExpanse
{
    // The rail gun: fires along the ship's nose (no aiming: the flight controls stay, the ship is the aim).
    //   fire:    hold RT (left mouse button) in rail gun mode: it charges for RailGunCharge seconds and fires when full;
    //            let go early and the charge drains. Then it reloads for RailGunCooldown seconds. In this mode the
    //            triggers don't move the throttle (it stays as set).
    //   beam:    the village rail gate's own beam copied at the muzzle along the nose (RailGateCopy); its beam keeps
    //            its size, RailGunGlowSize scales only its glow (at full size the glow covers the whole view); plus a
    //            short slug trail;
    //   shot:    first the blast at the muzzle (the gate's flash, RailGunBlastSize), then RailGunBeamDelay later the
    //            beam and the slug; both move with the ship;
    //   hit:     the slug goes through up to RailGunPierce parts not of the own ship within RailGunRange: each takes
    //            RailGunHeat of heat damage and a shove.
    internal static class RailGun
    {

        static Transform? sMuzzle;
        static float sCharge, sReadyAt;
        static bool sHeld, sLoggedMuzzle;
        static LineRenderer? sTrail;
        static float sTrailLength;
        static float sTrailUntil;
        static Material? sTrailMaterial;
        const float TrailSeconds = 0.6f;
        static float sShotDue = -1f;                 // the beam and slug follow the muzzle blast
        static readonly RaycastHit[] sHits = new RaycastHit[64];
        static readonly List<RaycastHit> sSorted = new List<RaycastHit>();

        internal static float Charge => sCharge;
        internal static float ReloadLeft => Mathf.Max(0f, sReadyAt - Time.time);
        internal static bool Ready => ReloadLeft <= 0f;

        internal static void Reset()
        {
            sMuzzle = null;
            sCharge = 0f; sReadyAt = 0f;
            sHeld = false;
            sShotDue = -1f;
            sLoggedMuzzle = false;
            RailGateCopy.Reset();
            if (sTrail != null) Object.Destroy(sTrail.gameObject);
            sTrail = null;
        }

        internal static void SetMuzzle(Transform muzzle) => sMuzzle = muzzle;

        // The muzzle and the way it fires: the Railgun_Muzzle part, along the ship's nose; else the ship's nose.
        internal static void Aim(out Vector3 from, out Vector3 dir)
        {
            dir = FlightModel.ForwardDirection.normalized;
            if (sMuzzle != null) from = sMuzzle.position;
            else from = FlightModel.ShipPosition + dir * FlightModel.HalfLength;
            if (!sLoggedMuzzle)
            {
                sLoggedMuzzle = true;
                Plugin.Log.LogInfo(sMuzzle != null ? $"[Weapons] rail gun fires from '{sMuzzle.name}' at {from}, along the ship's nose {dir}."
                                                   : $"[Weapons] no Railgun_Muzzle found: the rail gun fires from the ship's nose at {from}.");
            }
        }

        // Every frame while seated (from Weapons.Tick).
        internal static void ReadInput()
        {
            var pad = InputManager.ActiveDevice;
            sHeld = Weapons.Allowed && Weapons.Mode == WeaponMode.RailGun && !Weapons.Looking
                    && (Input.GetMouseButton(0) || (pad != null && pad.RightTrigger.Value > 0.5f));
        }

        // Every frame, LateUpdate after the ship's frame is placed.
        internal static void Frame()
        {
            float dt = Time.deltaTime;
            bool armed = Weapons.Allowed && Weapons.Mode == WeaponMode.RailGun;
            if (!armed) sHeld = false;
            float chargeTime = Mathf.Max(0.05f, Plugin.WeaponsRailGunCharge.Value);
            if (sHeld && Ready) sCharge = Mathf.Min(1f, sCharge + dt / chargeTime);
            else sCharge = Mathf.Max(0f, sCharge - dt / (chargeTime * 0.5f));
            if (sCharge >= 1f && sHeld && Ready && dt > 0f) Fire();
            if (sShotDue >= 0f && Time.time >= sShotDue) { sShotDue = -1f; Shoot(); }
            RailGateCopy.Tick();
            if (sTrail != null && sTrail.enabled)
            {
                float left = sTrailUntil - Time.time;
                if (left <= 0f) sTrail.enabled = false;
                else
                {
                    // from the muzzle where it is now: the trail goes with the ship
                    Aim(out var from, out var dir);
                    sTrail.SetPosition(0, from);
                    sTrail.SetPosition(1, from + dir * sTrailLength);
                    float w = 2.5f * left / TrailSeconds;
                    sTrail.startWidth = w; sTrail.endWidth = w * 0.4f;
                }
            }
        }

        static void Fire()
        {
            sCharge = 0f;
            sReadyAt = Time.time + Mathf.Max(0.2f, Plugin.WeaponsRailGunCooldown.Value);
            Aim(out var muzzle, out var nose);
            float delay = Mathf.Max(0f, Plugin.WeaponsRailGunBeamDelay.Value);
            RailGateCopy.Fire(sMuzzle != null ? sMuzzle : FlightModel.Frame, muzzle, nose, Mathf.Max(0.001f, Plugin.WeaponsRailGunGlowSize.Value),
                              Plugin.WeaponsRailGunBlastSize.Value, delay);
            sShotDue = Time.time + delay;
        }

        static void Shoot()
        {
            Aim(out var from, out var dir);
            // recoil: the ship is pushed back along the nose
            FlightModel.Kick(-dir * Mathf.Max(0f, Plugin.WeaponsRailGunRecoil.Value));
            float range = Mathf.Max(100f, Plugin.WeaponsRailGunRange.Value);
            var end = from + dir * range;
            int pierced = 0;
            string what = "nothing";
            int n = Physics.RaycastNonAlloc(from, dir, sHits, range, ~0, QueryTriggerInteraction.Ignore);
            sSorted.Clear();
            for (int i = 0; i < n; i++) sSorted.Add(sHits[i]);
            sSorted.Sort((a, b) => a.distance.CompareTo(b.distance));
            var touched = new HashSet<StructurePart>();
            int pierce = Mathf.Max(1, Plugin.WeaponsRailGunPierce.Value);
            foreach (var hit in sSorted)
            {
                if (hit.collider == null) continue;
                if (FlightSeat.PlayerCollider != null && hit.collider == FlightSeat.PlayerCollider) continue;
                var part = hit.collider.GetComponentInParent<StructurePart>();
                if (part != null && FlightModel.IsShipPart(part) && FlightModel.IsMainShipPart(part)) continue;    // the own ship
                if (pierced == 0) { end = hit.point; what = part != null ? $"'{part.name}'" : $"'{hit.collider.name}' (no part: the station)"; }
                if (part == null) { end = hit.point; break; }            // the station stops it
                if (!touched.Add(part)) continue;
                try { ReactorFire.HeatPart(part, Mathf.Max(0f, Plugin.WeaponsRailGunHeat.Value)); }
                catch (System.Exception ex) { Plugin.Log.LogWarning("[Weapons] rail gun damage failed: " + ex.Message); }
                var body = hit.collider.attachedRigidbody;
                if (body != null && !body.isKinematic) body.AddForceAtPosition(dir * Mathf.Max(0f, Plugin.WeaponsRailGunPush.Value), hit.point, ForceMode.Impulse);
                end = hit.point;
                if (++pierced >= pierce) break;
            }
            Plugin.Log.LogInfo($"[Weapons] RAIL GUN fired from {from} along {dir}: hit {what} at {Vector3.Distance(from, end):0} m, through {pierced} part(s).");
            ShowTrail(from, end);
        }

        // ---- the slug's trail and the explosion --------------------------------------------------------------------

        static void ShowTrail(Vector3 from, Vector3 to)
        {
            if (sTrail == null)
            {
                var go = new GameObject("TheExpanse_RailGunTrail");
                Object.DontDestroyOnLoad(go);
                sTrail = go.AddComponent<LineRenderer>();
                sTrail.useWorldSpace = true;
                sTrail.positionCount = 2;
                sTrail.alignment = LineAlignment.View;
                sTrail.textureMode = LineTextureMode.Stretch;
                sTrail.shadowCastingMode = ShadowCastingMode.Off;
                sTrail.receiveShadows = false;
                sTrailMaterial = DrivePlume.AdditiveMaterial(TrailTexture());
                sTrail.sharedMaterial = sTrailMaterial;
            }
            sTrail.SetPosition(0, from);
            sTrail.SetPosition(1, to);
            sTrailLength = Vector3.Distance(from, to);
            sTrail.enabled = true;
            sTrailUntil = Time.time + TrailSeconds;
        }

        static Texture2D TrailTexture()
        {
            const int W = 8, H = 16;
            var tex = new Texture2D(W, H, TextureFormat.RGBAHalf, false) { wrapMode = TextureWrapMode.Clamp, name = "TheExpanse_RailTrail" };
            var pixels = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = (y + 0.5f) / H * 2f - 1f;
                    float a = Mathf.Exp(-v * v * 5f), core = Mathf.Exp(-v * v * 40f);
                    pixels[y * W + x] = new Color(0.6f * a + 2.5f * core, 0.9f * a + 2.5f * core, 2.5f * a + 2.5f * core, a);
                }
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }

    }
}
