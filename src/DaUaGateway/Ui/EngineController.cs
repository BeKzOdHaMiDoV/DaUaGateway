using System;
using System.Threading.Tasks;
using DaUaGateway.Logging;

namespace DaUaGateway.Ui
{
    public enum EngineState { Stopped, Starting, Running, Stopping, Failed }

    /// <summary>Запуск и остановка конвертера из окна-пульта (без перезапуска программы).</summary>
    public sealed class EngineController
    {
        private GatewayHost _host;

        public EngineState State { get; private set; } = EngineState.Stopped;
        public string Error { get; private set; }
        public GatewayHost Host => State == EngineState.Running ? _host : null;

        public async Task StartAsync()
        {
            if (State == EngineState.Running || State == EngineState.Starting) return;
            State = EngineState.Starting;
            Error = null;

            var host = new GatewayHost();
            try
            {
                await Task.Run(() => host.StartAsync());
                _host = host;
                State = EngineState.Running;
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось запустить конвертер", ex);
                Error = ex.Message;
                State = EngineState.Failed;
                try { await Task.Run(() => host.StopAsync()); } catch { }
            }
        }

        public async Task StopAsync()
        {
            if (State != EngineState.Running) return;
            State = EngineState.Stopping;
            var host = _host;
            _host = null;
            try { await Task.Run(() => host.StopAsync()); }
            catch (Exception ex) { Log.Error("Ошибка при остановке", ex); }
            State = EngineState.Stopped;
        }
    }
}
