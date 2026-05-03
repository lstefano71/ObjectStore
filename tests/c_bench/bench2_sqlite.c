/**
 * bench2_sqlite.c — Mixed workload benchmark for SQLite (amalgamation).
 *
 * Same workload as bench2_objstore.c for direct comparison:
 *   Phase 1 (Seed):  Create 100,000 objects with deterministic random sizes (256B–16KB).
 *   Phase 2 (Read):  50,000 random reads with content verification.
 *   Phase 3 (Mixed): 50,000 random reads interleaved with a new write every 100 reads.
 *
 * Uses the same deterministic PRNG (xorshift32) and seeds to produce
 * identical data and access patterns as the ObjectStore benchmark.
 *
 * Compile: cl /O2 /DSQLITE_THREADSAFE=0 /DSQLITE_OMIT_LOAD_EXTENSION bench2_sqlite.c sqlite3.c /Fe:bench2_sqlite.exe
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include "sqlite3.h"

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

/* ---- Timer ---- */
static LARGE_INTEGER qpc_freq;

static double elapsed_ms(LARGE_INTEGER start, LARGE_INTEGER end) {
    return (double)(end.QuadPart - start.QuadPart) * 1000.0 / (double)qpc_freq.QuadPart;
}

/* ---- DB Setup ---- */
static int setup_db(sqlite3* db) {
    char* err = NULL;
    int rc;

    rc = sqlite3_exec(db, "PRAGMA journal_mode=WAL;", NULL, NULL, &err);
    if (rc != SQLITE_OK) { fprintf(stderr, "WAL: %s\n", err); sqlite3_free(err); return rc; }

    rc = sqlite3_exec(db, "PRAGMA synchronous=FULL;", NULL, NULL, &err);
    if (rc != SQLITE_OK) { fprintf(stderr, "sync: %s\n", err); sqlite3_free(err); return rc; }

    rc = sqlite3_exec(db,
        "CREATE TABLE IF NOT EXISTS objects ("
        "  id INTEGER PRIMARY KEY,"
        "  name TEXT UNIQUE NOT NULL,"
        "  data BLOB NOT NULL"
        ");", NULL, NULL, &err);
    if (rc != SQLITE_OK) { fprintf(stderr, "create table: %s\n", err); sqlite3_free(err); return rc; }

    return SQLITE_OK;
}

/* ---- Constants (must match bench2_objstore.c) ---- */
#define SEED_COUNT      100000
#define READ_COUNT      50000
#define MIXED_READS     50000
#define WRITE_INTERVAL  100
#define MIN_SIZE        256
#define MAX_SIZE        16384
#define RNG_SEED        12345u
#define BATCH_SIZE      1000

/* ---- Main ---- */
int main(int argc, char* argv[]) {
    const char* db_dir = "C:\\temp";
    if (argc > 1) db_dir = argv[1];

    QueryPerformanceFrequency(&qpc_freq);

    printf("=== SQLite Mixed Workload Benchmark ===\n");
    printf("SQLite version: %s\n", sqlite3_libversion());
    printf("Directory: %s\n", db_dir);
    printf("Seed: %u objects (%d B – %d B), Reads: %d, Mixed: %d reads + writes every %d\n\n",
           SEED_COUNT, MIN_SIZE, MAX_SIZE, READ_COUNT, MIXED_READS, WRITE_INTERVAL);

    char db_path[MAX_PATH];
    sprintf(db_path, "%s\\bench2_sqlite.db", db_dir);
    DeleteFileA(db_path);
    /* Also remove WAL/SHM from previous runs */
    {
        char wal_path[MAX_PATH], shm_path[MAX_PATH];
        sprintf(wal_path, "%s-wal", db_path);
        sprintf(shm_path, "%s-shm", db_path);
        DeleteFileA(wal_path);
        DeleteFileA(shm_path);
    }

    sqlite3* db;
    int rc = sqlite3_open(db_path, &db);
    if (rc != SQLITE_OK) { fprintf(stderr, "sqlite3_open failed: %s\n", sqlite3_errmsg(db)); return 1; }
    if (setup_db(db) != SQLITE_OK) return 1;

    /* Allocate tracking arrays — we track sizes per object index for verification.
     * SQLite ids are assigned sequentially starting at 0 in our scheme. */
    int32_t* sizes = (int32_t*)malloc(sizeof(int32_t) * (SEED_COUNT + MIXED_READS / WRITE_INTERVAL + 1));
    uint8_t* buf = (uint8_t*)malloc(MAX_SIZE);
    if (!sizes || !buf) { fprintf(stderr, "malloc failed\n"); return 1; }

    int total_objects = 0;
    LARGE_INTEGER t0, t1;

    /* ================================================================
     * Phase 1: SEED — create 100,000 objects in batched transactions
     * ================================================================ */
    printf("Phase 1: Seeding %d objects...\n", SEED_COUNT);
    rng_seed(RNG_SEED);

    sqlite3_stmt* stmt_insert;
    sqlite3_prepare_v2(db, "INSERT INTO objects(id, name, data) VALUES(?, ?, ?);", -1, &stmt_insert, NULL);

    QueryPerformanceCounter(&t0);
    for (int batch_start = 0; batch_start < SEED_COUNT; batch_start += BATCH_SIZE) {
        int batch_end = batch_start + BATCH_SIZE;
        if (batch_end > SEED_COUNT) batch_end = SEED_COUNT;

        sqlite3_exec(db, "BEGIN TRANSACTION;", NULL, NULL, NULL);

        for (int i = batch_start; i < batch_end; i++) {
            int32_t obj_size = (int32_t)rng_range(MIN_SIZE, MAX_SIZE);
            sizes[i] = obj_size;

            fill_content(buf, obj_size, (uint32_t)i);

            char name[64];
            sprintf(name, "obj_%d", i);
            sqlite3_bind_int64(stmt_insert, 1, (sqlite3_int64)i);
            sqlite3_bind_text(stmt_insert, 2, name, -1, SQLITE_TRANSIENT);
            sqlite3_bind_blob(stmt_insert, 3, buf, obj_size, SQLITE_TRANSIENT);
            rc = sqlite3_step(stmt_insert);
            if (rc != SQLITE_DONE) {
                fprintf(stderr, "insert failed at i=%d: %s\n", i, sqlite3_errmsg(db));
                goto seed_done;
            }
            sqlite3_reset(stmt_insert);
            total_objects++;
        }

        sqlite3_exec(db, "COMMIT;", NULL, NULL, NULL);
    }
seed_done:
    QueryPerformanceCounter(&t1);
    sqlite3_finalize(stmt_insert);

    double seed_ms = elapsed_ms(t0, t1);
    printf("  Seeded %d objects in %.1f ms (%.1f ops/s)\n\n",
           total_objects, seed_ms, (double)total_objects / (seed_ms / 1000.0));

    /* ================================================================
     * Phase 2: RANDOM READ with content verification
     * ================================================================ */
    printf("Phase 2: Random read (%d reads) with verification...\n", READ_COUNT);
    rng_seed(99999u);

    int read_ok = 0, read_fail = 0, verify_fail = 0;
    uint8_t* read_buf = (uint8_t*)malloc(MAX_SIZE);
    if (!read_buf) { fprintf(stderr, "malloc failed\n"); return 1; }

    sqlite3_stmt* stmt_read;
    sqlite3_prepare_v2(db, "SELECT data FROM objects WHERE id = ?;", -1, &stmt_read, NULL);

    QueryPerformanceCounter(&t0);
    for (int i = 0; i < READ_COUNT; i++) {
        uint32_t idx = rng_range(0, total_objects - 1);
        int32_t expected_size = sizes[idx];

        sqlite3_bind_int64(stmt_read, 1, (sqlite3_int64)idx);
        rc = sqlite3_step(stmt_read);
        if (rc == SQLITE_ROW) {
            const void* blob = sqlite3_column_blob(stmt_read, 0);
            int blen = sqlite3_column_bytes(stmt_read, 0);
            if (blen != expected_size) {
                read_fail++;
            } else {
                memcpy(read_buf, blob, blen);
                if (!verify_content(read_buf, expected_size, idx)) {
                    verify_fail++;
                } else {
                    read_ok++;
                }
            }
        } else {
            read_fail++;
        }
        sqlite3_reset(stmt_read);
    }
    QueryPerformanceCounter(&t1);
    sqlite3_finalize(stmt_read);

    double read_ms = elapsed_ms(t0, t1);
    printf("  %d reads in %.1f ms (%.1f ops/s)\n", READ_COUNT, read_ms,
           (double)READ_COUNT / (read_ms / 1000.0));
    printf("  Verified OK: %d | Read errors: %d | Verify mismatch: %d\n\n",
           read_ok, read_fail, verify_fail);

    /* ================================================================
     * Phase 3: MIXED — random reads with occasional writes
     * ================================================================ */
    printf("Phase 3: Mixed read+write (%d reads, write every %d)...\n", MIXED_READS, WRITE_INTERVAL);
    rng_seed(77777u);

    int mixed_reads_ok = 0, mixed_read_fail = 0, mixed_verify_fail = 0;
    int mixed_writes = 0;

    sqlite3_stmt* stmt_mix_insert;
    sqlite3_stmt* stmt_mix_read;
    sqlite3_prepare_v2(db, "INSERT INTO objects(id, name, data) VALUES(?, ?, ?);", -1, &stmt_mix_insert, NULL);
    sqlite3_prepare_v2(db, "SELECT data FROM objects WHERE id = ?;", -1, &stmt_mix_read, NULL);

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
            sqlite3_bind_int64(stmt_mix_insert, 1, (sqlite3_int64)new_idx);
            sqlite3_bind_text(stmt_mix_insert, 2, name, -1, SQLITE_TRANSIENT);
            sqlite3_bind_blob(stmt_mix_insert, 3, buf, obj_size, SQLITE_TRANSIENT);
            rc = sqlite3_step(stmt_mix_insert);
            if (rc == SQLITE_DONE) {
                total_objects++;
                mixed_writes++;
            }
            sqlite3_reset(stmt_mix_insert);
        }

        /* Random read from all objects written so far */
        uint32_t idx = rng_range(0, total_objects - 1);
        int32_t expected_size = sizes[idx];

        sqlite3_bind_int64(stmt_mix_read, 1, (sqlite3_int64)idx);
        rc = sqlite3_step(stmt_mix_read);
        if (rc == SQLITE_ROW) {
            const void* blob = sqlite3_column_blob(stmt_mix_read, 0);
            int blen = sqlite3_column_bytes(stmt_mix_read, 0);
            if (blen != expected_size) {
                mixed_read_fail++;
            } else {
                memcpy(read_buf, blob, blen);
                if (!verify_content(read_buf, expected_size, idx)) {
                    mixed_verify_fail++;
                } else {
                    mixed_reads_ok++;
                }
            }
        } else {
            mixed_read_fail++;
        }
        sqlite3_reset(stmt_mix_read);
    }
    QueryPerformanceCounter(&t1);
    sqlite3_finalize(stmt_mix_insert);
    sqlite3_finalize(stmt_mix_read);

    double mixed_ms = elapsed_ms(t0, t1);
    printf("  %d reads + %d writes in %.1f ms\n", MIXED_READS, mixed_writes, mixed_ms);
    printf("  Read throughput: %.1f ops/s\n", (double)MIXED_READS / (mixed_ms / 1000.0));
    printf("  Verified OK: %d | Read errors: %d | Verify mismatch: %d\n\n",
           mixed_reads_ok, mixed_read_fail, mixed_verify_fail);

    /* ================================================================
     * Store size measurement (before cleanup)
     * ================================================================ */
    sqlite3_close(db);

    int64_t store_size = 0;
    {
        WIN32_FILE_ATTRIBUTE_DATA fdata;
        char wal_path[MAX_PATH], shm_path[MAX_PATH];
        sprintf(wal_path, "%s-wal", db_path);
        sprintf(shm_path, "%s-shm", db_path);

        if (GetFileAttributesExA(db_path, GetFileExInfoStandard, &fdata))
            store_size += ((int64_t)fdata.nFileSizeHigh << 32) | fdata.nFileSizeLow;
        if (GetFileAttributesExA(wal_path, GetFileExInfoStandard, &fdata))
            store_size += ((int64_t)fdata.nFileSizeHigh << 32) | fdata.nFileSizeLow;
        if (GetFileAttributesExA(shm_path, GetFileExInfoStandard, &fdata))
            store_size += ((int64_t)fdata.nFileSizeHigh << 32) | fdata.nFileSizeLow;
    }

    /* ================================================================
     * Summary Report
     * ================================================================ */
    printf("--- SUMMARY (SQLite) ----------------------------------------------\n");
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
    {
        char wal_path[MAX_PATH], shm_path[MAX_PATH];
        sprintf(wal_path, "%s-wal", db_path);
        sprintf(shm_path, "%s-shm", db_path);
        DeleteFileA(wal_path);
        DeleteFileA(shm_path);
    }
    free(sizes);
    free(buf);
    free(read_buf);

    return 0;
}
