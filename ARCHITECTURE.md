# Архитектура LWhisper

## Обзор

LWhisper построен на принципах Clean Architecture с модульным разделением для обеспечения портируемости на другие платформы.

## Структура проектов

### 1. LWhisper.Core (netstandard2.1)

Ядро системы без зависимостей от платформы.

**Интерфейсы:**
- `ISpeechRecognizer` - Распознавание речи
- `IAudioRecorder` - Запись аудио
- `ITextInjector` - Вставка текста
- `IHotkeyManager` - Глобальные горячие клавиши

**Модели:**
- `AudioData` - Аудио данные (PCM 16kHz mono 16-bit)
- `RecognitionResult` - Результат распознавания
- `RecordingMode` - Режимы записи (Toggle/PushToTalk/Hotkey)
- `AppSettings` - Настройки приложения

### 2. LWhisper.SpeechEngine (net8.0)

Реализация распознавания речи через Whisper.

**Классы:**
- `WhisperSpeechRecognizer` - Обработка аудио через Whisper.net

**Зависимости:**
- Whisper.net 1.9.0
- Whisper.net.Runtime 1.9.0

### 3. LWhisper.UI.WPF (net8.0-windows)

Windows-специфичная реализация UI и сервисов.

**Views:**
- `FloatingMicrophoneWidget` - Плавающий виджет микрофона
- `PreviewWindow` - Окно предпросмотра текста
- `SettingsWindow` - Окно настроек

**Services:**
- `NAudioRecorder` - Запись аудио через NAudio
- `WindowsTextInjector` - Вставка текста через WinAPI SendInput
- `WindowsHotkeyManager` - Глобальные хоткеи через RegisterHotKey
- `TrayIconManager` - Управление иконкой в системном трее
- `SettingsManager` - Сохранение/загрузка настроек
- `MockSpeechRecognizer` - Заглушка для тестирования

**Зависимости:**
- NAudio 2.2.1
- Hardcodet.NotifyIcon.Wpf 2.0.1
- Serilog 4.3.0
- Serilog.Sinks.File 7.0.0
- System.Text.Json 10.0.2

## Диаграмма компонентов

```
┌─────────────────────────────────────────────────────────┐
│                   LWhisper.UI.WPF                       │
│  ┌──────────────────┐  ┌────────────────────────────┐  │
│  │  Views           │  │  Services                  │  │
│  │  - Widget        │  │  - NAudioRecorder          │  │
│  │  - PreviewWindow │  │  - WindowsTextInjector     │  │
│  │  - Settings      │  │  - WindowsHotkeyManager    │  │
│  │  - TrayIcon      │  │  - SettingsManager         │  │
│  └──────────────────┘  └────────────────────────────┘  │
│         │                        │                      │
│         └────────────┬───────────┘                      │
│                      ▼                                  │
├─────────────────────────────────────────────────────────┤
│              Implements Interfaces                      │
└─────────────────────────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────┐
│                  LWhisper.Core                          │
│  ┌──────────────────────────────────────────────────┐  │
│  │  Interfaces                                       │  │
│  │  - ISpeechRecognizer                             │  │
│  │  - IAudioRecorder                                │  │
│  │  - ITextInjector                                 │  │
│  │  - IHotkeyManager                                │  │
│  └──────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────┐  │
│  │  Models                                           │  │
│  │  - AudioData, RecognitionResult, AppSettings     │  │
│  └──────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                       ▲
                       │
                       │ Implements
┌─────────────────────────────────────────────────────────┐
│             LWhisper.SpeechEngine                       │
│  ┌──────────────────────────────────────────────────┐  │
│  │  WhisperSpeechRecognizer                         │  │
│  │  - Whisper.net integration                       │  │
│  │  - Model: ggml-small.bin                         │  │
│  └──────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
```

## Поток данных

### Запись и распознавание

```
User clicks widget
    ↓
FloatingMicrophoneWidget.RecordingStarted event
    ↓
NAudioRecorder.StartRecording()
    ↓
[User speaks]
    ↓
FloatingMicrophoneWidget.RecordingStopped event
    ↓
NAudioRecorder.StopRecordingAsync() → AudioData
    ↓
WhisperSpeechRecognizer.RecognizeAsync(audioData) → RecognitionResult
    ↓
PreviewWindow.ShowWithText(result.Text)
    ↓
[Auto-timer or user clicks Insert]
    ↓
WindowsTextInjector.InjectTextAsync(text)
    ↓
Text appears in active window
```

## Конфигурация

### Файлы

- **Настройки**: `%APPDATA%\LWhisper\settings.json`
- **Логи**: `%APPDATA%\LWhisper\logs\log-YYYYMMDD.txt`
- **Модели**: `%APPDATA%\LWhisper\Models\ggml-{size}.bin`

### Настройки по умолчанию

```json
{
  "RecordingMode": "Toggle",
  "HotkeyBinding": "Ctrl+Shift+Space",
  "AutoInsertDelaySeconds": 2,
  "WidgetPositionX": 100,
  "WidgetPositionY": 100,
  "RecognitionLanguage": "auto",
  "WhisperModelSize": "small"
}
```

## Логирование

Используется Serilog с записью в файл:
- **Уровни**: Debug, Info, Warning, Error, Fatal
- **Ротация**: По дням
- **Лимит размера**: 10 MB на файл
- **Хранение**: 7 дней

## WinAPI интеграция

### Вставка текста
- `SendInput` - эмуляция клавиатуры
- `KEYEVENTF_UNICODE` - поддержка Unicode символов

### Горячие клавиши
- `RegisterHotKey` - регистрация глобального хоткея
- `WM_HOTKEY` - обработка сообщения

### Окна
- `GetForegroundWindow` / `SetForegroundWindow` - управление фокусом

## Портируемость

Для портирования на Linux/Home Assistant:

1. **Создать новый UI проект** (Console/Avalonia/GTK)
2. **Заменить Windows-сервисы**:
   - `NAudioRecorder` → `OpenALRecorder` / `PortAudioRecorder`
   - `WindowsTextInjector` → `XDoToolInjector`
   - `WindowsHotkeyManager` → `LinuxHotkeyManager`
3. **Core и SpeechEngine остаются без изменений**

## Производительность

- **Модель small**: ~500 MB, ~1-2 сек на 10 сек аудио
- **Модель base**: ~150 MB, менее точная
- **Модель medium**: ~1.5 GB, более точная

## Безопасность

- Полностью оффлайн работа
- Локальная обработка данных
- Нет отправки аудио в облако
- Настройки хранятся локально


