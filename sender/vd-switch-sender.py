#!/usr/bin/env python3
"""
vd-switch foot switch sender
Reads key a/b/c from a foot switch and sends IPv6 link-local multicast
packets to vd-switch.exe listeners on the local network.

Keys a/b/c map to actions decided by the receiver.
"""

import argparse
import socket
import struct
import sys
import time

try:
    import evdev
    from evdev import ecodes
except ImportError:
    print("evdev not installed. Run: pip install --break-system-packages evdev", file=sys.stderr)
    sys.exit(1)

MULTICAST_ADDR = 'ff12::7664:7377'
PORT = 5356
RETRY_INTERVAL = 5  # seconds between retries when device is unavailable

TARGET_KEYS = {
    ecodes.KEY_A: b'a',
    ecodes.KEY_B: b'b',
    ecodes.KEY_C: b'c',
}


def find_link_local_interface():
    """Return the first interface name that has an fe80:: address."""
    try:
        with open('/proc/net/if_inet6') as f:
            for line in f:
                parts = line.split()
                if len(parts) >= 6 and parts[0].startswith('fe80'):
                    return parts[5]
    except OSError:
        pass
    return None


def open_socket(ifname):
    ifindex = socket.if_nametoindex(ifname)
    sock = socket.socket(socket.AF_INET6, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_MULTICAST_HOPS, 1)
    sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_MULTICAST_IF, struct.pack('@I', ifindex))
    return sock, ifindex


def find_devices():
    """Return input devices that have KEY_A, KEY_B, KEY_C in their capabilities."""
    result = []
    for path in evdev.list_devices():
        try:
            dev = evdev.InputDevice(path)
            keys = dev.capabilities().get(ecodes.EV_KEY, [])
            if all(k in keys for k in (ecodes.KEY_A, ecodes.KEY_B, ecodes.KEY_C)):
                result.append(dev)
            else:
                dev.close()
        except (OSError, PermissionError):
            pass
    return result


def try_open_device(args):
    """Try to open the input device. Returns the device or None if not available."""
    if args.device:
        try:
            return evdev.InputDevice(args.device)
        except (OSError, PermissionError):
            return None
    else:
        devices = find_devices()
        if not devices:
            return None
        if len(devices) > 1:
            # Multiple candidates: hard error, user must specify --device
            print("Multiple candidate devices found. Specify one with --device:", file=sys.stderr)
            for dev in devices:
                print(f"  {dev.path}  {dev.name}", file=sys.stderr)
            sys.exit(1)
        return devices[0]


def main():
    parser = argparse.ArgumentParser(
        description='Send vd-switch commands via IPv6 multicast from a foot switch'
    )
    parser.add_argument('--device', '-d', metavar='PATH',
                        help='Input device (e.g. /dev/input/event3). Auto-detected if omitted.')
    parser.add_argument('--grab', '-g', action='store_true',
                        help='Exclusively grab the device (suppresses key events in other apps)')
    parser.add_argument('--interface', '-i', metavar='IFNAME',
                        help='Network interface. Auto-detected (first fe80::) if omitted.')
    parser.add_argument('--list', '-l', action='store_true',
                        help='List candidate input devices and exit')
    args = parser.parse_args()

    # ── device listing ──────────────────────────────────────────────────────
    if args.list:
        devices = find_devices()
        if not devices:
            print("No devices with KEY_A/B/C found (permission issue?)")
        else:
            print("Candidate devices:")
            for dev in devices:
                print(f"  {dev.path}  {dev.name}")
        return

    # ── network interface ───────────────────────────────────────────────────
    ifname = args.interface or find_link_local_interface()
    if not ifname:
        print("No interface with fe80:: address found. Use --interface.", file=sys.stderr)
        sys.exit(1)

    sock, ifindex = open_socket(ifname)
    dest = (MULTICAST_ADDR, PORT, 0, ifindex)
    print(f"Interface: {ifname}")
    print(f"Multicast: [{MULTICAST_ADDR}%{ifname}]:{PORT}")

    # ── main loop with retry ─────────────────────────────────────────────────
    try:
        while True:
            device = try_open_device(args)
            if device is None:
                label = args.device if args.device else 'suitable device'
                print(f"Waiting for {label} (retry every {RETRY_INTERVAL}s)...")
                time.sleep(RETRY_INTERVAL)
                continue

            print(f"Device   : {device.path} ({device.name})")
            if args.grab:
                device.grab()
                print("Device grabbed (keys suppressed in other apps)")
            print("Listening for keys a / b / c ...")

            try:
                for event in device.read_loop():
                    if event.type != ecodes.EV_KEY:
                        continue
                    key_event = evdev.categorize(event)
                    if key_event.keystate != evdev.KeyEvent.key_down:
                        continue
                    payload = TARGET_KEYS.get(key_event.scancode)
                    if payload is None:
                        continue
                    sock.sendto(payload, dest)
                    print(f"  sent: {payload.decode()}")
            except OSError as e:
                print(f"Device error: {e} — retrying in {RETRY_INTERVAL}s...")
            finally:
                try:
                    if args.grab:
                        device.ungrab()
                except OSError:
                    pass
                device.close()

            time.sleep(RETRY_INTERVAL)

    except KeyboardInterrupt:
        print("\nStopped.")
    finally:
        sock.close()


if __name__ == '__main__':
    main()
