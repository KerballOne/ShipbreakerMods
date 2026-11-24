using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

namespace TheExpanse
{
    // Measures where one of the game's effects actually draws relative to its own origin, so it can be centered (the
    // ball) or set with its base at the origin and its long axis along a chosen direction (the flare). The measure is
    // the renderers' bounds, which for a VFX graph are the bounds its author set; good enough to find an offset.
    internal static class FxShape
    {
        internal struct Shape
        {
            internal Bounds Local;          // in the effect root's own space (its own scale included)
            internal Vector3 LongAxis;      // unit, in root space: the longest side of the bounds, pointing away from the origin
            internal Vector3 Base;          // root space: the end of the bounds on the long axis nearest the origin
        }

        static readonly List<VFXExposedProperty> sExposed = new List<VFXExposedProperty>();

        // False while nothing has drawn yet (bounds still empty).
        internal static bool TryMeasure(GameObject shown, out Shape shape)
        {
            shape = default;
            var root = shown.transform;
            bool any = false;
            var local = new Bounds();
            foreach (var renderer in shown.GetComponentsInChildren<Renderer>(false))
            {
                var world = renderer.bounds;
                if (world.size.sqrMagnitude < 1e-6f) continue;
                var min = world.min; var max = world.max;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? min.x : max.x, (c & 2) == 0 ? min.y : max.y, (c & 4) == 0 ? min.z : max.z);
                    var p = root.InverseTransformPoint(corner);
                    if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                    else local.Encapsulate(p);
                }
            }
            if (!any) return false;
            var size = local.size;
            // a cube centered on the origin is the author's placeholder box, not where it draws: nothing to learn
            if (Mathf.Abs(size.x - size.y) < 0.01f && Mathf.Abs(size.y - size.z) < 0.01f && local.center.sqrMagnitude < 0.0001f) return false;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            var dir = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            if (local.center[axis] < -0.01f) dir = -dir;
            shape.Local = local;
            shape.LongAxis = dir;
            shape.Base = local.center - dir * (size[axis] * 0.5f);
            return true;
        }

        internal static string Describe(GameObject shown, bool measured, Shape shape)
        {
            var root = shown.transform;
            var text = new System.Text.StringBuilder();
            text.Append(measured
                ? $"drawn center {F(shape.Local.center)}, size {F(shape.Local.size)}, long axis {F(shape.LongAxis)}, base {F(shape.Base)}"
                : "nothing drawn yet");
            text.Append($"; root scale {F(root.localScale)}");
            int n = 0;
            foreach (var child in shown.GetComponentsInChildren<Transform>(true))
            {
                if (child == root || n++ >= 6) continue;
                text.Append($"; child {child.name} at {F(root.InverseTransformPoint(child.position))} fwd {F(root.InverseTransformDirection(child.forward))}");
            }
            foreach (var effect in shown.GetComponentsInChildren<VisualEffect>(true))
            {
                if (effect.visualEffectAsset == null) continue;
                sExposed.Clear();
                effect.visualEffectAsset.GetExposedProperties(sExposed);
                if (sExposed.Count == 0) continue;
                text.Append($"; {effect.visualEffectAsset.name} settings:");
                foreach (var p in sExposed)
                {
                    string name = p.name, value = "?";
                    if (effect.HasVector3(name)) value = F(effect.GetVector3(name));
                    else if (effect.HasFloat(name)) value = effect.GetFloat(name).ToString("0.###");
                    else if (effect.HasInt(name)) value = effect.GetInt(name).ToString();
                    else if (effect.HasBool(name)) value = effect.GetBool(name).ToString();
                    else if (effect.HasVector4(name)) value = effect.GetVector4(name).ToString("0.##");
                    else if (effect.HasVector2(name)) value = effect.GetVector2(name).ToString("0.##");
                    text.Append($" {name}({p.type?.Name})={value}");
                }
            }
            return text.ToString();
        }

        // What the effect draws with: each renderer's materials, their shaders, and the color/value/texture settings
        // on them, to see whether the flames can be tinted.
        internal static string DescribeMaterials(GameObject shown)
        {
            var text = new System.Text.StringBuilder();
            foreach (var renderer in shown.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                text.Append($" | {renderer.GetType().Name} on {renderer.name}: {materials.Length} material(s)");
                foreach (var material in materials)
                {
                    if (material == null) { text.Append("; (none)"); continue; }
                    var shader = material.shader;
                    text.Append($"; {material.name} ({(shader != null ? shader.name : "no shader")}):");
                    if (shader == null) continue;
                    for (int p = 0; p < shader.GetPropertyCount(); p++)
                    {
                        string name = shader.GetPropertyName(p);
                        switch (shader.GetPropertyType(p))
                        {
                            case UnityEngine.Rendering.ShaderPropertyType.Color: text.Append($" {name}={material.GetColor(name)}"); break;
                            case UnityEngine.Rendering.ShaderPropertyType.Vector: text.Append($" {name}={material.GetVector(name)}"); break;
                            case UnityEngine.Rendering.ShaderPropertyType.Float:
                            case UnityEngine.Rendering.ShaderPropertyType.Range: text.Append($" {name}={material.GetFloat(name):0.###}"); break;
                            case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                var texture = material.GetTexture(name);
                                text.Append($" {name}={(texture != null ? texture.name : "none")}"); break;
                        }
                    }
                }
            }
            return text.Length == 0 ? " no renderers" : text.ToString();
        }

        static string F(Vector3 v) => $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##})";

        // Effects like FX_Fire_01 spawn on points read from a "PositionMap" texture (16 x 16, RGBAHalf, one local
        // position per pixel), which the game bakes from the mesh of the part that burns (PointCacheUtilities), with
        // "Size" set to that mesh's bounds. The prefab's own map belongs to some other mesh, so it draws off to one
        // side. Give it points in a ball of the given diameter, centered on its origin, instead.
        static readonly Dictionary<float, Texture2D> sSpheres = new Dictionary<float, Texture2D>();

        // A plain glowing material. Shader.Find cannot see the game's HDRP shaders (they are not in its always-included
        // list), so look among the shaders already loaded instead.
        static Shader? sUnlit;
        internal static Material GlowMaterial(Color color, float brightness)
        {
            if (sUnlit == null)
            {
                foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
                    if (shader != null && shader.name == "HDRP/Unlit") { sUnlit = shader; break; }
                if (sUnlit == null)
                    foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
                        if (shader != null && shader.name == "HDRP/Lit") { sUnlit = shader; break; }
                Plugin.Log.LogInfo(sUnlit != null ? $"[Reactor] plain shapes drawn with {sUnlit.name}." : "[Reactor] no HDRP shader found loaded; plain shapes will not show.");
            }
            var material = sUnlit != null ? new Material(sUnlit) : new Material(Shader.Find("Hidden/InternalErrorShader"));
            material.name = "TheExpanse_Glow";
            PaintGlow(material, color, brightness);
            return material;
        }

        internal static void PaintGlow(Material material, Color color, float brightness)
        {
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", color * brightness);
            if (material.HasProperty("_EmissiveExposureWeight")) material.SetFloat("_EmissiveExposureWeight", 0f);
        }

        // sizes false: only move the points, leave Size/Radius as the effect's own (a packed glow keeps its look)
        internal static bool GiveSphere(GameObject shown, float diameter, bool sizes = true)
        {
            diameter = Mathf.Max(0.01f, diameter);
            bool any = false;
            foreach (var effect in shown.GetComponentsInChildren<VisualEffect>(true))
            {
                if (effect.visualEffectAsset == null) continue;
                // every system in the graph: "PositionMap", "PositionMap 1", ... and "Size", "Size 1", ...; effects
                // sized by a radius instead of points get the same ball
                sExposed.Clear();
                effect.visualEffectAsset.GetExposedProperties(sExposed);
                foreach (var p in sExposed)
                {
                    string name = p.name;
                    if (name.StartsWith("PositionMap") && effect.HasTexture(name))
                    {
                        effect.SetTexture(name, Sphere(diameter));
                        any = true;
                    }
                    else if (!sizes) continue;
                    else if (name.StartsWith("Size") && effect.HasVector3(name)) { effect.SetVector3(name, Vector3.one * diameter); any = true; }
                    else if (name.StartsWith("Radius") && effect.HasFloat(name)) { effect.SetFloat(name, diameter * 0.5f); any = true; }
                }
            }
            return any;
        }

        static Texture2D Sphere(float diameter)
        {
            if (sSpheres.TryGetValue(diameter, out var cached) && cached != null) return cached;
            var random = new System.Random(1234);
            var points = new Color[256];
            float radius = diameter * 0.5f;
            for (int i = 0; i < points.Length; i++)
            {
                // even over the sphere, mostly near the surface, some inside so it reads as a ball, not a shell
                float z = (float)(random.NextDouble() * 2.0 - 1.0);
                float a = (float)(random.NextDouble() * Mathf.PI * 2.0);
                float s = Mathf.Sqrt(1f - z * z);
                float r = radius * (0.55f + 0.45f * Mathf.Sqrt((float)random.NextDouble()));
                points[i] = new Color(Mathf.Cos(a) * s * r, Mathf.Sin(a) * s * r, z * r, 0f);
            }
            var texture = new Texture2D(16, 16, TextureFormat.RGBAHalf, false) { name = "TheExpanse_PlasmaPoints" };
            texture.SetPixels(points);
            texture.Apply();
            sSpheres[diameter] = texture;
            return texture;
        }

        // A plume along +Z, for FX_Fire_01 to burn on as a flare: from `start` (inside the ball, so it grows out of
        // it) for `length` meters, `width` across at its base and narrowing to a tip. Cached by size in 0.1 m steps.
        static readonly Dictionary<long, Texture2D> sPlumes = new Dictionary<long, Texture2D>();

        internal static void GivePlume(GameObject shown, float start, float length, float width)
        {
            start = Mathf.Round(start * 10f) / 10f;
            length = Mathf.Max(0.1f, Mathf.Round(length * 10f) / 10f);
            width = Mathf.Max(0.1f, Mathf.Round(width * 10f) / 10f);
            long key = ((long)(start * 10f) << 40) | ((long)(length * 10f) << 20) | (long)(width * 10f);
            if (!sPlumes.TryGetValue(key, out var texture) || texture == null)
            {
                var random = new System.Random(4321);
                var points = new Color[256];
                for (int i = 0; i < points.Length; i++)
                {
                    // more near the base than at the tip; a disk across that narrows along the way
                    float u = 1f - Mathf.Sqrt((float)random.NextDouble());
                    float across = width * 0.5f * (1f - 0.8f * u);
                    float r = across * Mathf.Sqrt((float)random.NextDouble());
                    float a = (float)(random.NextDouble() * Mathf.PI * 2.0);
                    points[i] = new Color(Mathf.Cos(a) * r, Mathf.Sin(a) * r, start + length * u, 0f);
                }
                texture = new Texture2D(16, 16, TextureFormat.RGBAHalf, false) { name = "TheExpanse_PlumePoints" };
                texture.SetPixels(points);
                texture.Apply();
                sPlumes[key] = texture;
            }
            foreach (var effect in shown.GetComponentsInChildren<VisualEffect>(true))
            {
                if (effect.HasTexture("PositionMap")) effect.SetTexture("PositionMap", texture);
                if (effect.HasVector3("Size")) effect.SetVector3("Size", new Vector3(width, width, length));
            }
        }

        // FX_Fire_Pipes (#13) is one small glow; packed onto its origin and scaled up, it fills the ball.
        internal const float CoreGlowPoints = 0.05f;    // diameter its points are packed into, m
        internal const float CoreGlowScalePerMeter = 2.7f;     // its transform scale per meter of ball (glow is ~0.37 m)
    }
}
