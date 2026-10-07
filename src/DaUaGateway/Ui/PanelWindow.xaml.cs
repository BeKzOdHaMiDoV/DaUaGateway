using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace DaUaGateway.Ui
{
    /// <summary>Окно-пульт: Запустить / Остановить, состояние DA-серверов, сворачивание в трей.</summary>
    public partial class PanelWindow : XpWindow
    {
        private readonly EngineController _engine = new EngineController();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly string _baseDir = AppDomain.CurrentDomain.BaseDirectory;
        private WF.NotifyIcon _tray;
        private bool _trayHintShown;
        private bool _exiting;
        private bool _serviceRunning;

        public PanelWindow()
        {
            InitializeComponent();
            CreateTray();

            AutoStartBox.IsChecked = LoadAutoStart();
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();

            StateChanged += (s, e) => { if (WindowState == WindowState.Minimized) HideToTray(); };
            Closing += OnClosing;
            Loaded += async (s, e) =>
            {
                Refresh();
                if (AutoStartBox.IsChecked == true && !_serviceRunning) await StartEngineAsync();
            };
        }

        // ================================================================== запуск / остановка

        private async void OnStartClick(object sender, RoutedEventArgs e) => await StartEngineAsync();

        private async System.Threading.Tasks.Task StartEngineAsync()
        {
            if (_serviceRunning) return;
            StartButton.IsEnabled = false;
            Refresh();
            await _engine.StartAsync();
            Refresh();

            if (_engine.State == EngineState.Failed)
                MessageBox.Show(this, "Не удалось запустить конвертер:\n\n" + _engine.Error +
                                      "\n\nЕсли порт 4840 занят — возможно, конвертер уже работает (как служба Windows или второй копией).",
                    "DaUaGateway", MessageBoxButton.OK, MessageBoxImage.Error);
            else if (_engine.State == EngineState.Running)
                _tray.ShowBalloonTip(4000, "Конвертер запущен", "Окно можно свернуть — конвертер продолжит работу в трее.", WF.ToolTipIcon.Info);
        }

        private async void OnStopClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Остановить конвертер?\nSCADA перестанет получать данные.", "DaUaGateway",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            StopButton.IsEnabled = false;
            await _engine.StopAsync();
            Refresh();
        }

        // ================================================================== состояние

        private void Refresh()
        {
            _serviceRunning = IsServiceRunning();
            var state = _engine.State;

            if (_serviceRunning && state != EngineState.Running)
            {
                SetLamp(Color.FromRgb(0x2F, 0xB3, 0x4F), "Работает как служба Windows");
                HintText.Text = "Конвертер установлен службой и работает в фоне. Управлять им здесь не нужно — " +
                                "останавливается через «Службы» Windows (services.msc).";
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                EndpointText.Text = "opc.tcp://" + Environment.MachineName + ":4840/DaUaGateway";
                TotalsText.Text = "см. Конфигуратор";
                SourcesList.Items.Clear();
                UpdateTray("DaUaGateway: работает как служба");
                return;
            }

            switch (state)
            {
                case EngineState.Running:
                    SetLamp(Color.FromRgb(0x2F, 0xB3, 0x4F), "Работает");
                    HintText.Text = "Конвертер переводит выбранные теги в OPC UA. Окно можно свернуть — работа продолжится в трее.";
                    break;
                case EngineState.Starting:
                    SetLamp(Color.FromRgb(0xE5, 0x97, 0x00), "Запуск…");
                    HintText.Text = "Конвертер запускается.";
                    break;
                case EngineState.Stopping:
                    SetLamp(Color.FromRgb(0xE5, 0x97, 0x00), "Остановка…");
                    HintText.Text = "";
                    break;
                case EngineState.Failed:
                    SetLamp(Color.FromRgb(0xC8, 0x40, 0x2A), "Ошибка запуска");
                    HintText.Text = _engine.Error ?? "";
                    break;
                default:
                    SetLamp(Color.FromRgb(0x9C, 0x9A, 0x8F), "Остановлен");
                    HintText.Text = "Нажмите «Запустить», чтобы конвертер начал переводить выбранные теги DA в OPC UA.";
                    break;
            }

            StartButton.IsEnabled = state == EngineState.Stopped || state == EngineState.Failed;
            StopButton.IsEnabled = state == EngineState.Running;

            var host = _engine.Host;
            if (host == null)
            {
                EndpointText.Text = "—";
                TotalsText.Text = "—";
                SourcesList.Items.Clear();
                UpdateTray("DaUaGateway: " + StateText.Text.ToLowerInvariant());
                return;
            }

            EndpointText.Text = host.Endpoint;
            var (lines, sources, tags) = host.Snapshot();
            TotalsText.Text = $"DA-серверов: {sources}, тегов: {tags}";

            SourcesList.Items.Clear();
            if (lines.Count == 0) SourcesList.Items.Add("DA-серверы ещё не найдены…");
            foreach (var l in lines) SourcesList.Items.Add(l);

            UpdateTray($"DaUaGateway: работает, тегов {tags}");
        }

        private void SetLamp(Color c, string text)
        {
            Lamp.Fill = new SolidColorBrush(c);
            StateText.Text = text;
        }

        private static bool IsServiceRunning()
        {
            try
            {
                using (var sc = new ServiceController("DaUaGateway"))
                    return sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending;
            }
            catch (InvalidOperationException) { return false; } // служба не установлена
            catch { return false; }
        }

        // ================================================================== кнопки

        private void OnConfiguratorClick(object sender, RoutedEventArgs e)
        {
            var exe = Path.Combine(_baseDir, "Configurator", "DaUaGateway.Configurator.exe");
            if (!File.Exists(exe)) exe = Path.Combine(_baseDir, "DaUaGateway.Configurator.exe");
            if (!File.Exists(exe))
            {
                MessageBox.Show(this, "Конфигуратор не найден в папке конвертера.", "DaUaGateway");
                return;
            }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        }

        private void OnLogsClick(object sender, RoutedEventArgs e)
        {
            var dir = _engine.Host?.LogsDir ?? Path.Combine(_baseDir, "logs");
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        private void OnHideClick(object sender, RoutedEventArgs e) => HideToTray();

        private void OnAutoStartChanged(object sender, RoutedEventArgs e) => SaveAutoStart(AutoStartBox.IsChecked == true);

        // ================================================================== трей

        private void CreateTray()
        {
            _tray = new WF.NotifyIcon { Icon = CreateIcon(), Text = "DaUaGateway", Visible = true };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Открыть окно конвертера", null, (s, e) => ShowFromTray());
            menu.Items.Add("Конфигуратор", null, (s, e) => OnConfiguratorClick(null, null));
            menu.Items.Add("Папка журналов", null, (s, e) => OnLogsClick(null, null));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, e) => ExitFromTray());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ShowFromTray();
            _tray.BalloonTipClicked += (s, e) => ShowFromTray();
        }

        private void UpdateTray(string text) => _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;

        private void HideToTray()
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(4000, "DaUaGateway в трее",
                    _engine.State == EngineState.Running
                        ? "Конвертер продолжает работать. Двойной клик по значку — открыть окно."
                        : "Двойной клик по значку — открыть окно.",
                    WF.ToolTipIcon.Info);
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private async void ExitFromTray()
        {
            ShowFromTray();
            if (_engine.State == EngineState.Running &&
                MessageBox.Show(this, "Остановить конвертер и выйти?\nSCADA перестанет получать данные.", "DaUaGateway",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _engine.StopAsync();
            _exiting = true;
            Close();
        }

        /// <summary>Крестик: если конвертер работает — свернуть в трей или остановить и выйти.</summary>
        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (_exiting) { Shutdown(); return; }

            if (_engine.State == EngineState.Running)
            {
                var r = MessageBox.Show(this,
                    "Конвертер работает.\n\nДа — свернуть в трей (конвертер продолжит работу)\nНет — остановить конвертер и выйти\nОтмена — вернуться",
                    "DaUaGateway", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                e.Cancel = true;
                if (r == MessageBoxResult.Yes) { HideToTray(); return; }
                if (r == MessageBoxResult.No)
                {
                    await _engine.StopAsync();
                    _exiting = true;
                    Close();
                }
                return;
            }
            Shutdown();
        }

        private void Shutdown()
        {
            _timer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            Application.Current.Shutdown();
        }

        // ================================================================== настройки окна

        private string SettingsPath => Path.Combine(_baseDir, "data", "panel.json");

        private bool LoadAutoStart()
        {
            try { return File.Exists(SettingsPath) && ((bool?)JObject.Parse(File.ReadAllText(SettingsPath))["autoStart"] ?? false); }
            catch { return false; }
        }

        private void SaveAutoStart(bool value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, new JObject { ["autoStart"] = value }.ToString());
            }
            catch { }
        }

        private static SD.Icon CreateIcon()
        {
            using (var bmp = new SD.Bitmap(32, 32))
            {
                using (var g = SD.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(SD.Color.Transparent);
                    using (var fill = new SD.SolidBrush(SD.Color.FromArgb(0x24, 0x5E, 0xDB)))
                        g.FillRectangle(fill, 1, 1, 30, 30);
                    using (var pen = new SD.Pen(SD.Color.White, 3f))
                        g.DrawLines(pen, new[]
                        {
                            new SD.Point(4, 17), new SD.Point(10, 17), new SD.Point(13, 7),
                            new SD.Point(19, 26), new SD.Point(22, 17), new SD.Point(28, 17)
                        });
                }
                return SD.Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
