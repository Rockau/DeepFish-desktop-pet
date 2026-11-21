using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace FishPet;

// The voice service supplies a PCM16 WAV. Measure its loudness off the UI thread so the mouth can
// follow the recording with the same lightweight updates used for plain text speech.
internal sealed class VoiceClip : IDisposable
{
    private const int MaxBytes = 24 * 1024 * 1024;
    private const double MaxSeconds = 90;
    private const int SamplesPerSecond = 50;
    private readonly SoundPlayer player;
    private readonly float[] levels;
    private readonly Stopwatch elapsed = new();

    private VoiceClip(SoundPlayer player, float[] levels, double seconds)
    {
        this.player = player;
        this.levels = levels;
        Seconds = seconds;
    }

    internal double Seconds { get; }
    internal bool IsPlaying => elapsed.IsRunning && elapsed.Elapsed.TotalSeconds < Seconds;

    internal static Task<VoiceClip> LoadAsync(byte[] bytes, CancellationToken token, double volume = 1)
        => Task.Run(() => Read(bytes, token, volume), token);

    internal static VoiceClip LoadFile(string path, double volume)
    {
        if (new FileInfo(path).Length is < 44 or > MaxBytes) throw new InvalidDataException("语音文件大小无效。");
        return Read(File.ReadAllBytes(path), CancellationToken.None, volume);
    }

    private static VoiceClip Read(byte[] bytes, CancellationToken token, double volume)
    {
        if (bytes.Length is < 44 or > MaxBytes) throw new InvalidDataException("语音文件大小无效。");
        var wav = bytes.AsSpan();
        if (!wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("语音不是 WAV 文件。");

        ReadOnlySpan<byte> format = default;
        Span<byte> samples = default;
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(wav[4..]);
        bool streaming = riffSize == uint.MaxValue;
        long limit = streaming ? wav.Length : 8L + riffSize;
        if (limit < 12 || limit > wav.Length) throw new InvalidDataException("WAV 文件不完整。");
        int end = (int)limit;
        int start = 12;
        int streamingDataLength = -1;
        while (start <= end - 8)
        {
            var tag = wav.Slice(start, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(wav[(start + 4)..]);
            bool data = tag.SequenceEqual("data"u8);
            // A non-seekable FFmpeg output leaves both sizes unknown. The data chunk then runs to EOF.
            if (streaming && data && length == uint.MaxValue)
            {
                streamingDataLength = start + 4;
                length = (uint)(end - start - 8);
            }
            start += 8;
            if (length > end - start) throw new InvalidDataException("WAV 文件不完整。");
            if (tag.SequenceEqual("fmt "u8)) format = wav.Slice(start, (int)length);
            if (data) samples = wav.Slice(start, (int)length);
            start += (int)length + ((int)length & 1);
        }
        if (start != end) throw new InvalidDataException("WAV 文件不完整。");

        if (format.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(format) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) != 16)
            throw new InvalidDataException("语音必须是 16 位 PCM WAV。");
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
        int frameSize = channels * sizeof(short);
        if (channels is < 1 or > 2 || rate is < 8000 or > 96000 || samples.Length < frameSize
            || samples.Length % frameSize != 0 || BinaryPrimitives.ReadUInt16LittleEndian(format[12..]) != frameSize)
            throw new InvalidDataException("语音声道或采样率无效。");

        int frames = samples.Length / frameSize;
        double seconds = (double)frames / rate;
        if (seconds > MaxSeconds) throw new InvalidDataException("单段语音不能超过 90 秒。");

        var levels = new float[(int)Math.Ceiling(seconds * SamplesPerSecond)];
        double previous = 0;
        for (int index = 0; index < levels.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            int first = index * (int)rate / SamplesPerSecond;
            int last = Math.Min(frames, (index + 1) * (int)rate / SamplesPerSecond);
            double energy = 0;
            for (int offset = first * frameSize; offset < last * frameSize; offset += sizeof(short))
            {
                var pcm = BinaryPrimitives.ReadInt16LittleEndian(samples[offset..]);
                double sample = pcm / 32768.0;
                energy += sample * sample;
                if (volume != 1) BinaryPrimitives.WriteInt16LittleEndian(samples[offset..], (short)Math.Round(pcm * volume));
            }
            double rms = Math.Sqrt(energy / (Math.Max(1, last - first) * channels));
            double target = Math.Clamp((rms - .008) * 5, 0, 1);
            previous += (target - previous) * (target > previous ? .62 : .42);
            levels[index] = (float)previous;
        }

        if (streaming) BinaryPrimitives.WriteUInt32LittleEndian(wav[4..], (uint)(end - 8));
        if (streamingDataLength >= 0) BinaryPrimitives.WriteUInt32LittleEndian(wav[streamingDataLength..], (uint)samples.Length);
        var player = new SoundPlayer(new MemoryStream(bytes, writable: false));
        try { player.Load(); }
        catch { player.Dispose(); throw; }
        return new VoiceClip(player, levels, seconds);
    }

    internal bool TryPlay()
    {
        try
        {
            player.Play();
            elapsed.Start();
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or Win32Exception)
        {
            Places.Log("无法播放对话语音：" + error.GetType().Name);
            return false;
        }
    }

    internal double MouthLevel
    {
        get
        {
            int index = (int)(elapsed.Elapsed.TotalSeconds * SamplesPerSecond);
            return elapsed.IsRunning && index < levels.Length ? levels[index] : 0;
        }
    }

    public void Dispose()
    {
        if (elapsed.IsRunning)
        {
            elapsed.Stop();
            player.Stop();
        }
        player.Stream?.Dispose();
        player.Dispose();
    }
}
