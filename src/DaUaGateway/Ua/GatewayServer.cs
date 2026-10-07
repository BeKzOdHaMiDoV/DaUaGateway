using System;
using Opc.Ua;
using Opc.Ua.Server;

namespace DaUaGateway.Ua
{
    /// <summary>UA-сервер шлюза.</summary>
    public class GatewayServer : StandardServer
    {
        private readonly string _namespaceUri;

        public GatewayServer(string namespaceUri) { _namespaceUri = namespaceUri; }

        public GatewayNodeManager NodeManager { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(
            IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new GatewayNodeManager(server, configuration, _namespaceUri);
            return new MasterNodeManager(server, configuration, null, NodeManager);
        }

        protected override ServerProperties LoadServerProperties() => new ServerProperties
        {
            ManufacturerName = "DaUaGateway",
            ProductName = "OPC DA to UA Gateway",
            ProductUri = "urn:DaUaGateway:product",
            SoftwareVersion = GatewayInfo.Version,
            BuildNumber = "1",
            BuildDate = DateTime.UtcNow
        };
    }

    public static class GatewayInfo
    {
        public static string Version =>
            typeof(GatewayInfo).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
    }
}
