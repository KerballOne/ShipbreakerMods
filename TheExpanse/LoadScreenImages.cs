using System;
using System.Collections.Generic;
using System.IO;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.UI;

namespace TheExpanse
{
    // Shows a picture on the loading screen.
    //
    // The loading screen (PRF_UI_LoadScreen, under the persistent SceneLoader object) is a canvas drawn through its own
    // camera, with a post-effects volume that gives everything on it the CRT / scanline look. Under its 'LoadScreen' panel:
    //   'Image - Background - Clouds'   dark teal gradient stretched over the whole screen
    //   'Image - Background - UI'       fixed 1920x1080 panel with the LYNX logo, the word "Salvage" and a few readout marks
    //   loading bar, tooltip box, loading / press prompt along the bottom
    //
    // Each time the screen appears, one image is picked at random from the LoadScreens folder beside the DLL and shown on
    // a layer of its own, just above the gradient and below everything else, shown whole or enlarged to cover the screen (see
    // the Zoom setting). The logo panel is hidden while a picture shows. Nothing of the game's own art is modified.
    internal static class LoadScreenImages
    {
        const string LayerName = "TheExpanse - Image";
        const string GradientName = "Image - Background - Clouds";
        const string LogoName = "Image - Background - UI";

        static Transform? sRoot;            // PRF_UI_LoadScreen
        static RectTransform? sPanel;       // 'LoadScreen'
        static GameObject? sLogo;
        static RawImage? sLayer;
        static RawImage? sBackdrop;
        const string BackdropName = "TheExpanse - Backdrop";
        static Texture2D? sTexture;
        static bool sWasShowing, sLogoHidden;
        static string sLast = "";
        static float sNextSearch;
        static readonly System.Random sRandom = new System.Random();

        static string Folder => Path.Combine(Path.GetDirectoryName(Plugin.Instance.Info.Location)!, Plugin.LoadScreenFolder.Value);

        // Called every frame.
        internal static void Tick()
        {
            if (sRoot == null)
            {
                if (Time.unscaledTime < sNextSearch) return;
                sNextSearch = Time.unscaledTime + 1f;
                if (!Find()) return;
            }

            bool showing = sRoot!.gameObject.activeInHierarchy;
            if (showing && !sWasShowing) OnShown();
            else if (!showing && sWasShowing) OnHidden();
            sWasShowing = showing;
            if (showing && sLayer != null && sLayer.enabled) Fit();
        }

        static bool Find()
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<SplashScreenController>())
            {
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                Transform root = c.transform;
                for (var t = c.transform; t != null; t = t.parent)
                    if (t.name.StartsWith("PRF_UI_LoadScreen", StringComparison.Ordinal)) root = t;
                Transform? gradient = null, logo = null;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == GradientName) gradient = t;
                    else if (t.name == LogoName) logo = t;
                }
                if (gradient == null || !(gradient.parent is RectTransform panel))
                {
                    Plugin.Log.LogWarning($"[LoadScreenImages] loading screen found but it has no '{GradientName}' layer; pictures disabled.");
                    sNextSearch = float.MaxValue;
                    return false;
                }
                sRoot = root; sPanel = panel; sLogo = logo != null ? logo.gameObject : null;
                sWasShowing = false;
                Plugin.Log.LogInfo($"[LoadScreenImages] loading screen found; pictures come from '{Folder}'.");
                return true;
            }
            return false;
        }

        static void OnShown()
        {
            string? file = Plugin.LoadScreenEnabled.Value ? Pick() : null;
            if (file == null) { Clear(); return; }

            Texture2D? tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = "TheExpanse_" + Path.GetFileNameWithoutExtension(file) };
                if (!tex.LoadImage(File.ReadAllBytes(file), true)) throw new InvalidDataException("not a PNG or JPG Unity can decode");
                tex.wrapMode = TextureWrapMode.Clamp;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[LoadScreenImages] could not load '{file}': {ex.Message}");
                if (tex != null) UnityEngine.Object.Destroy(tex);
                Clear();
                return;
            }

            if (sLayer == null)
            {
                var go = new GameObject(LayerName, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                go.layer = sPanel!.gameObject.layer;
                var rt = (RectTransform)go.transform;
                rt.SetParent(sPanel, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);       // centred; Fit() sizes it
                rt.anchoredPosition = Vector2.zero;
                sLayer = go.GetComponent<RawImage>();
                sLayer.raycastTarget = false;
                sLayer.color = Color.white;
            }
            if (sBackdrop == null)
            {
                // A plain black sheet over the whole screen, so the teal gradient does not show beside the picture.
                var go = new GameObject(BackdropName, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                go.layer = sPanel!.gameObject.layer;
                var rt = (RectTransform)go.transform;
                rt.SetParent(sPanel, false);
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                sBackdrop = go.GetComponent<RawImage>();
                sBackdrop.raycastTarget = false;
                sBackdrop.color = Color.black;
            }
            // Just above the gradient, below the logo panel and the rest: gradient, black sheet, picture.
            var gradient = sPanel!.Find(GradientName);
            int at = gradient != null ? gradient.GetSiblingIndex() + 1 : 0;
            sBackdrop.transform.SetSiblingIndex(at);
            sLayer.transform.SetSiblingIndex(sBackdrop.transform.GetSiblingIndex() + 1);
            sBackdrop.enabled = Plugin.LoadScreenBlackBackground.Value;

            if (sTexture != null) UnityEngine.Object.Destroy(sTexture);
            sTexture = tex;
            sLayer.texture = tex;
            sLayer.enabled = true;
            Fit();

            if (Plugin.LoadScreenHideLogo.Value && sLogo != null && sLogo.activeSelf) { sLogo.SetActive(false); sLogoHidden = true; }
            sLast = file;
            if (Plugin.DebugLogging.Value)
                Plugin.Log.LogInfo($"[LoadScreenImages] showing '{Path.GetFileName(file)}' ({tex.width}x{tex.height}), logo {(sLogoHidden ? "hidden" : "left on")}.");
        }

        static void OnHidden() => Clear();

        // Takes the picture off and puts the logo back.
        static void Clear()
        {
            if (sLayer != null) { sLayer.enabled = false; sLayer.texture = null; }
            if (sBackdrop != null) sBackdrop.enabled = false;
            if (sTexture != null) { UnityEngine.Object.Destroy(sTexture); sTexture = null; }
            if (sLogoHidden && sLogo != null) sLogo.SetActive(true);
            sLogoHidden = false;
        }

        // A random image from the folder, avoiding the one shown last time when there is a choice.
        static string? Pick()
        {
            string dir = Folder;
            if (!Directory.Exists(dir)) return null;
            var files = new List<string>();
            foreach (var f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") files.Add(f);
            }
            if (files.Count == 0) return null;
            if (files.Count > 1) files.Remove(sLast);
            return files[sRandom.Next(files.Count)];
        }

        // Sizes the picture on the panel. With Stretch on it is simply pulled to the screen's shape. Otherwise its
        // proportions are kept: zoom 0 shows the whole picture (with the backdrop beside it where the shapes differ), zoom 1
        // enlarges it until the screen is covered, in between is in between, and whatever overflows the screen is cropped
        // equally from both sides.
        static void Fit()
        {
            if (sLayer == null || sTexture == null || sPanel == null) return;
            var panel = sPanel.rect.size;
            if (panel.x < 1f || panel.y < 1f) panel = new Vector2(1080f * Screen.width / Mathf.Max(1, Screen.height), 1080f);
            if (Plugin.LoadScreenStretch.Value)
            {
                var full = new Rect(0f, 0f, 1f, 1f);
                if (sLayer.rectTransform.sizeDelta != panel) sLayer.rectTransform.sizeDelta = panel;
                if (sLayer.uvRect != full) sLayer.uvRect = full;
                return;
            }
            float w = sTexture.width, h = Mathf.Max(1, sTexture.height);
            float fit = Mathf.Min(panel.x / w, panel.y / h), fill = Mathf.Max(panel.x / w, panel.y / h);
            float scale = Mathf.Lerp(fit, fill, Mathf.Clamp01(Plugin.LoadScreenZoom.Value));
            var shown = new Vector2(w * scale, h * scale);                                   // picture size in panel units
            var size = new Vector2(Mathf.Min(shown.x, panel.x), Mathf.Min(shown.y, panel.y));
            var uvSize = new Vector2(size.x / shown.x, size.y / shown.y);
            var uv = new Rect((1f - uvSize.x) * 0.5f, (1f - uvSize.y) * 0.5f, uvSize.x, uvSize.y);

            var rt = sLayer.rectTransform;
            if (rt.sizeDelta != size) rt.sizeDelta = size;
            if (sLayer.uvRect != uv) sLayer.uvRect = uv;
        }
    }
}
