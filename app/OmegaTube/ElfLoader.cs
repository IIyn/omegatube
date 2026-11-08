using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OmegaTube
{
    // A minimal in-process loader for the ARM (ELF32, ET_DYN) core, used only on Tizen 5.5 and older where
    // Samsung's UEP refuses dlopen of an unsigned library: it blocks executable mappings backed by a file or
    // fd (so both the packaged .so and the memfd route fail with "failed to map segment"), but it DOES allow
    // making anonymous memory executable. So instead of asking the dynamic linker to map the file, this loader
    // copies the PT_LOAD segments into anonymous memory, applies the relocations, resolves imports against the
    // firmware's own (signed) libraries via dlsym, sets each segment's real permissions, and runs the C++
    // constructors. See GitHub issue #15.
    //
    // Scope kept deliberately small, matching exactly what libomegacore.so needs (verified with readelf):
    //   - ELF32 little-endian, EM_ARM, ET_DYN
    //   - reloc types R_ARM_RELATIVE / ABS32 / GLOB_DAT / JUMP_SLOT / TLS_DTPMOD32 (TLS handled natively in the
    //     core, see core/src/util/glibc_compat.cpp), all REL (addend in place)
    //   - DT_INIT + DT_INIT_ARRAY for the static constructors
    // Pure System.* so the same algorithm is exercised off-device by the ARM loader test (tests/elf-load).
    internal sealed class ElfImage
    {
        private readonly Dictionary<string, IntPtr> exports;
        public IntPtr Base { get; }

        internal ElfImage(IntPtr @base, Dictionary<string, IntPtr> exports)
        {
            Base = @base;
            this.exports = exports;
        }

        // Address of an exported symbol (e.g. "omega_version"), or IntPtr.Zero if absent.
        public IntPtr GetSymbol(string name) => exports.TryGetValue(name, out IntPtr p) ? p : IntPtr.Zero;
    }

    internal static class ElfLoader
    {
        private const int ProtRead = 1, ProtWrite = 2, ProtExec = 4, MapPrivate = 2, MapAnon = 0x20;
        private const int RtldNow = 2, RtldGlobal = 0x100;
        private const int PF_X = 1, PF_W = 2, PF_R = 4;
        private const int ET_DYN = 3, EM_ARM = 40, SHN_UNDEF = 0;
        private const long Page = 0x1000;
        private static readonly IntPtr RtldDefault = IntPtr.Zero;

        private const uint PT_LOAD = 1, PT_DYNAMIC = 2;
        private const int DT_NULL = 0, DT_NEEDED = 1, DT_PLTRELSZ = 2, DT_STRTAB = 5, DT_SYMTAB = 6,
            DT_RELA = 7, DT_STRSZ = 10, DT_SYMENT = 11, DT_INIT = 12, DT_REL = 17, DT_RELSZ = 18,
            DT_RELENT = 19, DT_JMPREL = 23, DT_INIT_ARRAY = 25, DT_INIT_ARRAYSZ = 27;
        private const int R_ARM_ABS32 = 2, R_ARM_TLS_DTPOFF32 = 18, R_ARM_TLS_DTPMOD32 = 17,
            R_ARM_GLOB_DAT = 21, R_ARM_JUMP_SLOT = 22, R_ARM_RELATIVE = 23;

        [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
        private static extern IntPtr Mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, IntPtr offset);

        [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
        private static extern int Mprotect(IntPtr addr, UIntPtr len, int prot);

        [DllImport("libdl.so.2", EntryPoint = "dlopen", SetLastError = true)]
        private static extern IntPtr DlOpen(string file, int mode);

        [DllImport("libdl.so.2", EntryPoint = "dlsym", SetLastError = true)]
        private static extern IntPtr DlSym(IntPtr handle, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InitFn();

        private struct Segment
        {
            public uint Vaddr, Memsz;
            public int Flags;
        }

        // Build a native pointer from a computed address without the overflow-checking (IntPtr)(long) cast:
        // on 32-bit ARM the core maps at a high address (>= 0x80000000) that does not fit a signed int, which
        // would throw OverflowException. Reinterpret the low 32 bits instead (issue #15).
        private static IntPtr Ptr(long addr) => IntPtr.Size == 4 ? (IntPtr)unchecked((int)addr) : (IntPtr)addr;

        private static long AlignDown(long v) => v & ~(Page - 1);
        private static long AlignUp(long v) => (v + Page - 1) & ~(Page - 1);

        public static ElfImage Load(byte[] img)
        {
            if (img.Length < 52 || img[0] != 0x7f || img[1] != (byte)'E' || img[2] != (byte)'L' || img[3] != (byte)'F')
            {
                throw new BadImageFormatException("not an ELF file");
            }
            if (img[4] != 1 || img[5] != 1)
            {
                throw new BadImageFormatException("not ELF32 little-endian");
            }
            if (U16(img, 16) != ET_DYN || U16(img, 18) != EM_ARM)
            {
                throw new BadImageFormatException("not an ARM shared object (ET_DYN/EM_ARM)");
            }

            uint phoff = U32(img, 28);
            int phentsize = U16(img, 42);
            int phnum = U16(img, 44);
            if (phoff + (long)phnum * phentsize > img.Length)
            {
                throw new BadImageFormatException("program headers out of range");
            }

            long minVaddr = long.MaxValue, maxVaddr = 0;
            uint dynVaddr = 0, dynSize = 0;
            var loads = new List<Segment>();
            for (int i = 0; i < phnum; i++)
            {
                int ph = (int)phoff + i * phentsize;
                uint type = U32(img, ph);
                if (type == PT_LOAD)
                {
                    uint vaddr = U32(img, ph + 8), memsz = U32(img, ph + 20);
                    loads.Add(new Segment { Vaddr = vaddr, Memsz = memsz, Flags = (int)U32(img, ph + 24) });
                    if (vaddr < minVaddr) minVaddr = vaddr;
                    if (vaddr + memsz > maxVaddr) maxVaddr = vaddr + memsz;
                }
                else if (type == PT_DYNAMIC)
                {
                    dynVaddr = U32(img, ph + 8);
                    dynSize = U32(img, ph + 20);
                }
            }
            if (minVaddr == long.MaxValue || dynVaddr == 0)
            {
                throw new BadImageFormatException("no PT_LOAD / PT_DYNAMIC");
            }

            long lo = AlignDown(minVaddr);
            long hi = AlignUp(maxVaddr);

            // One anonymous region for the whole image, mapped writable for copying + relocating; each segment
            // is then set to its real permissions below, so code pages are not left writable (W^X).
            IntPtr @base = Mmap(IntPtr.Zero, (UIntPtr)(ulong)(hi - lo), ProtRead | ProtWrite, MapPrivate | MapAnon, -1, IntPtr.Zero);
            if (@base == (IntPtr)(-1))
            {
                throw new InvalidOperationException("mmap anon failed, errno " + Marshal.GetLastWin32Error());
            }
            long bias = @base.ToInt64() - lo;  // load bias; lo is 0 for this core

            for (int i = 0; i < phnum; i++)
            {
                int ph = (int)phoff + i * phentsize;
                if (U32(img, ph) != PT_LOAD) continue;
                uint off = U32(img, ph + 4), vaddr = U32(img, ph + 8), filesz = U32(img, ph + 16);
                if (filesz > 0)
                {
                    if ((long)off + filesz > img.Length)
                    {
                        throw new BadImageFormatException("segment file range out of bounds");
                    }
                    Marshal.Copy(img, (int)off, Ptr(bias + vaddr), (int)filesz);  // bss (memsz>filesz) stays zero
                }
            }

            IntPtr dyn = Ptr(bias + dynVaddr);
            long symtab = 0, strtab = 0, syment = 16, strsz = 0;
            long rel = 0, relsz = 0, relent = 8, jmprel = 0, pltrelsz = 0;
            long init = 0, initArray = 0, initArraySz = 0;
            var needed = new List<uint>();
            for (int o = 0; o < (int)dynSize; o += 8)
            {
                int tag = Marshal.ReadInt32(dyn, o);
                uint val = (uint)Marshal.ReadInt32(dyn, o + 4);
                if (tag == DT_NULL) break;
                switch (tag)
                {
                    case DT_SYMTAB: symtab = bias + val; break;
                    case DT_STRTAB: strtab = bias + val; break;
                    case DT_STRSZ: strsz = val; break;
                    case DT_SYMENT: syment = val; break;
                    case DT_REL: rel = bias + val; break;
                    case DT_RELSZ: relsz = val; break;
                    case DT_RELENT: relent = val; break;
                    case DT_JMPREL: jmprel = bias + val; break;
                    case DT_PLTRELSZ: pltrelsz = val; break;
                    case DT_INIT: init = bias + val; break;
                    case DT_INIT_ARRAY: initArray = bias + val; break;
                    case DT_INIT_ARRAYSZ: initArraySz = val; break;
                    case DT_NEEDED: needed.Add(val); break;
                    case DT_RELA: throw new BadImageFormatException("RELA relocations not supported (expected REL)");
                }
            }
            if (symtab == 0 || strtab == 0)
            {
                throw new BadImageFormatException("missing SYMTAB/STRTAB");
            }

            foreach (uint nameOff in needed)
            {
                string name = Marshal.PtrToStringAnsi(Ptr(strtab + nameOff));
                if (string.IsNullOrEmpty(name) || name.StartsWith("ld-linux"))
                {
                    continue;  // the loader itself; its symbols the core needs live in libc
                }
                DlOpen(name, RtldNow | RtldGlobal);  // best effort; resolution below also uses the default scope
            }

            // Resolve a symbol by index, caching the result (the same import appears in many relocations).
            var symCache = new Dictionary<int, IntPtr>();
            IntPtr SymAddr(int index)
            {
                if (symCache.TryGetValue(index, out IntPtr cached))
                {
                    return cached;
                }
                long s = symtab + index * syment;
                uint stName = (uint)Marshal.ReadInt32(Ptr(s), 0);
                uint stValue = (uint)Marshal.ReadInt32(Ptr(s), 4);
                ushort shndx = (ushort)Marshal.ReadInt16(Ptr(s), 14);
                IntPtr addr;
                if (shndx != SHN_UNDEF)
                {
                    addr = Ptr(bias + stValue);  // defined here (st_value LSB already encodes Thumb)
                }
                else
                {
                    string name = Marshal.PtrToStringAnsi(Ptr(strtab + stName));
                    addr = string.IsNullOrEmpty(name) ? IntPtr.Zero : DlSym(RtldDefault, name);  // 0 ok for unused weak
                }
                symCache[index] = addr;
                return addr;
            }

            ApplyRel(rel, relsz, relent, bias, symtab, syment, SymAddr);
            ApplyRel(jmprel, pltrelsz, relent, bias, symtab, syment, SymAddr);

            // Set each segment to its real permissions (text R+X, data R+W) before running any code.
            foreach (Segment l in loads)
            {
                long start = AlignDown(bias + l.Vaddr);
                long end = AlignUp(bias + l.Vaddr + l.Memsz);
                int prot = ((l.Flags & PF_R) != 0 ? ProtRead : 0) | ((l.Flags & PF_W) != 0 ? ProtWrite : 0)
                           | ((l.Flags & PF_X) != 0 ? ProtExec : 0);
                if (Mprotect(Ptr(start), (UIntPtr)(ulong)(end - start), prot) != 0)
                {
                    throw new InvalidOperationException("mprotect failed, errno " + Marshal.GetLastWin32Error());
                }
            }

            if (init != 0)
            {
                Marshal.GetDelegateForFunctionPointer<InitFn>(Ptr(init))();
            }
            for (long o = 0; o < initArraySz; o += 4)
            {
                uint fnAddr = (uint)Marshal.ReadInt32(Ptr(initArray + o));
                if (fnAddr != 0 && fnAddr != 0xffffffff)
                {
                    Marshal.GetDelegateForFunctionPointer<InitFn>(Ptr(fnAddr))();
                }
            }

            // Export table (defined, named symbols). dynstr follows dynsym, so count = (strtab - symtab)/syment.
            var exports = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            long count = (strtab - symtab) / syment;
            for (long i = 0; i < count; i++)
            {
                long s = symtab + i * syment;
                uint stName = (uint)Marshal.ReadInt32(Ptr(s), 0);
                uint stValue = (uint)Marshal.ReadInt32(Ptr(s), 4);
                ushort shndx = (ushort)Marshal.ReadInt16(Ptr(s), 14);
                if (shndx == SHN_UNDEF || stName == 0 || stName >= strsz)
                {
                    continue;
                }
                string name = Marshal.PtrToStringAnsi(Ptr(strtab + stName));
                if (!string.IsNullOrEmpty(name) && !exports.ContainsKey(name))
                {
                    exports[name] = Ptr(bias + stValue);
                }
            }

            return new ElfImage(@base, exports);
        }

        private static void ApplyRel(long rel, long size, long entsize, long bias, long symtab, long syment, Func<int, IntPtr> symAddr)
        {
            for (long o = 0; o < size; o += entsize)
            {
                IntPtr where = Ptr(rel + o);
                uint rOffset = (uint)Marshal.ReadInt32(where, 0);
                uint rInfo = (uint)Marshal.ReadInt32(where, 4);
                int type = (int)(rInfo & 0xff);
                int sym = (int)(rInfo >> 8);
                IntPtr target = Ptr(bias + rOffset);
                int addend = Marshal.ReadInt32(target);  // REL: addend is in place

                switch (type)
                {
                    case R_ARM_RELATIVE:
                        Marshal.WriteInt32(target, (int)(bias + (uint)addend));
                        break;
                    case R_ARM_JUMP_SLOT:
                        // A PLT slot's in-place value is the lazy-resolver stub, not an addend: overwrite with
                        // S (this is what glibc's ARM loader does). Treating it as S + A was the init crash (#15).
                        Marshal.WriteInt32(target, (int)symAddr(sym).ToInt64());
                        break;
                    case R_ARM_GLOB_DAT:
                    case R_ARM_ABS32:
                        Marshal.WriteInt32(target, (int)(symAddr(sym).ToInt64() + addend));
                        break;
                    case R_ARM_TLS_DTPMOD32:
                        Marshal.WriteInt32(target, 1);  // single TLS module; core's own __tls_get_addr ignores it
                        break;
                    case R_ARM_TLS_DTPOFF32:
                        uint stValue = (uint)Marshal.ReadInt32(Ptr(symtab + sym * syment), 4);
                        Marshal.WriteInt32(target, (int)(stValue + (uint)addend));
                        break;
                    default:
                        throw new BadImageFormatException("unhandled ARM relocation type " + type);
                }
            }
        }

        private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }
}
