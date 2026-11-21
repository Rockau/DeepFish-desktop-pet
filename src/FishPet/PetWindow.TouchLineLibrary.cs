using System.IO;
using System.Text.Json;

namespace FishPet;

internal sealed partial class PetWindow
{
    private static string TouchLineLibraryFile => Path.ChangeExtension(TouchLinesFile, ".scoped.json");
    private sealed record TouchLineScope(int Level, TouchBox Box, string Reaction);
    private sealed record TouchLineOverride(TouchLineScope Scope, string? Text, string? Name);
    private sealed record TouchLineLibrary(int Version, TouchLineOverride[] Entries);
    private Dictionary<TouchLineScope, TouchLineOverride>? scopedTouchLines;

    private void LoadTouchLineLibrary()
    {
        try
        {
            if (!File.Exists(TouchLineLibraryFile))
            {
                scopedTouchLines = [];
                return;
            }
            var data = JsonSerializer.Deserialize<TouchLineLibrary>(File.ReadAllText(TouchLineLibraryFile));
            if (data is not { Version: 1, Entries: { } entries }) throw new JsonException();
            var edits = new Dictionary<TouchLineScope, TouchLineOverride>();
            foreach (var entry in entries)
            {
                if (entry?.Scope is not { Level: >= 0, Reaction: { Length: > 0 } } scope
                    || !ValidShape(scope.Box.Shape) || scope.Box.Button is not ("left" or "right")
                    || string.IsNullOrEmpty(scope.Box.Zone) || scope.Level > Affection.Levels.Length || !edits.TryAdd(scope, entry))
                    throw new JsonException();
                if (entry.Text != null) ReadScopedTouchText(entry.Text);
                if (entry.Name != null) ReadTouchLineName(entry.Name);
            }
            scopedTouchLines = edits;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Places.Log("无法读取分档互动台词：" + error.GetType().Name);
        }
    }

    private IEnumerable<(string Text, int Level)> LinesForTouch(TouchLineScope scope, int legacyLevel)
    {
        if (scopedTouchLines?.GetValueOrDefault(scope)?.Text is { } text)
            return ParseTouchLines(text, legacy: false);
        return InheritedTouchLines(scope with { Level = legacyLevel });
    }

    private IEnumerable<(string Text, int Level)> InheritedTouchLines(TouchLineScope scope) =>
        TouchLineGroup(scope.Reaction, scope.Level) is { } group
            ? LegacyLinesForTouch(group).Where(line => line.Level <= scope.Level) : [];

    private static string? TouchClickEvent(string zone, int level) =>
        zone == "face" ? "戳脸" : AffectionTouchReactions.ForZone(zone, level)?.Event;

    private string? StrokeReleaseCue(int level, string kind, TouchRegion? region) =>
        EffectiveAffectionRule(level, kind).Action ?? region?.Action;

    private List<string> TouchLineReactions(TouchRegion region, int level)
    {
        var reactions = new List<string>();

        void Add(string? reaction)
        {
            if (string.IsNullOrEmpty(reaction)) return;
            string group = TouchLineGroup(reaction, level) ?? reaction;
            if (!reactions.Contains(group)) reactions.Add(group);
        }

        void AddDragRelease(string gesture)
        {
            if (gesture.Length == 0) return;
            var kind = RightGestureAffectionEvent(gesture);
            Add((kind != null ? EffectiveAffectionRule(level, kind).Action : null) ?? region.Action);
            if (gesture == "foot") Add(region.Action); // 穿鞋时只执行判定框自己的松手动作。
        }

        if (region.Box.Button == "left")
        {
            if (region.Id == "foot" && region.Action == null)
            {
                var shoeAction = EffectiveAffectionRule(level, "换鞋").Action;
                if (!string.IsNullOrEmpty(shoeAction)) Add(shoeAction);
                else
                {
                    Add("脱鞋");
                    Add("穿鞋");
                }
            }
            else if (region.Action != "")
            {
                var kind = TouchClickEvent(region.Id, level);
                var defaultReaction = Behavior.ClickMood(region.Id, level);
                Add((kind != null ? EffectiveAffectionRule(level, kind).Action : null) ?? region.Action ?? defaultReaction);
                if (region.Action == null && defaultReaction != Behavior.DisgustedBodyClick)
                    Add(EffectiveAffectionRule(level, "挨打").Action ?? "挨打");
            }
            if (region.Hover != "" && Affection.AllowsInitiative(level))
                Add(region.Hover ?? Behavior.HoverMood(region.Id, level));
            Add("头晕中");
            Add("头晕重");
        }
        else if (region.Gesture == "stroke")
        {
            var release = StrokeReleaseCue(level, "摸头", region);
            if (release != null) Add(release);
            else
            {
                foreach (double seconds in new double[] { 1, 4, 9 })
                {
                    Add(HeadStrokeFace.ReleaseAt(level, seconds, 1).Key);
                    Add(HeadStrokeFace.ReleaseAt(level, seconds, 0).Key);
                }
            }
            if (level > 0)
            {
                var melt = StrokeReleaseCue(level, "融化", region);
                if (melt != null) Add(melt);
                else
                {
                    Add(HeadStrokeFace.ReleaseAt(level, 20, 1, "抚摸头4").Key);
                    Add(HeadStrokeFace.ReleaseAt(level, 30, 1, "抚摸头5").Key);
                    Add(HeadStrokeFace.ReleaseAt(level, 20, 0, "抚摸头4").Key);
                }
            }
            var alternate = RightGesture(region, pinchModifier: true);
            if (alternate != region.Gesture) AddDragRelease(alternate);
        }
        else AddDragRelease(region.Gesture);
        return reactions;
    }

    private static string DefaultTouchLineName(TouchBox box, string reaction) => reaction switch
    {
        "抚摸头1" => "摸头不足 3 秒后松手",
        "抚摸头2" => "摸头 3 至不足 8 秒后松手",
        "抚摸头3" => "摸头满 8 秒后松手",
        "抚摸头4" => "摸头满 20 秒的彩蛋",
        "抚摸头5" => "摸头满 30 秒的彩蛋",
        "摸头" when box is { Button: "left", Zone: "head" } => "点击头部",
        "护裙" => "护胸",
        "护裙·嫌弃" or "点胸口·嫌弃" => "护胸（嫌弃）",
        "点胸口·厌恶" => "护胸（厌恶）",
        "心虚·反感" when box is { Button: "left", Zone: "body" or "waist" } => "点击后黑脸",
        "娇羞" when box is { Button: "left", Zone: "chest" } => "戳胸后害羞或爱心眼",
        "开心" when box is { Button: "left", Zone: "hand" } => "点击后回应握手",
        "摸后嫌弃·反感" => "摸头不足 5 秒后嫌弃",
        "摸太久生气·反感" => "摸头满 5 秒后生气",
        "挨打" or "挨打·反感" => "短时间内多次点击",
        "头晕中" or "头晕中·反感" => "拖动后有点晕",
        "头晕重" or "头晕重·反感" => "拖动后很晕",
        _ when reaction.StartsWith("悬停", StringComparison.Ordinal) =>
            reaction.Contains('·') ? "悬停" + reaction[reaction.IndexOf('·')..] : "悬停",
        _ => reaction
    };

    private static string ReadScopedTouchText(string text)
    {
        if (text.Length > 8500) throw new ArgumentException("互动台词过长。");
        return string.Join(Environment.NewLine, ParseTouchLines(text, legacy: false).Select(line => line.Text));
    }

    private static string? ReadTouchLineName(string name)
    {
        name = name.Trim();
        if (name.Length > 48 || name.Any(char.IsControl))
            throw new ArgumentException("反应名称最多 48 个字，请写在一行内。");
        return name.Length == 0 ? null : name;
    }

    private object ScopedTouchLinesCommand(string command, JsonElement args)
    {
        bool reading = command == "touch_lines";
        int level = affection.Level;
        if (args.TryGetProperty("level", out var requestedLevel)
            && (requestedLevel.ValueKind != JsonValueKind.Number || !requestedLevel.TryGetInt32(out level)
                || level < 0 || level > Affection.Levels.Length))
            throw new ArgumentException("请选择要编辑的好感阶段。");

        var edits = scopedTouchLines ?? throw new IOException("分档互动台词无法读取。");
        string shape = Conversation.Text(args, "shape");
        string button = Conversation.Text(args, "button");
        string zone = Conversation.Text(args, "zone");
        if (!ValidShape(shape) || button is not ("left" or "right") || zone.Length == 0)
            throw new ArgumentException("请选择已保存的判定框。");
        var region = TouchRegions(shape, right: button == "right").FirstOrDefault(option => option.Id == zone);
        if (region == null) throw new ArgumentException("这个判定框不存在。");

        var reactions = TouchLineReactions(region, level);
        string reaction = Conversation.Text(args, "reaction");
        if (reading && !reactions.Contains(reaction)) reaction = reactions.FirstOrDefault() ?? "";
        if (!reading && !reactions.Contains(reaction)) throw new ArgumentException("请选择要编辑的互动反应。");

        var scope = new TouchLineScope(level, region.Box, reaction);
        var entry = edits.GetValueOrDefault(scope);
        string defaultName = DefaultTouchLineName(region.Box, reaction);
        if (!reading)
        {
            string? text = entry?.Text;
            string? name = entry?.Name;
            if (command == "touch_lines_reset") text = null;
            else
            {
                if (args.TryGetProperty("text", out var lines))
                    text = lines.ValueKind == JsonValueKind.String ? ReadScopedTouchText(lines.GetString()!)
                        : throw new ArgumentException("互动台词格式无效。");
                if (args.TryGetProperty("displayName", out var label))
                    name = label.ValueKind == JsonValueKind.String ? ReadTouchLineName(label.GetString()!)
                        : throw new ArgumentException("反应名称格式无效。");
                if (name == defaultName) name = null;
            }
            var updatedEntry = text == null && name == null ? null : new TouchLineOverride(scope, text, name);
            if (updatedEntry != entry)
            {
                var updated = new Dictionary<TouchLineScope, TouchLineOverride>(edits);
                if (updatedEntry == null) updated.Remove(scope);
                else updated[scope] = updatedEntry;
                string temporary = TouchLineLibraryFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new TouchLineLibrary(1, [.. updated.Values]),
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    }));
                File.Move(temporary, TouchLineLibraryFile, overwrite: true);
                scopedTouchLines = edits = updated;
                entry = updatedEntry;
            }
        }

        return Tools.Text(new
        {
            levels = Enumerable.Range(0, Affection.Levels.Length + 1).Select(id => new { id, label = Affection.LevelBand(id).Name }),
            reactions = reactions.Select(id => new
            {
                id,
                label = $"{region.Label} · {edits.GetValueOrDefault(new(level, region.Box, id))?.Name ?? DefaultTouchLineName(region.Box, id)}"
            }),
            level,
            shape,
            button,
            zone,
            reaction,
            text = entry?.Text ?? string.Join(Environment.NewLine, InheritedTouchLines(scope).Select(line => line.Text)),
            displayName = entry?.Name ?? defaultName,
            custom = entry?.Text != null
        });
    }
}
