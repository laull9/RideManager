#!/usr/bin/env python3
"""
Diagnose EVADAR-C6 radar BLE behavior through Linux BlueZ D-Bus.

This script is intentionally independent from the .NET runtime. It checks the
same layers used by RideManager on Linux:
  - BlueZ adapter and controller state
  - BLE discovery and cached device properties
  - GATT service and characteristic discovery
  - StartNotify + PropertiesChanged(Value)
  - polling org.bluez.GattCharacteristic1.Value
  - ReadValue fallback
  - optional second health notify subscription stress test

Requires Debian/Ubuntu packages:
  sudo apt install python3-dbus python3-gi
"""

from __future__ import annotations

import argparse
import json
import signal
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

try:
    import dbus
    import dbus.mainloop.glib
    from gi.repository import GLib
except ImportError as exc:
    print(
        "Missing dependencies. Install: sudo apt install python3-dbus python3-gi",
        file=sys.stderr,
    )
    raise SystemExit(2) from exc


BLUEZ_SERVICE = "org.bluez"
DBUS_OBJECT_MANAGER = "org.freedesktop.DBus.ObjectManager"
DBUS_PROPERTIES = "org.freedesktop.DBus.Properties"
ADAPTER_IFACE = "org.bluez.Adapter1"
DEVICE_IFACE = "org.bluez.Device1"
GATT_SERVICE_IFACE = "org.bluez.GattService1"
GATT_CHAR_IFACE = "org.bluez.GattCharacteristic1"

DEFAULT_NAME = "EVADAR-C6"
DEFAULT_SERVICE_UUID = "0000ad01-0000-1000-8000-00805f9b34fb"
DEFAULT_NOTIFY_UUID = "0000ad02-0000-1000-8000-00805f9b34fb"
DEFAULT_HEALTH_UUID = "0000ad04-0000-1000-8000-00805f9b34fb"


def load_radar_config(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}

    config: dict[str, str] = {}
    in_radar = False
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("[") and line.endswith("]"):
            in_radar = line == "[sensors.radar]"
            continue
        if not in_radar or "=" not in line:
            continue

        key, value = line.split("=", 1)
        value = value.strip()
        if value.startswith('"') and value.endswith('"'):
            value = value[1:-1]
        config[key.strip()] = value
    return config


def as_str(value: Any) -> str:
    return "" if value is None else str(value)


def normalize_uuid(value: str) -> str:
    return value.strip().lower()


def dbus_bytes(value: Any) -> bytes:
    if value is None:
        return b""
    if isinstance(value, bytes):
        return value
    if isinstance(value, bytearray):
        return bytes(value)
    return bytes(int(item) & 0xFF for item in value)


def decode_payload(payload: bytes) -> str:
    return payload.decode("utf-8", errors="replace").strip()


def get_all(bus: dbus.SystemBus, path: str, iface: str) -> dict[str, Any]:
    props = dbus.Interface(bus.get_object(BLUEZ_SERVICE, path), DBUS_PROPERTIES)
    return dict(props.GetAll(iface))


def get_prop(bus: dbus.SystemBus, path: str, iface: str, name: str) -> Any:
    props = dbus.Interface(bus.get_object(BLUEZ_SERVICE, path), DBUS_PROPERTIES)
    return props.Get(iface, name)


def set_prop(bus: dbus.SystemBus, path: str, iface: str, name: str, value: Any) -> None:
    props = dbus.Interface(bus.get_object(BLUEZ_SERVICE, path), DBUS_PROPERTIES)
    props.Set(iface, name, value)


def print_header(title: str) -> None:
    print()
    print(f"== {title} ==")


def parse_frame(payload: bytes) -> tuple[Optional[int], str]:
    text = decode_payload(payload)
    try:
        obj = json.loads(text)
        seq = obj.get("seq")
        st = int(obj.get("st", 0))
        flags = []
        if st & 0x01:
            flags.append("BR")
        if st & 0x02:
            flags.append("HR")
        if st & 0x04:
            flags.append("D")
        if st & 0x08:
            flags.append("P")
        detail = (
            f"seq={seq} st=0x{st:02X} flags={','.join(flags) or '-'} "
            f"hr={obj.get('hr', '--')} br={obj.get('br', '--')} d={obj.get('d', '--')}"
        )
        return int(seq) if seq is not None else None, detail
    except Exception as exc:  # noqa: BLE001
        return None, f"parse_error={exc}: {text!r}"


@dataclass
class StreamStats:
    name: str
    frames: int = 0
    changed: int = 0
    parse_errors: int = 0
    first_seq: Optional[int] = None
    last_seq: Optional[int] = None
    drops: int = 0
    last_payload: bytes = b""
    source_counts: dict[str, int] = field(default_factory=dict)

    def observe(self, source: str, payload: bytes, verbose: bool) -> None:
        if not payload:
            return

        self.source_counts[source] = self.source_counts.get(source, 0) + 1
        self.frames += 1
        if payload != self.last_payload:
            self.changed += 1
            self.last_payload = payload

        seq, detail = parse_frame(payload)
        if seq is None:
            self.parse_errors += 1
        else:
            if self.first_seq is None:
                self.first_seq = seq
            if self.last_seq is not None:
                gap = seq - self.last_seq - 1
                if gap > 0:
                    self.drops += gap
            self.last_seq = seq

        if verbose:
            print(f"[{self.name}:{source}] {detail}")

    @property
    def seq_delta(self) -> int:
        if self.first_seq is None or self.last_seq is None:
            return 0
        return self.last_seq - self.first_seq


def managed_objects(bus: dbus.SystemBus) -> dict[str, dict[str, dict[str, Any]]]:
    manager = dbus.Interface(bus.get_object(BLUEZ_SERVICE, "/"), DBUS_OBJECT_MANAGER)
    return {str(path): dict(interfaces) for path, interfaces in manager.GetManagedObjects().items()}


def select_adapter(objects: dict[str, dict[str, dict[str, Any]]], requested: Optional[str]) -> str:
    adapters = [path for path, ifaces in objects.items() if ADAPTER_IFACE in ifaces]
    if requested:
        suffix = f"/{requested}"
        for path in adapters:
            if path.endswith(suffix):
                return path
        raise RuntimeError(f"Adapter {requested!r} not found. Available: {', '.join(adapters) or 'none'}")
    if not adapters:
        raise RuntimeError("No BlueZ adapter found.")
    return adapters[0]


def print_adapter(bus: dbus.SystemBus, path: str) -> None:
    props = get_all(bus, path, ADAPTER_IFACE)
    print(f"adapter={path}")
    for key in ("Address", "Name", "Alias", "Powered", "Discovering", "Pairable", "Discoverable"):
        if key in props:
            print(f"  {key}: {props[key]}")
    if "Roles" in props:
        print(f"  Roles: {list(props['Roles'])}")
    if "UUIDs" in props:
        print(f"  UUID count: {len(props['UUIDs'])}")


def device_matches(props: dict[str, Any], args: argparse.Namespace) -> bool:
    address = as_str(props.get("Address"))
    if args.address and address.lower() == args.address.lower():
        return True

    uuids = [normalize_uuid(str(uuid)) for uuid in props.get("UUIDs", [])]
    if args.by_service and normalize_uuid(args.service_uuid) in uuids:
        return True

    name = as_str(props.get("Name"))
    alias = as_str(props.get("Alias"))
    if args.name and (name == args.name or alias == args.name):
        return True

    return normalize_uuid(args.service_uuid) in uuids


def find_device(objects: dict[str, dict[str, dict[str, Any]]], adapter: str, args: argparse.Namespace) -> Optional[str]:
    for path, ifaces in objects.items():
        if not path.startswith(f"{adapter}/") or DEVICE_IFACE not in ifaces:
            continue
        if device_matches(ifaces[DEVICE_IFACE], args):
            return path
    return None


def print_device(objects: dict[str, dict[str, dict[str, Any]]], path: str) -> None:
    props = objects[path][DEVICE_IFACE]
    print(f"device={path}")
    for key in ("Address", "Name", "Alias", "RSSI", "Connected", "ServicesResolved", "Paired", "Trusted"):
        if key in props:
            print(f"  {key}: {props[key]}")
    print(f"  UUIDs: {[str(uuid) for uuid in props.get('UUIDs', [])]}")


def discover(bus: dbus.SystemBus, adapter: str, args: argparse.Namespace) -> str:
    print_header("Discovery")
    adapter_obj = bus.get_object(BLUEZ_SERVICE, adapter)
    adapter_iface = dbus.Interface(adapter_obj, ADAPTER_IFACE)

    objects = managed_objects(bus)
    cached = find_device(objects, adapter, args)
    if cached:
        print("matched cached device before scanning")
        print_device(objects, cached)
        return cached

    filt: dict[str, Any] = {
        "Transport": dbus.String("le"),
        "DuplicateData": dbus.Boolean(True),
    }
    if args.by_service:
        filt["UUIDs"] = dbus.Array([dbus.String(args.service_uuid)], signature="s")
    adapter_iface.SetDiscoveryFilter(dbus.Dictionary(filt, signature="sv"))
    adapter_iface.StartDiscovery()
    print(f"scanning {args.scan_seconds:.1f}s with filter={dict(filt)}")
    try:
        deadline = time.monotonic() + args.scan_seconds
        while time.monotonic() < deadline:
            objects = managed_objects(bus)
            device = find_device(objects, adapter, args)
            if device:
                print("matched device during scan")
                print_device(objects, device)
                return device
            time.sleep(0.25)
    finally:
        try:
            adapter_iface.StopDiscovery()
        except Exception:
            pass

    raise RuntimeError("Radar device not found.")


def connect_and_resolve(bus: dbus.SystemBus, device: str, timeout: float) -> None:
    print_header("Connect")
    dev_iface = dbus.Interface(bus.get_object(BLUEZ_SERVICE, device), DEVICE_IFACE)
    props = get_all(bus, device, DEVICE_IFACE)
    if not bool(props.get("Connected", False)):
        print("calling Device1.Connect")
        dev_iface.Connect()
    else:
        print("device already connected")

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        props = get_all(bus, device, DEVICE_IFACE)
        print(
            f"  connected={props.get('Connected')} services_resolved={props.get('ServicesResolved')}",
            end="\r",
        )
        if bool(props.get("ServicesResolved", False)):
            print()
            return
        time.sleep(0.2)
    print()
    raise RuntimeError("Timed out waiting for ServicesResolved.")


def find_gatt(objects: dict[str, dict[str, dict[str, Any]]], device: str, args: argparse.Namespace) -> tuple[str, str, Optional[str]]:
    print_header("GATT")
    service_path: Optional[str] = None
    for path, ifaces in objects.items():
        if not path.startswith(f"{device}/") or GATT_SERVICE_IFACE not in ifaces:
            continue
        uuid = normalize_uuid(str(ifaces[GATT_SERVICE_IFACE].get("UUID", "")))
        print(f"service {path} uuid={uuid}")
        if uuid == normalize_uuid(args.service_uuid):
            service_path = path

    if service_path is None:
        raise RuntimeError(f"Service {args.service_uuid} not found.")

    notify_path: Optional[str] = None
    health_path: Optional[str] = None
    for path, ifaces in objects.items():
        if not path.startswith(f"{service_path}/") or GATT_CHAR_IFACE not in ifaces:
            continue
        props = ifaces[GATT_CHAR_IFACE]
        uuid = normalize_uuid(str(props.get("UUID", "")))
        flags = [str(flag) for flag in props.get("Flags", [])]
        print(f"char {path} uuid={uuid} flags={flags} notifying={props.get('Notifying')}")
        if uuid == normalize_uuid(args.notify_uuid):
            notify_path = path
        if uuid == normalize_uuid(args.health_uuid):
            health_path = path

    if notify_path is None:
        raise RuntimeError(f"Notify characteristic {args.notify_uuid} not found.")
    return service_path, notify_path, health_path


def read_char_value(bus: dbus.SystemBus, path: str) -> bytes:
    props = dbus.Interface(bus.get_object(BLUEZ_SERVICE, path), DBUS_PROPERTIES)
    return dbus_bytes(props.Get(GATT_CHAR_IFACE, "Value"))


def read_char_readvalue(bus: dbus.SystemBus, path: str) -> bytes:
    char = dbus.Interface(bus.get_object(BLUEZ_SERVICE, path), GATT_CHAR_IFACE)
    return dbus_bytes(char.ReadValue(dbus.Dictionary({}, signature="sv")))


def start_notify(bus: dbus.SystemBus, path: str) -> None:
    char = dbus.Interface(bus.get_object(BLUEZ_SERVICE, path), GATT_CHAR_IFACE)
    char.StartNotify()


def stop_notify(bus: dbus.SystemBus, path: str) -> None:
    char = dbus.Interface(bus.get_object(BLUEZ_SERVICE, path), GATT_CHAR_IFACE)
    try:
        char.StopNotify()
    except Exception:
        pass


def run_stream_test(
    bus: dbus.SystemBus,
    notify_path: str,
    health_path: Optional[str],
    args: argparse.Namespace,
) -> tuple[StreamStats, Optional[StreamStats]]:
    print_header("Stream Test")
    notify_stats = StreamStats("notify")
    health_stats = StreamStats("health") if health_path else None
    loop = GLib.MainLoop()
    stop_requested = {"value": False}

    def on_stop(*_args: object) -> None:
        stop_requested["value"] = True
        loop.quit()

    def on_notify_props(interface: str, changed: dict[str, Any], _invalidated: list[str], path: str = "") -> None:
        if interface != GATT_CHAR_IFACE or "Value" not in changed:
            return
        notify_stats.observe("signal", dbus_bytes(changed["Value"]), args.verbose)

    def on_health_props(interface: str, changed: dict[str, Any], _invalidated: list[str], path: str = "") -> None:
        if health_stats is None or interface != GATT_CHAR_IFACE or "Value" not in changed:
            return
        health_stats.observe("signal", dbus_bytes(changed["Value"]), args.verbose)

    notify_match = bus.add_signal_receiver(
        on_notify_props,
        dbus_interface=DBUS_PROPERTIES,
        signal_name="PropertiesChanged",
        path=notify_path,
        path_keyword="path",
    )
    health_match = None
    if health_path and args.health_notify_phase:
        health_match = bus.add_signal_receiver(
            on_health_props,
            dbus_interface=DBUS_PROPERTIES,
            signal_name="PropertiesChanged",
            path=health_path,
            path_keyword="path",
        )

    def poll() -> bool:
        if stop_requested["value"]:
            return False
        try:
            notify_stats.observe("property", read_char_value(bus, notify_path), args.verbose)
        except Exception as exc:  # noqa: BLE001
            print(f"[notify:property-error] {exc}")
        return True

    def readvalue_poll() -> bool:
        if stop_requested["value"]:
            return False
        try:
            notify_stats.observe("readvalue", read_char_readvalue(bus, notify_path), args.verbose)
        except Exception as exc:  # noqa: BLE001
            print(f"[notify:readvalue-error] {exc}")
        if health_path and health_stats is not None:
            try:
                health_stats.observe("readvalue", read_char_readvalue(bus, health_path), args.verbose)
            except Exception as exc:  # noqa: BLE001
                print(f"[health:readvalue-error] {exc}")
        return True

    GLib.timeout_add(int(args.property_poll_seconds * 1000), poll)
    GLib.timeout_add(int(args.readvalue_poll_seconds * 1000), readvalue_poll)
    GLib.timeout_add(int(args.duration * 1000), on_stop)

    print(f"StartNotify notify={notify_path}")
    start_notify(bus, notify_path)
    try:
        print(f"notify Notifying={get_prop(bus, notify_path, GATT_CHAR_IFACE, 'Notifying')}")
    except Exception:
        pass

    if health_path and args.health_notify_phase:
        print(f"StartNotify health={health_path}")
        try:
            start_notify(bus, health_path)
            print(f"health Notifying={get_prop(bus, health_path, GATT_CHAR_IFACE, 'Notifying')}")
        except Exception as exc:  # noqa: BLE001
            print(f"[health:start-notify-error] {exc}")

    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            signal.signal(sig, lambda *_args: on_stop())
        except Exception:
            pass

    loop.run()

    stop_notify(bus, notify_path)
    if health_path and args.health_notify_phase:
        stop_notify(bus, health_path)
    notify_match.remove()
    if health_match is not None:
        health_match.remove()

    return notify_stats, health_stats


def print_summary(notify: StreamStats, health: Optional[StreamStats]) -> int:
    print_header("Summary")
    print(
        "notify: "
        f"frames={notify.frames} changed={notify.changed} first_seq={notify.first_seq} "
        f"last_seq={notify.last_seq} seq_delta={notify.seq_delta} drops={notify.drops} "
        f"sources={notify.source_counts}"
    )
    if health is not None:
        print(
            "health: "
            f"frames={health.frames} changed={health.changed} first_seq={health.first_seq} "
            f"last_seq={health.last_seq} sources={health.source_counts}"
        )

    if notify.seq_delta > 2:
        print("VERDICT: PASS - radar data advances on Linux BlueZ.")
        return 0

    if notify.changed > 1:
        print("VERDICT: PARTIAL - payload changes but seq does not advance. Check firmware payload schema.")
        return 1

    print("VERDICT: FAIL - BlueZ connection works, but radar data does not advance after initial value.")
    print("Next checks:")
    print("  - Run this script with --health-notify-phase to see if a second notify subscription breaks data.")
    print("  - Run the docs/randar_bluetooth.md Bleak example on the same Linux host.")
    print("  - Capture btmon while running this script to confirm whether ATT Handle Value Notification packets arrive.")
    return 2


def parse_args() -> argparse.Namespace:
    config = load_radar_config(Path("config.toml"))
    parser = argparse.ArgumentParser(description="Diagnose EVADAR-C6 radar over Linux BlueZ D-Bus")
    parser.add_argument("--adapter", default=None, help="Adapter name, for example hci0")
    parser.add_argument("--address", default=config.get("address", ""))
    parser.add_argument("--name", default=config.get("device_name", DEFAULT_NAME))
    parser.add_argument("--service-uuid", default=config.get("service_uuid", DEFAULT_SERVICE_UUID))
    parser.add_argument("--notify-uuid", default=config.get("notify_uuid", DEFAULT_NOTIFY_UUID))
    parser.add_argument("--health-uuid", default=config.get("health_uuid", DEFAULT_HEALTH_UUID))
    parser.add_argument("--by-service", action="store_true", default=config.get("match_by_service", "true").lower() == "true")
    parser.add_argument("--scan-seconds", type=float, default=float(config.get("scan_timeout_seconds", "12")))
    parser.add_argument("--services-timeout", type=float, default=float(config.get("services_timeout_seconds", "10")))
    parser.add_argument("--duration", type=float, default=12.0)
    parser.add_argument("--property-poll-seconds", type=float, default=0.2)
    parser.add_argument("--readvalue-poll-seconds", type=float, default=1.0)
    parser.add_argument("--health-notify-phase", action="store_true", help="Also StartNotify on health characteristic during the test")
    parser.add_argument("--verbose", action="store_true", help="Print each observed payload")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    bus = dbus.SystemBus()

    print_header("Adapter")
    objects = managed_objects(bus)
    adapter = select_adapter(objects, args.adapter)
    set_prop(bus, adapter, ADAPTER_IFACE, "Powered", dbus.Boolean(True))
    print_adapter(bus, adapter)

    device = discover(bus, adapter, args)
    connect_and_resolve(bus, device, args.services_timeout)
    objects = managed_objects(bus)
    print_device(objects, device)
    _service, notify_path, health_path = find_gatt(objects, device, args)

    notify_stats, health_stats = run_stream_test(bus, notify_path, health_path, args)
    return print_summary(notify_stats, health_stats)


if __name__ == "__main__":
    raise SystemExit(main())
