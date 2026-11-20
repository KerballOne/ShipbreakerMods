using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BBI.Unity.Game;
using HarmonyLib;

namespace PartInfoLogger
{
    // How long does a ship take to load?
    //   start     SceneLoader.TearDownAndLoadLevelAsync is called (the moment a ship / level is chosen)
    //   jointing  the first JointHelper.JointingWorkerData is constructed (JointBudget's hook) - the joint pass begins
    //   live      the game state changes to Gameplay
    // One line per load in BepInEx's log and in load_times.csv next to OutputPath:
    //   When,TotalSeconds,ToJointingSeconds,JointingToLiveSeconds,Parts,LargestMeshVerts
    //
    // Logging only (a prefix that never changes arguments or skips the original). Patched manually inside a try/catch
    // so a missing / renamed target can never abort the rest of the plugin's patches.
    internal static class LoadTimer
    {
        static readonly Stopwatch _sw = new Stopwatch();
        static double _jointAt = -1;
        static int _parts, _verts;
        static bool _armed;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var targets = AccessTools.GetDeclaredMethods(typeof(SceneLoader)).Where(m => m.Name == "TearDownAndLoadLevelAsync").ToList();
                if (targets.Count == 0) { Plugin.Log.LogWarning("[LoadTimer] SceneLoader.TearDownAndLoadLevelAsync not found; load timing disabled."); return; }
                foreach (var m in targets) harmony.Patch(m, prefix: new HarmonyMethod(typeof(LoadTimer), nameof(Prefix)));
                Plugin.Log.LogInfo($"[LoadTimer] hooked ({targets.Count} overload(s)).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[LoadTimer] could not hook: " + e.Message);
            }
        }

        static void Prefix()
        {
            try
            {
                _sw.Reset(); _sw.Start(); _jointAt = -1; _parts = 0; _verts = 0; _armed = true;
                Plugin.Log.LogInfo($"[LoadTimer] {DateTime.Now:yyyy-MM-dd HH:mm:ss} load started.");
                // A new level load always re-arms the once-per-ship dumps (going ship -> ship never passes through Hab).
                SpawnDiagnostics.OnLeftLevel(); BayGeometry.OnLeftLevel();
            }
            catch { }
        }

        // Called from JointBudget's hook. The ship's joint pass is the one with the most parts.
        internal static void OnJointing(int largestVerts, int parts)
        {
            if (!_armed || parts <= _parts) return;
            if (_jointAt < 0 || parts > _parts) _jointAt = _sw.Elapsed.TotalSeconds;
            _parts = parts; _verts = largestVerts;
        }

        internal static void OnGameplay()
        {
            if (!_armed) return;
            _armed = false;
            try
            {
                double total = _sw.Elapsed.TotalSeconds;
                string toJoint = _jointAt >= 0 ? _jointAt.ToString("F1") : "";
                string jointToLive = _jointAt >= 0 ? (total - _jointAt).ToString("F1") : "";
                Plugin.Log.LogInfo($"[LoadTimer] load took {total:F1} s (to joint pass {(toJoint == "" ? "?" : toJoint)} s, joint pass to live {(jointToLive == "" ? "?" : jointToLive)} s), {_parts} parts, largest mesh {_verts} verts.");
                var dir = Path.GetDirectoryName(Plugin.OutputPath.Value);
                if (string.IsNullOrEmpty(dir)) return;
                var path = Path.Combine(dir, "load_times.csv");
                if (!File.Exists(path)) File.WriteAllText(path, "When,TotalSeconds,ToJointingSeconds,JointingToLiveSeconds,Parts,LargestMeshVerts" + Environment.NewLine);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{total:F1},{toJoint},{jointToLive},{_parts},{_verts}" + Environment.NewLine);
            }
            catch (Exception e)
            {
                try { Plugin.Log.LogWarning("[LoadTimer] could not write: " + e.Message); } catch { }
            }
        }
    }
}
