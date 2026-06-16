#!/usr/bin/env python3
"""Stream EVADAR-C6 BLE radar notifications as JSON lines."""

from __future__ import annotations

import argparse
import asyncio
import json
import signal
import sys
import time
from typing import Any

try:
    from bleak import BleakClient, BleakScanner
except ImportError:
    sys.stderr.write("bleak is required: python3 -m pip install bleak\n")
    sys.exit(12)


def emit(event_type: str, **fields: Any) -> None:
    payload = {"type": event_type}
    payload.update(fields)
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def decode_json(data: bytearray) -> Any:
    text = bytes(data).decode("utf-8", errors="replace").strip()
    return json.loads(text)


def normalize_uuid(value: str | None) -> str:
    return (value or "").strip().lower()


def matches_device(device: Any, adv: Any, args: argparse.Namespace) -> bool:
    uuids = [normalize_uuid(uuid) for uuid in getattr(adv, "service_uuids", None) or []]
    uuid_hit = normalize_uuid(args.service_uuid) in uuids
    if args.by_service:
        return uuid_hit

    wanted_name = (args.name or "").strip()
    if wanted_name:
        device_name = (getattr(device, "name", None) or "").strip()
        local_name = (getattr(adv, "local_name", None) or "").strip()
        if device_name == wanted_name or local_name == wanted_name:
            return True

    return uuid_hit


async def find_device(args: argparse.Namespace) -> str | None:
    if args.address:
        return args.address

    found: dict[str, tuple[Any, Any]] = {}
    emit(
        "state",
        phase="python_scanning",
        deviceName=args.name,
        deviceAddress=None,
        message=f"scan timeout {args.scan_timeout:.1f}s",
    )

    def on_detection(device: Any, adv: Any) -> None:
        if device.address in found:
            return
        if matches_device(device, adv, args):
            found[device.address] = (device, adv)

    scanner = BleakScanner(detection_callback=on_detection)
    await scanner.start()
    started_at = time.monotonic()
    try:
        while time.monotonic() - started_at < args.scan_timeout and not found:
            await asyncio.sleep(0.2)
    finally:
        await scanner.stop()

    if not found:
        return None

    address, (device, adv) = next(iter(found.items()))
    emit(
        "state",
        phase="python_discovered",
        deviceName=getattr(device, "name", None) or getattr(adv, "local_name", None) or args.name,
        deviceAddress=address,
        message="matched BLE advertisement",
    )
    return address


async def stream(args: argparse.Namespace) -> int:
    address = await find_device(args)
    if not address:
        emit(
            "state",
            phase="python_not_found",
            deviceName=args.name,
            deviceAddress=None,
            message="radar BLE device not found",
        )
        return 2

    stop = asyncio.Event()

    def on_disconnect(_: Any) -> None:
        emit(
            "state",
            phase="python_disconnected",
            deviceName=args.name,
            deviceAddress=address,
            message="BLE client disconnected",
        )
        stop.set()

    def on_notify(_: Any, data: bytearray) -> None:
        try:
            emit("frame", payload=decode_json(data))
        except Exception as exc:
            emit(
                "state",
                phase="python_parse_error",
                deviceName=args.name,
                deviceAddress=address,
                message=f"notify parse failed: {exc}",
            )

    def on_health(_: Any, data: bytearray) -> None:
        try:
            emit("health", payload=decode_json(data))
        except Exception as exc:
            emit(
                "state",
                phase="python_parse_error",
                deviceName=args.name,
                deviceAddress=address,
                message=f"health parse failed: {exc}",
            )

    emit(
        "state",
        phase="python_connecting",
        deviceName=args.name,
        deviceAddress=address,
        message="connecting GATT",
    )

    async with BleakClient(
        address,
        timeout=args.connect_timeout,
        disconnected_callback=on_disconnect,
    ) as client:
        emit(
            "state",
            phase="python_connected",
            deviceName=args.name,
            deviceAddress=address,
            message="notifications subscribed",
        )
        await client.start_notify(args.notify_uuid, on_notify)

        if args.health_uuid and not args.no_health:
            try:
                await client.start_notify(args.health_uuid, on_health)
            except Exception as exc:
                emit(
                    "state",
                    phase="python_health_unavailable",
                    deviceName=args.name,
                    deviceAddress=address,
                    message=str(exc),
                )

        loop = asyncio.get_running_loop()
        for sig in (signal.SIGINT, signal.SIGTERM):
            try:
                loop.add_signal_handler(sig, stop.set)
            except NotImplementedError:
                pass

        await stop.wait()

        try:
            await client.stop_notify(args.notify_uuid)
            if args.health_uuid and not args.no_health:
                await client.stop_notify(args.health_uuid)
        except Exception:
            pass

    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--jsonl", action="store_true", help="kept for explicit C# process protocol")
    parser.add_argument("--name", default="EVADAR-C6")
    parser.add_argument("--address", default=None)
    parser.add_argument("--service-uuid", required=True)
    parser.add_argument("--notify-uuid", required=True)
    parser.add_argument("--health-uuid", default="")
    parser.add_argument("--by-service", action="store_true")
    parser.add_argument("--no-health", action="store_true")
    parser.add_argument("--scan-timeout", type=float, default=12.0)
    parser.add_argument("--connect-timeout", type=float, default=10.0)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        return asyncio.run(stream(args))
    except KeyboardInterrupt:
        return 0
    except Exception as exc:
        emit(
            "state",
            phase="python_error",
            deviceName=args.name,
            deviceAddress=args.address,
            message=str(exc),
        )
        return 1


if __name__ == "__main__":
    sys.exit(main())
