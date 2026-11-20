using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace TheExpanse
{
    // One-off export of the background planet cards (Earth = PRF_PlanetCard_Octane, its glow and the Moon), as a template
    // for replacement art. Writes into <plugin folder>\PlanetExport:
    //   report.txt          every renderer under PRF_Base_Planets / PRF_Base_Planets_HAB with its transform, mesh and each
    //                       material's shader, keywords and full property list
    //   <texture>.png       every texture those materials use (read back through a render target, so non-readable ones work)
    //   <mesh>.obj          the card meshes with their UVs, where the game keeps them readable
    internal static class PlanetExport
    {
        const string RootPrefix = "PRF_Base_Planets";

        static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";
        static string V(Vector4 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)}, {F(v.w)})";

        // Returns true once something was exported.
        internal static bool Run(string outDir)
        {
            var roots = new List<Transform>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t != null && t.gameObject.scene.IsValid() && t.name.StartsWith(RootPrefix, StringComparison.Ordinal) &&
                    (t.parent == null || !t.parent.name.StartsWith(RootPrefix, StringComparison.Ordinal)))
                    roots.Add(t);
            if (roots.Count == 0) return false;

            Directory.CreateDirectory(outDir);
            var sb = new StringBuilder();
            var textures = new Dictionary<int, Texture>();
            var meshes = new Dictionary<int, Mesh>();

            foreach (var root in roots)
            {
                sb.AppendLine($"=== root '{root.name}'  active={root.gameObject.activeInHierarchy}  pos={V(root.position)}  euler={V(root.eulerAngles)}  scale={V(root.lossyScale)}");
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    var t = r.transform;
                    sb.AppendLine($"--- {r.GetType().Name} '{r.name}'  active={r.gameObject.activeInHierarchy} enabled={r.enabled} staticBatch={r.isPartOfStaticBatch} layer={LayerMask.LayerToName(r.gameObject.layer)}");
                    sb.AppendLine($"    pos={V(t.position)}  euler={V(t.eulerAngles)}  lossyScale={V(t.lossyScale)}");
                    sb.AppendLine($"    worldBounds centre={V(r.bounds.center)} size={V(r.bounds.size)}  shadows={r.shadowCastingMode}");
                    var mf = r.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh != null)
                    {
                        sb.AppendLine($"    mesh '{mesh.name}' verts={mesh.vertexCount} subMeshes={mesh.subMeshCount} readable={mesh.isReadable} bounds centre={V(mesh.bounds.center)} size={V(mesh.bounds.size)}");
                        meshes[mesh.GetInstanceID()] = mesh;
                    }
                    foreach (var m in r.sharedMaterials) Describe(m, sb, textures);
                }
                foreach (var l in root.GetComponentsInChildren<Light>(true))
                    sb.AppendLine($"--- Light '{l.name}' type={l.type} active={l.gameObject.activeInHierarchy} colour={V((Vector4)l.color)} intensity={F(l.intensity)} euler={V(l.transform.eulerAngles)}");
                sb.AppendLine();
            }

            sb.AppendLine("=== textures");
            foreach (var tex in textures.Values)
            {
                string file = Safe(tex.name) + ".png";
                string result;
                try { result = SavePng(tex, Path.Combine(outDir, file)) ? file : "not a 2D texture, skipped"; }
                catch (Exception ex) { result = "FAILED: " + ex.Message; }
                sb.AppendLine($"    '{tex.name}' {tex.GetType().Name} {tex.width}x{tex.height} format={(tex is Texture2D t2 ? t2.format.ToString() : tex.graphicsFormat.ToString())} mips={tex.mipmapCount} readable={tex.isReadable} wrap={tex.wrapMode} -> {result}");
            }

            sb.AppendLine();
            sb.AppendLine("=== meshes");
            foreach (var mesh in meshes.Values)
            {
                string result = "not readable, not exported";
                if (mesh.isReadable)
                {
                    try { string file = Safe(mesh.name) + ".obj"; SaveObj(mesh, Path.Combine(outDir, file)); result = file; }
                    catch (Exception ex) { result = "FAILED: " + ex.Message; }
                }
                sb.AppendLine($"    '{mesh.name}' -> {result}");
            }

            // The bay's ambient light comes from the sky cubemap, not from the planet card; list what is loaded.
            sb.AppendLine();
            sb.AppendLine("=== cubemaps in memory (sky lighting candidates)");
            foreach (var c in Resources.FindObjectsOfTypeAll<Cubemap>())
                if (c != null) sb.AppendLine($"    '{c.name}' {c.width}x{c.height} format={c.format}");

            File.WriteAllText(Path.Combine(outDir, "report.txt"), sb.ToString());
            Plugin.Log.LogInfo($"[PlanetExport] {roots.Count} planet root(s), {textures.Count} texture(s), {meshes.Count} mesh(es) -> {outDir}");
            return true;
        }

        static void Describe(Material? m, StringBuilder sb, Dictionary<int, Texture> textures)
        {
            if (m == null) { sb.AppendLine("    material <null>"); return; }
            var sh = m.shader;
            sb.AppendLine($"    material '{m.name}' shader='{sh.name}' renderQueue={m.renderQueue} keywords=[{string.Join(" ", m.shaderKeywords)}]");
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                string name = sh.GetPropertyName(i);
                string desc = sh.GetPropertyDescription(i);
                string value;
                switch (sh.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color: value = "colour " + V((Vector4)m.GetColor(name)); break;
                    case ShaderPropertyType.Vector: value = "vector " + V(m.GetVector(name)); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range: value = "float " + F(m.GetFloat(name)); break;
                    case ShaderPropertyType.Texture:
                        var tex = m.GetTexture(name);
                        if (tex == null) { value = "texture <none>"; break; }
                        textures[tex.GetInstanceID()] = tex;
                        var sc = m.GetTextureScale(name); var of = m.GetTextureOffset(name);
                        value = $"texture '{tex.name}' {tex.width}x{tex.height} tiling=({F(sc.x)}, {F(sc.y)}) offset=({F(of.x)}, {F(of.y)})";
                        break;
                    default: value = "?"; break;
                }
                sb.AppendLine($"        {name} [{desc}] = {value}");
            }
        }

        static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length == 0 ? "unnamed" : name;
        }

        // Copies the texture through an sRGB render target so the PNG holds the same colours as the source image.
        static bool SavePng(Texture tex, string path)
        {
            if (tex.dimension != TextureDimension.Tex2D) return false;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Texture2D? copy = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                copy.Apply();
                File.WriteAllBytes(path, copy.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
            return true;
        }

        static void SaveObj(Mesh mesh, string path)
        {
            var sb = new StringBuilder();
            var verts = mesh.vertices; var uvs = mesh.uv; var normals = mesh.normals; var tris = mesh.triangles;
            foreach (var v in verts) sb.AppendLine($"v {F(v.x)} {F(v.y)} {F(v.z)}");
            foreach (var u in uvs) sb.AppendLine($"vt {F(u.x)} {F(u.y)}");
            foreach (var n in normals) sb.AppendLine($"vn {F(n.x)} {F(n.y)} {F(n.z)}");
            bool hasUv = uvs.Length == verts.Length, hasN = normals.Length == verts.Length;
            string Idx(int i) { int k = i + 1; return hasUv ? (hasN ? $"{k}/{k}/{k}" : $"{k}/{k}") : (hasN ? $"{k}//{k}" : k.ToString()); }
            for (int i = 0; i + 2 < tris.Length; i += 3) sb.AppendLine($"f {Idx(tris[i])} {Idx(tris[i + 1])} {Idx(tris[i + 2])}");
            File.WriteAllText(path, sb.ToString());
        }
    }
}
