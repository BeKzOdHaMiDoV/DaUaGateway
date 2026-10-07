# DaUaGateway — конвертер OPC DA → OPC UA

Windows-конвертер, который находит DA-серверы на компьютере, переводит выбранные теги в OPC UA
и отдаёт их SCADA и любым OPC UA клиентам по `opc.tcp://<host>:4840/DaUaGateway`.

## Состав

| Проект | Что это |
|---|---|
| `src/DaUaGateway` | Конвертер: OPC DA клиент + OPC UA сервер. Окно-пульт с запуском/остановкой и треем, служба Windows, журнал с кодами проблем |
| `src/DaUaGateway.Configurator` | Конфигуратор: дерево тегов DA с выбором, живые значения из OPC UA, атрибуты, журнал |

## Возможности

- автопоиск DA-серверов (OpcEnum) и обзор дерева тегов;
- выбор тегов и папок с исключениями, изменения применяются на лету без перезапуска;
- точные типы данных, массивы, качество DA → StatusCode UA, контроль меток времени;
- автоматическое переподключение к DA-серверу;
- OPC UA с шифрованием (Basic256Sha256) и сертификатами;
- работа как служба Windows или как приложение в трее;
- журнал на русском с кодами проблем (E2xx–E6xx) и отдельным файлом ошибок.

## Сборка

Требования: Windows, .NET SDK 8+ (сборка под .NET Framework 4.8, x86), OPC Core Components.

```
dotnet build DaUaGateway.sln -c Release
```

Конфигуратор кладётся в подпапку `Configurator` рядом с `DaUaGateway.exe`.
Подробности по настройке и запуску — `src/DaUaGateway/README.md`.

## Зависимости

- [OPC Foundation UA .NET Standard](https://github.com/OPCFoundation/UA-.NETStandard) (MIT)
- [TitaniumAS.Opc.Client](https://github.com/titanium-as/TitaniumAS.Opc.Client) (MIT)
- Newtonsoft.Json (MIT)

© Bekzod. Все права защищены.
