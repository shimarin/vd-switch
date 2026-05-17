#!/usr/bin/env python3
"""
vd-switch foot switch sender
Reads key 1/2/3 from a foot switch and sends IPv6 link-local multicast
packets to vd-switch.exe listeners on the local network.

Keys a/b/c map to actions decided by the receiver.
"""

import argparse
import socket
import struct
import sys

try:
    import evdev
    from evdev import ecodes
except ImportError:
    print("evdev not installed. Run: pip install --break-system-packages evdev", file=sys.stderr)
    sys.exit(1)

MULTICAST_ADDR = 'ff12::7664:7377'
PORT = 5356

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
    """Return input devices that have KEY_1, KEY_2, KEY_3 in their capabilities."""
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
            print("No devices with KEY_1/2/3 found (permission issue?)")
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

    # ── input device ────────────────────────────────────────────────────────
    if args.device:
        try:
            device = evdev.InputDevice(args.device)
        except (OSError, PermissionError) as e:
            print(f"Cannot open {args.device}: {e}", file=sys.stderr)
            sys.exit(1)
    else:
        devices = find_devices()
        if not devices:
            print("No suitable input device found.", file=sys.stderr)
            print("Try: sudo python3 sender.py --list", file=sys.stderr)
            sys.exit(1)
        if len(devices) > 1:
            print("Multiple candidate devices found. Specify one with --device:", file=sys.stderr)
            for dev in devices:
                print(f"  {dev.path}  {dev.name}", file=sys.stderr)
            sys.exit(1)
        device = devices[0]

    sock, ifindex = open_socket(ifname)
    dest = (MULTICAST_ADDR, PORT, 0, ifindex)

    print(f"Device   : {device.path} ({device.name})")
    print(f"Interface: {ifname}")
    print(f"Multicast: [{MULTICAST_ADDR}%{ifname}]:{PORT}")
    if args.grab:
        device.grab()
        print("Device grabbed (keys suppressed in other apps)")
    print("Listening for keys 1 / 2 / 3 ...  Ctrl-C to quit")

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
    except KeyboardInterrupt:
        print("\nStopped.")
    finally:
        if args.grab:
            try:
                device.ungrab()
            except OSError:
                pass
        device.close()
        sock.close()


if __name__ == '__main__':
    main()
