#!/usr/bin/env python3
"""
Run a minimal RideManager AppSync BLE peripheral on Linux BlueZ.

Requires Debian/Ubuntu packages:
  sudo apt install python3-dbus python3-gi

This script is intentionally standalone so the Bluetooth peripheral path can be
tested without the .NET application.
"""

from __future__ import annotations

import argparse
import json
import signal
import sys
from pathlib import Path
from typing import Any

try:
    import dbus
    import dbus.mainloop.glib
    import dbus.service
    from gi.repository import GLib
except ImportError as exc:
    print(
        "Missing Python BlueZ dependencies. Install: sudo apt install python3-dbus python3-gi",
        file=sys.stderr,
    )
    raise SystemExit(2) from exc


BLUEZ_SERVICE = "org.bluez"
DBUS_OBJECT_MANAGER = "org.freedesktop.DBus.ObjectManager"
DBUS_PROPERTIES = "org.freedesktop.DBus.Properties"
GATT_MANAGER = "org.bluez.GattManager1"
LE_ADV_MANAGER = "org.bluez.LEAdvertisingManager1"
LE_ADVERTISEMENT = "org.bluez.LEAdvertisement1"
GATT_SERVICE = "org.bluez.GattService1"
GATT_CHARACTERISTIC = "org.bluez.GattCharacteristic1"

APP_PATH = "/com/ridemanager/appsync"
SERVICE_PATH = f"{APP_PATH}/service0"
RX_PATH = f"{SERVICE_PATH}/rx"
TX_PATH = f"{SERVICE_PATH}/tx"
ADVERTISEMENT_PATH = "/com/ridemanager/appsync/advertisement0"

DEFAULT_DEVICE_NAME = "RideManager"
DEFAULT_SERVICE_UUID = "7f7d0001-4f52-4d32-9b2a-0f0b5a8b1000"
DEFAULT_RX_UUID = "7f7d0002-4f52-4d32-9b2a-0f0b5a8b1000"
DEFAULT_TX_UUID = "7f7d0003-4f52-4d32-9b2a-0f0b5a8b1000"


def dbus_array(values: list[Any], signature: str) -> dbus.Array:
    return dbus.Array(values, signature=signature)


def bytes_to_dbus(value: bytes) -> dbus.Array:
    return dbus.Array([dbus.Byte(byte) for byte in value], signature="y")


def load_appsync_config(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}

    config: dict[str, str] = {}
    in_app_sync = False
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("[") and line.endswith("]"):
            in_app_sync = line == "[app_sync]"
            continue
        if not in_app_sync or "=" not in line:
            continue

        key, value = line.split("=", 1)
        value = value.strip()
        if value.startswith('"') and value.endswith('"'):
            value = value[1:-1]
        config[key.strip()] = value
    return config


class PropertiesMixin:
    def get_properties(self) -> dict[str, dict[str, Any]]:
        raise NotImplementedError

    @dbus.service.method(DBUS_PROPERTIES, in_signature="s", out_signature="a{sv}")
    def GetAll(self, interface: str) -> dict[str, Any]:
        return self.get_properties().get(interface, {})

    @dbus.service.method(DBUS_PROPERTIES, in_signature="ss", out_signature="v")
    def Get(self, interface: str, prop: str) -> Any:
        props = self.get_properties().get(interface, {})
        if prop not in props:
            raise dbus.exceptions.DBusException(
                f"No such property {interface}.{prop}",
                name="org.freedesktop.DBus.Error.InvalidArgs",
            )
        return props[prop]

    @dbus.service.method(DBUS_PROPERTIES, in_signature="ssv", out_signature="")
    def Set(self, interface: str, prop: str, value: Any) -> None:
        raise dbus.exceptions.DBusException(
            f"Property {interface}.{prop} is read-only",
            name="org.freedesktop.DBus.Error.PropertyReadOnly",
        )

    @dbus.service.signal(DBUS_PROPERTIES, signature="sa{sv}as")
    def PropertiesChanged(
        self,
        interface: str,
        changed: dict[str, Any],
        invalidated: list[str],
    ) -> None:
        pass


class Advertisement(dbus.service.Object, PropertiesMixin):
    def __init__(self, bus: dbus.Bus, device_name: str, service_uuid: str):
        super().__init__(bus, ADVERTISEMENT_PATH)
        self.path = dbus.ObjectPath(ADVERTISEMENT_PATH)
        self.device_name = device_name
        self.service_uuid = service_uuid

    def get_properties(self) -> dict[str, dict[str, Any]]:
        return {
            LE_ADVERTISEMENT: {
                "Type": dbus.String("peripheral"),
                "ServiceUUIDs": dbus_array([self.service_uuid], "s"),
                "LocalName": dbus.String(self.device_name),
                "Discoverable": dbus.Boolean(True),
                "Includes": dbus_array(["tx-power"], "s"),
            }
        }

    @dbus.service.method(LE_ADVERTISEMENT, in_signature="", out_signature="")
    def Release(self) -> None:
        print("Advertisement released by BlueZ")


class Application(dbus.service.Object):
    def __init__(
        self,
        bus: dbus.Bus,
        service_uuid: str,
        rx_uuid: str,
        tx_uuid: str,
        notify_chunk_bytes: int,
    ):
        super().__init__(bus, APP_PATH)
        self.path = dbus.ObjectPath(APP_PATH)
        self.service = AppSyncService(bus, service_uuid)
        self.tx = TxCharacteristic(bus, tx_uuid, notify_chunk_bytes)
        self.rx = RxCharacteristic(bus, rx_uuid, self.tx.notify_response)

    @dbus.service.method(DBUS_OBJECT_MANAGER, out_signature="a{oa{sa{sv}}}")
    def GetManagedObjects(self) -> dict[dbus.ObjectPath, dict[str, dict[str, Any]]]:
        objects = {
            self.service.path: self.service.get_properties(),
            self.rx.path: self.rx.get_properties(),
            self.tx.path: self.tx.get_properties(),
        }
        return objects


class AppSyncService(dbus.service.Object, PropertiesMixin):
    def __init__(self, bus: dbus.Bus, uuid: str):
        super().__init__(bus, SERVICE_PATH)
        self.path = dbus.ObjectPath(SERVICE_PATH)
        self.uuid = uuid

    def get_properties(self) -> dict[str, dict[str, Any]]:
        return {
            GATT_SERVICE: {
                "UUID": dbus.String(self.uuid),
                "Primary": dbus.Boolean(True),
                "Characteristics": dbus.Array(
                    [dbus.ObjectPath(RX_PATH), dbus.ObjectPath(TX_PATH)],
                    signature="o",
                ),
            }
        }


class RxCharacteristic(dbus.service.Object, PropertiesMixin):
    def __init__(self, bus: dbus.Bus, uuid: str, responder):
        super().__init__(bus, RX_PATH)
        self.path = dbus.ObjectPath(RX_PATH)
        self.uuid = uuid
        self.responder = responder

    def get_properties(self) -> dict[str, dict[str, Any]]:
        return {
            GATT_CHARACTERISTIC: {
                "UUID": dbus.String(self.uuid),
                "Service": dbus.ObjectPath(SERVICE_PATH),
                "Flags": dbus_array(["write", "write-without-response"], "s"),
            }
        }

    @dbus.service.method(GATT_CHARACTERISTIC, in_signature="aya{sv}", out_signature="")
    def WriteValue(self, value: list[int], options: dict[str, Any]) -> None:
        data = bytes(value)
        text = data.decode("utf-8", errors="replace")
        print(f"RX {len(data)} bytes: {text}")
        self.responder(create_response(text))


class TxCharacteristic(dbus.service.Object, PropertiesMixin):
    def __init__(self, bus: dbus.Bus, uuid: str, notify_chunk_bytes: int):
        super().__init__(bus, TX_PATH)
        self.path = dbus.ObjectPath(TX_PATH)
        self.uuid = uuid
        self.notify_chunk_bytes = max(20, notify_chunk_bytes)
        self.notifying = False
        self.value = b""

    def get_properties(self) -> dict[str, dict[str, Any]]:
        return {
            GATT_CHARACTERISTIC: {
                "UUID": dbus.String(self.uuid),
                "Service": dbus.ObjectPath(SERVICE_PATH),
                "Flags": dbus_array(["notify"], "s"),
                "Notifying": dbus.Boolean(self.notifying),
                "Value": bytes_to_dbus(self.value),
            }
        }

    @dbus.service.method(GATT_CHARACTERISTIC, in_signature="", out_signature="")
    def StartNotify(self) -> None:
        if self.notifying:
            return
        self.notifying = True
        print("TX notifications enabled")
        self.PropertiesChanged(
            GATT_CHARACTERISTIC,
            {"Notifying": dbus.Boolean(True)},
            [],
        )

    @dbus.service.method(GATT_CHARACTERISTIC, in_signature="", out_signature="")
    def StopNotify(self) -> None:
        if not self.notifying:
            return
        self.notifying = False
        print("TX notifications disabled")
        self.PropertiesChanged(
            GATT_CHARACTERISTIC,
            {"Notifying": dbus.Boolean(False)},
            [],
        )

    def notify_response(self, response: str) -> None:
        data = response.encode("utf-8")
        print(f"TX {len(data)} bytes: {response}")
        if not self.notifying:
            print("TX notification skipped because client has not subscribed")
            return

        for offset in range(0, len(data), self.notify_chunk_bytes):
            self.value = data[offset : offset + self.notify_chunk_bytes]
            self.PropertiesChanged(
                GATT_CHARACTERISTIC,
                {"Value": bytes_to_dbus(self.value)},
                [],
            )


def create_response(text: str) -> str:
    try:
        request = json.loads(text)
        request_id = str(request.get("id", "python-test"))
        request_type = str(request.get("type", "unknown"))
    except json.JSONDecodeError as exc:
        return json.dumps(
            {
                "v": 1,
                "id": "",
                "type": "error",
                "status": "bad_json",
                "payload": {"message": str(exc)},
            },
            separators=(",", ":"),
        )

    if request_type == "hello":
        payload: dict[str, Any] = {
            "deviceName": DEFAULT_DEVICE_NAME,
            "protocol": "RideManager.AppSync.PythonBlueZTest",
            "version": 1,
            "defaultSyncWindowHours": 24,
            "maxPageSize": 100,
            "capabilities": ["hello", "ping"],
        }
    elif request_type == "ping":
        payload = {"pong": GLib.DateTime.new_now_utc().format_iso8601()}
    else:
        payload = {"message": f"Python BlueZ test host received {request_type}"}

    return json.dumps(
        {
            "v": 1,
            "id": request_id,
            "type": request_type,
            "status": "ok",
            "payload": payload,
        },
        separators=(",", ":"),
    )


def find_adapter(bus: dbus.Bus, adapter_name: str | None) -> dbus.ObjectPath:
    manager = dbus.Interface(
        bus.get_object(BLUEZ_SERVICE, "/"),
        DBUS_OBJECT_MANAGER,
    )
    objects = manager.GetManagedObjects()
    for path, interfaces in objects.items():
        if GATT_MANAGER not in interfaces or LE_ADV_MANAGER not in interfaces:
            continue
        if adapter_name is None or str(path).endswith(f"/{adapter_name}"):
            return path

    requested = adapter_name or "any"
    raise RuntimeError(f"No BlueZ adapter with GATT and advertising managers found: {requested}")


def set_adapter_property(bus: dbus.Bus, adapter_path: dbus.ObjectPath, name: str, value: Any) -> None:
    props = dbus.Interface(bus.get_object(BLUEZ_SERVICE, adapter_path), DBUS_PROPERTIES)
    props.Set("org.bluez.Adapter1", name, value)


def parse_args() -> argparse.Namespace:
    config = load_appsync_config(Path("config.toml"))
    parser = argparse.ArgumentParser(description="RideManager BlueZ AppSync BLE host test")
    parser.add_argument("--adapter", default=None, help="Adapter name, for example hci0")
    parser.add_argument("--name", default=config.get("device_name", DEFAULT_DEVICE_NAME))
    parser.add_argument("--service-uuid", default=config.get("service_uuid", DEFAULT_SERVICE_UUID))
    parser.add_argument("--rx-uuid", default=config.get("rx_uuid", DEFAULT_RX_UUID))
    parser.add_argument("--tx-uuid", default=config.get("tx_uuid", DEFAULT_TX_UUID))
    parser.add_argument(
        "--notify-chunk-bytes",
        type=int,
        default=int(config.get("notify_chunk_bytes", "180")),
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    bus = dbus.SystemBus()
    adapter_path = find_adapter(bus, args.adapter)

    set_adapter_property(bus, adapter_path, "Powered", dbus.Boolean(True))
    set_adapter_property(bus, adapter_path, "Alias", dbus.String(args.name))

    app = Application(
        bus,
        args.service_uuid,
        args.rx_uuid,
        args.tx_uuid,
        args.notify_chunk_bytes,
    )
    advertisement = Advertisement(bus, args.name, args.service_uuid)

    adapter_obj = bus.get_object(BLUEZ_SERVICE, adapter_path)
    gatt_manager = dbus.Interface(adapter_obj, GATT_MANAGER)
    adv_manager = dbus.Interface(adapter_obj, LE_ADV_MANAGER)
    loop = GLib.MainLoop()
    state = {"gatt": False, "advertisement": False}

    def stop(*_args: object) -> None:
        print("Stopping BlueZ AppSync host...")
        try:
            if state["advertisement"]:
                adv_manager.UnregisterAdvertisement(advertisement.path)
                state["advertisement"] = False
        except Exception as exc:  # noqa: BLE001
            print(f"Warning: advertisement unregister failed: {exc}")
        try:
            if state["gatt"]:
                gatt_manager.UnregisterApplication(app.path)
                state["gatt"] = False
        except Exception as exc:  # noqa: BLE001
            print(f"Warning: GATT unregister failed: {exc}")
        loop.quit()

    def on_gatt_registered() -> None:
        state["gatt"] = True
        print(f"GATT registered on {adapter_path}")
        print(f"  service={args.service_uuid}")
        print(f"  rx={args.rx_uuid}")
        print(f"  tx={args.tx_uuid}")

    def on_advertisement_registered() -> None:
        state["advertisement"] = True
        print(f"Advertisement registered as {args.name}")
        print("Use nRF Connect: scan service UUID, connect, subscribe TX, write JSON to RX.")

    def on_error(error: Exception) -> None:
        print(f"BlueZ registration failed: {error}", file=sys.stderr)
        stop()

    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)

    gatt_manager.RegisterApplication(
        app.path,
        {},
        reply_handler=on_gatt_registered,
        error_handler=on_error,
    )
    adv_manager.RegisterAdvertisement(
        advertisement.path,
        {},
        reply_handler=on_advertisement_registered,
        error_handler=on_error,
    )

    print(f"Starting BlueZ AppSync host on {adapter_path}...")
    loop.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
