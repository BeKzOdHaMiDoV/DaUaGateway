using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using DaUaGateway.Logging;

namespace DaUaGateway
{
    internal static class Program
    {
        [MTAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Error("Необработанная ошибка", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error("Необработанная ошибка в задаче", e.Exception);
                e.SetObserved();
            };

            // Русский текст в консоли (иначе на английской Windows вместо букв «???»)
            if (Environment.UserInteractive)
            {
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            }

            // DaUaGateway.exe --list [маска|all] — показать, какие теги DA переведены в UA
            if (args.Length > 0 && args[0].Equals("--list", StringComparison.OrdinalIgnoreCase))
                return TagMapReport.Run(args.Skip(1).ToArray());

            bool console = args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase));
            bool panel = args.Any(a => a.Equals("--panel", StringComparison.OrdinalIgnoreCase));

            // Двойной клик по DaUaGateway.exe: перезапускаем себя БЕЗ чёрного окна консоли
            // в режиме окна-пульта (кнопки «Запустить / Остановить», сворачивание в трей)
            if (Environment.UserInteractive && !console && !panel)
            {
                try
                {
                    var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--panel")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                    });
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Error("Не удалось открыть окно конвертера, запускаю в консоли", ex);
                    console = true;
                }
            }

            // Обязательная инициализация COM-части TitaniumAS (в главном MTA-потоке)
            TitaniumAS.Opc.Client.Bootstrap.Initialize();

            if (panel) return Ui.PanelApp.Run();          // окно-пульт
            if (console || Environment.UserInteractive)    // DaUaGateway.exe --console — чёрное окно, как раньше
                return RunConsole();

            ServiceBase.Run(new GatewayService());          // служба Windows
            return 0;
        }

        private static int RunConsole()
        {
            var host = new GatewayHost();
            try
            {
                host.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось запустить шлюз", ex);
                Console.WriteLine(ex);
                return 1;
            }

            var exit = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; exit.Set(); };
            Console.WriteLine("Шлюз работает. Ctrl+C для остановки.");
            exit.WaitOne();

            host.StopAsync().GetAwaiter().GetResult();
            return 0;
        }
    }
}
