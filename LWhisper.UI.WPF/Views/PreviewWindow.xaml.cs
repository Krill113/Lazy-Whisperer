using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace LWhisper.UI.WPF.Views
{
    /// <summary>
    /// Окно предпросмотра распознанного текста
    /// </summary>
    public partial class PreviewWindow : Window
    {
        private DispatcherTimer? _autoInsertTimer;
        private int _secondsRemaining;
        private bool _userEdited;
        private bool _autoInsertEnabled = true;

        public event Action<string>? InsertRequested;
        public event Action<bool>? AutoInsertSettingChanged;
        public event Action? SettingsRequested;

        public PreviewWindow()
        {
            InitializeComponent();
            Opacity = 0; // Начальная прозрачность для анимации
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Запустить анимацию появления
            var storyboard = (Storyboard)FindResource("FadeInAnimation");
            storyboard.Begin(this);
        }

        /// <summary>
        /// Показать текст с авто-вставкой через заданное время
        /// </summary>
        /// <param name="text">Текст для отображения</param>
        /// <param name="autoInsertDelaySeconds">Задержка перед автовставкой</param>
        /// <param name="autoInsertEnabled">Состояние чекбокса автовставки</param>
        /// <param name="startTimer">Запустить ли таймер сразу</param>
        public void ShowWithText(string text, int autoInsertDelaySeconds, bool autoInsertEnabled = true, bool startTimer = true)
        {
            TextBox.Text = text;
            _userEdited = false;
            _secondsRemaining = autoInsertDelaySeconds;
            _autoInsertEnabled = autoInsertEnabled;
            
            // Установить состояние чекбокса согласно настройкам
            AutoInsertCheckBox.IsChecked = autoInsertEnabled;

            // Запустить таймер только если требуется И автовставка включена
            if (startTimer && autoInsertEnabled && autoInsertDelaySeconds > 0)
            {
                StartAutoInsertTimer();
            }

            Show();
            Activate();
        }

        /// <summary>
        /// Обновить текст в уже открытом окне
        /// </summary>
        /// <param name="text">Новый текст</param>
        /// <param name="startTimer">Запустить ли таймер после обновления</param>
        public void UpdateText(string text, bool startTimer = false)
        {
            TextBox.Text = text;
            _userEdited = false;
            
            // Запустить таймер только если требуется И автовставка включена
            if (startTimer && _autoInsertEnabled && _secondsRemaining > 0)
            {
                // Перезапустить таймер при необходимости
                StopAutoInsertTimer();
                StartAutoInsertTimer();
            }
        }

        private void StartAutoInsertTimer()
        {
            _autoInsertTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _autoInsertTimer.Tick += AutoInsertTimer_Tick;
            _autoInsertTimer.Start();
            UpdateTimerText();
        }

        private void StopAutoInsertTimer()
        {
            _autoInsertTimer?.Stop();
            _autoInsertTimer = null;
            
            // Проверка на null для безопасности при инициализации
            if (TimerText != null)
            {
                TimerText.Text = string.Empty;
            }
        }

        private void AutoInsertTimer_Tick(object? sender, EventArgs e)
        {
            if (_userEdited)
            {
                StopAutoInsertTimer();
                return;
            }

            _secondsRemaining--;

            if (_secondsRemaining <= 0)
            {
                StopAutoInsertTimer();
                PerformInsert();
            }
            else
            {
                UpdateTimerText();
            }
        }

        private void UpdateTimerText()
        {
            TimerText.Text = $"Автовставка через {_secondsRemaining} сек";
        }

        private void TextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (IsLoaded && TextBox.IsFocused)
            {
                _userEdited = true;
                StopAutoInsertTimer();
            }
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                PerformInsert();
                e.Handled = true;
            }
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(TextBox.Text);
        }

        private void InsertButton_Click(object sender, RoutedEventArgs e)
        {
            PerformInsert();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsRequested?.Invoke();
        }

        private void AutoInsertCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // Игнорировать события до полной загрузки окна
            if (!IsLoaded) return;
            
            _autoInsertEnabled = AutoInsertCheckBox.IsChecked == true;
            
            if (_autoInsertEnabled && !string.IsNullOrEmpty(TextBox?.Text) && _secondsRemaining > 0)
            {
                // Включена - запустить таймер
                if (_autoInsertTimer == null)
                {
                    StartAutoInsertTimer();
                }
            }
            else
            {
                // Выключена - остановить таймер
                StopAutoInsertTimer();
            }
            
            // Уведомить об изменении настройки
            AutoInsertSettingChanged?.Invoke(_autoInsertEnabled);
        }

        private void PerformInsert()
        {
            StopAutoInsertTimer();
            InsertRequested?.Invoke(TextBox.Text);
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            StopAutoInsertTimer();
            base.OnClosed(e);
        }
    }
}

