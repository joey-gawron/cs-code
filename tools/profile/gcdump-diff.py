"""Compares two `dotnet-gcdump report` outputs: the types whose total size grew, largest growth first (finding a leak between two moments).

The report lists big objects in size buckets ("Type (Bytes > 100K)"); sizes are summed per type across buckets. With one file, prints its top types.
"""
import re
import sys

ROW = re.compile(r"^\s*([\d,]+)\s+([\d,]+)\s+(.+?)\s*$")
BUCKET = re.compile(r"\s*\(Bytes > [^)]*\)")


def parse(path):
    sizes = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = ROW.match(line)
            if not m:
                continue
            size, count = int(m.group(1).replace(",", "")), int(m.group(2).replace(",", ""))
            name = BUCKET.sub("", m.group(3))
            old = sizes.get(name, (0, 0))
            sizes[name] = (old[0] + size, old[1] + count)
    return sizes


def main(args):
    if len(args) == 1:
        rows = sorted(((s, c, n) for n, (s, c) in parse(args[0]).items()), reverse=True)
        for size, count, name in rows[:40]:
            print("%12d %10d  %s" % (size, count, name))
        return
    before, after = parse(args[0]), parse(args[1])
    rows = []
    for name, (size, count) in after.items():
        old_size, old_count = before.get(name, (0, 0))
        if size > old_size:
            rows.append((size - old_size, count - old_count, name, old_size, size))
    rows.sort(reverse=True)
    print("%12s %10s  %s" % ("+bytes", "+count", "type (before -> after bytes)"))
    for grow, count, name, old, new in rows[:50]:
        print("%12d %10d  %s (%d -> %d)" % (grow, count, name, old, new))


if __name__ == "__main__":
    main(sys.argv[1:])
