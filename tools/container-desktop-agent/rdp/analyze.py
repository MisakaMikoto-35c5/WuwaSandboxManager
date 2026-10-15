#!/usr/bin/env python3
"""PE string + RIP-relative xref + function analyzer (x64).

Usage:
  analyze.py <dll> strings <regex>            # matching strings with VA + file offset
  analyze.py <dll> xrefs <regex>              # xref instruction sites to matching strings
  analyze.py <dll> func <hexVA>               # function containing VA: call/jmp/string refs + disasm
  analyze.py <dll> exports <regex>            # exported names
  analyze.py <dll> imports <regex>            # imported dll/function names
"""
import re
import sys

import capstone
import pefile

CS = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
CS.detail = True


def is_printable(c):
    return 0x20 <= c <= 0x7E


def collect_strings(data):
    """Return list of (rva, kind, text)."""
    out = []
    # ASCII runs
    n = len(data)
    i = 0
    while i < n:
        c = data[i]
        if is_printable(c):
            j = i
            while j < n and is_printable(data[j]):
                j += 1
            if j - i >= 6:
                out.append((i, "A", data[i:j].decode("ascii")))
            i = j
        else:
            i += 1
    # UTF-16LE runs
    i = 0
    while i + 1 < n:
        if is_printable(data[i]) and data[i + 1] == 0:
            j = i
            while j + 1 < n and is_printable(data[j]) and data[j + 1] == 0:
                j += 2
            if (j - i) // 2 >= 4:
                out.append((i, "W", data[i:j:2].decode("ascii")))
            i = j
        else:
            i += 1
    return out


class Pe:
    def __init__(self, path):
        self.path = path
        self.pe = pefile.PE(path, fast_load=True)
        self.pe.parse_data_directories()
        self.image_base = self.pe.OPTIONAL_HEADER.ImageBase
        with open(path, "rb") as f:
            self.data = f.read()
        self.sections = []
        for s in self.pe.sections:
            name = s.Name.rstrip(b"\x00").decode("ascii", "replace")
            self.sections.append({
                "name": name,
                "va": s.VirtualAddress,
                "vsize": max(s.Misc_VirtualSize, s.SizeOfRawData),
                "raw": s.PointerToRawData,
                "rawsize": s.SizeOfRawData,
            })
        self._text = self._find_section(".text")
        self._pdata = self._find_section(".pdata")
        self._xrefs = None
        self._funcs = None

    def _find_section(self, name):
        for s in self.sections:
            if s["name"] == name:
                return s
        return None

    def off_to_rva(self, off):
        for s in self.sections:
            if s["raw"] <= off < s["raw"] + s["rawsize"]:
                return s["va"] + (off - s["raw"])
        return None

    def rva_to_off(self, rva):
        for s in self.sections:
            if s["va"] <= rva < s["va"] + s["vsize"]:
                return s["raw"] + (rva - s["va"])
        return None

    def va_to_off(self, va):
        return self.rva_to_off(va - self.image_base)

    def read_rva(self, rva, n):
        off = self.rva_to_off(rva)
        return self.data[off:off + n] if off is not None else b""

    def import_hn_map(self):
        """Map hint/name RVA -> 'dll!name' (built from the ILT, so it also
        resolves the values stored in IAT slots in the file image)."""
        if getattr(self, "_hn", None) is not None:
            return self._hn
        import struct as _s
        m = {}
        for entry in getattr(self.pe, "DIRECTORY_ENTRY_IMPORT", []):
            dll = entry.dll.decode()
            oft = entry.struct.OriginalFirstThunk
            for i, imp in enumerate(entry.imports):
                nm = imp.name.decode() if imp.name else f"#{imp.ordinal}"
                m.setdefault(id(imp), (dll, nm))
                if oft:
                    raw = self.read_rva(oft + 8 * i, 8)
                    if len(raw) == 8:
                        q = _s.unpack("<Q", raw)[0]
                        if q:
                            m[q] = f"{dll}!{nm}"
        self._hn = m
        return m

    def import_name_for(self, q):
        return self.import_hn_map().get(q)

    # ---- xrefs ----
    def scan(self):
        if self._xrefs is not None:
            return
        t = self._text
        code = self.data[t["raw"]:t["raw"] + t["rawsize"]]
        base = self.image_base + t["va"]
        view = memoryview(code)
        out = {}
        calls = {}
        pos = 0
        n = len(code)
        while pos < n:
            produced = False
            last_end = pos
            for insn in CS.disasm(view[pos:], base + pos):
                produced = True
                last_end = insn.address + insn.size - base
                for op in insn.operands:
                    if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                        target = insn.address + insn.size + op.mem.disp
                        if self.image_base <= target < self.image_base + 0x10000000:
                            out.setdefault(target, []).append((insn.address, insn))
                if insn.id == capstone.x86.X86_INS_CALL and insn.operands \
                        and insn.operands[0].type == capstone.x86.X86_OP_IMM:
                    calls.setdefault(insn.operands[0].imm, []).append((insn.address, insn))
            pos = last_end + 1 if produced else pos + 1
        self._xrefs = out
        self._calls = calls

    def xrefs(self):
        self.scan()
        return self._xrefs

    def callers(self, va):
        self.scan()
        return self._calls.get(va, [])

    # ---- functions from .pdata ----
    def funcs(self):
        if self._funcs is not None:
            return self._funcs
        self._funcs = []
        p = self._pdata
        if p:
            raw = self.data[p["raw"]:p["raw"] + p["rawsize"]]
            for i in range(0, len(raw) - 11, 12):
                begin = int.from_bytes(raw[i:i + 4], "little")
                end = int.from_bytes(raw[i + 4:i + 8], "little")
                if begin == 0 and end == 0:
                    continue
                self._funcs.append((begin, end))
            self._funcs.sort()
        return self._funcs

    def func_at(self, rva):
        for begin, end in self.funcs():
            if begin <= rva < end:
                return begin, end
        return None

    def disasm_range(self, rva, count=60):
        off = self.rva_to_off(rva)
        if off is None:
            return []
        code = self.data[off:off + 65536]
        view = memoryview(code)
        base = self.image_base + rva
        out = []
        pos = 0
        while pos < len(code) and len(out) < count:
            produced = False
            last_end = pos
            for insn in CS.disasm(view[pos:], base + pos):
                produced = True
                last_end = insn.address + insn.size - base
                out.append(insn)
                if len(out) >= count:
                    break
            if len(out) >= count:
                break
            pos = last_end + 1 if produced else pos + 1
        return out

    def string_at_va(self, va):
        off = self.va_to_off(va)
        if off is None:
            return None
        hit = self._strings_by_off.get(off)
        if hit:
            return hit
        # allow pointing into the middle (aligned strings)
        for k in range(1, 3):
            hit = self._strings_by_off.get(off - k)
            if hit:
                return hit
        return None

    def build_string_index(self):
        self._strings = collect_strings(self.data)
        self._strings_by_off = {}
        for off, kind, text in self._strings:
            rva = self.off_to_rva(off)
            if rva is not None:
                self._strings_by_off[off] = (rva, kind, text)


def fmt_insn(insn):
    return f"0x{insn.address:X}  {insn.mnemonic} {insn.op_str}"


def main():
    path, mode = sys.argv[1], sys.argv[2]
    arg = sys.argv[3] if len(sys.argv) > 3 else ""
    p = Pe(path)
    p.build_string_index()
    print(f"== {path}  imagebase=0x{p.image_base:X}")

    if mode == "strings":
        rx = re.compile(arg, re.I)
        for off, kind, text in p._strings:
            if rx.search(text):
                rva = p.off_to_rva(off)
                if rva is None:
                    continue
                print(f"  [{kind}] va=0x{p.image_base + rva:X} off=0x{off:X}  {text!r}")
        return

    if mode == "range":
        lo = int(sys.argv[3], 16)
        hi = int(sys.argv[4], 16)
        for off, kind, text in p._strings:
            rva = p.off_to_rva(off)
            if rva is None:
                continue
            va = p.image_base + rva
            if lo <= va <= hi:
                print(f"  [{kind}] va=0x{va:X}  {text!r}")
        return

    if mode == "xrefs":
        rx = re.compile(arg, re.I)
        xr = p.xrefs()
        for off, kind, text in p._strings:
            if not rx.search(text):
                continue
            rva = p.off_to_rva(off)
            va = p.image_base + rva
            sites = xr.get(va, [])
            print(f"  {text!r} va=0x{va:X}  xrefs={len(sites)}")
            for addr, insn in sites:
                f = p.func_at(addr - p.image_base)
                froot = f" func=0x{p.image_base + f[0]:X}" if f else ""
                print(f"      {fmt_insn(insn)}{froot}")
        return

    if mode == "events":
        # Locate EVENT_DESCRIPTOR structures (Id,Version,Channel,Level,Opcode,Task,Keyword)
        # for the given event ids and report who references them (the emitting code).
        ids = {int(x, 16) for x in arg.split(",")}
        import struct as _s
        for sec in p.sections:
            if sec["name"] not in (".rdata", ".data"):
                continue
            raw = p.data[sec["raw"]:sec["raw"] + sec["rawsize"]]
            for i in range(0, len(raw) - 16, 8):
                (eid,) = _s.unpack_from("<H", raw, i)
                if eid not in ids:
                    continue
                ver, chan, level, opcode = raw[i + 2], raw[i + 3], raw[i + 4], raw[i + 5]
                (task,) = _s.unpack_from("<H", raw, i + 6)
                (kw,) = _s.unpack_from("<Q", raw, i + 8)
                if ver > 1 or chan > 0x12 or level > 5 or opcode > 0x40:
                    continue
                if kw != 0 and bin(kw).count("1") > 4:
                    continue
                va = p.image_base + sec["va"] + (i - sec["raw"])
                sites = p.xrefs().get(va, [])
                if not sites:
                    continue
                print(f"  event #{eid} desc@0x{va:X} ver={ver} chan={chan} level={level} op={opcode} task={task} kw=0x{kw:X}")
                for addr, insn in sites:
                    f = p.func_at(addr - p.image_base)
                    print(f"      {fmt_insn(insn)}" + (f" func=0x{p.image_base + f[0]:X}" if f else ""))
        return

    if mode == "thunks":
        import struct as _struct
        for token in arg.split(","):
            slot = int(token, 16)
            rva = slot - p.image_base
            hit = None
            for entry in getattr(p.pe, "DIRECTORY_ENTRY_IMPORT", []):
                for imp in entry.imports:
                    if imp.address == rva:
                        hit = f"{entry.dll.decode()}!{imp.name.decode() if imp.name else '#' + str(imp.ordinal)}"
            for entry in getattr(p.pe, "DIRECTORY_ENTRY_DELAY_IMPORT", []):
                for imp in entry.imports:
                    if imp.address == rva:
                        hit = f"(delay) {entry.dll.decode()}!{imp.name.decode() if imp.name else '#' + str(imp.ordinal)}"
            if hit is None:
                raw = p.read_rva(rva, 8)
                if len(raw) == 8:
                    q = _struct.unpack("<Q", raw)[0]
                    name = None
                    if 0 < q < 0x10000000:
                        off = p.rva_to_off(q)
                        if off is not None:
                            end = p.data.find(b"\x00", off + 2)
                            name = p.data[off + 2:end].decode("ascii", "replace")
                    hit = f"ptr->0x{q:X} ({name})" if name else f"ptr->0x{q:X}"
            print(f"  0x{slot:X}  {hit}")
        return

    if mode == "iat":
        target = int(arg, 16)
        rva = target - p.image_base
        found = False
        for entry in getattr(p.pe, "DIRECTORY_ENTRY_IMPORT", []):
            dll = entry.dll.decode()
            for imp in entry.imports:
                if imp.address == rva:
                    print(f"  0x{target:X} -> {dll}!{imp.name.decode() if imp.name else '#' + str(imp.ordinal)}")
                    found = True
        if not found:
            print(f"  0x{target:X} -> (not an IAT slot)")
        return

    if mode == "callers":
        va = int(arg, 16)
        for addr, insn in p.callers(va):
            f = p.func_at(addr - p.image_base)
            froot = f" func=0x{p.image_base + f[0]:X}" if f else ""
            print(f"  {fmt_insn(insn)}{froot}")
        return

    if mode == "func":
        va = int(arg, 16)
        rva = va - p.image_base
        f = p.func_at(rva)
        if not f:
            print("  no function")
            return
        print(f"  function 0x{p.image_base + f[0]:X} .. 0x{p.image_base + f[1]:X}")
        xr = p.xrefs()
        for insn in p.disasm_range(f[0], 4000):
            if insn.address >= p.image_base + f[1]:
                break
            extra = ""
            for op in insn.operands:
                if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                    tgt = insn.address + insn.size + op.mem.disp
                    s = p.string_at_va(tgt)
                    if s:
                        extra += f"   ; {s[2]!r}"
                    elif insn.mnemonic in ("call", "jmp"):
                        import struct as _s
                        raw = p.read_rva(tgt - p.image_base, 8)
                        q = _s.unpack("<Q", raw)[0] if len(raw) == 8 else 0
                        name = p.import_name_for(q) or ""
                        extra += f"   ; slot@0x{tgt:X} -> {name or hex(q)}"
            print(f"    {fmt_insn(insn)}{extra}")
        return

    if mode == "exports":
        rx = re.compile(arg, re.I) if arg else None
        for e in getattr(p.pe, "DIRECTORY_ENTRY_EXPORT", None).symbols if hasattr(p.pe, "DIRECTORY_ENTRY_EXPORT") else []:
            nm = e.name.decode() if e.name else f"#{e.ordinal}"
            if rx is None or rx.search(nm):
                print(f"  ord={e.ordinal} rva=0x{e.address:X} {nm}")
        return

    if mode == "imports":
        rx = re.compile(arg, re.I) if arg else None
        for entry in getattr(p.pe, "DIRECTORY_ENTRY_IMPORT", []):
            dll = entry.dll.decode()
            names = [imp.name.decode() if imp.name else f"#{imp.ordinal}" for imp in entry.imports]
            if rx is None:
                print(f"  {dll}: {len(names)} imports")
            else:
                hits = [n for n in names if rx.search(n)]
                for h in hits:
                    print(f"  {dll}!{h}")
        return

    print("unknown mode")


if __name__ == "__main__":
    main()
