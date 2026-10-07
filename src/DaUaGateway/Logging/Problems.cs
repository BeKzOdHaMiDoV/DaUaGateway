using System;
using System.Runtime.InteropServices;

namespace DaUaGateway.Logging
{
    /// <summary>Понятное описание проблемы для журнала: код, что случилось, причина, что сделать.</summary>
    public sealed class Problem
    {
        public string Code;
        public string Title;
        public string Cause;
        public string Action;
        public string Raw;

        /// <summary>Одна строка для журнала (без переносов — её читают Конфигуратор и окно конвертера).</summary>
        public override string ToString() =>
            $"[{Code}] {Title}. Причина: {Cause}. Что сделать: {Action}." + (string.IsNullOrEmpty(Raw) ? "" : $" ({Raw})");
    }

    /// <summary>
    /// Каталог проблем. Коды:
    ///   E2xx — связь с DA-сервером,  E3xx — теги,  E4xx — OPC UA / SCADA,
    ///   E5xx — данные,  E6xx — настройки.
    /// Полная таблица — в ИНСТРУКЦИЯ.txt.
    /// </summary>
    public static class Problems
    {
        // HRESULT-коды Windows / COM / OPC
        private const int CO_E_SERVER_EXEC_FAILURE = unchecked((int)0x80080005);
        private const int E_ACCESSDENIED = unchecked((int)0x80070005);
        private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
        private const int CO_E_CLASSSTRING = unchecked((int)0x800401F3);
        private const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);
        private const int RPC_S_CALL_FAILED = unchecked((int)0x800706BE);
        private const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
        private const int RPC_E_SERVERFAULT = unchecked((int)0x80010105);
        private const int OPC_E_INVALIDITEMID = unchecked((int)0xC0040008);
        private const int OPC_E_UNKNOWNITEMID = unchecked((int)0xC0040007);
        private const int OPC_E_BADRIGHTS = unchecked((int)0xC0040006);
        private const int OPC_E_UNKNOWNPATH = unchecked((int)0xC004000A);

        // ------------------------------------------------------------------ связь с DA-сервером

        /// <summary>Ошибка подключения / связи с DA-сервером.</summary>
        public static Problem FromDaException(string server, Exception ex)
        {
            int hr = FindHResult(ex);
            string msg = Flatten(ex);
            string raw = msg + (hr != 0 ? $", 0x{hr:X8}" : "");

            if (hr == CO_E_SERVER_EXEC_FAILURE || Has(msg, "Server execution failed", "Сбой при выполнении сервера"))
                return P("E201", $"DA-сервер {server} не запустился",
                    "Windows не смогла запустить программу DA-сервера — обычно она работает только вместе со своей SCADA/средой исполнения, или ей не хватает прав DCOM",
                    $"запустите программу, которой принадлежит {server} (например, среду исполнения SCADA), или снимите выбор тегов этого сервера в Конфигураторе", raw);

            if (hr == E_ACCESSDENIED || Has(msg, "Access is denied", "Отказано в доступе"))
                return P("E202", $"Нет доступа к DA-серверу {server}",
                    "учётная запись, от которой работает конвертер, не имеет прав DCOM на запуск/доступ к DA-серверу (часто — когда конвертер установлен службой)",
                    "запустите конвертер от того же пользователя, что и DA-клиенты, или в свойствах службы DaUaGateway (services.msc → «Вход в систему») укажите этого пользователя",
                    raw);

            if (hr == REGDB_E_CLASSNOTREG || hr == CO_E_CLASSSTRING || Has(msg, "Class not registered", "Класс не зарегистрирован", "Invalid class string"))
                return P("E203", $"DA-сервер {server} не установлен или не зарегистрирован",
                    "в Windows нет зарегистрированной программы с таким именем (ProgID) — сервер удалён, имя указано с ошибкой или не установлены OPC Core Components",
                    "проверьте имя DA-сервера, переустановите DA-сервер или OPC Core Components", raw);

            if (hr == RPC_S_SERVER_UNAVAILABLE || hr == RPC_S_CALL_FAILED || hr == RPC_E_DISCONNECTED || hr == RPC_E_SERVERFAULT ||
                Has(msg, "RPC server is unavailable", "Сервер RPC недоступен", "remote procedure call failed", "disconnected from its clients",
                    "Соединение с DA-сервером потеряно", "завершил работу"))
                return P("E204", $"Связь с DA-сервером {server} оборвалась",
                    "программа DA-сервера закрылась, перезапустилась или зависла",
                    "конвертер переподключится сам; если ошибка повторяется — проверьте, что DA-сервер работает (в его собственной программе или в Диспетчере задач)", raw);

            return P("E200", $"Ошибка связи с DA-сервером {server}",
                "DA-сервер вернул ошибку при подключении или обмене",
                "конвертер будет переподключаться сам; если повторяется — пришлите папку logs", raw);
        }

        public static Problem DiscoveryFailed(string host, Exception ex) =>
            P("E205", $"Не удалось получить список DA-серверов на {host}",
                "служба OpcEnum не отвечает или не установлены OPC Core Components (для удалённого компьютера — нужен DCOM)",
                "проверьте, что установлены OPC Core Components и служба OpcEnum запущена (services.msc); DA-серверы, выбранные в Конфигураторе, будут подключаться и без этого списка",
                Flatten(ex));

        public static Problem DaShutdown(string server, string reason) =>
            P("E206", $"DA-сервер {server} сообщил, что завершает работу",
                "программа DA-сервера закрывается (вручную, при обновлении или перезапуске SCADA)",
                "конвертер переподключится, когда DA-сервер снова запустится", reason);

        // ------------------------------------------------------------------ теги

        public static Problem ItemRejected(string server, string itemId, int hr)
        {
            string raw = $"0x{hr:X8}";
            if (hr == OPC_E_UNKNOWNITEMID || hr == OPC_E_UNKNOWNPATH)
                return P("E301", $"Тег {itemId} не найден в DA-сервере {server}",
                    "такого тега нет (удалён, переименован или в проекте ПЛК изменилась структура)",
                    "снимите этот тег в Конфигураторе или выберите его заново из дерева", raw);
            if (hr == OPC_E_INVALIDITEMID)
                return P("E302", $"Неверное имя тега {itemId} в DA-сервере {server}",
                    "DA-сервер не принимает имя тега в таком виде (опечатка или неверный формат)",
                    "выберите тег из дерева в Конфигураторе, а не вводите имя вручную / через CSV", raw);
            if (hr == OPC_E_BADRIGHTS)
                return P("E302", $"Нет прав на тег {itemId} в DA-сервере {server}",
                    "DA-сервер запрещает доступ к этому тегу",
                    "проверьте настройки доступа в самом DA-сервере или снимите тег", raw);
            return P("E300", $"DA-сервер {server} не принял тег {itemId}",
                "DA-сервер вернул ошибку при добавлении тега",
                "снимите тег в Конфигураторе; если таких тегов много — пришлите папку logs", raw);
        }

        public static Problem BadQuality(string server, int count, string example, string reason) =>
            P("E303", $"{count} тег(ов) DA-сервера {server} с плохим качеством (Bad)",
                $"DA-сервер не получает данные от оборудования: {reason}",
                "проверьте связь DA-сервера с ПЛК/устройством (кабель, сеть, питание ПЛК, настройки драйвера в DA-сервере)",
                "например, тег " + example);

        public static Problem WriteRejected(string server, string itemId, string raw) =>
            P("E304", $"Запись в тег {itemId} отклонена DA-сервером {server}",
                "тег только для чтения, значение недопустимо или нет связи с ПЛК",
                "проверьте права тега (в Конфигураторе — «Права в DA») и значение", raw);

        // ------------------------------------------------------------------ OPC UA / SCADA

        public static Problem UaStartFailed(string endpoint, Exception ex)
        {
            var msg = Flatten(ex);
            if (Has(msg, "already in use", "Обычно разрешается только одно использование", "only one usage", "10048", "BadNoCommunication"))
                return P("E401", $"Порт OPC UA уже занят ({endpoint})",
                    "конвертер уже работает — службой Windows или второй копией, либо порт занят другим OPC UA сервером",
                    "проверьте службу DaUaGateway (services.msc) и значок конвертера в трее; если порт занят другой программой — смените порт в gateway.json (ua.endpoint)",
                    msg);
            return P("E400", "Не удалось запустить OPC UA сервер",
                "ошибка при запуске сервера OPC UA (сертификат, права на папку pki, порт)",
                "запустите конвертер от имени администратора один раз; если не помогло — пришлите папку logs", msg);
        }

        public static Problem ClientCertificateRejected(string subject, string rejectedDir) =>
            P("E402", $"Клиент OPC UA не допущен: сертификат «{subject}» не доверен",
                "SCADA (или другой клиент) подключается с шифрованием, а её сертификат ещё не добавлен в доверенные",
                $"перенесите файл сертификата из {rejectedDir} в папку pki\\trusted\\certs конвертера и подключитесь снова",
                null);

        // ------------------------------------------------------------------ данные / настройки

        public static Problem WrongTimestamps(string server, string itemId) =>
            P("E501", $"DA-сервер {server} присылает новые значения с неверным временем",
                $"время источника не обновляется или отстаёт (например, тег {itemId})",
                "ничего делать не нужно: для таких тегов конвертер ставит своё время, остальные сохраняют время DA (настройка timestampSource)",
                null);

        public static Problem SelectionFileBroken(string path, Exception ex) =>
            P("E601", "Файл выбора тегов повреждён",
                $"не удалось прочитать {path}",
                "откройте Конфигуратор, проверьте выбор и нажмите «Применить» — файл перезапишется; до этого действует прежний выбор",
                Flatten(ex));

        // ------------------------------------------------------------------ помощники

        private static Problem P(string code, string title, string cause, string action, string raw) =>
            new Problem { Code = code, Title = title, Cause = cause, Action = action, Raw = raw };

        private static bool Has(string text, params string[] parts)
        {
            foreach (var p in parts)
                if (text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static int FindHResult(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is COMException com) return com.ErrorCode;
                if (e.HResult != 0 && (e.HResult & 0x80000000) != 0 && e.HResult != unchecked((int)0x80131500)) return e.HResult;
            }
            return 0;
        }

        private static string Flatten(Exception ex)
        {
            if (ex == null) return "";
            var text = ex.Message;
            for (var e = ex.InnerException; e != null; e = e.InnerException)
                if (!text.Contains(e.Message)) text += " → " + e.Message;
            return text.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
