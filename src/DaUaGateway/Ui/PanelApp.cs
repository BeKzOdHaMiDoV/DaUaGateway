using System;
using System.Threading;
using System.Windows;

namespace DaUaGateway.Ui
{
    /// <summary>
    /// Режим окна-пульта: небольшое окно с кнопками «Запустить / Остановить»,
    /// сворачивается в трей. Окна WPF работают в отдельном STA-потоке
    /// (главный поток — MTA, он нужен для OPC DA).
    /// </summary>
    public static class PanelApp
    {
        public static int Run()
        {
            using (var mutex = new Mutex(true, "DaUaGateway.Panel", out bool created))
            {
                if (!created)
                {
                    // главный поток — MTA, поэтому сообщение через WinForms
                    System.Windows.Forms.MessageBox.Show("Окно конвертера уже открыто — его значок в правом нижнем углу экрана (трей).",
                        "DaUaGateway", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
                    return 0;
                }

                Exception crash = null;
                var ui = new Thread(() =>
                {
                    try
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/DaUaGateway;component/Ui/Xp.xaml", UriKind.Absolute)
                        });
                        var window = new PanelWindow();
                        window.Show();
                        app.Run();
                    }
                    catch (Exception ex) { crash = ex; }
                });
                ui.SetApartmentState(ApartmentState.STA);
                ui.Start();
                ui.Join();

                if (crash != null)
                {
                    Logging.Log.Error("Окно конвертера завершилось с ошибкой", crash);
                    System.Windows.Forms.MessageBox.Show("Ошибка окна конвертера:\n" + crash.Message, "DaUaGateway",
                        System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                    return 1;
                }
                return 0;
            }
        }
    }
}
