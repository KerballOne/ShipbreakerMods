using System;
using System.Linq;
using System.Text;
using BBI;
using UnityEngine;

namespace NewtonianPhysics
{
    // One-shot scene dump for figuring out what actually renders Earth/Moon, so we stop
    // guessing at decompiled C# (which only proves a class exists, not that it's used, wired
    // up, active, or the actual thing on screen). Triggered by a hotkey rather than every frame
    // so a single game session/keypress gives a complete answer instead of trickling one log
    // line at a time across repeated relaunches.
    internal static class SceneDiagnostics
    {
        public static void Tick()
        {
            if (!Plugin.ConfigEnabled.Value)
                return;

            if (Plugin.ConfigSceneDumpKey.Value.IsDown())
                Dump();
        }

        private static void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== NewtonianPhysics SceneDiagnostics dump ====");

            // 1) Every _3DBackgroundCam / _3DBackgroundDrawer, active or not, with full hierarchy path.
            sb.AppendLine("-- _3DBackgroundCam instances (Resources.FindObjectsOfTypeAll) --");
            var bgCams = Resources.FindObjectsOfTypeAll<_3DBackgroundCam>();
            sb.AppendLine($"count={bgCams.Length}");
            foreach (var cam in bgCams)
                sb.AppendLine(DescribeComponent(cam, $"ScaleFactor={cam.ScaleFactor}"));

            sb.AppendLine("-- _3DBackgroundDrawer instances (Resources.FindObjectsOfTypeAll) --");
            var bgDrawers = Resources.FindObjectsOfTypeAll<_3DBackgroundDrawer>();
            sb.AppendLine($"count={bgDrawers.Length}");
            foreach (var drawer in bgDrawers)
                sb.AppendLine(DescribeComponent(drawer, ""));

            // 2) Every Camera in memory, active or not - to see what's actually compositing the view.
            sb.AppendLine("-- All Camera components (Resources.FindObjectsOfTypeAll) --");
            var cameras = Resources.FindObjectsOfTypeAll<Camera>();
            sb.AppendLine($"count={cameras.Length}");
            foreach (var cam in cameras)
            {
                sb.AppendLine(DescribeComponent(cam,
                    $"depth={cam.depth} cullingMask={cam.cullingMask} targetTexture={(cam.targetTexture != null ? cam.targetTexture.name : "null")} clearFlags={cam.clearFlags} fov={cam.fieldOfView} pos={cam.transform.position}"));
            }

            // 3) Any GameObject anywhere (including inactive/prefab-in-memory) whose name hints
            // at Earth/Moon/planet/sky/background, so we're not locked into the _3DBackgroundCam
            // theory if that turns out to be unused or wrong.
            sb.AppendLine("-- GameObjects with suggestive names (Resources.FindObjectsOfTypeAll<Transform>) --");
            string[] keywords = { "earth", "moon", "planet", "sky", "background", "backdrop", "parallax" };
            var allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
            var matches = allTransforms
                .Where(t => t != null && t.gameObject != null &&
                            keywords.Any(k => t.gameObject.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToArray();
            sb.AppendLine($"count={matches.Length}");
            foreach (var t in matches)
                sb.AppendLine(DescribeRendererCandidate(t));

            // 4) Size-based scan, independent of naming: the real Earth/Moon meshes should be by
            // far the largest render bounds in the scene, and (per user's own in-editor check)
            // have no Collider since they're pure background visuals, not physics objects. This
            // catches the right object even if it's misnamed or not caught by the keyword list above.
            sb.AppendLine("-- Top 15 largest Renderer bounds, no Collider (Resources.FindObjectsOfTypeAll<Renderer>) --");
            var allRenderers = Resources.FindObjectsOfTypeAll<Renderer>();
            var biggestNoCollider = allRenderers
                .Where(r => r != null && r.gameObject != null && r.GetComponent<Collider>() == null)
                .OrderByDescending(r => r.bounds.size.sqrMagnitude)
                .Take(15);
            foreach (var r in biggestNoCollider)
                sb.AppendLine(DescribeRendererCandidate(r.transform, r));

            sb.AppendLine("==== end dump ====");

            string text = sb.ToString();
            Plugin.Log.LogInfo(text);
        }

        private static string DescribeRendererCandidate(Transform t, Renderer? r = null)
        {
            if (t == null)
                return "null";
            r ??= t.GetComponent<Renderer>();
            var components = t.GetComponents<Component>().Select(c => c == null ? "null" : c.GetType().Name);
            bool hasCollider = t.GetComponent<Collider>() != null;
            string boundsInfo = r != null ? $"boundsSize={r.bounds.size}" : "boundsSize=<no Renderer>";
            return $"path={GetFullPath(t)} active={t.gameObject.activeInHierarchy} pos={t.position} scale={t.localScale} hasCollider={hasCollider} {boundsInfo} components=[{string.Join(",", components)}]";
        }

        private static string DescribeComponent(Component c, string extra)
        {
            if (c == null)
                return "null";
            return $"path={GetFullPath(c.transform)} active={c.gameObject.activeInHierarchy} instanceId={c.GetInstanceID()} {extra}";
        }

        private static string GetFullPath(Transform t)
        {
            if (t == null)
                return "<null>";
            string path = t.name;
            Transform parent = t.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
