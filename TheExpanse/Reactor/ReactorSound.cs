using UnityEngine;

namespace TheExpanse
{
    // What the reactor sounds like, from the game's own sounds (Wwise events), played from a sound source that sits at
    // 1 m in front of the ear, in the ball's direction:
    //   hum:      a loop (HumSound) while the plasma burns, louder off balance;
    //   rumble:   a controller rumble (Haptics) at the height of each pulse;
    //   warning:  a beep (WarningSound) once a second while the plasma is unstable, and in the last CountdownSeconds
    //             before it goes up a level by itself (or melts down) a beep that speeds up to four a second.
    // All of it is at full volume within SoundNear meters of the ball and fades to nothing at SoundFar, so it is not heard
    // outside the reactor room. Being in the game's own sound engine, it is muffled like the game's other sounds where
    // the game does that (in vacuum, through the helmet).
    internal static class ReactorSound
    {
        static GameObject? sEmitter;
        static GameObject? sListener;
        static uint sHum;
        static float sLastPulse = -1f, sNextWarning;
        static bool sLogged, sFailedLogged;

        internal static void Destroy()
        {
            if (sEmitter != null)
            {
                try { AkSoundEngine.StopAll(sEmitter); AkSoundEngine.UnregisterGameObj(sEmitter); } catch { }
                Object.Destroy(sEmitter);
            }
            sEmitter = null;
            sHum = 0;
            sLastPulse = -1f;
            sTouchSet = false;
            sRumbleAt = -1f;
            if (sRumbleUntil > 0f) StopRumble();
        }

        // The game hears through its own audio service (Carbon.Audio), not a Wwise AkAudioListener: the service's
        // "Listener" audio object is Wwise's default listener, its proxy GameObject the one Wwise knows it by, and the
        // scene's GlobalAudioListener is where it sits. Found by reflection; looked for once a second until found.
        static Transform? sEar;
        static float sNextListenerLook;

        static void FindListener()
        {
            if (Time.unscaledTime < sNextListenerLook) return;
            sNextListenerLook = Time.unscaledTime + 1f;
            try
            {
                var type = System.Type.GetType("Carbon.Audio.Unity.GlobalAudioListener, Carbon.Audio.Unity");
                if (type == null) return;
                var global = Object.FindObjectOfType(type) as Component;
                if (global == null) return;
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var service = type.GetField("mAudioService", all)?.GetValue(global);
                var listener = service?.GetType().GetProperty("Listener", all)?.GetValue(service);
                var proxy = listener?.GetType().GetProperty("ProxyObject", all)?.GetValue(listener) as GameObject;
                if (proxy == null) return;
                sEar = global.transform;
                sListener = proxy;
                Plugin.Log.LogInfo($"[Reactor] the game's audio listener found ('{proxy.name}'): the reactor fades with distance.");
            }
            catch (System.Exception ex)
            {
                sNextListenerLook = float.MaxValue;
                Plugin.Log.LogWarning("[Reactor] could not find the game's audio listener: " + ex.Message);
            }
        }

        // A one-off sound from the reactor (from where its sounds come from).
        internal static void Play(string name)
        {
            if (Plugin.ReactorSounds.Value) Post(name);
        }

        static uint Post(string name)
        {
            if (sEmitter == null || string.IsNullOrWhiteSpace(name)) return 0;
            uint id;
            try { id = AkSoundEngine.PostEvent(name.Trim(), sEmitter); }
            catch (System.Exception ex)
            {
                if (!sFailedLogged) { sFailedLogged = true; Plugin.Log.LogWarning($"[Reactor] sound '{name}' could not be played: {ex.Message}"); }
                return 0;
            }
            if (id == 0 && !sFailedLogged) { sFailedLogged = true; Plugin.Log.LogWarning($"[Reactor] sound '{name}' did not play (unknown event, or its sound bank is not loaded)."); }
            return id;
        }

        const float CountdownSeconds = 5f;

        // Every frame. pulse: 0..1 through the ball's cycle (its height at 0.5); imbalance in injectors' worth; fade 1..0;
        // nextStep: seconds until the plasma goes up a level by itself, -1 if it is not counting down.
        static bool sTouchSet;
        static float sRumbleUntil, sRumbleAt = -1f, sRumbleFrom, sRumbleStrength;

        // A rumble on the controller in use, as the game does for its tools (scaled by the game's vibration setting), at the
        // height of the pulse, fading over a quarter second.
        const float RumbleSeconds = 0.25f;
        static void Rumble(float strength)
        {
            if (!Plugin.ReactorHaptics.Value || strength <= 0.01f) return;
            sRumbleStrength = Mathf.Clamp01(strength);
            sRumbleAt = Time.unscaledTime;
        }

        static void RumbleFrame()
        {
            float now = Time.unscaledTime;
            if (sRumbleAt >= 0f && now >= sRumbleAt)
            {
                sRumbleAt = -1f;
                sRumbleFrom = now;
                sRumbleUntil = now + RumbleSeconds;
            }
            if (sRumbleUntil <= 0f) return;
            if (now >= sRumbleUntil) { StopRumble(); return; }
            float left = 1f - (now - sRumbleFrom) / (sRumbleUntil - sRumbleFrom);
            try
            {
                var device = InControl.InputManager.ActiveDevice;
                var controls = BBI.Unity.Game.LynxControls.Instance;
                if (device == null || device == InControl.InputDevice.Null || controls == null) return;
                float s = sRumbleStrength * left * left;
                device.Vibrate(controls.GetVibrationIntensity(s), controls.GetVibrationIntensity(s * 0.4f));
            }
            catch { }
        }

        static void StopRumble()
        {
            sRumbleUntil = 0f;
            try { InControl.InputManager.ActiveDevice?.StopVibration(); } catch { }
        }
        static readonly uint sTouchRtpc = AkSoundEngine.GetIDFromString("Touch_Transfer_Active_Grab");

        internal static void Show(Vector3 at, float pulse, float imbalance, bool unstable, float fade, float nextStep, bool touching)
        {
            if (fade <= 0.01f || (!Plugin.ReactorSounds.Value && !Plugin.ReactorHaptics.Value)) { Destroy(); return; }
            bool sounds = Plugin.ReactorSounds.Value;               // the rumble runs without them
            if (sEmitter == null)
            {
                sEmitter = new GameObject("TheExpanse_ReactorSound");
                try { AkSoundEngine.RegisterGameObj(sEmitter, "TheExpanse_ReactorSound"); } catch { }
            }
            // Volume by distance from where the game hears from.
            if (sListener == null) FindListener();
            var ear = sEar != null ? sEar.position : (Camera.main != null ? Camera.main.transform.position : at);
            float near = Mathf.Max(0f, Plugin.ReactorSoundNear.Value), far = Mathf.Max(near + 0.1f, Plugin.ReactorSoundFar.Value);
            float distance = Vector3.Distance(ear, at);

            // The game's sounds carry their own fall-off with distance, made for small things (a generator, a clang), which
            // left the reactor barely heard even up close. So the sound source sits just in front of the ear, in the
            // ball's direction (so it still comes from the ball's side), and the fading by distance is our own, below.
            var source = distance > 1f ? ear + (at - ear) / distance : at;
            sEmitter.transform.position = source;
            try { AkSoundEngine.SetObjectPosition(sEmitter, source, Vector3.forward, Vector3.up); } catch { }
            float reach = Mathf.Clamp01((far - distance) / (far - near));
            // Holding the reactor: the game's touch transfer on our sound source (heard through the suit, in vacuum too), at
            // full volume.
            if (touching != sTouchSet)
            {
                sTouchSet = touching;
                try { AkSoundEngine.SetRTPCValue(sTouchRtpc, touching ? 100f : 0f, sEmitter); } catch { }
            }
            if (touching) reach = 1f;
            float volume = reach * fade * (touching ? 1f : 0.6f + 0.4f * Mathf.Clamp01(imbalance / 2.4f));
            if (sListener != null) { try { AkSoundEngine.SetGameObjectOutputBusVolume(sEmitter, sListener, volume); } catch { } }

            if (!sLogged)
            {
                sLogged = true;
                Plugin.Log.LogInfo($"[Reactor] sounds from the ball: hum '{Plugin.ReactorHumSound.Value}', " +
                                   $"warning '{Plugin.ReactorWarningSound.Value}'; heard fully within {near:0} m, gone at {far:0} m" +
                                   (sListener != null ? "." : "; no audio listener found yet, so no fading by distance until it is."));
            }

            bool audible = reach > 0.001f || sListener == null;
            // Hum: running while in earshot, stopped beyond it.
            if (sounds && audible && sHum == 0) sHum = Post(Plugin.ReactorHumSound.Value);
            else if ((!sounds || !audible) && sHum != 0) { try { AkSoundEngine.StopPlayingID(sHum, 500); } catch { } sHum = 0; }

            // Pulse: a rumble on a controller as the core reaches its height.
            if (sLastPulse >= 0f && audible && sLastPulse < 0.5f && pulse >= 0.5f)
            {
                Rumble(touching ? 1f : reach * (0.35f + 0.5f * Mathf.Clamp01(imbalance / 2.4f)));
            }
            RumbleFrame();
            sLastPulse = pulse;

            // Warning: a beep once a second while unstable; in the countdown to the next level, faster as it nears, from
            // once a second to four.
            bool counting = nextStep >= 0f && nextStep <= CountdownSeconds;
            float interval = counting ? Mathf.Lerp(0.25f, 1f, nextStep / CountdownSeconds) : 1f;
            sNextWarning = Mathf.Min(sNextWarning, Time.time + interval);      // a faster beat starts at once
            if (sounds && (unstable || counting) && audible && Time.time >= sNextWarning)
            {
                sNextWarning = Time.time + interval;
                Post(Plugin.ReactorWarningSound.Value);
            }
        }
    }
}
