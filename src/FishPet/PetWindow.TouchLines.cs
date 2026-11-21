using System.IO;
using System.Text.Json;

namespace FishPet;

internal sealed partial class PetWindow
{
    private static string TouchLinesFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Places.SettingsFile))!, "touch-lines.json");
    private Dictionary<string, string>? touchLineEdits;

    private void LoadTouchLineEdits()
    {
        try
        {
            touchLineEdits = File.Exists(TouchLinesFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TouchLinesFile)) ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Places.Log("无法读取互动台词：" + error.GetType().Name);
        }
    }

    private static string? TouchLineGroup(string reaction, int level)
    {
        if (reaction.StartsWith("娇羞", StringComparison.Ordinal)) reaction = "娇羞";
        if (Behavior.IsFacePokeVariant(reaction)) reaction = "戳脸";
        if (level > 0) return reaction;
        return reaction switch
        {
            "点头部·黑脸" or "点手·缩手" or "点胸口·厌恶" or "护裙·嫌弃" => reaction,
            Behavior.DisgustedBodyClick => "心虚·反感",
            "戳脸" or "震惊" or "心虚" or "挨打" or "摸后嫌弃" or "摸太久生气" or "头晕中" or "头晕重"
                => reaction + "·反感",
            "戳脸·反感" or "震惊·反感" or "心虚·反感" or "挨打·反感" or "摸后嫌弃·反感"
                or "摸太久生气·反感" or "头晕中·反感" or "头晕重·反感" => reaction,
            _ => null
        };
    }

    private IEnumerable<(string Text, int Level)> LegacyLinesForTouch(string reaction)
    {
        if (touchLineEdits?.TryGetValue(reaction, out var saved) == true && saved != null)
        {
            try { return ParseTouchLines(saved); }
            catch (ArgumentException) { }
        }
        return touchLines.TryGetValue(reaction, out var defaults) ? defaults : [];
    }

    private static List<(string Text, int Level)> ParseTouchLines(string text, bool legacy = true)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 32) throw new ArgumentException("每个反应最多可保存 32 句台词。");
        var parsed = new List<(string Text, int Level)>(lines.Length);
        foreach (var line in lines)
        {
            if (!legacy)
            {
                if (line.Length > 256) throw new ArgumentException("单句台词最多可写 256 个字。");
                parsed.Add((line, 0));
                continue;
            }
            var fields = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (fields[0].Length is 0 or > 256) throw new ArgumentException("单句台词须为 1 到 256 个字。");
            int level = 0;
            if (fields.Length == 2 && (!fields[1].StartsWith("亲密", StringComparison.Ordinal)
                || !int.TryParse(fields[1].AsSpan(2), out level) || level < 1 || level > Affection.Levels.Length))
                throw new ArgumentException("台词可加上「|亲密3」这样的档位要求，数字可填 1 到 5。");
            parsed.Add((fields[0], level));
        }
        return parsed;
    }

    private object TouchLinesCommand(string command, JsonElement args)
    {
        try
        {
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("互动台词设置格式无效。");
            if (args.TryGetProperty("zone", out _)) return ScopedTouchLinesCommand(command, args);
            var edits = touchLineEdits;
            if (edits == null) throw new IOException("现有互动台词文件无法读取。");
            string[] reactions = touchLines.Keys.Order().ToArray();
            string reaction = Conversation.Text(args, "reaction");
            if (command == "touch_lines" && reaction.Length == 0) reaction = reactions.FirstOrDefault() ?? "";
            if (!touchLines.TryGetValue(reaction, out var defaults)) throw new ArgumentException("请选择要编辑的互动反应。");
            if (command is "touch_lines_save" or "touch_lines_reset")
            {
                var updated = new Dictionary<string, string>(edits);
                if (command == "touch_lines_save")
                {
                    if (!args.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String
                        || value.GetString() is not { Length: <= 8500 } text)
                        throw new ArgumentException("互动台词格式无效，或文本过长。");
                    ParseTouchLines(text);
                    updated[reaction] = text;
                }
                else updated.Remove(reaction);
                string temporary = TouchLinesFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(updated, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
                File.Move(temporary, TouchLinesFile, overwrite: true);
                touchLineEdits = edits = updated;
            }
            bool custom = edits.TryGetValue(reaction, out var saved) && saved != null;
            string shown = custom ? saved! : string.Join(Environment.NewLine,
                defaults.Select(line => line.Level > 0 ? $"{line.Text}|亲密{line.Level}" : line.Text));
            return Tools.Text(new { reactions, reaction, text = shown, custom });
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Tools.Text(new { error = error is ArgumentException ? error.Message : "互动台词无法读取或保存。" });
        }
    }
}
