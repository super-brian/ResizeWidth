using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace ResizeWidth
{
    /// <summary>Represents one entry in the Alt-Tab-style window list.</summary>
    public class WindowItem : INotifyPropertyChanged
    {
        public IntPtr Hwnd        { get; init; }
        public string Title       { get; init; } = string.Empty;
        public string ProcessName { get; init; } = string.Empty;

        /// <summary>Window icon (may be null if retrieval fails).</summary>
        public BitmapSource? Icon { get; init; }

        private bool _isIgnored;
        /// <summary>When true, the global hotkey skips this window.</summary>
        public bool IsIgnored
        {
            get => _isIgnored;
            set { if (_isIgnored != value) { _isIgnored = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public override string ToString() =>
            string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
    }
}
