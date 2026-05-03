/**
 * bench2_objstore.c — Mixed workload benchmark for ObjectStore via NativeAOT DLL.
 *
 * Workload:
 *   Phase 1 (Seed):  Create 100,000 objects with deterministic random sizes (256B–16KB).
 *   Phase 2 (Read):  50,000 random reads with content verification.
 *   Phase 3 (Mixed): 50,000 random reads interleaved with a new write every 100 reads.
 *
 * Uses a deterministic PRNG (xorshift32) so that both ObjectStore and SQLite
 * benchmarks exercise exactly the same data and access patterns.
 *
 * Compile: cl /O2 bench2_objstore.c /Fe:bench2_objstore.exe
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>

/* ---- Deterministic PRNG (xorshift32) ---- */
static uint32_t rng_state;

static void rng_seed(uint32_t seed) { rng_state = seed; }

static uint32_t rng_next(void) {
    uint32_t x = rng_state;
    x ^= x << 13;
    x ^= x >> 17;
    x ^= x << 5;
    rng_state = x;
    return x;
}

/* Returns a value in [lo, hi] inclusive */
static uint32_t rng_range(uint32_t lo, uint32_t hi) {
    return lo + (rng_next() % (hi - lo + 1));
}

/* ---- Deterministic content generation / verification ---- */
static void fill_content(uint8_t* buf, int32_t size, uint32_t obj_index) {
    /* Generate content that is unique per object and verifiable */
    uint32_t s = obj_index ^ 0xA5A5A5A5u;
    for (int32_t i = 0; i < size; i++) {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        buf[i] = (uint8_t)(s & 0xFF);
    }
}

static int verify_content(const uint8_t* buf, int32_t size, uint32_t obj_index) {
    uint32_t s = obj_index ^ 0xA5A5A5A5u;
    for (int32_t i = 0; i < size; i++) {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        if (buf[i] != (uint8_t)(s & 0xFF)) return 0;
    }
    return 1;
}

/* ---- Function pointer types matching objstore.h ---- */
typedef void* objstore_t;
typedef void* objstore_options_t;

typedef int (*fn_options_create)(objstore_options_t*);
typedef int (*fn_options_free)(objstore_options_t);
typedef int (*fn_options_set_multi_process)(objstore_options_t, int);
typedef int (*fn_open_or_create)(const char*, objstore_options_t, objstore_t*);
typedef int (*fn_close)(objstore_t);
typedef int (*fn_object_create)(objstore_t, const char*, uint64_t*);
typedef int (*fn_append)(objstore_t, uint64_t, const uint8_t*, int32_t);
typedef int (*fn_read)(objstore_t, uint64_t, int64_t, uint8_t*, int32_t, int32_t*);
typedef int (*fn_txn_begin)(objstore_t);
typedef int (*fn_txn_commit)(objstore_t);
typedef int (*fn_object_get_size)(objstore_t, uint64_t, int64_t*);
typedef int (*fn_refresh)(objstore_t);

/* ---- Globals ---- */
static fn_options_create    p_options_create;
static fn_options_free      p_options_free;
static fn_options_set_multi_process p_options_set_multi_process;
static fn_open_or_create    p_open_or_create;
static fn_close             p_close;
static fn_object_create     p_object_create;
static fn_append            p_append;
static fn_read              p_read;
static fn_txn_begin         p_txn_begin;
static fn_txn_commit        p_txn_commit;
static fn_object_get_size   p_object_get_size;
static fn_refresh           p_refresh;

static LARGE_INTEGER qpc_freq;

static double elapsed_ms(LARGE_INTEGER start, LARGE_INTEGER end) {
    return (double)(end.QuadPart - start.QuadPart) * 1000.0 / (double)qpc_freq.QuadPart;
}

static int load_dll(const char* dll_path) {
    HMODULE h = LoadLibraryA(dll_path);
    if (!h) {
        fprintf(stderr, "ERROR: LoadLibrary failed for %s (err=%lu)\n", dll_path, GetLastError());
        return 0;
    }
    #define LOAD(name) do { \
        p_##name = (fn_##name)GetProcAddress(h, "objstore_" #name); \
        if (!p_##name) { fprintf(stderr, "ERROR: missing symbol objstore_%s\n", #name); return 0; } \
    } while(0)

    LOAD(options_create);
    LOAD(options_free);
    LOAD(options_set_multi_process);
    LOAD(open_or_create);
    LOAD(close);
    LOAD(object_create);
    LOAD(append);
    LOAD(read);
    LOAD(txn_begin);
    LOAD(txn_commit);
    LOAD(object_get_size);
    LOAD(refresh);
    #undef LOAD
    return 1;
}

/* ---- Constants ---- */
#define SEED_COUNT      100000
#define READ_COUNT      50000
#define MIXED_READS     50000
#define WRITE_INTERVAL  100      /* insert a new object every N reads in mixed phase */
#define MIN_SIZE        256
#define MAX_SIZE        16384
#define RNG_SEED        12345u
#define BATCH_SIZE      1000     /* transaction batch size for seeding */

/* ---- Main ---- */
int main(int argc, char* argv[]) {
    const char* dll_path = NULL;
    const char* db_dir = "C:\\temp";

    if (argc > 1) dll_path = argv[1];
    if (argc > 2) db_dir = argv[2];

    if (!dll_path) {
        fprintf(stderr, "Usage: bench2_objstore.exe <path_to_ObjectStore.Native.dll> [db_directory]\n");
        return 1;
    }

    QueryPerformanceFrequency(&qpc_freq);
    if (!load_dll(dll_path)) return 1;

    printf("=== ObjectStore Mixed Workload Benchmark ===\n");
    printf("DLL: %s\n", dll_path);
    printf("Directory: %s\n", db_dir);
    printf("Seed: %u objects (%d B – %d B), Reads: %d, Mixed: %d reads + writes every %d\n\n",
           SEED_COUNT, MIN_SIZE, MAX_SIZE, READ_COUNT, MIXED_READS, WRITE_INTERVAL);

    /* Open store */
    char db_path[MAX_PATH];
    sprintf(db_path, "%s\\bench2_objstore.db", db_dir);
    DeleteFileA(db_path);

    objstore_options_t opts;
    objstore_t store;
    p_options_create(&opts);
    p_options_set_multi_process(opts, 1);
    int rc = p_open_or_create(db_path, opts, &store);
    if (rc != 0) { fprintf(stderr, "open failed: %d\n", rc); return 1; }
    p_options_free(opts);

    /* Allocate tracking arrays */
    uint64_t* ids = (uint64_t*)malloc(sizeof(uint64_t) * (SEED_COUNT + MIXED_READS / WRITE_INTERVAL + 1));
    int32_t* sizes = (int32_t*)malloc(sizeof(int32_t) * (SEED_COUNT + MIXED_READS / WRITE_INTERVAL + 1));
    uint8_t* buf = (uint8_t*)malloc(MAX_SIZE);
    if (!ids || !sizes || !buf) { fprintf(stderr, "malloc failed\n"); return 1; }

    int total_objects = 0;
    LARGE_INTEGER t0, t1;

    /* ================================================================
     * Phase 1: SEED — create 100,000 objects in batched transactions
     * ================================================================ */
    printf("Phase 1: Seeding %d objects...\n", SEED_COUNT);
    rng_seed(RNG_SEED);

    QueryPerformanceCounter(&t0);
    for (int batch_start = 0; batch_start < SEED_COUNT; batch_start += BATCH_SIZE) {
        int batch_end = batch_start + BATCH_SIZE;
        if (batch_end > SEED_COUNT) batch_end = SEED_COUNT;

        rc = p_txn_begin(store);
        if (rc != 0) { fprintf(stderr, "txn_begin failed at %d: %d\n", batch_start, rc); break; }

        for (int i = batch_start; i < batch_end; i++) {
            int32_t obj_size = (int32_t)rng_range(MIN_SIZE, MAX_SIZE);
            sizes[i] = obj_size;

            fill_content(buf, obj_size, (uint32_t)i);

            char name[64];
            sprintf(name, "obj_%d", i);
            uint64_t id;
            rc = p_object_create(store, name, &id);
            if (rc != 0) { fprintf(stderr, "create failed at i=%d: %d\n", i, rc); goto seed_done; }
            rc = p_append(store, id, buf, obj_size);
            if (rc != 0) { fprintf(stderr, "append failed at i=%d: %d\n", i, rc); goto seed_done; }
            ids[i] = id;
            total_objects++;
        }

        rc = p_txn_commit(store);
        if (rc != 0) { fprintf(stderr, "txn_commit failed: %d\n", rc); break; }
    }
seed_done:
    QueryPerformanceCounter(&t1);

    double seed_ms = elapsed_ms(t0, t1);
    printf("  Seeded %d objects in %.1f ms (%.1f ops/s)\n\n",
           total_objects, seed_ms, (double)total_objects / (seed_ms / 1000.0));

    /* ================================================================
     * Phase 2: RANDOM READ with content verification
     * ================================================================ */
    printf("Phase 2: Random read (%d reads) with verification...\n", READ_COUNT);
    rng_seed(99999u);  /* different seed for read pattern */

    int read_ok = 0, read_fail = 0, verify_fail = 0;
    uint8_t* read_buf = (uint8_t*)malloc(MAX_SIZE);
    if (!read_buf) { fprintf(stderr, "malloc failed\n"); return 1; }

    QueryPerformanceCounter(&t0);
    for (int i = 0; i < READ_COUNT; i++) {
        uint32_t idx = rng_range(0, total_objects - 1);
        int32_t expected_size = sizes[idx];
        int32_t bytes_read = 0;

        rc = p_read(store, ids[idx], 0, read_buf, expected_size, &bytes_read);
        if (rc != 0 || bytes_read != expected_size) {
            read_fail++;
            continue;
        }
        if (!verify_content(read_buf, expected_size, idx)) {
            verify_fail++;
        } else {
            read_ok++;
        }
    }
    QueryPerformanceCounter(&t1);

    double read_ms = elapsed_ms(t0, t1);
    printf("  %d reads in %.1f ms (%.1f ops/s)\n", READ_COUNT, read_ms,
           (double)READ_COUNT / (read_ms / 1000.0));
    printf("  Verified OK: %d | Read errors: %d | Verify mismatch: %d\n\n",
           read_ok, read_fail, verify_fail);

    /* ================================================================
     * Phase 3: MIXED — random reads with occasional writes
     * ================================================================ */
    printf("Phase 3: Mixed read+write (%d reads, write every %d)...\n", MIXED_READS, WRITE_INTERVAL);
    rng_seed(77777u);  /* third seed for mixed pattern */

    int mixed_reads_ok = 0, mixed_read_fail = 0, mixed_verify_fail = 0;
    int mixed_writes = 0;

    QueryPerformanceCounter(&t0);
    for (int i = 0; i < MIXED_READS; i++) {
        /* Every WRITE_INTERVAL reads, insert a new object */
        if (i > 0 && (i % WRITE_INTERVAL) == 0) {
            int new_idx = total_objects;
            int32_t obj_size = (int32_t)rng_range(MIN_SIZE, MAX_SIZE);
            sizes[new_idx] = obj_size;
            fill_content(buf, obj_size, (uint32_t)new_idx);

            char name[64];
            sprintf(name, "mixed_%d", new_idx);
            uint64_t id;
            rc = p_object_create(store, name, &id);
            if (rc == 0) {
                rc = p_append(store, id, buf, obj_size);
                if (rc == 0) {
                    ids[new_idx] = id;
                    total_objects++;
                    mixed_writes++;
                }
            }
        }

        /* Random read from all objects written so far */
        uint32_t idx = rng_range(0, total_objects - 1);
        int32_t expected_size = sizes[idx];
        int32_t bytes_read = 0;

        rc = p_read(store, ids[idx], 0, read_buf, expected_size, &bytes_read);
        if (rc != 0 || bytes_read != expected_size) {
            mixed_read_fail++;
            continue;
        }
        if (!verify_content(read_buf, expected_size, idx)) {
            mixed_verify_fail++;
        } else {
            mixed_reads_ok++;
        }
    }
    QueryPerformanceCounter(&t1);

    double mixed_ms = elapsed_ms(t0, t1);
    printf("  %d reads + %d writes in %.1f ms\n", MIXED_READS, mixed_writes, mixed_ms);
    printf("  Read throughput: %.1f ops/s\n", (double)MIXED_READS / (mixed_ms / 1000.0));
    printf("  Verified OK: %d | Read errors: %d | Verify mismatch: %d\n\n",
           mixed_reads_ok, mixed_read_fail, mixed_verify_fail);

    /* ================================================================
     * Store size measurement (before cleanup)
     * ================================================================ */
    p_close(store);

    int64_t store_size = 0;
    {
        WIN32_FILE_ATTRIBUTE_DATA fdata;
        if (GetFileAttributesExA(db_path, GetFileExInfoStandard, &fdata)) {
            store_size = ((int64_t)fdata.nFileSizeHigh << 32) | fdata.nFileSizeLow;
        }
    }

    /* ================================================================
     * Summary Report
     * ================================================================ */
    printf("--- SUMMARY (ObjectStore) -----------------------------------------\n");
    printf("  %-30s %10.1f ms  %10.1f ops/s\n", "Seed (100K objects)", seed_ms,
           (double)SEED_COUNT / (seed_ms / 1000.0));
    printf("  %-30s %10.1f ms  %10.1f ops/s\n", "Random Read (50K)", read_ms,
           (double)READ_COUNT / (read_ms / 1000.0));
    printf("  %-30s %10.1f ms  %10.1f ops/s\n", "Mixed R+W (50K+writes)", mixed_ms,
           (double)(MIXED_READS + mixed_writes) / (mixed_ms / 1000.0));
    printf("  Store size: %.2f MB (%lld bytes)\n",
           (double)store_size / (1024.0 * 1024.0), (long long)store_size);
    printf("  Integrity: %s\n",
           (read_fail == 0 && verify_fail == 0 && mixed_read_fail == 0 && mixed_verify_fail == 0)
           ? "ALL PASSED" : "FAILURES DETECTED");
    printf("-------------------------------------------------------------------\n");

    /* Cleanup */
    DeleteFileA(db_path);
    free(ids);
    free(sizes);
    free(buf);
    free(read_buf);

    return 0;
}
