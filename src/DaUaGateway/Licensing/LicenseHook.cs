namespace DaUaGateway.Licensing
{
    /// <summary>
    /// Точка подключения лицензирования ЗАКАЗЧИКА.
    /// Вызывается один раз при старте шлюза. Верните false — шлюз не запустится,
    /// message будет записан в лог.
    /// </summary>
    public static class LicenseHook
    {
        public static bool Check(out string message)
        {
            // TODO (заказчик): вставить свою проверку лицензии здесь.
            message = null;
            return true;
        }
    }
}
