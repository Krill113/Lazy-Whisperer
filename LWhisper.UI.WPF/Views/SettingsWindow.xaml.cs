using System.Windows;
using System.IO;
using System.Net.Http;
using LWhisper.Core.Models;
using LWhisper.UI.WPF.Services;
using NAudio.Wave;

namespace LWhisper.UI.WPF.Views
{
    /// <summary>
    /// Окно настроек приложения
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public AppSettings Settings { get; private set; }
        private readonly HttpClient _httpClient = new();
        private static readonly string[] _aggressivenessLabels = { "Мягкий", "Норма", "Строгий", "Максимум" };

        /// <summary>
        /// Элемент списка моделей для отображения в UI
        /// </summary>
        private class ModelListItem
        {
            public string Id { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string SizeText { get; init; } = "";
            public string RamText { get; init; } = "";
            public string QuantizationBadge { get; init; } = "";
            public string StatusIndicator { get; init; } = "";
        }

        public SettingsWindow(AppSettings currentSettings, List<string> audioDevices)
        {
            InitializeComponent();

            Settings = new AppSettings
            {
                RecordingMode = currentSettings.RecordingMode,
                HotkeyBinding = currentSettings.HotkeyBinding,
                AutoInsertDelaySeconds = currentSettings.AutoInsertDelaySeconds,
                AutoInsertEnabled = currentSettings.AutoInsertEnabled,
                SelectedAudioDevice = currentSettings.SelectedAudioDevice,
                RecognitionLanguage = currentSettings.RecognitionLanguage,
                WhisperModelSize = currentSettings.WhisperModelSize,
                Streaming = currentSettings.Streaming ?? new StreamingSettings()
            };

            PopulateModelList();
            LoadSettings();
            LoadAudioDevices(audioDevices);
        }

        /// <summary>
        /// Заполнить список моделей из ModelCatalog и выбрать текущую модель
        /// </summary>
        private void PopulateModelList()
        {
            var items = new List<ModelListItem>();
            foreach (var model in ModelCatalog.All)
            {
                string sizeText = model.FileSizeMB >= 1024
                    ? $"{model.FileSizeMB / 1024.0:F1} ГБ"
                    : $"{model.FileSizeMB} МБ";

                string ramText = model.EstimatedRamMB >= 1024
                    ? $"~{model.EstimatedRamMB / 1024.0:F1} ГБ RAM"
                    : $"~{model.EstimatedRamMB} МБ RAM";

                string badge = model.IsQuantized ? $"[{model.QuantizationType}]" : "";

                string status = File.Exists(ModelCatalog.GetModelPath(model)) ? "✓" : "";

                items.Add(new ModelListItem
                {
                    Id = model.Id,
                    DisplayName = model.DisplayName,
                    SizeText = sizeText,
                    RamText = ramText,
                    QuantizationBadge = badge,
                    StatusIndicator = status
                });
            }

            WhisperModelListBox.ItemsSource = items;

            // Выбрать текущую модель
            var selected = items.Find(i => i.Id == Settings.WhisperModelSize);
            if (selected != null)
            {
                WhisperModelListBox.SelectedItem = selected;
            }
            else if (items.Count > 0)
            {
                // Fallback: выбрать модель по умолчанию
                var defaultItem = items.Find(i => i.Id == ModelCatalog.DefaultModelId);
                WhisperModelListBox.SelectedItem = defaultItem ?? items[0];
            }

            CheckModelStatus();
        }

        private void LoadSettings()
        {
            switch (Settings.RecordingMode)
            {
                case RecordingMode.Toggle:
                    ToggleModeRadio.IsChecked = true;
                    break;
                case RecordingMode.PushToTalk:
                    PushToTalkRadio.IsChecked = true;
                    break;
                case RecordingMode.Hotkey:
                    HotkeyRadio.IsChecked = true;
                    break;
            }

            HotkeyTextBox.Text = Settings.HotkeyBinding ?? "Ctrl+Shift+Space";
            AutoInsertDelayTextBox.Text = Settings.AutoInsertDelaySeconds.ToString();
            AutoInsertEnabledCheckBox.IsChecked = Settings.AutoInsertEnabled;

            // Загрузить настройки потокового режима
            StreamingEnabledCheckBox.IsChecked = Settings.Streaming?.Enabled ?? true;
            PauseThresholdTextBox.Text = Settings.Streaming?.PauseThresholdMs.ToString() ?? "1000";
            AutoStopCheckBox.IsChecked = Settings.Streaming?.AutoStopOnLongPause ?? false;
            AutoStopPauseTextBox.Text = Settings.Streaming?.AutoStopPauseDurationMs.ToString() ?? "3000";

            // Загрузить настройки VAD
            VadAggressivenessSlider.Value = Settings.Streaming?.VadAggressiveness ?? 2;
            PostSpeechPaddingSlider.Value = Settings.Streaming?.PostSpeechPaddingMs ?? 400;

            // Выбрать язык распознавания
            foreach (System.Windows.Controls.ComboBoxItem item in LanguageComboBox.Items)
            {
                if (item.Tag.ToString() == Settings.RecognitionLanguage)
                {
                    LanguageComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void LoadAudioDevices(List<string> devices)
        {
            AudioDeviceComboBox.ItemsSource = devices;
            if (!string.IsNullOrEmpty(Settings.SelectedAudioDevice))
            {
                AudioDeviceComboBox.SelectedItem = Settings.SelectedAudioDevice;
            }
            else if (devices.Count > 0)
            {
                AudioDeviceComboBox.SelectedIndex = 0;
            }
        }

        private void WhisperModelListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            CheckModelStatus();
        }

        private void CheckModelStatus()
        {
            var selectedItem = WhisperModelListBox.SelectedItem as ModelListItem;
            if (selectedItem == null) return;

            var model = ModelCatalog.GetById(selectedItem.Id);
            if (model == null) return;

            var modelPath = ModelCatalog.GetModelPath(model);
            if (File.Exists(modelPath))
            {
                ModelStatusText.Text = "✓ Модель установлена";
                ModelStatusText.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                ModelStatusText.Text = "✗ Модель не установлена — нажмите «Скачать»";
                ModelStatusText.Foreground = System.Windows.Media.Brushes.Orange;
            }
        }

        private async void DownloadModelButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = WhisperModelListBox.SelectedItem as ModelListItem;
            if (selectedItem == null) return;

            var model = ModelCatalog.GetById(selectedItem.Id);
            if (model == null) return;

            var modelPath = ModelCatalog.GetModelPath(model);

            if (File.Exists(modelPath))
            {
                var result = MessageBox.Show($"Модель {model.DisplayName} уже установлена. Скачать заново?",
                    "LWhisper", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }

            try
            {
                DownloadModelButton.IsEnabled = false;
                DownloadProgressBar.Visibility = Visibility.Visible;
                DownloadStatusText.Text = "Скачивание модели...";

                var modelUrl = ModelCatalog.GetDownloadUrl(model);

                using (var response = await _httpClient.GetAsync(modelUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(modelPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;

                            if (totalBytes > 0)
                            {
                                var progress = (double)totalRead / totalBytes * 100;
                                DownloadProgressBar.Value = progress;
                                DownloadStatusText.Text = $"Скачано {totalRead / 1024 / 1024} МБ из {totalBytes / 1024 / 1024} МБ";
                            }
                        }
                    }
                }

                Settings.WhisperModelSize = model.Id;
                DownloadStatusText.Text = "✓ Модель успешно скачана!";
                DownloadStatusText.Foreground = System.Windows.Media.Brushes.Green;

                // Обновить список — показать индикатор установки
                PopulateModelList();

                MessageBox.Show("Модель успешно скачана и готова к использованию!", "LWhisper",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DownloadStatusText.Text = $"✗ Ошибка: {ex.Message}";
                DownloadStatusText.Foreground = System.Windows.Media.Brushes.Red;
                MessageBox.Show($"Ошибка скачивания модели: {ex.Message}", "LWhisper",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DownloadModelButton.IsEnabled = true;
                DownloadProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void VadAggressivenessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int level = (int)e.NewValue;
            if (VadAggressivenessLabel != null)
                VadAggressivenessLabel.Text = $"{level} — {_aggressivenessLabels[level]}";
        }

        private void PostSpeechPaddingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PostSpeechPaddingLabel != null)
                PostSpeechPaddingLabel.Text = $"{(int)e.NewValue} мс";
        }

        /// <summary>
        /// Калибровка VAD: запись 3 секунд тишины, вычисление RMS/dBFS, автоподбор aggressiveness
        /// </summary>
        private async void CalibrateVadButton_Click(object sender, RoutedEventArgs e)
        {
            CalibrateVadButton.IsEnabled = false;
            CalibrationProgressBar.Visibility = Visibility.Visible;
            CalibrationProgressBar.Value = 0;
            CalibrationResultText.Text = "Запись тишины...";
            CalibrationResultText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#333333"));

            WaveInEvent? waveIn = null;
            try
            {
                var samples = new List<short>();
                var tcs = new TaskCompletionSource<bool>();
                var shouldStop = false;

                waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 100
                };

                waveIn.DataAvailable += (_, args) =>
                {
                    if (shouldStop) return;

                    for (int i = 0; i + 1 < args.BytesRecorded; i += 2)
                        samples.Add(BitConverter.ToInt16(args.Buffer, i));

                    // 48000 samples = 3 sec at 16kHz
                    var progress = Math.Min(100.0, samples.Count / 480.0);
                    Dispatcher.Invoke(() =>
                    {
                        if (CalibrationProgressBar.Visibility == Visibility.Visible)
                            CalibrationProgressBar.Value = progress;
                    });

                    if (samples.Count >= 48000)
                    {
                        shouldStop = true;
                        tcs.TrySetResult(true);
                    }
                };

                waveIn.RecordingStopped += (_, _) =>
                {
                    tcs.TrySetResult(true);
                };

                waveIn.StartRecording();

                // Wait for completion or 4-second timeout
                await Task.WhenAny(tcs.Task, Task.Delay(4000));

                shouldStop = true;
                waveIn.StopRecording();

                if (samples.Count < 8000) // Less than 0.5 sec of data
                {
                    CalibrationResultText.Text = "Ошибка калибровки. Проверьте доступность микрофона.";
                    CalibrationResultText.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E74C3C"));
                    return;
                }

                // Calculate RMS
                double sumSquares = 0;
                for (int i = 0; i < samples.Count; i++)
                    sumSquares += (double)samples[i] * samples[i];
                double rms = Math.Sqrt(sumSquares / samples.Count);
                double dBFS = rms > 0 ? 20 * Math.Log10(rms / 32768.0) : -96.0;

                // Map dB to aggressiveness (UI-SPEC algorithm)
                int recommended;
                string noiseLevel;
                string resultColor;

                if (dBFS < -50)
                {
                    recommended = 0;
                    noiseLevel = "тихо";
                    resultColor = "#27AE60";
                }
                else if (dBFS < -40)
                {
                    recommended = 1;
                    noiseLevel = "нормально";
                    resultColor = "#333333";
                }
                else if (dBFS < -30)
                {
                    recommended = 2;
                    noiseLevel = "шумно";
                    resultColor = "#333333";
                }
                else
                {
                    recommended = 3;
                    noiseLevel = "очень шумно";
                    resultColor = "#E67E22";
                }

                // Update slider
                VadAggressivenessSlider.Value = recommended;

                // Show result
                CalibrationResultText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(resultColor));
                CalibrationResultText.Text = $"Уровень шума: {dBFS:F1} дБ ({noiseLevel}). Рекомендуется: {recommended} — {_aggressivenessLabels[recommended]}";

                // Warning for high noise
                if (dBFS > -30)
                {
                    CalibrationResultText.Text += "\nВысокий уровень шума. Рекомендуем тихое помещение.";
                }

                Serilog.Log.Information("Калибровка VAD: {dBFS:F1} дБ, рекомендуется aggressiveness={Recommended}", dBFS, recommended);
            }
            catch (Exception ex)
            {
                CalibrationResultText.Text = "Ошибка калибровки. Проверьте доступность микрофона.";
                CalibrationResultText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E74C3C"));
                Serilog.Log.Error(ex, "Ошибка калибровки VAD");
            }
            finally
            {
                waveIn?.Dispose();
                CalibrationProgressBar.Visibility = Visibility.Collapsed;
                CalibrateVadButton.IsEnabled = true;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ToggleModeRadio.IsChecked == true)
                Settings.RecordingMode = RecordingMode.Toggle;
            else if (PushToTalkRadio.IsChecked == true)
                Settings.RecordingMode = RecordingMode.PushToTalk;
            else if (HotkeyRadio.IsChecked == true)
                Settings.RecordingMode = RecordingMode.Hotkey;

            Settings.HotkeyBinding = HotkeyTextBox.Text;

            if (int.TryParse(AutoInsertDelayTextBox.Text, out int delay))
            {
                Settings.AutoInsertDelaySeconds = Math.Max(0, delay);
            }

            Settings.AutoInsertEnabled = AutoInsertEnabledCheckBox.IsChecked == true;

            Settings.SelectedAudioDevice = AudioDeviceComboBox.SelectedItem as string;

            // Сохранить выбранную модель
            var selectedModel = WhisperModelListBox.SelectedItem as ModelListItem;
            if (selectedModel != null)
            {
                Settings.WhisperModelSize = selectedModel.Id;
            }

            // Сохранить язык распознавания (SETT-01, SETT-02)
            var selectedLanguage = LanguageComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem;
            if (selectedLanguage != null)
            {
                Settings.RecognitionLanguage = selectedLanguage.Tag.ToString()!;
            }

            // Сохранить настройки потокового режима
            if (Settings.Streaming == null)
            {
                Settings.Streaming = new LWhisper.Core.Models.StreamingSettings();
            }

            Settings.Streaming.Enabled = StreamingEnabledCheckBox.IsChecked == true;

            if (int.TryParse(PauseThresholdTextBox.Text, out int pauseThreshold))
            {
                Settings.Streaming.PauseThresholdMs = Math.Max(100, Math.Min(pauseThreshold, 5000));
            }

            Settings.Streaming.AutoStopOnLongPause = AutoStopCheckBox.IsChecked == true;

            if (int.TryParse(AutoStopPauseTextBox.Text, out int autoStopPause))
            {
                Settings.Streaming.AutoStopPauseDurationMs = Math.Max(1000, Math.Min(autoStopPause, 10000));
            }

            Settings.Streaming.VadAggressiveness = Math.Max(0, Math.Min((int)VadAggressivenessSlider.Value, 3));
            Settings.Streaming.PostSpeechPaddingMs = Math.Max(100, Math.Min((int)PostSpeechPaddingSlider.Value, 800));

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
