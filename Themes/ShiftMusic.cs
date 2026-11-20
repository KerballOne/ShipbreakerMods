using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BBI.Unity.Game;
using Carbon.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UnityEngine;

namespace Themes
{
    // Plays the user's own music during a shift.
    //
    // The game's sound all goes through Wwise and Unity's built-in audio is switched off in this build, so the tracks are
    // played with NAudio through a Windows output of our own. That means none of the game's audio handling applies and
    // this class does it itself: it follows the game's music and master volume sliders, pauses when the game is paused,
    // in a cutscene or not the focused window, and turns the game's own music down while it is in use.
    //
    // Tracks are the WAV and MP3 files in the music folder (Audio\Shift beside the DLL), shuffled so that each is played
    // once before any repeats. Playback pauses and resumes with the game, and the place in the current track is kept between shifts, so a long track
    // carries on where it left off. Tracks follow one another without a gap (the next is opened shortly before the
    // current one ends) and a lone track loops straight back to its start. With no tracks in the folder nothing is played
    // and the game's music is left alone.
    //
    // Two hotkeys step to the next and the previous track. The tracks heard so far are kept in order, so stepping back
    // and then forward again goes through the same tracks before the shuffle picks new ones.
    internal static class ShiftMusic
    {
        const int Rate = 44100;
        const float PauseFadeSeconds = 0.25f, PreloadSeconds = 20f;
        static readonly WwiseShortID sStockMusicVolume = WwiseShortID.HashString("settings_vol_music");     // the game's own music slider

        static WaveOutEvent? sDevice;
        static Mixer? sMixer;
        static bool sDeviceFailed, sDevicePlaying, sLoading, sFirstTrack = true, sStockDucked, sWasOutOfShift = true;
        static readonly List<string> sTracks = new List<string>();
        static readonly HashSet<string> sPlayedThisRound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string sLast = "";
        // Every track started so far, in order; the mixer reports which entry it is playing. Stepping back and forward
        // walks this list, and a track is only drawn from the shuffle once the end of it is reached.
        static readonly List<string> sHistory = new List<string>();
        static readonly HashSet<string> sUnplayable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static int sStep;
        static float sNextScan, sNextDuck;
        static readonly System.Random sRandom = new System.Random();

        // Results handed back from the loader thread, picked up on the main thread.
        static readonly object sLock = new object();
        static string? sLoadedMessage, sLoadError, sBadTrack;

        // Called every frame.
        internal static void Tick()
        {
            lock (sLock)
            {
                if (sLoadedMessage != null) { Plugin.Log.LogInfo(sLoadedMessage); sLoadedMessage = null; }
                if (sLoadError != null) { Plugin.Log.LogError(sLoadError); sLoadError = null; }
                if (sBadTrack != null) { sTracks.Remove(sBadTrack); sUnplayable.Add(sBadTrack); sBadTrack = null; }     // not offered again until the next scan
            }

            GameSession.GameState state;
            try { state = GameSession.CurrentGameState; } catch { return; }
            bool inShift = Plugin.MusicEnabled.Value && !sDeviceFailed &&
                           (state == GameSession.GameState.Gameplay || state == GameSession.GameState.Paused);

            if (inShift && Time.unscaledTime >= sNextScan) { sNextScan = Time.unscaledTime + 2f; Scan(); }
            bool haveTracks = inShift && sTracks.Count > 0;
            bool audible = haveTracks && state == GameSession.GameState.Gameplay && Application.isFocused;

            if (audible && EnsureDevice())
            {
                sMixer!.LoopLoneTrack = sTracks.Count == 1;
                if (Pressed(Plugin.MusicNextKey.Value)) sStep = 1;
                if (Pressed(Plugin.MusicPrevKey.Value)) sStep = -1;
                if (!sLoading)          // a key pressed while a track is still being opened is acted on once it is open
                {
                    if (sMixer.NeedsTrack) Load(Neighbour(1), false);
                    else if (sStep != 0)
                    {
                        sMixer.DropNext();
                        Load(Neighbour(sStep), false);
                    }
                    // More than one track: have the next one open before this one runs out, so there is no gap.
                    else if (sTracks.Count > 1 && !sMixer.HasNext && sMixer.Remaining < PreloadSeconds) Load(Neighbour(1), true);
                    sStep = 0;
                }
                if (!sDevicePlaying) { sDevice!.Play(); sDevicePlaying = true; }
            }
            if (sMixer != null)
            {
                // Starting and ending a shift fades gently. Pausing, a cutscene or losing focus cuts out and back in
                // quickly, so it behaves like a pause and resumes from the same place.
                bool shiftEdge = audible ? sWasOutOfShift : !inShift;
                sMixer.FadeSeconds = shiftEdge ? Mathf.Max(0.05f, Plugin.MusicFadeSeconds.Value) : PauseFadeSeconds;
                sMixer.Target = audible ? Mathf.Clamp01(LynxAudioUserOptions.MusicVolume * LynxAudioUserOptions.MasterVolume * Plugin.MusicVolume.Value) : 0f;
                if (!inShift) sWasOutOfShift = true;
                else if (audible && sMixer.Target > 0f && !sMixer.Fading) sWasOutOfShift = false;     // the fade-in has finished
                // Once it has faded right out, stop pulling audio so the place in the track is held.
                if (!audible && sDevicePlaying && sMixer.Silent) { sDevice!.Pause(); sDevicePlaying = false; }
            }

            DuckStockMusic(haveTracks);
        }

        // The game re-sends its own slider value whenever options are applied, so the lowered value is re-sent regularly.
        static void DuckStockMusic(bool duck)
        {
            if (duck)
            {
                if (Time.unscaledTime < sNextDuck) return;
                sNextDuck = Time.unscaledTime + 1f;
                Main.EventSystem.Post(SetRTPCEvent.GetGlobalEvent(sStockMusicVolume, LynxAudioUserOptions.MusicVolume * Mathf.Clamp01(Plugin.MusicStockVolume.Value)));
                sStockDucked = true;
            }
            else if (sStockDucked)
            {
                Main.EventSystem.Post(SetRTPCEvent.GetGlobalEvent(sStockMusicVolume, LynxAudioUserOptions.MusicVolume));
                sStockDucked = false;
            }
        }

        static void Scan()
        {
            sTracks.Clear();
            string dir = Path.Combine(Path.GetDirectoryName(Plugin.Instance.Info.Location)!, Plugin.MusicFolder.Value);
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".mp3" || ext == ".wav") sTracks.Add(f);
            }
        }

        static bool EnsureDevice()
        {
            if (sDevice != null) return true;
            if (sDeviceFailed) return false;
            try
            {
                sMixer = new Mixer();
                sDevice = new WaveOutEvent { DesiredLatency = 200 };
                sDevice.Init(sMixer, true);                 // 16-bit out: accepted by every Windows output device
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Music] could not open a Windows audio output; music is off for this session: " + ex.Message);
                sDeviceFailed = true; sDevice = null; sMixer = null;
                return false;
            }
        }

        // Other keys being held (thrusting, say) must not block these, so this does not use KeyboardShortcut.IsDown.
        static bool Pressed(BepInEx.Configuration.KeyboardShortcut key)
        {
            if (key.MainKey == KeyCode.None || !Input.GetKeyDown(key.MainKey)) return false;
            foreach (var modifier in key.Modifiers) if (!Input.GetKey(modifier)) return false;
            return true;
        }

        // The place in the history one step after (1) or before (-1) the track that is playing, passing over tracks that
        // have since left the folder or would not open. Past the end means a new track from the shuffle; with nothing
        // earlier to go back to, it is the playing track again, from its start.
        static int Neighbour(int direction)
        {
            int current = sMixer!.Index, i = current + direction;
            while (i >= 0 && i < sHistory.Count && (sUnplayable.Contains(sHistory[i]) || !sTracks.Contains(sHistory[i]))) i += direction;
            return i < 0 ? Math.Max(0, current) : i;
        }

        // Opens a track on a worker thread: reading a long MP3's index takes a moment and must not stall the game.
        // index is its place in the history; at the end of the history a track is drawn from the shuffle and added.
        // asNext queues it behind the track that is playing; otherwise it starts straight away.
        static void Load(int index, bool asNext)
        {
            string file;
            if (index < sHistory.Count) file = sHistory[index];
            else
            {
                file = Shuffled();
                index = sHistory.Count;
                sHistory.Add(file);
            }
            bool first = sFirstTrack; sFirstTrack = false;
            double roll = sRandom.NextDouble();
            var mixer = sMixer!;
            sLoading = true;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                AudioFileReader? reader = null;
                try
                {
                    reader = new AudioFileReader(file);
                    double length = reader.TotalTime.TotalSeconds;
                    // The first track of a session does not always start from the top, so one long track is not always
                    // heard from its opening minutes.
                    double start = first && length > 600 ? roll * (length - 300) : 0;
                    if (start > 0) reader.CurrentTime = TimeSpan.FromSeconds(start);

                    ISampleProvider chain = reader;
                    if (reader.WaveFormat.Channels == 1) chain = new MonoToStereoSampleProvider(chain);
                    else if (reader.WaveFormat.Channels != 2) throw new InvalidDataException($"{reader.WaveFormat.Channels}-channel audio is not supported (mono or stereo only)");
                    if (chain.WaveFormat.SampleRate != Rate) chain = new WdlResamplingSampleProvider(chain, Rate);

                    if (asNext) mixer.SetNext(chain, reader, index); else mixer.SetTrack(chain, reader, index);
                    lock (sLock) sLoadedMessage = $"[Music] {(asNext ? "next up" : "playing")} '{Path.GetFileName(file)}' ({Clock(length)}" + (start > 0 ? $", from {Clock(start)})." : ").");
                }
                catch (Exception ex)
                {
                    reader?.Dispose();
                    lock (sLock) { sLoadError = $"[Music] could not play '{Path.GetFileName(file)}': {ex.Message}"; sBadTrack = file; }
                }
                finally { sLoading = false; }
            });
        }

        static string Shuffled()
        {
            // True shuffle: every track in the folder is played once before any is heard again. Working from "not yet
            // played this round" (rather than a fixed queue) lets tracks be added or removed while it runs.
            var choices = sTracks.FindAll(t => !sPlayedThisRound.Contains(t));
            if (choices.Count == 0)
            {
                sPlayedThisRound.Clear();
                choices = new List<string>(sTracks);
                if (choices.Count > 1) choices.Remove(sLast);       // a new round never opens with the track that just ended
            }
            string file = choices[sRandom.Next(choices.Count)];
            sPlayedThisRound.Add(file);
            sLast = file;
            return file;
        }

        static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

        internal static void Shutdown()
        {
            try
            {
                if (sStockDucked) DuckStockMusic(false);
                sDevice?.Stop();
                sDevice?.Dispose();
                sMixer?.DisposeTracks();
            }
            catch { }
            sDevice = null; sMixer = null; sDevicePlaying = false;
        }

        // What the output device pulls from, on NAudio's own thread: the current track with a volume that glides towards
        // Target. While silent with a Target of zero it stops reading the track, which is what keeps the place in it.
        // When the track runs out it carries straight on, within the same block of audio, into the track queued as next,
        // or back to its own start if it is the only one.
        sealed class Mixer : ISampleProvider
        {
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
            public volatile float Target;
            public volatile float FadeSeconds = 3f;
            public volatile bool LoopLoneTrack;
            volatile float mGain;
            readonly object mSwap = new object();
            ISampleProvider? mTrack, mNext;
            AudioFileReader? mFile, mNextFile;
            volatile bool mNeedsTrack = true;
            volatile int mIndex = -1;
            int mNextIndex;

            public bool NeedsTrack => mNeedsTrack;
            // Place in the history of the track that is playing, or that played last; -1 before the first.
            public int Index => mIndex;
            public bool HasNext { get { lock (mSwap) return mNext != null; } }
            public bool Silent => mGain <= 0.0001f;
            public bool Fading => Math.Abs(mGain - Target) > 0.0001f;

            // Seconds left in the current track (a huge number when nothing is loaded, so nothing is queued behind it).
            public double Remaining
            {
                get { lock (mSwap) return mFile == null ? double.MaxValue : (mFile.TotalTime - mFile.CurrentTime).TotalSeconds; }
            }

            public void SetTrack(ISampleProvider track, AudioFileReader file, int index)
            {
                lock (mSwap)
                {
                    mFile?.Dispose();
                    mTrack = track; mFile = file; mIndex = index;
                    mNeedsTrack = false;
                }
            }

            public void SetNext(ISampleProvider track, AudioFileReader file, int index)
            {
                lock (mSwap)
                {
                    // The playing track ran out while this one was being opened: it starts now instead of waiting.
                    if (mTrack == null) { mFile?.Dispose(); mTrack = track; mFile = file; mIndex = index; mNeedsTrack = false; return; }
                    mNextFile?.Dispose();
                    mNext = track; mNextFile = file; mNextIndex = index;
                }
            }

            public void DropNext()
            {
                lock (mSwap)
                {
                    mNextFile?.Dispose();
                    mNext = null; mNextFile = null;
                }
            }

            public void DisposeTracks()
            {
                lock (mSwap)
                {
                    mFile?.Dispose(); mNextFile?.Dispose();
                    mTrack = mNext = null; mFile = mNextFile = null;
                }
            }

            public int Read(float[] buffer, int offset, int count)
            {
                float target = Target, gain = mGain;
                int read = 0;
                lock (mSwap)
                {
                    if (mTrack == null || (gain <= 0f && target <= 0f))
                    {
                        Array.Clear(buffer, offset, count);
                        return count;
                    }

                    // Fill the block; at the end of a track continue from the next one (or from the top) without a gap.
                    int turns = 0;
                    while (read < count && mTrack != null && turns++ < 4)
                    {
                        int n;
                        try { n = mTrack.Read(buffer, offset + read, count - read); } catch { n = 0; }
                        read += n;
                        if (read >= count) break;

                        if (mNext != null)
                        {
                            mFile?.Dispose();
                            mTrack = mNext; mFile = mNextFile; mIndex = mNextIndex;
                            mNext = null; mNextFile = null;
                        }
                        else if (LoopLoneTrack && mFile != null && mFile.Length > 0)
                        {
                            try { mFile.Position = 0; } catch { mTrack = null; }
                        }
                        else mTrack = null;         // nothing queued: the main thread will open another
                    }
                    if (mTrack == null) { mFile?.Dispose(); mFile = null; mNeedsTrack = true; }
                }
                if (read < count) Array.Clear(buffer, offset + read, count - read);

                float step = 1f / (Math.Max(0.01f, FadeSeconds) * Rate);
                for (int i = 0; i + 1 < read; i += 2)
                {
                    if (gain < target) gain = Math.Min(target, gain + step);
                    else if (gain > target) gain = Math.Max(target, gain - step);
                    buffer[offset + i] *= gain;
                    buffer[offset + i + 1] *= gain;
                }
                mGain = gain;
                return count;
            }
        }
    }
}
