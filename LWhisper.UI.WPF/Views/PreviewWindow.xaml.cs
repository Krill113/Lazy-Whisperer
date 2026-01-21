using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

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

        public event Action<string>? InsertRequested;

        public PreviewWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Показать текст с авто-вставкой через заданное время
        /// </summary>
        public void ShowWithText(string text, int autoInsertDelaySeconds)
        {
            TextBox.Text = text;
            _userEdited = false;
            _secondsRemaining = autoInsertDelaySeconds;

            if (autoInsertDelaySeconds > 0)
            {
                StartAutoInsertTimer();
            }

            Show();
            Activate();
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
            TimerText.Text = string.Empty;
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

