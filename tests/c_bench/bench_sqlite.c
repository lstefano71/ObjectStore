/**
 * bench_sqlite.c — Pure C benchmark for SQLite (linked via amalgamation).
 * 
 * Same workloads as bench_objstore.c for direct comparison.
 * Compile: cl /O2 bench_sqlite.c sqlite3.c /link /out:bench_sqlite.exe
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include "sqlite3.h"

static LARGE_INTEGER qpc_freq;

static double elapsed_ms(LARGE_INTEGER start, LARGE_INTEGER end) {
    return (double)(end.QuadPart - start.QuadPart) * 1000.0 / (double)qpc_freq.QuadPart;
}

static void fill_data(uint8_t* buf, int32_t len) {
    for (int32_t i = 0; i < len; i++) buf[i] = (uint8_t)(i & 0xFF);
}

typedef struct {
    int count;
    double total_ms;
    double ops_per_sec;
} bench_result;

static void print_result(const char* name, bench_result* r) {
    printf("  %-35s %6d ops  %8.1f ms  %10.1f ops/s\n",
           name, r->count, r->total_ms, r->ops_per_sec);
}

/* ---- Setup ---- */

static int setup_db(sqlite3* db) {
    char* err = NULL;
    int rc;
    
    rc = sqlite3_exec(db, "PRAGMA journal_mode=WAL;", NULL, NULL, &err);
    if (rc != SQLITE_OK) { fprintf(stderr, "WAL: %s\n", err); sqlite3_free(err); return rc; }
    
    rc = sqlite3_exec(db, "PRAGMA synchronous=FULL;", NULL, NULL, &err);
    if (rc != SQLITE_OK) { fprintf(stderr, "sync: %s\n", err); sqlite3_free(err); return rc; }
    
    rc = sqlite3_exec(db,
        "CREATE TABLE IF NOT EXISTS objects ("
        "  id INTEGER PRIMARY KEY AUTOINCREMENT,"
        "  name TEXT UNIQUE NOT NULL,"
        "  data BLOB DEFAULT X''"
        ");", NULL, NULL, &err);
    if (rc != SQLITE_OK) { fprintf(stderr, "create table: %s\n", err); sqlite3_free(err); return rc; }
    
    return SQLITE_OK;
}

/* ---- Benchmarks ---- */

static bench_result bench_seq_write(sqlite3* db, int count, int32_t payload_size, uint8_t* data) {
    bench_result r = {0};
    LARGE_INTEGER t0, t1;
    sqlite3_stmt* stmt_insert;
    char name[64];
    
    /* Each write = INSERT with blob data (auto-commit per statement) */
    sqlite3_prepare_v2(db, "INSERT INTO objects(name, data) VALUES(?, ?);", -1, &stmt_insert, NULL);
    
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < count; i++) {
        sprintf(name, "obj_%d", i);
        sqlite3_bind_text(stmt_insert, 1, name, -1, SQLITE_TRANSIENT);
        sqlite3_bind_blob(stmt_insert, 2, data, payload_size, SQLITE_STATIC);
        int rc = sqlite3_step(stmt_insert);
        if (rc != SQLITE_DONE) {
            fprintf(stderr, "insert failed: %s at i=%d\n", sqlite3_errmsg(db), i);
            break;
        }
        sqlite3_reset(stmt_insert);
    }
    QueryPerformanceCounter(&t1);
    
    sqlite3_finalize(stmt_insert);
    r.count = count;
    r.total_ms = elapsed_ms(t0, t1);
    r.ops_per_sec = (double)count / (r.total_ms / 1000.0);
    return r;
}

static bench_result bench_seq_read(sqlite3* db, int count, int32_t payload_size, uint8_t* buf) {
    bench_result r = {0};
    LARGE_INTEGER t0, t1;
    sqlite3_stmt* stmt_read;
    (void)payload_size; /* used only to match interface */
    
    sqlite3_prepare_v2(db, "SELECT data FROM objects WHERE id = ?;", -1, &stmt_read, NULL);
    
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < count; i++) {
        sqlite3_bind_int64(stmt_read, 1, (sqlite3_int64)(i + 1));
        int rc = sqlite3_step(stmt_read);
        if (rc == SQLITE_ROW) {
            const void* blob = sqlite3_column_blob(stmt_read, 0);
            int blen = sqlite3_column_bytes(stmt_read, 0);
            if (blob && blen > 0) {
                memcpy(buf, blob, blen < 65536 ? blen : 65536);
            }
        } else {
            fprintf(stderr, "read failed: %s at i=%d\n", sqlite3_errmsg(db), i);
            break;
        }
        sqlite3_reset(stmt_read);
    }
    QueryPerformanceCounter(&t1);
    
    sqlite3_finalize(stmt_read);
    r.count = count;
    r.total_ms = elapsed_ms(t0, t1);
    r.ops_per_sec = (double)count / (r.total_ms / 1000.0);
    return r;
}

static bench_result bench_txn_batch(sqlite3* db, int batch_size, int batches, int32_t payload_size, uint8_t* data) {
    bench_result r = {0};
    LARGE_INTEGER t0, t1;
    sqlite3_stmt* stmt_insert;
    char name[64];
    int total = 0;
    
    sqlite3_prepare_v2(db, "INSERT INTO objects(name, data) VALUES(?, ?);", -1, &stmt_insert, NULL);
    
    QueryPerformanceCounter(&t0);
    for (int b = 0; b < batches; b++) {
        sqlite3_exec(db, "BEGIN TRANSACTION;", NULL, NULL, NULL);
        for (int i = 0; i < batch_size; i++) {
            sprintf(name, "batch_%d_%d", b, i);
            sqlite3_bind_text(stmt_insert, 1, name, -1, SQLITE_TRANSIENT);
            sqlite3_bind_blob(stmt_insert, 2, data, payload_size, SQLITE_STATIC);
            int rc = sqlite3_step(stmt_insert);
            if (rc != SQLITE_DONE) {
                fprintf(stderr, "insert in batch failed: %s\n", sqlite3_errmsg(db));
                break;
            }
            sqlite3_reset(stmt_insert);
            total++;
        }
        sqlite3_exec(db, "COMMIT;", NULL, NULL, NULL);
    }
    QueryPerformanceCounter(&t1);
    
    sqlite3_finalize(stmt_insert);
    r.count = total;
    r.total_ms = elapsed_ms(t0, t1);
    r.ops_per_sec = (double)total / (r.total_ms / 1000.0);
    return r;
}

/* ---- Main ---- */
int main(int argc, char* argv[]) {
    const char* db_dir = "C:\\temp";
    if (argc > 1) db_dir = argv[1];
    
    QueryPerformanceFrequency(&qpc_freq);
    
    printf("=== SQLite C Benchmark ===\n");
    printf("SQLite version: %s\n", sqlite3_libversion());
    printf("Directory: %s\n\n", db_dir);
    
    uint8_t* data_256 = (uint8_t*)malloc(256);
    uint8_t* data_64k = (uint8_t*)malloc(65536);
    uint8_t* read_buf = (uint8_t*)malloc(65536);
    fill_data(data_256, 256);
    fill_data(data_64k, 65536);
    
    char db_path[MAX_PATH];
    bench_result r;
    sqlite3* db;
    
    /* --- Test 1: Sequential Write 256B --- */
    {
        sprintf(db_path, "%s\\bench_sqlite_w256.db", db_dir);
        DeleteFileA(db_path);
        
        sqlite3_open(db_path, &db);
        setup_db(db);
        
        r = bench_seq_write(db, 500, 256, data_256);
        print_result("Sequential Write (256B, 500 ops)", &r);
        
        sqlite3_close(db);
        DeleteFileA(db_path);
        /* Clean WAL/SHM files */
        char wal_path[MAX_PATH], shm_path[MAX_PATH];
        sprintf(wal_path, "%s-wal", db_path);
        sprintf(shm_path, "%s-shm", db_path);
        DeleteFileA(wal_path);
        DeleteFileA(shm_path);
    }
    
    /* --- Test 2: Sequential Write 64KB --- */
    {
        sprintf(db_path, "%s\\bench_sqlite_w64k.db", db_dir);
        DeleteFileA(db_path);
        
        sqlite3_open(db_path, &db);
        setup_db(db);
        
        r = bench_seq_write(db, 200, 65536, data_64k);
        print_result("Sequential Write (64KB, 200 ops)", &r);
        
        sqlite3_close(db);
        DeleteFileA(db_path);
        char wal_path[MAX_PATH], shm_path[MAX_PATH];
        sprintf(wal_path, "%s-wal", db_path);
        sprintf(shm_path, "%s-shm", db_path);
        DeleteFileA(wal_path);
        DeleteFileA(shm_path);
    }
    
    /* --- Test 3: Sequential Read 256B --- */
    {
        sprintf(db_path, "%s\\bench_sqlite_r256.db", db_dir);
        DeleteFileA(db_path);
        
        sqlite3_open(db_path, &db);
        setup_db(db);
        
        /* Write 1000 objects first */
        sqlite3_stmt* ins;
        sqlite3_prepare_v2(db, "INSERT INTO objects(name,data) VALUES(?,?);", -1, &ins, NULL);
        sqlite3_exec(db, "BEGIN;", NULL, NULL, NULL);
        for (int i = 0; i < 1000; i++) {
            char name[64];
            sprintf(name, "obj_%d", i);
            sqlite3_bind_text(ins, 1, name, -1, SQLITE_TRANSIENT);
            sqlite3_bind_blob(ins, 2, data_256, 256, SQLITE_STATIC);
            sqlite3_step(ins);
            sqlite3_reset(ins);
        }
        sqlite3_exec(db, "COMMIT;", NULL, NULL, NULL);
        sqlite3_finalize(ins);
        
        r = bench_seq_read(db, 1000, 256, read_buf);
        print_result("Sequential Read (256B, 1000 ops)", &r);
        
        sqlite3_close(db);
        DeleteFileA(db_path);
        char wal_path[MAX_PATH], shm_path[MAX_PATH];
        sprintf(wal_path, "%s-wal", db_path);
        sprintf(shm_path, "%s-shm", db_path);
        DeleteFileA(wal_path);
        DeleteFileA(shm_path);
    }
    
    /* --- Test 4: Sequential Read 64KB --- */
    {
        sprintf(db_path, "%s\\bench_sqlite_r64k.db", db_dir);
        DeleteFileA(db_path);
        
        sqlite3_open(db_path, &db);
        setup_db(db);
        
        /* Write 200 objects first */
        sqlite3_stmt* ins;
        sqlite3_prepare_v2(db, "INSERT INTO objects(name,data) VALUES(?,?);", -1, &ins, NULL);
        sqlite3_exec(db, "BEGIN;", NULL, NULL, NULL);
        for (int i = 0; i < 200; i++) {
            char name[64];
            sprintf(name, "obj_%d", i);
            sqlite3_bind_text(ins, 1, name, -1, SQLITE_TRANSIENT);
            sqlite3_bind_blob(ins, 2, data_64k, 65536, SQLITE_STATIC);
            sqlite3_step(ins);
            sqlite3_reset(ins);
        }
        sqlite3_exec(db, "COMMIT;", NULL, NULL, NULL);
        sqlite3_finalize(ins);
        
        r = bench_seq_read(db, 200, 65536, read_buf);
        print_result("Sequential Read (64KB, 200 ops)", &r);
        
        sqlite3_close(db);
        DeleteFileA(db_path);
        char wal_path[MAX_PATH], shm_path[MAX_PATH];
        sprintf(wal_path, "%s-wal", db_path);
        sprintf(shm_path, "%s-shm", db_path);
        DeleteFileA(wal_path);
        DeleteFileA(shm_path);
    }
    
    /* --- Test 5: Transaction Batch (200×256B per txn, 5 batches) --- */
    {
        sprintf(db_path, "%s\\bench_sqlite_batch.db", db_dir);
        DeleteFileA(db_path);
        
        sqlite3_open(db_path, &db);
        setup_db(db);
        
        r = bench_txn_batch(db, 200, 5, 256, data_256);
        print_result("Txn Batch (200/txn x5, 256B)", &r);
        
        sqlite3_close(db);
        DeleteFileA(db_path);
        char wal_path[MAX_PATH], shm_path[MAX_PATH];
        sprintf(wal_path, "%s-wal", db_path);
        sprintf(shm_path, "%s-shm", db_path);
        DeleteFileA(wal_path);
        DeleteFileA(shm_path);
    }
    
    /* --- Test 6: Transaction Batch (200×64KB per txn, 3 batches) --- */
    {
        sprintf(db_path, "%s\\bench_sqlite_batch64k.db", db_dir);
        DeleteFileA(db_path);
        
        sqlite3_open(db_path, &db);
        setup_db(db);
        
        r = bench_txn_batch(db, 200, 3, 65536, data_64k);
        print_result("Txn Batch (200/txn x3, 64KB)", &r);
        
        sqlite3_close(db);
        DeleteFileA(db_path);
        char wal_path[MAX_PATH], shm_path[MAX_PATH];
        sprintf(wal_path, "%s-wal", db_path);
        sprintf(shm_path, "%s-shm", db_path);
        DeleteFileA(wal_path);
        DeleteFileA(shm_path);
    }
    
    free(data_256);
    free(data_64k);
    free(read_buf);
    
    printf("\nDone.\n");
    return 0;
}
