import os
import sys

needle = sys.argv[1].encode("utf-16-le")
roots = sys.argv[2:]
hits = []
for root in roots:
    for dirpath, _dirs, files in os.walk(root):
        for f in files:
            p = os.path.join(dirpath, f)
            try:
                if os.path.getsize(p) > 200 * 1024 * 1024:
                    continue
                with open(p, "rb") as fh:
                    data = fh.read()
            except OSError:
                continue
            if needle in data:
                ascii_needle = sys.argv[1].encode("ascii", "ignore")
                hits.append((p, needle in data, ascii_needle in data if ascii_needle else False))
for p, w, a in hits:
    print(f"{p}\twide={w}\tascii={a}")
print(f"-- {len(hits)} file(s) contain {sys.argv[1]!r}")
