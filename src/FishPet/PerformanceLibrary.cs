using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FishPet;

internal sealed record PerformanceKeyframe([property: JsonRequired] double Time, [property: JsonRequired] double Value);

internal sealed record PerformanceItem(string Name, string? Reference = null, string? Cue = null,
    Dictionary<string, double>? Parameters = null, Dictionary<string, PerformanceKeyframe[]>? Tracks = null,
    string[]? Expressions = null, double? Seconds = null, ActionStyle? Style = null,
    string? Script = null, string? Condition = null, bool AutoPlay = false);

internal sealed record PerformancePack(int Version, string Category, string Model, PerformanceItem[] Items);

internal sealed class PerformanceLibrary
{
    internal const string Expression = "expression", Action = "action", Skit = "skit";
    internal const int Version = 1, MaxPackageBytes = 128 * 1024, MaxItems = 160;
    private const int MaxName = 48;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed record Saved(int Version, string Model, Dictionary<string, PerformanceItem[]> Categories, string[] RemovedSkits);

    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal)
        { "EyeOpen", "FaceAngleX", "FaceAngleY", "FaceAngleZ", "ViewX", "ViewY", "ViewRoll", "ViewSquash", "LeanInput" };
    private static readonly HashSet<string> Effects = new(ActionTuning.Faces.Where(face => face != "无表情"),
        StringComparer.Ordinal) { "加载", "前倾", "竖食指开" };

    private readonly string path;
    private readonly HashSet<string> parameters;
    private readonly Func<string, string, bool> acceptsReference;
    private readonly Func<string, bool> reservedAction;
    private readonly bool readFailed;
    private Saved saved;

    internal string Model { get; }
    internal string[] ParameterIds => parameters.Concat(Controls).Distinct().Order(StringComparer.Ordinal).ToArray();
    internal string Revision { get; private set; } = Guid.NewGuid().ToString("N");
    internal IReadOnlyList<string> RemovedSkits => saved.RemovedSkits;

    internal PerformanceLibrary(string path, string model, HashSet<string> parameters,
        Func<string, string, bool> acceptsReference, Func<string, bool> reservedAction)
    {
        this.path = path;
        Model = model;
        this.parameters = parameters;
        this.acceptsReference = acceptsReference;
        this.reservedAction = reservedAction;
        saved = new(Version, model, [], []);
        try
        {
            if (!File.Exists(path)) return;
            var content = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json);
            if (content is not { Version: Version, Categories: not null, RemovedSkits: not null } || content.Model != model ||
                content.RemovedSkits.Any(name => !ValidName(name)))
                throw new ArgumentException("本机的表演库格式或模型不匹配。");
            foreach (var (category, items) in content.Categories)
                ValidateItems(category, items);
            saved = content;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            readFailed = true;
            Places.Log("无法读取表演库：" + error.Message);
        }
    }

    internal static bool CategoryValid(string category) => category is Expression or Action or Skit;

    internal PerformanceItem[] Entries(string category) => saved.Categories.GetValueOrDefault(category) ?? [];

    internal (string Category, PerformanceItem Item)? FindAction(string name)
    {
        foreach (var category in new[] { Expression, Action })
            if (Entries(category).FirstOrDefault(item => NameEquals(item.Name, name)) is { } item)
                return (category, item);
        return null;
    }

    internal static JsonElement Package(PerformancePack pack) => JsonSerializer.SerializeToElement(pack, Json);

    internal JsonElement Export(string category, PerformanceItem[] items)
    {
        ValidateItems(category, items);
        var package = Package(new(Version, category, Model, items));
        if (!FitsPackage(package)) throw new ArgumentException("整栏内容超过 128 KB，请选一个条目导出。");
        return package;
    }

    private static bool FitsPackage(JsonElement package) => JsonSerializer.SerializeToUtf8Bytes(package, Json).Length <= MaxPackageBytes;

    internal string[] Import(string category, JsonElement source, IEnumerable<string> occupiedNames)
    {
        if (source.ValueKind != JsonValueKind.Object || !FitsPackage(source))
            throw new ArgumentException("请选择有效的表演包，大小须在 128 KB 以内。");
        var pack = source.Deserialize<PerformancePack>(Json);
        if (pack is not { Version: Version } || pack.Category != category || pack.Model != Model)
            throw new ArgumentException("表演包的类别、格式版本或适用模型不匹配。");
        return Add(category, pack.Items, occupiedNames);
    }

    internal string[] Add(string category, PerformanceItem[] items, IEnumerable<string> occupiedNames)
    {
        ValidateItems(category, items);
        var existing = Entries(category);
        if (existing.Length + items.Length > MaxItems)
            throw new ArgumentException("这一栏最多可保存 160 个自定义条目。");
        var occupied = new HashSet<string>(occupiedNames.Concat(existing.Select(item => item.Name)), StringComparer.OrdinalIgnoreCase);
        var added = new List<PerformanceItem>();
        foreach (var item in items)
        {
            var name = item.Name.Trim();
            var candidate = name;
            bool Reserved(string value) => category != Skit && reservedAction(value);
            for (int copy = 2; occupied.Contains(candidate) || Reserved(candidate); copy++)
            {
                if (copy > occupied.Count + 2) throw new ArgumentException($"无法为「{name}」分配名称。");
                var suffix = $"（{copy}）";
                candidate = WithSuffix(name, suffix);
                if (Reserved(candidate)) candidate = WithSuffix(name, suffix, "自定义·");
            }
            occupied.Add(candidate);
            added.Add(item with { Name = candidate });
        }
        var next = new Dictionary<string, PerformanceItem[]>(saved.Categories) { [category] = [.. existing, .. added] };
        Save(saved with { Categories = next });
        return added.Select(item => item.Name).ToArray();
    }

    private static string WithSuffix(string name, string suffix, string prefix = "")
    {
        int length = Math.Min(MaxName - prefix.Length - suffix.Length, name.Length);
        if (length > 0 && char.IsHighSurrogate(name[length - 1])) length--;
        return prefix + name[..length] + suffix;
    }

    internal void SetSkitRemoved(string name, bool removed)
    {
        if (!ValidName(name)) throw new ArgumentException("小剧场名称无效。");
        var hidden = new HashSet<string>(saved.RemovedSkits, StringComparer.OrdinalIgnoreCase);
        bool changed = removed ? hidden.Add(name) : hidden.Remove(name);
        if (changed) Save(saved with { RemovedSkits = hidden.ToArray() });
    }

    private void Save(Saved value)
    {
        if (readFailed) throw new IOException("本机表演库读不出来，请先检查原文件。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        saved = value;
        Revision = Guid.NewGuid().ToString("N");
    }

    private void ValidateItems(string category, PerformanceItem[]? items)
    {
        if (!CategoryValid(category) || items is not { Length: > 0 and <= MaxItems })
            throw new ArgumentException("请选择正确的类别，表演包至少要有一个条目。");
        foreach (var item in items)
        {
            if (item == null || !ValidName(item.Name))
                throw new ArgumentException("名称须为 1 到 48 个字，使用单行文字。");
            if (category == Skit) ValidateSkit(item);
            else ValidateMotion(category, item);
        }
    }

    private static void ValidateSkit(PerformanceItem item)
    {
        if (item.Reference != null || item.Cue != null || item.Parameters != null || item.Tracks != null ||
            item.Expressions != null || item.Seconds != null || item.Style != null ||
            item.Condition is { Length: > 160 } ||
            (item.Condition is { Length: > 0 } condition && Condition.Parse(condition, out _) == null))
            throw new ArgumentException($"小剧场「{item.Name}」的格式或播放条件无效。");
        SkitScript.Parse(item.Script, strict: true);
    }

    private void ValidateMotion(string category, PerformanceItem item)
    {
        bool reference = item.Reference != null, cue = item.Cue != null;
        bool numeric = item.Parameters != null || item.Tracks != null || item.Expressions != null;
        if ((reference ? 1 : 0) + (cue ? 1 : 0) + (numeric ? 1 : 0) != 1 ||
            item.Script != null || item.Condition != null || item.AutoPlay)
            throw new ArgumentException($"「{item.Name}」请给出一个预设、动作词或一组参数。");
        if (item.Style is { } style && (!ActionTuning.Allowed(style) ||
            (!numeric && item.Reference?.StartsWith("Idle:", StringComparison.Ordinal) != true)))
            throw new ArgumentException($"「{item.Name}」的动作调节无效。");

        if (reference && (item.Reference!.Length > 140 || !acceptsReference(category, item.Reference)))
            throw new ArgumentException($"「{item.Name}」引用了本机没有的预设。");
        if (cue && (item.Cue!.Length > 120 || !Body.CueReadable(item.Cue, 12)))
            throw new ArgumentException($"「{item.Name}」有无法识别的动作词。");
        if (!numeric)
        {
            if (item.Seconds != null) throw new ArgumentException("自定义时长用于参数或关键帧动作。");
            return;
        }

        double seconds = item.Seconds ?? 3.5;
        if (!double.IsFinite(seconds) || seconds is < .2 or > 60 ||
            (item.Parameters?.Count ?? 0) + (item.Tracks?.Count ?? 0) + (item.Expressions?.Length ?? 0) == 0)
            throw new ArgumentException($"「{item.Name}」需要可用参数，动作时长须为 0.2 到 60 秒。");
        if (item.Expressions is { } expressions && (expressions.Length > 8 || expressions.Any(effect => !Effects.Contains(effect))))
            throw new ArgumentException($"「{item.Name}」使用了本机未识别的表情。");
        if (item.Parameters is { } holds && (holds.Count > 64 || holds.Any(hold => !ValidParameter(hold.Key) || !ValidValue(hold.Key, hold.Value))))
            throw new ArgumentException($"「{item.Name}」有模型不支持的参数或超范围数值。");
        if (item.Tracks is { } tracks && (tracks.Count > 32 || tracks.Any(track =>
            !ValidParameter(track.Key) || !ValidTrack(track.Key, track.Value, seconds))))
            throw new ArgumentException($"「{item.Name}」的关键帧无效，时间须在动作时长内严格递增。");
    }

    private bool ValidParameter(string id)
    {
        if (Controls.Contains(id)) return true;
        if (id.StartsWith("post:", StringComparison.Ordinal)) id = id[5..];
        return id.Length is > 0 and <= 80 && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            && parameters.Contains(id);
    }

    private static bool ValidTrack(string id, PerformanceKeyframe[]? frames, double seconds)
    {
        if (frames is not { Length: >= 2 and <= 64 }) return false;
        double previous = -1;
        foreach (var frame in frames)
        {
            if (frame == null || !double.IsFinite(frame.Time) || frame.Time < 0 || frame.Time > seconds ||
                Math.Round(frame.Time, 6) <= previous || !ValidValue(id, frame.Value)) return false;
            previous = Math.Round(frame.Time, 6);
        }
        return true;
    }

    private static bool ValidValue(string id, double value)
    {
        double max = id switch
        {
            "EyeOpen" => 1,
            "ViewX" or "ViewY" => .5,
            "ViewSquash" => .25,
            "ViewRoll" => 30,
            "FaceAngleX" or "FaceAngleY" or "FaceAngleZ" or "LeanInput" => 45,
            _ => 100
        };
        return double.IsFinite(value) && Math.Abs(value) <= max;
    }
    private static bool ValidName(string? name) => name is { Length: > 0 and <= MaxName } &&
        name.Trim().Length > 0 && !name.Any(char.IsControl);
    private static bool NameEquals(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);

    internal static (string Name, HashSet<string> Parameters) ModelInfo(string modelPath)
    {
        var name = Path.GetFileName(modelPath).Replace(".model3.json", "", StringComparison.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var model = JsonDocument.Parse(File.ReadAllText(modelPath));
            string? display = model.RootElement.GetProperty("FileReferences").GetProperty("DisplayInfo").GetString();
            if (!string.IsNullOrEmpty(display) && Path.GetFileName(display) == display)
            {
                using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(modelPath)!, display)));
                foreach (var entry in info.RootElement.GetProperty("Parameters").EnumerateArray())
                    if (entry.GetProperty("Id").GetString() is { Length: > 0 } id) ids.Add(id);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Places.Log("无法读取模型的参数表：" + error.Message);
        }
        return (name, ids);
    }

    internal static Mood ToMood(PerformanceItem item)
    {
        var tracks = item.Tracks is { Count: > 0 } motions ? motions.Select(track => (track.Key, string.Join(' ', track.Value.Select(frame =>
            FormattableString.Invariant($"{frame.Time:0.######}:{frame.Value:0.######}"))))).ToArray() : null;
        var pose = new Mood(item.Expressions ?? [], item.Parameters?.Select(parameter => (parameter.Key, parameter.Value)).ToArray() ?? [],
            item.Seconds ?? 3.5, tracks);
        return item.Style?.Apply(pose) ?? pose;
    }
}
