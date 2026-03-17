# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LWhisper — desktop Windows app for offline voice-to-text input using Whisper AI. Captures microphone audio, recognizes speech via Whisper.net, and injects recognized text into the active window via WinAPI SendInput. Primary UI language is Russian.

## Build & Run Commands

```bash
# Build all projects
dotnet build

# Run the application
dotnet run --project LWhisper.UI.WPF

# Create self-contained release executable (win-x64, single file ~50-100MB)
build-release.bat Release

# Create debug build
build-release.bat Debug
```

No test framework is currently configured — there are no unit tests.

## Architecture

Three-layer Clean Architecture solution (`LWhisperer.sln`):

- **LWhisper.Core** (netstandard2.1) — Platform-independent interfaces (`ISpeechRecognizer`, `IAudioRecorder`, `ITextInjector`, `IHotkeyManager`, `IVoiceActivityDetector`) and models (`AudioData` as PCM 16kHz mono 16-bit, `RecognitionResult`, `AppSettings`, `StreamingSettings`). No external dependencies.
- **LWhisper.SpeechEngine** (net8.0) — `WhisperSpeechRecognizer` wrapping Whisper.net with CUDA GPU fallback to CPU. References Core.
- **LWhisper.UI.WPF** (net8.0-windows) — WPF app, Windows-specific services, all UI. References Core and SpeechEngine.

This separation exists for future Linux/Home Assistant portability — Core and SpeechEngine stay unchanged, only UI layer needs replacement.

## Key Data Flows

**Normal mode:** Widget click → `NAudioRecorder` captures audio → `WhisperSpeechRecognizer.RecognizeAsync()` → `PreviewWindow` shows text → auto-timer or manual insert → `WindowsTextInjector` sends keystrokes to foreground window.

**Streaming mode:** Widget click → `StreamingAudioRecorder` captures audio → `WebRtcVoiceActivityDetector` detects speech pauses → `SegmentRecognitionManager` processes segments in parallel (max 3 concurrent via semaphore) → progressive text updates → final insert.

## Application Entry Point

`App.xaml.cs` (~756 lines) is the central orchestrator — initializes all services, wires events between components, and manages the full recording-recognition-injection lifecycle. This is the first place to look when understanding control flow.

## Runtime Files

All user data lives in `%APPDATA%\LWhisper\`:
- `settings.json` — app configuration (managed by `SettingsManager`)
- `Models\ggml-{size}.bin` — Whisper model files (downloaded on-demand via Settings UI)
- `logs\log-*.txt` — Serilog daily rotation, 10MB limit, 7-day retention

## Key Dependencies

| Package | Purpose |
|---------|---------|
| Whisper.net + Runtime + Runtime.Cuda | Speech recognition (CPU + GPU) |
| NAudio | Audio capture |
| WebRtcVadSharp | Voice Activity Detection for streaming mode |
| Hardcodet.NotifyIcon.Wpf | System tray icon |
| Serilog + Sinks | Structured logging to console and file |

## Important Implementation Details

- `WindowsTextInjector` uses clipboard + `SendInput` with `KEYEVENTF_UNICODE` for text injection — be aware of clipboard side effects.
- `MockSpeechRecognizer` is used when no Whisper model is available, enabling UI development without the ~500MB model file.
- Event-driven architecture with heavy use of C# events for loose coupling between components.
- All audio processing uses async/await patterns to keep the WPF UI thread responsive.
