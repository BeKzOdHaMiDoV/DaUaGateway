using System.Windows;
using System.Windows.Input;

namespace DaUaGateway.Configurator
{
    /// <summary>Окно в стиле XP (как в OPC Мониторе).</summary>
    public class XpWindow : Window
    {
        public XpWindow()
        {
            SetResourceReference(StyleProperty, "XpWindow");
            CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (s, e) => SystemCommands.CloseWindow(this)));
            CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (s, e) => SystemCommands.MinimizeWindow(this)));
            CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (s, e) =>
            {
                if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
                else SystemCommands.MaximizeWindow(this);
            }));
        }
    }
}
