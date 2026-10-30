"""Compares two `dotnet-gcdump report` outputs: the types whose total size grew, largest growth first (finding a leak between two moments)."""
import re
import sys

ROW = re.compile(r"^\s*([\d,]+)\s+([\d,]+)\s+(.+?)\s*$")


def parse(path):
    sizes = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = ROW.match(line)
            if not m:
                continue
            size, count, name = int(m.group(1).replace(",", "")), int(m.group(2).replace(",", "")), m.group(3)
            sizes[name] = (size, count)
    return sizes


def main(a, b):
    before, after = parse(a), parse(b)
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
    main(sys.argv[1], sys.argv[2])
