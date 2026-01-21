using System.Windows;
using System.IO;
using System.Net.Http;
using LWhisper.Core.Models;

namespace LWhisper.UI.WPF.Views
{
    /// <summary>
    /// Окно настроек приложения
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public AppSettings Settings { get; private set; }
        private readonly HttpClient _httpClient = new();

        public SettingsWindow(AppSettings currentSettings, List<string> audioDevices)
        {
            InitializeComponent();

            Settings = new AppSettings
            {
                RecordingMode = currentSettings.RecordingMode,
                HotkeyBinding = currentSettings.HotkeyBinding,
                AutoInsertDelaySeconds = currentSettings.AutoInsertDelaySeconds,
                SelectedAudioDevice = currentSettings.SelectedAudioDevice,
                WhisperModelSize = currentSettings.WhisperModelSize
            };

            LoadSettings();
            LoadAudioDevices(audioDevices);
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

            // Выбрать модель
            foreach (System.Windows.Controls.ComboBoxItem item in WhisperModelComboBox.Items)
            {
                if (item.Tag.ToString() == Settings.WhisperModelSize)
                {
                    WhisperModelComboBox.SelectedItem = item;
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

        private void CheckModelStatus()
        {
            var modelPath = GetModelPath(Settings.WhisperModelSize);
            if (File.Exists(modelPath))
            {
                ModelStatusText.Text = "✓ Модель установлена";
                ModelStatusText.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                ModelStatusText.Text = "✗ Модель не установлена";
                ModelStatusText.Foreground = System.Windows.Media.Brushes.Orange;
            }
        }

        private string GetModelPath(string modelSize)
        {
            var modelsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
            return Path.Combine(modelsDir, $"ggml-{modelSize}.bin");
        }

        private async void DownloadModelButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = WhisperModelComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem;
            if (selectedItem == null) return;

            var modelSize = selectedItem.Tag.ToString();
            var modelPath = GetModelPath(modelSize!);

            if (File.Exists(modelPath))
            {
                var result = MessageBox.Show($"Модель {modelSize} уже установлена. Скачать заново?", 
                    "LWhisper", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }

            try
            {
                DownloadModelButton.IsEnabled = false;
                DownloadProgressBar.Visibility = Visibility.Visible;
                DownloadStatusText.Text = "Скачивание модели...";

                // URL модели на HuggingFace
                var modelUrl = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{modelSize}.bin";

                var modelsDir = Path.GetDirectoryName(modelPath);
                if (!Directory.Exists(modelsDir))
                {
                    Directory.CreateDirectory(modelsDir!);
                }

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
                                DownloadStatusText.Text = $"Скачано {totalRead / 1024 / 1024} MB из {totalBytes / 1024 / 1024} MB";
                            }
                        }
                    }
                }

                Settings.WhisperModelSize = modelSize!;
                DownloadStatusText.Text = "✓ Модель успешно скачана!";
                DownloadStatusText.Foreground = System.Windows.Media.Brushes.Green;
                CheckModelStatus();

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

            Settings.SelectedAudioDevice = AudioDeviceComboBox.SelectedItem as string;

            // Сохранить выбранную модель
            var selectedItem = WhisperModelComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem;
            if (selectedItem != null)
            {
                Settings.WhisperModelSize = selectedItem.Tag.ToString()!;
            }

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

