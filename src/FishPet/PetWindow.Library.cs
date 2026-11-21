using System.IO;
using System.Text.Json;
using FishTray;

namespace FishPet;

internal sealed partial class PetWindow
{
    private readonly PerformanceLibrary library;
    private static readonly Dictionary<(string Category, string Reference), Item> StockPerformances = Catalog.Build([])
        .Where(section => section.Key != "skit")
        .SelectMany(section => section.Groups.SelectMany(group => group.Items).Select(item =>
            (Category: section.Key == "expr" ? PerformanceLibrary.Expression : PerformanceLibrary.Action, Item: item)))
        .ToDictionary(entry => (entry.Category, entry.Item.Key), entry => entry.Item);

    private StockSkit[] stockSkits = [];
    private DateTime skitFileAt;
    private readonly HashSet<string> autoPlaySkits = [];

    private static PerformanceLibrary OpenLibrary(string? modelPath)
    {
        var (name, parameters) = modelPath == null
            ? (Path.GetFileNameWithoutExtension(Places.ModelFile).Replace(".model3", ""), new HashSet<string>())
            : PerformanceLibrary.ModelInfo(modelPath);
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Places.SettingsFile))!, "performance-library.json");
        return new(path, name, parameters, HasStockReference, name => Behavior.Moods.ContainsKey(name) ||
            ImportedActions.Moods.ContainsKey(name) || ImportedActions.Combinations.ContainsKey(name) || Body.CueReadable(name, 12));
    }

    private static bool HasStockReference(string category, string reference) =>
        StockPerformances.ContainsKey((category, reference)) ||
        (category == PerformanceLibrary.Action && reference.StartsWith("Idle:", StringComparison.Ordinal) && Behavior.Moods.ContainsKey(reference[5..]));

    private bool LibraryCue(string name) => library.FindAction(name) != null;
    private IEnumerable<string> LibraryCueNames => library.Entries(PerformanceLibrary.Expression).Concat(library.Entries(PerformanceLibrary.Action)).Select(item => item.Name);

    private void LoadSkits()
    {
        var path = Path.Combine(Places.Root, "skits.txt");
        try
        {
            stockSkits = SkitScript.ReadStock(path);
            skitFileAt = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Places.Log("无法读取原有小剧场：" + error.Message); }
        RegisterSkits();
    }

    private void RegisterSkits()
    {
        skits.Clear(); skitConds.Clear(); autoPlaySkits.Clear();
        foreach (var item in stockSkits.Select(entry => entry.Item).Concat(library.Entries(PerformanceLibrary.Skit)))
        {
            if (library.RemovedSkits.Contains(item.Name, StringComparer.OrdinalIgnoreCase)) continue;
            string? bad = null;
            var condition = string.IsNullOrEmpty(item.Condition) ? Condition.Always : Condition.Parse(item.Condition, out bad);
            if (condition == null)
            {
                Places.Log($"skits.txt 的条件读不懂「{bad}」，这个小剧场先不用：{item.Name}");
                continue;
            }
            var skit = SkitScript.Parse(item.Script ?? "", strict: false);
            if (skit.Steps.Length == 0) continue;
            skits[item.Name] = skit;
            if (condition.Plain) skitConds.Remove(item.Name);
            else skitConds[item.Name] = condition;
            if (item.AutoPlay) autoPlaySkits.Add(item.Name);
        }
    }

    private PerformanceItem[] SkitEntries() => stockSkits.Where(entry => !entry.Hidden).Select(entry => entry.Item)
        .Concat(library.Entries(PerformanceLibrary.Skit)).Where(item => skits.ContainsKey(item.Name))
        .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToArray();

    private (string Id, PerformanceItem Item)[] ShareableItems(string category)
    {
        if (category == PerformanceLibrary.Skit) return SkitEntries().Select(item => (item.Name, item)).ToArray();
        var stock = StockPerformances.Where(pair => pair.Key.Category == category)
            .Select(pair => (pair.Key.Reference, new PerformanceItem(pair.Value.Label, Reference: pair.Key.Reference)));
        if (category == PerformanceLibrary.Action)
            stock = stock.Concat(Behavior.Moods.Keys.Order().Select(name =>
                ("Idle:" + name, new PerformanceItem(name, Reference: "Idle:" + name,
                    Style: ActionStyleFor(name) is { } style && style != ActionStyle.Default ? style : null))));
        return [.. stock, .. library.Entries(category).Select(item => ("custom:" + item.Name, item))];
    }

    private object CatalogState()
    {
        string[] Names(string category) => library.Entries(category).Select(item => item.Name).ToArray();
        return new
        {
            model = library.Model,
            revision = library.Revision,
            expressions = Names(PerformanceLibrary.Expression),
            actions = Names(PerformanceLibrary.Action),
            skits = SkitEntries().Select(item => item.Name).ToArray(),
            removedSkits = library.RemovedSkits,
            choices = new[] { PerformanceLibrary.Expression, PerformanceLibrary.Action, PerformanceLibrary.Skit }.ToDictionary(category => category,
                category => ShareableItems(category).Select(entry => new
                {
                    id = entry.Id,
                    name = entry.Id.StartsWith("Idle:", StringComparison.Ordinal) ? entry.Item.Name + "（互动或待机）" : entry.Item.Name
                }).ToArray())
        };
    }

    private object LibraryCommand(JsonElement args)
    {
        try
        {
            // The tray uses this same endpoint, so manual and Agent imports share validation and storage.
            var operation = Tools.RequiredText(args, "operation", 20);
            if (operation == "list")
            {
                if (File.GetLastWriteTimeUtc(Path.Combine(Places.Root, "skits.txt")) != skitFileAt)
                {
                    LoadSkits();
                    behavior.SkitIdleMoods = [.. autoPlaySkits];
                }
                return Tools.Text(CatalogState());
            }

            if (operation == "parameters") return Tools.Text(new { model = library.Model, parameters = library.ParameterIds });

            var category = Tools.RequiredText(args, "category", 16);
            if (!PerformanceLibrary.CategoryValid(category)) throw new ArgumentException("请选择表情、动作或小剧场。");
            if (operation == "export")
            {
                var id = Tools.OptionalText(args, "id");
                var items = ShareableItems(category).Where(entry => id == null || entry.Id == id).Select(entry => entry.Item).ToArray();
                if (items.Length == 0) throw new ArgumentException("没有找到要导出的条目。");
                if (items.Length > PerformanceLibrary.MaxItems)
                    throw new ArgumentException("整栏超过 160 项，请选一个条目导出。");
                if (category == PerformanceLibrary.Skit)
                    items = items.Select(item => item with { Script = SkitScript.ForSharing(item.Script!, skits[item.Name]) }).ToArray();
                return Tools.Text(new { package = library.Export(category, items) });
            }

            if (operation == "import")
            {
                if (!args.TryGetProperty("package", out var package)) throw new ArgumentException("请提供表演包。");
                var added = library.Import(category, package, OccupiedNames(category));
                if (category == PerformanceLibrary.Skit) RefreshSkitLibrary();
                return Tools.Text(new { added, catalog = CatalogState() });
            }

            if (operation == "add_skit" && category == PerformanceLibrary.Skit)
            {
                var name = Tools.RequiredText(args, "name", 48);
                var script = Tools.RequiredText(args, "script", 6000);
                var added = library.Add(category, [new(name, Script: script,
                    AutoPlay: args.TryGetProperty("autoPlay", out var auto) && auto.ValueKind == JsonValueKind.True)], OccupiedNames(category));
                RefreshSkitLibrary();
                return Tools.Text(new { added, catalog = CatalogState() });
            }

            if ((operation is "remove_skit" or "restore_skit") && category == PerformanceLibrary.Skit)
            {
                var name = Tools.RequiredText(args, "name", 48);
                var names = operation == "remove_skit" ? SkitEntries().Select(item => item.Name) : library.RemovedSkits;
                if (!names.Contains(name, StringComparer.Ordinal))
                    throw new ArgumentException("请选择一个小剧场。");
                library.SetSkitRemoved(name, operation == "remove_skit");
                RefreshSkitLibrary();
                return Tools.Text(new { ok = true, catalog = CatalogState() });
            }

            if (operation == "play")
            {
                var items = ShareableItems(category);
                var id = Tools.RequiredText(args, "id", 140);
                var item = items.FirstOrDefault(entry => entry.Id == id).Item;
                if (item == null) throw new ArgumentException("没有找到要播放的条目。");
                if (!ready) throw new ArgumentException(trouble ?? "立绘正在加载，等她出来后再试播。");
                if (dragging || rightDragging || stroking || speech.IsSaying)
                    throw new ArgumentException("等当前互动结束后再播放。");
                Wake(); YieldMutter(); relaxAt = null;
                if (category == PerformanceLibrary.Skit) PlaySkit(item.Name, force: true);
                else PlayLibraryAction(category, item, 1, plainFace: false);
                return Tools.Text(new { playing = item.Name });
            }

            throw new ArgumentException("没有这个表演库操作。");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            return Tools.Text(new { error = error is JsonException ? "表演包 JSON 格式无效。" : error.Message });
        }
    }

    private IEnumerable<string> OccupiedNames(string category) => category == PerformanceLibrary.Skit
        ? stockSkits.Select(skit => skit.Item.Name).Concat(library.RemovedSkits)
        : StockPerformances.Values.Select(item => item.Label).Concat(LibraryCueNames);

    private void RefreshSkitLibrary()
    {
        RegisterSkits();
        behavior.SkitIdleMoods = [.. autoPlaySkits];
    }

    private void PlayLibraryAction(string category, PerformanceItem item, int side, bool plainFace)
    {
        if (item.Cue is { } cue)
        {
            behavior.Interrupt();
            PlaySilently("〔" + cue + "〕", filmed: false, plainFace: plainFace);
        }
        else if (item.Reference?.StartsWith("Idle:", StringComparison.Ordinal) == true)
        {
            string source = item.Reference[5..];
            Mood original = Behavior.Moods[source];
            behavior.Show(source, DateTime.UtcNow, side, force: true,
                preview: item.Style?.Apply(original) ?? original, plainFace: plainFace);
        }
        else if (item.Reference is { } reference && StockPerformances.TryGetValue((category, reference), out var stock))
        {
            if (stock.Kind is Kind.RawMotion && !plainFace)
                Send($"motion {stock.Arg}");
            else if (stock.Kind is Kind.Face or Kind.RawExp)
                behavior.Show(item.Name, DateTime.UtcNow, side, force: true,
                    preview: new Mood([stock.State], []), plainFace: plainFace);
            else
            {
                behavior.Interrupt();
                var words = stock.Kind == Kind.RawMotion ? "缩一下" : stock.Arg;
                PlaySilently("〔" + words + "〕", filmed: false, plainFace: plainFace);
            }
        }
        else
        {
            var motion = PerformanceLibrary.ToMood(item);
            if (plainFace)
            {
                // A shared action can also describe eye artwork as raw parameter IDs.
                motion = motion with
                {
                    Parameters = motion.Parameters.Where(parameter => !ArtworkParameter(parameter.Id)).ToArray(),
                    Motion = motion.Motion?.Where(track => !ArtworkParameter(track.Id)).ToArray()
                };
                if (motion.Motion is { Length: 0 }) motion = motion with { Motion = null };
            }
            behavior.Show(item.Name, DateTime.UtcNow, side, force: true, preview: motion, plainFace: plainFace);
        }
    }

    private static bool ArtworkParameter(string id) => id.StartsWith("post:", StringComparison.Ordinal)
        ? ArtworkParameter(id[5..]) : id is "key2" or "key3" or "key4" or "key5" or "key6" or "key7" or "key10" or "key13";
}
