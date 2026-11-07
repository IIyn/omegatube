// Host test of the custom ELF loader used on Tizen 5.5 (GitHub issue #15). It mirrors the algorithm in
// app/OmegaTube/ElfLoader.cs: map the core's PT_LOAD segments into anonymous memory, apply the REL
// relocations, resolve imports against the system libraries, set each segment's real permissions, run the
// C++ constructors, then call omega_version()/omega_selftest() resolved from the dynamic symbol table.
//
// UEP (the kernel check that forces this approach) exists only on the TV, so it cannot be tested here; what
// IS tested is everything else - relocation maths, Thumb interworking, the native __tls_get_addr, the C++
// constructors - against the real cross-built core, run under qemu-arm. If omega_selftest() returns 0 the
// loader is sound. Soft-float ARM to match the core's ABI (build it with arm-linux-gnueabi-gcc, run under
// qemu-arm). Exit 0 = pass.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <elf.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

typedef const char* (*version_fn)(void);
typedef int (*selftest_fn)(void);
typedef void (*init_fn)(void);

#define FAIL(...) do { fprintf(stderr, "FAIL: " __VA_ARGS__); return 1; } while (0)

int main(int argc, char** argv) {
    if (argc < 3) { fprintf(stderr, "usage: %s <libomegacore.so> <expected-version>\n", argv[0]); return 2; }
    const char* want = argv[2];

    FILE* f = fopen(argv[1], "rb");
    if (!f) { perror("fopen"); return 1; }
    fseek(f, 0, SEEK_END); long sz = ftell(f); fseek(f, 0, SEEK_SET);
    uint8_t* img = malloc(sz);
    if (fread(img, 1, sz, f) != (size_t)sz) { perror("fread"); return 1; }
    fclose(f);

    Elf32_Ehdr* eh = (Elf32_Ehdr*)img;
    if (memcmp(eh->e_ident, ELFMAG, 4) || eh->e_machine != EM_ARM || eh->e_type != ET_DYN)
        FAIL("not an ARM ET_DYN ELF\n");

    uint32_t minv = 0xffffffff, maxv = 0, dynv = 0, dyns = 0;
    struct { uint32_t vaddr, memsz; int flags; } loads[16]; int nloads = 0;
    for (int i = 0; i < eh->e_phnum; i++) {
        Elf32_Phdr* ph = (Elf32_Phdr*)(img + eh->e_phoff + i * eh->e_phentsize);
        if (ph->p_type == PT_LOAD) {
            if (ph->p_vaddr < minv) minv = ph->p_vaddr;
            if (ph->p_vaddr + ph->p_memsz > maxv) maxv = ph->p_vaddr + ph->p_memsz;
            if (nloads < 16) { loads[nloads].vaddr = ph->p_vaddr; loads[nloads].memsz = ph->p_memsz; loads[nloads].flags = ph->p_flags; nloads++; }
        } else if (ph->p_type == PT_DYNAMIC) { dynv = ph->p_vaddr; dyns = ph->p_memsz; }
    }
    if (minv == 0xffffffff || !dynv) FAIL("no PT_LOAD/PT_DYNAMIC\n");

    uint32_t lo = minv & ~0xfffu, hi = (maxv + 0xfff) & ~0xfffu;
    uint8_t* base = mmap(NULL, hi - lo, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (base == MAP_FAILED) { perror("mmap"); return 1; }
    long bias = (long)base - lo;

    for (int i = 0; i < eh->e_phnum; i++) {
        Elf32_Phdr* ph = (Elf32_Phdr*)(img + eh->e_phoff + i * eh->e_phentsize);
        if (ph->p_type == PT_LOAD && ph->p_filesz) memcpy(base + ph->p_vaddr, img + ph->p_offset, ph->p_filesz);
    }

    Elf32_Dyn* dyn = (Elf32_Dyn*)(bias + dynv);
    Elf32_Sym* symtab = NULL; const char* strtab = NULL;
    Elf32_Rel* rel = NULL; uint32_t relsz = 0; Elf32_Rel* jmprel = NULL; uint32_t pltrelsz = 0;
    uint32_t initf = 0, initarr = 0, initarrsz = 0;
    for (Elf32_Dyn* d = dyn; (uint8_t*)d < (uint8_t*)dyn + dyns && d->d_tag != DT_NULL; d++) {
        switch (d->d_tag) {
            case DT_SYMTAB: symtab = (Elf32_Sym*)(bias + d->d_un.d_ptr); break;
            case DT_STRTAB: strtab = (const char*)(bias + d->d_un.d_ptr); break;
            case DT_REL: rel = (Elf32_Rel*)(bias + d->d_un.d_ptr); break;
            case DT_RELSZ: relsz = d->d_un.d_val; break;
            case DT_JMPREL: jmprel = (Elf32_Rel*)(bias + d->d_un.d_ptr); break;
            case DT_PLTRELSZ: pltrelsz = d->d_un.d_val; break;
            case DT_INIT: initf = d->d_un.d_ptr; break;
            case DT_INIT_ARRAY: initarr = d->d_un.d_ptr; break;
            case DT_INIT_ARRAYSZ: initarrsz = d->d_un.d_val; break;
            case DT_RELA: FAIL("RELA not supported\n");
        }
    }
    if (!symtab || !strtab) FAIL("missing SYMTAB/STRTAB\n");

    for (Elf32_Dyn* d = dyn; (uint8_t*)d < (uint8_t*)dyn + dyns && d->d_tag != DT_NULL; d++)
        if (d->d_tag == DT_NEEDED) {
            const char* name = strtab + d->d_un.d_val;
            if (strncmp(name, "ld-linux", 8)) dlopen(name, RTLD_NOW | RTLD_GLOBAL);
        }

    int missing = 0;
    Elf32_Rel* tables[2] = { rel, jmprel };
    uint32_t sizes[2] = { relsz, pltrelsz };
    for (int t = 0; t < 2; t++) {
        if (!tables[t]) continue;
        for (uint32_t o = 0; o < sizes[t]; o += sizeof(Elf32_Rel)) {
            Elf32_Rel* r = (Elf32_Rel*)((uint8_t*)tables[t] + o);
            int type = ELF32_R_TYPE(r->r_info), si = ELF32_R_SYM(r->r_info);
            uint32_t* where = (uint32_t*)(bias + r->r_offset);
            uint32_t A = *where;
            if (type == R_ARM_RELATIVE) { *where = bias + A; }
            else if (type == R_ARM_GLOB_DAT || type == R_ARM_JUMP_SLOT || type == R_ARM_ABS32) {
                Elf32_Sym* s = &symtab[si];
                uint32_t val;
                if (s->st_shndx != SHN_UNDEF) val = bias + s->st_value;
                else {
                    const char* nm = strtab + s->st_name;
                    void* p = dlsym(RTLD_DEFAULT, nm);
                    if (!p && ELF32_ST_BIND(s->st_info) != STB_WEAK) { fprintf(stderr, "[missing] %s\n", nm); missing++; }
                    val = (uint32_t)(uintptr_t)p;
                }
                *where = (type == R_ARM_JUMP_SLOT) ? val : val + A;  // JUMP_SLOT overwrites (glibc), no addend
            } else if (type == R_ARM_TLS_DTPMOD32) { *where = 1; }
            else if (type == R_ARM_TLS_DTPOFF32) { *where = symtab[si].st_value + A; }
            else FAIL("unhandled reloc %d\n", type);
        }
    }
    if (missing) FAIL("%d unresolved imports\n", missing);

    for (int i = 0; i < nloads; i++) {
        long start = (bias + loads[i].vaddr) & ~0xfffL;
        long end = (bias + loads[i].vaddr + loads[i].memsz + 0xfff) & ~0xfffL;
        int prot = ((loads[i].flags & PF_R) ? PROT_READ : 0) | ((loads[i].flags & PF_W) ? PROT_WRITE : 0)
                 | ((loads[i].flags & PF_X) ? PROT_EXEC : 0);
        if (mprotect((void*)start, end - start, prot) != 0) { perror("mprotect"); return 1; }
    }

    if (initf) ((init_fn)(bias + initf))();
    for (uint32_t o = 0; o < initarrsz; o += 4) { init_fn fn = *(init_fn*)(bias + initarr + o); if (fn) fn(); }

    long count = ((const char*)strtab - (const char*)symtab) / sizeof(Elf32_Sym);
    version_fn ver = NULL; selftest_fn st = NULL;
    for (long i = 0; i < count; i++) {
        if (symtab[i].st_shndx == SHN_UNDEF || !symtab[i].st_name) continue;
        const char* nm = strtab + symtab[i].st_name;
        if (!strcmp(nm, "omega_version")) ver = (version_fn)(bias + symtab[i].st_value);
        else if (!strcmp(nm, "omega_selftest")) st = (selftest_fn)(bias + symtab[i].st_value);
    }
    if (!ver || !st) FAIL("omega_version/omega_selftest not found\n");

    const char* v = ver();
    int rc = st();
    printf("omega_version=%s omega_selftest=%d\n", v ? v : "(null)", rc);
    if (!v || strcmp(v, want)) FAIL("version mismatch (want %s)\n", want);
    if (rc != 0) FAIL("selftest returned %d\n", rc);
    printf("PASS: custom ELF loader ran the real core\n");
    return 0;
}
