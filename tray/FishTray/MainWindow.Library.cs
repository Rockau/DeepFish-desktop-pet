using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace FishTray;

public partial class MainWindow
{
    private sealed record LibraryChoice(string? Id, string Name);
    private const string SkitCategory = "skit";
    private const int PackageSizeLimit = 128 * 1024;
    private static readonly JsonSerializerOptions CompactPackageFormat = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions PackageFileFormat = new(CompactPackageFormat) { WriteIndented = true };

    private JsonElement? libraryCatalog;
    private bool refreshingLibrary;
    private int catalogGeneration;

    private static string LibraryCategory(Section section) => section.Key switch
    {
        "expr" => "expression",
        "move" => "action",
        _ => SkitCategory
    };

    private static string? LibraryString(JsonElement? value, string name) =>
        value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private string[] LibraryNames(string field) =>
        libraryCatalog is { } catalog && catalog.TryGetProperty(field, out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(name => name.GetString()!).ToArray() : [];

    private Section WithImportedItems(Section section)
    {
        if (section.Key == SkitCategory) return section;
        var names = LibraryNames(section.Key == "expr" ? "expressions" : "actions");
        if (names.Length == 0) return section;
        var group = new Group("已导入", names.Select(name => new Item(name, Kind.Library, "custom:" + name, State: LibraryCategory(section))).ToArray());
        return section with { Groups = [.. section.Groups, group] };
    }

    private void ApplyLibraryCatalog(JsonElement catalog)
    {
        catalogGeneration++;
        bool changed = libraryCatalog?.GetRawText() != catalog.GetRawText();
        libraryCatalog = catalog;
        if (changed) BuildSections();
    }

    private async Task RefreshLibrary(string? revision, bool force)
    {
        if (refreshingLibrary || revision == null || (!force && LibraryString(libraryCatalog, "revision") == revision)) return;
        refreshingLibrary = true;
        int generation = catalogGeneration;
        try
        {
            var result = await Pet.Call("performance_library", new { operation = "list" });
            if (generation == catalogGeneration && LibraryString(result, "revision") != null) ApplyLibraryCatalog(result!.Value);
        }
        finally { refreshingLibrary = false; }
    }

    private WrapPanel LibraryToolbar(Section section)
    {
        string category = LibraryCategory(section);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Add(string label, Func<Task> action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4), Style = (Style)FindResource("ChipButton") };
            AutomationProperties.SetName(button, label + section.Title);
            button.Click += async (_, _) => await action();
            toolbar.Children.Add(button);
        }

        Add("导入", () => ImportLibrary(category, section.Title));
        Add("导出", () => ExportLibrary(category, section.Title));
        if (category == SkitCategory)
        {
            Add("新增", NewLibrarySkit);
            Add("移除", () => ToggleLibrarySkit(restore: false));
            if (LibraryNames("removedSkits").Length > 0)
                Add("恢复", () => ToggleLibrarySkit(restore: true));
        }
        return toolbar;
    }

    private static string LibraryError(JsonElement? result) =>
        LibraryString(result, "error") ?? "连不上桌宠的表演库，请确认桌宠正在运行。";

    private bool CompleteLibraryChange(JsonElement? result, string notice)
    {
        if (result is not { ValueKind: JsonValueKind.Object } response || !response.TryGetProperty("catalog", out var catalog)) return false;
        ApplyLibraryCatalog(catalog);
        Notice(notice);
        return true;
    }

    private async Task ImportLibrary(string category, string title)
    {
        var dialog = new OpenFileDialog { Title = "导入" + title, Filter = "表演包 (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await using var file = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > PackageSizeLimit) { Notice("请选择不超过 128 KB 的表演包。"); return; }
            using var document = await JsonDocument.ParseAsync(file);
            var result = await Pet.Call("performance_library", new { operation = "import", category, package = document.RootElement }, 8);
            int count = result is { } response && response.TryGetProperty("added", out var added) ? added.GetArrayLength() : 0;
            if (!CompleteLibraryChange(result, $"已导入 {count} 项{title}")) Notice(LibraryError(result));
        }
        catch (JsonException) { Notice("无法读取表演包，请选择有效的 JSON 文件。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Notice("无法打开所选文件。"); }
    }

    private async Task<LibraryChoice[]?> ReadLibraryChoices(string category, bool removed = false)
    {
        int generation = catalogGeneration;
        var result = await Pet.Call("performance_library", new { operation = "list" });
        if (LibraryString(result, "revision") == null) { Notice(LibraryError(result)); return null; }
        if (generation == catalogGeneration) ApplyLibraryCatalog(result!.Value);
        if (removed) return LibraryNames("removedSkits").Select(name => new LibraryChoice(name, name)).ToArray();
        return libraryCatalog!.Value.GetProperty("choices").GetProperty(category).EnumerateArray()
            .Select(choice => new LibraryChoice(LibraryString(choice, "id"), LibraryString(choice, "name")!)).ToArray();
    }

    private async Task ExportLibrary(string category, string title)
    {
        var choices = await ReadLibraryChoices(category);
        if (choices == null) return;
        if (choices.Length == 0) { Notice("这里还没有可导出的条目。"); return; }
        var selected = ChooseLibraryItem("导出" + title, "导出", [new LibraryChoice(null, "全部" + title), .. choices]);
        if (selected == null) return;
        var result = await Pet.Call("performance_library", new { operation = "export", category, id = selected.Id }, 8);
        if (result is not { } response || !response.TryGetProperty("package", out var package)) { Notice(LibraryError(result)); return; }

        var fileName = new string(selected.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
        var dialog = new SaveFileDialog { Title = "导出" + title, FileName = title + "-" + fileName, Filter = "表演包 (*.json)|*.json", DefaultExt = ".json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var contents = JsonSerializer.Serialize(package, PackageFileFormat);
            if (Encoding.UTF8.GetByteCount(contents) > PackageSizeLimit) contents = JsonSerializer.Serialize(package, CompactPackageFormat);
            await File.WriteAllTextAsync(dialog.FileName, contents);
            Notice("已导出" + title);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Notice("无法写入所选位置。"); }
    }

    private async Task ToggleLibrarySkit(bool restore)
    {
        var choices = await ReadLibraryChoices(SkitCategory, restore);
        if (choices == null) return;
        if (choices.Length == 0) { Notice(restore ? "没有可恢复的小剧场。" : "没有可移除的小剧场。"); return; }
        string verb = restore ? "恢复" : "移除";
        var selected = ChooseLibraryItem(verb + "小剧场", verb, choices, restore ? null : "移除后从列表和待机演出中隐藏，可随时恢复。");
        if (selected == null) return;
        var result = await Pet.Call("performance_library", new { operation = restore ? "restore_skit" : "remove_skit", category = SkitCategory, name = selected.Id });
        if (!CompleteLibraryChange(result, $"已{verb}「{selected.Name}」")) Notice(LibraryError(result));
    }

    private LibraryChoice? ChooseLibraryItem(string title, string verb, LibraryChoice[] choices, string? hint = null)
    {
        var (dialog, content) = LibraryDialog(title);
        if (hint != null) content.Children.Add(LibraryCaption(hint));
        var picker = new ComboBox
        {
            Style = (Style)FindResource("Picker"), ItemsSource = choices, DisplayMemberPath = nameof(LibraryChoice.Name),
            SelectedIndex = choices.Length == 1 || choices[0].Id == null ? 0 : -1,
            Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetName(picker, title);
        content.Children.Add(picker);
        var button = LibraryDialogButtons(content, verb);
        button.IsEnabled = picker.SelectedIndex >= 0;
        picker.SelectionChanged += (_, _) => button.IsEnabled = picker.SelectedIndex >= 0;
        button.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? picker.SelectedItem as LibraryChoice : null;
    }

    private Task NewLibrarySkit()
    {
        var (dialog, content) = LibraryDialog("新增小剧场");
        TextBox Input(string label, int limit)
        {
            content.Children.Add(LibraryCaption(label));
            var field = new TextBox { Style = (Style)FindResource("Input"), MaxLength = limit, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(field, label);
            content.Children.Add(field);
            return field;
        }
        var name = Input("名称", 48);
        var script = Input("剧本", 6000);
        script.Height = 140;
        script.AcceptsReturn = true;
        script.TextWrapping = TextWrapping.Wrap;
        script.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        content.Children.Add(LibraryCaption("每行可写一句台词，或用 台词|动作|秒数，例如「准备好了！|点头|3」。"));
        var autoPlay = new CheckBox
        {
            Content = "加入待机自动演出", Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 2, 0, 12)
        };
        content.Children.Add(autoPlay);
        var error = LibraryCaption("");
        error.Foreground = Brushes.LightSalmon;
        error.Visibility = Visibility.Collapsed;
        content.Children.Add(error);
        var save = LibraryDialogButtons(content, "新增");
        save.IsEnabled = false;

        bool saving = false;
        void Update() => save.IsEnabled = !saving && !string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(script.Text);
        name.TextChanged += (_, _) => Update();
        script.TextChanged += (_, _) => Update();
        dialog.Closing += (_, args) => args.Cancel = saving;
        save.Click += async (_, _) =>
        {
            if (saving) return;
            saving = true;
            Update();
            error.Visibility = Visibility.Collapsed;
            var result = await Pet.Call("performance_library", new
            {
                operation = "add_skit", category = SkitCategory, name = name.Text.Trim(),
                script = script.Text.Trim(), autoPlay = autoPlay.IsChecked == true
            });
            saving = false;
            if (CompleteLibraryChange(result, "已新增小剧场")) { dialog.DialogResult = true; return; }
            error.Text = LibraryError(result);
            error.Visibility = Visibility.Visible;
            Update();
        };
        dialog.Loaded += (_, _) => name.Focus();
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    private (Window Dialog, StackPanel Content) LibraryDialog(string title)
    {
        var content = new StackPanel { Margin = new Thickness(18) };
        var dialog = new Window
        {
            Title = title, Owner = this, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
            Width = 390, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(13, 22, 53)), Foreground = (Brush)FindResource("TextBrush"),
            FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 14, Content = content
        };
        return (dialog, content);
    }

    private TextBlock LibraryCaption(string label) => new()
    {
        Text = label, Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
    };

    private Button LibraryDialogButtons(StackPanel content, string verb)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var accept = new Button { Content = verb, IsDefault = true, Style = (Style)FindResource("PillAccent") };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0) });
        buttons.Children.Add(accept);
        content.Children.Add(buttons);
        return accept;
    }
}
