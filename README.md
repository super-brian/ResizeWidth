# ResizeWidth <img src="ResizeWidth/app.ico" alt="ResizeWidth icon" width="32" align="center">

A Windows utility for resizing windows beyond their native monitor width and managing multi-monitor arrangements.

## Features

### Window Resizing

- **Extend window width** to 150%, 133%, or 125% of the monitor, stretching onto an adjacent display
- Cycle through 150% → 133% → 125% with repeated presses
- Extend to the right or to the left
- DPI-aware positioning for pixel-perfect results across mixed-DPI setups

### Window Filtering

- **Ignore specific windows** from hotkey-triggered resizing via toggle buttons in the window list
- Rules persisted to `ignored_processes.txt` with two match types:
  - `exact: ProcessName` — exact process name match (case-insensitive)
  - `contains: substring` — substring match in window title (case-insensitive)

### Monitor Management

- **Detach** monitors from the desktop without physically disconnecting them
- **Attach** previously detached monitors back to the desktop
- **Save** multi-monitor arrangements (positions and resolutions)
- **Restore** saved arrangements with a single click or hotkey
- **Visualize** monitor layout in a graphical dialog
- Tracks detached and unreported monitors across sessions

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+Alt+Shift+Right | Extend foreground window to the right (cycle 150%/133%/125%) |
| Ctrl+Alt+Shift+Left | Extend foreground window to the left (cycle 150%/133%/125%) |
| Ctrl+Alt+Shift+Up | Restore saved monitor arrangement |
| Win+Right | Snap foreground window to right half/2&#8260;3/1&#8260;3 of monitor (cycle) |
| Win+Left | Snap foreground window to left half/2&#8260;3/1&#8260;3 of monitor (cycle) |
| Win+Up | Snap foreground window to top half, press again for full area |
| Win+Down | Snap foreground window to bottom half, press again for full area |

Hotkeys are system-wide and work even when the app is minimized.

## Requirements

- Windows 10 or later
- .NET 10.0
- Administrator privileges (required for changing display settings)

## Building

Open the solution in Visual Studio and build, or from the command line:

```
dotnet build ResizeWidth.slnx
```

## How It Works

- **Window list** shows all Alt+Tab-eligible windows in foreground order, with icons and process names
- **Monitor list** shows all connected (and previously known) monitors with resolution and attachment status
- Window resizing uses `SetWindowPos` with DPI context matching to position windows accurately
- Monitor attach uses the modern CCD (Connecting and Configuring Displays) API with a fallback to `ChangeDisplaySettingsEx`
- Window icons are retrieved without cross-process messages to prevent desktop freezes
- Arrangements, pre-detach resolutions, and ignore rules are persisted to local text files alongside the executable
