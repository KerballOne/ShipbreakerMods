using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace TheExpanse
{
    // Replaces the main menu's backdrop picture.
    //
    // The menu has no 3D scene behind it: the hex station over the planet is one flat UI picture, the texture
    // 'TitleScreen_Background_v2' (2048 x 1159). Whatever UI element shows that texture is given a replacement picture
    // instead, one chosen at random per game launch from the TitleScreens folder beside the DLL; the element itself, and
    // so its size and position, is left alone.
    internal static class TitleScreen
    {
        const string StockTexture = "TitleScreen_Background_v2";

        static Texture2D? sImage;
        static Sprite? sSprite;
        static Graphic? sTarget;
        static Texture? sStockTexture;
        static Sprite? sStockSprite;
        static float sNextScan;
        static bool sLoadFailed;
        static string sPicked = "";

        // Called every frame; does real work about once a second.
        internal static void Tick()
        {
            if (Time.unscaledTime < sNextScan) return;
            sNextScan = Time.unscaledTime + 1f;

            if (!Plugin.TitleEnabled.Value) { Restore(); return; }
            if (sTarget != null) { Set(sTarget); return; }             // already found: just make sure it still shows ours

            foreach (var g in Resources.FindObjectsOfTypeAll<Graphic>())
            {
                if (g == null || !g.gameObject.scene.IsValid()) continue;
                if (g is RawImage ri && ri.texture != null && ri.texture.name == StockTexture) { sStockTexture = ri.texture; }
                else if (g is Image im && im.sprite != null && im.sprite.texture != null && im.sprite.texture.name == StockTexture) { sStockSprite = im.sprite; }
                else continue;

                if (!Load()) return;
                sTarget = g;
                Set(g);
                Plugin.Log.LogInfo($"[TitleScreen] menu backdrop '{g.name}' ({g.GetType().Name}) now shows {sPicked} ({sImage!.width}x{sImage.height}).");
                return;
            }
        }

        static void Set(Graphic g)
        {
            if (sImage == null) return;
            if (g is RawImage ri) { if (ri.texture != sImage) ri.texture = sImage; }
            else if (g is Image im)
            {
                if (sSprite == null)
                {
                    // Same pivot and the same on-screen size as the stock sprite, whatever the replacement's pixel size.
                    var stock = sStockSprite!;
                    float ppu = stock.pixelsPerUnit * sImage.width / Mathf.Max(1f, stock.rect.width);
                    var pivot = new Vector2(stock.pivot.x / Mathf.Max(1f, stock.rect.width), stock.pivot.y / Mathf.Max(1f, stock.rect.height));
                    sSprite = Sprite.Create(sImage, new Rect(0, 0, sImage.width, sImage.height), pivot, ppu);
                    sSprite.name = "TheExpanse_TitleScreen";
                }
                if (im.sprite != sSprite) im.sprite = sSprite;
            }
        }

        static bool Load()
        {
            if (sImage != null) return true;
            if (sLoadFailed) return false;
            // One picture per game launch, picked at random from the folder.
            string dir = Path.Combine(Path.GetDirectoryName(Plugin.Instance.Info.Location)!, Plugin.TitleFolder.Value);
            var files = new System.Collections.Generic.List<string>();
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") files.Add(f);
                }
            if (files.Count == 0)
            {
                Plugin.Log.LogInfo($"[TitleScreen] no pictures in '{dir}'; menu backdrop left as it is.");
                sLoadFailed = true;
                return false;
            }
            string path = files[new System.Random().Next(files.Count)];
            sPicked = $"{Path.GetFileName(path)} (1 of {files.Count})";
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = "TheExpanse_TitleScreen" };
                if (!tex.LoadImage(File.ReadAllBytes(path), true)) throw new InvalidDataException("not a PNG or JPG Unity can decode");
                tex.wrapMode = TextureWrapMode.Clamp;
                sImage = tex;
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[TitleScreen] could not load '{path}': {ex.Message}");
                sLoadFailed = true;
                return false;
            }
        }

        static void Restore()
        {
            if (sTarget is RawImage ri && sStockTexture != null && ri.texture == sImage) ri.texture = sStockTexture;
            else if (sTarget is Image im && sStockSprite != null && im.sprite == sSprite) im.sprite = sStockSprite;
            sTarget = null;
        }
    }
}
