#pragma once
#include <stddef.h>
#include <stdint.h>

namespace selection_loop {
constexpr size_t kMaximumPolicies = 256;
constexpr size_t kMaximumPolicyBytes = 16384;
constexpr uint32_t kMaximumFrames = 1000000;

struct Policy {
    uint32_t wrestler_id = 0;
    uint32_t video_frames = 0;
    uint32_t motion_frames = 0;
    char sha256[65] = {};
};

struct Binding {
    uint32_t wrestler_id;
    uint32_t start;
    uint32_t end;
    uint32_t freeze;
    uint32_t native_frames;
    uint32_t primary_claims;
    uint32_t all_claims;
    uintptr_t segment_address;
};

struct Override {
    uint32_t wrestler_id;
    uint32_t start;
    uint32_t end;
    uint32_t freeze;
    uint32_t loop_end;
    uintptr_t segment_address;
};

// Strict, bounded schema 1. Duplicate/unknown/escaped keys and trailing data fail.
bool ParsePolicy(const char* bytes, size_t length, Policy* output);
bool BindPolicy(const Policy& policy, const Binding& binding, Override* output);
// Input must be sorted by unique wrestler ID. No allocation, I/O or locks.
uint64_t Lookup(const Override* rows, size_t count, uint32_t wrestler_id,
                uint32_t start, uint32_t end, uint32_t freeze, uintptr_t segment_address);
}
