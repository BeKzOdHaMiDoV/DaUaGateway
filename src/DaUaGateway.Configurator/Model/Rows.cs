using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Opc.Ua;

namespace DaUaGateway.Configurator.Model
{
    /// <summary>Строка таблицы «Переведённые теги» (как Data Access View в UaExpert).</summary>
    public sealed class UaRow : INotifyPropertyChanged
    {
        private static readonly Brush GoodBrush = Frozen(0x1D, 0x6B, 0x2F);
        private static readonly Brush BadBrush = Frozen(0xB3, 0x26, 0x1E);
        private static readonly Brush UncertainBrush = Frozen(0x8A, 0x5A, 0x00);
        private static readonly Brush WaitBrush = Frozen(0x4D, 0x4A, 0x3F);
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        private int _index;
        private string _value = "…", _type = "", _source = "", _server = "", _status = "ожидание";
        private Brush _statusBrush = WaitBrush;

        public UaRow(NodeId nodeId, string serverName, string path, string name)
        {
            NodeId = nodeId;
            NodeIdText = nodeId.ToString();
            ServerName = serverName;
            Path = path;
            Name = name;
        }

        public NodeId NodeId { get; }
        public string NodeIdText { get; }
        public string ServerName { get; }
        public string Path { get; }
        public string Name { get; }
        public DataValue Last { get; private set; }

        public int Index { get => _index; set => Set(ref _index, value); }
        public string ValueText { get => _value; private set => Set(ref _value, value); }
        public string TypeText { get => _type; private set => Set(ref _type, value); }
        public string SourceTime { get => _source; private set => Set(ref _source, value); }
        public string ServerTime { get => _server; private set => Set(ref _server, value); }
        public string StatusText { get => _status; private set => Set(ref _status, value); }
        public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

        public void Apply(DataValue dv)
        {
            Last = dv;
            ValueText = Format(dv.Value);
            var ti = dv.WrappedValue.TypeInfo;
            if (ti != null && ti.BuiltInType != BuiltInType.Null)
                TypeText = ti.BuiltInType + (ti.ValueRank >= 1 ? "[]" : "");
            SourceTime = Time(dv.SourceTimestamp);
            ServerTime = Time(dv.ServerTimestamp);

            var sc = dv.StatusCode;
            if (StatusCode.IsGood(sc)) { StatusText = "Good"; StatusBrush = GoodBrush; }
            else if (StatusCode.IsUncertain(sc)) { StatusText = sc.ToString(); StatusBrush = UncertainBrush; }
            else { StatusText = sc.ToString(); StatusBrush = BadBrush; }
        }

        public static string Format(object value)
        {
            switch (value)
            {
                case null: return "—";
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case DateTime d: return d.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", Ru);
                case double d: return d.ToString("G10", Ru);
                case float f: return f.ToString("G7", Ru);
                case Array a:
                    return "{" + string.Join("; ", a.Cast<object>().Take(5).Select(Format)) + (a.Length > 5 ? "; …" : "") + "}";
                case IFormattable fm: return fm.ToString(null, Ru);
                default: return value.ToString();
            }
        }

        private static string Time(DateTime t) =>
            t == DateTime.MinValue ? "" : t.ToLocalTime().ToString("HH:mm:ss.fff", Ru);

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Строка панели «Атрибуты».</summary>
    public sealed class AttrRow
    {
        public AttrRow(string name, string value) { Name = name; Value = value; }
        public string Name { get; }
        public string Value { get; }
    }

    /// <summary>Строка панели «Журнал».</summary>
    public sealed class LogRow
    {
        private static readonly Brush Normal = Frozen(0x10, 0x20, 0x2C);
        private static readonly Brush Warn = Frozen(0x8A, 0x5A, 0x00);
        private static readonly Brush Error = Frozen(0xB3, 0x26, 0x1E);

        public LogRow(string time, string source, string message, string level = "INFO")
        {
            Time = time; Source = source; Message = message;
            Brush = level == "ERROR" ? Error : level == "WARN" ? Warn : Normal;
        }

        public string Time { get; }
        public string Source { get; }
        public string Message { get; }
        public Brush Brush { get; }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
