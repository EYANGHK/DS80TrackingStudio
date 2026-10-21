#!/usr/bin/env python3
"""
DS80 Tracking Studio 输出监视器。

同时监听：
  - Free-D UDP 端口（默认 40000）
  - JSON UDP 端口（默认 40001）

用途：
  * 在没有 UE 的机器上验证 DS80 Bridge 是否在正常发送
  * 检查 Free-D 报文校验和、位置/角度是否符合预期
  * 排查坐标轴方向

用法：
  python pose_monitor.py [--freed-port 40000] [--json-port 40001] [--seconds 10]
"""

import argparse
import json
import socket
import struct
import sys
import time

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def decode_freed(packet: bytes):
    """按 Vinten free-d D1 报文（29 字节）解码。"""
    if len(packet) != 29 or packet[0] != 0xD1:
        return None

    checksum = (0x40 - sum(packet[:28])) % 256
    if checksum != packet[28]:
        return None

    def s24(off):
        v = (packet[off] << 16) | (packet[off + 1] << 8) | packet[off + 2]
        if v & 0x800000:
            v -= 1 << 24
        return v

    return {
        "id": packet[1],
        "pan": s24(2) / 32768.0,
        "tilt": s24(5) / 32768.0,
        "roll": s24(8) / 32768.0,
        "x": s24(11) / 64.0 / 1000.0,
        "y": s24(14) / 64.0 / 1000.0,
        "z": s24(17) / 64.0 / 1000.0,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--freed-port", type=int, default=40000)
    ap.add_argument("--json-port", type=int, default=40001)
    ap.add_argument("--seconds", type=float, default=10.0)
    args = ap.parse_args()

    freed = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    freed.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    freed.bind(("127.0.0.1", args.freed_port))
    freed.settimeout(0.5)

    js = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    js.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    js.bind(("127.0.0.1", args.json_port))
    js.settimeout(0.5)

    print(f"监听 Free-D :{args.freed_port} / JSON :{args.json_port}，{args.seconds:.0f} 秒…")
    freed_count = json_count = freed_bad = 0
    last_print = 0.0
    start = time.time()

    while time.time() - start < args.seconds:
        try:
            data, _ = freed.recvfrom(2048)
            parsed = decode_freed(data)
            if parsed is None:
                freed_bad += 1
            else:
                freed_count += 1
                if time.time() - last_print > 1.0:
                    last_print = time.time()
                    print(
                        "Free-D  pan={pan:8.3f} tilt={tilt:8.3f} roll={roll:8.3f}  "
                        "pos=({x:7.3f},{y:7.3f},{z:7.3f}) m".format(**parsed)
                    )
        except socket.timeout:
            pass

        try:
            data, _ = js.recvfrom(4096)
            obj = json.loads(data.decode("utf-8"))
            json_count += 1
            if json_count % 120 == 0:
                print(
                    f"JSON    seq={obj.get('seq')} valid={obj.get('valid')} "
                    f"conf={obj.get('conf')} age={obj.get('age')}ms"
                )
        except socket.timeout:
            pass

    print()
    print(f"统计：Free-D {freed_count} 帧（校验失败 {freed_bad}），JSON {json_count} 帧")
    if freed_count and json_count:
        rate = freed_count / args.seconds
        print(f"平均 Free-D 速率 ≈ {rate:.1f} Hz")
        print("结论：Bridge 输出正常。下一步可在 UE5 中添加 LiveLinkFreeD Source（端口 40000）。")
    else:
        print("结论：没有收到数据。请确认 DS80 Bridge 已点击“连接”，并检查 IP / 端口。")


if __name__ == "__main__":
    main()
