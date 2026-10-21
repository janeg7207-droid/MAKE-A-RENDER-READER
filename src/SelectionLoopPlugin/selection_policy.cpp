#include "selection_policy.h"
#include <string.h>
#include <limits.h>

namespace selection_loop {
namespace {
struct Reader {
    const char* next;
    const char* end;
    void Space() { while (next != end && (*next == ' ' || *next == '\r' || *next == '\n' || *next == '\t')) ++next; }
    bool Take(char value) { Space(); if (next == end || *next != value) return false; ++next; return true; }
    bool String(char* output, size_t capacity) {
        if (!Take('"')) return false;
        size_t used = 0;
        while (next != end && *next != '"') {
            unsigned char value = static_cast<unsigned char>(*next++);
            if (value < 0x20 || value >= 0x7f || value == '\\' || used + 1 >= capacity) return false;
            output[used++] = static_cast<char>(value);
        }
        if (next == end) return false;
        ++next; output[used] = 0; return true;
    }
    bool Number(uint32_t* output) {
        Space();
        if (next == end || *next < '0' || *next > '9') return false;
        bool zero = *next == '0';
        uint32_t value = 0; size_t digits = 0;
        while (next != end && *next >= '0' && *next <= '9') {
            uint32_t digit = static_cast<uint32_t>(*next++ - '0');
            if (++digits > 10 || (zero && digits > 1) || value > (UINT32_MAX - digit) / 10) return false;
            value = value * 10 + digit;
        }
        *output = value; return true;
    }
};
}

bool ParsePolicy(const char* bytes, size_t length, Policy* output) {
    if (!bytes || !output || length == 0 || length > kMaximumPolicyBytes) return false;
    Reader input{bytes, bytes + length}; Policy candidate{}; uint32_t seen = 0;
    uint32_t schema = 0, fps = 0; char ending[16] = {};
    if (!input.Take('{')) return false;
    for (size_t field = 0; field < 7; ++field) {
        char key[32] = {}; if (!input.String(key, sizeof(key)) || !input.Take(':')) return false;
        uint32_t bit;
        if (!strcmp(key, "schema")) { bit = 1; if (!input.Number(&schema)) return false; }
        else if (!strcmp(key, "wrestlerId")) { bit = 2; if (!input.Number(&candidate.wrestler_id)) return false; }
        else if (!strcmp(key, "sha256")) { bit = 4; if (!input.String(candidate.sha256, sizeof(candidate.sha256))) return false; }
        else if (!strcmp(key, "videoFrames")) { bit = 8; if (!input.Number(&candidate.video_frames)) return false; }
        else if (!strcmp(key, "motionFrames")) { bit = 16; if (!input.Number(&candidate.motion_frames)) return false; }
        else if (!strcmp(key, "fps")) { bit = 32; if (!input.Number(&fps)) return false; }
        else if (!strcmp(key, "ending")) { bit = 64; if (!input.String(ending, sizeof(ending))) return false; }
        else return false;
        if (seen & bit) return false;
        seen |= bit;
        if (!input.Take(field == 6 ? '}' : ',')) return false;
    }
    input.Space();
    if (input.next != input.end || seen != 127 || schema != 1 || fps != 30 || strcmp(ending, "freeze")
        || candidate.wrestler_id < 1 || candidate.wrestler_id > 65535
        || candidate.motion_frames < 2 || candidate.motion_frames > kMaximumFrames - 11
        || candidate.video_frames != candidate.motion_frames + 11 || strlen(candidate.sha256) != 64) return false;
    for (size_t i = 0; i < 64; ++i) {
        char c = candidate.sha256[i]; if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
    }
    *output = candidate; return true;
}

bool BindPolicy(const Policy& policy, const Binding& binding, Override* output) {
    if (!output || policy.wrestler_id == 0 || policy.wrestler_id > 65535
        || binding.wrestler_id != policy.wrestler_id || binding.primary_claims != 1 || binding.all_claims != 1
        || binding.freeze != 1 || binding.start < 3 || binding.end <= binding.start || binding.segment_address == 0
        || binding.native_frames != policy.video_frames || binding.end - binding.start != policy.video_frames
        || policy.motion_frames < 2 || policy.motion_frames > kMaximumFrames - 11
        || policy.video_frames != policy.motion_frames + 11 || binding.start > UINT32_MAX - policy.motion_frames) return false;
    *output = Override{binding.wrestler_id, binding.start, binding.end, binding.freeze, binding.start + policy.motion_frames, binding.segment_address};
    return true;
}

uint64_t Lookup(const Override* rows, size_t count, uint32_t wrestler_id,
                uint32_t start, uint32_t end, uint32_t freeze, uintptr_t segment_address) {
    const uint64_t unchanged = static_cast<uint64_t>(freeze) << 32 | end;
    if (!rows || count > kMaximumPolicies) return unchanged;
    size_t first = 0, last = count;
    while (first < last) {
        size_t mid = first + (last - first) / 2;
        if (rows[mid].wrestler_id < wrestler_id) first = mid + 1; else last = mid;
    }
    if (first == count) return unchanged;
    const Override& row = rows[first];
    if (row.wrestler_id != wrestler_id || row.start != start || row.end != end || row.freeze != freeze
        || row.segment_address != segment_address
        || row.loop_end <= start + 1ull || row.loop_end >= end) return unchanged;
    return row.loop_end; // upper32 freeze=0; original match-card path is untouched.
}
}
