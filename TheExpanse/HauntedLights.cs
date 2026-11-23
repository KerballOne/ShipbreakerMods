using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace TheExpanse
{
    // Haunted lights on a ghost ship.
    //
    //   Rooms   another haunting action on the pattern of the game's own: now and then the ship's lights around one
    //           spot (a room, near enough) either flicker for a few seconds or go dark for a while. More often as nodes
    //           are destroyed; never once the last one is gone.
    //   Helmet  near a node, the helmet light stutters, more the closer you are. Only while it is switched on.
    //
    // Lights are switched off and on again directly (Light.enabled), and a fitting's glowing surface is darkened with
    // them. Everything touched is put back as it was when the effect ends, except a light the game itself has turned
    // off meanwhile (its power cut, say), which is left off.
    internal static class HauntedLights
    {
        const float RescanHelmetSeconds = 2f;

        sealed class Fitting
        {
            public DynamicLight? Owner;
            public Color Emissive;
            public readonly List<Light> Lights = new List<Light>();
        }

        // ---- rooms ------------------------------------------------------------------------------------------------------
        static readonly List<Light> sRoomLights = new List<Light>();
        static readonly List<Fitting> sFittings = new List<Fitting>();
        static float sNextEventAt = -1f;
        static bool sEventOn, sBlackout, sRoomDark;
        static float sEventStart, sEventEnds, sNextToggle;

        // ---- helmet -----------------------------------------------------------------------------------------------------
        static Light? sHelmet;
        static float sNextHelmetLook;
        static bool sHelmetOff;                 // we switched it off
        static float sHelmetToggleAt;

        internal static void Reset()
        {
            EndEvent();
            RestoreHelmet();
            sHelmet = null;
            sNextHelmetLook = 0f;
            sNextEventAt = -1f;
        }

        static float Escalation => 1f - GhostNodes.ShareLeft;

        // Called a couple of times a second, after GhostNodes.Refresh.
        internal static void Schedule()
        {
            bool any = Plugin.LightFlicker.Value || Plugin.LightBlackout.Value;
            if (!any || GhostNodes.Total <= 0f)
            {
                sNextEventAt = -1f;
                if (sEventOn) EndEvent();
                return;
            }
            if (sEventOn) return;
            if (sNextEventAt < 0f) { sNextEventAt = Time.time + NextWait(); return; }
            if (Time.time >= sNextEventAt) Begin();
        }

        internal static void OnSettingsChanged()
        {
            if (sEventOn || sNextEventAt < 0f) return;
            sNextEventAt = Time.time + NextWait();
        }

        static float NextWait()
        {
            float low = Mathf.Max(1f, Plugin.LightMinInterval.Value);
            float wait = Random.Range(low, Mathf.Max(low, Plugin.LightMaxInterval.Value));
            return wait / Mathf.Lerp(1f, Mathf.Max(1f, Plugin.LightFrequencyRamp.Value), Escalation);
        }

        static void Begin()
        {
            sNextEventAt = Time.time + NextWait();
            bool flicker = Plugin.LightFlicker.Value, blackout = Plugin.LightBlackout.Value;
            sBlackout = blackout && (!flicker || Random.value < 0.5f);

            // The ship's lights that are on now; one of them, picked at random, marks the spot.
            var shipLights = new List<Light>();
            foreach (var light in Object.FindObjectsOfType<Light>())
                if (light != null && light != sHelmet && light.isActiveAndEnabled && light.GetComponentInParent<StructurePart>() != null)
                    shipLights.Add(light);
            if (shipLights.Count == 0) return;
            Vector3 spot = shipLights[Random.Range(0, shipLights.Count)].transform.position;
            float radius = Mathf.Max(1f, Plugin.LightRoomRadius.Value);

            sRoomLights.Clear();
            sFittings.Clear();
            var byOwner = new Dictionary<DynamicLight, Fitting>();
            foreach (var light in shipLights)
            {
                if (Vector3.Distance(light.transform.position, spot) > radius) continue;
                sRoomLights.Add(light);
                var owner = light.GetComponentInParent<DynamicLight>();
                if (owner == null || byOwner.ContainsKey(owner)) continue;
                var fitting = new Fitting { Owner = owner, Emissive = Traverse.Create(owner).Field("mCurrentEmissiveColor").GetValue<Color>() };
                byOwner[owner] = fitting;
                sFittings.Add(fitting);
            }
            if (sRoomLights.Count == 0) return;

            float seconds = sBlackout
                ? Random.Range(Mathf.Max(0.5f, Plugin.BlackoutMinSeconds.Value), Mathf.Max(Plugin.BlackoutMinSeconds.Value, Plugin.BlackoutMaxSeconds.Value))
                : Mathf.Max(0.5f, Plugin.FlickerSeconds.Value);
            sEventOn = true;
            sRoomDark = false;
            sEventStart = Time.time;
            sEventEnds = Time.time + seconds;
            sNextToggle = Time.time;
            Plugin.Log.LogInfo($"[Haunting] {(sBlackout ? "blackout" : "flicker")} for {seconds:0.#} s: {sRoomLights.Count} light(s) within " +
                               $"{radius:0} m; {GhostNodes.ShareLeft * 100f:0}% of the haunting left.");
        }

        // Called every frame.
        internal static void Frame()
        {
            if (sEventOn) RoomFrame();
            HelmetFrame();
        }

        static void RoomFrame()
        {
            float now = Time.time;
            if (now >= sEventEnds) { EndEvent(); return; }
            if (now >= sNextToggle)
            {
                if (sBlackout)
                {
                    // Stutters at the start and the end, dark in between.
                    bool edge = now - sEventStart < 1.2f || sEventEnds - now < 1.2f;
                    sRoomDark = edge ? !sRoomDark : true;
                    sNextToggle = now + (edge ? Random.Range(0.05f, 0.2f) : 0.25f);
                }
                else
                {
                    sRoomDark = !sRoomDark;
                    sNextToggle = now + (sRoomDark ? Random.Range(0.04f, 0.2f) : Random.Range(0.05f, 0.6f));
                }
            }
            // Every frame, in case the game switches one back on.
            SetRoom(!sRoomDark);
        }

        static void SetRoom(bool lit)
        {
            foreach (var light in sRoomLights)
                if (light != null) light.enabled = lit;
            foreach (var fitting in sFittings)
                if (fitting.Owner != null) fitting.Owner.SetUnityMeshEmissiveColor(lit ? fitting.Emissive : Color.black);
        }

        static void EndEvent()
        {
            if (sEventOn)
            {
                foreach (var light in sRoomLights)
                {
                    if (light == null) continue;
                    var owner = light.GetComponentInParent<DynamicLight>();
                    light.enabled = owner == null || owner.State == DynamicLight.LightState.On;
                }
                foreach (var fitting in sFittings)
                    if (fitting.Owner != null) fitting.Owner.SetUnityMeshEmissiveColor(fitting.Emissive, force: true);
            }
            sRoomLights.Clear();
            sFittings.Clear();
            sEventOn = false;
            sRoomDark = false;
        }

        // ---- helmet -----------------------------------------------------------------------------------------------------
        static void HelmetFrame()
        {
            if (sHelmet == null && Time.unscaledTime >= sNextHelmetLook)
            {
                sNextHelmetLook = Time.unscaledTime + RescanHelmetSeconds;
                var sender = Object.FindObjectOfType<HelmetSpotlightToggleSender>();
                if (sender != null) sHelmet = Traverse.Create(sender).Field("m_Light").GetValue<Light>();
            }
            if (sHelmet == null) return;

            float radius = Mathf.Max(0.1f, Plugin.HelmetFlickerRadius.Value);
            float nearest = float.MaxValue;                 // distance as a share of that node's reach
            if (Plugin.HelmetFlicker.Value && Plugin.Enabled.Value && PartSeparation.CustomShip)
            {
                Vector3 at = sHelmet.transform.position;
                for (int i = 0; i < GhostNodes.Positions.Count; i++)
                    nearest = Mathf.Min(nearest, Vector3.Distance(at, GhostNodes.Positions[i]) / (radius * GhostNodes.Reach[i]));
            }
            // Only while the player has it switched on: when we are not holding it off, its state is the player's.
            if (nearest >= 1f || (!sHelmetOff && !sHelmet.isActiveAndEnabled)) { RestoreHelmet(); return; }

            float close = 1f - nearest;                     // 0 at the edge, 1 at the node
            float now = Time.unscaledTime;
            if (now < sHelmetToggleAt) return;
            if (sHelmetOff)
            {
                sHelmetOff = false;
                sHelmet.enabled = true;
                sHelmetToggleAt = now + Random.Range(0.05f, Mathf.Lerp(2f, 0.25f, close));
            }
            else if (Random.value < Mathf.Lerp(0.2f, 0.9f, close))
            {
                sHelmetOff = true;
                sHelmet.enabled = false;
                sHelmetToggleAt = now + Random.Range(0.03f, Mathf.Lerp(0.08f, 0.35f, close));
            }
            else sHelmetToggleAt = now + Random.Range(0.1f, Mathf.Lerp(1.5f, 0.3f, close));
        }

        static void RestoreHelmet()
        {
            if (sHelmetOff && sHelmet != null) sHelmet.enabled = true;
            sHelmetOff = false;
        }
    }
}
