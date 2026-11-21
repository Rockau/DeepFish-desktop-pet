using System.Globalization;
using System.IO;

namespace FishPet;

internal sealed record SkitStep(string Text, string? Cue, double Seconds);
internal sealed record Skit(string[] Props, SkitStep[] Steps);
internal sealed record StockSkit(PerformanceItem Item, bool Hidden);

internal static class SkitScript
{
    // Alongside the old skits.txt format, shared skits keep the body of a single script in JSON.
    internal static Skit Parse(string? script, bool strict)
    {
        if (script == null || (strict && script.Length > 6000))
            throw new ArgumentException("小剧场须有台词或动作，正文最多 6000 字。");
        var props = new List<string>();
        var steps = new List<SkitStep>();
        foreach (var row in script.Split('\n'))
        {
            var line = row.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("@道具", StringComparison.Ordinal))
            {
                var words = line[3..].Split([' ', '　', '、', '，', ','], StringSplitOptions.RemoveEmptyEntries);
                if (strict && words.Any(word => !IsProp(word)))
                    throw new ArgumentException("小剧场里有无法识别的临时道具。");
                props.AddRange(words);
                continue;
            }

            var fields = line.Split('|', 3, StringSplitOptions.TrimEntries);
            string? cue = fields.Length > 1 && Body.CueReadable(fields[1], 8) ? fields[1] : null;
            bool timed = fields.Length > 2 && fields[2].Length > 0;
            double seconds = 3.5;
            if (timed && (!double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || !double.IsFinite(seconds)))
                seconds = strict ? double.NaN : 3.5;
            if (strict && (line.StartsWith('#') || line.StartsWith('@') || fields[0].Length > 280 ||
                (fields.Length > 1 && fields[1].Length > 0 && cue == null) ||
                !double.IsFinite(seconds) || seconds is < 1 or > 10))
                throw new ArgumentException("每行填「台词|动作|秒数」，动作须能识别，时长为 1 到 10 秒。");
            if (fields[0].Length > 0 || cue != null)
                steps.Add(new(fields[0], cue, Math.Clamp(seconds, 1, 10)));
        }
        if (strict && steps.Count is < 1 or > 24)
            throw new ArgumentException("一个小剧场须有 1 到 24 行台词或动作。");
        return new([.. props.Distinct()], [.. steps]);
    }

    // Older scripts accepted longer timings and ignored unknown cues. Share the effect she actually plays.
    internal static string ForSharing(string script, Skit parsed)
    {
        try { Parse(script, strict: true); return script; }
        catch (ArgumentException)
        {
            var props = parsed.Props.Where(IsProp).ToArray();
            var steps = parsed.Steps.Select(step =>
                FormattableString.Invariant($"{step.Text}|{step.Cue}|{step.Seconds}"));
            if (props.Length > 0) steps = steps.Prepend("@道具 " + string.Join(' ', props));
            return string.Join('\n', steps);
        }
    }

    private static bool IsProp(string word) => Outfit.Accessories.Any(prop => prop.Label == word || prop.Expression == word);

    internal static StockSkit[] ReadStock(string path)
    {
        if (!File.Exists(path)) return [];
        var result = new List<StockSkit>();
        string? name = null, condition = null;
        var body = new List<string>();
        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(name) && body.Count > 0)
            {
                bool egg = body[0].Trim().StartsWith("//", StringComparison.Ordinal) &&
                    body[0].Trim().TrimStart('/').Trim().StartsWith("彩蛋", StringComparison.Ordinal);
                result.Add(new(new PerformanceItem(name, Script: string.Join('\n', body).Trim(),
                    Condition: condition, AutoPlay: true), egg));
            }
            body.Clear();
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (!line.StartsWith('#'))
            {
                if (name != null) body.Add(raw);
                continue;
            }
            Flush();
            var title = line.TrimStart('#').Trim().Split('@', 2, StringSplitOptions.TrimEntries);
            name = title[0];
            condition = title.Length > 1 ? title[1] : null;
        }
        Flush();
        return result.ToArray();
    }
}
