using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace DaUaGateway.Configurator
{
    /// <summary>
    /// Точка входа по схеме из документации TitaniumAS:
    ///   1) главный поток — MTA, в нём ПЕРВЫМ делом Bootstrap.Initialize() (CoInitializeSecurity);
    ///   2) окна WPF запускаются в отдельном потоке STA.
    /// Если инициализация безопасности COM всё же не удалась — работаем дальше с настройками
    /// COM по умолчанию: Конфигуратору (обзор и разовое чтение) обратные вызовы не нужны.
    /// </summary>
    internal static class Program
    {
        public static string ComInitWarning { get; private set; }

        [MTAThread]
        private static void Main()
        {
            try
            {
                TitaniumAS.Opc.Client.Bootstrap.Initialize();
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException;
                ComInitWarning = "Настройка безопасности COM не применена" +
                                 (inner != null ? $" (0x{(inner as ExternalException)?.ErrorCode ?? inner.HResult:X8})" : "") +
                                 ". Обзор тегов будет работать с настройками COM по умолчанию.";
            }

            var ui = new Thread(() =>
            {
                var app = new App();
                app.InitializeComponent();
                app.Run();
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            ui.Join();
        }
    }
}
