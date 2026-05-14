using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LWhisper.UI.WPF.Views
{
    /// <summary>
    /// Плавающий виджет микрофона
    /// </summary>
    public partial class FloatingMicrophoneWidget : Window
    {
        private bool _isDragging;
        private Point _clickPosition;
        private WidgetState _currentState = WidgetState.Idle;
        private bool _isShowTextButtonClick = false;

        public event Action? RecordingStarted;
        public event Action? RecordingStopped;
        public event Action<double, double>? PositionChanged;
        public event Action? ShowTextRequested;
        public event Action? RememberTargetWindow;
        public event Action? MinimizeRequested;
        public event Action? CloseRequested;

        public FloatingMicrophoneWidget()
        {
            InitializeComponent();
            LocationChanged += (s, e) => PositionChanged?.Invoke(Left, Top);
        }

        /// <summary>
        /// Установить состояние виджета
        /// </summary>
        public void SetState(WidgetState state)
        {
            Dispatcher.Invoke(() =>
            {
                _currentState = state;

                switch (state)
                {
                    case WidgetState.Idle:
                        SetIdleState();
                        break;
                    case WidgetState.Initializing:
                        SetInitializingState();
                        break;
                    case WidgetState.Recording:
                        SetRecordingState();
                        break;
                    case WidgetState.Processing:
                        SetProcessingState();
                        break;
                }
            });
        }

        private void SetIdleState()
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(74, 144, 226), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(53, 122, 189), 1));
            MicrophoneButton.Fill = brush;

            // Остановить все анимации
            MicrophoneButton.BeginAnimation(OpacityProperty, null);
            if (MicrophoneIcon.RenderTransform is RotateTransform rotateTransform)
            {
                rotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            }
            MicrophoneIcon.RenderTransform = null;
        }

        private void SetInitializingState()
        {
            // W0: жёлтый статический круг (без анимации) — отличает от Processing (с вращением) и Recording (с pulse)
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(241, 196, 15), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(212, 172, 13), 1));
            MicrophoneButton.Fill = brush;

            // Остановить все анимации
            MicrophoneButton.BeginAnimation(OpacityProperty, null);
            if (MicrophoneIcon.RenderTransform is RotateTransform rotateTransform)
            {
                rotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            }
            MicrophoneIcon.RenderTransform = null;
        }

        private void SetRecordingState()
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(231, 76, 60), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(192, 57, 43), 1));
            MicrophoneButton.Fill = brush;

            // Остановить анимацию вращения, если была
            if (MicrophoneIcon.RenderTransform is RotateTransform rt)
            {
                rt.BeginAnimation(RotateTransform.AngleProperty, null);
            }
            MicrophoneIcon.RenderTransform = null;

            var animation = new DoubleAnimation(1.0, 0.6, TimeSpan.FromMilliseconds(800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            MicrophoneButton.BeginAnimation(OpacityProperty, animation);
        }

        private void SetProcessingState()
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(241, 196, 15), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(243, 156, 18), 1));
            MicrophoneButton.Fill = brush;

            var rotateTransform = new RotateTransform();
            MicrophoneIcon.RenderTransform = rotateTransform;
            MicrophoneIcon.RenderTransformOrigin = new Point(0.5, 0.5);

            var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            rotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            // Запомнить активное окно при наведении мыши (ДО того как виджет получит фокус при клике)
            RememberTargetWindow?.Invoke();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _clickPosition = e.GetPosition(this);
                _isDragging = false;
            }
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPosition = e.GetPosition(this);
                var diff = currentPosition - _clickPosition;

                if (!_isDragging && (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5))
                {
                    _isDragging = true;
                }

                if (_isDragging)
                {
                    Left += diff.X;
                    Top += diff.Y;
                }
            }
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isShowTextButtonClick)
            {
                _isShowTextButtonClick = false;
                _isDragging = false;
                return;
            }
            
            if (e.ChangedButton == MouseButton.Left && !_isDragging)
            {
                HandleClick();
            }
            _isDragging = false;
        }

        private void HandleClick()
        {
            if (_currentState == WidgetState.Idle)
            {
                RecordingStarted?.Invoke();
            }
            else if (_currentState == WidgetState.Recording)
            {
                RecordingStopped?.Invoke();
            }
            // Initializing/Processing — клики игнорируются (юзер ждёт ready)
        }

        private void ShowTextButton_MouseEnter(object sender, MouseEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                border.Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
            }
        }

        private void ShowTextButton_MouseLeave(object sender, MouseEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                border.Background = new SolidColorBrush(Color.FromArgb(136, 0, 0, 0));
            }
        }

        private void ShowTextButton_Click(object sender, MouseButtonEventArgs e)
        {
            _isShowTextButtonClick = true;
            ShowTextRequested?.Invoke();
            e.Handled = true;
        }

        private void MinimizeButton_MouseEnter(object sender, MouseEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                border.Opacity = 1.0;
            }
        }

        private void MinimizeButton_MouseLeave(object sender, MouseEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                border.Opacity = 0.7;
            }
        }

        private void MinimizeButton_Click(object sender, MouseButtonEventArgs e)
        {
            MinimizeRequested?.Invoke();
            e.Handled = true;
        }

        private void CloseButton_MouseEnter(object sender, MouseEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                border.Opacity = 1.0;
                border.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0x39, 0x2B));
            }
        }

        private void CloseButton_MouseLeave(object sender, MouseEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                border.Opacity = 0.7;
                border.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xE7, 0x4C, 0x3C));
            }
        }

        private void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            CloseRequested?.Invoke();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Состояние виджета
    /// </summary>
    public enum WidgetState
    {
        Idle,
        Initializing,
        Recording,
        Processing
    }
}

