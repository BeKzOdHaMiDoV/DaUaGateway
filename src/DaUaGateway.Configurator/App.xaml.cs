using System.Windows;

namespace DaUaGateway.Configurator
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, a) =>
            {
                MessageBox.Show(a.Exception.Message, "Конфигуратор — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };
            base.OnStartup(e);
        }
    }
}
