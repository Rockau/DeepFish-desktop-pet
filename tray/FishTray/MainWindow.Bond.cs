using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FishTray;

// 首页的关系值、阶段和进度条。
public partial class MainWindow
{
    private static readonly string[] LevelNames = ["反感", "初识", "磨合", "同频", "默契", "羁绊"];
    private double bondRatio;
    private int shownLevel = -1;
    private bool bondWired;

    // 条纹一格的宽（和 xaml 里 BondStripes 的 Viewport 宽一致），条按整格走，不露半截条纹
    private const double StripePitch = 6;
    // 条里面离卡片边的距离：边框 1.4 + 内边距 7（和 xaml 里 BondTrack、BondInner 对得上），节点要和条的两头对齐
    private const double BondInset = 8.4;
    private readonly List<(Grid Marker, System.Windows.Shapes.Ellipse Halo, System.Windows.Shapes.Ellipse Dot, TextBlock Label)> bondNodes = [];

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private void WireBond()
    {
        if (bondWired) return;
        bondWired = true;
        // 外圈（当前级才亮）、圆点、名字
        for (int i = 0; i < LevelNames.Length; i++)
        {
            var halo = new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Stroke = Rgb(0x7C, 0xC4, 0xFF), StrokeThickness = 1.2, Opacity = 0, IsHitTestVisible = false };
            var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, StrokeThickness = 1.3, IsHitTestVisible = false };
            var label = new TextBlock { Text = LevelNames[i], FontSize = 10.5, IsHitTestVisible = false };
            // 外环和圆点装进同一个 15×15 的格子里各自居中，所以一定同心（以前各摆各的，会差半个像素）
            var marker = new Grid { Width = 15, Height = 15, IsHitTestVisible = false };
            marker.Children.Add(halo); marker.Children.Add(dot);
            BondNodes.Children.Add(marker); BondNodes.Children.Add(label);
            bondNodes.Add((marker, halo, dot, label));
        }
        BondNodes.UseLayoutRounding = true;
        BondNodes.SizeChanged += (_, _) => LayoutNodes();
        BondInner.SizeChanged += (_, _) => PlaceBond(false);
        StyleNodes(1);
        SetBondIcon(1, false);
    }

    private void ApplyBond(JsonElement s)
    {
        WireBond();
        if (s.TryGetProperty("affection", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            int I(string k) => a.TryGetProperty(k, out var v) && v.TryGetInt32(out var x) ? x : 0;
            int points = I("points"), level = Math.Clamp(I("level"), 0, LevelNames.Length - 1), from = I("from"), next = I("next");
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string? nextName = a.TryGetProperty("next_name", out var nn) && nn.ValueKind == JsonValueKind.String ? nn.GetString() : null;
            PointsText.Text = points.ToString();
            PointsText.Foreground = points < 0 ? Rgb(0xFF, 0x9F, 0xB0) : (Brush)FindResource("AccentBrush");
            AffectionSettings.ShowCurrentScore(points, name);
            LevelHint.Text = nextName == null ? $"{name} · 已达最高阶段" : $"{name} · 再 {Math.Max(0, next - points)} 点到「{nextName}」";
            double within = nextName == null ? 1 : next > from ? Math.Clamp((points - from) / (double)(next - from), 0, 1) : 0;
            double ratio = nextName == null ? 1 : (level + within) / (LevelNames.Length - 1);
            bool grew = shownLevel >= 0 && level > shownLevel;
            shownLevel = level;
            if (Math.Abs(ratio - bondRatio) > 0.0001 || grew) { bondRatio = ratio; PlaceBond(true); }
            StyleNodes(level);
            SetBondIcon(level, true);
        }
        if (!focusRequestPending && s.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.Object) ApplyFocus(f);
    }

    // 左边的图标：等级变了才重画，升级时淡入一下
    private int iconLevel = -1;
    private void SetBondIcon(int level, bool fade)
    {
        if (level == iconLevel) return;
        bool first = iconLevel < 0;
        iconLevel = level;
        BondIconHost.Child = BondIcon.Build(level);
        if (fade && !first) BondIconHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(500)));
    }

    // 羁绊条：宽度跟着整条（各个阶段）走了多少，变化时滑过去；按整格条纹取宽，有一点也至少亮一格
    private void PlaceBond(bool animate)
    {
        double inner = BondInner.ActualWidth;
        double width = inner * bondRatio;
        if (width > 0) width = Math.Min(inner, Math.Max(StripePitch, Math.Round(width / StripePitch) * StripePitch));
        if (!animate || inner <= 0) { BondFill.BeginAnimation(WidthProperty, null); BondFill.Width = width; return; }
        BondFill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // 节点摆在条的下面，各节点等距；名字在节点下面居中，头尾的不超出卡片
    private void LayoutNodes()
    {
        double w = BondNodes.ActualWidth;
        if (w <= 0) return;
        double span = w - 2 * BondInset;
        for (int i = 0; i < bondNodes.Count; i++)
        {
            double x = BondInset + span * i / (bondNodes.Count - 1);
            var (marker, _, _, label) = bondNodes[i];
            Canvas.SetLeft(marker, x - marker.Width / 2); Canvas.SetTop(marker, 9 - marker.Height / 2);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(x - label.DesiredSize.Width / 2, 0, Math.Max(0, w - label.DesiredSize.Width)));
            Canvas.SetTop(label, 19);
        }
    }

    // 到过的节点实心点亮；当前这一级再加一圈外环、名字最亮；下一级的轮廓亮一点；更远的都暗着
    private void StyleNodes(int level)
    {
        int current = Math.Clamp(level, 0, bondNodes.Count - 1);
        for (int i = 0; i < bondNodes.Count; i++)
        {
            var (_, halo, dot, label) = bondNodes[i];
            if (i < current) { dot.Fill = Rgb(0x7C, 0xC4, 0xFF); dot.Stroke = Rgb(0x7C, 0xC4, 0xFF); }
            else if (i == current) { dot.Fill = Rgb(0xD6, 0xEE, 0xFF); dot.Stroke = Rgb(0xD6, 0xEE, 0xFF); }
            else { dot.Fill = Rgb(0x0B, 0x16, 0x40); dot.Stroke = i == current + 1 ? Rgb(0x7C, 0xC4, 0xFF) : Rgb(0x3A, 0x4F, 0x8C); }
            halo.Opacity = i == current ? 0.9 : 0;
            label.Foreground = i == current ? (Brush)FindResource("TextBrush") : i == current + 1 ? (Brush)FindResource("SubBrush") : Rgb(0x5A, 0x6A, 0x9C);
            label.FontWeight = i == current ? FontWeights.SemiBold : FontWeights.Normal;
        }
        LayoutNodes();
    }

}
