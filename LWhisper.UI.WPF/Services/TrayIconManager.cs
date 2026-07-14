using System.Windows;
using System.Drawing;
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
                Icon = CreateMicrophoneIcon(),
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

            // Клик по баллону «доступно обновление» открывает Настройки
            _trayIcon.TrayBalloonTipClicked += (s, e) => SettingsRequested?.Invoke();
        }

        /// <summary>
        /// Ненавязчивое уведомление о доступном обновлении (баллон из трея)
        /// </summary>
        public void ShowUpdateNotification(string version)
        {
            _trayIcon?.ShowBalloonTip(
                "LWhisper — доступно обновление",
                $"Вышла версия {version}. Нажмите, чтобы открыть Настройки и обновиться.",
                BalloonIcon.Info);
        }

        private Icon CreateMicrophoneIcon()
        {
            // Создаем простую иконку микрофона программно
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Рисуем микрофон
                using (var brush = new SolidBrush(Color.FromArgb(74, 144, 226)))
                {
                    // Корпус микрофона
                    g.FillEllipse(brush, 10, 6, 12, 16);
                    // Стойка
                    g.FillRectangle(brush, 15, 22, 2, 6);
                    // Основание
                    g.FillRectangle(brush, 12, 27, 8, 2);
                    // Дуга
                    using (var pen = new Pen(brush, 2))
                    {
                        g.DrawArc(pen, 6, 16, 20, 12, 0, -180);
                    }
                }

                return Icon.FromHandle(bitmap.GetHicon());
            }
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

