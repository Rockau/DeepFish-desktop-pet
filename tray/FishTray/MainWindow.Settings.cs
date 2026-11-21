using System.Windows;
using System.Windows.Threading;

namespace FishTray;

public partial class MainWindow
{
    private sealed record SettingSection(string Id, string Label, FrameworkElement? Content);

    private void WireSettingsMenu()
    {
        SettingSection[] sections =
        [
            new("home", "设置", null),
            new("relationship", "关系设置", RelationshipDetails),
            new("appearance", "外观与窗口", AppearanceDetails),
            new("actions", "表情、动作与小剧场", ActionsDetails),
            new("touch", "触摸区域与台词", TouchDetails),
            new("voice", "语音与对话", VoiceDetails),
            new("behavior", "待机与陪看", BehaviorDetails),
            new("reminders", "睡觉与午饭提醒", ReminderDetails),
            new("updates", "检查更新", UpdateDetails),
        ];
        SettingsPicker.ItemsSource = sections;
        SettingsPicker.SelectionChanged += (_, _) =>
        {
            var selected = SettingsPicker.SelectedItem as SettingSection;
            foreach (var section in sections)
                if (section.Content is { } content)
                    content.Visibility = section == selected ? Visibility.Visible : Visibility.Collapsed;
            SearchBorder.Visibility = selected?.Id == "actions" ? Visibility.Visible : Visibility.Collapsed;
            if (SearchBorder.Visibility == Visibility.Collapsed) SearchBox.Clear();
            Scroller.ScrollToTop();
        };
        SettingsPicker.SelectedValue = "home";
    }

    private void SelectSettings(string id, bool scrollToMenu = false)
    {
        SettingsPicker.SelectedValue = id;
        if (!scrollToMenu) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (SettingsPicker.IsVisible && Scroller.Content is UIElement content)
                Scroller.ScrollToVerticalOffset(System.Math.Max(0, SettingsPicker.TranslatePoint(new Point(), content).Y - 10));
        }, DispatcherPriority.Loaded);
    }
}
