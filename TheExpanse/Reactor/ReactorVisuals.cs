using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.VFX;

namespace TheExpanse
{
    // What the reactor looks like, built from the game's own effects (picked in the effect gallery):
    //   the ball:     FX_Fire_01 spawning on points in a ball, with FX_Fire_Pipes' glow packed into its middle and
    //                 scaled up to fill it; both pulse;
    //   unstable:     FX_Trail_Fire_01's fire cloud wrapped around the ball.
    // Plus a point light. Until the ball's effect has loaded (or if it cannot load), a plain glowing sphere stands in
    // for it. Nothing here touches the game's objects.
    internal static class ReactorVisuals
    {
        const string BallGuid = "71965b2f0487c9947903596d99098ae9";     // FX_Fire_01
        const string CoreGuid = "d250c5869f68ba04f8fd13eab21ceadf";     // FX_Fire_Pipes.vfx, layered into the ball
        const string CloudGuid = "42bbec3ebf0338d48aca3a7cc6ba1354";    // FX_Trail_Fire_01

        // Each effect sits in a pivot: the pivot is placed and scaled; the effect inside is moved once, after it has
        // drawn, so that what it draws is centered on the pivot.
        sealed class Effect
        {
            internal readonly string Name, Guid;
            internal bool Graph;                // a bare VFX graph asset, not a prefab
            internal bool MaterialsLogged;
            internal GameObject? Pivot, Shown;
            internal bool Loading, Failed, Fitted;
            internal float FitFrom;
            internal int FitTries;
            internal Effect(string name, string guid) { Name = name; Guid = guid; }
        }

        static readonly Effect sBallFx = new Effect("FX_Fire_01", BallGuid);
        static readonly Effect sCoreFx = new Effect("FX_Fire_Pipes", CoreGuid) { Graph = true };
        static readonly Effect sCloudFx = new Effect("FX_Trail_Fire_01", CloudGuid);

        static GameObject? sRoot, sBall;
        static Material? sBallMaterial;
        static HDAdditionalLightData? sLight;
        static float sCloudShown;               // 0..1, how far the unstable fire cloud is in

        internal static bool Built => sRoot != null;

        internal static void Destroy()
        {
            ReactorFeeds.Destroy();
            ReactorSound.Destroy();
            if (sRoot != null) Object.Destroy(sRoot);
            if (sBallMaterial != null) Object.Destroy(sBallMaterial);
            sRoot = sBall = null;
            sBallMaterial = null;
            sLight = null;
            sCloudShown = 0f;
            foreach (var fx in new[] { sBallFx, sCoreFx, sCloudFx })
            {
                fx.Pivot = fx.Shown = null;
                fx.Loading = fx.Failed = fx.Fitted = fx.MaterialsLogged = false;
                fx.FitTries = 0;
            }
        }

        internal static void Build()
        {
            Destroy();
            sRoot = new GameObject("TheExpanse_ReactorPlasma");

            // Stand-in, shown only while the game's effect loads or if it fails.
            sBall = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sBall.name = "Ball";
            Object.Destroy(sBall.GetComponent<Collider>());
            sBall.transform.SetParent(sRoot.transform, false);
            sBallMaterial = FxShape.GlowMaterial(Color.white, 1f);
            var renderer = sBall.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = sBallMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var lightObject = new GameObject("Light");
            lightObject.transform.SetParent(sRoot.transform, false);
            sLight = lightObject.AddHDLight(HDLightTypeAndShape.Point);
            sLight.SetRange(20f);
            sLight.EnableShadows(true);

            Load(sBallFx);
            Load(sCoreFx);
            Load(sCloudFx);
            ReactorFeeds.Build();
        }

        static void Load(Effect fx)
        {
            fx.Loading = true;
            var root = sRoot;
            try
            {
                if (fx.Graph)
                    Addressables.LoadAssetAsync<VisualEffectAsset>(fx.Guid).Completed += handle =>
                    {
                        if (!Loaded(fx, root, handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)) return;
                        var shown = new GameObject();
                        shown.AddComponent<VisualEffect>().visualEffectAsset = handle.Result;
                        Attach(fx, root!, shown);
                    };
                else
                    Addressables.LoadAssetAsync<GameObject>(fx.Guid).Completed += handle =>
                    {
                        if (!Loaded(fx, root, handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)) return;
                        Attach(fx, root!, Object.Instantiate(handle.Result)!);
                    };
            }
            catch (System.Exception ex)
            {
                fx.Loading = false;
                fx.Failed = true;
                Plugin.Log.LogWarning($"[Reactor] {fx.Name} could not be loaded: {ex.Message}");
            }
        }

        static bool Loaded(Effect fx, GameObject? root, bool ok)
        {
            fx.Loading = false;
            if (root == null || root != sRoot) return false;     // torn down while loading
            if (ok) return true;
            fx.Failed = true;
            Plugin.Log.LogWarning($"[Reactor] {fx.Name} could not be loaded; a plain stand-in is drawn instead (if any).");
            return false;
        }

        static void Attach(Effect fx, GameObject root, GameObject shown)
        {
            fx.Pivot = new GameObject("TheExpanse_" + fx.Name + "_Pivot");
            fx.Pivot.transform.SetParent(root.transform, false);
            shown.transform.SetParent(fx.Pivot.transform, false);
            shown.name = "TheExpanse_" + fx.Name;
            fx.Shown = shown;
            fx.FitFrom = Time.time + 0.75f;
            fx.FitTries = 12;
            if (fx == sBallFx && FxShape.GiveSphere(shown, Plugin.ReactorBallSize.Value))
                Plugin.Log.LogInfo($"[Reactor] {fx.Name} spawns on points in a {Plugin.ReactorBallSize.Value:0.#} m ball around its origin.");
            if (fx == sCoreFx && FxShape.GiveSphere(shown, FxShape.CoreGlowPoints, sizes: false))
            {
                float scale = FxShape.CoreGlowScalePerMeter * Mathf.Max(0.1f, Plugin.ReactorBallSize.Value);
                shown.transform.localScale = Vector3.one * scale;
                fx.Fitted = true;                       // packed onto its origin already; its box says nothing
                Plugin.Log.LogInfo($"[Reactor] {fx.Name} packed onto its origin and scaled {scale:0.#}x.");
            }
            if (fx == sBallFx || fx == sCoreFx) DrawOnTop(shown);
            Play(shown);
        }

        // Drawn after the feed beams (ReactorFeeds.DrawBehind), so the ball covers them where they meet it.
        static void DrawOnTop(GameObject shown)
        {
            foreach (var renderer in shown.GetComponentsInChildren<Renderer>(true)) { renderer.rendererPriority = 10; renderer.sortingOrder = 10; }
        }

        // The game fires these effects with a start event; send the usual ones and play everything.
        static void Play(GameObject shown)
        {
            shown.SetActive(true);
            foreach (var effect in shown.GetComponentsInChildren<VisualEffect>(true))
            {
                effect.enabled = true;
                effect.Reinit();
                effect.Play();
                effect.SendEvent("Start");
                effect.SendEvent("OnPlay");
            }
            foreach (var system in shown.GetComponentsInChildren<ParticleSystem>(true)) system.Play(true);
        }

        // Once the effect has drawn (and only while it is showing): measure it, log it, and center it in its pivot.
        static void TryFit(Effect fx)
        {
            if (fx.Fitted || fx.Shown == null || fx.FitTries <= 0 || Time.time < fx.FitFrom || !fx.Shown.activeInHierarchy) return;
            var t = fx.Shown.transform;
            bool measured = FxShape.TryMeasure(fx.Shown, out var shape);
            if (!measured && --fx.FitTries > 0) { fx.FitFrom = Time.time + 0.25f; return; }
            fx.Fitted = true;
            if (!measured) return;              // its box is a placeholder: left where it spawns
            // A point p of the effect sits at localPosition + localRotation * (localScale * p) in the pivot.
            t.localPosition = -(t.localRotation * Vector3.Scale(t.localScale, shape.Local.center));
            Plugin.Log.LogInfo($"[Reactor] {fx.Name} centered: moved to {t.localPosition}.");
        }

        // The ball's light: blue, deepening as the plasma goes off balance (the flames keep the game's own amber).
        // The core's light by level: blue in balance, then orange, red and purple, each brighter.
        internal static Color ColorFor(int level) => level <= 0 ? new Color(0.55f, 0.75f, 1f)
                                                   : level == 1 ? new Color(1f, 0.55f, 0.15f)
                                                   : level == 2 ? new Color(1f, 0.15f, 0.08f)
                                                   : new Color(0.75f, 0.2f, 1f);
        // A mistake: the core's light flashes red for a moment.
        static float sRedUntil = -1f, sShownBright = 1f;
        static Color sShownColor = new Color(0.55f, 0.75f, 1f);
        static bool sWasRed;
        internal static void FlashRed(float seconds) => sRedUntil = Time.time + seconds;

        static float BrightnessFor(int level) => level <= 0 ? 1f : level == 1 ? 1.3f : level == 2 ? 1.7f : 2.2f;

        static void Place(Effect fx, bool visible, Vector3 scale)
        {
            if (fx.Pivot == null || fx.Shown == null) return;
            if (fx.Pivot.activeSelf != visible)
            {
                fx.Pivot.SetActive(visible);
                if (visible) Play(fx.Shown);
            }
            if (!visible) return;
            fx.Pivot.transform.localPosition = Vector3.zero;
            fx.Pivot.transform.localRotation = Quaternion.identity;
            fx.Pivot.transform.localScale = scale;
            TryFit(fx);
        }

        static void SetFloat(Effect fx, string name, float value)
        {
            if (fx.Shown == null || !fx.Shown.activeInHierarchy) return;
            foreach (var effect in fx.Shown.GetComponentsInChildren<VisualEffect>())
                if (effect.HasFloat(name)) effect.SetFloat(name, value);
        }

        // Every frame. ballDiameter is the size right now (pulse included); level is the color step (1 safe,
        // 2 unstable, 3 badly unbalanced); unstable wraps the ball in fire; fade 1 = full, 0 = gone.
        const float MovingSpeed = 1f;       // m/s of ship speed above which the effects give way to the stand-in

        internal static void Show(Vector3 center, float ballDiameter, int level, bool unstable, float fade)
        {
            if (sRoot == null || sBall == null) return;
            float brightness = Mathf.Max(0f, Plugin.ReactorBrightness.Value) * fade;
            // The light eases to the level's color and brightness over about a second; a mistake's red comes at once
            // and eases back.
            bool red = Time.time < sRedUntil;
            var target = red ? new Color(1f, 0.04f, 0.04f) : ColorFor(level);
            float targetBright = red ? 3f : BrightnessFor(level);
            float ease = red && !sWasRed ? 1f : 1f - Mathf.Exp(-Time.deltaTime * 2.5f);
            sWasRed = red;
            sShownColor = Color.Lerp(sShownColor, target, ease);
            sShownBright = Mathf.Lerp(sShownBright, targetBright, ease);
            var color = sShownColor;

            sRoot.transform.position = center;
            float flicker = 1f + Mathf.Max(0, level - 1) * 0.06f * (Mathf.PerlinNoise(Time.time * 7f, 0.3f) - 0.5f);
            float baseSize = Mathf.Max(0.1f, Plugin.ReactorBallSize.Value);
            float ballScale = Mathf.Max(0.001f, ballDiameter / baseSize * fade * flicker);   // 1 = BallSize

            // The ball: the game's fire and glow at BallSize times the pulse; the stand-in until it loads.
            // The game's effects leave their particles in world space: with the ship under way they stream out behind it,
            // through the hull. While it moves, the plain glowing stand-in is shown instead.
            bool underWay = FlightModel.UnderWay && FlightModel.Speed > MovingSpeed;
            bool ballReady = sBallFx.Shown != null && !underWay;
            Place(sBallFx, fade > 0.001f && !underWay, Vector3.one * ballScale);
            Place(sCoreFx, fade > 0.001f && !underWay, Vector3.one * ballScale);
            sBall.SetActive(!ballReady);
            if (!ballReady)
            {
                sBall.transform.localScale = Vector3.one * Mathf.Max(0.001f, ballDiameter * fade * flicker);
                FxShape.PaintGlow(sBallMaterial!, color, brightness);
            }

            if (sLight != null)
            {
                sLight.SetColor(color);
                sLight.SetIntensity(Mathf.Max(0f, Plugin.ReactorLightLumens.Value) * fade * (ballDiameter / baseSize) * sShownBright, LightUnit.Lumen);
                // short reach, and the hull's shadow: the pulse stays in the chamber instead of glowing through the hull
                sLight.SetRange(Mathf.Max(1f, ballDiameter * 3f));
            }

            // Unstable: the fire cloud wraps the ball, just past its surface; it comes and goes over half a second.
            sCloudShown = Mathf.MoveTowards(sCloudShown, unstable ? 1f : 0f, Time.deltaTime * 2f);
            bool cloud = sCloudShown > 0.001f && !underWay;
            Place(sCloudFx, cloud, Vector3.one);
            if (cloud) SetFloat(sCloudFx, "Radius", Mathf.Max(0.05f, ballDiameter * 0.5f * (1.15f + 0.2f * (level - 2)) * sCloudShown * fade));

            if (!sBallFx.MaterialsLogged && sBallFx.Shown != null && sBallFx.Shown.activeInHierarchy && Time.time > sBallFx.FitFrom + 0.5f)
            {
                sBallFx.MaterialsLogged = true;
                Plugin.Log.LogInfo($"[Reactor] {sBallFx.Name} materials:{FxShape.DescribeMaterials(sBallFx.Shown)}");
            }
        }
    }
}
