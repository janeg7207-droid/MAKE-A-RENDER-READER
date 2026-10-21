#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>
#include <atomic>
#include "selection_policy.h"
#include "vendor/minhook/include/MinHook.h"

using namespace selection_loop;
namespace {
constexpr wchar_t kConfig[] = L"WWE2K26_SelectionLoop.cfg";
constexpr wchar_t kLog[] = L"WWE2K26_SelectionLoop.log";
constexpr wchar_t kTargetName[] = L"WWE2K26_MovingRenders.dlp";
constexpr char kTargetHash[] = "b752da3c9a9dc48dc377972a7183db0194c2b3b551074f7d0f0c9766432bf75c";
constexpr uintptr_t kSite = 0x1a42, kResume = 0x1a5b;
constexpr uintptr_t kMarker = 0xe0e0, kReadyFlag = 0x33c460, kBinkModule = 0x13058, kBinkTrampoline = 0x137d0;
constexpr uintptr_t kNodeBase = 0x1b73e0, kNodeCount = 0x1b73d4, kSegmentBase = 0x143f0, kSegmentCount = 0x143e8;
constexpr size_t kNodeStride = 0x580, kMaximumSegments = 1024;
constexpr unsigned char kSiteBytes[25] = {
    0x42,0x8b,0x5c,0x02,0x04,0x46,0x8b,0x74,0x02,0x08,0x42,0x83,0x7c,
    0x02,0x0c,0x00,0x41,0x0f,0x95,0xc4,0xba,0x01,0x00,0x00,0x00 };
constexpr char kMarkerBytes[] = "MOVINGRENDERS-4.21";
constexpr unsigned char kTrampolinePrefix[11] = {0x48,0x89,0x5c,0x24,0x10,0xff,0x25,0,0,0,0};

struct Segment { uint32_t id, start, end, freeze; };
struct Snapshot {
    int32_t node_count;
    int32_t segment_count;
    unsigned char nodes[kMaximumPolicies][kNodeStride];
    Segment segments[kMaximumSegments];
};
static_assert(sizeof(Segment) == 16, "Native segment ABI");
Snapshot initial{}, current{}; // Never place the large snapshots on a worker's stack.
Override policies[kMaximumPolicies]{};
size_t policy_count = 0;
std::atomic<bool> active{false};
static_assert(std::atomic<bool>::is_always_lock_free, "Callback requires lock-free publication");
HMODULE self_module = nullptr;
wchar_t plugin_directory[MAX_PATH] = {};
wchar_t render_directory[MAX_PATH] = {};
wchar_t log_path[MAX_PATH] = {};

// Avoid Zig 0.13's private UCRT API-set imports for these three scan routines.
const wchar_t* FindWide(const wchar_t* text,wchar_t value) {
    for (;*text;++text) if (*text == value) return text;
    return nullptr;
}
wchar_t* LastWide(wchar_t* text,wchar_t value) {
    wchar_t* found = nullptr;
    for (;*text;++text) if (*text == value) found = text;
    return found;
}
bool ContainsNul(const char* text,size_t length) {
    for (size_t i=0;i<length;++i) if (text[i] == 0) return true;
    return false;
}

bool Join(wchar_t* output, const wchar_t* directory, const wchar_t* filename) {
    size_t a = wcslen(directory), b = wcslen(filename);
    if (a + b + 2 > MAX_PATH) return false;
    memcpy(output, directory, a * sizeof(wchar_t)); output[a++] = L'\\';
    memcpy(output + a, filename, (b + 1) * sizeof(wchar_t)); return true;
}

void Log(const char* message, DWORD detail = 0) {
    if (!*log_path) return;
    HANDLE file = CreateFileW(log_path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER length{};
    if (GetFileSizeEx(file, &length) && length.QuadPart <= 1024 * 1024) {
        char text[1024]; SYSTEMTIME now{}; GetSystemTime(&now);
        int count = wsprintfA(text, "%04u-%02u-%02u %02u:%02u:%02u UTC %s (%lu)\r\n",
            now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, message, detail);
        DWORD written; if (count > 0) WriteFile(file, text, static_cast<DWORD>(count), &written, nullptr);
    }
    CloseHandle(file);
}

bool ReadMemory(uintptr_t address, void* output, size_t bytes) {
    SIZE_T got = 0;
    return address && bytes <= SIZE_MAX - address
        && ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<const void*>(address), output, bytes, &got) && got == bytes;
}
template<typename T> bool ReadValue(uintptr_t address, T* output) { return ReadMemory(address, output, sizeof(T)); }
uint32_t U32(const unsigned char* bytes, size_t offset) { uint32_t value; memcpy(&value, bytes + offset, 4); return value; }
uint64_t U64(const unsigned char* bytes, size_t offset) { uint64_t value; memcpy(&value, bytes + offset, 8); return value; }

bool Executable(uintptr_t pointer, size_t bytes) {
    MEMORY_BASIC_INFORMATION region{};
    if (!pointer || !VirtualQuery(reinterpret_cast<const void*>(pointer), &region, sizeof(region))
        || region.State != MEM_COMMIT || (region.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    DWORD access = region.Protect & 0xff;
    uintptr_t base = reinterpret_cast<uintptr_t>(region.BaseAddress);
    return (access == PAGE_EXECUTE || access == PAGE_EXECUTE_READ || access == PAGE_EXECUTE_READWRITE || access == PAGE_EXECUTE_WRITECOPY)
        && pointer >= base && bytes <= region.RegionSize && pointer - base <= region.RegionSize - bytes;
}

bool ReadAt(HANDLE file, uint64_t position, void* output, DWORD size) {
    LARGE_INTEGER offset{}; offset.QuadPart = static_cast<LONGLONG>(position);
    DWORD read = 0;
    return position <= INT64_MAX && SetFilePointerEx(file, offset, nullptr, FILE_BEGIN)
        && ReadFile(file, output, size, &read, nullptr) && read == size;
}

bool HashFile(HANDLE file, uint64_t expected_length, char output[65]) {
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    unsigned char object[1024], digest[32], buffer[65536]; DWORD object_size = 0, returned = 0;
    bool success = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0
        && BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size), 4, &returned, 0) >= 0
        && object_size <= sizeof(object)
        && BCryptCreateHash(algorithm, &hash, object, object_size, nullptr, 0, 0) >= 0;
    uint64_t offset = 0;
    while (success && offset < expected_length) {
        DWORD wanted = static_cast<DWORD>((expected_length - offset) < sizeof(buffer) ? expected_length - offset : sizeof(buffer));
        success = ReadAt(file, offset, buffer, wanted) && BCryptHashData(hash, buffer, wanted, 0) >= 0;
        offset += wanted;
    }
    LARGE_INTEGER length{};
    success = success && GetFileSizeEx(file, &length) && length.QuadPart >= 0
        && static_cast<uint64_t>(length.QuadPart) == expected_length && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    if (!success) return false;
    constexpr char hex[] = "0123456789abcdef";
    for (size_t i = 0; i < 32; ++i) { output[2*i] = hex[digest[i] >> 4]; output[2*i+1] = hex[digest[i] & 15]; }
    output[64] = 0; return true;
}

bool NativeImage(uintptr_t base) {
    IMAGE_DOS_HEADER dos{}; IMAGE_NT_HEADERS64 pe{}; char marker[sizeof(kMarkerBytes)]{};
    return ReadValue(base, &dos) && dos.e_magic == IMAGE_DOS_SIGNATURE && dos.e_lfanew > 0 && dos.e_lfanew < 0x1000
        && ReadValue(base + static_cast<uint32_t>(dos.e_lfanew), &pe) && pe.Signature == IMAGE_NT_SIGNATURE
        && pe.FileHeader.Machine == IMAGE_FILE_MACHINE_AMD64 && pe.OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC
        && pe.OptionalHeader.SizeOfImage == 0xf4f000 && pe.OptionalHeader.SizeOfHeaders >= sizeof(dos)
        && ReadMemory(base + kMarker, marker, sizeof(marker)) && !memcmp(marker, kMarkerBytes, sizeof(marker));
}

bool OriginalFile(HMODULE module) {
    wchar_t filename[MAX_PATH] = {}; DWORD got = GetModuleFileNameW(module, filename, MAX_PATH);
    if (!got || got >= MAX_PATH) return false;
    HANDLE file = CreateFileW(filename, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER length{}; char digest[65]{};
    bool valid = GetFileSizeEx(file, &length) && length.QuadPart == 69632 && HashFile(file, 69632, digest) && !strcmp(digest, kTargetHash);
    CloseHandle(file); return valid;
}

bool AuthorizedOriginalReady(uintptr_t base, bool check_site) {
    LONG flag = 0; uintptr_t recorded_bink = 0, trampoline = 0;
    HMODULE loaded_bink = GetModuleHandleW(L"bink2w64.dll");
    if (!NativeImage(base) || !ReadValue(base + kReadyFlag, &flag) || flag != 1 || !loaded_bink
        || !ReadValue(base + kBinkModule, &recorded_bink) || recorded_bink != reinterpret_cast<uintptr_t>(loaded_bink)
        || !ReadValue(base + kBinkTrampoline, &trampoline) || !Executable(trampoline, 19)) return false;
    uintptr_t bink_next = reinterpret_cast<uintptr_t>(GetProcAddress(loaded_bink, "BinkNextFrame"));
    unsigned char patch[14]{}, copied[19]{};
    if (!bink_next || !Executable(bink_next, 14) || !ReadMemory(bink_next, patch, 14)
        || memcmp(patch, "\xff\x25\0\0\0\0", 6) || U64(patch, 6) != base + 0x1000
        || !ReadMemory(trampoline, copied, 19) || memcmp(copied, kTrampolinePrefix, 11) || U64(copied, 11) != bink_next + 14) return false;
    if (check_site) {
        unsigned char site[25]{};
        if (!Executable(base + kSite, 25) || !ReadMemory(base + kSite, site, sizeof(site)) || memcmp(site, kSiteBytes, sizeof(site))) return false;
    }
    return true;
}

bool OurInstalledDetour(uintptr_t base,const unsigned char site[25],void* entry) {
    if (site[0] != 0xe9 || memcmp(site+5,kSiteBytes+5,20)) return false;
    int32_t displacement; memcpy(&displacement,site+1,4);
    int64_t address = static_cast<int64_t>(base+kSite+5) + displacement;
    if (address <= 0) return false;
    uintptr_t relay = static_cast<uintptr_t>(address); unsigned char jump[14]{};
    return Executable(relay,sizeof(jump)) && ReadMemory(relay,jump,sizeof(jump))
        && !memcmp(jump,"\xff\x25\0\0\0\0",6) && U64(jump,6) == reinterpret_cast<uintptr_t>(entry);
}

bool TakeSnapshot(uintptr_t base, Snapshot* snapshot) {
    if (!ReadValue(base + kNodeCount, &snapshot->node_count) || snapshot->node_count < 0 || snapshot->node_count > 256
        || !ReadValue(base + kSegmentCount, &snapshot->segment_count) || snapshot->segment_count < 0 || snapshot->segment_count > 1024) return false;
    return ReadMemory(base + kNodeBase, snapshot->nodes, static_cast<size_t>(snapshot->node_count) * kNodeStride)
        && ReadMemory(base + kSegmentBase, snapshot->segments, static_cast<size_t>(snapshot->segment_count) * sizeof(Segment));
}

bool SameSnapshot(const Snapshot& left, const Snapshot& right) {
    if (left.node_count != right.node_count || left.segment_count != right.segment_count
        || memcmp(left.segments, right.segments, static_cast<size_t>(left.segment_count) * sizeof(Segment))) return false;
    for (int i = 0; i < left.node_count; ++i) {
        // IDs/count, exact source path, frame/payload sizes, ending, file size/mtime.
        if (memcmp(left.nodes[i], right.nodes[i], 0x14) || memcmp(left.nodes[i] + 0x324, right.nodes[i] + 0x324, 0x210)
            || memcmp(left.nodes[i] + 0x56c, right.nodes[i] + 0x56c, 0x14)) return false;
    }
    return true;
}

bool EnabledConfig() {
    wchar_t path[MAX_PATH]; if (!Join(path, plugin_directory, kConfig)) return false;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER length{}; char bytes[4097]{};
    bool valid = GetFileSizeEx(file, &length) && length.QuadPart > 0 && length.QuadPart <= 4096
        && ReadAt(file, 0, bytes, static_cast<DWORD>(length.QuadPart)) && !ContainsNul(bytes,static_cast<size_t>(length.QuadPart)); CloseHandle(file);
    if (!valid) return false;
    bool seen = false, enabled = false; char* position = bytes;
    while (*position) {
        char* line = position; while (*position && *position != '\n' && *position != '\r') ++position;
        if (*position) *position++ = 0;
        while (*line == ' ' || *line == '\t') ++line;
        if (!*line || *line == '#') continue;
        size_t size = strlen(line); while (size && (line[size-1] == ' ' || line[size-1] == '\t')) line[--size] = 0;
        if (seen || (strcmp(line, "enabled 0") && strcmp(line, "enabled 1"))) return false;
        seen = true; enabled = !strcmp(line, "enabled 1");
    }
    return seen && enabled;
}

bool NativePath(const unsigned char* node, wchar_t path[MAX_PATH]) {
    wchar_t source[260]; memcpy(source, node + 0x324, sizeof(source));
    size_t size = 0; while (size < 260 && source[size]) ++size;
    if (size == 0 || size >= 260) return false;
    DWORD got = GetFullPathNameW(source, MAX_PATH, path, nullptr);
    if (!got || got >= MAX_PATH) return false;
    size_t root = wcslen(render_directory);
    if (_wcsnicmp(path, render_directory, root) || path[root] != L'\\') return false;
    const wchar_t* filename = path + root + 1; size_t length = wcslen(filename);
    return length > 4 && !_wcsicmp(filename + length - 4, L".gmr") && !FindWide(filename, L'\\') && !FindWide(filename, L'/')
        && !FindWide(filename, L':');
}

bool RegularSource(const wchar_t* path) {
    DWORD attributes = GetFileAttributesW(path);
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT));
}

bool ReadPolicyFile(const wchar_t* source_path, Policy* policy) {
    wchar_t path[MAX_PATH]; constexpr wchar_t suffix[] = L".selection-loop.json";
    size_t a = wcslen(source_path), b = wcslen(suffix);
    if (a + b >= MAX_PATH) return false;
    memcpy(path, source_path, a * 2); memcpy(path + a, suffix, (b + 1) * 2);
    if (!RegularSource(path)) return false;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER length{}; char bytes[kMaximumPolicyBytes];
    bool valid = GetFileSizeEx(file, &length) && length.QuadPart > 0 && static_cast<uint64_t>(length.QuadPart) <= kMaximumPolicyBytes
        && ReadAt(file, 0, bytes, static_cast<DWORD>(length.QuadPart))
        && ParsePolicy(bytes, static_cast<size_t>(length.QuadPart), policy);
    CloseHandle(file); return valid;
}

bool GmrMatches(const wchar_t* path, const unsigned char* native, const Policy& policy) {
    if (!RegularSource(path)) return false;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER length{}; FILETIME changed{}; unsigned char header[44]{}, footer[8]{};
    bool valid = GetFileSizeEx(file, &length) && length.QuadPart >= 60 && static_cast<uint64_t>(length.QuadPart) == U64(native, 0x570)
        && GetFileTime(file, nullptr, nullptr, &changed) && !memcmp(&changed, native + 0x578, sizeof(changed))
        && ReadAt(file, 0, header, sizeof(header)) && U32(header,0) == 0x694b4942
        && U32(header,8) == policy.video_frames && U32(header,16) == policy.video_frames
        && U32(header,20) == 1024 && U32(header,24) == 1024 && U32(header,28) == 30 && U32(header,32) == 1
        && U32(header,36) == 1048576 && U32(header,40) == 0
        && ReadAt(file, static_cast<uint64_t>(length.QuadPart) - 8, footer, 8)
        && U32(footer,4) == 0x31524d47 && U32(footer,0) > 0 && U32(footer,0) <= 16 * 1024 * 1024;
    uint64_t video_length = static_cast<uint64_t>(U32(header,4)) + 8;
    valid = valid && video_length + U32(footer,0) + 8 == static_cast<uint64_t>(length.QuadPart)
        && U32(native,0x52c) == policy.video_frames && U32(native,0x530) == video_length;
    uint64_t table_end = 44 + (static_cast<uint64_t>(policy.video_frames) + 1) * 4;
    uint32_t prior = 0, largest = 0;
    valid = valid && table_end < video_length && ReadAt(file,44,&prior,4) && (prior & 1) && (prior & ~1u) == table_end;
    // Bound index reads; source hashing is worker-only and never performed in Bink callbacks.
    uint32_t indices[4096]; uint32_t processed = 0;
    while (valid && processed < policy.video_frames) {
        DWORD count = policy.video_frames - processed; if (count > 4096) count = 4096;
        valid = ReadAt(file, 48ull + 4ull * processed, indices, count * 4);
        for (DWORD i = 0; valid && i < count; ++i) {
            uint32_t value = indices[i], start = prior & ~1u, end = value & ~1u;
            bool final = processed + i + 1 == policy.video_frames;
            valid = end > start && end <= video_length && (final ? (!(value & 1) && end == video_length) : (value & 1));
            if (end - start > largest) largest = end - start;
            prior = value;
        }
        processed += count;
    }
    char digest[65]{};
    valid = valid && largest == U32(header,12) && HashFile(file, static_cast<uint64_t>(length.QuadPart), digest) && !strcmp(digest, policy.sha256);
    CloseHandle(file); return valid;
}

bool BuildPolicies(uintptr_t base, const Snapshot& snapshot) {
    policy_count = 0;
    for (int node_index = 0; node_index < snapshot.node_count; ++node_index) {
        const unsigned char* node = snapshot.nodes[node_index]; uint32_t id = U32(node,0), id_count = U32(node,0x10);
        if (id < 1 || id > 65535 || id_count < 1 || id_count > 4 || U32(node,0x56c) != 1) continue;
        Binding binding{id,0,0,1,U32(node,0x52c),0,0,0};
        bool malformed = false;
        for (int i = 0; i < snapshot.node_count; ++i) {
            uint32_t count = U32(snapshot.nodes[i],0x10);
            if (count > 4 || count == 0) { malformed = true; break; }
            if (U32(snapshot.nodes[i],0) == id) ++binding.primary_claims;
            for (uint32_t slot = 0; slot < count; ++slot) if (U32(snapshot.nodes[i], slot*4) == id) ++binding.all_claims;
        }
        if (malformed || binding.primary_claims != 1 || binding.all_claims != 1) continue;
        size_t matches = 0;
        for (int i = 0; i < snapshot.segment_count; ++i) {
            const Segment& segment = snapshot.segments[i];
            if (segment.id == id) {
                ++matches; binding.start = segment.start; binding.end = segment.end; binding.freeze = segment.freeze;
                binding.segment_address = base + kSegmentBase + static_cast<size_t>(i) * sizeof(Segment);
            }
        }
        if (matches != 1) continue;
        wchar_t path[MAX_PATH]{}; Policy policy{}; Override row{};
        if (!NativePath(node,path) || !ReadPolicyFile(path,&policy) || !BindPolicy(policy,binding,&row) || !GmrMatches(path,node,policy)) continue;
        if (policy_count == kMaximumPolicies) return false;
        size_t place = policy_count;
        while (place && policies[place-1].wrestler_id > row.wrestler_id) { policies[place] = policies[place-1]; --place; }
        if ((place && policies[place-1].wrestler_id == row.wrestler_id) || (place < policy_count && policies[place].wrestler_id == row.wrestler_id)) return false;
        policies[place] = row; ++policy_count;
    }
    return policy_count != 0;
}

DWORD WINAPI Worker(void*) {
    wchar_t filename[MAX_PATH]{};
    DWORD got = GetModuleFileNameW(self_module,filename,MAX_PATH);
    if (!got || got >= MAX_PATH) return 0;
    wchar_t* last = LastWide(filename,L'\\'); if (!last) return 0; *last = 0;
    memcpy(plugin_directory,filename,(wcslen(filename)+1)*2);
    if (!Join(log_path,plugin_directory,kLog) || !Join(render_directory,plugin_directory,L"GorillaRenders")) return 0;
    if (!EnabledConfig()) { Log("Disabled: opt-in enabled 1 configuration was not present"); return 0; }
    HMODULE pinned = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&Worker),&pinned)) { Log("Disabled: could not pin companion",GetLastError()); return 0; }
    HMODULE original = nullptr; bool verified = false;
    for (unsigned attempt = 0; attempt < 480; ++attempt) {
        HMODULE found = GetModuleHandleW(kTargetName);
        if (!found) found = GetModuleHandleW(L"WWE2K26_MovingRenders.dll");
        if (found && !verified) {
            if (!NativeImage(reinterpret_cast<uintptr_t>(found)) || !OriginalFile(found)) {
                Log("Disabled: original is not the supported SHA-pinned MovingRenders 4.21"); return 0;
            }
            original = found; verified = true;
        }
        if (verified && found == original && AuthorizedOriginalReady(reinterpret_cast<uintptr_t>(original),true)) break;
        if (attempt == 479) { Log("Disabled: original authorized Bink hook was not ready within 120 seconds"); return 0; }
        Sleep(250);
    }
    uintptr_t base = reinterpret_cast<uintptr_t>(original);
    // This only pins the already-authorized, already-loaded original; no loader
    // initialization or original entry point is called by the companion.
    HMODULE pinned_original = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(base),&pinned_original) || pinned_original != original) {
        Log("Disabled: could not retain already-loaded original",GetLastError()); return 0;
    }
    DWORD directory_attributes = GetFileAttributesW(render_directory);
    if (directory_attributes == INVALID_FILE_ATTRIBUTES || !(directory_attributes & FILE_ATTRIBUTE_DIRECTORY)
        || (directory_attributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
        Log("Disabled: render directory is missing or a reparse point"); return 0;
    }
    HANDLE watcher = FindFirstChangeNotificationW(render_directory,FALSE,FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME
        | FILE_NOTIFY_CHANGE_SIZE | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_ATTRIBUTES | FILE_NOTIFY_CHANGE_SECURITY);
    if (watcher == INVALID_HANDLE_VALUE) { Log("Disabled: source directory watcher unavailable",GetLastError()); return 0; }
    if (!TakeSnapshot(base,&initial) || !BuildPolicies(base,initial) || !TakeSnapshot(base,&current) || !SameSnapshot(initial,current)
        || WaitForSingleObject(watcher,0) != WAIT_TIMEOUT || !AuthorizedOriginalReady(base,true)) {
        Log("Disabled: no stable, unique primary-ID source and policy bindings validated"); FindCloseChangeNotification(watcher); return 0;
    }
    MH_STATUS status = MH_Initialize();
    if (status != MH_OK) { Log("Disabled: MinHook initialization failed",status); FindCloseChangeNotification(watcher); return 0; }
    extern void* GetEntryAddress();
    void* unused_trampoline = nullptr;
    status = MH_CreateHook(reinterpret_cast<void*>(base+kSite),GetEntryAddress(),&unused_trampoline);
    if (status != MH_OK) { Log("Disabled: MinHook creation failed",status); FindCloseChangeNotification(watcher); return 0; }
    // The installed detour initially passes through; publish only after final guards.
    extern void SetResume(uintptr_t address);
    SetResume(base+kResume);
    status = MH_EnableHook(reinterpret_cast<void*>(base+kSite));
    if (status != MH_OK) {
        active.store(false,std::memory_order_release); MH_RemoveHook(reinterpret_cast<void*>(base+kSite));
        Log("Disabled: MinHook safe-thread installation failed",status); FindCloseChangeNotification(watcher); return 0;
    }
    unsigned char installed_site[25]{};
    if (!ReadMemory(base+kSite,installed_site,sizeof(installed_site)) || !OurInstalledDetour(base,installed_site,GetEntryAddress())
        || WaitForSingleObject(watcher,0) != WAIT_TIMEOUT
        || !TakeSnapshot(base,&current) || !SameSnapshot(initial,current) || !AuthorizedOriginalReady(base,false)) {
        active.store(false,std::memory_order_release);
        Log("Disabled: installed detour did not match the expected five-byte boundary"); FindCloseChangeNotification(watcher); return 0;
    }
    active.store(true,std::memory_order_release);
    Log("Active: selection loops only validated clean motion prefixes; match cards unchanged",static_cast<DWORD>(policy_count));
    for (;;) {
        DWORD wait = WaitForSingleObject(watcher,1000);
        unsigned char site[25]{};
        if (wait != WAIT_TIMEOUT || !AuthorizedOriginalReady(base,false) || !TakeSnapshot(base,&current) || !SameSnapshot(initial,current)
            || !ReadMemory(base+kSite,site,sizeof(site)) || memcmp(site,installed_site,sizeof(site)) || !OurInstalledDetour(base,site,GetEntryAddress())) {
            active.store(false,std::memory_order_release);
            Log("Disabled until restart: source directory or original active mapping/readiness changed",wait);
            FindCloseChangeNotification(watcher); return 0;
        }
    }
}
}

extern "C" {
void* g_selection_resume = nullptr;
void SelectionLoopEntry();
uint64_t SelectionLoopOverride(uint32_t id,uint32_t start,uint32_t end,uint32_t freeze,uintptr_t segment_address) {
    if (!active.load(std::memory_order_acquire)) return static_cast<uint64_t>(freeze) << 32 | end;
    return Lookup(policies,policy_count,id,start,end,freeze,segment_address);
}
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        self_module = module; DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr,0,Worker,nullptr,0,nullptr); if (thread) CloseHandle(thread);
    }
    return TRUE;
}
}
namespace {
void* GetEntryAddress() { return reinterpret_cast<void*>(&SelectionLoopEntry); }
void SetResume(uintptr_t address) { g_selection_resume = reinterpret_cast<void*>(address); }
}
