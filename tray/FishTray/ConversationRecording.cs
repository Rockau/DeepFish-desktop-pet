using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

namespace FishTray;

internal sealed class ConversationRecording : IDisposable
{
    internal const int SecondsLimit = 20;
    private const int SampleRate = 16000;
    private const int BlockSize = 2;
    private const int ByteLimit = SecondsLimit * SampleRate * BlockSize;
    private readonly MemoryStream buffer = new();
    private readonly TaskCompletionSource<byte[]> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WaveInEvent microphone = new() { WaveFormat = new WaveFormat(SampleRate, 16, 1) };
    private readonly WaveFileWriter writer;
    private int written;
    private bool stopRequested;

    internal ConversationRecording()
    {
        writer = new WaveFileWriter(buffer, microphone.WaveFormat);
        microphone.DataAvailable += OnData;
        microphone.RecordingStopped += OnStop;
        try { microphone.StartRecording(); }
        catch
        {
            microphone.DataAvailable -= OnData;
            microphone.RecordingStopped -= OnStop;
            microphone.Dispose();
            writer.Dispose();
            throw;
        }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        int count = Math.Min(args.BytesRecorded, ByteLimit - written);
        if (count <= 0) return;
        writer.Write(args.Buffer, 0, count);
        written += count;
    }

    private void OnStop(object? sender, StoppedEventArgs args)
    {
        microphone.DataAvailable -= OnData;
        microphone.RecordingStopped -= OnStop;
        try
        {
            microphone.Dispose();
            writer.Dispose();
            if (args.Exception is { } error) completion.TrySetException(error);
            else completion.TrySetResult(written >= SampleRate * BlockSize / 5 ? buffer.ToArray() : []);
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal Task<byte[]> Finish()
    {
        if (!stopRequested && !completion.Task.IsCompleted)
        {
            stopRequested = true;
            microphone.StopRecording();
        }
        return completion.Task;
    }

    public void Dispose()
    {
        try { _ = Finish().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        catch (Exception) { microphone.Dispose(); writer.Dispose(); }
    }
}
