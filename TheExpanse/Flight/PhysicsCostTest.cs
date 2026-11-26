using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace TheExpanse
{
    // Diagnostic: where the physics time goes. [Debug] PhysicsCostTestKey (Ctrl+F10) samples 5 s, anywhere (in the bay,
    // flying near it or far out, parked on foot), and logs per frame:
    //   steps   physics steps run in the frame (the game runs 50 a second);
    //   window  from the frame's first physics step to its Update: every script's step and the physics engine;
    //   engine  the physics engine itself: from the end of this mod's step to just after the engine ran (a coroutine
    //           waiting for the fixed update resumes there), so it also holds the scripts that run after this mod's;
    //   flight  this mod's flight step (the ship's motion, loose things, the suit aboard);
    // and once: how many bodies there are (free, fixed, asleep) and how many colliders, and where the ship is.
    internal class PhysicsCostTest : MonoBehaviour
    {
        static PhysicsCostTest? sRunner;
        static bool sActive, sStepSeen;
        static float sEnd;
        static readonly Stopwatch sWindow = new Stopwatch(), sEngine = new Stopwatch(), sFlight = new Stopwatch();
        static double sWindowMs, sEngineMs, sFlightMs;
        static int sFrames, sSteps;
        static float sFrameMs;

        // every frame (Update, first thing)
        internal static void Tick()
        {
            if (sStepSeen) { sWindow.Stop(); if (sActive) sWindowMs += sWindow.Elapsed.TotalMilliseconds; sStepSeen = false; }
            if (Plugin.PhysicsCostTestKey.Value.IsDown() && !sActive) Begin();
            if (!sActive) return;
            sFrames++;
            sFrameMs += Time.unscaledDeltaTime * 1000f;
            if (Time.unscaledTime >= sEnd) Report();
        }

        // each physics step, at the start of this mod's step
        internal static void StepStart()
        {
            if (!sStepSeen) { sStepSeen = true; sWindow.Restart(); }
            if (sActive) sSteps++;
        }

        internal static void FlightStart() { if (sActive) sFlight.Restart(); }

        internal static void FlightEnd()
        {
            if (!sActive) return;
            sFlight.Stop();
            sFlightMs += sFlight.Elapsed.TotalMilliseconds;
            sEngine.Restart();
        }

        IEnumerator AfterEngine()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                if (sActive && sEngine.IsRunning) { sEngine.Stop(); sEngineMs += sEngine.Elapsed.TotalMilliseconds; }
            }
        }

        static void Begin()
        {
            if (sRunner == null)
            {
                var go = new GameObject("TheExpanse_PhysicsCostTest");
                Object.DontDestroyOnLoad(go);
                sRunner = go.AddComponent<PhysicsCostTest>();
                sRunner.StartCoroutine(sRunner.AfterEngine());
            }
            int free = 0, fixedBodies = 0, asleep = 0, colliders = 0, meshColliders = 0;
            foreach (var body in Object.FindObjectsOfType<Rigidbody>())
            {
                if (body.isKinematic) fixedBodies++; else free++;
                if (!body.isKinematic && body.IsSleeping()) asleep++;
            }
            foreach (var c in Object.FindObjectsOfType<Collider>())
                if (c.enabled) { colliders++; if (c is MeshCollider) meshColliders++; }
            string state = !FlightModel.UnderWay ? "not flying" : FlightModel.Parked ? "parked" : $"flying at {FlightModel.Speed:0} m/s";
            Plugin.Log.LogInfo($"[Physics test] {state}, ship at {FlightModel.ShipPosition.magnitude / 1000f:0.0} km from the origin: " +
                               $"{free} free body(ies) ({asleep} asleep), {fixedBodies} fixed, {colliders} collider(s) on ({meshColliders} mesh). Sampling 5 s...");
            sActive = true;
            sEnd = Time.unscaledTime + 5f;
            sWindowMs = sEngineMs = sFlightMs = 0; sFrames = sSteps = 0; sFrameMs = 0f;
        }

        static void Report()
        {
            sActive = false;
            float n = Mathf.Max(1, sFrames);
            Plugin.Log.LogInfo($"[Physics test] {sFrames} frames, {sSteps / n:0.00} steps a frame: frame {sFrameMs / n:0.0} ms, window {sWindowMs / n:0.00} ms, " +
                               $"engine (and later scripts) {sEngineMs / n:0.00} ms, flight step {sFlightMs / n:0.00} ms (a frame each).");
        }
    }
}
