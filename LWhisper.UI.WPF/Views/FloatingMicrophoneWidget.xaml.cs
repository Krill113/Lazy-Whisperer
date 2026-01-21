using System.Windows;
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

        public event Action? RecordingStarted;
        public event Action? RecordingStopped;

        public FloatingMicrophoneWidget()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Установить состояние виджета
        /// </summary>
        public void SetState(WidgetState state)
        {
            _currentState = state;

            switch (state)
            {
                case WidgetState.Idle:
                    SetIdleState();
                    break;
                case WidgetState.Recording:
                    SetRecordingState();
                    break;
                case WidgetState.Processing:
                    SetProcessingState();
                    break;
            }
        }

        private void SetIdleState()
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(74, 144, 226), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(53, 122, 189), 1));
            MicrophoneButton.Fill = brush;
            MicrophoneButton.BeginAnimation(OpacityProperty, null);
        }

        private void SetRecordingState()
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(231, 76, 60), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(192, 57, 43), 1));
            MicrophoneButton.Fill = brush;

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
        }
    }

    /// <summary>
    /// Состояние виджета
    /// </summary>
    public enum WidgetState
    {
        Idle,
        Recording,
        Processing
    }
}

