using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Управление иконкой в системном трее
    /// </summary>
    public class TrayIconManager : IDisposable
    {
        private TaskbarIcon? _trayIcon;

        public event Action? ShowMicrophoneRequested;
        public event Action? SettingsRequested;
        public event Action? ExitRequested;

        public void Initialize()
        {
            _trayIcon = new TaskbarIcon
            {
                Icon = new System.Drawing.Icon(System.Drawing.SystemIcons.Application, 40, 40),
                ToolTipText = "LWhisper - Голосовой ввод"
            };

            var contextMenu = new System.Windows.Controls.ContextMenu();

            var showItem = new System.Windows.Controls.MenuItem { Header = "Показать микрофон" };
            showItem.Click += (s, e) => ShowMicrophoneRequested?.Invoke();

            var settingsItem = new System.Windows.Controls.MenuItem { Header = "Настройки" };
            settingsItem.Click += (s, e) => SettingsRequested?.Invoke();

            var exitItem = new System.Windows.Controls.MenuItem { Header = "Выход" };
            exitItem.Click += (s, e) => ExitRequested?.Invoke();

            contextMenu.Items.Add(showItem);
            contextMenu.Items.Add(settingsItem);
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            contextMenu.Items.Add(exitItem);

            _trayIcon.ContextMenu = contextMenu;
            _trayIcon.TrayMouseDoubleClick += (s, e) => ShowMicrophoneRequested?.Invoke();
        }

        public void SetIcon(TrayIconState state)
        {
            if (_trayIcon == null) return;

            _trayIcon.ToolTipText = state switch
            {
                TrayIconState.Idle => "LWhisper - Готов",
                TrayIconState.Recording => "LWhisper - Запись...",
                TrayIconState.Processing => "LWhisper - Обработка...",
                _ => "LWhisper"
            };
        }

        public void Dispose()
        {
            _trayIcon?.Dispose();
        }
    }

    public enum TrayIconState
    {
        Idle,
        Recording,
        Processing
    }
}

