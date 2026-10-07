"""
Запускать на ЧУЖОМ (удалённом) компьютере.
Читает все DA-теги через наш шлюз (версия 2: все найденные DA-серверы) по OPC UA и печатает изменения в реальном времени.

Установка (один раз):
    pip install asyncua

Запуск:
    python 2_read_da.py opc.tcp://192.168.1.10:4840/DaUaGateway
    (вместо 192.168.1.10 — IP компьютера со шлюзом)
"""
import asyncio
import sys

from asyncua import Client, ua

URL = sys.argv[1] if len(sys.argv) > 1 else "opc.tcp://192.168.1.10:4840/DaUaGateway"
NAMESPACE_URI = "urn:DaUaGateway:tags"   # тот же, что в gateway.json
RECONNECT_DELAY = 5                      # секунд между попытками переподключения

names = {}


class Handler:
    """Вызывается при каждом изменении значения тега."""

    def datachange_notification(self, node, val, data):
        dv = data.monitored_item.Value
        ts = dv.SourceTimestamp.strftime("%H:%M:%S") if dv.SourceTimestamp else "--:--:--"
        status = getattr(dv.StatusCode, "name", str(dv.StatusCode))
        print(f"{ts}  {names.get(node.nodeid, node):<45} {str(val):<22} {status}")


async def run_once():
    async with Client(url=URL, timeout=10) as client:
        # Индекс namespace ищем по URI, а не хардкодим ns=2
        idx = await client.get_namespace_index(NAMESPACE_URI)
        # v2: Objects/Servers/<источник>/<папки>/<теги> — обходим дерево рекурсивно
        servers = await client.nodes.objects.get_child([f"{idx}:Servers"])
        tags = []

        async def collect(node, prefix):
            for child in await node.get_children():
                name = (await child.read_browse_name()).Name
                if await child.read_node_class() == ua.NodeClass.Variable:
                    tags.append(child)
                    names[child.nodeid] = prefix + name
                else:
                    await collect(child, prefix + name + "/")

        await collect(servers, "")

        print(f"Подключено к {URL}, тегов: {len(tags)}\n")
        print(f"{'ВРЕМЯ':<9} {'ТЕГ':<45} {'ЗНАЧЕНИЕ':<22} СТАТУС")

        sub = await client.create_subscription(500, Handler())  # 500 мс
        await sub.subscribe_data_change(tags)

        # Держим соединение; если шлюз пропадёт — check_connection выбросит исключение
        while True:
            await asyncio.sleep(2)
            await client.check_connection()


async def main():
    while True:
        try:
            await run_once()
        except KeyboardInterrupt:
            break
        except Exception as e:
            print(f"\n[!] Нет связи со шлюзом: {e}. Повтор через {RECONNECT_DELAY} с...")
            await asyncio.sleep(RECONNECT_DELAY)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nОстановлено.")
