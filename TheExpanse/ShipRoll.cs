using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;

namespace TheExpanse
{
    // A haunting action of its own: now and then a ghost ship rolls slowly about its long axis.
    //
    // The game's haunting flips doors, flips room pressure and electrifies parts, each on a random timer while nodes are
    // left. This adds a fourth action on the same pattern and leaves those three alone. It is active while the share of
    // the haunting left is at or below RollStartShare, waits a random time between rolls, and stops for good with the
    // last node.
    //
    // What turns: the game holds any body of 50 tonnes or more fixed in place (kinematic), so the hull does not react
    // to forces. Those heavy bodies are turned directly, all about one line: the long axis of the heaviest, through its
    // center of mass. Everything lighter is a free body, and is pushed by the turning hull like anything else it
    // touches, the player included. The long axis is read from the heaviest body's mass distribution (it is the axis
    // the body is easiest to spin about), or from its size if that is not available.
    //
    // The speed eases in and out over a few seconds, so nothing is hit by a sudden start.
    internal static class ShipRoll
    {
        const float EaseSeconds = 3f;
        const float RescanSeconds = 1f;
        const float ClearlyLonger = 0.8f;       // the long axis must be this much easier to spin about than the next

        static readonly List<Rigidbody> sBodies = new List<Rigidbody>();
        static float sNextRollAt = -1f;         // game time; below zero = not scheduled
        static bool sRolling;
        static float sElapsed, sDuration, sRate, sNextScan;
        static Vector3 sAxis, sPivot;

        internal static void Reset()
        {
            sBodies.Clear();
            sNextRollAt = -1f;
            sRolling = false;
        }

        // New settings: a roll that is waiting is timed again from now with the new interval. One under way keeps going.
        internal static void OnSettingsChanged()
        {
            if (sRolling || sNextRollAt < 0f) return;
            float wait = NextWait();
            sNextRollAt = Time.time + wait;
            Plugin.Log.LogInfo($"[Haunting] next roll in {wait:0} s.");
        }

        // Called a couple of times a second, after GhostNodes.Refresh.
        internal static void Schedule()
        {
            bool on = Plugin.RollShip.Value && GhostNodes.Total > 0f && GhostNodes.ShareLeft <= Plugin.RollStartShare.Value;
            if (!on)
            {
                sNextRollAt = -1f;
                // Mid-roll with the last node gone: ease to a stop instead of freezing.
                if (sRolling && sElapsed < sDuration - EaseSeconds) sDuration = sElapsed + Mathf.Min(EaseSeconds, sElapsed);
                return;
            }
            if (sRolling) return;
            if (sNextRollAt < 0f)
            {
                float wait = NextWait();
                sNextRollAt = Time.time + wait;
                Plugin.Log.LogInfo($"[Haunting] first roll in {wait:0} s ({GhostNodes.Positions.Count} node(s) left).");
                return;
            }
            if (Time.time >= sNextRollAt) Begin();
        }

        // How far the haunting has gone: 0 with every node there, 1 with the last node left.
        static float Escalation => 1f - GhostNodes.ShareLeft;
        static float FrequencyFactor => Mathf.Lerp(1f, Mathf.Max(1f, Plugin.RollFrequencyRamp.Value), Escalation);
        static float SpeedFactor => Mathf.Lerp(1f, Mathf.Max(0f, Plugin.RollSpeedRamp.Value), Escalation);

        static float NextWait()
        {
            float low = Mathf.Max(1f, Plugin.RollMinInterval.Value);
            return Random.Range(low, Mathf.Max(low, Plugin.RollMaxInterval.Value)) / FrequencyFactor;
        }

        static void Begin()
        {
            sNextRollAt = Time.time + NextWait();           // also the retry time if this roll cannot start
            sRate = Plugin.RollDegreesPerSecond.Value * SpeedFactor;
            sDuration = Plugin.RollSeconds.Value;
            if (sRate <= 0f || sDuration <= 0f) return;

            var heaviest = Collect();
            if (heaviest == null)
            {
                Plugin.Log.LogInfo("[Haunting] no roll: nothing on this ship is heavy enough to be held in place by the game.");
                return;
            }
            if (!TryLongAxis(out sAxis, out sPivot, out string how))
            {
                Plugin.Log.LogInfo("[Haunting] no roll: the ship has no clear long axis (" + how + ").");
                return;
            }
            if (Random.value < 0.5f) sRate = -sRate;
            sElapsed = 0f;
            sNextScan = Time.time + RescanSeconds;
            sRolling = true;
            float ease = Mathf.Min(EaseSeconds, sDuration * 0.5f);
            Plugin.Log.LogInfo($"[Haunting] rolling the ship {sRate * (sDuration - ease):0.#} degrees over {sDuration:0.#} s about its long axis " +
                               $"({how}); {sBodies.Count} fixed body(ies), the heaviest {heaviest.mass / 1000f:0.#} t; " +
                               $"{GhostNodes.ShareLeft * 100f:0}% of the haunting left, so {SpeedFactor:0.##}x speed and " +
                               $"{FrequencyFactor:0.##}x as often.");
        }

        // The ship's fixed bodies; returns the heaviest.
        static Rigidbody? Collect()
        {
            sBodies.Clear();
            float threshold = HierarchyProcessor.sKinematicMassThreshold > 0f ? HierarchyProcessor.sKinematicMassThreshold : 50000f;
            Rigidbody? heaviest = null;
            foreach (var body in Object.FindObjectsOfType<Rigidbody>())
            {
                if (body == null || !body.isKinematic || body.mass < threshold) continue;
                if (body.GetComponentInChildren<StructurePart>() == null) continue;         // not a piece of ship
                sBodies.Add(body);
                if (heaviest == null || body.mass > heaviest.mass) heaviest = body;
            }
            return heaviest;
        }

        // The ship's long axis and middle, from where its parts are: the direction they spread out furthest along
        // (the main axis of their spread). The game reports the same mass spread every way for its fixed bodies, so
        // their own mass figures cannot be used.
        static bool TryLongAxis(out Vector3 axis, out Vector3 middle, out string how)
        {
            axis = Vector3.forward;
            middle = Vector3.zero;
            var points = new List<Vector3>();
            foreach (var body in sBodies)
                foreach (var part in body.GetComponentsInChildren<StructurePart>())
                    if (part != null) points.Add(part.transform.position);
            if (points.Count < 10) { how = $"only {points.Count} part(s) found"; return false; }

            foreach (var point in points) middle += point;
            middle /= points.Count;
            float xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
            foreach (var point in points)
            {
                var d = point - middle;
                xx += d.x * d.x; yy += d.y * d.y; zz += d.z * d.z;
                xy += d.x * d.y; xz += d.x * d.z; yz += d.y * d.z;
            }

            // The direction of greatest spread, by repeated multiplication; started along the widest of the three
            // world axes so it cannot start at right angles to the answer.
            Vector3 Spread(Vector3 v) => new Vector3(xx * v.x + xy * v.y + xz * v.z, xy * v.x + yy * v.y + yz * v.z, xz * v.x + yz * v.y + zz * v.z);
            axis = xx >= yy && xx >= zz ? Vector3.right : yy >= zz ? Vector3.up : Vector3.forward;
            axis = (axis + new Vector3(0.01f, 0.02f, 0.03f)).normalized;
            for (int i = 0; i < 64; i++) axis = Spread(axis).normalized;
            float along = Vector3.Dot(axis, Spread(axis));

            // The next widest spread, at right angles to that.
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            for (int i = 0; i < 64; i++)
            {
                side = Spread(side);
                side = (side - Vector3.Dot(side, axis) * axis).normalized;
            }
            float across = Vector3.Dot(side, Spread(side));

            float ratio = along > 0f ? Mathf.Sqrt(Mathf.Max(0f, across) / along) : 1f;
            how = $"from {points.Count} parts, width {ratio:0.00} of length";
            return along > 0f && ratio <= ClearlyLonger;
        }

        // Called every physics step.
        internal static void FixedStep()
        {
            if (!sRolling) return;
            float dt = Time.fixedDeltaTime;
            float ease = Mathf.Max(0.01f, Mathf.Min(EaseSeconds, sDuration * 0.5f));
            float speed = sRate * Mathf.Clamp01(Mathf.Min(sElapsed / ease, (sDuration - sElapsed) / ease));
            sElapsed += dt;

            // Pieces cut off during the roll: heavy ones are new fixed bodies and must keep turning with the rest.
            if (Time.time >= sNextScan)
            {
                sNextScan = Time.time + RescanSeconds;
                Collect();
            }

            var step = Quaternion.AngleAxis(speed * dt, sAxis);
            foreach (var body in sBodies)
            {
                if (body == null || !body.isKinematic) continue;
                body.MovePosition(sPivot + step * (body.position - sPivot));
                body.MoveRotation(step * body.rotation);
            }

            if (sElapsed >= sDuration)
            {
                sRolling = false;
                sBodies.Clear();
                sNextRollAt = Time.time + NextWait();
            }
        }
    }
}
