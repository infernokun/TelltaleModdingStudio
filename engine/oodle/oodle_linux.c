/*
 * Loads a game's own Oodle DLL (oo2core_N_win64.dll) on Linux and calls
 * OodleLZ_Decompress in it, so Oodle-compressed archives open there too.
 *
 * Oodle is proprietary and nothing of it is bundled: this maps the DLL the game
 * ships into memory, applies its relocations, points its KERNEL32 imports at the
 * small set of stand-ins below and runs its C runtime start-up, then calls the
 * export with the Windows x64 calling convention.
 *
 * The only Windows thread-block read in the DLL is the stack limit in __chkstk
 * (gs:[0x10]). Each call points the calling thread's gs at a zeroed block, where
 * a stack limit of 0 means nothing needs probing, and puts gs back afterwards.
 *
 * Build: make -C engine/oodle (gives libtelltale_oodle.so). x86-64 Linux only.
 */
#define _GNU_SOURCE
#include <asm/prctl.h>
#include <ctype.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

#define WINAPI __attribute__((ms_abi))
#define EXPORT __attribute__((visibility("default")))

typedef int32_t BOOL;
typedef uint32_t DWORD;
typedef uint16_t WCHAR;
typedef void *HANDLE;

/* ---- the calling thread's stand-in thread block ---------------------------- */

static uint8_t g_teb[0x1000] __attribute__((aligned(4096)));

static unsigned long gs_enter(void)
{
    unsigned long old = 0;
    syscall(SYS_arch_prctl, ARCH_GET_GS, &old);
    syscall(SYS_arch_prctl, ARCH_SET_GS, (unsigned long)g_teb);
    return old;
}

static void gs_leave(unsigned long old)
{
    syscall(SYS_arch_prctl, ARCH_SET_GS, old);
}

/* ---- loaded modules --------------------------------------------------------- */

typedef struct {
    uint8_t *base;
    size_t size;
    char path[4096];
} module_t;

static module_t g_modules[8];
static int g_module_count;
static pthread_mutex_t g_load_lock = PTHREAD_MUTEX_INITIALIZER;

static module_t *module_at(const void *p)
{
    for (int i = 0; i < g_module_count; i++)
        if ((const uint8_t *)p >= g_modules[i].base && (const uint8_t *)p < g_modules[i].base + g_modules[i].size)
            return &g_modules[i];
    return NULL;
}

static module_t *main_module(void)
{
    return g_module_count ? &g_modules[0] : NULL;
}

/* ---- KERNEL32 stand-ins ----------------------------------------------------- */

static __thread DWORD t_last_error;

/* HeapAlloc blocks carry their size, for HeapSize and HeapReAlloc. 16 bytes keeps
   the 16-byte alignment Windows heaps give. */
#define HEAP_ZERO_MEMORY 0x8
#define HEAP_HEADER 16

static void *heap_alloc(size_t n, int zero)
{
    uint8_t *p = zero ? calloc(1, n + HEAP_HEADER) : malloc(n + HEAP_HEADER);
    if (!p)
        return NULL;
    *(size_t *)p = n;
    return p + HEAP_HEADER;
}

static size_t heap_size(const void *p)
{
    return *(const size_t *)((const uint8_t *)p - HEAP_HEADER);
}

WINAPI static void *k_HeapAlloc(HANDLE heap, DWORD flags, size_t n)
{
    (void)heap;
    return heap_alloc(n, (flags & HEAP_ZERO_MEMORY) != 0);
}

WINAPI static BOOL k_HeapFree(HANDLE heap, DWORD flags, void *p)
{
    (void)heap, (void)flags;
    if (p)
        free((uint8_t *)p - HEAP_HEADER);
    return 1;
}

WINAPI static void *k_HeapReAlloc(HANDLE heap, DWORD flags, void *p, size_t n)
{
    (void)heap;
    if (!p)
        return heap_alloc(n, (flags & HEAP_ZERO_MEMORY) != 0);
    size_t old = heap_size(p);
    uint8_t *q = realloc((uint8_t *)p - HEAP_HEADER, n + HEAP_HEADER);
    if (!q)
        return NULL;
    *(size_t *)q = n;
    if ((flags & HEAP_ZERO_MEMORY) && n > old)
        memset(q + HEAP_HEADER + old, 0, n - old);
    return q + HEAP_HEADER;
}

WINAPI static size_t k_HeapSize(HANDLE heap, DWORD flags, const void *p)
{
    (void)heap, (void)flags;
    return p ? heap_size(p) : (size_t)-1;
}

WINAPI static HANDLE k_GetProcessHeap(void) { return (HANDLE)1; }

/* Critical sections hold a pointer to a recursive mutex in their first 8 bytes. */
WINAPI static BOOL k_InitializeCriticalSectionAndSpinCount(void *cs, DWORD spin)
{
    (void)spin;
    pthread_mutexattr_t attr;
    pthread_mutexattr_init(&attr);
    pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutex_t *m = malloc(sizeof *m);
    if (!m)
        return 0;
    pthread_mutex_init(m, &attr);
    memset(cs, 0, 40);
    *(pthread_mutex_t **)cs = m;
    return 1;
}

WINAPI static void k_EnterCriticalSection(void *cs) { pthread_mutex_lock(*(pthread_mutex_t **)cs); }
WINAPI static void k_LeaveCriticalSection(void *cs) { pthread_mutex_unlock(*(pthread_mutex_t **)cs); }

WINAPI static void k_DeleteCriticalSection(void *cs)
{
    pthread_mutex_t *m = *(pthread_mutex_t **)cs;
    if (m) {
        pthread_mutex_destroy(m);
        free(m);
    }
    *(pthread_mutex_t **)cs = NULL;
}

#define TLS_OUT_OF_INDEXES 0xFFFFFFFFu

WINAPI static DWORD k_TlsAlloc(void)
{
    pthread_key_t key;
    return pthread_key_create(&key, NULL) == 0 ? (DWORD)key : TLS_OUT_OF_INDEXES;
}

WINAPI static void *k_TlsGetValue(DWORD i)
{
    t_last_error = 0;
    return pthread_getspecific((pthread_key_t)i);
}

WINAPI static BOOL k_TlsSetValue(DWORD i, void *v) { return pthread_setspecific((pthread_key_t)i, v) == 0; }
WINAPI static BOOL k_TlsFree(DWORD i) { return pthread_key_delete((pthread_key_t)i) == 0; }

WINAPI static DWORD k_GetLastError(void) { return t_last_error; }
WINAPI static void k_SetLastError(DWORD e) { t_last_error = e; }

WINAPI static void *k_EncodePointer(void *p) { return p; }
WINAPI static void *k_DecodePointer(void *p) { return p; }

WINAPI static BOOL k_IsDebuggerPresent(void) { return 0; }

WINAPI static BOOL k_IsProcessorFeaturePresent(DWORD feature)
{
    /* SSE, SSE2 and SSE3, which every x86-64 CPU this runs on has. */
    return feature == 6 || feature == 10 || feature == 13;
}

WINAPI static void k_OutputDebugStringA(const char *s) { (void)s; }
WINAPI static void k_OutputDebugStringW(const WCHAR *s) { (void)s; }

static char g_command_line[] = "oodle";
WINAPI static char *k_GetCommandLineA(void) { return g_command_line; }

WINAPI static DWORD k_GetCurrentThreadId(void) { return (DWORD)syscall(SYS_gettid); }
WINAPI static DWORD k_GetCurrentProcessId(void) { return (DWORD)getpid(); }
WINAPI static HANDLE k_GetCurrentProcess(void) { return (HANDLE)(intptr_t)-1; }

WINAPI static void k_Sleep(DWORD ms) { usleep((useconds_t)ms * 1000); }

WINAPI static BOOL k_QueryPerformanceCounter(int64_t *count)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    *count = (int64_t)ts.tv_sec * 1000000000 + ts.tv_nsec;
    return 1;
}

WINAPI static void k_GetSystemTimeAsFileTime(uint64_t *ft)
{
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    *ft = (uint64_t)ts.tv_sec * 10000000 + (uint64_t)ts.tv_nsec / 100 + 116444736000000000ull;
}

WINAPI static void k_GetStartupInfoW(void *info)
{
    memset(info, 0, 104);
    *(DWORD *)info = 104;
}

static WCHAR g_environment[2];
WINAPI static WCHAR *k_GetEnvironmentStringsW(void) { return g_environment; }
WINAPI static BOOL k_FreeEnvironmentStringsW(WCHAR *env) { (void)env; return 1; }

/* Modules: only the loaded DLLs are known; anything else isn't there. */
WINAPI static HANDLE k_GetModuleHandleW(const WCHAR *name)
{
    if (name)
        return NULL;
    module_t *m = main_module();
    return m ? m->base : NULL;
}

#define GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS 4

WINAPI static BOOL k_GetModuleHandleExW(DWORD flags, const WCHAR *name, HANDLE *out)
{
    module_t *m = (flags & GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS) ? module_at(name) : (name ? NULL : main_module());
    *out = m ? m->base : NULL;
    return m != NULL;
}

WINAPI static DWORD k_GetModuleFileNameA(HANDLE module, char *buf, DWORD size)
{
    module_t *m = module ? module_at(module) : main_module();
    if (!m || size == 0)
        return 0;
    DWORD n = (DWORD)strlen(m->path);
    if (n >= size)
        n = size - 1;
    memcpy(buf, m->path, n);
    buf[n] = 0;
    return n;
}

WINAPI static DWORD k_GetModuleFileNameW(HANDLE module, WCHAR *buf, DWORD size)
{
    module_t *m = module ? module_at(module) : main_module();
    if (!m || size == 0)
        return 0;
    DWORD n = (DWORD)strlen(m->path);
    if (n >= size)
        n = size - 1;
    for (DWORD i = 0; i < n; i++)
        buf[i] = (unsigned char)m->path[i];
    buf[n] = 0;
    return n;
}

WINAPI static HANDLE k_LoadLibraryExW(const WCHAR *name, HANDLE file, DWORD flags)
{
    (void)name, (void)file, (void)flags;
    return NULL;
}

static void *export_of(const module_t *m, const char *name);

WINAPI static void *k_GetProcAddress(HANDLE module, const char *name)
{
    module_t *m = module_at(module);
    return m && (uintptr_t)name > 0xFFFF ? export_of(m, name) : NULL;
}

/* Code pages: a single-byte one, with ASCII-only conversions and character classes. */
WINAPI static uint32_t k_GetACP(void) { return 1252; }
WINAPI static uint32_t k_GetOEMCP(void) { return 437; }
WINAPI static BOOL k_IsValidCodePage(uint32_t cp) { (void)cp; return 1; }

WINAPI static BOOL k_GetCPInfo(uint32_t cp, uint8_t *info)
{
    (void)cp;
    memset(info, 0, 20);
    *(uint32_t *)info = 1; /* MaxCharSize */
    info[4] = '?';         /* DefaultChar */
    return 1;
}

static int wide_length(const WCHAR *s)
{
    int n = 0;
    while (s[n])
        n++;
    return n;
}

WINAPI static int k_MultiByteToWideChar(uint32_t cp, DWORD flags, const char *src, int src_len, WCHAR *dst, int dst_len)
{
    (void)cp, (void)flags;
    if (src_len < 0)
        src_len = (int)strlen(src) + 1;
    if (dst_len == 0)
        return src_len;
    int n = src_len < dst_len ? src_len : dst_len;
    for (int i = 0; i < n; i++)
        dst[i] = (unsigned char)src[i];
    return n;
}

WINAPI static int k_WideCharToMultiByte(uint32_t cp, DWORD flags, const WCHAR *src, int src_len, char *dst, int dst_len,
                                        const char *default_char, BOOL *used_default)
{
    (void)cp, (void)flags, (void)default_char;
    if (src_len < 0)
        src_len = wide_length(src) + 1;
    if (used_default)
        *used_default = 0;
    if (dst_len == 0)
        return src_len;
    int n = src_len < dst_len ? src_len : dst_len;
    for (int i = 0; i < n; i++)
        dst[i] = src[i] < 256 ? (char)src[i] : '?';
    return n;
}

WINAPI static BOOL k_GetStringTypeW(DWORD type, const WCHAR *src, int len, uint16_t *out)
{
    if (len < 0)
        len = wide_length(src) + 1;
    for (int i = 0; i < len; i++) {
        uint16_t c = src[i], t = 0;
        if (type == 1 /* CT_CTYPE1 */ && c < 128) {
            if (isupper(c)) t |= 0x001;
            if (islower(c)) t |= 0x002;
            if (isdigit(c)) t |= 0x004;
            if (isspace(c)) t |= 0x008;
            if (ispunct(c)) t |= 0x010;
            if (iscntrl(c)) t |= 0x020;
            if (c == ' ' || c == '\t') t |= 0x040;
            if (isxdigit(c)) t |= 0x080;
            if (isalpha(c)) t |= 0x100;
            t |= 0x200; /* C1_DEFINED */
        }
        out[i] = t;
    }
    return 1;
}

#define LCMAP_LOWERCASE 0x100
#define LCMAP_UPPERCASE 0x200

WINAPI static int k_LCMapStringW(DWORD locale, DWORD flags, const WCHAR *src, int len, WCHAR *dst, int dst_len)
{
    (void)locale;
    if (len < 0)
        len = wide_length(src) + 1;
    if (dst_len == 0)
        return len;
    int n = len < dst_len ? len : dst_len;
    for (int i = 0; i < n; i++) {
        WCHAR c = src[i];
        if (c < 128 && (flags & LCMAP_LOWERCASE))
            c = (WCHAR)tolower(c);
        else if (c < 128 && (flags & LCMAP_UPPERCASE))
            c = (WCHAR)toupper(c);
        dst[i] = c;
    }
    return n;
}

/* Files and the console: there are none, and writes go nowhere. */
#define INVALID_HANDLE_VALUE ((HANDLE)(intptr_t)-1)

WINAPI static HANDLE k_GetStdHandle(DWORD which) { (void)which; return INVALID_HANDLE_VALUE; }
WINAPI static BOOL k_SetStdHandle(DWORD which, HANDLE h) { (void)which, (void)h; return 1; }
WINAPI static DWORD k_GetFileType(HANDLE h) { (void)h; return 0; }
WINAPI static uint32_t k_GetConsoleCP(void) { return 0; }
WINAPI static BOOL k_GetConsoleMode(HANDLE h, DWORD *mode) { (void)h, (void)mode; return 0; }
WINAPI static BOOL k_FlushFileBuffers(HANDLE h) { (void)h; return 1; }
WINAPI static BOOL k_CloseHandle(HANDLE h) { (void)h; return 1; }
WINAPI static BOOL k_SetFilePointerEx(HANDLE h, int64_t dist, int64_t *pos, DWORD method)
{
    (void)h, (void)dist, (void)pos, (void)method;
    return 0;
}

WINAPI static HANDLE k_CreateFileW(const WCHAR *name, DWORD access, DWORD share, void *security, DWORD creation,
                                   DWORD flags, HANDLE template_file)
{
    (void)name, (void)access, (void)share, (void)security, (void)creation, (void)flags, (void)template_file;
    return INVALID_HANDLE_VALUE;
}

WINAPI static BOOL k_WriteFile(HANDLE h, const void *buf, DWORD n, DWORD *written, void *overlapped)
{
    (void)h, (void)buf, (void)overlapped;
    if (written)
        *written = n;
    return 1;
}

WINAPI static BOOL k_WriteConsoleW(HANDLE h, const void *buf, DWORD n, DWORD *written, void *reserved)
{
    (void)h, (void)buf, (void)reserved;
    if (written)
        *written = n;
    return 1;
}

/* Exceptions and process exit only happen when something has gone badly wrong. */
WINAPI static void k_RaiseException(DWORD code, DWORD flags, DWORD argc, const uintptr_t *argv)
{
    (void)flags, (void)argc, (void)argv;
    fprintf(stderr, "oodle: the Oodle DLL raised exception 0x%08x\n", code);
    abort();
}

WINAPI static void k_ExitProcess(uint32_t code)
{
    fprintf(stderr, "oodle: the Oodle DLL asked to exit (%u)\n", code);
    abort();
}

WINAPI static BOOL k_TerminateProcess(HANDLE process, uint32_t code)
{
    (void)process;
    fprintf(stderr, "oodle: the Oodle DLL asked to terminate (%u)\n", code);
    abort();
}

WINAPI static void k_RtlUnwindEx(void *frame, void *target, void *record, void *value, void *context, void *table)
{
    (void)frame, (void)target, (void)record, (void)value, (void)context, (void)table;
    fprintf(stderr, "oodle: the Oodle DLL tried to unwind an exception\n");
    abort();
}

WINAPI static void k_RtlCaptureContext(void *context) { (void)context; }

WINAPI static void *k_RtlLookupFunctionEntry(uint64_t pc, uint64_t *image_base, void *history)
{
    (void)pc, (void)history;
    *image_base = 0;
    return NULL;
}

WINAPI static void *k_RtlVirtualUnwind(DWORD type, uint64_t base, uint64_t pc, void *entry, void *context, void **data,
                                       uint64_t *frame, void *pointers)
{
    (void)type, (void)base, (void)pc, (void)entry, (void)context, (void)data, (void)frame, (void)pointers;
    return NULL;
}

WINAPI static void *k_RtlPcToFileHeader(void *pc, void **base)
{
    module_t *m = module_at(pc);
    *base = m ? m->base : NULL;
    return *base;
}

WINAPI static long k_UnhandledExceptionFilter(void *pointers) { (void)pointers; return 1; }
WINAPI static void *k_SetUnhandledExceptionFilter(void *filter) { (void)filter; return NULL; }

/* An import this file doesn't provide: returns 0 (failure for almost every call). */
WINAPI static uintptr_t k_missing(void) { return 0; }

typedef struct {
    const char *name;
    void *fn;
} stub_t;

#define STUB(n) { #n, (void *)k_##n }
static const stub_t g_kernel32[] = {
    STUB(CloseHandle), STUB(CreateFileW), STUB(DecodePointer), STUB(DeleteCriticalSection),
    STUB(EncodePointer), STUB(EnterCriticalSection), STUB(ExitProcess), STUB(FlushFileBuffers),
    STUB(FreeEnvironmentStringsW), STUB(GetACP), STUB(GetCPInfo), STUB(GetCommandLineA),
    STUB(GetConsoleCP), STUB(GetConsoleMode), STUB(GetCurrentProcess), STUB(GetCurrentProcessId),
    STUB(GetCurrentThreadId), STUB(GetEnvironmentStringsW), STUB(GetFileType), STUB(GetLastError),
    STUB(GetModuleFileNameA), STUB(GetModuleFileNameW), STUB(GetModuleHandleExW), STUB(GetModuleHandleW),
    STUB(GetOEMCP), STUB(GetProcAddress), STUB(GetProcessHeap), STUB(GetStartupInfoW),
    STUB(GetStdHandle), STUB(GetStringTypeW), STUB(GetSystemTimeAsFileTime), STUB(HeapAlloc),
    STUB(HeapFree), STUB(HeapReAlloc), STUB(HeapSize), STUB(InitializeCriticalSectionAndSpinCount),
    STUB(IsDebuggerPresent), STUB(IsProcessorFeaturePresent), STUB(IsValidCodePage), STUB(LCMapStringW),
    STUB(LeaveCriticalSection), STUB(LoadLibraryExW), STUB(MultiByteToWideChar), STUB(OutputDebugStringA),
    STUB(OutputDebugStringW), STUB(QueryPerformanceCounter), STUB(RaiseException), STUB(RtlCaptureContext),
    STUB(RtlLookupFunctionEntry), STUB(RtlPcToFileHeader), STUB(RtlUnwindEx), STUB(RtlVirtualUnwind),
    STUB(SetFilePointerEx), STUB(SetLastError), STUB(SetStdHandle), STUB(SetUnhandledExceptionFilter),
    STUB(Sleep), STUB(TerminateProcess), STUB(TlsAlloc), STUB(TlsFree),
    STUB(TlsGetValue), STUB(TlsSetValue), STUB(UnhandledExceptionFilter), STUB(WideCharToMultiByte),
    STUB(WriteConsoleW), STUB(WriteFile),
};

static void *kernel32_stub(const char *name)
{
    for (size_t i = 0; i < sizeof g_kernel32 / sizeof g_kernel32[0]; i++)
        if (strcmp(g_kernel32[i].name, name) == 0)
            return g_kernel32[i].fn;
    return NULL;
}

/* ---- the PE loader ---------------------------------------------------------- */

#define RD16(p) (*(const uint16_t *)(p))
#define RD32(p) (*(const uint32_t *)(p))
#define RD64(p) (*(const uint64_t *)(p))

static void set_error(char *err, int err_len, const char *fmt, const char *arg)
{
    if (err && err_len > 0)
        snprintf(err, (size_t)err_len, fmt, arg);
}

static const uint8_t *nt_headers(const uint8_t *base) { return base + RD32(base + 0x3C); }
static const uint8_t *optional_header(const uint8_t *base) { return nt_headers(base) + 24; }

static void data_directory(const uint8_t *base, int index, uint32_t *rva, uint32_t *size)
{
    const uint8_t *dir = optional_header(base) + 112 + 8 * index;
    *rva = RD32(dir);
    *size = RD32(dir + 4);
}

static void *export_of(const module_t *m, const char *name)
{
    uint32_t rva, size;
    data_directory(m->base, 0, &rva, &size);
    if (!rva)
        return NULL;
    const uint8_t *dir = m->base + rva;
    uint32_t count = RD32(dir + 24);
    const uint8_t *functions = m->base + RD32(dir + 28);
    const uint8_t *names = m->base + RD32(dir + 32);
    const uint8_t *ordinals = m->base + RD32(dir + 36);
    for (uint32_t i = 0; i < count; i++) {
        if (strcmp((const char *)m->base + RD32(names + 4 * i), name) != 0)
            continue;
        uint32_t fn = RD32(functions + 4 * RD16(ordinals + 2 * i));
        if (fn >= rva && fn < rva + size)
            return NULL; /* forwarded to another DLL */
        return m->base + fn;
    }
    return NULL;
}

static uint8_t *read_file(const char *path, size_t *size)
{
    FILE *f = fopen(path, "rb");
    if (!f)
        return NULL;
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t *data = n > 0 ? malloc((size_t)n) : NULL;
    if (data && fread(data, 1, (size_t)n, f) != (size_t)n) {
        free(data);
        data = NULL;
    }
    fclose(f);
    *size = (size_t)n;
    return data;
}

static int map_image(module_t *m, const uint8_t *file, size_t file_size, char *err, int err_len)
{
    if (file_size < 0x40 || RD16(file) != 0x5A4D || RD32(file + 0x3C) + 0x108 > file_size ||
        RD32(nt_headers(file)) != 0x00004550) {
        set_error(err, err_len, "%s is not a Windows DLL", m->path);
        return 0;
    }
    const uint8_t *nt = nt_headers(file), *opt = optional_header(file);
    if (RD16(nt + 4) != 0x8664 || RD16(opt) != 0x20B) {
        set_error(err, err_len, "%s is not a 64-bit DLL", m->path);
        return 0;
    }
    uint64_t image_base = RD64(opt + 24);
    size_t image_size = RD32(opt + 56), headers_size = RD32(opt + 60);
    uint16_t sections = RD16(nt + 6), opt_size = RD16(nt + 20);
    const uint8_t *section = opt + opt_size;

    uint8_t *base = mmap((void *)(uintptr_t)image_base, image_size, PROT_READ | PROT_WRITE,
                         MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (base == MAP_FAILED) {
        set_error(err, err_len, "could not map %s", m->path);
        return 0;
    }
    m->base = base;
    m->size = image_size;
    memcpy(base, file, headers_size < file_size ? headers_size : file_size);
    for (int i = 0; i < sections; i++, section += 40) {
        uint32_t va = RD32(section + 12), raw_size = RD32(section + 16), raw = RD32(section + 20);
        uint32_t virtual_size = RD32(section + 8);
        uint32_t n = raw_size < virtual_size || virtual_size == 0 ? raw_size : virtual_size;
        if (raw + (size_t)n > file_size || va + (size_t)n > image_size) {
            set_error(err, err_len, "%s has a damaged section table", m->path);
            return 0;
        }
        memcpy(base + va, file + raw, n);
    }

    /* Relocations, for wherever the image landed. */
    int64_t delta = (int64_t)((uintptr_t)base - image_base);
    uint32_t rva, size;
    data_directory(base, 5, &rva, &size);
    for (uint32_t at = 0; delta && rva && at + 8 <= size;) {
        const uint8_t *block = base + rva + at;
        uint32_t page = RD32(block), block_size = RD32(block + 4);
        if (block_size < 8)
            break;
        for (uint32_t i = 8; i + 2 <= block_size; i += 2) {
            uint16_t entry = RD16(block + i);
            uint8_t *target = base + page + (entry & 0xFFF);
            if (entry >> 12 == 10) /* IMAGE_REL_BASED_DIR64 */
                *(uint64_t *)target += (uint64_t)delta;
            else if (entry >> 12 == 3) /* IMAGE_REL_BASED_HIGHLOW */
                *(uint32_t *)target += (uint32_t)delta;
        }
        at += block_size;
    }

    /* Imports: KERNEL32 from the stand-ins above, everything else fails when called. */
    data_directory(base, 1, &rva, &size);
    for (const uint8_t *desc = base + rva; rva && RD32(desc + 12); desc += 20) {
        const char *dll = (const char *)base + RD32(desc + 12);
        int kernel32 = strcasecmp(dll, "KERNEL32.dll") == 0;
        uint64_t *iat = (uint64_t *)(base + RD32(desc + 16));
        const uint64_t *names = (const uint64_t *)(base + (RD32(desc) ? RD32(desc) : RD32(desc + 16)));
        for (; *names; names++, iat++) {
            void *fn = NULL;
            const char *name = "(by ordinal)";
            if (!(*names >> 63)) {
                name = (const char *)base + (uint32_t)*names + 2;
                fn = kernel32 ? kernel32_stub(name) : NULL;
            }
            if (!fn) {
                fprintf(stderr, "oodle: %s!%s is not provided\n", dll, name);
                fn = (void *)k_missing;
            }
            *iat = (uint64_t)(uintptr_t)fn;
        }
    }

    /* Page protections as the sections ask (headers read-only). */
    mprotect(base, (headers_size + 0xFFF) & ~(size_t)0xFFF, PROT_READ);
    section = opt + opt_size;
    for (int i = 0; i < sections; i++, section += 40) {
        uint32_t va = RD32(section + 12), virtual_size = RD32(section + 8), flags = RD32(section + 36);
        if (!virtual_size)
            virtual_size = RD32(section + 16);
        int prot = (flags & 0x40000000 ? PROT_READ : 0) | (flags & 0x80000000 ? PROT_WRITE : 0) |
                   (flags & 0x20000000 ? PROT_EXEC : 0);
        mprotect(base + (va & ~0xFFFu), ((va & 0xFFFu) + virtual_size + 0xFFF) & ~(size_t)0xFFF, prot);
    }
    return 1;
}

typedef BOOL(WINAPI *dll_main_t)(void *, DWORD, void *);

typedef intptr_t(WINAPI *decompress_t)(const void *comp, intptr_t comp_len, void *raw, intptr_t raw_len,
                                        int fuzz_safe, int check_crc, int verbosity, void *dec_buf_base,
                                        intptr_t dec_buf_size, void *callback, void *callback_data,
                                        void *decoder_memory, intptr_t decoder_memory_size, int thread_phase);

/*
 * Loads dll_path (once per path) and returns the address of export_name in it, or
 * NULL with a message in err.
 */
EXPORT void *tto_load(const char *dll_path, const char *export_name, char *err, int err_len)
{
    pthread_mutex_lock(&g_load_lock);
    module_t *m = NULL;
    for (int i = 0; i < g_module_count; i++)
        if (strcmp(g_modules[i].path, dll_path) == 0)
            m = &g_modules[i];

    if (!m) {
        if (g_module_count == (int)(sizeof g_modules / sizeof g_modules[0])) {
            set_error(err, err_len, "too many Oodle DLLs loaded (%s)", dll_path);
            pthread_mutex_unlock(&g_load_lock);
            return NULL;
        }
        size_t size = 0;
        uint8_t *file = read_file(dll_path, &size);
        if (!file) {
            set_error(err, err_len, "could not read %s", dll_path);
            pthread_mutex_unlock(&g_load_lock);
            return NULL;
        }
        module_t loaded = {0};
        snprintf(loaded.path, sizeof loaded.path, "%s", dll_path);
        int ok = map_image(&loaded, file, size, err, err_len);
        free(file);
        if (!ok) {
            if (loaded.base)
                munmap(loaded.base, loaded.size);
            pthread_mutex_unlock(&g_load_lock);
            return NULL;
        }
        m = &g_modules[g_module_count++];
        *m = loaded;

        /* The DLL's C runtime start-up (DllMain with DLL_PROCESS_ATTACH). */
        uint32_t entry = RD32(optional_header(m->base) + 16);
        if (entry) {
            unsigned long gs = gs_enter();
            BOOL started = ((dll_main_t)(m->base + entry))(m->base, 1, NULL);
            gs_leave(gs);
            if (!started) {
                set_error(err, err_len, "the start-up code of %s failed", dll_path);
                g_module_count--;
                munmap(m->base, m->size);
                pthread_mutex_unlock(&g_load_lock);
                return NULL;
            }
        }
    }

    void *fn = export_of(m, export_name);
    if (!fn)
        set_error(err, err_len, "the DLL has no %s", export_name);
    pthread_mutex_unlock(&g_load_lock);
    return fn;
}

/* Calls an OodleLZ_Decompress returned by tto_load. */
EXPORT intptr_t tto_decompress(void *fn, const void *comp, intptr_t comp_len, void *raw, intptr_t raw_len,
                               int fuzz_safe, int check_crc, int verbosity, void *dec_buf_base, intptr_t dec_buf_size,
                               void *callback, void *callback_data, void *decoder_memory,
                               intptr_t decoder_memory_size, int thread_phase)
{
    unsigned long gs = gs_enter();
    intptr_t n = ((decompress_t)fn)(comp, comp_len, raw, raw_len, fuzz_safe, check_crc, verbosity, dec_buf_base,
                                    dec_buf_size, callback, callback_data, decoder_memory, decoder_memory_size,
                                    thread_phase);
    gs_leave(gs);
    return n;
}
