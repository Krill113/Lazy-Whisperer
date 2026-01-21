using System.Windows;
using LWhisper.Core.Models;

namespace LWhisper.UI.WPF.Views
{
    /// <summary>
    /// Окно настроек приложения
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public AppSettings Settings { get; private set; }

        public SettingsWindow(AppSettings currentSettings, List<string> audioDevices)
        {
            InitializeComponent();

            Settings = new AppSettings
            {
                RecordingMode = currentSettings.RecordingMode,
                HotkeyBinding = currentSettings.HotkeyBinding,
                AutoInsertDelaySeconds = currentSettings.AutoInsertDelaySeconds,
                SelectedAudioDevice = currentSettings.SelectedAudioDevice
            };

            LoadSettings();
            LoadAudioDevices(audioDevices);
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

