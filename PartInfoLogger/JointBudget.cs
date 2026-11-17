using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;

namespace PartInfoLogger
{
    // The game's jointing step allocates NativeList<float2>(largestVertexCount * numParts) per worker
    // (JointHelper.JointingWorkerData ctor). When largestVertexCount * numParts * 8 reaches 2^31 the byte
    // size overflows int and the game dies with "System out of memory" before any other diagnostic can
    // run. This logs the two inputs just before that allocation, to BepInEx's log and - because the log
    // may not be flushed before a native crash - straight to joint_budget.txt next to OutputPath.
    //
    // Logging only. Patched manually (not via PatchAll) inside a try/catch so a missing/renamed target
    // can never abort the rest of the plugin's patches.
    internal static class JointBudget
    {
        const long Limit = 134217728L;          // 2^27: NativeList rounds capacity up to a power of two, so above this it becomes 2^28 entries x 8 bytes = 2^31 -> int overflow
        static int _lastVerts = -1, _lastParts = -1;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var type = AccessTools.Inner(typeof(JointHelper), "JointingWorkerData");
                var ctor = type?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var p = c.GetParameters();
                        return p.Length >= 2 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(int);
                    });
                if (ctor == null) { Plugin.Log.LogWarning("[JointBudget] JointingWorkerData constructor not found; budget logging disabled."); return; }
                harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(JointBudget), nameof(Prefix)));
                Plugin.Log.LogInfo("[JointBudget] hooked.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[JointBudget] could not hook: " + e.Message);
            }
        }

        // __0 = largestVertexCount, __1 = numParts
        static void Prefix(int __0, int __1)
        {
            try { LoadTimer.OnJointing(__0, __1); } catch { }
            try
            {
                if (__0 == _lastVerts && __1 == _lastParts) return;      // one worker per core: log each distinct pair once
                _lastVerts = __0; _lastParts = __1;
                long product = (long)__0 * __1;
                long maxParts = __0 > 0 ? Limit / __0 : 0;
                string line = $"[JointBudget] {DateTime.Now:yyyy-MM-dd HH:mm:ss} parts={__1} largestMeshVerts={__0} product={product} limit={Limit} " +
                              $"maxPartsAtThisMesh={maxParts} headroomParts={maxParts - __1} {(product > Limit ? "OVER LIMIT - the game will crash here" : "ok")}";
                try
                {
                    var dir = Path.GetDirectoryName(Plugin.OutputPath.Value);
                    if (!string.IsNullOrEmpty(dir)) File.AppendAllText(Path.Combine(dir, "joint_budget.txt"), line + Environment.NewLine);
                }
                catch { /* file is best effort */ }
                if (product > Limit) Plugin.Log.LogError(line); else Plugin.Log.LogInfo(line);
            }
            catch { /* never interfere with jointing */ }
        }
    }
}
