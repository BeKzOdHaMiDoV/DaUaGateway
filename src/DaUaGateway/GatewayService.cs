using System;
using System.ServiceProcess;
using DaUaGateway.Logging;

namespace DaUaGateway
{
    /// <summary>Служба Windows. Устанавливается скриптом scripts/install-service.ps1.</summary>
    public class GatewayService : ServiceBase
    {
        private GatewayHost _host;

        public GatewayService()
        {
            ServiceName = "DaUaGateway";
            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            _host = new GatewayHost();
            try
            {
                _host.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось запустить службу", ex);
                throw;
            }
        }

        protected override void OnStop()
        {
            try { _host?.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error("Ошибка при остановке", ex); }
        }

        protected override void OnShutdown() => OnStop();
    }
}
