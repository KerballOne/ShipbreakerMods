using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.VFX;

namespace PartInfoLogger
{
    // Logged with every F9 (ElectricNetwork) dump: where the frame time is likely going.
    // Frame time is averaged over the last 120 frames; the rest are live counts of the usual suspects
    // (awake rigidbodies = physics, shadow-casting lights = HDRP shadow maps, visible renderers, VFX, particles).
    internal static class PerfProbe
    {
        static readonly float[] s_Frames = new float[120];
        static int s_Index, s_Filled;

        internal static void Tick()
        {
            s_Frames[s_Index] = Time.unscaledDeltaTime;
            s_Index = (s_Index + 1) % s_Frames.Length;
            if (s_Filled < s_Frames.Length) s_Filled++;
        }

        static string Top(IEnumerable<string> names, int n) =>
            string.Join(", ", names.GroupBy(x => x).OrderByDescending(g => g.Count()).Take(n).Select(g => $"{g.Key} x{g.Count()}"));

        static string Root(Transform t)
        {
            // second-from-top ancestor name: groups ship parts by their section, loose debris by itself
            var path = new List<string>();
            for (; t != null; t = t.parent) path.Add(t.name.Replace("(Clone)", "").Trim());
            return path.Count >= 2 ? path[path.Count - 2] : path[0];
        }

        internal static void Log()
        {
            try
            {
                float avg = 0, worst = 0;
                for (int i = 0; i < s_Filled; i++) { avg += s_Frames[i]; worst = Mathf.Max(worst, s_Frames[i]); }
                avg = s_Filled > 0 ? avg / s_Filled : 0;
                Plugin.Log.LogInfo($"[Perf] frame avg {avg * 1000f:F1} ms ({(avg > 0 ? 1f / avg : 0):F1} fps), worst {worst * 1000f:F0} ms over {s_Filled} frames; " +
                                   $"fixedDeltaTime {Time.fixedDeltaTime * 1000f:F1} ms, maxDeltaTime {Time.maximumDeltaTime * 1000f:F0} ms");

                var bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
                var awake = bodies.Where(b => !b.isKinematic && !b.IsSleeping()).ToArray();
                Plugin.Log.LogInfo($"[Perf] rigidbodies {bodies.Length}: awake {awake.Length}, kinematic {bodies.Count(b => b.isKinematic)}, sleeping {bodies.Count(b => !b.isKinematic && b.IsSleeping())}");
                if (awake.Length > 0) Plugin.Log.LogInfo($"[Perf]   awake by group: {Top(awake.Select(b => Root(b.transform)), 8)}");
                if (awake.Length > 0) Plugin.Log.LogInfo($"[Perf]   awake by name: {Top(awake.Select(b => b.name.Replace("(Clone)", "").Trim()), 8)}");

                var lights = UnityEngine.Object.FindObjectsOfType<Light>().Where(l => l.enabled && l.gameObject.activeInHierarchy).ToArray();
                var shadow = lights.Where(l => l.shadows != LightShadows.None).ToArray();
                Plugin.Log.LogInfo($"[Perf] lights enabled {lights.Length}: shadow-casting {shadow.Length} " +
                                   $"(point {shadow.Count(l => l.type == LightType.Point)}, spot {shadow.Count(l => l.type == LightType.Spot)}, other {shadow.Count(l => l.type != LightType.Point && l.type != LightType.Spot)})");
                if (shadow.Length > 0) Plugin.Log.LogInfo($"[Perf]   shadow lights by group: {Top(shadow.Select(l => Root(l.transform)), 8)}");
                if (shadow.Length > 0) Plugin.Log.LogInfo($"[Perf]   shadow lights by name: {Top(shadow.Select(l => l.name), 8)}");

                var rends = UnityEngine.Object.FindObjectsOfType<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
                Plugin.Log.LogInfo($"[Perf] renderers enabled {rends.Length}, visible {rends.Count(r => r.isVisible)}");

                var vfx = UnityEngine.Object.FindObjectsOfType<VisualEffect>().Where(v => v.enabled && v.gameObject.activeInHierarchy).ToArray();
                var ps = UnityEngine.Object.FindObjectsOfType<ParticleSystem>().Where(p => p.isPlaying).ToArray();
                var audio = UnityEngine.Object.FindObjectsOfType<AudioSource>().Count(a => a.isPlaying);
                Plugin.Log.LogInfo($"[Perf] VFX active {vfx.Length}, particle systems playing {ps.Length}, audio playing {audio}");
                if (vfx.Length > 0) Plugin.Log.LogInfo($"[Perf]   VFX by name: {Top(vfx.Select(v => v.name.Replace("(Clone)", "").Trim()), 8)}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Perf] probe failed: {ex.Message}"); }
        }
    }
}
