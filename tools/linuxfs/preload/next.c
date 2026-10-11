/*
 * next.c: the definition a hook passes a call on to, as dlsym(RTLD_NEXT, name) finds it.
 *
 * Before glibc 2.34 a thread's first dlsym callocs dlerror's state. A hook first called from inside
 * an allocator that is still starting up - Chromium's PartitionAlloc calls syscall() from its first
 * malloc - re-enters the allocator there, and it waits for itself forever: every Electron and
 * Chromium program on Steam's x86 runtime (glibc 2.31) hung at start. There the libraries after this
 * one are searched by hand, with nothing allocated; an object or symbol that walk cannot handle is
 * left to dlsym.
 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <link.h>
#include <stdint.h>
#include <string.h>

/* NEXT_TEST: tools/tests builds the walk on a newer glibc. */
#if __GLIBC_PREREQ(2, 34) && !defined(NEXT_TEST)

__attribute__((visibility("hidden"))) void *bl_next(const char *name) {
  return dlsym(RTLD_NEXT, name);
}

#else

struct search {
  const char *name;
  uint32_t hash;
  int passed;
  int unsure;
  void *found;
};

static uint32_t gnu_hash(const char *name) {
  uint32_t h = 5381;
  for (; *name; name++) h = h * 33 + (unsigned char)*name;
  return h;
}

/* glibc relocates these entries in place, but not every object's (the vDSO's). */
static ElfW(Addr) address(const struct dl_phdr_info *info, ElfW(Addr) value) {
  return value < info->dlpi_addr ? info->dlpi_addr + value : value;
}

/* The object's definition, as glibc 2.36 and later pick it: unversioned or the default version. */
static void find(const struct dl_phdr_info *info, const ElfW(Dyn) *dyn, struct search *s) {
  const uint32_t *table = NULL;
  const ElfW(Sym) *symbols = NULL;
  const char *strings = NULL;
  const ElfW(Half) *versions = NULL;
  for (; dyn->d_tag != DT_NULL; dyn++) {
    if (dyn->d_tag == DT_GNU_HASH) table = (const uint32_t *)address(info, dyn->d_un.d_ptr);
    else if (dyn->d_tag == DT_SYMTAB) symbols = (const ElfW(Sym) *)address(info, dyn->d_un.d_ptr);
    else if (dyn->d_tag == DT_STRTAB) strings = (const char *)address(info, dyn->d_un.d_ptr);
    else if (dyn->d_tag == DT_VERSYM) versions = (const ElfW(Half) *)address(info, dyn->d_un.d_ptr);
  }
  if (!table || !symbols || !strings || !table[0]) {
    s->unsure = 1;
    return;
  }
  const uint32_t *buckets = (const uint32_t *)((const ElfW(Addr) *)(table + 4) + table[2]);
  const uint32_t *chain = buckets + table[0] - table[1];
  const ElfW(Sym) *only = NULL;
  int matches = 0, defaults = 0;
  for (uint32_t i = buckets[s->hash % table[0]]; i >= table[1] && i != 0; i++) {
    const ElfW(Sym) *sym = &symbols[i];
    if ((chain[i] | 1) == (s->hash | 1) && sym->st_shndx != SHN_UNDEF && sym->st_value != 0 &&
        ELF64_ST_BIND(sym->st_info) != STB_LOCAL && !strcmp(strings + sym->st_name, s->name)) {
      if (ELF64_ST_TYPE(sym->st_info) == STT_GNU_IFUNC || ELF64_ST_TYPE(sym->st_info) == STT_TLS) {
        s->unsure = 1;
        return;
      }
      if (!versions || (versions[i] & 0x7fff) < 2) {
        only = sym;
        defaults = 1;
        break;
      }
      matches++;
      if (!(versions[i] & 0x8000) && defaults++ == 0) only = sym;
    }
    if (chain[i] & 1) break;
  }
  /* Only hidden versions: dlsym's own rules decide. */
  if (defaults == 1) s->found = (void *)(info->dlpi_addr + only->st_value);
  else if (matches) s->unsure = 1;
}

static int visit(struct dl_phdr_info *info, size_t size, void *data) {
  struct search *s = data;
  const ElfW(Dyn) *dyn = NULL;
  (void)size;
  for (int i = 0; i < info->dlpi_phnum; i++)
    if (info->dlpi_phdr[i].p_type == PT_DYNAMIC) dyn = (const ElfW(Dyn) *)(info->dlpi_addr + info->dlpi_phdr[i].p_vaddr);
  if (!dyn) return 0;
  if (!s->passed) {
    s->passed = dyn == _DYNAMIC;
    return 0;
  }
  find(info, dyn, s);
  return s->found || s->unsure;
}

__attribute__((visibility("hidden"))) void *bl_next(const char *name) {
  struct search s = {name, gnu_hash(name), 0, 0, NULL};
  dl_iterate_phdr(visit, &s);
  if (s.found || (s.passed && !s.unsure)) return s.found;
  return dlsym(RTLD_NEXT, name);
}

#endif
