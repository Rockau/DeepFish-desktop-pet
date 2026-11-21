using System.IO;
using System.Text.Json;

namespace FishPet;

// The voice branch indexes its Japanese recordings by the Chinese subtitle.
internal sealed class RecordedVoice
{
    private readonly Dictionary<string, string> recordings = new(StringComparer.Ordinal);

    internal int Count => recordings.Count;

    internal static RecordedVoice Load(string folder)
    {
        var voice = new RecordedVoice();
        try
        {
            string path = Path.Combine(folder, "index.json");
            if (!File.Exists(path)) return voice;
            var index = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            foreach (var (line, file) in index ?? [])
            {
                if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file
                    || !file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;
                string recording = Path.Combine(folder, file);
                if (File.Exists(recording)) voice.recordings[Normalize(line)] = recording;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Places.Log("无法读取固定台词语音索引：" + error.GetType().Name); }
        return voice;
    }

    internal string? Find(string text)
    {
        string line = Normalize(text);
        return recordings.GetValueOrDefault(line)
            ?? recordings.GetValueOrDefault(line.Replace("老大", "主人", StringComparison.Ordinal));
    }

    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
