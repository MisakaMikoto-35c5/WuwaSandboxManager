"""List a PE's imported / delay-imported DLLs and any *.dll strings, marking
which are present in a reference directory (e.g. the image's System32)."""
import os
import re
import sys

sys.path.insert(0, r"C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd\rdp")
from analyze import Pe  # noqa: E402

dll = sys.argv[1]
refdirs = sys.argv[2:]

present = set()
for d in refdirs:
    if os.path.isdir(d):
        for f in os.listdir(d):
            present.add(f.lower())

p = Pe(dll)
p.build_string_index()
print(f"== {os.path.basename(dll)}")

deps = []
for e in getattr(p.pe, "DIRECTORY_ENTRY_IMPORT", []):
    deps.append(("import", e.dll.decode()))
for e in getattr(p.pe, "DIRECTORY_ENTRY_DELAY_IMPORT", []):
    deps.append(("delay", e.dll.decode()))

rx = re.compile(r"^[A-Za-z0-9_\-\.]+\.dll$", re.I)
strdeps = set()
for off, kind, text in p._strings:
    if kind == "A" and rx.match(text):
        strdeps.add(text)

for kind, name in deps:
    mark = "OK " if name.lower() in present else "MISSING"
    print(f"  {kind:5} {name:40} {mark}")

missing_str = sorted(s for s in strdeps if s.lower() not in present and s.lower() != os.path.basename(dll).lower())
if missing_str:
    print("  -- dll names referenced as strings but absent from the image:")
    for s in missing_str:
        print(f"       {s}")
