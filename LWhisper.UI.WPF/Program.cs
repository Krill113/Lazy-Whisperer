using LWhisper.UI.WPF.Services;

namespace LWhisper.UI.WPF
{
    /// <summary>
    /// Собственная точка входа (StartupObject). Updater-режим (--apply-update) обрабатывается
    /// ДО конструирования WPF Application: копия single-file exe в %LOCALAPPDATA%\updater живёт
    /// без loose-нативок WPF (PresentationNative_cor3.dll и др.), и базовый конструктор
    /// Application уронил бы процесс DllNotFoundException раньше любого нашего кода.
    /// В updater-ветке нельзя трогать ни один WPF-тип.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (UpdateApplier.IsApplyUpdateInvocation(args))
            {
                UpdateApplier.RunAndExit(args); // не возвращается
            }

            // App.xaml пуст (нет StartupUri/ресурсов) — генератор не создаёт InitializeComponent,
            // вся инициализация в App.OnStartup
            var app = new App();
            app.Run();
        }
    }
}
