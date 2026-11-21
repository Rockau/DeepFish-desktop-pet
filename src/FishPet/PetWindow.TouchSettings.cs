using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FishPet;

internal sealed partial class PetWindow
{
    private sealed record HitBox(double X, double Y, double Width, double Height)
    {
        internal bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
        internal bool Valid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
            && X >= 0 && Y >= 0 && Width >= .01 && Height >= .01 && X + Width <= 1.000001 && Y + Height <= 1.000001;
    }

    private sealed record LegacyTouchRule(string Shape, string Zone, HitBox? Rect, string Click, string Hover, string Stroke);
    private readonly record struct TouchBox(string Shape, string Button, string Zone);
    private sealed record TouchRegion(TouchBox Box, string Label, HitBox Rect, string? Action, string? Hover,
        string Gesture, bool BuiltIn, bool Custom)
    {
        internal string Id => Box.Zone;
    }
    // An override without a box keeps the outfit-aware default geometry; a tombstone hides a built-in only in this list.
    private sealed record TouchOverride(string Shape, string Button, string Id, string Label, HitBox? Rect,
        string? Action, string? Hover, string Gesture, bool Deleted = false);
    private sealed record TouchConfiguration(int Version, List<TouchOverride> Regions);

    private static readonly (string Id, string Label)[] TouchZones =
        [("face", "脸"), ("head", "头"), ("hand", "手"), ("chest", "胸口"), ("foot", "脚"), ("waist", "腰"),
            ("skirt", "裙子"), ("tail", "尾巴"), ("body", "身体")];
    private static readonly (string Id, string Label, string Gesture)[] RightTouchZones =
        [("face", "脸", "stroke"), ("head", "头", "stroke"), ("foot", "脚", "foot"),
            ("waist", "腰", "waist"), ("skirt", "裙子", "skirt")];
    private static readonly JsonSerializerOptions TouchJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string TouchFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Places.SettingsFile))!, "touch-settings.json");
    private List<TouchOverride>? touchOverrides;
    private List<TouchOverride> TouchOverrides => touchOverrides ??= LoadTouchOverrides();

    private List<TouchOverride> LoadTouchOverrides()
    {
        if (!File.Exists(TouchFile)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(TouchFile));
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                return MigrateTouchOverrides(document.RootElement.Deserialize<List<LegacyTouchRule>>(TouchJson) ?? []);
            var saved = document.RootElement.Deserialize<TouchConfiguration>(TouchJson);
            if (saved is not { Version: 2, Regions: not null }) return [];
            return saved.Regions.Where(r => r is not null && ValidShape(r.Shape) && r.Button is "left" or "right" &&
                !string.IsNullOrWhiteSpace(r.Id) && r.Label is { Length: > 0 and <= 32 } &&
                (r.Rect == null || r.Rect.Valid) && ValidCue(r.Action) && ValidCue(r.Hover) && ValidRightGesture(r.Gesture)).ToList();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            Places.Log("无法读取触摸设置：" + error.GetType().Name);
            return [];
        }
    }

    private List<TouchOverride> MigrateTouchOverrides(List<LegacyTouchRule> legacy)
    {
        var result = new List<TouchOverride>();
        foreach (var old in legacy)
        {
            if (old is null || !ValidShape(old.Shape)) continue;
            var (_, label) = Array.Find(TouchZones, item => item.Id == old.Zone);
            if (label == null) continue;
            var rect = old.Rect?.Valid == true ? old.Rect : null;
            result.Add(new(old.Shape, "left", old.Zone, label, rect, LegacyCue(old.Click), LegacyCue(old.Hover), ""));
            if (old.Zone is "face" or "head")
                result.Add(new(old.Shape, "right", old.Zone, label, rect, LegacyCue(old.Stroke), "", "stroke"));
            else if (old.Zone is "waist" or "foot")
                result.Add(new(old.Shape, "right", old.Zone, label, rect, "", "", old.Zone));
            // The old skirt box was shared with clicking and could even be on the shirt. Use the visible skirt for dragging.
        }
        return result;
    }

    private string? LegacyCue(string? cue) => string.IsNullOrWhiteSpace(cue) || !ValidCue(cue) ? null : cue;
    private static bool ValidShape(string? shape) => Shape.All.Any(item => item.Id == shape);
    private static bool ValidRightGesture(string? gesture) => gesture is "" or "stroke" or "pinch" or "skirt" or "waist" or "foot" or "action";

    private bool ValidCue(string? cue) => string.IsNullOrWhiteSpace(cue)
        || (cue.Length <= 120 && (LibraryCue(cue) || Behavior.Moods.ContainsKey(cue) || ImportedActions.Combinations.ContainsKey(cue) || Body.CueReadable(cue, 12)));

    private IEnumerable<TouchRegion> TouchRegions(string shape, bool right)
    {
        string button = right ? "right" : "left";
        var defaults = right
            ? RightTouchZones
            : TouchZones.Select(zone => (zone.Id, zone.Label, Gesture: "")).ToArray();
        var edits = TouchOverrides.Where(edit => edit.Shape == shape && edit.Button == button).ToList();
        foreach (var (id, label, gesture) in defaults)
        {
            var edit = edits.FindLast(rule => rule.Id == id);
            if (edit?.Deleted == true) continue;
            var rect = edit?.Rect ?? TouchBounds(shape, id, right);
            var action = edit != null ? edit.Action : !right || gesture == "stroke" ? null : "";
            var hover = edit != null ? edit.Hover : right ? "" : null;
            yield return new TouchRegion(new(shape, button, id), edit?.Label ?? label, rect, action, hover,
                edit?.Gesture ?? gesture, true, edit?.Rect != null);
        }
        foreach (var edit in edits)
            if (!edit.Deleted && edit.Rect is { } rect && !defaults.Any(zone => zone.Id == edit.Id))
                yield return new TouchRegion(new(shape, button, edit.Id), edit.Label, rect, edit.Action, edit.Hover, edit.Gesture, false, true);
    }

    private TouchRegion? TouchAt(double x, double y, bool right)
    {
        string shape = Shape.Id;
        TouchRegion? best = null;
        TouchRegion? chest = null;
        foreach (var area in TouchRegions(shape, right))
        {
            bool tail = area.Id == "tail";
            if (tail && settings.Outfit.Contains("尾巴开关")) continue;
            if (!area.Rect.Contains(x, y) && !(tail && !area.Custom && DefaultTailOnLeft(shape, x, y))) continue;
            if (area.Id == "chest") chest = area;
            if (best == null || TouchPriority(area) > TouchPriority(best) ||
                TouchPriority(area) == TouchPriority(best) &&
                area.Rect.Width * area.Rect.Height < best.Rect.Width * best.Rect.Height)
                best = area;
        }
        // 旧版的自定义裙子矩形可能盖到胸口。使用默认动作时，让胸口用自己的反应。
        if (chest != null && best is { Id: "skirt", Action: null, Hover: null }) best = chest;
        // A more specific region with its action cleared still overrides a larger enclosing region.
        return right && (best?.Gesture == "" || best?.Gesture == "action" && string.IsNullOrWhiteSpace(best.Action)) ? null : best;
    }

    private static int TouchPriority(TouchRegion area) => area.Id == "body" ? 0 : area.Custom ? 2 : 1;

    // 尾巴从双腿后面绕到另一侧，左边可见的弧段也属于默认尾巴区域。
    private static bool DefaultTailOnLeft(string shape, double x, double y) => shape switch
    {
        "chibi" => x is >= .41 and <= .445 && y is >= .705 and <= .775,
        "full" => x is >= .17 and <= .36 && y is >= .60 and <= .84,
        _ => x is >= .17 and <= .36 && y is >= .70 and <= .95
    };

    private (double X, double Y, double Rx, double Ry) TouchHeadBox() =>
        TouchRegions(Shape.Id, right: true).FirstOrDefault(area => area.Id == "head" && area.Custom)?.Rect is { } rect
        ? (rect.X + rect.Width / 2, rect.Y + rect.Height / 2, rect.Width / 2, rect.Height / 2) : Behavior.HeadBox(Shape.Id);

    private void PlayTouchCue(string cue, int side = 1, bool plainFace = false)
    {
        if (library.FindAction(cue) is { } imported)
        {
            PlayLibraryAction(imported.Category, imported.Item, side, plainFace);
            return;
        }
        if (TryPlayImportedCombination(cue, side, plainFace)) return;
        if (Behavior.Moods.ContainsKey(cue)) behavior.Show(cue, DateTime.UtcNow, side, force: true, plainFace: plainFace);
        else
        {
            behavior.Interrupt();
            PlaySilently("〔" + cue + "〕", filmed: false, plainFace: plainFace);
        }
    }

    private object TouchCommand(string command, JsonElement args)
    {
        try { return EditTouch(command, args); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { return Tools.Text(new { error = error is ArgumentException ? error.Message : "触摸设置无法保存。" }); }
    }

    private object EditTouch(string command, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("触摸设置格式无效。");
        if (command == "touch_preview")
        {
            var cue = Conversation.Text(args, "cue").Trim();
            if (cue.Length == 0 || !ValidCue(cue)) throw new ArgumentException("请选择一个已有动作，或填写可识别的动作词。");
            Wake();
            if (ImportedActions.Combinations.ContainsKey(cue) && ImportedCombinationBlocked)
                throw new ArgumentException("等大肥鱼结束当前说话或互动，再试播这套连招。");
            YieldMutter();
            PlayTouchCue(cue);
            return Tools.Text(new { played = true });
        }

        string shape = Conversation.Text(args, "shape");
        if (shape.Length == 0) shape = Shape.Id;
        if (!ValidShape(shape)) throw new ArgumentException("未知体型。");
        string button = Conversation.Text(args, "button");
        if (button.Length == 0) button = "left";
        if (button is not ("left" or "right")) throw new ArgumentException("请选择左键或右键。");
        bool right = button == "right";
        string? selected = null;
        if (command is "touch_save" or "touch_reset" or "touch_delete")
        {
            string id = Conversation.Text(args, "zone");
            var region = TouchRegions(shape, right).FirstOrDefault(r => r.Id == id);
            bool builtIn = right ? RightTouchZones.Any(z => z.Id == id) : TouchZones.Any(z => z.Id == id);
            bool restoreAll = command == "touch_reset" && id.Length == 0;
            var updated = TouchOverrides.Where(r => r.Shape != shape || r.Button != button ||
                (!restoreAll && r.Id != id)).ToList();

            if (command == "touch_save")
            {
                if (id.Length != 0 && region == null) throw new ArgumentException("这个区域不存在。");
                var rect = TouchOverrides.FindLast(r => r.Shape == shape && r.Button == button && r.Id == id)?.Rect;
                if (args.TryGetProperty("rect", out var value))
                {
                    if (builtIn && value.ValueKind == JsonValueKind.Null) rect = null;
                    else rect = ReadTouchBox(value);
                }
                string label = args.TryGetProperty("label", out _) ? Conversation.Text(args, "label").Trim() : region?.Label ?? "";
                if (label.Length is < 1 or > 32) throw new ArgumentException("区域名称需为 1 到 32 个字符。");
                if (id.Length == 0)
                {
                    if (rect == null) throw new ArgumentException("请先画出新区域的范围。");
                    if (TouchRegions(shape, right).Count() >= 64) throw new ArgumentException("每个体型的左键和右键各支持 64 个区域。");
                    id = "zone-" + Guid.NewGuid().ToString("N");
                }
                string gesture = right
                    ? args.TryGetProperty("gesture", out _) ? Conversation.Text(args, "gesture").Trim() : region?.Gesture ?? ""
                    : "";
                if (!ValidRightGesture(gesture)) throw new ArgumentException("右键拖动方式无效，请从列表选择。");
                string? action = ReadTouchCue(args, right ? "release" : "click", region != null ? region.Action : "");
                string? hover = right ? "" : ReadTouchCue(args, "hover", region != null ? region.Hover : "");
                updated.Add(new TouchOverride(shape, button, id, label, rect, action, hover, gesture));
                selected = id;
            }
            else if (command == "touch_delete")
            {
                if (region == null) throw new ArgumentException("这个区域不存在。");
                if (builtIn) updated.Add(new TouchOverride(shape, button, id, region.Label, null, "", "", "", Deleted: true));
            }
            else if (!restoreAll)
            {
                if (!builtIn) throw new ArgumentException("自定义区域没有默认设置，可编辑或删除。");
                selected = id;
            }
            SaveTouchOverrides(updated);
        }

        var leftRegions = TouchRegions(shape, right: false).ToArray();
        var rightRegions = TouchRegions(shape, right: true).ToArray();
        var leftZones = leftRegions.Select(z => TouchZonePayload(z, right: false)).ToArray();
        var rightZones = rightRegions.Select(z => TouchZonePayload(z, right: true)).ToArray();
        var zones = leftRegions.Select(z => new
        {
            id = z.Id, label = z.Label, rect = TouchRectPayload(z.Rect), custom = z.Custom,
            click = z.Action ?? "", hover = z.Hover ?? "", stroke = rightRegions.FirstOrDefault(r => r.Id == z.Id)?.Action ?? ""
        }).ToArray();
        string? hit = null;
        if (shape == Shape.Id && args.TryGetProperty("point", out var point) && point.ValueKind == JsonValueKind.Object
            && point.TryGetProperty("x", out var px) && px.ValueKind == JsonValueKind.Number && px.TryGetDouble(out double x)
            && point.TryGetProperty("y", out var py) && py.ValueKind == JsonValueKind.Number && py.TryGetDouble(out double y)
            && x is >= 0 and <= 1 && y is >= 0 and <= 1) hit = TouchAt(x, y, right)?.Id;
        var added = ImportedActions.Moods.Keys.Concat(ImportedActions.Combinations.Keys).Concat(LibraryCueNames).ToArray();
        return Tools.Text(new { shape, activeShape = Shape.Id, button, selected, zones, leftZones, rightZones, hit,
            moods = Behavior.Moods.Keys.Concat(ImportedActions.Combinations.Keys).Concat(LibraryCueNames).Order().ToArray(), added,
            preview = shape == Shape.Id ? TouchPreviewImage() : null });
    }

    private static HitBox ReadTouchBox(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("x", out _) || !value.TryGetProperty("y", out _))
            throw new ArgumentException("请填写有效的区域坐标。");
        HitBox? rect;
        try { rect = value.Deserialize<HitBox>(TouchJson); }
        catch (JsonException) { throw new ArgumentException("请填写有效的区域坐标。"); }
        if (rect?.Valid != true) throw new ArgumentException("区域须位于预览范围内，宽高至少 1%。");
        return rect;
    }

    private string? ReadTouchCue(JsonElement args, string key, string? fallback)
    {
        if (!args.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("请选择要使用的互动动作。");
        string cue = value.GetString()!.Trim();
        if (cue == "default") return null;
        if (!ValidCue(cue)) throw new ArgumentException("有未识别的动作词，请检查或从列表选择。");
        return cue;
    }

    private void SaveTouchOverrides(List<TouchOverride> updated)
    {
        var temporary = TouchFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new TouchConfiguration(2, updated), TouchJson));
        File.Move(temporary, TouchFile, overwrite: true);
        touchOverrides = updated;
        hoverZone = null;
    }

    private object TouchZonePayload(TouchRegion zone, bool right) => new
    {
        id = zone.Id, label = zone.Label, rect = TouchRectPayload(zone.Rect), custom = zone.Custom, builtIn = zone.BuiltIn,
        click = right ? "" : zone.Action ?? "default", hover = right ? "" : zone.Hover ?? "default",
        gesture = zone.Gesture, release = right ? zone.Action ?? "default" : "",
        editAction = RegionEditableAction(zone, right)
    };

    private static object TouchRectPayload(HitBox rect) => new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };

    private string? TouchPreviewImage()
    {
        if (image.Source is not BitmapSource frame || frame.PixelWidth < 1 || frame.PixelHeight < 1) return null;
        double scale = Math.Min(1, 600d / frame.PixelHeight);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new TransformedBitmap(frame, new ScaleTransform(scale, scale))));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return Convert.ToBase64String(buffer.ToArray());
    }

    private HitBox FootBox(string shape)
    {
        var box = ExtraTouchDefault(shape, "foot");
        if (!settings.Outfit.Contains(SkirtExpression)) return box;
        // A long skirt should be draggable at its hem. Only the visible tips of the feet retain their default hit box.
        double top = Math.Max(box.Y, Shape.Find(shape).FootAt - .035);
        return box with { Y = top, Height = box.Y + box.Height - top };
    }

    private HitBox TouchBounds(string shape, string zone, bool right)
    {
        if (zone == "foot") return FootBox(shape);
        if (!right || zone != "skirt") return TouchDefault(shape, zone);
        bool longSkirt = settings.Outfit.Contains(SkirtExpression);
        return (shape, longSkirt) switch
        {
            ("chibi", true) => new(.31, .57, .38, .245),
            ("chibi", false) => new(.39, .56, .22, .15),
            ("full", true) => new(.12, .39, .76, .43),
            ("full", false) => new(.22, .39, .56, .18),
            (_, true) => new(.14, .50, .72, .40),
            _ => new(.22, .46, .56, .19)
        };
    }

    private static HitBox TouchDefault(string shape, string zone) => (shape, zone) switch
    {
        (_, "waist" or "foot") => ExtraTouchDefault(shape, zone),
        ("chibi", "head") => new(.20, .07, .60, .31), ("chibi", "face") => new(.27, .33, .46, .20),
        ("chibi", "hand") => new(.22, .54, .56, .12), ("chibi", "skirt") => new(.20, .63, .60, .13),
        ("chibi", "chest") => new(.427, .532, .146, .052),
        ("chibi", "tail") => new(.54, .70, .055, .077), ("chibi", _) => new(.19, .53, .62, .40),
        ("full", "head") => new(.32, .075, .36, .11), ("full", "face") => new(.33, .17, .34, .09),
        ("full", "hand") => new(.18, .46, .64, .10), ("full", "skirt") => new(.20, .42, .60, .15),
        ("full", "chest") => new(.36, .292, .28, .103),
        ("full", "tail") => new(.63, .57, .34, .37), ("full", _) => new(.28, .26, .44, .68),
        (_, "head") => new(.27, .10, .46, .135), (_, "face") => new(.30, .21, .40, .11),
        (_, "hand") => new(.17, .55, .66, .10), (_, "skirt") => new(.18, .49, .64, .17),
        (_, "chest") => new(.35, .347, .30, .101),
        (_, "tail") => new(.62, .66, .35, .31), _ => new(.22, .32, .56, .65)
    };
}
