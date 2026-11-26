using System.Collections.Generic;
using System.Diagnostics;
using BBI.Unity.Game;
using UnityEngine;

namespace TheExpanse
{
    // Cost test for the stationary-ship idea: instead of the ship moving through the world, the world (the bay, the Hab,
    // everything not the ship or the player) moves past the ship. Started by [Debug] WorldShiftTestKey (Ctrl+F10) with the
    // ship parked in the bay, not flying. It runs three timed phases and logs each, then puts everything back:
    //   1. baseline, 5 s: nothing moves;
    //   2. world moving, 10 s: every top-level object of the scene except the ship's and the player's moved toward the
    //      stern at 50 m/s, one step at a time (the colliders under them included);
    //   3. world moving, colliders off, 10 s: the same with the moved objects' colliders switched off (as far from the
    //      bay, where it cannot be touched anyway).
    // Logged per phase: average and worst frame time, the time spent moving, and how many objects and colliders moved.
    internal static class WorldShiftTest
    {
        const float Speed = 50f;
        static readonly float[] PhaseSeconds = { 5f, 10f, 10f };
        static readonly string[] PhaseNames = { "baseline (nothing moving)", "world moving at 50 m/s", "world moving, its colliders off" };

        static int sPhase = -1;
        static float sPhaseEnd;
        static readonly List<Transform> sRoots = new List<Transform>();
        static readonly List<Collider> sColliders = new List<Collider>();
        static Vector3 sOffset, sDirection;
        static readonly List<float> sFrames = new List<float>();
        static double sMoveMs;
        static int sSteps, sStaticColliders, sBodyColliders;
        static readonly Stopwatch sWatch = new Stopwatch();

        internal static bool Running => sPhase >= 0;

        // every frame (Update)
        internal static void Tick()
        {
            if (Plugin.WorldShiftTestKey.Value.IsDown())
            {
                if (Running) Finish("stopped by the key");
                else Start();
            }
            if (!Running) return;
            sFrames.Add(Time.unscaledDeltaTime * 1000f);
            if (Time.unscaledTime >= sPhaseEnd) NextPhase();
        }

        static void Start()
        {
            if (FlightModel.UnderWay) { Plugin.Log.LogInfo("[Shift test] park the ship first (not while flying)."); return; }
            var player = FlightSeat.PlayerBody;
            sRoots.Clear();
            sColliders.Clear();
            sStaticColliders = sBodyColliders = 0;
            var camera = Camera.main;
            int ship = 0, loose = 0;
            foreach (var root in SceneLookup.Roots())
            {
                if (player != null && player.transform.root == root) continue;
                if (camera != null && camera.transform.root == root) continue;
                if (root.name.StartsWith("TheExpanse_")) continue;
                bool isShip = false;
                foreach (var part in root.GetComponentsInChildren<StructurePart>())
                    if (FlightModel.IsShipPart(part)) { isShip = true; break; }
                if (isShip) { ship++; continue; }
                // free bodies (loose salvage, debris) stay out of the test: moved by hand at 50 m/s they would plough
                // through the ship
                bool free = false;
                foreach (var body in root.GetComponentsInChildren<Rigidbody>())
                    if (!body.isKinematic) { free = true; break; }
                if (free) { loose++; continue; }
                sRoots.Add(root);
                foreach (var collider in root.GetComponentsInChildren<Collider>())
                {
                    if (!collider.enabled) continue;
                    sColliders.Add(collider);
                    if (collider.attachedRigidbody == null) sStaticColliders++; else sBodyColliders++;
                }
            }
            // toward the stern of the ship if one is found, else along -z
            sDirection = FlightCamera.TryShipFrame(out _, out var forward, out _) ? -forward : Vector3.back;
            sOffset = Vector3.zero;
            Plugin.Log.LogInfo($"[Shift test] starting: {sRoots.Count} top-level object(s) to move ({ship} ship root(s), {loose} with free bodies, and the player left in place), " +
                               $"{sColliders.Count} collider(s) under them ({sStaticColliders} fixed, {sBodyColliders} on bodies).");
            sPhase = -1;
            NextPhase();
        }

        static void NextPhase()
        {
            if (sPhase >= 0) Report();
            sPhase++;
            if (sPhase >= PhaseSeconds.Length) { Finish("done"); return; }
            if (sPhase == 2) foreach (var c in sColliders) if (c != null) c.enabled = false;
            sFrames.Clear();
            sMoveMs = 0;
            sSteps = 0;
            sPhaseEnd = Time.unscaledTime + PhaseSeconds[sPhase];
        }

        // each physics step (FixedUpdate)
        internal static void Step()
        {
            if (sPhase < 1) return;
            var delta = sDirection * (Speed * Time.fixedDeltaTime);
            sWatch.Restart();
            foreach (var root in sRoots) if (root != null) root.position += delta;
            sWatch.Stop();
            sOffset += delta;
            sMoveMs += sWatch.Elapsed.TotalMilliseconds;
            sSteps++;
        }

        static void Report()
        {
            if (sFrames.Count == 0) return;
            var sorted = new List<float>(sFrames);
            sorted.Sort();
            float sum = 0f;
            foreach (var f in sFrames) sum += f;
            float avg = sum / sFrames.Count;
            float p95 = sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))];
            Plugin.Log.LogInfo($"[Shift test] {PhaseNames[sPhase]}: {sFrames.Count} frames, average {avg:0.0} ms ({1000f / avg:0} fps), " +
                               $"95% under {p95:0.0} ms, worst {sorted[sorted.Count - 1]:0.0} ms; moving took {(sSteps > 0 ? sMoveMs / sSteps : 0):0.00} ms a step " +
                               $"over {sSteps} steps.");
        }

        static void Finish(string why)
        {
            if (sPhase >= 0 && sPhase < PhaseSeconds.Length && why != "done") Report();
            foreach (var root in sRoots) if (root != null) root.position -= sOffset;
            foreach (var c in sColliders) if (c != null) c.enabled = true;
            Plugin.Log.LogInfo($"[Shift test] {why}: everything put back ({sOffset.magnitude:0} m).");
            sRoots.Clear();
            sColliders.Clear();
            sOffset = Vector3.zero;
            sPhase = -1;
        }

        internal static void Reset()
        {
            if (Running) Finish("level change");
        }
    }
}
