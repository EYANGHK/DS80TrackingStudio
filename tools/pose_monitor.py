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
import selectors
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

    if args.seconds <= 0:
        ap.error("--seconds 必须大于 0")

    counts = {"Free-D": 0, "JSON": 0}
    bad = {"Free-D": 0, "JSON": 0}
    last_print = {"Free-D": 0.0, "JSON": 0.0}
    sockets = []
    with selectors.DefaultSelector() as selector:
        try:
            for name, port in (("Free-D", args.freed_port), ("JSON", args.json_port)):
                sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                sockets.append(sock)
                sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
                sock.bind(("127.0.0.1", port))
                sock.setblocking(False)
                selector.register(sock, selectors.EVENT_READ, name)

            print(f"监听 Free-D :{args.freed_port} / JSON :{args.json_port}，{args.seconds:.0f} 秒…")
            start = time.perf_counter()
            deadline = start + args.seconds
            while (remaining := deadline - time.perf_counter()) > 0:
                for key, _ in selector.select(timeout=min(remaining, 0.1)):
                    name = key.data
                    # 有界排空，某一路关闭或持续高流量不会阻塞另一路。
                    for _ in range(256):
                        if time.perf_counter() >= deadline:
                            break
                        try:
                            data, _ = key.fileobj.recvfrom(65535)
                        except BlockingIOError:
                            break
                        try:
                            parsed = decode_freed(data) if name == "Free-D" else json.loads(data.decode("utf-8"))
                            if not isinstance(parsed, dict):
                                raise ValueError("invalid packet")
                        except (ValueError, UnicodeDecodeError):
                            bad[name] += 1
                            continue
                        counts[name] += 1
                        now = time.perf_counter()
                        if now - last_print[name] >= 1.0:
                            last_print[name] = now
                            if name == "Free-D":
                                print("Free-D  pan={pan:8.3f} tilt={tilt:8.3f} roll={roll:8.3f}  "
                                      "pos=({x:7.3f},{y:7.3f},{z:7.3f}) m".format(**parsed))
                            else:
                                print(f"JSON    seq={parsed.get('seq')} valid={parsed.get('valid')} "
                                      f"conf={parsed.get('conf')} age={parsed.get('age')}ms")
            duration = time.perf_counter() - start
        finally:
            for sock in sockets:
                sock.close()

    print()
    for name in counts:
        print(f"{name}: {counts[name]} 帧，错误 {bad[name]}，平均 {counts[name] / duration:.1f} Hz")
    if any(counts.values()):
        print("已收到输出；未收到数据的通道可能未启用，请按配置核对。")
    else:
        print("没有收到数据。请确认 Bridge 已连接，并检查 IP / 端口。")


if __name__ == "__main__":
    main()
