import sys

sys.path.insert(0, r"C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd\rdp")
from analyze import Pe  # noqa: E402

p = Pe(sys.argv[1])
for token in sys.argv[2:]:
    va = int(token, 16)
    raw = p.read_rva(va - p.image_base, 16)
    d1, d2, d3 = int.from_bytes(raw[0:4], "little"), int.from_bytes(raw[4:6], "little"), int.from_bytes(raw[6:8], "little")
    tail = raw[8:16].hex().upper()
    print(f"  {token} -> {{{d1:08X}-{d2:04X}-{d3:04X}-{tail[0:4]}-{tail[4:16]}}}")
