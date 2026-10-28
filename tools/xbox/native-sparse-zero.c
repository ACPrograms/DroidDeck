#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <sys/syscall.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>
#include <stdatomic.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <sys/vfs.h>
#include <unistd.h>

/* Preserve memset's bytes while avoiding eager allocation of large tmpfs IPC
 * buffers under TCG. Never change a game executable or a Steam program file. */
extern void *compat_dlvsym(void *, const char *, const char *);
__asm__(".symver compat_dlvsym,dlvsym@GLIBC_2.2.5");
static void *(*next_memset)(void *, int, size_t);
static _Thread_local int inside;
static int enabled;
static size_t page_size;
static int status_fd = -1;
static atomic_flag reported = ATOMIC_FLAG_INIT;

__attribute__((constructor)) static void initialize(void)
{
    inside = 1;
    next_memset = compat_dlvsym(RTLD_NEXT, "memset", "GLIBC_2.2.5");
    page_size = (size_t)sysconf(_SC_PAGESIZE);
    char executable[512];
    ssize_t length = readlink("/proc/self/exe", executable, sizeof(executable) - 1);
    const char *option = getenv("DROIDDECK_SPARSE_ZERO");
    if (length > 0 && option && !strcmp(option, "1")) {
        executable[length] = '\0';
        const char *name = strrchr(executable, '/');
        name = name ? name + 1 : executable;
        enabled = !strcmp(name, "steamwebhelper");
#ifdef DROIDDECK_SPARSE_ZERO_FIXTURE
        const char *fixture = getenv("DROIDDECK_SPARSE_ZERO_TEST");
        enabled |= fixture && !strcmp(fixture, "1");
#endif
        /* Optional diagnostics contain fixed states only, never buffer data. */
        const char *directory = getenv("DROIDDECK_SPARSE_ZERO_STATUS_DIR");
        if (enabled && directory) {
            char path[768];
            int count = snprintf(path, sizeof(path), "%s/marker-%d.status", directory, getpid());
            if (count > 0 && (size_t)count < sizeof(path)) {
                status_fd = open(path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
                if (status_fd >= 0) {
                    static const char state[] = "HELPER_LOADED\n";
                    if (write(status_fd, state, sizeof(state) - 1) != (ssize_t)(sizeof(state) - 1)) {
                        close(status_fd);
                        status_fd = -1;
                    }
                }
            }
        }
    }
    inside = 0;
}

static int hex_number(const char **cursor, unsigned long *value)
{
    const char *text = *cursor;
    unsigned long number = 0;
    int digits = 0;
    for (;;) {
        unsigned char c = (unsigned char)*text;
        unsigned int digit;
        if (c >= '0' && c <= '9') digit = c - '0';
        else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
        else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
        else break;
        if (number > (ULONG_MAX - digit) / 16) return 0;
        number = number * 16 + digit;
        ++digits;
        ++text;
    }
    *cursor = text;
    *value = number;
    return digits != 0;
}

static void spaces(const char **cursor)
{
    while (**cursor == ' ') ++*cursor;
}

static int qualifying_mapping(const char *line, uintptr_t address, size_t length,
                              dev_t device)
{
    unsigned long start, end, offset, device_major, device_minor;
    const char *text = line;
    if (!hex_number(&text, &start) || *text != '-') return 0;
    ++text;
    if (!hex_number(&text, &end) || address < start || address >= end ||
        length > end - address) return 0;
    spaces(&text);
    if (*text++ != 'r' || *text++ != 'w' || !*text++) return 0;
    if (*text++ != 's' || *text != ' ') return 0;
    spaces(&text);
    if (!hex_number(&text, &offset) || *text != ' ') return 0;
    spaces(&text);
    if (!hex_number(&text, &device_major) || *text != ':') return 0;
    ++text;
    if (!hex_number(&text, &device_minor)) return 0;
    return device_major == major(device) && device_minor == minor(device);
}

static int writable_shared_tmpfs(uintptr_t address, size_t length)
{
    struct stat device;
    struct statfs filesystem;
    if (syscall(SYS_newfstatat, AT_FDCWD, "/dev/shm", &device, 0) ||
        syscall(SYS_statfs, "/dev/shm", &filesystem) ||
        filesystem.f_type != 0x01021994 || length > UINTPTR_MAX - address)
        return 0;

    /* Current VMA metadata prevents stale mmap bookkeeping from qualifying a
     * replacement mapping. Parse only numeric prefixes, never file names or
     * mapped contents. No allocation or stdio locks inside this memset hook. */
    int fd = open("/proc/self/maps", O_RDONLY | O_CLOEXEC);
    if (fd < 0) return 0;
    char buffer[4096], prefix[160];
    size_t used = 0;
    ssize_t count;
    int matched = 0;
    while (!matched && (count = read(fd, buffer, sizeof(buffer))) > 0) {
        for (ssize_t i = 0; i < count; ++i) {
            if (buffer[i] == '\n') {
                prefix[used] = '\0';
                matched = qualifying_mapping(prefix, address, length, device.st_dev);
                used = 0;
                if (matched) break;
            } else if (used < sizeof(prefix) - 1) {
                prefix[used++] = buffer[i];
            }
        }
    }
    close(fd);
    return matched;
}

void *memset(void *destination, int value, size_t length)
{
    if (!next_memset) {
        volatile unsigned char *bytes = destination;
        for (size_t i = 0; i < length; ++i)
            bytes[i] = (unsigned char)value;
        return destination;
    }
    if (!enabled || inside || (unsigned char)value || length < 1048576 ||
        !page_size || (uintptr_t)destination % page_size)
        return next_memset(destination, value, length);

    int saved_errno = errno;
    inside = 1;
    size_t full_pages = length - length % page_size;
    int removed = writable_shared_tmpfs((uintptr_t)destination, length) &&
                  madvise(destination, full_pages, MADV_REMOVE) == 0;
    if (removed) {
        next_memset((char *)destination + full_pages, 0, length - full_pages);
        if (!atomic_flag_test_and_set(&reported) && status_fd >= 0) {
            static const char state[] = "SPARSE_ZERO_USED\n";
            if (write(status_fd, state, sizeof(state) - 1) != (ssize_t)(sizeof(state) - 1)) {
                close(status_fd);
                status_fd = -1;
            }
        }
    }
    inside = 0;
    errno = saved_errno;
    return removed ? destination : next_memset(destination, value, length);
}
