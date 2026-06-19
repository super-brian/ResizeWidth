using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ResizeWidth
{
    public partial class SnapResultDialog : Window
    {
        private readonly DispatcherTimer _timer;
        private int _remaining = 15;
        private readonly List<(string deviceName, int x, int y, int w, int h, uint orientation)>? _originalPositions;
        private Action? _onReverted;

        public SnapResultDialog(string log,
            List<(string deviceName, int x, int y, int w, int h, uint orientation)>? originalPositions = null,
            Action? onReverted = null)
        {
            InitializeComponent();
            LogTextBox.Text = log;
            _originalPositions = originalPositions;
            _onReverted = onReverted;

            if (_originalPositions == null || _originalPositions.Count == 0)
                RevertButton.Visibility = Visibility.Collapsed;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            MouseDown += OnWindowClicked;
            LogTextBox.GotFocus += OnInteraction;
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _timer.Stop();
                Close();
            }
            else
            {
                CloseButton.Content = $"Close ({_remaining})";
            }
        }

        private void OnWindowClicked(object sender, MouseButtonEventArgs e)
        {
            CancelAutoClose();
        }

        private void OnInteraction(object sender, RoutedEventArgs e)
        {
            CancelAutoClose();
        }

        private void CancelAutoClose()
        {
            _timer.Stop();
            CloseButton.Content = "Close";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnRevert_Click(object sender, RoutedEventArgs e)
        {
            if (_originalPositions == null) return;

            foreach (var (deviceName, x, y, w, h, orientation) in _originalPositions)
            {
                var dm = new NativeMethods.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                dm.dmFields = NativeMethods.DM_POSITION | NativeMethods.DM_PELSWIDTH | NativeMethods.DM_PELSHEIGHT
                    | NativeMethods.DM_DISPLAYORIENTATION;
                dm.dmPositionX = x;
                dm.dmPositionY = y;
                dm.dmPelsWidth = (uint)w;
                dm.dmPelsHeight = (uint)h;
                dm.dmDisplayOrientation = orientation;

                NativeMethods.ChangeDisplaySettingsEx(
                    deviceName, ref dm, IntPtr.Zero,
                    NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET, IntPtr.Zero);
            }

            NativeMethods.ChangeDisplaySettingsEx(
                null!, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

            RevertButton.IsEnabled = false;
            RevertButton.Content = "Reverted";
            CancelAutoClose();
            _onReverted?.Invoke();
        }
    }
}
