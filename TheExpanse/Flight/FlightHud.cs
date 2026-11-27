using System.Collections.Generic;
using BBI.Unity.Game;
using InControl;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheExpanse
{
    // Flight Control's on-screen parts, kept small and around the edges of the screen, in the game's own HUD look (its
    // clean Rajdhani face, as on its ship-name and hazard banners, and its amber), with a soft drop shadow so they read
    // over a bright hull or planet.
    //   left edge:     the main drive's gauge (amber), its name above and the throttle below;
    //   right edge:    the thrusters' gauge (cyan, bold), name above, use below, attitude hold beside;
    //   top middle:    all bold: the speed (largest), right of it the prograde / retrograde bubble (where the ship is
    //                  going, seen out of the nose) and the turn rate; under the speed the acceleration (drive and
    //                  thrusters both give it);
    //   bottom middle: the flip and burn's progress, while it runs and briefly after.
    //   sit prompt:    near a command chair, the Interact button's own picture for the input last used (keyboard,
    //                  Xbox or PlayStation), as in the game's own prompts, and SIT AT THE CONTROLS.
    internal static class FlightHud
    {
        static readonly Color Amber = new Color(1f, 0.8f, 0.35f, 0.95f);
        static readonly Color DriveColor = new Color(1f, 0.6f, 0.2f, 0.9f);
        static readonly Color RcsColor = new Color(0.4f, 0.85f, 1f, 0.9f);
        static readonly Color FrameColor = new Color(0f, 0f, 0f, 0.3f);
        const float GaugeHeight = 280f, GaugeWidth = 14f, Edge = 36f;

        static GameObject? sCanvas;
        static RectTransform? sReadout, sDriveFill, sRcsFill, sPrompt;
        static TextMeshProUGUI? sSetupText;
        static TextMeshProUGUI? sClock;
        static BBI.Unity.Game.UI.GameTimerUIController? sTimer;
        static TextMeshProUGUI? sTimerText;
        static float sNextTimerLook;
        static bool sClockLogged;
        static readonly System.Reflection.FieldInfo? sTimerTextField = HarmonyLib.AccessTools.Field(typeof(BBI.Unity.Game.UI.GameTimerUIController), "m_TimerText");
        static TextMeshProUGUI? sDriveText, sDrivePercent, sRcsText, sRcsPercent, sSpeedText, sTurnText, sAccelText, sAutoText, sPromptText, sPromptKey;
        static TextMeshProUGUI? sModeText, sAimText;
        static RectTransform? sAim, sPdcBar, sRail, sRailBar;
        static TextMeshProUGUI? sRailText;
        static readonly List<Image> sRailSegments = new List<Image>();
        static readonly List<Image> sPdcSegments = new List<Image>();
        static RectTransform? sBubble, sPrograde, sRetrograde;
        static RectTransform? sHome, sHomeArrow;
        static TextMeshProUGUI? sHomeText;
        static readonly Color HomeColor = new Color(1f, 0.8f, 0.35f, 0.55f);
        static readonly Dictionary<string, Texture2D> sMarkers = new Dictionary<string, Texture2D>();
        const float BubbleSize = 46f, MarkerSize = 18f;
        static Image? sPromptIcon;
        static TMP_FontAsset? sFont;
        static Material? sShadowed;
        static bool sFontLogged;

        internal static void Reset()
        {
            if (sCanvas != null) Object.Destroy(sCanvas);
            if (sShadowed != null) Object.Destroy(sShadowed);
            foreach (var m in sGlows.Values) if (m != null) Object.Destroy(m);
            sGlows.Clear();
            sCanvas = null;
            sShadowed = null;
            sReadout = sDriveFill = sRcsFill = sPrompt = null;
            sDriveText = sDrivePercent = sRcsText = sRcsPercent = sSpeedText = sTurnText = sAccelText = sAutoText = sPromptText = sPromptKey = null;
            sModeText = sAimText = null;
            sAim = sPdcBar = sRail = sRailBar = null;
            sRailText = null;
            sRailSegments.Clear();
            sPdcSegments.Clear();
            sBubble = sPrograde = sRetrograde = null;
            sHome = sHomeArrow = null;
            sHomeText = null;
            sPromptIcon = null;
            sClock = null;
            sTimer = null; sTimerText = null;
            sNextTimerLook = 0f;
            sClockLogged = false;
        }

        // Every frame (LateUpdate).
        internal static void Frame()
        {
            bool readout = FlightModel.Flying;
            bool prompt = !FlightSeat.Seated && FlightSeat.NearChair;
            string setup = ShipSetup.Status;
            if (!readout && !prompt && setup.Length == 0)
            {
                if (sCanvas != null && sCanvas.activeSelf) sCanvas.SetActive(false);
                return;
            }
            if (sCanvas == null) Build();
            if (sCanvas == null) return;
            if (!sCanvas.activeSelf) sCanvas.SetActive(true);
            sReadout!.gameObject.SetActive(readout);
            sPrompt!.gameObject.SetActive(prompt);
            sSetupText!.gameObject.SetActive(setup.Length > 0);
            if (setup.Length > 0) sSetupText.text = setup;
            if (readout) ShowReadout();
            if (prompt) ShowPrompt();
            ShowClock(readout && FlightCamera.HudHidden);
        }

        // The shift clock, as on the suit HUD: copied from the game's timer text while the suit HUD is faded out (outside
        // and nose views), at the same place on the screen when that can be worked out, else top center.
        static void ShowClock(bool want)
        {
            if (sClock == null) return;
            if (want && sTimerText == null && Time.unscaledTime >= sNextTimerLook)
            {
                sNextTimerLook = Time.unscaledTime + 2f;
                sTimer = Object.FindObjectOfType<BBI.Unity.Game.UI.GameTimerUIController>();
                sTimerText = sTimer != null ? sTimerTextField?.GetValue(sTimer) as TextMeshProUGUI : null;
                if (sTimerText != null) sClock.fontSize = Mathf.Max(ClockSize, sTimerText.fontSize * 1.6f);
            }
            bool show = want && sTimerText != null && sTimerText.enabled && !string.IsNullOrEmpty(sTimerText.text);
            sClock.gameObject.SetActive(show);
            if (!show) return;
            sClock.text = sTimerText!.text;
            var color = sTimerText.color; color.a = 1f;
            sClock.color = color;
            PlaceClock();
        }

        static void PlaceClock()
        {
            var rect = (RectTransform)sClock!.transform;
            var gameCanvas = sTimerText!.canvas;
            var ourCanvas = sCanvas!.GetComponent<Canvas>();
            Vector2 local = default;
            bool placed = false;
            if (gameCanvas != null && ourCanvas != null)
            {
                var root = gameCanvas.rootCanvas;
                var camera = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
                var world = sTimerText.rectTransform.TransformPoint(sTimerText.rectTransform.rect.center);
                var screen = RectTransformUtility.WorldToScreenPoint(camera, world);
                placed = screen.x > 0f && screen.y > 0f && screen.x < Screen.width && screen.y < Screen.height
                         && RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)ourCanvas.transform, screen,
                                ourCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : ourCanvas.worldCamera, out local);
                if (!sClockLogged)
                {
                    sClockLogged = true;
                    Plugin.Log.LogInfo($"[Flight] shift clock copied from '{sTimerText.name}' on '{root.name}' ({root.renderMode}), " +
                                       $"{sTimerText.fontSize:0} pt, at screen {screen} of {Screen.width}x{Screen.height}: " +
                                       (placed ? "same place" : "off screen, top center instead") + ".");
                }
            }
            // the first row of the top: over the speed
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -ClockTop);
        }

        // ---- the readout -------------------------------------------------------------------------------------------

        static void ShowReadout()
        {
            SetFill(sDriveFill!, FlightModel.Throttle);
            SetFill(sRcsFill!, FlightModel.RcsLevel);
            sDrivePercent!.text = $"{FlightModel.Throttle * 100f:0}%";
            sRcsPercent!.text = $"{FlightModel.RcsLevel * 100f:0}%";
            sRcsText!.text = FlightModel.Holding ? "HOLD" : "FREE";
            // speed and acceleration together (the drive and the thrusters both accelerate the ship)
            // top: turn rate, speed, acceleration (drive and thrusters both give it)
            sTurnText!.text = $"{FlightModel.TurnRate:0.0}°/s";
            sSpeedText!.text = $"{FlightModel.Speed:0.0} m/s";
            sAccelText!.text = $"{FlightModel.FeltAcceleration:0.00} m/s²  {FlightModel.FeltG:0.00} g";
            PlaceRowTwo();
            ShowBubble();
            ShowHome();
            string auto = FlightModel.AutoStatus;
            sAutoText!.gameObject.SetActive(auto.Length > 0);
            if (auto.Length > 0)
            {
                sAutoText.text = auto.ToUpperInvariant();
                sAutoText.color = FlightModel.AutoActive ? RcsColor : Amber;
                sAutoText.fontStyle = FlightModel.AutoActive ? FontStyles.Bold : FontStyles.Normal;
            }
            // the weapon mode, over the flip and burn line (none at the helm)
            bool weapon = Weapons.Allowed && Weapons.Mode != WeaponMode.Helm;
            sModeText!.gameObject.SetActive(weapon);
            if (weapon) sModeText.text = Weapons.ModeName;
            ShowAim();
            ShowRailSight();
        }

        // The text's material: the dark drop shadow (underlay) and a bloom in the text's own color (glow), one per color.
        static readonly Dictionary<Color, Material> sGlows = new Dictionary<Color, Material>();

        static Material? Glowing(Color color)
        {
            if (sShadowed == null) return null;
            if (sGlows.TryGetValue(color, out var cached) && cached != null) return cached;
            var m = new Material(sShadowed) { name = "TheExpanse_HudGlow" };
            if (m.HasProperty("_GlowColor"))
            {
                m.EnableKeyword("GLOW_ON");
                m.SetColor("_GlowColor", new Color(color.r, color.g, color.b, 0.55f));
                m.SetFloat("_GlowOffset", 0.1f);
                m.SetFloat("_GlowInner", 0.1f);
                m.SetFloat("_GlowOuter", 0.75f);
                m.SetFloat("_GlowPower", 0.6f);
            }
            sGlows[color] = m;
            return m;
        }

        static readonly Color WeaponColor = new Color(1f, 0.35f, 0.25f, 0.95f);

        // The aim cursor where the aim point is on the screen, with the turrets' state.
        static void ShowAim()
        {
            Vector3 position = Vector3.zero; Quaternion rotation = Quaternion.identity;
            bool show = Weapons.Aiming && Camera.main != null && Pdc.View(out position, out rotation);
            sAim!.gameObject.SetActive(show);
            if (!show) return;
            var size = ((RectTransform)sCanvas!.transform).rect.size;
            float half = size.y * 0.5f;
            float tanV = Mathf.Tan(Camera.main!.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var local = Quaternion.Inverse(rotation) * Pdc.CursorDirection(rotation);
            sAim.anchoredPosition = local.z > 0.01f ? new Vector2(local.x / local.z, local.y / local.z) / tanV * half : Vector2.zero;
            bool pdc = Weapons.Mode == WeaponMode.Pdc;
            sPdcBar!.gameObject.SetActive(pdc);
            if (pdc)
            {
                ShowPdcBar();
                float distance = Vector3.Distance(position, Pdc.AimPoint);
                sAimText!.text = Pdc.Turrets.Count == 0 ? "NO PDCS"
                               : Pdc.AimHit ? (distance < 1000f ? $"{distance:0} M" : $"{distance / 1000f:0.0} KM") : "NO TARGET";
            }
            else sAimText!.text = "";
        }

        static readonly Color RailColor = new Color(0.45f, 0.75f, 1f, 0.95f);
        const int RailSegments = 10;

        // The rail gun's sight where the ship's nose points (5 km out), its charge as segments, and its state.
        static void ShowRailSight()
        {
            Vector3 eye = Vector3.zero; Quaternion view = Quaternion.identity;
            bool show = Weapons.Allowed && Weapons.Mode == WeaponMode.RailGun && Camera.main != null && Pdc.View(out eye, out view);
            sRail!.gameObject.SetActive(show);
            if (!show) return;
            RailGun.Aim(out var from, out var dir);
            var size = ((RectTransform)sCanvas!.transform).rect.size;
            float half = size.y * 0.5f;
            float tanV = Mathf.Tan(Camera.main!.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var local = Quaternion.Inverse(view) * (from + dir * 5000f - eye);
            bool ahead = local.z > 0.01f;
            sRail.gameObject.SetActive(ahead);
            if (!ahead) return;
            sRail.anchoredPosition = new Vector2(local.x / local.z, local.y / local.z) / tanV * half;
            while (sRailSegments.Count < RailSegments)
            {
                var go = new GameObject("Segment", typeof(RectTransform));
                go.transform.SetParent(sRailBar, false);
                var image = go.AddComponent<Image>();
                image.raycastTarget = false;
                AddShadow(go);
                var rect = image.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.sizeDelta = new Vector2(SegmentWidth, SegmentHeight);
                float width = RailSegments * SegmentWidth + (RailSegments - 1) * SegmentGap;
                rect.anchoredPosition = new Vector2(-width / 2f + sRailSegments.Count * (SegmentWidth + SegmentGap), 0f);
                sRailSegments.Add(image);
            }
            float reload = RailGun.ReloadLeft, total = Mathf.Max(0.2f, Plugin.WeaponsRailGunCooldown.Value);
            float fill = RailGun.Ready ? RailGun.Charge : 1f - reload / total;
            for (int i = 0; i < sRailSegments.Count; i++)
            {
                var c = RailColor;
                c.a = (i + 0.5f) / RailSegments <= fill ? (RailGun.Ready ? 1f : 0.45f) : 0.12f;
                sRailSegments[i].color = c;
            }
            sRailText!.text = !RailGun.Ready ? $"RELOADING {reload:0.0} S" : RailGun.Charge > 0f ? "CHARGING" : "READY";
        }

        // One segment per turret: bright on target, dim while turning to it, faint when it can't see the aim point.
        const float SegmentWidth = 9f, SegmentHeight = 6f, SegmentGap = 3f;

        static void ShowPdcBar()
        {
            var turrets = Pdc.Turrets;
            while (sPdcSegments.Count < turrets.Count)
            {
                var go = new GameObject("Segment", typeof(RectTransform));
                go.transform.SetParent(sPdcBar, false);
                var image = go.AddComponent<Image>();
                image.raycastTarget = false;
                AddShadow(go);
                sPdcSegments.Add(image);
            }
            float width = turrets.Count * SegmentWidth + Mathf.Max(0, turrets.Count - 1) * SegmentGap;
            for (int i = 0; i < sPdcSegments.Count; i++)
            {
                var image = sPdcSegments[i];
                bool used = i < turrets.Count;
                image.gameObject.SetActive(used);
                if (!used) continue;
                var rect = image.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.sizeDelta = new Vector2(SegmentWidth, SegmentHeight);
                rect.anchoredPosition = new Vector2(-width / 2f + i * (SegmentWidth + SegmentGap), 0f);
                var c = WeaponColor;
                c.a = turrets[i].State == TurretState.OnTarget ? 1f : turrets[i].State == TurretState.Tracking ? 0.45f : 0.12f;
                image.color = c;
            }
        }

        // The top of the screen, in rows: the shift clock (outside views), then the speed, the heading bubble and the
        // turn rate centered together, then the acceleration in both units centered.
        const float ClockTop = 10f, ClockSize = 30f, RowTwoTop = 50f, RowThreeTop = 98f, SpeedSize = 30f, RowSize = 22f, RowGap = 16f;

        static void PlaceRowTwo()
        {
            float speed = sSpeedText!.preferredWidth, turn = sTurnText!.preferredWidth;
            float total = speed + RowGap + BubbleSize + RowGap + turn;
            float x = -total / 2f;
            sSpeedText.rectTransform.anchoredPosition = new Vector2(x, -RowTwoTop);
            x += speed + RowGap;
            sBubble!.anchoredPosition = new Vector2(x + BubbleSize / 2f, -RowTwoTop + 4f);
            x += BubbleSize + RowGap;
            sTurnText.rectTransform.anchoredPosition = new Vector2(x, -RowTwoTop - 6f);
        }

        static void SetFill(RectTransform fill, float level)
        {
            fill.sizeDelta = new Vector2(GaugeWidth - 2f, Mathf.Clamp01(level) * (GaugeHeight - 2f));
        }

        // ---- the sit prompt ----------------------------------------------------------------------------------------

        static void ShowPrompt()
        {
            var sprite = InteractSprite(out string fallback);
            sPromptIcon!.gameObject.SetActive(sprite != null);
            if (sprite != null) sPromptIcon.sprite = sprite;
            sPromptKey!.gameObject.SetActive(sprite == null);
            sPromptKey.text = $"[{fallback}]";
        }

        // The Interact button's picture for the input in use, from the game's own control-sprite lists (the same
        // lookup its button prompts make).
        static Sprite? InteractSprite(out string fallback)
        {
            bool pad = LynxControls.Instance != null && LynxControls.Instance.LastInputType == BindingSourceType.DeviceBindingSource;
            fallback = pad ? "X" : "F";
            try
            {
                if (!LynxConfigurableControlOptions.LoadableMappings.TryGetValue(GameplayActions.GameplayActionSet.Interact, out var mappings)) return null;
                var context = pad ? LynxConfigurableControlOptions.MappingContextFlags.Consoles : LynxConfigurableControlOptions.MappingContextFlags.PC;
                var key = pad ? LynxConfigurableControlOptions.ConfigKey.NoneGamepad : LynxConfigurableControlOptions.ConfigKey.None;
                foreach (var m in mappings)
                    if (m.IsPrimary && m.MainContext == context) { key = m.ConfigEnumVar.Value; break; }
                fallback = key.ToString();
                var lists = Main.Instance?.MainSettings?.ControlSpriteListAsset;
                if (lists == null) return null;
                Dictionary<LynxConfigurableControlOptions.ConfigKey, ControlSpriteAsset> sprites;
                if (!pad) sprites = lists.MKB_ControlSprites;
                else
                {
                    var device = InputManager.ActiveDevice;
                    bool playStation = device != null && device.DeviceStyle.ToString().StartsWith("PlayStation");
                    sprites = playStation ? lists.PS4_ControlSprites : lists.XboxOne_ControlSprites;
                }
                return sprites.TryGetValue(key, out var asset) && asset != null ? asset.Data.Sprite : null;
            }
            catch (System.Exception) { return null; }
        }

        // ---- building --------------------------------------------------------------------------------------------

        static void Build()
        {
            FindFont();
            sCanvas = new GameObject("TheExpanse_FlightHud");
            var canvas = sCanvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;
            var scaler = sCanvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            var readout = new GameObject("Readout", typeof(RectTransform));
            readout.transform.SetParent(sCanvas.transform, false);
            sReadout = (RectTransform)readout.transform;
            sReadout.anchorMin = Vector2.zero;
            sReadout.anchorMax = Vector2.one;
            sReadout.offsetMin = sReadout.offsetMax = Vector2.zero;

            // left edge: the drive, its name above, its percentage below
            sDriveFill = Gauge("Drive", sReadout, new Vector2(0f, 0.5f), new Vector2(Edge, 0f), DriveColor);
            sDriveText = Text("DriveName", sReadout, new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(Edge - 4f, GaugeHeight / 2f + 6f),
                              new Vector2(120f, 20f), 13f, TextAlignmentOptions.BottomLeft, DriveColor);
            sDriveText.text = "DRIVE";
            sDrivePercent = Text("DrivePercent", sReadout, new Vector2(0f, 0.5f), new Vector2(0f, 1f), new Vector2(Edge - 4f, -GaugeHeight / 2f - 6f),
                                 new Vector2(120f, 26f), 18f, TextAlignmentOptions.TopLeft, DriveColor);
            // right edge: the thrusters, name above, percentage below, turn rate and attitude hold beside
            sRcsFill = Gauge("Rcs", sReadout, new Vector2(1f, 0.5f), new Vector2(-Edge - GaugeWidth, 0f), RcsColor);
            var rcsName = Text("RcsName", sReadout, new Vector2(1f, 0.5f), new Vector2(1f, 0f), new Vector2(-Edge + 4f, GaugeHeight / 2f + 6f),
                               new Vector2(120f, 20f), 13f, TextAlignmentOptions.BottomRight, RcsColor);
            rcsName.text = "RCS";
            sRcsPercent = Text("RcsPercent", sReadout, new Vector2(1f, 0.5f), new Vector2(1f, 1f), new Vector2(-Edge + 4f, -GaugeHeight / 2f - 6f),
                               new Vector2(120f, 26f), 18f, TextAlignmentOptions.TopRight, RcsColor);
            sRcsText = Text("RcsText", sReadout, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-Edge - GaugeWidth - 12f, -GaugeHeight / 2f + 14f),
                            new Vector2(220f, 28f), 18f, TextAlignmentOptions.BottomRight, RcsColor);
            foreach (var cyan in new[] { rcsName, sRcsPercent, sRcsText }) cyan.fontStyle = FontStyles.Bold;
            // top middle: speed
            // top middle: speed in the middle; the heading marker and the turn rate to its right; the acceleration under
            // it; all bold
            sSpeedText = Text("Speed", sReadout, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(0f, -RowTwoTop),
                              new Vector2(260f, 40f), SpeedSize, TextAlignmentOptions.TopLeft, Amber);
            sTurnText = Text("Turn", sReadout, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(0f, -RowTwoTop - 6f),
                             new Vector2(200f, 30f), RowSize, TextAlignmentOptions.TopLeft, Amber);
            sAccelText = Text("Accel", sReadout, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -RowThreeTop),
                              new Vector2(520f, 30f), RowSize, TextAlignmentOptions.Top, Amber);
            foreach (var top in new[] { sSpeedText, sTurnText, sAccelText }) top.fontStyle = FontStyles.Bold;
            sBubble = Bubble(sReadout, new Vector2(0f, -RowTwoTop + 4f));
            // the bay marker (outside and nose views): a thin hexagon outline with the distance, over the bay or on the
            // screen's edge
            var home = new GameObject("Home", typeof(RectTransform));
            home.transform.SetParent(sReadout, false);
            sHome = (RectTransform)home.transform;
            sHome.anchorMin = sHome.anchorMax = sHome.pivot = new Vector2(0.5f, 0.5f);
            sHome.sizeDelta = new Vector2(34f, 34f);
            Marker(sHome, "hex", 34f, HomeColor, Vector2.zero);
            sHomeArrow = Marker(sHome, "arrow", 16f, HomeColor, Vector2.zero);
            sHomeText = Text("HomeDistance", sHome, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -2f),
                             new Vector2(160f, 22f), 16f, TextAlignmentOptions.Top, HomeColor);
            sHomeText.fontStyle = FontStyles.Bold;
            // bottom middle: flip and burn
            sAutoText = Text("Auto", sReadout, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, Edge),
                             new Vector2(900f, 30f), 18f, TextAlignmentOptions.Bottom, Amber);
            // over it: the weapon mode, large
            sModeText = Text("WeaponMode", sReadout, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, Edge + 34f),
                             new Vector2(500f, 60f), 48f, TextAlignmentOptions.Bottom, new Color(1f, 0.35f, 0.25f, 1f));
            sModeText.fontStyle = FontStyles.Bold;
            // the rail gun's sight: where the ship's nose points, with its charge under it
            sRail = Marker(sReadout, "railsight", 56f, RailColor, Vector2.zero);
            var railBar = new GameObject("RailBar", typeof(RectTransform));
            railBar.transform.SetParent(sRail, false);
            sRailBar = (RectTransform)railBar.transform;
            sRailBar.anchorMin = sRailBar.anchorMax = new Vector2(0.5f, 0f);
            sRailBar.pivot = new Vector2(0.5f, 1f);
            sRailBar.anchoredPosition = new Vector2(0f, -6f);
            sRailText = Text("RailState", sRail, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -18f),
                             new Vector2(300f, 26f), 18f, TextAlignmentOptions.Top, RailColor);
            sRailText.fontStyle = FontStyles.Bold;
            // the aim cursor (PDC and torpedo modes) and the turrets' state under it
            sAim = Marker(sReadout, "reticle", 44f, WeaponColor, Vector2.zero);
            sAimText = Text("AimRange", sAim, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -16f),
                            new Vector2(300f, 26f), 18f, TextAlignmentOptions.Top, WeaponColor);
            var bar = new GameObject("PdcBar", typeof(RectTransform));
            bar.transform.SetParent(sAim, false);
            sPdcBar = (RectTransform)bar.transform;
            sPdcBar.anchorMin = sPdcBar.anchorMax = new Vector2(0.5f, 0f);
            sPdcBar.pivot = new Vector2(0.5f, 1f);
            sPdcBar.anchoredPosition = new Vector2(0f, -4f);
            sAimText.fontStyle = FontStyles.Bold;

            // sit prompt: under the middle of the screen, like the game's own interaction prompts
            var prompt = new GameObject("SitPrompt", typeof(RectTransform));
            prompt.transform.SetParent(sCanvas.transform, false);
            sPrompt = (RectTransform)prompt.transform;
            sPrompt.anchorMin = sPrompt.anchorMax = new Vector2(0.5f, 0.5f);
            sPrompt.pivot = new Vector2(0.5f, 0.5f);
            sPrompt.anchoredPosition = new Vector2(0f, -120f);
            sPrompt.sizeDelta = new Vector2(260f, 32f);
            var iconObject = new GameObject("Icon", typeof(RectTransform));
            iconObject.transform.SetParent(sPrompt, false);
            var iconRect = (RectTransform)iconObject.transform;
            iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = Vector2.zero;
            iconRect.sizeDelta = new Vector2(28f, 28f);
            sPromptIcon = iconObject.AddComponent<Image>();
            sPromptIcon.preserveAspect = true;
            iconObject.AddComponent<Shadow>().effectDistance = new Vector2(1f, -1f);
            sPromptKey = Text("Key", sPrompt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(40f, 28f), 16f,
                              TextAlignmentOptions.Left, Amber);
            sPromptText = Text("Label", sPrompt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(36f, 0f), new Vector2(220f, 28f), 16f,
                               TextAlignmentOptions.Left, Amber);
            sPromptText.text = "SIT AT THE CONTROLS";
            // setup mode: what is placed and the keys, one per line in the top left corner, under the HAB / SHIP / G
            // readout
            sSetupText = Text("Setup", sCanvas.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -170f),
                              new Vector2(500f, 220f), 16f, TextAlignmentOptions.TopLeft, Amber);
            sSetupText.fontStyle = FontStyles.Bold;
            sClock = Text("Clock", sCanvas.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -ClockTop),
                          new Vector2(300f, 40f), ClockSize, TextAlignmentOptions.Top, Amber);
            sClock.gameObject.SetActive(false);
            sCanvas.SetActive(false);
        }

        // The prograde / retrograde bubble, as seen looking out of the nose: the middle is dead ahead, the ring is 90
        // degrees off, up is the ship's up and right its starboard. The prograde marker (a circle with three ticks)
        // sits where the ship is going while that is ahead of it; past 90 degrees the retrograde marker (a circle with
        // an X) shows instead, where the ship is coming from. Neither shows while the ship is nearly still.
        static void ShowBubble()
        {
            var local = FlightModel.LocalVelocity;
            bool moving = FlightModel.Speed > 0.05f;
            bool ahead = local.z >= 0f;
            sPrograde!.gameObject.SetActive(moving && ahead);
            sRetrograde!.gameObject.SetActive(moving && !ahead);
            if (!moving) return;
            var dir = ahead ? local.normalized : -local.normalized;      // the marker's direction, in front of the nose
            float off = Mathf.Acos(Mathf.Clamp(dir.z, -1f, 1f)) * Mathf.Rad2Deg;   // 0..90 degrees from the nose
            var across = new Vector2(dir.x, dir.y);
            var place = across.sqrMagnitude > 1e-8f ? across.normalized * (off / 90f) * (BubbleSize * 0.5f - 2f) : Vector2.zero;
            (ahead ? sPrograde : sRetrograde)!.anchoredPosition = place;
        }

        // The work bay, seen from the outside (or nose) camera: a thin, semi-transparent hexagon outline with the distance
        // from the ship, over the bay when it is in view; otherwise on the screen's edge in its direction, with an arrow
        // pointing out. (The Hab can be pushed far from the bay; the bay stays put.)
        static Transform? sBay;
        static float sNextBayLook;

        static Transform? FindBay()
        {
            if (sBay != null) return sBay;
            if (Time.unscaledTime < sNextBayLook) return null;
            sNextBayLook = Time.unscaledTime + 5f;
            sBay = SceneLookup.Root(BackgroundConstants.BayRootObjectName);
            return sBay;
        }
        const float HomeMargin = 70f;

        static void ShowHome()
        {
            var hab = FlightCamera.OffSeat ? FindBay() : null;
            var camera = Camera.main;
            sHome!.gameObject.SetActive(hab != null && camera != null);
            if (hab == null || camera == null) return;
            var size = ((RectTransform)sCanvas!.transform).rect.size;
            float half = size.y * 0.5f;
            var local = Quaternion.Inverse(FlightCamera.CameraRotation) * (hab.position - FlightCamera.CameraPosition);
            float tanV = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float hw = size.x * 0.5f - HomeMargin, hh = half - HomeMargin;
            Vector2 place;
            bool onScreen = false;
            Vector2 direction;
            if (local.z > 0.01f)
            {
                place = new Vector2(local.x / local.z, local.y / local.z) / tanV * half;
                onScreen = Mathf.Abs(place.x) <= hw && Mathf.Abs(place.y) <= hh;
                direction = place;
            }
            else
            {
                place = Vector2.zero;
                direction = new Vector2(local.x, local.y);
                if (direction.sqrMagnitude < 1e-6f) direction = Vector2.down;
            }
            if (!onScreen)
            {
                var d = direction.normalized;
                float t = Mathf.Min(Mathf.Abs(d.x) > 1e-4f ? hw / Mathf.Abs(d.x) : float.MaxValue, Mathf.Abs(d.y) > 1e-4f ? hh / Mathf.Abs(d.y) : float.MaxValue);
                place = d * t;
                sHomeArrow!.anchoredPosition = d * 30f;
                sHomeArrow.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg - 90f);
            }
            sHomeArrow!.gameObject.SetActive(!onScreen);
            sHome.anchoredPosition = place;
            float distance = Vector3.Distance(FlightModel.ShipPosition, hab.position);
            sHomeText!.text = distance < 1000f ? $"{distance:0} m" : $"{distance / 1000f:0.0} km";
        }

        static RectTransform Bubble(RectTransform parent, Vector2 position)
        {
            var go = new GameObject("Bubble", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(BubbleSize, BubbleSize);
            Marker(rect, "ring", BubbleSize, new Color(0f, 0f, 0f, 0.35f), Vector2.zero, "ringfill");
            Marker(rect, "ring", BubbleSize, new Color(Amber.r, Amber.g, Amber.b, 0.6f), Vector2.zero);
            Marker(rect, "nose", 12f, new Color(Amber.r, Amber.g, Amber.b, 0.7f), Vector2.zero);
            sPrograde = Marker(rect, "prograde", MarkerSize, Amber, Vector2.zero);
            sRetrograde = Marker(rect, "retrograde", MarkerSize, DriveColor, Vector2.zero);
            return rect;
        }

        static RectTransform Marker(RectTransform parent, string shape, float size, Color color, Vector2 position, string? textureKey = null)
        {
            var go = new GameObject(shape, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(size, size);
            var image = go.AddComponent<RawImage>();
            image.texture = MarkerTexture(textureKey ?? shape);
            image.color = color;
            image.raycastTarget = false;
            AddShadow(go);
            return rect;
        }

        // 64 x 64 white shapes with soft edges, drawn once: the bubble's ring (and its dark fill), the nose cross, and the
        // prograde (circle with three ticks: up, left, right) and retrograde (circle with an X) markers.
        static Texture2D MarkerTexture(string shape)
        {
            if (sMarkers.TryGetValue(shape, out var have) && have != null) return have;
            const int N = 64;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "TheExpanse_Marker_" + shape };
            var px = new Color[N * N];
            float Line(float d, float width) => Mathf.Clamp01(1f - Mathf.Abs(d) / width);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v), a = 0f;
                    switch (shape)
                    {
                        case "ring": a = Line(r - 0.93f, 0.06f); break;
                        case "railsight":   // four corner brackets and a dot: the nose's line
                        {
                            float au = Abs(u), av = Abs(v);
                            bool corner = au > 0.55f && av > 0.55f && au < 0.95f && av < 0.95f;
                            if (corner) a = Mathf.Max(Line(au - 0.9f, 0.05f) * (av > 0.6f ? 1f : 0f), Line(av - 0.9f, 0.05f) * (au > 0.6f ? 1f : 0f));
                            if (r < 0.07f) a = 1f;
                            break;
                        }
                        case "reticle":     // a ring with four ticks pointing in, and a dot
                            a = Line(r - 0.8f, 0.05f);
                            if (Abs(u) < 0.05f && Abs(v) > 0.45f && Abs(v) < 0.8f) a = Mathf.Max(a, Line(u, 0.05f));
                            if (Abs(v) < 0.05f && Abs(u) > 0.45f && Abs(u) < 0.8f) a = Mathf.Max(a, Line(v, 0.05f));
                            if (r < 0.07f) a = 1f;
                            break;
                        case "ringfill": a = r < 0.93f ? 1f : 0f; break;
                        case "nose":        // a small plus
                            a = Mathf.Max(Abs(u) < 0.9f ? Line(v, 0.12f) : 0f, Abs(v) < 0.9f ? Line(u, 0.12f) : 0f); break;
                        case "prograde":
                            a = Line(r - 0.5f, 0.1f);
                            if (Abs(u) < 0.08f && v > 0.5f) a = Mathf.Max(a, Line(u, 0.08f));          // tick up
                            if (Abs(v) < 0.08f && Abs(u) > 0.5f) a = Mathf.Max(a, Line(v, 0.08f));     // ticks left and right
                            break;
                        case "hex":         // a thin hollow hexagon (flat top)
                        {
                            float qx = Abs(u), qy = Abs(v);
                            float d = Mathf.Max(qx * 0.866f + qy * 0.5f, qy) - 0.8f;
                            a = Line(d, 0.06f);
                            break;
                        }
                        case "home":        // a house: a roof over a square body, a door cut out
                        {
                            bool body = Abs(u) < 0.55f && v > -0.8f && v < 0.15f && !(Abs(u) < 0.15f && v < -0.35f);
                            bool roof = v >= 0.05f && v < 0.85f && Abs(u) < (0.85f - v) * 1.15f;
                            a = body || roof ? 1f : 0f;
                            break;
                        }
                        case "arrow":       // a triangle pointing up
                            a = v > -0.6f && v < 0.8f && Abs(u) < (0.8f - v) * 0.55f ? 1f : 0f;
                            break;
                        case "retrograde":
                            a = Line(r - 0.5f, 0.1f);
                            if (r < 0.95f) a = Mathf.Max(a, Mathf.Max(Line((u - v) * 0.7071f, 0.07f), Line((u + v) * 0.7071f, 0.07f)));
                            break;
                    }
                    px[y * N + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels(px);
            t.Apply(false, true);
            sMarkers[shape] = t;
            return t;
        }

        static float Abs(float f) => Mathf.Abs(f);

        // A thin vertical gauge centered on its edge: a faint frame, quarter ticks, the fill growing from the bottom.
        static RectTransform Gauge(string name, RectTransform parent, Vector2 anchor, Vector2 position, Color color)
        {
            // drop shadow: a soft dark copy of the gauge's outline just below and to the right, drawn first
            for (int k = 0; k < 2; k++)
                Box(name + "Shadow", parent, anchor, new Vector2(0f, 0.5f), position + new Vector2(2f + k, -2f - k),
                    new Vector2(GaugeWidth + 2f, GaugeHeight + 2f), new Color(0f, 0f, 0f, 0.28f));
            var frame = Box(name, parent, anchor, new Vector2(0f, 0.5f), position, new Vector2(GaugeWidth, GaugeHeight), FrameColor);
            frame.gameObject.AddComponent<Outline>().effectColor = new Color(color.r, color.g, color.b, 0.35f);
            for (int i = 1; i < 4; i++)
            {
                var tick = Box("Tick", frame, new Vector2(0f, 0f), new Vector2(0f, 0.5f), new Vector2(-3f, GaugeHeight * i / 4f), new Vector2(GaugeWidth + 6f, 1f),
                               new Color(color.r, color.g, color.b, 0.5f));
                var tickShadow = tick.gameObject.AddComponent<Shadow>();
                tickShadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
                tickShadow.effectDistance = new Vector2(1.5f, -1.5f);
                tickShadow.useGraphicAlpha = false;
            }
            var fill = Box("Fill", frame, Vector2.zero, Vector2.zero, new Vector2(1f, 1f), new Vector2(GaugeWidth - 2f, 0f), color);
            var fillShadow = fill.gameObject.AddComponent<Shadow>();
            fillShadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            fillShadow.effectDistance = new Vector2(2f, -2f);
            fillShadow.useGraphicAlpha = false;
            return fill;
        }

        static RectTransform Box(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        // A drop shadow under a picture (the same offset and darkness as the texts').
        static void AddShadow(GameObject go)
        {
            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = ShadowColor;
            shadow.effectDistance = ShadowOffset;
            shadow.useGraphicAlpha = true;
        }

        static TextMeshProUGUI Text(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size, float fontSize,
                                    TextAlignmentOptions alignment, Color color, bool glow = true, bool shadow = true)
        {
            // the drop shadow: a dark copy, made first so it is drawn under the text, kept in step with it each frame
            TextMeshProUGUI? copy = null;
            if (shadow)
            {
                copy = Text(name + "Shadow", parent, anchor, pivot, position + ShadowOffset, size, fontSize, alignment, ShadowColor, glow: false, shadow: false);
                if (sPlain != null) copy.fontSharedMaterial = sPlain;
            }
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var text = go.AddComponent<TextMeshProUGUI>();
            if (sFont != null) text.font = sFont;
            var material = glow ? Glowing(color) : sShadowed;
            if (material != null) text.fontSharedMaterial = material;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.enableWordWrapping = false;
            text.richText = true;
            text.raycastTarget = false;
            if (copy != null) go.AddComponent<DropShadow>().Copy = copy;
            return text;
        }

        static readonly Vector2 ShadowOffset = new Vector2(3f, -3f);
        static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.9f);
        static Material? sPlain;                // the font's own material, for the shadow copies (no glow)

        // Keeps a text's dark copy under it: same words, size, style and place (offset down-right), shown with it.
        sealed class DropShadow : MonoBehaviour
        {
            internal TextMeshProUGUI? Copy;
            TextMeshProUGUI? mText;
            RectTransform? mRect;

            void OnDisable() { if (Copy != null) Copy.gameObject.SetActive(false); }

            void LateUpdate()
            {
                if (Copy == null) return;
                mText ??= GetComponent<TextMeshProUGUI>();
                mRect ??= (RectTransform)transform;
                if (mText == null) return;
                bool show = mText.isActiveAndEnabled && mText.color.a > 0.01f;
                if (Copy.gameObject.activeSelf != show) Copy.gameObject.SetActive(show);
                if (!show) return;
                if (Copy.text != mText.text) Copy.text = mText.text;
                Copy.fontSize = mText.fontSize;
                Copy.fontStyle = mText.fontStyle;
                Copy.alignment = mText.alignment;
                var c = ShadowColor; c.a *= mText.color.a;
                Copy.color = c;
                var r = Copy.rectTransform;
                r.anchorMin = mRect.anchorMin; r.anchorMax = mRect.anchorMax; r.pivot = mRect.pivot;
                r.sizeDelta = mRect.sizeDelta;
                r.anchoredPosition = mRect.anchoredPosition + ShadowOffset;
            }
        }

        // The suit HUD's own face and look: its speed readout's font and material (as on the suit's velocity and
        // acceleration). Failing that Rajdhani (the game's ship-name and hazard banners), then whatever most of its
        // on-screen text uses. Its material gets a soft dark underlay (the drop shadow) and a glow (the bloom).
        static readonly System.Reflection.FieldInfo? sVelocityField = HarmonyLib.AccessTools.Field(typeof(VelocityUIController), "m_VelocityTextField");
        static string sFontFrom = "a search";

        static void FindFont()
        {
            sFont = null;
            Material? baseMaterial = null;
            // the suit HUD's speed readout (VelocityUIController): its font and its material
            var velocity = Object.FindObjectOfType<VelocityUIController>();
            if (velocity != null && sVelocityField?.GetValue(velocity) is TMP_Text speed && speed.font != null)
            {
                sFont = speed.font;
                baseMaterial = speed.fontSharedMaterial;
                sFontFrom = $"the suit's speed readout '{speed.name}'";
            }
            string[] wanted = { "Rajdhani-SemiBold", "Rajdhani-Medium", "Rajdhani-Regular" };
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();    // only font assets: a handful
            foreach (var name in wanted)
            {
                if (sFont != null) break;
                foreach (var f in fonts) if (f != null && f.name.StartsWith(name)) { sFont = f; break; }
                if (sFont != null) break;
            }
            if (sFont == null)
            {
                var counts = new Dictionary<TMP_FontAsset, int>();
                foreach (var t in Object.FindObjectsOfType<TextMeshProUGUI>())
                    if (t != null && t.font != null && t.isActiveAndEnabled)
                        counts[t.font] = counts.TryGetValue(t.font, out int n) ? n + 1 : 1;
                int best = 0;
                foreach (var pair in counts) if (pair.Value > best) { best = pair.Value; sFont = pair.Key; }
            }
            if (sFont == null) sFont = TMP_Settings.defaultFontAsset;
            if (baseMaterial == null && sFont != null) { baseMaterial = sFont.material; sFontFrom = "its font asset"; }
            if (baseMaterial != null)
            {
                sPlain = baseMaterial;
                sShadowed = new Material(baseMaterial) { name = "TheExpanse_HudShadowed" };
                sShadowed.EnableKeyword("UNDERLAY_ON");
                sShadowed.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.9f));
                sShadowed.SetFloat("_UnderlayOffsetX", 0.7f);
                sShadowed.SetFloat("_UnderlayOffsetY", -0.7f);
                sShadowed.SetFloat("_UnderlayDilate", 0.2f);
                sShadowed.SetFloat("_UnderlaySoftness", 0.5f);
            }
            if (!sFontLogged)
            {
                sFontLogged = true;
                var shader = baseMaterial != null ? baseMaterial.shader : null;
                Plugin.Log.LogInfo($"[Flight] readout font: '{(sFont != null ? sFont.name : "none")}' from {sFontFrom}, material '{(baseMaterial != null ? baseMaterial.name : "none")}', shader '{(shader != null ? shader.name : "none")}' " +
                                   $"(glow {(sShadowed != null && sShadowed.HasProperty("_GlowColor") ? "yes" : "NO")}, shadow {(sShadowed != null && sShadowed.HasProperty("_UnderlayColor") ? "yes" : "NO")}).");
            }
        }
    }
}
