# Project Memory — NAFF Warehouse Application

## Overview
Windows desktop app built with **.NET MAUI (net10.0-windows)** for in-house warehouse stock tracking.
- App title: **Warehouse** / App ID: `com.nafstationery.app`
- Targets Windows 10+ (unpackaged, `WindowsPackageType=None`)

## Core Concept
Scan a barcode → webcam starts recording → scan same barcode again → recording stops → webhook fires to n8n.

## Architecture

### Entry Point
- [MauiProgram.cs](app/MauiProgram.cs): Registers `IRecordingService` (→ `RtspRecordingService`) and `WebhookService` as singletons.

### UI Structure
- [MainPage.xaml.cs](app/MainPage.xaml.cs): Manages a dynamic grid of **StationView** cards. Supports add/remove station, refresh devices, open logs, open settings. Grid auto-arranges in a square layout (1→1×1, 4→2×2, 9→3×3, etc.). Each row is 380px tall.
- [Controls/StationView.xaml](app/Controls/StationView.xaml) + [.cs](app/Controls/StationView.xaml.cs): The core card component — camera feed, barcode scanner, recording state machine.
- [Views/SettingsPage.xaml](app/Views/SettingsPage.xaml): Settings for video folder and webhook URL.

### StationView State Machine
1. **Idle**: Waiting for first scan.
2. **Recording**: First scan received → `CameraFeed.StartVideoRecording()` begins → shows REC badge.
3. **Stop + Webhook**: Same barcode scanned again → stops recording → saves `.mp4` to `AppSettings.VideoFolder/{date}/S{id}-{name}_{barcode}_{timestamp}.mp4` → POSTs webhook.
4. **Different barcode while recording**: Ignored (logged).

### Services
- [Services/RtspRecordingService.cs](app/Services/RtspRecordingService.cs): **RTSP recording via ffmpeg.exe** (in `Tools/`). Records to `.mkv` using `ffmpeg` with TCP transport. (Note: currently hardcoded RTSP URL — likely legacy/unused now that CameraView records directly.)
- [Services/WebhookService.cs](app/Services/WebhookService.cs): HTTP POST to n8n webhook with JSON payload: `{ barcode, filePath, fileName, finishedAt }`.
- [Services/AppSettings.cs](app/Services/AppSettings.cs): Persisted settings via `Preferences`. Keys: `settings.video_folder`, `settings.webhook_url`. Default webhook: `http://localhost:5678/webhook-test/...`.
- [Services/Logger.cs](app/Services/Logger.cs): App logging (writes to `FileSystem.AppDataDirectory`).

### Hardware Integration
- **Camera**: `CommunityToolkit.Maui.Camera` (`CameraView`) — live preview + video recording via `StartVideoRecording`/`StopVideoRecording` returning a `Stream`.
- **Barcode Scanner**: Serial COM port (USB barcode scanner via `System.IO.Ports.SerialPort`). Baud 9600, newline `\r`. Discovered via WMI (`Win32_PnPEntity WHERE PNPClass = 'Ports'`) with friendly names.
- **VLC**: `LibVLCSharp.MAUI` + `VideoLAN.LibVLC.Windows` referenced (possibly for RTSP preview). VLC binaries in `Platforms/Windows/VLC/`.

### Key Packages
- `CommunityToolkit.Maui` 13.0
- `CommunityToolkit.Maui.Camera` 5.0
- `CommunityToolkit.Maui.MediaElement` 7.0
- `LibVLCSharp.MAUI` 3.9.5 + `VideoLAN.LibVLC.Windows` 3.0.23
- `System.IO.Ports` 9.0, `System.Management` 9.0

## File Naming Convention
Videos saved as: `S{stationId}-{stationName}_{barcode}_{yyyyMMdd_HHmmss}.mp4`
Under: `AppSettings.VideoFolder / yyyy-MM-dd /`

## Notes
- Multiple stations can run simultaneously, each with its own camera and scanner.
- Camera/scanner pickers have a lock button (🔒/🔓) to prevent accidental changes.
- `RtspRecordingService` appears to be a legacy/alternative approach — active recording uses `CameraView` directly.
- Grid layout avoids `Children.Clear()` to preserve `CameraView` handler state.
