# EV-ADS BLE 协议（ESP32-C6 ↔ RK3588）

## 1. 设备发现
- **Local Name** / **Advertising Name**：`EVADAR-C6`
- 主 Service UUID 写入 Adv，便于扫描时直接过滤。
- Connection Interval：固件请求 12–24 (15–30 ms)，supervision timeout 200 (2 s)。

## 2. GATT 结构

| 名称 | UUID | 属性 | 说明 |
|------|------|------|------|
| Vital Service | `0000ad01-0000-1000-8000-00805f9b34fb` | — | 雷达根服务 |
| Notify Char   | `0000ad02-0000-1000-8000-00805f9b34fb` | READ + NOTIFY | 雷达 JSON 数据，5–10 Hz |
| Config Char   | `0000ad03-0000-1000-8000-00805f9b34fb` | READ + WRITE  | 主机下发命令（可选） |
| Health Char   | `0000ad04-0000-1000-8000-00805f9b34fb` | READ + NOTIFY | 固件健康 JSON，2 s |

> 注：UUID 与 `firmware/esp32c6_mmwave_ble/include/project_config.h` 严格一致，修改时**同步更新两侧**。

## 3. Notify 数据格式（v1 调试版）

紧凑 JSON，每帧一条 ASCII 字符串，**不带换行**：

```json
{"v":1,"seq":12,"t":34567,"br":16.20,"hr":78.40,"d":52.10,"st":7}
```

| 字段 | 类型 | 含义 |
|------|------|------|
| `v`  | int  | 协议版本，当前 `1` |
| `seq`| u32  | ESP32-C6 自增帧号；用于 RK3588 端推断丢帧 |
| `t`  | u32  | ESP32-C6 `millis()`；用于估计端到端延迟 |
| `br` | float | 呼吸率 BPM；`st & 0x01` 为 1 时有效 |
| `hr` | float | 心率 BPM；`st & 0x02` 为 1 时有效 |
| `d`  | float | 距离 cm；`st & 0x04` 为 1 时有效 |
| `st` | u8   | 状态位 bitmask |

### 状态位 `st`
| bit | 名称       | 含义 |
|-----|------------|------|
| 0   | `BREATH`   | 本帧呼吸率有效 |
| 1   | `HEART`    | 本帧心率有效 |
| 2   | `DISTANCE` | 本帧距离有效 |
| 3   | `PRESENCE` | 本帧人体被检测到 |
| 4-7 | 保留       | 必须填 0 |

未带有效位的字段保留上一帧值，便于解析端简化逻辑，但 **必须** 配合 `st` 判断是否信任。

## 4. Health 数据格式

```json
{"v":1,"up":12345,"nt":120,"nd":3,"rs":0,"cn":1,"fw":"0.1.0"}
```

| 字段 | 类型 | 含义 |
|------|------|------|
| `up` | u32 | 固件 uptime ms |
| `nt` | u32 | 累计 Notify 帧数 |
| `nd` | u32 | 因未连接被丢弃的帧数 |
| `rs` | u32 | 距离上一次雷达成功 update 的毫秒数 |
| `cn` | u8  | 当前是否有 BLE 客户端 (0/1) |
| `fw` | str | 固件版本 |

## 5. Config 写入命令（保留扩展位）

主机向 Config Char 写入 UTF-8 字符串，固件按行解析：
```json
{"cmd":"ping"}
{"cmd":"notify_rate","hz":10}
{"cmd":"led","r":0,"g":125,"b":0}
```
当前固件只打印不执行，**预留协议槽位**给未来版本。

## 6. 节流策略
- 雷达驱动 `mmWave.update(100)` 阻塞 ≤100 ms。
- 主循环每 `EVADAR_NOTIFY_MIN_INTERVAL_MS`(100 ms) 触发一次 Notify，
  无新数据时沿用上一帧值，但 `st` 中相应有效位置 0。
- 实测速率约 **8–10 Hz**。

## 7. 错误恢复
- BLE 断开：固件立即重新广播。
- 雷达 UART 无数据：`rs` 增大，串口打印 `[mmWave] waiting for data...`，但 Notify 仍按节流发送（用于探活）。
- 主机端建议 `stale_ms > 500` 标记 STALE，> 3000 标记 DISCONNECTED。

## 8. 版本演进
| version | 状态 | 变更 |
|---------|------|------|
| `v=1`  | 当前  | 紧凑 JSON 调试版 |
| `v=2`  | 计划  | 二进制 12 字节定长帧；保留 JSON 作为调试模式 |


## python示例
``` python

#!/usr/bin/env python3
"""订阅 EVADAR-C6 的 Notify Characteristic，打印每帧雷达 JSON。

用法:
    python3 ble_subscribe_mmwave.py --name EVADAR-C6
    python3 ble_subscribe_mmwave.py --address AA:BB:CC:DD:EE:FF
    python3 ble_subscribe_mmwave.py --name EVADAR-C6 --pretty
    python3 ble_subscribe_mmwave.py --list                 # 只扫描列表
    python3 ble_subscribe_mmwave.py --by-service           # 按 Service UUID 匹配（最稳）
"""
import argparse
import asyncio
import json
import signal
import sys
import time
from typing import Optional
a
try:
    from bleak import BleakClient, BleakScanner
except ImportError:
    sys.stderr.write("[ble_subscribe] 需要 bleak: pip install bleak\n")
    sys.exit(1)

SERVICE_UUID = "0000ad01-0000-1000-8000-00805f9b34fb"
NOTIFY_UUID = "0000ad02-0000-1000-8000-00805f9b34fb"
HEALTH_UUID = "0000ad04-0000-1000-8000-00805f9b34fb"


def _decode(data: bytearray) -> str:
    try:
        return data.decode("utf-8", errors="replace").strip()
    except Exception:
        return repr(bytes(data))


def _matches(device, adv, name: Optional[str], by_service: bool) -> bool:
    """name / advertisement.local_name / service UUID 任一命中即匹配。

    默认: name 命中 *或* Service UUID 命中 都算 hit
          —— 解决 macOS 把旧名字 (例如 'MMRADAR') 缓存住，bleak 看不到新名 'EVADAR-C6' 的问题
    --by-service: 只信 Service UUID，忽略名字
    """
    try:
        uuids = [u.lower() for u in (adv.service_uuids or [])]
    except Exception:
        uuids = []
    uuid_hit = SERVICE_UUID.lower() in uuids

    if by_service:
        return uuid_hit
    if name:
        wanted = name.strip()
        if (device.name or "").strip() == wanted:
            return True
        # macOS 经常把名字放在 scan response 里
        local = getattr(adv, "local_name", None) or ""
        if local.strip() == wanted:
            return True
    # 兜底：即便名字没匹配上，但 Service UUID 是我们的，也认
    return uuid_hit


async def scan_list(seconds: float) -> int:
    print(f"[ble] listing all devices for {seconds:.1f}s ...")
    seen = {}

    def _cb(device, adv):
        info = seen.setdefault(device.address, {
            "name": None, "local_name": None,
            "rssi": None, "uuids": set(),
        })
        if device.name and not info["name"]:
            info["name"] = device.name
        ln = getattr(adv, "local_name", None)
        if ln and not info["local_name"]:
            info["local_name"] = ln
        try:
            for u in (adv.service_uuids or []):
                info["uuids"].add(u.lower())
        except Exception:
            pass
        rssi = getattr(adv, "rssi", None) or getattr(device, "rssi", None)
        info["rssi"] = rssi

    scanner = BleakScanner(detection_callback=_cb)
    await scanner.start()
    await asyncio.sleep(seconds)
    await scanner.stop()
    print(f"[ble] found {len(seen)} device(s):")
    for addr, info in sorted(seen.items()):
        hit = "  *EVADAR*" if (
            info["name"] == "EVADAR-C6" or info["local_name"] == "EVADAR-C6"
            or SERVICE_UUID.lower() in info["uuids"]
        ) else ""
        print(f"  - {addr}  name={info['name']!r:<20}  "
              f"local_name={info['local_name']!r:<20}  "
              f"rssi={info['rssi']}{hit}")
        if info["uuids"]:
            print(f"      uuids={sorted(info['uuids'])}")
    return 0


async def _find_device(name: Optional[str], address: Optional[str],
                       by_service: bool, timeout: float) -> Optional[str]:
    if address:
        return address
    desc = "service UUID" if by_service else f"name={name!r}"
    print(f"[ble] scanning for {desc} (timeout {timeout:.1f}s) ...")
    found: dict = {}

    def _cb(device, adv):
        if device.address in found:
            return
        if _matches(device, adv, name, by_service):
            found[device.address] = (device, adv)

    scanner = BleakScanner(detection_callback=_cb)
    await scanner.start()
    t0 = time.time()
    try:
        while time.time() - t0 < timeout and not found:
            await asyncio.sleep(0.2)
    finally:
        await scanner.stop()
    if not found:
        return None
    addr, (device, adv) = next(iter(found.items()))
    print(f"[ble] hit: {addr}  name={device.name!r}  "
          f"local_name={getattr(adv, 'local_name', None)!r}")
    return addr


async def subscribe(name: Optional[str], address: Optional[str],
                    pretty: bool, with_health: bool, by_service: bool,
                    scan_timeout: float) -> int:
    addr = await _find_device(name, address, by_service, scan_timeout)
    if addr is None:
        sys.stderr.write(
            f"[ble] device not found.\n"
            f"  尝试列出所有可见设备:\n"
            f"    python3 {sys.argv[0]} --list\n"
            f"  或按 Service UUID 匹配（最稳）:\n"
            f"    python3 {sys.argv[0]} --by-service --pretty\n"
            f"  常见原因 (macOS):\n"
            f"   1) 终端没有蓝牙权限 → 系统设置 > 隐私与安全性 > 蓝牙，勾选 Terminal/iTerm\n"
            f"   2) ESP32 没在广播 → 看串口是否打印 [BLE] advertising as EVADAR-C6\n"
            f"   3) macOS BLE 缓存了旧名字 → 关蓝牙再开，或重启 bluetoothd\n"
        )
        return 2
    print(f"[ble] connecting to {addr} ...")

    stats = {"n": 0, "last_seq": None, "drops": 0, "errs": 0, "t0": time.time()}

    def _on_notify(_handle, data: bytearray) -> None:
        line = _decode(data)
        stats["n"] += 1
        if pretty:
            try:
                obj = json.loads(line)
                seq = obj.get("seq")
                if stats["last_seq"] is not None and seq is not None:
                    gap = seq - stats["last_seq"] - 1
                    if gap > 0:
                        stats["drops"] += gap
                stats["last_seq"] = seq
                st = obj.get("st", 0)
                flags = []
                if st & 0x01: flags.append("BR")
                if st & 0x02: flags.append("HR")
                if st & 0x04: flags.append("D")
                if st & 0x08: flags.append("P")
                hz = stats["n"] / max(time.time() - stats["t0"], 1e-3)
                print(f"seq={seq:>5}  br={obj.get('br'):>5}  "
                      f"hr={obj.get('hr'):>5}  d={obj.get('d'):>6}  "
                      f"st={st:#04x} [{','.join(flags) or '-'}]  "
                      f"rate~{hz:5.2f}Hz drops={stats['drops']}")
            except Exception as e:
                stats["errs"] += 1
                print(f"[parse-err] {e}: {line!r}")
        else:
            print(line)

    def _on_health(_handle, data: bytearray) -> None:
        print(f"[health] {_decode(data)}")

    async with BleakClient(addr) as client:
        print(f"[ble] connected. discovering services ...")
        try:
            services = client.services
            for svc in services:
                if svc.uuid.lower() == SERVICE_UUID.lower():
                    print(f"[ble] found service {svc.uuid}")
        except Exception:
            pass

        await client.start_notify(NOTIFY_UUID, _on_notify)
        print(f"[ble] subscribed notify {NOTIFY_UUID}")
        if with_health:
            try:
                await client.start_notify(HEALTH_UUID, _on_health)
                print(f"[ble] subscribed health {HEALTH_UUID}")
            except Exception as e:
                print(f"[ble] health subscribe failed: {e}")

        stop = asyncio.Event()
        loop = asyncio.get_running_loop()
        for sig in (signal.SIGINT, signal.SIGTERM):
            try:
                loop.add_signal_handler(sig, stop.set)
            except NotImplementedError:
                pass
        await stop.wait()

        try:
            await client.stop_notify(NOTIFY_UUID)
            if with_health:
                await client.stop_notify(HEALTH_UUID)
        except Exception:
            pass

    print(f"[ble] disconnected. total frames={stats['n']} drops={stats['drops']} "
          f"parse_err={stats['errs']}")
    return 0


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--name", default="EVADAR-C6")
    p.add_argument("--address", default=None, help="直接指定 BLE 地址")
    p.add_argument("--pretty", action="store_true", help="按字段格式化打印")
    p.add_argument("--no-health", action="store_true", help="不订阅 health char")
    p.add_argument("--by-service", action="store_true",
                   help="按 Service UUID 匹配，忽略名字（macOS 最稳）")
    p.add_argument("--list", action="store_true",
                   help="只扫描并列出所有可见 BLE 设备，不连接")
    p.add_argument("--scan-timeout", type=float, default=12.0)
    args = p.parse_args()
    try:
        if args.list:
            return asyncio.run(scan_list(args.scan_timeout))
        return asyncio.run(subscribe(
            args.name, args.address,
            args.pretty, not args.no_health, args.by_service,
            args.scan_timeout,
        ))
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    sys.exit(main())

```