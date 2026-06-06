namespace ResizeWidth
{
    /// <summary>Represents one display adapter output (attached or detached).</summary>
    public class MonitorItem
    {
        /// <summary>Device name used by ChangeDisplaySettingsEx (e.g. \\.\DISPLAY1).</summary>
        public string DeviceName { get; init; } = string.Empty;

        /// <summary>Friendly description from EDID or driver (e.g. "DELL U2723QE").</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Serial number from the driver string, if available.</summary>
        public string DevicePath { get; init; } = string.Empty;

        /// <summary>Current or last-known resolution (e.g. "3072×1920").</summary>
        public string Resolution { get; init; } = string.Empty;

        /// <summary>True when the display is currently part of the desktop.</summary>
        public bool IsAttached { get; init; }

        /// <summary>True when the monitor was not reported by Windows but exists in the saved file.</summary>
        public bool IsUnreported { get; init; }

        /// <summary>Display string for the list box.</summary>
        public string DisplayText =>
            string.IsNullOrWhiteSpace(Description)
                ? $"{DeviceName}  {Resolution}"
                : $"{Description}  {Resolution}";

        /// <summary>Second-line detail text.</summary>
        public string DetailText
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();

                // Extract model and UID from device path
                // e.g. "\\?\DISPLAY#HSJ1600#5&15c019df&0&UID281#{...}"
                if (!string.IsNullOrWhiteSpace(DevicePath))
                {
                    var segs = DevicePath.Split('#');
                    if (segs.Length >= 3)
                    {
                        parts.Add(segs[1]); // model e.g. "HSJ1600"
                        // Extract UIDxxx from instance string like "5&15c019df&0&UID281"
                        string instance = segs[2];
                        int uidIdx = instance.IndexOf("UID", System.StringComparison.OrdinalIgnoreCase);
                        if (uidIdx >= 0)
                            parts.Add(instance.Substring(uidIdx)); // e.g. "UID281"
                    }
                }

                string displayName = DeviceName.Replace(@"\\.\", ""); // e.g. "DISPLAY1"
                parts.Add(displayName);
                parts.Add(IsAttached ? "Attached" : "Detached");
                return string.Join("  ·  ", parts);
            }
        }
    }
}
