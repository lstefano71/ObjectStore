/**
 * bench_objstore.c — Pure C benchmark for ObjectStore via NativeAOT DLL.
 * 
 * Uses LoadLibrary/GetProcAddress to bind the ObjectStore C ABI at runtime.
 * Measures: sequential write, sequential read, transaction batch.
 * Compile: cl /O2 bench_objstore.c /link /out:bench_objstore.exe
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>

/* ---- Function pointer types matching objstore.h ---- */
typedef void* objstore_t;
typedef void* objstore_options_t;

typedef int (*fn_options_create)(objstore_options_t*);
typedef int (*fn_options_free)(objstore_options_t);
typedef int (*fn_open_or_create)(const char*, objstore_options_t, objstore_t*);
typedef int (*fn_close)(objstore_t);
typedef int (*fn_object_create)(objstore_t, const char*, uint64_t*);
typedef int (*fn_append)(objstore_t, uint64_t, const uint8_t*, int32_t);
typedef int (*fn_read)(objstore_t, uint64_t, int64_t, uint8_t*, int32_t, int32_t*);
typedef int (*fn_txn_begin)(objstore_t);
typedef int (*fn_txn_commit)(objstore_t);
typedef int (*fn_object_get_size)(objstore_t, uint64_t, int64_t*);

/* ---- Globals ---- */
static fn_options_create    p_options_create;
static fn_options_free      p_options_free;
static fn_open_or_create    p_open_or_create;
static fn_close             p_close;
static fn_object_create     p_object_create;
static fn_append            p_append;
static fn_read              p_read;
static fn_txn_begin         p_txn_begin;
static fn_txn_commit        p_txn_commit;
static fn_object_get_size   p_object_get_size;

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
    LOAD(open_or_create);
    LOAD(close);
    LOAD(object_create);
    LOAD(append);
    LOAD(read);
    LOAD(txn_begin);
    LOAD(txn_commit);
    LOAD(object_get_size);
    #undef LOAD
    return 1;
}

/* ---- Benchmark helpers ---- */

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

/* ---- Benchmarks ---- */

static bench_result bench_seq_write(objstore_t store, int count, int32_t payload_size, uint8_t* data, uint64_t* out_ids) {
    bench_result r = {0};
    LARGE_INTEGER t0, t1;
    char name[64];
    
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < count; i++) {
        uint64_t id;
        sprintf(name, "obj_%d", i);
        int rc = p_object_create(store, name, &id);
        if (rc != 0) { fprintf(stderr, "create failed: %d at i=%d\n", rc, i); break; }
        rc = p_append(store, id, data, payload_size);
        if (rc != 0) { fprintf(stderr, "append failed: %d at i=%d\n", rc, i); break; }
        if (out_ids) out_ids[i] = id;
    }
    QueryPerformanceCounter(&t1);
    
    r.count = count;
    r.total_ms = elapsed_ms(t0, t1);
    r.ops_per_sec = (double)count / (r.total_ms / 1000.0);
    return r;
}

static bench_result bench_seq_read(objstore_t store, int count, int32_t payload_size, uint8_t* buf, uint64_t* ids) {
    bench_result r = {0};
    LARGE_INTEGER t0, t1;
    
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < count; i++) {
        int32_t bytes_read = 0;
        int rc = p_read(store, ids[i], 0, buf, payload_size, &bytes_read);
        if (rc != 0) { fprintf(stderr, "read failed: %d at i=%d (id=%llu)\n", rc, i, ids[i]); break; }
    }
    QueryPerformanceCounter(&t1);
    
    r.count = count;
    r.total_ms = elapsed_ms(t0, t1);
    r.ops_per_sec = (double)count / (r.total_ms / 1000.0);
    return r;
}

static bench_result bench_txn_batch(objstore_t store, int batch_size, int batches, int32_t payload_size, uint8_t* data) {
    bench_result r = {0};
    LARGE_INTEGER t0, t1;
    char name[64];
    int total = 0;
    
    QueryPerformanceCounter(&t0);
    for (int b = 0; b < batches; b++) {
        int rc = p_txn_begin(store);
        if (rc != 0) { fprintf(stderr, "txn_begin failed: %d\n", rc); break; }
        for (int i = 0; i < batch_size; i++) {
            uint64_t id;
            sprintf(name, "batch_%d_%d", b, i);
            rc = p_object_create(store, name, &id);
            if (rc != 0) { fprintf(stderr, "create in batch failed: %d\n", rc); break; }
            rc = p_append(store, id, data, payload_size);
            if (rc != 0) { fprintf(stderr, "append in batch failed: %d\n", rc); break; }
            total++;
        }
        rc = p_txn_commit(store);
        if (rc != 0) { fprintf(stderr, "txn_commit failed: %d\n", rc); break; }
    }
    QueryPerformanceCounter(&t1);
    
    r.count = total;
    r.total_ms = elapsed_ms(t0, t1);
    r.ops_per_sec = (double)total / (r.total_ms / 1000.0);
    return r;
}

/* ---- Main ---- */
int main(int argc, char* argv[]) {
    const char* dll_path = NULL;
    const char* db_dir = "C:\\temp";
    
    if (argc > 1) dll_path = argv[1];
    if (argc > 2) db_dir = argv[2];
    
    if (!dll_path) {
        fprintf(stderr, "Usage: bench_objstore.exe <path_to_ObjectStore.Native.dll> [db_directory]\n");
        return 1;
    }
    
    QueryPerformanceFrequency(&qpc_freq);
    
    if (!load_dll(dll_path)) return 1;
    
    printf("=== ObjectStore C Benchmark ===\n");
    printf("DLL: %s\n", dll_path);
    printf("Directory: %s\n\n", db_dir);
    
    /* Allocate data buffers */
    uint8_t* data_256 = (uint8_t*)malloc(256);
    uint8_t* data_64k = (uint8_t*)malloc(65536);
    uint8_t* read_buf = (uint8_t*)malloc(65536);
    uint64_t* ids = (uint64_t*)malloc(sizeof(uint64_t) * 1000);
    fill_data(data_256, 256);
    fill_data(data_64k, 65536);
    
    char db_path[MAX_PATH];
    bench_result r;
    
    /* --- Test 1: Sequential Write 256B --- */
    {
        sprintf(db_path, "%s\\bench_objstore_w256.db", db_dir);
        DeleteFileA(db_path);
        
        objstore_options_t opts;
        objstore_t store;
        p_options_create(&opts);
        int rc = p_open_or_create(db_path, opts, &store);
        if (rc != 0) { fprintf(stderr, "open failed: %d\n", rc); return 1; }
        p_options_free(opts);
        
        r = bench_seq_write(store, 500, 256, data_256, NULL);
        print_result("Sequential Write (256B, 500 ops)", &r);
        
        p_close(store);
        DeleteFileA(db_path);
    }
    
    /* --- Test 2: Sequential Write 64KB --- */
    {
        sprintf(db_path, "%s\\bench_objstore_w64k.db", db_dir);
        DeleteFileA(db_path);
        
        objstore_options_t opts;
        objstore_t store;
        p_options_create(&opts);
        int rc = p_open_or_create(db_path, opts, &store);
        if (rc != 0) { fprintf(stderr, "open failed: %d\n", rc); return 1; }
        p_options_free(opts);
        
        r = bench_seq_write(store, 200, 65536, data_64k, NULL);
        print_result("Sequential Write (64KB, 200 ops)", &r);
        
        p_close(store);
        DeleteFileA(db_path);
    }
    
    /* --- Test 3: Sequential Read 256B --- */
    {
        sprintf(db_path, "%s\\bench_objstore_r256.db", db_dir);
        DeleteFileA(db_path);
        
        objstore_options_t opts;
        objstore_t store;
        p_options_create(&opts);
        int rc = p_open_or_create(db_path, opts, &store);
        if (rc != 0) { fprintf(stderr, "open failed: %d\n", rc); return 1; }
        p_options_free(opts);
        
        /* Write 1000 objects first, capturing IDs */
        bench_seq_write(store, 1000, 256, data_256, ids);
        
        r = bench_seq_read(store, 1000, 256, read_buf, ids);
        print_result("Sequential Read (256B, 1000 ops)", &r);
        
        p_close(store);
        DeleteFileA(db_path);
    }
    
    /* --- Test 4: Sequential Read 64KB --- */
    {
        sprintf(db_path, "%s\\bench_objstore_r64k.db", db_dir);
        DeleteFileA(db_path);
        
        objstore_options_t opts;
        objstore_t store;
        p_options_create(&opts);
        int rc = p_open_or_create(db_path, opts, &store);
        if (rc != 0) { fprintf(stderr, "open failed: %d\n", rc); return 1; }
        p_options_free(opts);
        
        /* Write 200 objects first, capturing IDs */
        bench_seq_write(store, 200, 65536, data_64k, ids);
        
        r = bench_seq_read(store, 200, 65536, read_buf, ids);
        print_result("Sequential Read (64KB, 200 ops)", &r);
        
        p_close(store);
        DeleteFileA(db_path);
    }
    
    /* --- Test 5: Transaction Batch (200×256B per txn, 5 batches) --- */
    {
        sprintf(db_path, "%s\\bench_objstore_batch.db", db_dir);
        DeleteFileA(db_path);
        
        objstore_options_t opts;
        objstore_t store;
        p_options_create(&opts);
        int rc = p_open_or_create(db_path, opts, &store);
        if (rc != 0) { fprintf(stderr, "open failed: %d\n", rc); return 1; }
        p_options_free(opts);
        
        r = bench_txn_batch(store, 200, 5, 256, data_256);
        print_result("Txn Batch (200/txn x5, 256B)", &r);
        
        p_close(store);
        DeleteFileA(db_path);
    }
    
    /* --- Test 6: Transaction Batch (200×64KB per txn, 3 batches) --- */
    {
        sprintf(db_path, "%s\\bench_objstore_batch64k.db", db_dir);
        DeleteFileA(db_path);
        
        objstore_options_t opts;
        objstore_t store;
        p_options_create(&opts);
        int rc = p_open_or_create(db_path, opts, &store);
        if (rc != 0) { fprintf(stderr, "open failed: %d\n", rc); return 1; }
        p_options_free(opts);
        
        r = bench_txn_batch(store, 200, 3, 65536, data_64k);
        print_result("Txn Batch (200/txn x3, 64KB)", &r);
        
        p_close(store);
        DeleteFileA(db_path);
    }
    
    free(data_256);
    free(data_64k);
    free(read_buf);
    free(ids);
    
    printf("\nDone.\n");
    return 0;
}
