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
- **LWhisper.SpeechEngine** (net8.0) — `WhisperSpeechRecognizer` wrapping Whisper.net with Vulkan GPU fallback to CPU. References Core.
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
| Whisper.net + Runtime + Runtime.Vulkan | Speech recognition (CPU + GPU via Vulkan) |
| NAudio | Audio capture |
| WebRtcVadSharp | Voice Activity Detection for streaming mode |
| Hardcodet.NotifyIcon.Wpf | System tray icon |
| Serilog + Sinks | Structured logging to console and file |

## Important Implementation Details

- `WindowsTextInjector` uses clipboard + `SendInput` with `KEYEVENTF_UNICODE` for text injection — be aware of clipboard side effects.
- `MockSpeechRecognizer` is used when no Whisper model is available, enabling UI development without the ~500MB model file.
- Event-driven architecture with heavy use of C# events for loose coupling between components.
- All audio processing uses async/await patterns to keep the WPF UI thread responsive.

## Project

**LWhisper**

Desktop Windows-приложение для офлайн голосового ввода текста с использованием Whisper AI. Захватывает звук с микрофона, распознаёт речь через Whisper.net и вставляет распознанный текст в активное окно через WinAPI SendInput. Используется ежедневно для продуктивной работы.

**Core Value:** Быстрый и точный голосовой ввод текста в любое активное окно Windows — без облака, без задержек, без мусора в распознанном тексте.

### Constraints

- **Platform**: Windows-only (WPF), но Core и SpeechEngine кроссплатформенные
- **Tech stack**: C# / .NET 8 / Whisper.net — менять нельзя
- **Offline**: Все операции локальные, без сети
- **Model size**: Модели Whisper до ~3GB (large-v3), учитывать место на диске

## Technology Stack

## Languages
- C# 12 - All three projects (.NET solution)
## Runtime
- .NET 8 (net8.0) — LWhisper.SpeechEngine and LWhisper.UI.WPF
- .NET Standard 2.1 (netstandard2.1) — LWhisper.Core (cross-platform compatibility layer)
- Windows (net8.0-windows with WPF) — UI layer only
- Cross-platform capable via .NET 8 (future Linux/Home Assistant ports would replace UI layer)
- NuGet (.NET package manager)
- No package-lock.json pattern (uses implicit NuGet restoration via .csproj)
## Frameworks
- WPF (Windows Presentation Foundation) — Desktop UI framework in `LWhisper.UI.WPF`
- Whisper.net 1.9.0 — C# wrapper for OpenAI's Whisper model
- Whisper.net.Runtime 1.9.0 — Base runtime
- Whisper.net.Runtime.Vulkan 1.9.0 — GPU acceleration via Vulkan (fallback to CPU if unavailable)
- NAudio 2.2.1 — Audio capture and playback
- WebRtcVadSharp 1.3.2 — Voice Activity Detection (VAD) for streaming mode
- Serilog 4.2.0 (SpeechEngine) / 4.3.0 (UI.WPF) — Structured logging
- Hardcodet.NotifyIcon.Wpf 2.0.1 — System tray icon support
- System.Drawing.Common 10.0.2 — Icon handling
- System.Text.Json 10.0.2 — JSON serialization for AppSettings (no external JSON library)
## Configuration
- Settings loaded from JSON file: `%APPDATA%\LWhisper\settings.json`
- `RecordingMode` — Toggle / PushToTalk / Hotkey
- `HotkeyBinding` — Default "Ctrl+Shift+Space"
- `AutoInsertDelaySeconds` — Default 2 seconds
- `AutoInsertEnabled` — Default true
- `SelectedAudioDevice` — Audio device ID
- `WidgetPositionX`, `WidgetPositionY` — Widget position persistence
- `RecognitionLanguage` — Default "auto", supports "ru", "en"
- `WhisperModelSize` — Default "small" (tiny, small, medium, large)
- `Streaming` — StreamingSettings object with VAD threshold parameters
- `Enabled` — Default false
- `PauseThresholdMs` — Default 1000ms (segment boundary detection)
- `MinSegmentDurationMs` — Default 1500ms (minimum segment length)
- `MaxSegmentDurationMs` — Default 15000ms (max segment length)
- `AutoStopOnLongPause` — Default false
- `AutoStopPauseDurationMs` — Default 3000ms
- `MaxParallelRecognitions` — Default 3 (concurrent Whisper recognition tasks)
- Solution uses Debug|Any CPU, Debug|x64, Debug|x86, Release variants
- Release build script: `build-release.bat` — Publishes self-contained win-x64 single-file executable
## Platform Requirements
- Visual Studio 17.14+ or JetBrains Rider (supports .NET 8 with C# 12)
- .NET 8 SDK
- Windows OS (for WPF development and testing)
- Windows 10 / Windows 11 (WPF required)
- Whisper.net requires:
- Self-contained executable includes all .NET runtime dependencies
## Runtime Files
- `settings.json` — User configuration and preferences
- `Models/ggml-*.bin` — Whisper model files (downloaded on-demand via Settings UI)
- `logs/log-*.txt` — Serilog daily rotation logs
## Build & Deployment
# Debug build
# Release build (self-contained single file)
# Debug build

## Conventions

## Naming Patterns
- PascalCase for service classes: `NAudioRecorder.cs`, `WindowsTextInjector.cs`, `SegmentRecognitionManager.cs`
- PascalCase for view/window classes: `FloatingMicrophoneWidget.xaml.cs`, `PreviewWindow.xaml.cs`
- XAML files mirror code-behind names: `FloatingMicrophoneWidget.xaml` + `FloatingMicrophoneWidget.xaml.cs`
- Interfaces prefixed with `I`: `ISpeechRecognizer`, `IAudioRecorder`, `ITextInjector`, `IHotkeyManager`
- Manager/orchestrator classes use *Manager suffix: `SegmentRecognitionManager`, `SettingsManager`, `TrayIconManager`
- Service implementations use platform-specific prefix: `Windows*` for Windows APIs (`WindowsTextInjector`, `WindowsHotkeyManager`)
- Recognizer classes specific to framework: `WhisperSpeechRecognizer`, `MockSpeechRecognizer`
- PascalCase for public methods: `StartRecording()`, `RecognizeAsync()`, `InjectTextAsync()`
- PascalCase for event handlers with prefix: `OnRecordingStarted()`, `OnRecordingStopped()`, `OnDataAvailable()`
- Async methods must use `Async` suffix: `InitializeAsync()`, `StopRecordingAsync()`, `RecognizeAsync()`
- Private helper methods use PascalCase: `CalculateAmplitudes()`, `CleanWhisperText()`, `RemoveDuplicatePrefix()`
- camelCase for local variables and parameters: `audioData`, `recognizer`, `isRecording`
- Underscore prefix for private fields: `_speechRecognizer`, `_isRecording`, `_currentState`
- SCREAMING_SNAKE_CASE for constants: `INPUT_KEYBOARD`, `KEYEVENTF_KEYUP`, `WM_PASTE`, `MOD_CONTROL`
- Prefix for backing fields in event handlers: `_callback`, `_ownWindow`, `_targetWindow`
- PascalCase for enum types: `WidgetState`, `RecordingMode`, `TrayIconState`
- SCREAMING_SNAKE_CASE for enum values: `RecordingMode.Toggle`, `WidgetState.Recording`
- PascalCase with descriptive names: `RecordingStarted`, `RecordingStopped`, `SegmentReady`, `FinalSegmentReady`
- Event handler delegates use `Action` or `Action<T>` type
- Event fields follow prefix pattern: `public event Action? RecordingStarted;`
## Code Style
- No `.editorconfig` or `.prettierrc` file — using implicit C# defaults
- Implicit .NET 8/C# 12 formatting conventions
- Braces on same line (Allman-style consistent): opening brace on same line as statement
- 4-space indentation (standard C#)
- Line length no strict limit observed
- No `.eslintrc` or static analysis configuration file found
- Using `<Nullable>enable</Nullable>` in project files: strict null checking enabled
- Using `<ImplicitUsings>enable</ImplicitUsings>`: global using directives for common namespaces
- Fields before constructors before properties before methods
- Public members before private members
- Event declarations with inline event field syntax: `public event Action? EventName;`
- XML documentation comments on all public types and methods
## Import Organization
- No path aliases (`@"" syntax`) used; all namespaces follow standard hierarchy
- Namespaces follow project structure: `LWhisper.Core.Models`, `LWhisper.UI.WPF.Services`
## Error Handling
- Try-catch-log-rethrow at initialization boundaries: `App.xaml.cs` catches exceptions during service setup and logs them
- Try-catch-return for method-level resilience: `SettingsManager.Load()` catches file I/O, returns defaults
- Try-catch-log-continue for ongoing operations: event handlers catch and log but don't stop execution
- Graceful degradation: Missing Whisper model falls back to `MockSpeechRecognizer`
- No throwing of generic `Exception` — specific exception types used (from .NET BCL)
- `OperationCanceledException` — explicitly caught for cancellation tokens in `SegmentRecognitionManager`
- `ArgumentNullException` — thrown with `nameof()` operator for guard clauses: `recognizer ?? throw new ArgumentNullException(nameof(recognizer))`
- File I/O exceptions — silently caught in `SettingsManager` with default return
- WinAPI failures — wrapped but not re-thrown; logged and alternatives tried
- `RecognitionResult` model used to encapsulate success/failure states, not exceptions
## Logging
- Initialized in `App.xaml.cs` OnStartup: `Log.Logger = new LoggerConfiguration()`
- Minimum level: Debug
- Two sinks: Console and File (daily rolling, 10MB per file, 7-day retention)
- File path: `%APPDATA%\LWhisper\logs\log-*.txt`
- Closed cleanly in `App.xaml.cs` OnExit: `Log.CloseAndFlush();`
- **Information level**: Feature-level events — initialization complete, mode selected, text recognized
- **Debug level**: Detailed control flow — method entry, state changes, intermediate results
- **Warning level**: Recoverable issues, unusual but safe conditions
- **Error level**: Failures with fallback or retry, exceptions that don't stop execution
- **Fatal level**: Unrecoverable errors that cause shutdown
- Named parameters with braces: `Log.Information("Text {Variable1} more {Variable2}", value1, value2)`
- Russian strings for user-facing messages
- Context identifiers for async operations: `[Segment #{Id}]` prefix in `SegmentRecognitionManager`
- Performance metrics included: `{Elapsed}мс`, `{Length} символов`, `{Duration}мс`
- Secrets or credentials — environment variables never logged
- Sensitive window content — only window titles logged
- Clipboard contents — not logged
## Comments
- XML documentation on all public classes, interfaces, methods, properties
- **Inline comments** used sparingly — only for non-obvious algorithms
- **Section headers** for logical blocks in long methods (e.g., "ПОТОКОВЫЙ РЕЖИМ" in `OnRecordingStopped()`)
- All public types use `/// <summary>` XML comments
- Russian language for comments (matching codebase language)
- Example from `AudioData.cs`:
- Interface method comments document contract behavior
- No comments explaining obvious code: `if (_isRecording) return;` needs no comment
- No changelog comments in code — git history used instead
## Function Design
- Methods average 20-50 lines
- Complex async orchestration can be 80-150 lines (e.g., `OnRecordingStopped()` async method)
- Limit: Methods over 200 lines considered candidates for refactoring
- Most methods: 0-2 parameters
- Dependency injection via constructor fields, not parameter passing between methods
- Long parameter lists avoided — related params grouped in objects (e.g., `StreamingSettings`)
- `CancellationToken` parameter convention: always last parameter, optional with `= default`
- Async methods always return `Task` or `Task<T>`, never `void` for async
- Void returns only for synchronous event handlers or UI callbacks
- Result objects used to communicate success/failure: `RecognitionResult`, `AudioData`
- Null returns avoided — use empty defaults or exceptions
## Async/Await Patterns
- Async/await throughout for I/O and CPU-bound operations
- No blocking calls on UI thread (`Wait()`, `Result` avoided on main thread)
- `Task.Run()` used to offload heavy work from UI thread
- Desktop WPF app — UI thread synchronization context always needed
- No `ConfigureAwait(false)` used (would break UI updates)
## Event-Driven Architecture
- Heavy use of C# events for loose coupling between components
- Events published by one component, subscribed by orchestrator (`App.xaml.cs`)
- Simple notifications: `public event Action? EventName;`
- With data: `public event Action<T1, T2>? EventName;`
- Multi-parameter events: `PositionChanged` passes X, Y coordinates as separate params
- Rarely used delegates — `EventHandler<TArgs>` pattern not prevalent
- Events invoked on UI thread via `Dispatcher.Invoke()`
- Audio event handlers in `StreamingAudioRecorder` marshal back to UI via events
- No cross-thread direct field access
## Null Safety
- `<Nullable>enable</Nullable>` in all .csproj files
- `?` used consistently for optional properties: `public string? HotkeyBinding { get; set; }`
- `??` null coalescing for defaults: `_settings.SelectedAudioDevice ?? "default"`
- `!` null-forgiving operator used sparingly for proven non-null paths:
- Guard clauses prefer throw over conditional: `recognizer ?? throw new ArgumentNullException(nameof(recognizer))`
## XAML Conventions
- Standard WPF XAML with namespace declarations at top
- `x:Class` binding to code-behind
- `x:Name` for element references in code-behind (PascalCase): `MicrophoneButton`, `MinimizeButton`
- Named resources for brushes and styles (rarely used in this codebase)
- `x:Name="MicrophoneButton"` referenced in code-behind as `MicrophoneButton`
- Event handlers use underscore-prefix syntax: `MouseDown="Window_MouseDown"` → `private void Window_MouseDown(...)`
- Data binding not heavily used (event-driven instead)
- Direct property setting from code-behind preferred
- No MVVM pattern — code-behind directly manipulates UI state
- Inline gradient brushes: `RadialGradientBrush` in `FloatingMicrophoneWidget.xaml`
- Dynamic color changes in code-behind via brush assignment
- Drop shadows and effects defined inline: `<Ellipse.Effect><DropShadowEffect.../></Ellipse.Effect>`

## Architecture

## Pattern Overview
- Strict separation of concerns: platform-agnostic Core, engine abstraction (SpeechEngine), platform-specific UI layer
- Interface-based abstraction for all major components (speaker recognition, audio recording, text injection, hotkeys, VAD)
- Event-driven communication between layers with loose coupling via C# events
- Support for dual recording modes: traditional full-capture and streaming with Voice Activity Detection (VAD)
- Async/await throughout to maintain responsive WPF UI thread
## Layers
- Purpose: Platform-independent contract definitions and data models
- Location: `LWhisper.Core/`
- Contains: Interface definitions in `Interfaces/`, data model classes in `Models/`
- Depends on: Only standard .NET libraries (zero external dependencies)
- Used by: Both SpeechEngine and UI.WPF
- Purpose: Speech recognition engine abstraction and Whisper.net implementation
- Location: `LWhisper.SpeechEngine/`
- Contains: `WhisperSpeechRecognizer` implementing `ISpeechRecognizer` interface
- Depends on: Core layer, Whisper.net + Vulkan/CUDA GPU runtimes
- Used by: UI.WPF layer during initialization and recognition pipeline
- Key feature: Automatic fallback from GPU (Vulkan) to CPU if unavailable
- Purpose: Application UI, Windows-specific service implementations, orchestration logic
- Location: `LWhisper.UI.WPF/`
- Contains: XAML views, Windows-specific service implementations, central App.xaml.cs orchestrator
- Depends on: Core and SpeechEngine layers
- Used by: User directly (entry point application)
## Data Flow
- `_isRecording`: Boolean flag prevents concurrent recording starts
- `_useStreamingMode`: Set at initialization based on settings; toggles entire VAD pipeline vs. traditional recording
- `_lastRecognizedText`: Persists last successful result for "Show Text" button when no active recording
- `SegmentRecognitionManager._recognizedSegments`: Dictionary maintains segment order and text for assembly
## Key Abstractions
- Purpose: Abstract speech recognition engine
- Examples: `WhisperSpeechRecognizer` (production), `MockSpeechRecognizer` (testing/UI development)
- Pattern: Async recognition with cancellation token support, readiness check via `IsReady` property
- Contract: `RecognizeAsync(AudioData, CancellationToken)` → `Task<RecognitionResult>`
- Purpose: Abstract audio capture device interaction
- Examples: `NAudioRecorder` (traditional full-capture), `StreamingAudioRecorder` (VAD-driven segmented)
- Pattern: Start/stop lifecycle with device enumeration and selection
- Events: `SegmentReady`, `FinalSegmentReady`, `RecordingAutoStopped` (used only in streaming mode)
- Contract: `StartRecording()`, `StopRecordingAsync()` → `AudioData`
- Purpose: Abstract text delivery to target application
- Examples: `WindowsTextInjector` (Windows API via clipboard + SendInput)
- Pattern: Remembers target window before recording, injects after recognition
- Contract: `InjectTextAsync(string)` → `Task`
- Purpose: Abstract global hotkey registration (system-wide, even when app not focused)
- Examples: `WindowsHotkeyManager` (Windows RegisterHotKey API)
- Pattern: Parse hotkey strings (e.g., "Ctrl+Shift+Space"), manage registration lifecycle
- Contract: `RegisterHotkey(string, Action)` → `bool`, `UnregisterHotkey()`, `IsRegistered` property
- Purpose: Abstract speech vs. silence detection for streaming segmentation
- Examples: `WebRtcVoiceActivityDetector` (WebRTC VAD library)
- Pattern: Frame-level analysis, returns confidence probability 0.0-1.0
- Contract: `IsSpeech(byte[], int sampleRate)` → `bool`, `GetSpeechProbability(byte[], int)` → `float`
## Entry Points
- Location: `LWhisper.UI.WPF/App.xaml.cs` (~756 lines)
- Triggers: Application startup via WPF Application class
- Responsibilities: 
- Key methods: `OnStartup()`, `InitializeSpeechRecognizer()`, `InitializeAudioRecorder()`, `OnRecordingStarted()`, `OnRecordingStopped()`
- Location: `LWhisper.UI.WPF/Views/FloatingMicrophoneWidget.xaml.cs`
- Triggers: User mouse clicks, drag/drop interactions
- Responsibilities: 
- Events: `RecordingStarted`, `RecordingStopped`, `PositionChanged`, `ShowTextRequested`, `RememberTargetWindow`
- Location: `LWhisper.UI.WPF/Views/PreviewWindow.xaml.cs`
- Triggers: After recognition completes, fires on every segment update in streaming mode
- Responsibilities:
- Events: `InsertRequested`, `AutoInsertSettingChanged`, `Closed`
- Location: `LWhisper.UI.WPF/MainWindow.xaml.cs`
- Purpose: Empty placeholder; app uses FloatingMicrophoneWidget and system tray exclusively
- Status: Not actively used; could be removed without impact
## Error Handling
- **Initialization Errors:** If model not found → fall back to `MockSpeechRecognizer` with dialog offering settings window
- **Recognition Errors:** Log error, close preview window, show user message, reset widget state to Idle
- **Recording Errors:** Reset state to Idle, show detailed error message
- **Text Injection Errors:** Log and display error; preview window remains open for manual retry
- **Settings Application Errors:** Caught at top level with user notification
- **Streaming Initialization Errors:** Fall back to traditional `NAudioRecorder` with warning log
- Framework: Serilog (structured logging)
- Levels: Debug (detailed flow), Information (state changes), Warning (graceful fallbacks), Error (failures), Fatal (startup crashes)
- Outputs: Console + rotating file in `%APPDATA%\LWhisper\logs\` (10MB daily rotation, 7-day retention)
## Cross-Cutting Concerns
- Serilog configured in `App.InitializeLogging()` with structured context (operation name, duration, results)
- Key operations: "Запись начата" (recording started), "Распознавание завершено" (recognition complete), segment processing with IDs
- Audio amplitude check in `SegmentRecognitionManager` (rejects noise below 0.03 normalized amplitude)
- Segment duration filters: min 1.5s, max 15s (configurable in StreamingSettings)
- Result validation: empty text rejected, silent segments skipped in streaming mode
- Device enumeration validates device ID before setting
- Not applicable (offline desktop app, no remote APIs)
- **UI Responsiveness:** Async/await pattern for all blocking operations (recognition, recording, text injection)
- **Parallel Recognition:** Streaming mode uses SemaphoreSlim to limit concurrent recognitions (configurable, default 3) preventing resource exhaustion
- **GPU Acceleration:** WhisperSpeechRecognizer tries Vulkan GPU first, falls back to CPU automatically
- **Buffer Management:** 30ms buffer interval for VAD (required for accuracy), memory streams freed immediately after StopRecording
- JSON file at `%APPDATA%\LWhisper\settings.json` managed by `SettingsManager`
- Persists: recording mode, hotkey binding, auto-insert delay, widget position, audio device ID, Whisper model size, streaming settings
<!-- symbiosis-brain v1: scope=lwhisper -->
