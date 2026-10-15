import itertools
import struct
import sys

sys.path.insert(0, r"C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd\rdp")
from analyze import Pe  # noqa: E402

p = Pe(sys.argv[1])
print("sections:", [(s["name"], hex(s["va"]), hex(s["rawsize"]), hex(s["vsize"])) for s in p.sections])
xr = p.xrefs()
print("xref targets:", len(xr))
for t, sites in itertools.islice(xr.items(), 5):
    print("  ", hex(t), [hex(a) for a, _ in sites])
d = p.data
for va in (0x1800F60B0, 0x1800E0350, 0x1800E00F0, 0x1800E0928):
    needle = struct.pack("<Q", va)
    idxs = []
    start = 0
    while True:
        i = d.find(needle, start)
        if i < 0:
            break
        idxs.append(i)
        start = i + 1
    print(hex(va), "ptr occurrences:", [hex(i) for i in idxs])
    for i in idxs:
        rva = p.off_to_rva(i)
        if rva is not None:
            sva = p.image_base + rva
            print("   slot va", hex(sva), "xrefs", [hex(a) for a, _ in xr.get(sva, [])])
