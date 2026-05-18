#!/usr/bin/env python3
"""
vd-switch foot switch sender
Reads key a/b/c from foot switch(es) and sends IPv6 link-local multicast
packets to vd-switch.exe listeners on the local network.

Keys a/b/c map to actions decided by the receiver.
Multiple devices are handled simultaneously. Newly connected devices are
picked up automatically every RETRY_INTERVAL seconds.
"""

import argparse
import asyncio
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
RETRY_INTERVAL = 5  # seconds between device scans

TARGET_KEYS = {
    ecodes.KEY_A: 'KEY_A',
    ecodes.KEY_B: 'KEY_B',
    ecodes.KEY_C: 'KEY_C',
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


def find_candidate_paths():
    """Return paths of input devices that have KEY_A, KEY_B, KEY_C."""
    result = []
    for path in evdev.list_devices():
        try:
            dev = evdev.InputDevice(path)
            keys = dev.capabilities().get(ecodes.EV_KEY, [])
            if all(k in keys for k in (ecodes.KEY_A, ecodes.KEY_B, ecodes.KEY_C)):
                result.append(path)
            dev.close()
        except (OSError, PermissionError):
            pass
    return result


async def handle_device(device, sock, dest, grab):
    """Read events from one device and forward as multicast packets."""
    path = device.path
    try:
        if grab:
            device.grab()
        print(f"Device opened: {path} ({device.name})" +
              (" [grabbed]" if grab else ""))
        async for event in device.async_read_loop():
            if event.type != ecodes.EV_KEY:
                continue
            key_event = evdev.categorize(event)
            if key_event.keystate == evdev.KeyEvent.key_hold:
                continue  # ignore auto-repeat
            key_name = TARGET_KEYS.get(key_event.scancode)
            if key_name is None:
                continue
            state = 'DOWN' if key_event.keystate == evdev.KeyEvent.key_down else 'UP'
            # Protocol: "KEY_X DOWN" or "KEY_X UP"
            # UDP delivery is best-effort; UP may be lost — receiver handles accordingly
            payload = f"{key_name} {state}".encode()
            sock.sendto(payload, dest)
            print(f"  sent: {payload.decode()} (from {path})")
    except OSError as e:
        print(f"Device error ({path}): {e}")
    finally:
        if grab:
            try:
                device.ungrab()
            except OSError:
                pass
        try:
            device.close()
        except OSError:
            pass
        print(f"Device closed: {path}")


async def run(explicit_paths, grab, sock, dest):
    """Scan for devices periodically and maintain one task per device."""
    tasks = {}  # path -> asyncio.Task

    while True:
        candidate_paths = explicit_paths if explicit_paths else find_candidate_paths()

        if not candidate_paths:
            label = ', '.join(explicit_paths) if explicit_paths else 'suitable device'
            print(f"Waiting for {label} (retry every {RETRY_INTERVAL}s)...")

        for path in candidate_paths:
            if path in tasks and not tasks[path].done():
                continue  # already being handled
            try:
                dev = evdev.InputDevice(path)
                tasks[path] = asyncio.create_task(handle_device(dev, sock, dest, grab))
            except (OSError, PermissionError):
                pass  # will retry next scan

        # Remove finished tasks
        tasks = {p: t for p, t in tasks.items() if not t.done()}

        await asyncio.sleep(RETRY_INTERVAL)


def main():
    parser = argparse.ArgumentParser(
        description='Send vd-switch commands via IPv6 multicast from foot switch(es)'
    )
    parser.add_argument('--device', '-d', metavar='PATH', action='append',
                        help='Input device path (e.g. /dev/input/event3). '
                             'May be specified multiple times. Auto-detected if omitted.')
    parser.add_argument('--grab', '-g', action='store_true',
                        help='Exclusively grab each device (suppresses key events in other apps)')
    parser.add_argument('--interface', '-i', metavar='IFNAME',
                        help='Network interface. Auto-detected (first fe80::) if omitted.')
    parser.add_argument('--list', '-l', action='store_true',
                        help='List candidate input devices and exit')
    args = parser.parse_args()

    # ── device listing ──────────────────────────────────────────────────────
    if args.list:
        paths = find_candidate_paths()
        if not paths:
            print("No devices with KEY_A/B/C found (permission issue?)")
        else:
            print("Candidate devices:")
            for path in paths:
                try:
                    dev = evdev.InputDevice(path)
                    print(f"  {path}  {dev.name}")
                    dev.close()
                except OSError:
                    print(f"  {path}  (unreadable)")
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
    if args.device:
        print(f"Devices  : {', '.join(args.device)}")
    else:
        print("Devices  : auto-detect")

    try:
        asyncio.run(run(args.device or [], args.grab, sock, dest))
    except KeyboardInterrupt:
        print("\nStopped.")
    finally:
        sock.close()


if __name__ == '__main__':
    main()
