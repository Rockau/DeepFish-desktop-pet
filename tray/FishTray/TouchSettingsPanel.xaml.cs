using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FishTray;

public partial class TouchSettingsPanel : UserControl
{
    private sealed record Shape(string Id, string Name);
    private sealed record Gesture(string Id, string Name);
    private sealed record Zone(string Id, Draft Saved, bool Custom, bool BuiltIn, string? EditAction = null)
    {
        public string Name => Saved.Name;
        internal Rect Area => Saved.Area;
        internal bool IsNew => Id.StartsWith("draft:", StringComparison.Ordinal);
    }
    private sealed record Draft(string Name, Rect Area, string Click, string Hover, string Gesture, string Release);
    private enum DragMode { Move, Resize, Draw }
    private sealed record Drag(Rect Start, Point Point, DragMode Mode);

    private const double BorderHitSlop = 5; // In displayed DIPs, after the Viewbox scales the preview.
    private const double ResizeHandleSize = 11;
    private static readonly Shape[] Shapes = [new("small", "小只"), new("chibi", "Q 版"), new("full", "全身")];
    private const string DefaultCue = "default";
    private static readonly Brush LeftSelection = new SolidColorBrush(Color.FromRgb(127, 209, 255));
    private static readonly Brush LeftOutline = new SolidColorBrush(Color.FromArgb(180, 110, 177, 235));
    private static readonly Brush RightSelection = new SolidColorBrush(Color.FromRgb(255, 192, 107));
    private static readonly Brush RightOutline = new SolidColorBrush(Color.FromArgb(190, 244, 159, 80));
    private static readonly Gesture[] Gestures =
    [
        new("", "无互动"), new("stroke", "抚摸"), new("pinch", "捏脸"), new("waist", "挠腰"),
        new("foot", "挠脚"), new("skirt", "拖裙摆"), new("action", "拖动后松手播放动作")
    ];
    private readonly Dictionary<(string Shape, string Button, string Zone), Draft> drafts = [];
    private readonly Dictionary<(string Shape, string Button), List<Zone>> newZones = [];
    private readonly Dictionary<(string Shape, string Button), string> selections = [];
    private List<Zone> leftZones = [], rightZones = [];
    private List<Zone> zones = [];
    private Zone? zone;
    private Drag? drag;
    private string shape = "";
    private string activeShape = "";
    private string button = "left";
    private bool busy;
    private bool changing;
    private bool closed;
    private bool IsRight => button == "right";
    private Brush SelectionStroke => IsRight ? RightSelection : LeftSelection;
    private Brush OutlineStroke => IsRight ? RightOutline : LeftOutline;

    public TouchSettingsPanel()
    {
        InitializeComponent();
        ShapePicker.ItemsSource = Shapes;
        GesturePicker.ItemsSource = Gestures;
        LeftRegions.Checked += (_, _) => SwitchButton("left");
        RightRegions.Checked += (_, _) => SwitchButton("right");
        UseDefaultClick.Checked += (_, _) => UpdateCueFields();
        UseDefaultClick.Unchecked += (_, _) => UpdateCueFields();
        UseDefaultHover.Checked += (_, _) => UpdateCueFields();
        UseDefaultHover.Unchecked += (_, _) => UpdateCueFields();
        UseDefaultRelease.Checked += (_, _) => UpdateCueFields();
        UseDefaultRelease.Unchecked += (_, _) => UpdateCueFields();
        GesturePicker.SelectionChanged += (_, _) => UpdateCueFields();
        Loaded += async (_, _) => await RefreshIfVisible();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) await RefreshIfVisible();
            else { EndDrag(); SuspendActionEditor(); }
        };
        Unloaded += (_, _) => { EndDrag(); SuspendActionEditor(); };
        RefreshButton.Click += async (_, _) => await LoadSettings(ShapePicker.SelectedValue as string);
        ShapePicker.SelectionChanged += async (_, _) =>
        {
            if (!changing && ShapePicker.SelectedValue is string id && id != shape) await LoadSettings(id);
        };
        PointerMode.SelectionChanged += (_, _) => UpdatePreviewHint();
        ZonePicker.SelectionChanged += (_, _) =>
        {
            if (changing) return;
            RememberEdits();
            Select(ZonePicker.SelectedItem as Zone);
        };
        foreach (var field in new[] { RectLeft, RectTop, RectWidth, RectHeight })
            field.TextChanged += (_, _) => { if (!changing) DrawAreas(); };

        SaveButton.Click += async (_, _) => await SaveSettings();
        ResetButton.Click += async (_, _) => await ResetSettings(all: false);
        RestoreDefaultsButton.Click += async (_, _) => await ResetSettings(all: true);
        AddButton.Click += (_, _) => AddRegion();
        DeleteButton.Click += async (_, _) => await DeleteSettings();
        PreviewClick.Click += async (_, _) => await PlayCue(ClickCue.Text);
        PreviewHover.Click += async (_, _) => await PlayCue(HoverCue.Text);
        PreviewStroke.Click += async (_, _) => await PlayCue(StrokeCue.Text);
        InitializeActionEditor();
        WireTouchLines();
        PreviewSurface.MouseLeftButtonDown += StartDrag;
        PreviewSurface.MouseMove += ContinueDrag;
        PreviewSurface.MouseLeftButtonUp += (_, _) => EndDrag();
        PreviewSurface.LostMouseCapture += (_, _) => drag = null;
    }

    internal void Dispose()
    {
        SuspendActionEditor();
        closed = true;
        EndDrag();
    }

    private async Task RefreshIfVisible()
    {
        if (!closed && IsLoaded && IsVisible) await LoadSettings(shape.Length == 0 ? null : shape);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        ShapePicker.IsEnabled = !value;
        RefreshButton.IsEnabled = !value;
        Editor.IsEnabled = !value && shape.Length != 0;
    }

    private async Task LoadSettings(string? id = null)
    {
        if (busy || closed) return;
        RememberEdits();
        SetBusy(true);
        Status.Text = "正在读取触摸区域…";
        var result = await Pet.Call("touch_settings", new { shape = id ?? "", button }, 8);
        if (closed) return;
        if (!ApplySettings(result))
        {
            Status.Text = Error(result, "桌宠连接中断，打开桌宠后点击刷新预览。");
            changing = true;
            ShapePicker.SelectedValue = shape;
            changing = false;
        }
        SetBusy(false);
    }

    private bool ApplySettings(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } data ||
            Read(data, "shape") is not { } profile ||
            !data.TryGetProperty("leftZones", out var left) || left.ValueKind != JsonValueKind.Array ||
            !data.TryGetProperty("rightZones", out var right) || right.ValueKind != JsonValueKind.Array) return false;

        string previous = shape;
        shape = profile;
        activeShape = Read(data, "activeShape") ?? "";
        leftZones = ParseZones(left);
        rightZones = ParseZones(right);
        changing = true;
        ShapePicker.SelectedValue = profile;
        string[] moods = ReadNames(data, "moods");
        ClickCue.ItemsSource = moods;
        HoverCue.ItemsSource = moods;
        StrokeCue.ItemsSource = moods;
        changing = false;

        if (Read(data, "preview") is { Length: > 0 } png) LoadPreview(png);
        else if (previous != profile || profile != activeShape) ModelPreview.Source = null;
        if (ModelPreview.Source is null)
        {
            PreviewFrame.Width = 300;
            PreviewFrame.Height = profile == "chibi" ? 340 : 480;
        }
        UpdatePreviewHint();
        RefreshRegionList(Read(data, "selected"));
        UpdateModeHint();
        Status.Text = zones.Count == 0 ? "当前没有区域，可新增或恢复默认区域。" : "选择区域后可单独编辑、删除或试播动作。";
        _ = RefreshActionEditor();
        return true;
    }

    private static List<Zone> ParseZones(JsonElement regions)
    {
        var options = new List<Zone>();
        foreach (var region in regions.EnumerateArray())
        {
            if (Read(region, "id") is not { } id || !region.TryGetProperty("rect", out var coordinates) ||
                !TryCoordinates(coordinates, out var area)) continue;
            var saved = new Draft(Read(region, "label") ?? id, area, Read(region, "click") ?? "", Read(region, "hover") ?? "",
                Read(region, "gesture") ?? "", Read(region, "release") ?? "");
            options.Add(new Zone(id, saved, Flag(region, "custom"), Flag(region, "builtIn"), Read(region, "editAction")));
        }
        return options;
    }

    private void SwitchButton(string next)
    {
        if (next == button) return;
        EndDrag();
        RememberEdits();
        button = next;
        PointerMode.SelectedIndex = 0;
        UpdateModeHint();
        RefreshRegionList();
        Status.Text = zones.Count == 0 ? "当前按键没有区域，可点击「新增区域」。" : "";
    }

    private void UpdateModeHint()
    {
        ModeDot.Fill = SelectionStroke;
        ButtonHint.Foreground = SelectionStroke;
        ButtonHint.Text = IsRight ? "橙色区域在桌面上按住右键拖动。" : "蓝色区域响应左键点击。左键拖动角色可移动桌宠。";
    }

    private void RefreshRegionList(string? selected = null)
    {
        var key = (shape, button);
        selected ??= selections.GetValueOrDefault(key);
        zones = [.. (IsRight ? rightZones : leftZones)];
        if (newZones.TryGetValue(key, out var pending)) zones.AddRange(pending);
        changing = true;
        ZonePicker.ItemsSource = zones;
        ZonePicker.SelectedItem = zones.Find(option => option.Id == selected) ?? zones.FirstOrDefault();
        changing = false;
        Select(ZonePicker.SelectedItem as Zone);
    }

    private void LoadPreview(string png)
    {
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(png));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            ModelPreview.Source = image;
            PreviewFrame.Height = 480;
            PreviewFrame.Width = 480 * (double)image.PixelWidth / image.PixelHeight;
        }
        catch (Exception) { ModelPreview.Source = null; }
    }

    private void Select(Zone? region)
    {
        zone = region;
        RefreshTouchLineEditor();
        SelectionEditor.Visibility = region is null ? Visibility.Collapsed : Visibility.Visible;
        ZonePicker.IsEnabled = zones.Count > 0;
        PointerMode.IsEnabled = zones.Count > 0;
        if (region is null)
        {
            ZoneHint.Text = "当前按键没有区域。";
            DrawAreas();
            UpdateRegionAction();
            return;
        }
        selections[(shape, button)] = region.Id;
        var draft = drafts.GetValueOrDefault((shape, button, region.Id)) ?? region.Saved;
        SetArea(draft.Area);
        RegionName.Text = draft.Name;
        UseDefaultClick.IsChecked = draft.Click == DefaultCue;
        UseDefaultHover.IsChecked = draft.Hover == DefaultCue;
        UseDefaultRelease.IsChecked = draft.Release == DefaultCue;
        ClickCue.Text = draft.Click == DefaultCue ? "" : draft.Click;
        HoverCue.Text = draft.Hover == DefaultCue ? "" : draft.Hover;
        StrokeCue.Text = draft.Release == DefaultCue ? "" : draft.Release;
        GesturePicker.SelectedValue = Gestures.Any(gesture => gesture.Id == draft.Gesture) ? draft.Gesture : "";
        LeftEditor.Visibility = IsRight ? Visibility.Collapsed : Visibility.Visible;
        StrokeEditor.Visibility = IsRight ? Visibility.Visible : Visibility.Collapsed;
        UseDefaultClick.Visibility = UseDefaultHover.Visibility = region.BuiltIn ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.Visibility = region.BuiltIn ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Content = region.IsNew ? "取消新增" : "删除区域";
        ZoneHint.Text = region.IsNew ? "新区域还未保存。"
            : region.Custom ? "当前使用自定义范围。" : "点预览上的位置可检查实际命中。";
        UpdateCueFields();
        DrawAreas();
        UpdateRegionAction();
    }

    private void UpdateCueFields()
    {
        ClickFields.IsEnabled = UseDefaultClick.IsChecked != true;
        HoverFields.IsEnabled = UseDefaultHover.IsChecked != true;
        string gesture = GesturePicker.SelectedValue as string ?? "";
        UseDefaultRelease.Visibility = gesture == "stroke" ? Visibility.Visible : Visibility.Collapsed;
        ReleaseFields.IsEnabled = gesture.Length > 0 && (gesture != "stroke" || UseDefaultRelease.IsChecked != true);
        GestureHint.Text = (IsRight, gesture) switch
        {
            (false, _) when zone?.Id == "foot" => "默认单击脚部可脱鞋或穿回。取消默认后，可另选动作或留空。",
            (true, "foot") => "脱鞋后，按住右键拖动脚部，拖动时就会露出怕痒的表情。",
            (true, "skirt") => "横拖让裙摆摆动。短裙往下拉会变长，长裙往上提会变短。",
            (true, "stroke") when zone?.Id == "face" => "脸部抚摸区也可按住 Shift 加右键捏脸。",
            (true, "") => "「无互动」时，此区域不会响应右键拖动。",
            (true, "action") => "右键拖动后松开，会播放所选动作。",
            _ => "",
        };
        GestureHint.Visibility = GestureHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RememberEdits()
    {
        if (zone is not { } region) return;
        var key = (shape, button, region.Id);
        if (!TryArea(out var area)) area = drafts.TryGetValue(key, out var previous) ? previous.Area : region.Area;
        var next = ReadDraft(area);
        if (SameArea(next.Area, region.Area) && (next with { Area = region.Area }) == region.Saved) drafts.Remove(key);
        else drafts[key] = next;
    }

    private Draft ReadDraft(Rect area)
    {
        var gesture = GesturePicker.SelectedValue as string ?? "";
        string release = gesture.Length == 0 ? "" : gesture == "stroke" && UseDefaultRelease.IsChecked == true
            ? DefaultCue : StrokeCue.Text.Trim();
        return new Draft(RegionName.Text.Trim(), area,
            IsRight ? "" : UseDefaultClick.IsChecked == true ? DefaultCue : ClickCue.Text.Trim(),
            IsRight ? "" : UseDefaultHover.IsChecked == true ? DefaultCue : HoverCue.Text.Trim(),
            IsRight ? gesture : "", IsRight ? release : "");
    }

    private void AddRegion()
    {
        if (busy) return;
        EndDrag();
        RememberEdits();
        var key = (shape, button);
        if (!newZones.TryGetValue(key, out var pending)) newZones[key] = pending = [];
        var region = new Zone("draft:" + Guid.NewGuid().ToString("N"),
            new Draft("新区域", new Rect(.36, .37, .28, .22), "", "", "", ""), true, false);
        pending.Add(region);
        RefreshRegionList(region.Id);
        PointerMode.SelectedIndex = 2;
        RegionName.Focus();
        RegionName.SelectAll();
        Status.Text = "在预览上拖出范围，再设置互动并保存。";
    }

    private async Task SaveSettings()
    {
        if (busy || zone is not { } region) return;
        if (!TryArea(out var area))
        {
            Status.Text = "请输入 0 到 100 的百分比。宽和高至少为 1，选框不能超出预览。";
            return;
        }
        var edit = ReadDraft(area);
        if (edit.Name.Length == 0)
        {
            Status.Text = "请给这个区域填写一个名称。";
            RegionName.Focus();
            return;
        }
        RememberEdits();
        SetBusy(true);
        var request = new Dictionary<string, object>
        {
            ["shape"] = shape, ["button"] = button, ["label"] = edit.Name
        };
        if (!region.IsNew) request["zone"] = region.Id;
        if (region.IsNew || !SameArea(area, region.Area))
            request["rect"] = new { x = area.X, y = area.Y, width = area.Width, height = area.Height };
        if (IsRight)
        {
            request["gesture"] = edit.Gesture;
            request["release"] = edit.Release;
        }
        else
        {
            request["click"] = edit.Click;
            request["hover"] = edit.Hover;
        }
        var response = await Pet.Call("touch_save", request, 8);
        if (FinishEdit(response, region, "已保存该区域。", "没有保存成功，请检查连接或动作名称。"))
            PointerMode.SelectedIndex = 0;
    }

    private async Task DeleteSettings()
    {
        if (busy || zone is not { } region) return;
        EndDrag();
        if (region.IsNew)
        {
            ForgetRegion(region);
            RefreshRegionList();
            PointerMode.SelectedIndex = 0;
            Status.Text = "已取消新增区域。";
            return;
        }
        RememberEdits();
        SetBusy(true);
        var response = await Pet.Call("touch_delete", new { shape, button, zone = region.Id }, 8);
        FinishEdit(response, region, "已删除该区域。", "没有删除成功，请检查连接后重试。");
    }

    private async Task ResetSettings(bool all)
    {
        if (busy || !all && zone?.BuiltIn != true) return;
        if (all && zones.Count > 0 && MessageBox.Show(
            "当前体型下，这个按键的自定义区域和修改将被清除。要恢复默认区域吗？",
            "恢复默认区域", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        RememberEdits();
        Zone? region = all ? null : zone;
        SetBusy(true);
        var response = await Pet.Call("touch_reset", region is null ? new { shape, button } : new { shape, button, zone = region.Id }, 8);
        FinishEdit(response, region, all ? "已恢复这个按键的默认区域。" : "已恢复该区域的默认设置。",
            "没有恢复成功，请检查连接后重试。");
    }

    private void ForgetRegion(Zone region)
    {
        drafts.Remove((shape, button, region.Id));
        if (region.IsNew && newZones.TryGetValue((shape, button), out var pending))
            pending.Remove(region);
    }

    private bool FinishEdit(JsonElement? response, Zone? region, string success, string failure)
    {
        if (closed) return false;
        bool valid = response is { ValueKind: JsonValueKind.Object } packet && Read(packet, "shape") == shape &&
            packet.TryGetProperty("leftZones", out var left) && left.ValueKind == JsonValueKind.Array &&
            packet.TryGetProperty("rightZones", out var right) && right.ValueKind == JsonValueKind.Array;
        if (valid)
        {
            if (region is not null) ForgetRegion(region);
            else
            {
                foreach (var key in drafts.Keys.Where(key => key.Shape == shape && key.Button == button).ToArray()) drafts.Remove(key);
                newZones.Remove((shape, button));
            }
        }
        bool applied = valid && ApplySettings(response);
        Status.Text = applied ? success : Error(response, failure);
        SetBusy(false);
        return applied;
    }

    private async Task PlayCue(string cue)
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(cue)) { Status.Text = "先选择或输入要试播的动作。"; return; }
        SetBusy(true);
        var result = await Pet.Call("touch_preview", new { cue = cue.Trim() }, 8);
        if (closed) return;
        Status.Text = Error(result, result is null ? "动作试播失败，请检查桌宠是否已出场。" : "已在桌面试播该动作。");
        SetBusy(false);
    }

    private bool TryArea(out Rect area)
    {
        area = Rect.Empty;
        return TryPercent(RectLeft.Text, out var left) && TryPercent(RectTop.Text, out var top) &&
            TryPercent(RectWidth.Text, out var width) && TryPercent(RectHeight.Text, out var height) &&
            ValidRect(left, top, width, height, out area);
    }

    private static bool TryPercent(string text, out double value)
    {
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        value /= 100;
        return parsed;
    }

    private static bool ValidRect(double x, double y, double width, double height, out Rect result)
    {
        result = Rect.Empty;
        if (!(x >= 0 && y >= 0 && width >= .01 && height >= .01 && x + width <= 1.000001 && y + height <= 1.000001)) return false;
        x = Math.Min(x, .99);
        y = Math.Min(y, .99);
        result = new Rect(x, y, Math.Min(width, 1 - x), Math.Min(height, 1 - y));
        return true;
    }

    private static bool TryCoordinates(JsonElement data, out Rect result) => ValidRect(Number(data, "x"), Number(data, "y"),
        Number(data, "width"), Number(data, "height"), out result);

    private static bool SameArea(Rect a, Rect b) => Math.Abs(a.X - b.X) < .000001 && Math.Abs(a.Y - b.Y) < .000001 &&
        Math.Abs(a.Width - b.Width) < .000001 && Math.Abs(a.Height - b.Height) < .000001;

    private static double Number(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result)
        ? result : double.NaN;

    private static string? Read(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] ReadNames(JsonElement data, string property) =>
        data.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];

    private static bool Flag(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Error(JsonElement? data, string fallback) => data is { } value ? Read(value, "error") ?? fallback : fallback;

    private void SetArea(Rect area)
    {
        static string Percent(double value) => (value * 100).ToString("0.####", CultureInfo.CurrentCulture);
        changing = true;
        RectLeft.Text = Percent(area.X);
        RectTop.Text = Percent(area.Y);
        RectWidth.Text = Percent(area.Width);
        RectHeight.Text = Percent(area.Height);
        changing = false;
    }

    private void DrawAreas()
    {
        PreviewSurface.Children.Clear();
        foreach (var item in zones.Where(item => item != zone)) DrawArea(PreviewBounds(AreaFor(item)), false);
        if (zone is { } selected) DrawArea(PreviewBounds(DisplayedArea(selected)), true);
    }

    private Rect AreaFor(Zone item) => drafts.TryGetValue((shape, button, item.Id), out var draft) ? draft.Area : item.Area;

    private Rect DisplayedArea(Zone item) => item == zone && TryArea(out var area) ? area : AreaFor(item);

    private Rect PreviewBounds(Rect area) => new(area.X * PreviewFrame.Width, area.Y * PreviewFrame.Height,
        area.Width * PreviewFrame.Width, area.Height * PreviewFrame.Height);

    private static Rect ResizeHandle(Rect bounds)
    {
        double width = Math.Min(ResizeHandleSize, bounds.Width), height = Math.Min(ResizeHandleSize, bounds.Height);
        return new Rect(bounds.Right - width, bounds.Bottom - height, width, height);
    }

    private void DrawArea(Rect bounds, bool selected)
    {
        var outline = new Rectangle
        {
            Width = bounds.Width, Height = bounds.Height, Stroke = selected ? SelectionStroke : OutlineStroke,
            StrokeThickness = selected ? 2.6 : 1.4, StrokeDashArray = selected ? null : new DoubleCollection { 3, 2 },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(outline, bounds.X);
        Canvas.SetTop(outline, bounds.Y);
        PreviewSurface.Children.Add(outline);
        if (!selected) return;
        var corner = ResizeHandle(bounds);
        var handle = new Rectangle { Width = corner.Width, Height = corner.Height, RadiusX = 2, RadiusY = 2,
            Fill = SelectionStroke, IsHitTestVisible = false };
        Canvas.SetLeft(handle, corner.X);
        Canvas.SetTop(handle, corner.Y);
        PreviewSurface.Children.Add(handle);
    }

    private Point Fraction(Point point) => new(point.X / PreviewFrame.Width, point.Y / PreviewFrame.Height);

    private void UpdatePreviewHint()
    {
        var instruction = PointerMode.SelectedIndex switch
        {
            1 => "点预览上的位置，查看命中的部位。",
            2 => "按住左键拖出一个新的区域。",
            _ => "点边框选中。拖边框移动，拖右下角调整大小。"
        };
        PreviewHint.Text = ModelPreview.Source is null ? "暂无此体型的截图。" + instruction : instruction;
    }

    private Zone? VisualHit(Point point) => zones.Where(item => AreaFor(item).Contains(point))
        .OrderBy(item => AreaFor(item).Width * AreaFor(item).Height).FirstOrDefault();

    private Zone? FindBorder(Point point, double tolerance) => zones
        .Select(item => (Item: item, Bounds: PreviewBounds(DisplayedArea(item))))
        .Select(box => (box.Item, Area: box.Bounds.Width * box.Bounds.Height, Distance: BorderDistance(point, box.Bounds)))
        .Where(hit => hit.Distance <= tolerance)
        .OrderBy(hit => hit.Distance)
        .ThenBy(hit => hit.Area)
        .Select(hit => hit.Item)
        .FirstOrDefault();

    private static double BorderDistance(Point point, Rect bounds)
    {
        double dx = Math.Min(Math.Abs(point.X - bounds.Left), Math.Abs(point.X - bounds.Right));
        double dy = Math.Min(Math.Abs(point.Y - bounds.Top), Math.Abs(point.Y - bounds.Bottom));
        if (point.X < bounds.Left || point.X > bounds.Right)
            return point.Y < bounds.Top || point.Y > bounds.Bottom ? Math.Sqrt(dx * dx + dy * dy) : dx;
        return point.Y < bounds.Top || point.Y > bounds.Bottom ? dy : Math.Min(dx, dy);
    }

    private async Task FindZone(Point point)
    {
        if (shape != activeShape)
        {
            if (VisualHit(point) is { } nearest) { ZonePicker.SelectedItem = nearest; Status.Text = $"参考位置对应「{nearest.Name}」。"; }
            else Status.Text = "这个位置没有标出的区域。";
            return;
        }
        SetBusy(true);
        var result = await Pet.Call("touch_settings", new { shape, button, point = new { x = point.X, y = point.Y } }, 8);
        if (closed) return;
        string? hit = result is { } data ? Read(data, "hit") : null;
        if (zones.Find(item => item.Id == hit) is { } matched)
        {
            ZonePicker.SelectedItem = matched;
            Status.Text = $"点击位置命中「{matched.Name}」。";
        }
        else Status.Text = result is null ? "无法读取这个位置，请刷新后重试。" : "这个位置没有命中区域。";
        SetBusy(false);
    }

    private async void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (busy || zone is null) return;
        Point onPreview = e.GetPosition(PreviewSurface);
        Point point = Fraction(onPreview);
        if (PointerMode.SelectedIndex == 1)
        {
            e.Handled = true;
            await FindZone(point);
            return;
        }
        var area = DisplayedArea(zone);
        if (PointerMode.SelectedIndex == 2)
        {
            if (!PreviewSurface.CaptureMouse()) return;
            drag = new Drag(area, point, DragMode.Draw);
            e.Handled = true;
            return;
        }
        bool resize = ResizeHandle(PreviewBounds(area)).Contains(onPreview);
        if (!resize)
        {
            var transform = PreviewSurface.TransformToAncestor(this);
            double scale = (transform.Transform(new Point(1, 0)) - transform.Transform(new Point())).Length;
            if (scale <= 0 || FindBorder(onPreview, BorderHitSlop / scale) is not { } hit) return;
            if (hit != zone) ZonePicker.SelectedItem = hit;
            area = DisplayedArea(hit);
        }
        if (!PreviewSurface.CaptureMouse()) return;
        drag = new Drag(area, point, resize ? DragMode.Resize : DragMode.Move);
        e.Handled = true;
    }

    private void ContinueDrag(object sender, MouseEventArgs e)
    {
        if (drag is not { } moving) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        Point point = Fraction(e.GetPosition(PreviewSurface));
        if (moving.Mode == DragMode.Draw)
        {
            point = new Point(Math.Clamp(point.X, 0, 1), Math.Clamp(point.Y, 0, 1));
            var from = moving.Point;
            if (ValidRect(Math.Min(from.X, point.X), Math.Min(from.Y, point.Y), Math.Abs(from.X - point.X),
                Math.Abs(from.Y - point.Y), out var drawn)) { SetArea(drawn); DrawAreas(); }
            return;
        }
        Vector change = point - moving.Point;
        var start = moving.Start;
        var area = moving.Mode == DragMode.Resize
            ? new Rect(start.X, start.Y, Math.Clamp(start.Width + change.X, .01, Math.Max(.01, 1 - start.X)),
                Math.Clamp(start.Height + change.Y, .01, Math.Max(.01, 1 - start.Y)))
            : new Rect(Math.Clamp(start.X + change.X, 0, 1 - start.Width), Math.Clamp(start.Y + change.Y, 0, 1 - start.Height), start.Width, start.Height);
        SetArea(area);
        DrawAreas();
    }

    private void EndDrag()
    {
        if (drag is null) return;
        drag = null;
        PreviewSurface.ReleaseMouseCapture();
        RememberEdits();
    }
}
