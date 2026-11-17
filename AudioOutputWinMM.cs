// PROTOTYPE (2026-11-17): arcade flight mode — sound: Windows device output through winmm waveOut.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SolarSystem;

// Design:
//  * One dedicated thread (above-normal priority) owns the device from open to close:
//    every waveOut call happens on it, so there are no cross-thread races with the driver.
//  * CALLBACK_EVENT: the driver signals an event when a block finishes. No driver
//    callback ever enters managed code, so nothing can call back into freed memory.
//  * BlockCount blocks of BlockFrames frames each (16-bit PCM, 48 kHz, stereo); headers and
//    sample data live in native memory (NativeMemory), stable for the device's lifetime.
//    4 × 10 ms keeps total queued latency at 40 ms plus the driver's own buffering.
//  * An underrun (the queue ran dry because the thread was starved) only produces a gap;
//    the loop simply refills and carries on.
//  * Shutdown: the stop flag is set and the event signalled; the thread calls
//    waveOutReset (returns every block), unprepares the headers, closes the device and
//    only then frees the native memory. Stop waits up to a second; if the driver hangs,
//    the memory is deliberately leaked rather than freed under the driver's feet.

/// <summary>Plays an <see cref="AudioEngine"/> on the default Windows audio device (winmm).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class AudioOutputWinMM : IAudioOutput
{
    /// <summary>Frames per device block (10 ms at 48 kHz).</summary>
    public const int BlockFrames = 480;
    /// <summary>Blocks kept queued on the device.</summary>
    public const int BlockCount = 4;

    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const uint CALLBACK_EVENT = 0x00050000;
    private const uint WHDR_DONE = 0x00000001;
    private const uint MMSYSERR_NOERROR = 0;
    private const ushort WAVE_FORMAT_PCM = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutOpen(nint* phwo, uint uDeviceID, WaveFormatEx* pwfx, nint dwCallback, nint dwInstance, uint fdwOpen);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutPrepareHeader(nint hwo, WaveHeader* pwh, uint cbwh);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutUnprepareHeader(nint hwo, WaveHeader* pwh, uint cbwh);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutWrite(nint hwo, WaveHeader* pwh, uint cbwh);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutReset(nint hwo);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutClose(nint hwo);

    private readonly AudioEngine _engine;
    private readonly AutoResetEvent _event = new(false);
    private readonly ManualResetEventSlim _opened = new(false);
    private readonly float[] _mix = new float[BlockFrames * AudioFormat.Channels];
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _running;
    private bool _openOk;
    private long _framesWritten;
    private int _underruns;

    public AudioOutputWinMM(AudioEngine engine) => _engine = engine;

    public bool IsRunning => _running;
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>Open the device on the audio thread; false when it cannot be opened.</summary>
    public bool Start()
    {
        _thread = new Thread(ThreadMain)
        {
            Name = "SolarSystem audio",
            IsBackground = true,                 // never keeps the process alive at exit
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        if (!_opened.Wait(TimeSpan.FromSeconds(2)) || !_openOk)
        {
            _stop = true;
            _event.Set();
            return false;
        }
        return true;
    }

    private void ThreadMain()
    {
        nint hwo = 0;
        WaveHeader* headers = null;
        byte* data = null;
        int prepared = 0;
        bool closedCleanly = false;
        try
        {
            var fmt = new WaveFormatEx
            {
                FormatTag = WAVE_FORMAT_PCM,
                Channels = AudioFormat.Channels,
                SamplesPerSec = AudioFormat.SampleRate,
                BitsPerSample = 16,
                BlockAlign = AudioFormat.Channels * 2,
                AvgBytesPerSec = AudioFormat.SampleRate * AudioFormat.Channels * 2,
                Size = 0,
            };
            nint handle = 0;
            uint rc = waveOutOpen(&handle, WAVE_MAPPER, &fmt, _event.SafeWaitHandle.DangerousGetHandle(), 0, CALLBACK_EVENT);
            if (rc != MMSYSERR_NOERROR || handle == 0)
            {
                _opened.Set();
                return;
            }
            hwo = handle;

            int blockBytes = BlockFrames * AudioFormat.Channels * sizeof(short);
            headers = (WaveHeader*)NativeMemory.AllocZeroed((nuint)(sizeof(WaveHeader) * BlockCount));
            data = (byte*)NativeMemory.AllocZeroed((nuint)(blockBytes * BlockCount));
            for (int i = 0; i < BlockCount; i++)
            {
                headers[i].Data = (nint)(data + i * blockBytes);
                headers[i].BufferLength = (uint)blockBytes;
                if (waveOutPrepareHeader(hwo, &headers[i], (uint)sizeof(WaveHeader)) != MMSYSERR_NOERROR)
                {
                    _opened.Set();
                    return;    // finally closes what was opened
                }
                prepared++;
                headers[i].Flags |= WHDR_DONE;   // mark as free so the loop fills it
            }

            _running = true;
            _openOk = true;
            _opened.Set();

            while (!_stop)
            {
                int free = 0;
                for (int i = 0; i < BlockCount && !_stop; i++)
                {
                    if ((headers[i].Flags & WHDR_DONE) == 0) continue;
                    free++;
                    FillBlock((short*)headers[i].Data);
                    headers[i].Flags &= ~WHDR_DONE;
                    if (waveOutWrite(hwo, &headers[i], (uint)sizeof(WaveHeader)) != MMSYSERR_NOERROR)
                    {
                        _stop = true;   // device lost (unplugged): stop quietly
                        break;
                    }
                    Interlocked.Add(ref _framesWritten, BlockFrames);
                }
                // Every block came back at once: the device ran dry before we refilled.
                if (free == BlockCount && Interlocked.Read(ref _framesWritten) > BlockFrames * BlockCount)
                    Interlocked.Increment(ref _underruns);
                _event.WaitOne(20);
            }

            waveOutReset(hwo);   // returns every queued block (marks them done)
            for (int i = 0; i < prepared; i++)
                waveOutUnprepareHeader(hwo, &headers[i], (uint)sizeof(WaveHeader));
            prepared = 0;
            waveOutClose(hwo);
            hwo = 0;
            closedCleanly = true;
        }
        catch
        {
            // Never let the audio thread take the process down.
        }
        finally
        {
            if (hwo != 0)
            {
                try
                {
                    waveOutReset(hwo);
                    for (int i = 0; i < prepared; i++)
                        waveOutUnprepareHeader(hwo, &headers[i], (uint)sizeof(WaveHeader));
                    closedCleanly = waveOutClose(hwo) == MMSYSERR_NOERROR;
                }
                catch
                {
                    closedCleanly = false;
                }
            }
            else if (headers == null || prepared == 0)
            {
                closedCleanly = true;
            }
            // Free only once the driver has let go of the memory.
            if (closedCleanly)
            {
                if (headers != null) NativeMemory.Free(headers);
                if (data != null) NativeMemory.Free(data);
            }
            _running = false;
            _opened.Set();
        }
    }

    private void FillBlock(short* dst)
    {
        var mix = _mix.AsSpan();
        try
        {
            _engine.Render(mix, BlockFrames);
        }
        catch
        {
            mix.Clear();
        }
        for (int k = 0; k < mix.Length; k++)
            dst[k] = WavFile.ToPcm16(mix[k]);
    }

    /// <summary>Stop the thread and close the device; waits up to a second.</summary>
    public void Dispose()
    {
        _stop = true;
        try { _event.Set(); } catch (ObjectDisposedException) { }
        var t = _thread;
        bool joined = t == null || t.Join(TimeSpan.FromSeconds(1));
        _running = false;
        if (joined)
        {
            _event.Dispose();
            _opened.Dispose();
        }
        // If the thread is stuck in the driver, keep the event alive: the driver may still signal it.
    }
}
