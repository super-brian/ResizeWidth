using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ResizeWidth
{
    public partial class LayoutDialog : Window
    {
        private readonly List<MonitorItem> _monitors;
        private DispatcherTimer? _resizeDebounce;

        public LayoutDialog(List<MonitorItem> monitors)
        {
            InitializeComponent();
            _monitors = monitors;
            Loaded += (_, _) => DrawLayout();
            SizeChanged += (_, _) =>
            {
                _resizeDebounce?.Stop();
                _resizeDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); DrawLayout(); };
                _resizeDebounce.Start();
            };
            MouseDown += (_, _) => Close();
        }

        private void DrawLayout()
        {
            LayoutCanvas.Children.Clear();

            double canvasW = LayoutCanvas.ActualWidth;
            double canvasH = LayoutCanvas.ActualHeight;
            if (canvasW <= 0 || canvasH <= 0) return;

            // Gather positions of attached monitors
            var rects = new List<(MonitorItem m, int x, int y, int w, int h, string uid, string model)>();
            foreach (var m in _monitors)
            {
                if (!m.IsAttached) continue;

                var dm = new NativeMethods.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                if (!NativeMethods.EnumDisplaySettingsEx(m.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm, 0))
                    continue;
                if (dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0) continue;

                // Extract model and UID from device path
                string uid = "";
                string model = "";
                if (!string.IsNullOrWhiteSpace(m.DevicePath))
                {
                    var segs = m.DevicePath.Split('#');
                    if (segs.Length >= 3)
                    {
                        model = segs[1];
                        string instance = segs[2];
                        int idx = instance.IndexOf("UID", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0) uid = instance.Substring(idx);
                    }
                }

                rects.Add((m, dm.dmPositionX, dm.dmPositionY, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight, uid, model));
            }

            if (rects.Count == 0) return;

            // Find bounding box
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var (_, x, y, w, h, _, _) in rects)
            {
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x + w > maxX) maxX = x + w;
                if (y + h > maxY) maxY = y + h;
            }

            int totalW = maxX - minX;
            int totalH = maxY - minY;
            if (totalW <= 0 || totalH <= 0) return;

            // Scale to fit canvas with padding
            double padding = 20;
            double availW = canvasW - padding * 2;
            double availH = canvasH - padding * 2;
            double scale = Math.Min(availW / totalW, availH / totalH);

            // Center offset
            double offsetX = padding + (availW - totalW * scale) / 2;
            double offsetY = padding + (availH - totalH * scale) / 2;

            var colors = new[] {
                Color.FromRgb(0xDD, 0xDD, 0xDD),
                Color.FromRgb(0xCC, 0xDD, 0xEE),
                Color.FromRgb(0xDD, 0xEE, 0xCC),
                Color.FromRgb(0xEE, 0xDD, 0xCC),
                Color.FromRgb(0xDD, 0xCC, 0xEE),
            };

            for (int i = 0; i < rects.Count; i++)
            {
                var (m, rx, ry, rw, rh, uid, model) = rects[i];

                double sx = offsetX + (rx - minX) * scale;
                double sy = offsetY + (ry - minY) * scale;
                double sw = rw * scale;
                double sh = rh * scale;

                var rect = new Rectangle
                {
                    Width = sw,
                    Height = sh,
                    Fill = new SolidColorBrush(colors[i % colors.Length]),
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1.5,
                    RadiusX = 4,
                    RadiusY = 4,
                };
                Canvas.SetLeft(rect, sx);
                Canvas.SetTop(rect, sy);
                LayoutCanvas.Children.Add(rect);

                // UID label above model
                var uidLabel = new TextBlock
                {
                    Text = uid,
                    FontSize = Math.Max(10, Math.Min(sw, sh) / 10),
                    Foreground = Brushes.Gray,
                    TextAlignment = TextAlignment.Center,
                };
                uidLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                // Model label (main)
                var label = new TextBlock
                {
                    Text = model,
                    FontSize = Math.Max(12, Math.Min(sw, sh) / 6),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.Black,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lx = sx + (sw - label.DesiredSize.Width) / 2;
                double ly = sy + (sh - label.DesiredSize.Height) / 2;
                Canvas.SetLeft(label, lx);
                Canvas.SetTop(label, ly);
                LayoutCanvas.Children.Add(label);

                double ulx = sx + (sw - uidLabel.DesiredSize.Width) / 2;
                double uly = ly - uidLabel.DesiredSize.Height - 2;
                Canvas.SetLeft(uidLabel, ulx);
                Canvas.SetTop(uidLabel, uly);
                LayoutCanvas.Children.Add(uidLabel);

                // DISPLAY name below UID
                string displayName = m.DeviceName.Replace(@"\\.\", "");
                var subLabel = new TextBlock
                {
                    Text = displayName,
                    FontSize = Math.Max(10, Math.Min(sw, sh) / 10),
                    Foreground = Brushes.Gray,
                    TextAlignment = TextAlignment.Center,
                };
                subLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double slx = sx + (sw - subLabel.DesiredSize.Width) / 2;
                double sly = ly + label.DesiredSize.Height + 2;
                Canvas.SetLeft(subLabel, slx);
                Canvas.SetTop(subLabel, sly);
                LayoutCanvas.Children.Add(subLabel);
            }
        }
    }
}
