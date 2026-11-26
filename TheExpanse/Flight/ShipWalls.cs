using System.Collections.Generic;
using UnityEngine;

namespace TheExpanse
{
    // Flight Control: the Master Jack (the Hab) is a wall the ship stops against.
    //
    // It cannot be let go like the light jacks: it is the Hab (its door, the kiosk, the end-shift prompt), and a free
    // body needs convex colliders, which filled the door. Left fixed, the game's physics does not collide it with the
    // ship (the flight moves the ship's bodies itself), so the hull passed through it. So the flight checks the ship's
    // colliders against the Master Jack's each physics step: where they overlap, the ship is pushed back out and loses
    // its speed into the jack (no bounce), as against a wall.
    internal static class ShipWalls
    {
        const string WallRoot = "PRF_MasterJack_NoSpawner";
        const float RefreshSeconds = 5f;            // the ship's colliders looked up again (parts cut off, taken on)

        static readonly List<Collider> sWall = new List<Collider>();
        static Bounds sWallBounds;
        static bool sWallFound;
        static float sNextWallLook;
        static readonly List<Collider> sShip = new List<Collider>();
        static float sNextShipLook;
        static bool sHitLogged;

        internal static void Reset()
        {
            sWall.Clear();
            sWallFound = false;
            sNextWallLook = 0f;
            sShip.Clear();
            sNextShipLook = 0f;
            sHitLogged = false;
        }

        static void FindWall()
        {
            if (sWallFound || Time.unscaledTime < sNextWallLook) return;
            sNextWallLook = Time.unscaledTime + 5f;
            var root = SceneLookup.Root(WallRoot);
            if (root == null) return;
            sWall.Clear();
            bool any = false;
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                sWall.Add(c);
                if (!any) { sWallBounds = c.bounds; any = true; } else sWallBounds.Encapsulate(c.bounds);
            }
            sWallFound = any;
            if (any) Plugin.Log.LogInfo($"[Flight] the Master Jack (the Hab) is a wall to the ship: {sWall.Count} collider(s), " +
                                        $"{sWallBounds.size.x:0} x {sWallBounds.size.y:0} x {sWallBounds.size.z:0} m around {sWallBounds.center}.");
        }

        // The flight starts: the ship's colliders are looked up now.
        internal static void Begin()
        {
            sNextShipLook = 0f;
            sHitLogged = false;
        }

        // Each physics step while the ship moves. pivot and velocity are the flight model's; the correction is applied to
        // both (pushed out, the speed into the wall taken away). True when the ship touched the wall.
        internal static bool Step(IEnumerable<Rigidbody> bodies, ref Vector3 pivot, ref Vector3 velocity, ref Vector3 spin)
        {
            FindWall();
            if (!sWallFound) return false;
            if (Time.unscaledTime >= sNextShipLook)
            {
                sNextShipLook = Time.unscaledTime + RefreshSeconds;
                sShip.Clear();
                foreach (var body in bodies)
                    if (body != null)
                        foreach (var c in body.GetComponentsInChildren<Collider>())
                            if (c != null && !c.isTrigger && c.enabled && c.attachedRigidbody == body) sShip.Add(c);
            }

            var push = Vector3.zero;
            int touching = 0;
            foreach (var s in sShip)
            {
                if (s == null) continue;
                var sb = s.bounds;
                if (!sb.Intersects(sWallBounds)) continue;
                var st = s.transform;
                foreach (var w in sWall)
                {
                    if (w == null || !sb.Intersects(w.bounds)) continue;
                    var wt = w.transform;
                    if (!Physics.ComputePenetration(s, st.position, st.rotation, w, wt.position, wt.rotation, out var dir, out var dist)) continue;
                    if (dist <= 0f) continue;
                    touching++;
                    // the deepest push along each axis wins (several overlaps the same way are one push, not a sum)
                    var p = dir * dist;
                    push.x = Mathf.Abs(p.x) > Mathf.Abs(push.x) ? p.x : push.x;
                    push.y = Mathf.Abs(p.y) > Mathf.Abs(push.y) ? p.y : push.y;
                    push.z = Mathf.Abs(p.z) > Mathf.Abs(push.z) ? p.z : push.z;
                }
            }
            if (touching == 0) return false;

            pivot += push;
            var n = push.normalized;
            float into = Vector3.Dot(velocity, n);
            if (into < 0f) velocity -= n * into;        // the speed into the wall is gone; along it the ship slides
            spin *= 0.5f;                               // a knock against it calms any turning
            if (!sHitLogged)
            {
                sHitLogged = true;
                Plugin.Log.LogInfo($"[Flight] the ship hit the Master Jack: {touching} overlap(s), pushed back {push.magnitude:0.00} m; " +
                                   $"speed into it taken away (now {velocity.magnitude:0.0} m/s).");
            }
            return true;
        }
    }
}
