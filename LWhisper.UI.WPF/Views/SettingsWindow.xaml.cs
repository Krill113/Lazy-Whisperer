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
        /// <summary>
        /// Флаг принятия настроек — используется вместо DialogResult для совместимости с немодальным режимом (Show())
        /// </summary>
        public bool SettingsAccepted { get; private set; }
        private readonly HttpClient _httpClient = new();
        private static readonly string[] _aggressivenessLabels = { "Мягкий", "Норма", "Строгий", "Максимум" };
        private readonly UpdateService _updateService;
        private UpdateInfo? _availableUpdate;

        /// <summary>Найдено обновление (ручной проверкой) — App синхронизирует своё состояние</summary>
        public event Action<UpdateInfo>? UpdateFound;

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

        public SettingsWindow(AppSettings currentSettings, List<string> audioDevices,
            UpdateService? updateService = null, UpdateInfo? availableUpdate = null)
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
                AutoUpdateCheckEnabled = currentSettings.AutoUpdateCheckEnabled,
                Streaming = currentSettings.Streaming ?? new StreamingSettings()
            };

            _updateService = updateService ?? new UpdateService();
            _availableUpdate = availableUpdate;

            PopulateModelList();
            LoadSettings();
            LoadAudioDevices(audioDevices);
            InitializeUpdateSection();
        }

        /// <summary>
        /// Заполнить секцию «Обновления»: версия, найденный ранее апдейт (из фоновой проверки при старте)
        /// </summary>
        private void InitializeUpdateSection()
        {
            CurrentVersionText.Text = UpdateService.IsDevBuild
                ? $"{UpdateService.CurrentVersion} (dev-сборка)"
                : UpdateService.CurrentVersion.ToString();

            if (_availableUpdate != null)
            {
                ShowAvailableUpdate(_availableUpdate);
            }
        }

        private void ShowAvailableUpdate(UpdateInfo update)
        {
            _availableUpdate = update;
            UpdateAvailableText.Text = $"Доступна версия {update.Version}";
            UpdateAvailablePanel.Visibility = Visibility.Visible;
            UpdateCheckStatusText.Text = "";
            UpdateFound?.Invoke(update);
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateCheckStatusText.Text = "Проверка...";
            try
            {
                var update = await _updateService.CheckAsync();
                if (update != null)
                {
                    ShowAvailableUpdate(update);
                }
                else
                {
                    UpdateAvailablePanel.Visibility = Visibility.Collapsed;
                    UpdateCheckStatusText.Text = UpdateService.IsDevBuild
                        ? "Новых релизов нет (dev-сборка сравнивается как 0.0.0)"
                        : "У вас последняя версия";
                }
            }
            catch (UpdateCheckException ex)
            {
                UpdateCheckStatusText.Text = ex.RateLimited
                    ? "GitHub ограничил частоту запросов — попробуйте позже"
                    : $"Не удалось проверить: {ex.Message}";
            }
            catch (Exception ex)
            {
                UpdateCheckStatusText.Text = $"Ошибка: {ex.Message}";
                Serilog.Log.Error(ex, "Ошибка ручной проверки обновлений");
            }
            finally
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
        {
            // Снапшот: параллельная «Проверить сейчас» может подменить _availableUpdate,
            // и LaunchUpdater получил бы версию, не соответствующую скачанному ZIP
            var update = _availableUpdate;
            if (update == null) return;

            if (UpdateService.IsDevBuild)
            {
                MessageBox.Show("Dev-сборка не обновляется автоматически. Соберите релиз или скачайте ZIP с GitHub.",
                    "LWhisper", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            UpdateNowButton.IsEnabled = false;
            CheckUpdatesButton.IsEnabled = false;
            UpdateProgressBar.Visibility = Visibility.Visible;
            UpdateStatusText.Text = "Скачивание...";
            try
            {
                var progress = new Progress<(long read, long total)>(p =>
                {
                    if (p.total > 0)
                    {
                        UpdateProgressBar.Value = (double)p.read / p.total * 100;
                        UpdateStatusText.Text = $"Скачано {p.read / 1024 / 1024} МБ из {p.total / 1024 / 1024} МБ";
                    }
                });

                var zipPath = await _updateService.DownloadAsync(update, progress);

                UpdateStatusText.Text = "Проверка целостности пройдена. Перезапуск для установки...";
                var confirm = MessageBox.Show(
                    $"Обновление {update.Version} скачано и проверено.\n\n" +
                    "Приложение закроется, обновится и запустится заново. Продолжить?",
                    "LWhisper", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (confirm == MessageBoxResult.Yes)
                {
                    _updateService.LaunchUpdater(zipPath, update.Version);
                    // Приложение завершается — сюда обычно уже не возвращаемся
                }
                else
                {
                    UpdateStatusText.Text = "Установка отложена — ZIP сохранён, можно установить позже";
                }
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = $"✗ {ex.Message}";
                Serilog.Log.Error(ex, "Ошибка скачивания/установки обновления");
                MessageBox.Show($"Не удалось обновиться: {ex.Message}", "LWhisper",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateNowButton.IsEnabled = true;
                CheckUpdatesButton.IsEnabled = true;
                UpdateProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void ReleaseNotesLink_Click(object sender, RoutedEventArgs e)
        {
            if (_availableUpdate == null) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_availableUpdate.Release.HtmlUrl)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Не удалось открыть страницу релиза");
            }
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
            AutoUpdateCheckBox.IsChecked = Settings.AutoUpdateCheckEnabled;

            // Загрузить настройки потокового режима
            StreamingEnabledCheckBox.IsChecked = Settings.Streaming?.Enabled ?? true;
            PauseThresholdTextBox.Text = Settings.Streaming?.PauseThresholdMs.ToString() ?? "1000";
            AutoStopCheckBox.IsChecked = Settings.Streaming?.AutoStopOnLongPause ?? false;
            AutoStopPauseTextBox.Text = Settings.Streaming?.AutoStopPauseDurationMs.ToString() ?? "3000";

            // Загрузить настройки VAD
            VadAggressivenessSlider.Value = Settings.Streaming?.VadAggressiveness ?? 2;
            PostSpeechPaddingSlider.Value = Settings.Streaming?.PostSpeechPaddingMs ?? 400;
            UseBeamSearchCheckBox.IsChecked = Settings.Streaming?.UseBeamSearch ?? false;

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
                var tempPath = modelPath + ".part";

                // Скачиваем во временный файл — целевой путь подменяем только после полной проверки.
                // Иначе обрыв связи оставлял битый файл, который File.Exists считал «установленной моделью».
                using (var response = await _httpClient.GetAsync(modelUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
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

                        // Проверка целостности: если сервер прислал Content-Length — размер должен совпасть.
                        if (totalBytes > 0 && totalRead != totalBytes)
                        {
                            throw new IOException($"Скачано {totalRead} из {totalBytes} байт — файл неполный (обрыв связи?)");
                        }
                    }
                }

                // Атомарная замена: подменяем целевой файл только после успешного полного скачивания.
                if (File.Exists(modelPath)) File.Delete(modelPath);
                File.Move(tempPath, modelPath);

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
                // Убрать недокачанный временный файл, чтобы он не подменял реальную модель
                try { var partPath = modelPath + ".part"; if (File.Exists(partPath)) File.Delete(partPath); } catch { }

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
        /// Калибровка VAD: запись 5 секунд тишины, вычисление RMS/dBFS, автоподбор aggressiveness
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

                    // 80000 samples = 5 sec at 16kHz
                    var progress = Math.Min(100.0, samples.Count / 800.0);
                    Dispatcher.Invoke(() =>
                    {
                        if (CalibrationProgressBar.Visibility == Visibility.Visible)
                            CalibrationProgressBar.Value = progress;
                    });

                    if (samples.Count >= 80000)
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

                // Wait for completion or 6-second timeout (1 sec safety margin over 5-sec recording)
                await Task.WhenAny(tcs.Task, Task.Delay(6000));

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

                // Map dB to aggressiveness — пороги сдвинуты на 10 dB вниз чтобы корректно работать
                // с микрофонными массивами с встроенным DSP (Windows noise suppression / beamforming),
                // которые режут фон до -55..-65 dBFS даже в обычной комнате.
                int recommended;
                string noiseLevel;
                string resultColor;

                if (dBFS < -60)
                {
                    recommended = 0;
                    noiseLevel = "очень тихо";
                    resultColor = "#27AE60";
                }
                else if (dBFS < -50)
                {
                    recommended = 1;
                    noiseLevel = "тихо";
                    resultColor = "#333333";
                }
                else if (dBFS < -40)
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

                // Warning for high noise (сдвинуто с -30 до -40 синхронно с новой шкалой)
                if (dBFS > -40)
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
            Settings.AutoUpdateCheckEnabled = AutoUpdateCheckBox.IsChecked == true;

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
            Settings.Streaming.UseBeamSearch = UseBeamSearchCheckBox.IsChecked == true;

            SettingsAccepted = true;
            try { DialogResult = true; } catch (System.InvalidOperationException) { }
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsAccepted = false;
            try { DialogResult = false; } catch (System.InvalidOperationException) { }
            Close();
        }
    }
}
