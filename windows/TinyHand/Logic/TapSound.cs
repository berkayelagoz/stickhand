using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;

namespace TinyHand.Logic;

/// <summary>macOS <c>TapSoundChoice</c>: the same 15 tap sounds, same ids and titles.</summary>
public enum TapSoundChoice
{
    Thock,
    Click,
    Tock,
    Clack,
    Typewriter,
    Snap,
    Wood,
    Pop,
    Boop,
    Blip,
    Coin,
    Pew,
    Squeak,
    Quack,
    Chirp,
}

/// <summary>
/// Tap sound playback (NAudio). The WAVs are the macOS <c>assets/sounds/tap_*.wav</c> files,
/// embedded unchanged; a small pool of <see cref="WaveOutEvent"/> voices lets rapid taps
/// overlap instead of cutting each other off.
///
/// Playback runs on its own thread: the low-level hook must never block on audio device
/// setup (a broken output device can stall for seconds, which would freeze the pointer —
/// the same reason macOS plays on a dedicated queue).
/// </summary>
public sealed class TapSound : IDisposable
{
    private const int VoiceCount = 4;
    private const string ResourcePrefix = "TinyHand.assets.sounds.";

    private sealed class Voice : IDisposable
    {
        public required WaveOutEvent Output { get; init; }
        public required WaveFileReader Reader { get; init; }

        public void Dispose()
        {
            try { Output.Stop(); } catch { /* device already gone */ }
            Output.Dispose();
            Reader.Dispose();
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<TapSoundChoice, byte[]> _data = new();
    private readonly BlockingCollection<TapSoundChoice?> _queue = new();
    private readonly Thread _thread;
    private Voice[]? _voices;
    private TapSoundChoice? _voicesChoice;
    private int _nextVoice;
    private volatile bool _enabled = true;
    private volatile TapSoundChoice _choice;
    private int _playCount;
    private volatile bool _disposed;

    public TapSound(TapSoundChoice choice = TapSoundChoice.Thock)
    {
        _choice = choice;
        foreach (var value in Enum.GetValues<TapSoundChoice>())
        {
            var bytes = LoadEmbedded(value);
            if (bytes is not null) _data[value] = bytes;
        }

        _thread = new Thread(PlaybackLoop)
        {
            Name = "TinyHand.TapSound",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Sound on/off (toolbar speaker button, macOS <c>Preferences.handSound</c>).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public TapSoundChoice Choice
    {
        get => _choice;
        set => _choice = value;
    }

    /// <summary>Taps requested since start (harness: proves the down event reached the sound path).</summary>
    public int PlayCount => Volatile.Read(ref _playCount);

    /// <summary>Number of embedded WAVs that decoded (contract: 15).</summary>
    public int LoadedSounds => _data.Count;

    /// <summary>True once the output device is open (harness/diagnostics).</summary>
    public bool Ready => _voices is not null;

    /// <summary>Sound the open voice pool was built for (harness: proves a choice change
    /// actually rebuilt the readers instead of replaying the first sound).</summary>
    public TapSoundChoice? VoicesChoice => _voicesChoice;

    /// <summary>Open the output device ahead of the first tap so the first click is not late.</summary>
    public void Warm() => Enqueue(null);

    /// <summary>Queue one tap. Never blocks the caller (hook thread).</summary>
    public void Play()
    {
        if (!_enabled || _disposed) return;
        Interlocked.Increment(ref _playCount);
        Enqueue(_choice);
    }

    private void Enqueue(TapSoundChoice? choice)
    {
        if (_disposed) return;
        try { _queue.Add(choice); }
        catch (InvalidOperationException) { /* completed: shutting down */ }
    }

    private void PlaybackLoop()
    {
        try
        {
            foreach (var choice in _queue.GetConsumingEnumerable())
            {
                if (choice is null)
                {
                    EnsureVoices(_choice);
                    continue;
                }
                PlayNow(choice.Value);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"tap sound thread stopped: {ex.Message}");
        }
    }

    private void PlayNow(TapSoundChoice choice)
    {
        if (!_enabled) return;
        if (!_data.ContainsKey(choice)) return;
        if (!EnsureVoices(choice)) return;

        var voices = _voices!;
        var voice = voices[_nextVoice % voices.Length];
        _nextVoice++;
        try
        {
            voice.Output.Stop();
            voice.Reader.Position = 0;
            voice.Output.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"tap sound failed: {ex.Message}");
            DisposeVoices();
        }
    }

    /// <summary>Open (or rebuild) the voice pool for <paramref name="choice"/>. The readers
    /// are bound to one WAV, so a different sound must rebuild them — the old code kept the
    /// first choice's readers and every later tap replayed that sound.</summary>
    private bool EnsureVoices(TapSoundChoice choice)
    {
        lock (_gate)
        {
            if (_voices is not null && _voicesChoice == choice) return true;
            DisposeVoicesLocked();
            if (!_data.TryGetValue(choice, out var bytes)) return false;
            try
            {
                var voices = new Voice[VoiceCount];
                for (int i = 0; i < voices.Length; i++)
                {
                    var reader = new WaveFileReader(new MemoryStream(bytes, writable: false));
                    var output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 2 };
                    output.Init(reader);
                    voices[i] = new Voice { Output = output, Reader = reader };
                }
                _voices = voices;
                _voicesChoice = choice;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"tap sound device unavailable: {ex.Message}");
                return false;
            }
        }
    }

    private void DisposeVoices()
    {
        lock (_gate) DisposeVoicesLocked();
    }

    private void DisposeVoicesLocked()
    {
        if (_voices is null) return;
        foreach (var voice in _voices) voice.Dispose();
        _voices = null;
        _voicesChoice = null;
    }

    private static byte[]? LoadEmbedded(TapSoundChoice choice)
    {
        string name = $"{ResourcePrefix}tap_{choice.ToString().ToLowerInvariant()}.wav";
        var asm = typeof(TapSound).Assembly;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            Debug.WriteLine($"embedded tap sound missing: {name}");
            return null;
        }
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        DisposeVoices();
        _queue.Dispose();
    }
}
