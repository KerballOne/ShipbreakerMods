using UnityEngine;
using UnityEngine.SceneManagement;

namespace NewtonianPhysics
{
    // The background objects the pinning moves, found where they sit in the loaded scenes instead of by searching
    // every loaded object (about 220,000 of them: prefabs, assets and inactive copies too, which also brought the
    // Hab's inactive duplicates, hence the old "_HAB" filters). Paths from the full object dump of the work bay.
    internal static class SceneLookup
    {
        // An active top-level object of a loaded scene, by exact name.
        public static Transform? Root(string name)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var go in scene.GetRootGameObjects())
                    if (go != null && go.name == name && go.activeInHierarchy) return go.transform;
            }
            return null;
        }

        // "Root/Child/Grandchild": the root by name, the rest by Transform.Find; only if active.
        public static Transform? Path(string path)
        {
            int slash = path.IndexOf('/');
            var root = Root(slash < 0 ? path : path.Substring(0, slash));
            if (root == null || slash < 0) return root;
            var found = root.Find(path.Substring(slash + 1));
            return found != null && found.gameObject.activeInHierarchy ? found : null;
        }

        // The objects the pinning knows by name, at their places in the work bay scene.
        public static Transform? Known(string name)
        {
            switch (name)
            {
                case "SunCard": return Path("FX_Salvagetest_Light_01/Salvage Bay Directional  (MAIN)/SunCard");
                case "PRF_PlanetGlow": return Path("PRF_Base_Planets/PRF_PlanetGlow");
                case "GateCard": return Path("PRF_Village/PRF_Village_02/GateCard");
                case "Quad":
                case "Quad (1)":
                case "Quad (2)":
                case "FX_Gate":
                case "FX_Gate_Near": return Path("PRF_Village/FX_Village/" + name);
                default: return Path(name);
            }
        }

        // The active children of an object whose names start with a prefix (the village's station cards).
        public static Transform[] Children(string path, string prefix)
        {
            var parent = Path(path);
            if (parent == null) return new Transform[0];
            var list = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in parent)
                if (child != null && child.gameObject.activeInHierarchy && child.name.StartsWith(prefix)) list.Add(child);
            return list.ToArray();
        }
    }
}
