using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DaUaGateway.Configurator.Model;
using DaUaGateway.Configurator.Services;
using Newtonsoft.Json.Linq;
using Opc.Ua;
using Shapes = System.Windows.Shapes;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace DaUaGateway.Configurator
{
    public partial class MainWindow : XpWindow
    {
        /// <summary>Что лежит в TreeViewItem.Tag.</summary>
        private sealed class Node
        {
            public DaElement Element;
            public string[] ParentPath;
            public string[] OwnPath;
            public CheckBox Check;
            public TreeViewItem Item;
            public bool? PrevState;       // для клика по «частично выбранной» папке
            public bool Loaded;
            public bool IsFolder => Element.HasChildren;
        }

        private const string Placeholder = "загрузка…";

        private readonly DaService _da = new DaService();
        private readonly UaMonitor _ua = new UaMonitor();
        private readonly ObservableCollection<UaRow> _rows = new ObservableCollection<UaRow>();
        private readonly Dictionary<NodeId, UaRow> _rowById = new Dictionary<NodeId, UaRow>();
        private readonly ObservableCollection<AttrRow> _attrs = new ObservableCollection<AttrRow>();
        private readonly ObservableCollection<LogRow> _log = new ObservableCollection<LogRow>();
        private readonly DispatcherTimer _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        private string _gatewayDir;
        private string _pkiDir;
        private static readonly string AppDataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DaUaGateway Configurator");
        private string _selectionPath;
        private string _tagMapDir;
        private string _endpoint = "opc.tcp://localhost:4840/DaUaGateway";
        private JObject _gatewayConfig;
        private LogTail _logTail;
        private SelectionFile _selection = new SelectionFile();
        private string _progId;
        private bool _dirty;
        private bool _updating;
        private int _loadToken;
        private int _tableToken;

        public MainWindow()
        {
            InitializeComponent();
            ValuesGrid.ItemsSource = _rows;
            AttrGrid.ItemsSource = _attrs;
            LogGrid.ItemsSource = _log;
            RestoreLayout();
            Loaded += async (s, e) => await InitAsync();
            Closing += OnClosing;
        }

        private SelectionEntry Entry => _progId == null ? null : _selection.GetOrCreate(_progId);

        // ================================================================== запуск

        private async Task InitAsync()
        {
            var dir = FindGatewayDir();
            if (dir == null) { Close(); return; }
            SetGatewayDir(dir);

            AddLog("Конфигуратор", "Запущен. Папка конвертера: " + _gatewayDir);
            if (LooksLikeTempFolder(_gatewayDir))
                AddLog("Конфигуратор", "Программа запущена из АРХИВА или временной папки. Распакуйте архив и запустите Конфигуратор из папки конвертера — иначе выбор не дойдёт до конвертера.", "ERROR");
            if (Program.ComInitWarning != null) AddLog("Конфигуратор", Program.ComInitWarning, "WARN");

            try { _selection = SelectionFile.Load(_selectionPath); }
            catch (Exception ex) { AddLog("Конфигуратор", "Файл выбора повреждён, будет создан заново: " + ex.Message, "WARN"); }

            // журнал конвертера
            _logTimer.Tick += (s, e) => PumpConverterLog();
            PumpConverterLog();
            _logTimer.Start();

            // OPC UA: живые значения с выхода конвертера
            _ua.StateChanged += (ok, text) => Dispatcher.BeginInvoke(new Action(() => OnUaState(ok, text)));
            _ua.ModelVersionChanged += v => Dispatcher.BeginInvoke(new Action(() => OnModelVersion(v)));
            _ua.ValueChanged += (id, dv) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_rowById.TryGetValue(id, out var row)) row.Apply(dv);
            }));

            try
            {
                _pkiDir = Path.Combine(AppDataDir, "pki");
                await _ua.InitAsync(_pkiDir);
                try { _ua.TrustSelfInGateway(_pkiDir, _gatewayDir); }
                catch (Exception ex) { AddLog("Конфигуратор", "Не удалось добавить сертификат Конфигуратора в доверенные у конвертера: " + ex.Message, "WARN"); }
                _ua.Start(_endpoint);
            }
            catch (Exception ex)
            {
                AddLog("Конфигуратор", "OPC UA клиент не запустился: " + ex.Message, "ERROR");
            }

            SetGatewayLamp(null, "Конвертер: проверка…");
            await RefreshServersAsync();
            UpdateCounters();
        }

        /// <summary>Папка конвертера, с которым работаем: его gateway.json, файл выбора, список тегов, журнал.</summary>
        private void SetGatewayDir(string dir)
        {
            _gatewayDir = dir.TrimEnd('\\');
            var dataDir = "data";
            try
            {
                _gatewayConfig = JObject.Parse(File.ReadAllText(Path.Combine(_gatewayDir, "gateway.json")));
                dataDir = (string)_gatewayConfig["dataDir"] ?? "data";
                _endpoint = (string)_gatewayConfig["ua"]?["endpoint"] ?? _endpoint;
            }
            catch (Exception ex) { AddLog("Конфигуратор", "Не удалось прочитать gateway.json: " + ex.Message, "WARN"); }

            _selectionPath = Path.Combine(_gatewayDir, dataDir, "selection.json");
            _tagMapDir = Path.Combine(_gatewayDir, dataDir, "tag-map");
            _logTail = new LogTail(Path.Combine(_gatewayDir, "logs"));
            FolderText.Text = "Папка конвертера: " + _gatewayDir;
        }

        private string FindGatewayDir()
        {
            // Конфигуратор лежит в подпапке Configurator конвертера (со своими библиотеками)
            // или прямо рядом с DaUaGateway.exe
            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (File.Exists(Path.Combine(baseDir, "gateway.json"))) return baseDir;
            var parent = Directory.GetParent(baseDir)?.FullName;
            if (parent != null && File.Exists(Path.Combine(parent, "gateway.json"))) return parent;

            MessageBox.Show(this, "Конфигуратор должен лежать в папке конвертера (в подпапке Configurator или рядом с DaUaGateway.exe).\n" +
                                  "Укажите файл gateway.json конвертера.", "Конфигуратор", MessageBoxButton.OK, MessageBoxImage.Information);
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Настройки конвертера|gateway.json", Title = "Где лежит gateway.json?" };
            return dlg.ShowDialog(this) == true ? Path.GetDirectoryName(dlg.FileName) : null;
        }

        private static bool LooksLikeTempFolder(string dir)
        {
            var temp = Path.GetTempPath().TrimEnd('\\');
            return dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                   dir.IndexOf(@"\Temp\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   dir.IndexOf("Rar$", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   dir.IndexOf("7z", StringComparison.OrdinalIgnoreCase) >= 0 && dir.IndexOf(@"\Temp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ================================================================== состояние конвертера

        private void OnUaState(bool connected, string text)
        {
            if (!connected)
            {
                SetGatewayLamp(false, "Конвертер не запущен");
                AddLog("OPC UA", text, "WARN");
                ClearTable("Конвертер не запущен (или не отвечает на " + _endpoint + ").\nВыбор можно делать и сохранять — конвертер применит его при запуске.");
                return;
            }

            AddLog("OPC UA", text);
            var install = _ua.GatewayInstallDir;
            if (install != null && !SameDir(install, _gatewayDir))
            {
                if (File.Exists(Path.Combine(install, "gateway.json")))
                {
                    // переключаемся на работающий конвертер — выбор должен попасть именно ему
                    var old = _gatewayDir;
                    SetGatewayDir(install);
                    if (!_dirty)
                    {
                        try { _selection = SelectionFile.Load(_selectionPath); }
                        catch (Exception ex) { AddLog("Конфигуратор", "Файл выбора работающего конвертера повреждён: " + ex.Message, "WARN"); }
                    }
                    else
                    {
                        AddLog("Конфигуратор", "Несохранённые изменения будут сохранены для работающего конвертера при «Применить».", "WARN");
                    }
                    try { if (_pkiDir != null) _ua.TrustSelfInGateway(_pkiDir, _gatewayDir); } catch { }

                    AddLog("Конфигуратор", "Работает конвертер из другой папки: " + _gatewayDir +
                           ". Конфигуратор переключился на него — выбор сохраняется туда (запущен был из " + old + ").", "WARN");
                    SetGatewayLamp(true, "Конвертер работает" + (string.IsNullOrEmpty(_ua.GatewayVersion) ? "" : " (" + _ua.GatewayVersion + ")"));
                    if (_progId != null) SetAutoAllBox(Entry.AutoAll);
                    UpdateServerLabels();
                    RefreshTree(Tree);
                    UpdateCounters();
                }
                else
                {
                    SetGatewayLamp(null, "Работает конвертер из ДРУГОЙ папки");
                    AddLog("Конфигуратор", "На порту 4840 работает конвертер из папки " + install +
                           ", но к ней нет доступа. Запустите Конфигуратор из папки работающего конвертера.", "ERROR");
                }
            }
            else
            {
                SetGatewayLamp(true, "Конвертер работает" + (string.IsNullOrEmpty(_ua.GatewayVersion) ? "" : " (" + _ua.GatewayVersion + ")"));
            }
            _ = RefreshTableAsync();
        }

        private static bool SameDir(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private void SetGatewayLamp(bool? ok, string text)
        {
            GatewayLamp.Fill = new SolidColorBrush(ok == true ? Color.FromRgb(0x2F, 0xB3, 0x4F)
                                                 : ok == false ? Color.FromRgb(0xC8, 0x40, 0x2A)
                                                 : Color.FromRgb(0xE5, 0x97, 0x00));
            GatewayStateText.Text = text;
        }

        private void OnModelVersion(uint version)
        {
            AddLog("OPC UA", "Конвертер обновил набор тегов (версия дерева " + version + ")");
            _ = RefreshTableAsync();
            UpdateCounters();
        }

        private void PumpConverterLog()
        {
            foreach (var line in _logTail.ReadNew())
            {
                // формат: 2026-10-02 09:53:01.372 [INFO ] сообщение
                var time = line.Length > 23 ? line.Substring(11, 12) : "";
                var level = line.Contains("[ERROR]") ? "ERROR" : line.Contains("[WARN ]") ? "WARN" : "INFO";
                var msgStart = line.IndexOf("] ", StringComparison.Ordinal);
                var msg = msgStart > 0 ? line.Substring(msgStart + 2) : line;
                AddLog("Конвертер", msg, level, time);
            }
        }

        private void AddLog(string source, string message, string level = "INFO", string time = null)
        {
            _log.Add(new LogRow(time ?? DateTime.Now.ToString("HH:mm:ss.fff"), source, message, level));
            while (_log.Count > 1000) _log.RemoveAt(0);
            if (_log.Count > 0) LogGrid.ScrollIntoView(_log[_log.Count - 1]);
            if (level != "INFO") StatusText.Text = message;
        }

        // ================================================================== центральная таблица (OPC UA)

        private async Task RefreshTableAsync()
        {
            int token = ++_tableToken;
            if (_progId == null) { ClearTable("Выберите DA-сервер слева"); return; }
            if (!_ua.Connected) { ClearTable("Конвертер не запущен — выбор сохранится и применится при его запуске."); return; }

            List<UaVariable> vars;
            try { vars = await _ua.BrowseSourceAsync(_progId); }
            catch (Exception ex) { AddLog("OPC UA", "Ошибка обзора: " + ex.Message, "WARN"); return; }
            if (token != _tableToken) return;

            var keep = new Dictionary<NodeId, UaRow>(_rowById);
            _rows.Clear();
            _rowById.Clear();
            int i = 0;
            foreach (var v in vars)
            {
                if (!keep.TryGetValue(v.NodeId, out var row)) row = new UaRow(v.NodeId, _progId, v.Path, v.Name);
                row.Index = ++i;
                _rows.Add(row);
                _rowById[v.NodeId] = row;
            }

            int tags = vars.Count(v => !v.Name.StartsWith("_"));
            TableCountText.Text = "тегов: " + tags;
            TableEmptyText.Text = tags == 0
                ? "Теги этого DA-сервера не выбраны — конвертер к нему не подключается.\nОтметьте теги или папки в дереве слева и нажмите «Применить»."
                : "";
            TableEmptyText.Visibility = tags == 0 ? Visibility.Visible : Visibility.Collapsed;

            await _ua.SetMonitoredAsync(_rowById.Keys.ToList());
        }

        private void ClearTable(string message)
        {
            _rows.Clear();
            _rowById.Clear();
            _ = _ua.SetMonitoredAsync(new NodeId[0]);
            TableCountText.Text = "";
            TableEmptyText.Text = message;
            TableEmptyText.Visibility = Visibility.Visible;
        }

        private void OnRowSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!(ValuesGrid.SelectedItem is UaRow row)) return;
            var dv = row.Last;
            var id = row.NodeIdText;
            var bar = row.NodeId.Identifier as string;
            var daItem = bar != null && bar.Contains("|") ? bar.Substring(bar.IndexOf('|') + 1) : "";

            ShowAttrs(
                ("Узел", "Переменная OPC UA"),
                ("NodeId", id),
                ("Имя", row.Name),
                ("Путь в UA", "Objects / Servers / " + _progId + (row.Path.Length > 0 ? " / " + row.Path : "")),
                ("Тег в DA", daItem),
                ("Значение", dv == null ? "ожидание" : UaRow.Format(dv.Value)),
                ("Тип", row.TypeText),
                ("Статус", row.StatusText),
                ("Время источника", row.SourceTime),
                ("Время сервера", row.ServerTime),
                ("Права в UA", WriteAllowed() ? "чтение и запись (если тег разрешает)" : "только чтение (запись в конвертере выключена)"));
        }

        private void ShowAttrs(params (string name, string value)[] items)
        {
            _attrs.Clear();
            foreach (var (n, v) in items) _attrs.Add(new AttrRow(n, v ?? ""));
        }

        private bool WriteAllowed()
        {
            try
            {
                var rule = (_gatewayConfig?["servers"] as JArray)?
                    .FirstOrDefault(s => string.Equals((string)s["progId"], _progId, StringComparison.OrdinalIgnoreCase));
                var ruleValue = (bool?)rule?["allowWrite"];
                return ruleValue ?? (bool?)_gatewayConfig?["defaults"]?["allowWrite"] ?? false;
            }
            catch { return false; }
        }

        // ================================================================== DA-серверы

        private async Task RefreshServersAsync()
        {
            AddLog("Конфигуратор", "Поиск DA-серверов на компьютере…");
            List<string> servers;
            try { servers = await Task.Run(() => DaService.EnumerateServers()); }
            catch (Exception ex) { AddLog("Конфигуратор", "Не удалось получить список DA-серверов: " + ex.Message, "ERROR"); return; }

            foreach (var key in _selection.Servers.Keys)
                if (!servers.Contains(key, StringComparer.OrdinalIgnoreCase)) servers.Add(key);

            var current = _progId;
            ServerList.Items.Clear();
            foreach (var p in servers) ServerList.Items.Add(new ListBoxItem { Tag = p, Content = ServerLabel(p) });
            AddLog("Конфигуратор", servers.Count == 0 ? "DA-серверы не найдены" : "Найдено DA-серверов: " + servers.Count);

            if (current != null)
                foreach (ListBoxItem li in ServerList.Items)
                    if (string.Equals((string)li.Tag, current, StringComparison.OrdinalIgnoreCase)) { li.IsSelected = true; break; }
        }

        private string ServerLabel(string progId)
        {
            if (!_selection.Servers.TryGetValue(progId, out var e) || e.IsEmpty) return progId + "   — ничего не выбрано";
            if (e.AutoAll) return progId + "   — все теги";
            return progId + $"   — {e.Items.Count} тегов, {e.Folders.Count} папок" + (e.ExcludedCount > 0 ? $", исключено {e.ExcludedCount}" : "");
        }

        private void UpdateServerLabels()
        {
            foreach (ListBoxItem li in ServerList.Items) li.Content = ServerLabel((string)li.Tag);
        }

        private async void OnRefreshServersClick(object sender, RoutedEventArgs e) => await RefreshServersAsync();

        private async void OnServerSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!(ServerList.SelectedItem is ListBoxItem li)) return;
            var progId = (string)li.Tag;
            if (progId == _progId && Tree.Items.Count > 0) return;
            int token = ++_loadToken;

            _progId = progId;
            Tree.Items.Clear();
            _attrs.Clear();
            AutoAllBox.IsEnabled = false;
            SetAutoAllBox(Entry.AutoAll);
            UpdateCounters();
            _ = RefreshTableAsync();

            AddLog("DA", "Подключение к DA-серверу " + progId + "…");
            try
            {
                await Task.Run(() => _da.Connect(progId));
                var roots = await Task.Run(() => _da.Browse(""));
                if (token != _loadToken) return;

                foreach (var el in roots) Tree.Items.Add(MakeNode(el, new string[0]));
                AutoAllBox.IsEnabled = true;
                AddLog("DA", "Подключено к " + progId + ". Отметьте нужные теги и папки и нажмите «Применить».");
            }
            catch (Exception ex)
            {
                if (token != _loadToken) return;
                AutoAllBox.IsEnabled = true;
                AddLog("DA", "Не удалось подключиться к " + progId + ": " + ex.Message, "ERROR");
            }
        }

        // ================================================================== дерево DA

        private TreeViewItem MakeNode(DaElement el, string[] parentPath)
        {
            var node = new Node
            {
                Element = el,
                ParentPath = parentPath,
                OwnPath = el.HasChildren ? parentPath.Concat(new[] { el.Name }).ToArray() : parentPath
            };

            node.Check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            node.Check.Checked += (s, a) => OnNodeChecked(node, true);
            node.Check.Unchecked += (s, a) => OnNodeChecked(node, false);
            System.Windows.Automation.AutomationProperties.SetName(node.Check, (node.IsFolder ? "Папка " : "Тег ") + el.Name);

            var header = new StackPanel { Orientation = WpfOrientation.Horizontal };
            header.Children.Add(node.Check);
            header.Children.Add(node.IsFolder ? FolderIcon() : TagIcon());
            header.Children.Add(new TextBlock { Text = el.Name, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

            var item = new TreeViewItem { Header = header, Tag = node };
            node.Item = item;
            if (node.IsFolder)
            {
                item.Items.Add(Placeholder);
                item.Expanded += async (s, a) =>
                {
                    if (!ReferenceEquals(a.OriginalSource, item)) return;
                    await ExpandAsync(item);
                };
            }
            ApplyState(item);
            return item;
        }

        private async Task ExpandAsync(TreeViewItem item)
        {
            var node = (Node)item.Tag;
            if (node.Loaded) return;
            node.Loaded = true;
            try
            {
                var parentId = string.IsNullOrEmpty(node.Element.ItemId) ? node.Element.Name : node.Element.ItemId;
                var children = await Task.Run(() => _da.Browse(parentId));
                item.Items.Clear();
                foreach (var c in children) item.Items.Add(MakeNode(c, node.OwnPath));
                if (children.Count == 0) item.Items.Add(new TextBlock { Text = "(пусто)", Foreground = Brushes.Gray });
            }
            catch (Exception ex)
            {
                node.Loaded = false;
                AddLog("DA", "Ошибка чтения папки: " + ex.Message, "WARN");
            }
        }

        private void ApplyState(TreeViewItem item)
        {
            var node = (Node)item.Tag;
            var e = Entry;
            _updating = true;
            try
            {
                // все галочки доступны всегда: внутри выбранной папки можно снять отдельные теги/подпапки
                node.Check.IsEnabled = true;
                bool? state;
                if (node.IsFolder)
                    state = e.FolderPartial(node.OwnPath) ? (bool?)null : e.FolderSelected(node.OwnPath);
                else
                    state = e.ItemSelected(node.Element.ItemId, node.ParentPath);
                node.Check.IsChecked = state;
                node.PrevState = state;
            }
            finally { _updating = false; }
        }

        private void RefreshTree(ItemsControl parent)
        {
            foreach (var child in parent.Items.OfType<TreeViewItem>())
            {
                ApplyState(child);
                RefreshTree(child);
            }
        }

        private void OnNodeChecked(Node node, bool on)
        {
            if (_updating || _progId == null) return;
            var e = Entry;
            if (node.PrevState == null) on = true; // клик по «частично» — выбрать папку целиком
            if (node.IsFolder) e.SetFolder(node.OwnPath, on);
            else if (!string.IsNullOrEmpty(node.Element.ItemId)) e.SetItem(node.Element.ItemId, node.ParentPath, node.Element.Name, on);
            MarkDirty();
            RefreshTree(Tree);

            // показать в атрибутах именно этот тег/папку с актуальным «Выбран»
            if (node.Item.IsSelected) _ = ShowNodeAttrsAsync(node.Item);
            else node.Item.IsSelected = true;
        }

        private void OnAutoAllChanged(object sender, RoutedEventArgs e)
        {
            if (_updating || _progId == null) return;
            Entry.AutoAll = AutoAllBox.IsChecked == true;
            MarkDirty();
            RefreshTree(Tree);
        }

        private void SetAutoAllBox(bool value)
        {
            _updating = true;
            AutoAllBox.IsChecked = value;
            _updating = false;
        }

        private async void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewItem item) await ShowNodeAttrsAsync(item);
        }

        private async Task ShowNodeAttrsAsync(TreeViewItem item)
        {
            if (!(item.Tag is Node node) || _progId == null) return;
            var entry = Entry;

            if (node.IsFolder)
            {
                ShowAttrs(
                    ("Узел", "Папка DA-сервера"),
                    ("Путь в DA", string.Join(" / ", node.OwnPath)),
                    ("Выбрана", entry.FolderPartial(node.OwnPath)
                              ? "частично — внутри есть снятые или отдельно отмеченные теги"
                              : entry.FolderSelected(node.OwnPath) ? "да — все теги внутри, включая новые" : "нет"),
                    ("В UA будет", "Objects / Servers / " + _progId + " / " + string.Join(" / ", node.OwnPath)));
                return;
            }

            var id = node.Element.ItemId;
            var uaId = _ua.NsIndex > 0 ? new NodeId(_progId + "|" + id, _ua.NsIndex) : null;
            bool converted = uaId != null && _rowById.ContainsKey(uaId);
            bool selected = entry.ItemSelected(id, node.ParentPath);

            ShowAttrs(("Узел", "Тег DA"), ("Тег в DA", id), ("Значение (DA)", "чтение…"));
            var r = await Task.Run(() => _da.Read(id));
            if (!(Tree.SelectedItem is TreeViewItem cur) || !ReferenceEquals(cur, item)) return;

            var daRights = AccessText(r.Access);
            var uaRights = WriteAllowed() && r.Access != null && r.Access.Contains("Write")
                ? "чтение и запись"
                : "только чтение" + (WriteAllowed() ? "" : " (запись в конвертере выключена)");

            ShowAttrs(
                ("Узел", "Тег DA"),
                ("Тег в DA", id),
                ("Имя", node.Element.Name),
                ("Путь в DA", string.Join(" / ", node.ParentPath)),
                ("Значение (DA)", r.Error != null && r.Value == null ? r.Error : UaRow.Format(r.Value)),
                ("Качество (DA)", r.Quality),
                ("Время (DA)", r.Timestamp == default(DateTimeOffset) ? "" : r.Timestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss")),
                ("Тип", r.Type),
                ("Права в DA", daRights),
                ("Права в UA", uaRights),
                ("NodeId в UA", "ns=" + (_ua.NsIndex > 0 ? _ua.NsIndex : 2) + ";s=" + _progId + "|" + id),
                ("Выбран", selected ? "да" : "нет"),
                ("Переводится сейчас", converted ? "да — см. таблицу в центре" : selected ? "ещё нет — нажмите «Применить»" : "нет"));
        }

        private static string AccessText(string access)
        {
            switch (access)
            {
                case "Read": return "только чтение";
                case "Write": return "только запись";
                case "ReadWrite": return "чтение и запись";
                default: return access ?? "";
            }
        }

        // ================================================================== счётчики и применение

        private void MarkDirty()
        {
            _dirty = true;
            UpdateCounters();
            UpdateServerLabels();
            StatusText.Text = "Есть несохранённые изменения — нажмите «Применить».";
        }

        private void UpdateCounters()
        {
            var e = _progId == null ? null : Entry;
            SelectedText.Text = e == null ? ""
                : e.AutoAll ? "Выбрано: все теги сервера"
                : $"Выбрано: тегов {e.Items.Count}, папок {e.Folders.Count}" + (e.ExcludedCount > 0 ? $", исключено {e.ExcludedCount}" : "");
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (var key in _selection.Servers.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList())
                    if (!string.Equals(key, _progId, StringComparison.OrdinalIgnoreCase)) _selection.Servers.Remove(key);

                _selection.Save(_selectionPath);
                _dirty = false;
            }
            catch (UnauthorizedAccessException)
            {
                AddLog("Конфигуратор", "Нет прав на запись в папку конвертера — запустите Конфигуратор от имени администратора.", "ERROR");
                return;
            }
            catch (Exception ex)
            {
                AddLog("Конфигуратор", "Не удалось сохранить выбор: " + ex.Message, "ERROR");
                return;
            }

            var en = Entry;
            AddLog("Конфигуратор", "Применено для " + _progId + ": " +
                   (en == null ? "" : en.AutoAll ? "все теги автоматически" : $"{en.Items.Count} тегов, {en.Folders.Count} папок"));
            if (!_ua.Connected)
                AddLog("Конфигуратор", "Конвертер сейчас не запущен — выбор применится при его запуске.", "WARN");
            else
                StatusText.Text = "Сохранено. Конвертер применит выбор за пару секунд — таблица обновится сама.";
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            if (_progId == null) return;
            if (MessageBox.Show(this, "Снять весь выбор для " + _progId + "?", "Конфигуратор",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var entry = Entry;
            entry.Clear();
            SetAutoAllBox(false);
            MarkDirty();
            RefreshTree(Tree);
        }

        // ================================================================== CSV

        private void OnExportClick(object sender, RoutedEventArgs e)
        {
            if (_progId == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (разделитель ;)|*.csv", FileName = "выбор_" + SafeName(_progId) + ".csv" };
            if (dlg.ShowDialog(this) != true) return;

            var entry = Entry;
            var sb = new StringBuilder();
            sb.AppendLine("ProgId;ItemId");
            if (entry.AutoAll) sb.AppendLine("# режим: все теги автоматически");
            foreach (var f in entry.Folders) sb.AppendLine("# папка: " + string.Join("/", f));
            foreach (var i in entry.Items) sb.AppendLine(_progId + ";" + i.Id);
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            AddLog("Конфигуратор", "Выбор экспортирован: " + dlg.FileName);
        }

        private void OnExportValuesClick(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0) { AddLog("Конфигуратор", "Таблица пуста — нечего экспортировать.", "WARN"); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV (разделитель ;)|*.csv",
                FileName = "значения_" + SafeName(_progId) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
            };
            if (dlg.ShowDialog(this) != true) return;

            var sb = new StringBuilder();
            sb.AppendLine("#;Сервер;NodeId;Путь;Имя;Значение;Тип;Время источника;Время сервера;Статус");
            foreach (var r in _rows)
                sb.AppendLine(string.Join(";", new[] { r.Index.ToString(), r.ServerName, r.NodeIdText, r.Path, r.Name, r.ValueText,
                    r.TypeText, r.SourceTime, r.ServerTime, r.StatusText }.Select(Csv)));
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            AddLog("Конфигуратор", "Таблица значений сохранена: " + dlg.FileName);
        }

        private void OnImportClick(object sender, RoutedEventArgs e)
        {
            if (_progId == null) { AddLog("Конфигуратор", "Сначала выберите DA-сервер слева.", "WARN"); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "CSV / TXT|*.csv;*.txt" };
            if (dlg.ShowDialog(this) != true) return;

            var entry = Entry;
            int added = 0;
            foreach (var raw in File.ReadAllLines(dlg.FileName))
            {
                var line = raw.Trim().Trim('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("ProgId", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split(';');
                var id = (parts.Length >= 2 ? parts[parts.Length - 1] : parts[0]).Trim().Trim('"');
                if (parts.Length >= 2 && !string.Equals(parts[parts.Length - 2].Trim(), _progId, StringComparison.OrdinalIgnoreCase)) continue;
                if (id.Length == 0 || entry.HasItem(id)) continue;
                var segs = id.Split('.');
                entry.SetItem(id, segs.Length > 1 ? segs.Take(segs.Length - 1).ToArray() : new string[0], segs.Last(), true);
                added++;
            }
            MarkDirty();
            RefreshTree(Tree);
            AddLog("Конфигуратор", $"Импортировано тегов: {added}. Нажмите «Применить».");
        }

        // ================================================================== меню и прочее

        private async void OnRefreshTableClick(object sender, RoutedEventArgs e) => await RefreshTableAsync();

        private void OnClearLogClick(object sender, RoutedEventArgs e) => _log.Clear();

        private void OnExitClick(object sender, RoutedEventArgs e) => Close();

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            var v = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
            MessageBox.Show(this, "DaUaGateway Конфигуратор " + v +
                                  "\n\nВыбор тегов OPC DA для конвертации в OPC UA и проверка результата.",
                "О программе", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_dirty)
            {
                var r = MessageBox.Show(this, "Есть несохранённые изменения. Применить их перед выходом?", "Конфигуратор",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (r == MessageBoxResult.Yes)
                {
                    try { _selection.Save(_selectionPath); }
                    catch (Exception ex) { MessageBox.Show(this, "Не удалось сохранить: " + ex.Message); e.Cancel = true; return; }
                }
            }
            SaveLayout();
            _logTimer.Stop();
            _ua.Dispose();
            _da.Dispose();
        }

        // ================================================================== размеры панелей

        private string LayoutPath => Path.Combine(AppDataDir, "layout.json");

        private void RestoreLayout()
        {
            try
            {
                if (!File.Exists(LayoutPath)) return;
                var j = JObject.Parse(File.ReadAllText(LayoutPath));
                double Get(string k, double min) => Math.Max(min, (double?)j[k] ?? 0);
                if (j["left"] != null) LeftColDef.Width = new GridLength(Get("left", 220));
                if (j["right"] != null) RightColDef.Width = new GridLength(Get("right", 220));
                if (j["servers"] != null) ServersRowDef.Height = new GridLength(Get("servers", 70));
                if (j["log"] != null) LogRowDef.Height = new GridLength(Get("log", 70));
            }
            catch { /* испорченный файл — стандартные размеры */ }
        }

        private void SaveLayout()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                var j = new JObject
                {
                    ["left"] = LeftColDef.ActualWidth,
                    ["right"] = RightColDef.ActualWidth,
                    ["servers"] = ServersRowDef.ActualHeight,
                    ["log"] = LogRowDef.ActualHeight
                };
                File.WriteAllText(LayoutPath, j.ToString());
            }
            catch { }
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ';', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        private static string SafeName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static UIElement FolderIcon() => new Shapes.Path
        {
            Width = 16, Height = 14, Stretch = Stretch.Uniform,
            Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0xD2, 0x7A)),
            Stroke = new SolidColorBrush(Color.FromRgb(0xA3, 0x7A, 0x12)), StrokeThickness = 1,
            Data = Geometry.Parse("M1,3 L6,3 L8,5 L15,5 L15,14 L1,14 Z"),
            VerticalAlignment = VerticalAlignment.Center
        };

        private static UIElement TagIcon() => new Shapes.Path
        {
            Width = 14, Height = 14, Stretch = Stretch.Uniform,
            Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57)),
            Data = Geometry.Parse("M2,2 L9,2 L14,7 L9,12 L2,12 Z"),
            VerticalAlignment = VerticalAlignment.Center
        };
    }
}
