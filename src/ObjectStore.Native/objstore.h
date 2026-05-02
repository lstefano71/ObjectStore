/**
 * @file objstore.h
 * @brief ObjectStore C API — stable ABI for a crash-safe, MVCC, single-file object store.
 *
 * All functions return OBJSTORE_OK (0) on success, or a negative error code on failure.
 * Thread safety: A single store handle must not be used concurrently from multiple threads
 * without external synchronization, unless opened read-only.
 */

#ifndef OBJSTORE_H
#define OBJSTORE_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ============================================================
 * Error Codes
 * ============================================================ */

#define OBJSTORE_OK                0
#define OBJSTORE_ERR              -1
#define OBJSTORE_ERR_NOT_FOUND    -2
#define OBJSTORE_ERR_EXISTS       -3
#define OBJSTORE_ERR_READONLY     -4
#define OBJSTORE_ERR_INVALID_ARG  -5
#define OBJSTORE_ERR_BUF_TOO_SMALL -6
#define OBJSTORE_ERR_NO_MORE      -7
#define OBJSTORE_ERR_TIMEOUT      -8
#define OBJSTORE_ERR_LOCK         -9
#define OBJSTORE_ERR_VERSION     -10
#define OBJSTORE_ERR_CORRUPT     -11
#define OBJSTORE_ERR_TXN_ABORTED -12
#define OBJSTORE_ERR_NAME_NOT_FOUND -13

/** Iterator end sentinel (returned by objstore_iter_next when exhausted). */
#define OBJSTORE_ITER_END          1

/* ============================================================
 * Opaque Handle Types
 * ============================================================ */

/** Opaque handle to an open object store. */
typedef void* objstore_t;

/** Opaque handle to a configuration options object. */
typedef void* objstore_options_t;

/** Opaque handle to an iterator over objects/children. */
typedef void* objstore_iter_t;

/* ============================================================
 * Version
 * ============================================================ */

/**
 * Gets the library version.
 * @param[out] out_major  Pointer to receive major version number.
 * @param[out] out_minor  Pointer to receive minor version number.
 */
int objstore_get_version(int32_t* out_major, int32_t* out_minor);

/* ============================================================
 * Options
 * ============================================================ */

int objstore_options_create(objstore_options_t* out_opts);
int objstore_options_free(objstore_options_t opts);
int objstore_options_set_read_only(objstore_options_t opts, int read_only);
int objstore_options_set_shared_access(objstore_options_t opts, int shared);
int objstore_options_set_cache_max_bytes(objstore_options_t opts, int64_t bytes);
int objstore_options_set_lock_timeout_ms(objstore_options_t opts, int32_t ms);
int objstore_options_set_encryption_key(objstore_options_t opts, const uint8_t* key, size_t key_len);
int objstore_options_set_compression(objstore_options_t opts, int codec);

/** Compression codecs for objstore_options_set_compression. */
#define OBJSTORE_COMPRESS_NONE    0
#define OBJSTORE_COMPRESS_DEFLATE 1
#define OBJSTORE_COMPRESS_BROTLI  2

/* ============================================================
 * Store Lifecycle
 * ============================================================ */

int objstore_create(const char* path, objstore_options_t opts, objstore_t* out_store);
int objstore_open(const char* path, objstore_options_t opts, objstore_t* out_store);
int objstore_open_or_create(const char* path, objstore_options_t opts, objstore_t* out_store);
int objstore_open_readonly(const char* path, objstore_options_t opts, objstore_t* out_store);
int objstore_close(objstore_t store);

/* ============================================================
 * Object CRUD
 * ============================================================ */

int objstore_object_create(objstore_t store, const char* name, uint64_t* out_id);
int objstore_object_delete(objstore_t store, uint64_t object_id);
int objstore_object_exists(objstore_t store, uint64_t object_id, int32_t* out_exists);
int objstore_object_get_size(objstore_t store, uint64_t object_id, int64_t* out_size);

/* ============================================================
 * Data I/O
 * ============================================================ */

int objstore_read(objstore_t store, uint64_t object_id, int64_t offset,
                  uint8_t* buffer, int32_t buffer_len, int32_t* out_bytes_read);
int objstore_write(objstore_t store, uint64_t object_id, int64_t offset,
                   const uint8_t* data, int32_t length);
int objstore_append(objstore_t store, uint64_t object_id,
                    const uint8_t* data, int32_t length);
int objstore_truncate(objstore_t store, uint64_t object_id, int64_t new_length);

/* ============================================================
 * Transactions
 * ============================================================ */

int objstore_txn_begin(objstore_t store);
int objstore_txn_commit(objstore_t store);
int objstore_txn_rollback(objstore_t store);

/* ============================================================
 * Metadata (per-object key-value pairs)
 * ============================================================ */

int objstore_metadata_set(objstore_t store, uint64_t object_id,
                          const char* key, const char* value);

/**
 * Gets a metadata value. Uses two-call pattern:
 * First call with buffer=NULL to get length, then with buffer to get data.
 */
int objstore_metadata_get(objstore_t store, uint64_t object_id,
                          const char* key, char* buffer, int32_t buffer_len,
                          int32_t* out_len);
int objstore_metadata_delete(objstore_t store, uint64_t object_id, const char* key);

/* ============================================================
 * Iterator (object listing)
 * ============================================================ */

int objstore_list_begin(objstore_t store, objstore_iter_t* out_iter);
int objstore_iter_next(objstore_iter_t iter, uint64_t* out_id, int64_t* out_size);
int objstore_iter_close(objstore_iter_t iter);

/* ============================================================
 * Statistics & Maintenance
 * ============================================================ */

int objstore_get_stats(objstore_t store, int32_t* out_object_count,
                       int64_t* out_total_size, int64_t* out_file_size);
int objstore_defragment(objstore_t store, int32_t* out_count);
int objstore_recover(objstore_t store, int32_t* out_needed);

/* ============================================================
 * Node Hierarchy
 * ============================================================ */

int objstore_get_root_id(objstore_t store, uint64_t* out_id);
int objstore_create_child(objstore_t store, uint64_t parent_id,
                          const char* name, int is_container, uint64_t* out_id);
int objstore_move_node(objstore_t store, uint64_t node_id,
                       uint64_t new_parent_id, const char* new_name);
int objstore_delete_subtree(objstore_t store, uint64_t node_id);
int objstore_resolve_path(objstore_t store, const char* path, uint64_t* out_id);
int objstore_list_children_begin(objstore_t store, uint64_t parent_id,
                                 objstore_iter_t* out_iter);

#ifdef __cplusplus
}
#endif

#endif /* OBJSTORE_H */
