#!/usr/bin/env python3
"""Builds a macOS .icns from CsCode's committed PNGs (assets/branding/cscode-<size>.png), unchanged: each PNG is stored as it is in the icon
type of its pixel size (PNG payloads, readable since macOS 10.7; what `iconutil -c icns` writes from an .iconset). Standard library only, so it
runs (and is tested) on Linux too.

    tools/macos/make-icns.py [--branding DIR] OUTPUT.icns
"""
import argparse
import os
import struct
import sys

# icon type -> pixel size of its PNG (the @2x types hold the doubled size of their point size)
TYPES = [
    ("icp4", 16), ("ic11", 32),    # 16 pt, 16 pt @2x
    ("icp5", 32), ("ic12", 64),    # 32 pt, 32 pt @2x
    ("ic07", 128), ("ic13", 256),  # 128 pt, 128 pt @2x
    ("ic08", 256), ("ic14", 512),  # 256 pt, 256 pt @2x
    ("ic09", 512), ("ic10", 1024), # 512 pt, 512 pt @2x
]
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def png_size(data):
    if not data.startswith(PNG_SIGNATURE) or data[12:16] != b"IHDR":
        raise ValueError("not a PNG")
    return struct.unpack(">II", data[16:24])


def build(branding):
    chunks = []
    for kind, size in TYPES:
        path = os.path.join(branding, "cscode-%d.png" % size)
        with open(path, "rb") as f:
            data = f.read()
        if png_size(data) != (size, size):
            raise ValueError("%s is not %dx%d" % (path, size, size))
        chunks.append(kind.encode("ascii") + struct.pack(">I", 8 + len(data)) + data)
    body = b"".join(chunks)
    return b"icns" + struct.pack(">I", 8 + len(body)) + body


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--branding", default=os.path.join(here, "..", "..", "assets", "branding"))
    parser.add_argument("output")
    args = parser.parse_args()
    data = build(args.branding)
    tmp = args.output + ".tmp"
    with open(tmp, "wb") as f:
        f.write(data)
    os.replace(tmp, args.output)
    print("%s: %d icons, %d bytes" % (args.output, len(TYPES), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
