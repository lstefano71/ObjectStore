# ObjectStore C API — Product Requirements Document

## 1. Overview

The ObjectStore C API is a native shared library (`objstore.dll` / `libobjecstore.so` / `libobjectstore.dylib`) produced by compiling the ObjectStore C# library with **.NET NativeAOT**. It exposes a stable, ABI-safe C interface so that any FFI-enabled programming language (Python, Rust, Go, Julia, Ruby, Zig, C, C++, …) can embed ObjectStore without requiring a .NET runtime on the caller's side.

The C API is a separate, optional layer on top of the core C# library. It adds no new features — it is a projection of the existing `ObjectStore` semantics through a C-compatible surface.

---

## 2. Goals

- **Zero .NET runtime dependency** for FFI callers — NativeAOT produces a self-contained native library.
- **ABI safety** — no struct layout dependencies across versions; all complex types cross the boundary as opaque handles or primitive scalars.
- **Two-tier handle model** — a low-level opaque-pointer API for performance-conscious callers, plus a high-level string-name API for languages where pointer lifetime is awkward.
- **Uniform error model** — every function returns `int32_t`; 0 = success, negative = error code.
- **Predictable memory ownership** — all output buffers are caller-allocated; library never allocates memory the caller must free (except iterator/options handles which have explicit close/free functions).
- **Thread-safe** — the library's global registry (for the string-name tier) is guarded by a reader-writer lock.

## 3. Non-Goals

- Not a re-implementation — the C API wraps the C# implementation; all storage logic lives there.
- No automatic code generation of language bindings (Python ctypes, Rust bindgen, etc.) — `objstore.h` is the single source of truth; binding generation is the caller's responsibility.
- No async/event-loop integration — all calls are synchronous (blocking with optional timeout).
- No C++ overloads or RAII wrappers in the distributed header — `objstore.h` is plain C99.

---

## 4. Design Decisions

### 4.1 NativeAOT Compilation

> **The core `ObjectStore` library must be NativeAOT-compatible.** This is not just a C API concern — it is NFR-11 of the core library. The `ObjectStore.Native` project simply enables `PublishAot=true`; if the core library uses unconstrained reflection, `dynamic`, or `Emit`, the native build will fail or produce a broken library. AOT compliance must be enforced in the core library's CI pipeline from the beginning.

The C# project is published with:
```
dotnet publish -r <rid> -p:PublishAot=true -p:NativeLib=Shared
```
All exported C functions are decorated with `[UnmanagedCallersOnly(EntryPoint = "objstore_...")]`. The resulting shared library is a standard native library with a stable C ABI.

### 4.2 Two-Tier Handle Model

**Tier 1 — Opaque handles (low-level)**

Every live object is represented by a typed opaque pointer:

```c
typedef struct objstore_store_s*    objstore_store_t;
typedef struct objstore_txn_s*      objstore_txn_t;
typedef struct objstore_stream_s*   objstore_stream_t;
typedef struct objstore_iter_s*     objstore_iter_t;
typedef struct objstore_options_s*  objstore_options_t;
```

On the managed side, each pointer is a GC-pinned `GCHandle` wrapping the corresponding managed object. The handle is valid from creation until the matching close/dispose call.

**Tier 2 — String-name layer (high-level)**

A global in-library registry maps caller-supplied UTF-8 string names → managed object handles. All Tier-1 functions have a `_named` variant that looks up the handle by name:

```c
// Tier 1
int32_t objstore_open(const char* path, size_t path_len,
                      objstore_options_t options,       // may be NULL
                      objstore_store_t* out_store);

// Tier 2
int32_t objstore_named_open(const char* name, size_t name_len,
                            const char* path, size_t path_len,
                            objstore_options_t options);

// Later, look up by name:
int32_t objstore_named_get_store(const char* name, size_t name_len,
                                 objstore_store_t* out_store);
```

String names are UTF-8, may contain any bytes except `\0`, max 255 bytes. The registry is a `ConcurrentDictionary` guarded for thread safety.

### 4.3 Error Model

#### 4.3.1 Return codes

Every function returns `int32_t`. Codes:

| Value | Constant | Meaning |
|---|---|---|
| `0` | `OBJSTORE_OK` | Success |
| `-1` | `OBJSTORE_ERR` | Generic / unexpected error |
| `-2` | `OBJSTORE_ERR_NOT_FOUND` | Object not found |
| `-3` | `OBJSTORE_ERR_EXISTS` | Object already exists |
| `-4` | `OBJSTORE_ERR_READ_ONLY` | Store opened read-only |
| `-5` | `OBJSTORE_ERR_INVALID_ARG` | Invalid argument |
| `-6` | `OBJSTORE_ERR_BUFFER_TOO_SMALL` | Caller buffer too small (two-call pattern) |
| `-7` | `OBJSTORE_NO_MORE_ITEMS` | Iterator exhausted |
| `-8` | `OBJSTORE_ERR_TIMEOUT` | Operation timed out |
| `-9` | `OBJSTORE_ERR_LOCK` | Lock acquisition failed |
| `-10` | `OBJSTORE_ERR_VERSION` | Incompatible container version |
| `-11` | `OBJSTORE_ERR_CORRUPT` | Checksum / integrity failure |
| `-12` | `OBJSTORE_ERR_TXN_ABORTED` | Transaction aborted |
| `-13` | `OBJSTORE_ERR_NAME_NOT_FOUND` | Name not in registry (Tier-2 only) |

#### 4.3.2 Exception handling at the FFI boundary

In NativeAOT, any unhandled exception crossing an `[UnmanagedCallersOnly]` boundary causes **immediate, unrecoverable process termination**. Therefore every exported function is wrapped in a top-level `try/catch(Exception)`:

```csharp
[UnmanagedCallersOnly(EntryPoint = "objstore_append")]
public static int32_t objstore_append(objstore_store_t store, ulong id, ...)
{
    try {
        // ... actual work via SyncBridge
        return (int32_t)NativeError.Ok;
    }
    catch (Exception ex) {
        return NativeErrorHelper.Capture(ex);   // maps + stores detail
    }
}
```

`NativeErrorHelper.Capture(ex)` does two things:

1. **Maps** the exception type to an error code:

| C# exception | Error code |
|---|---|
| `ObjectNotFoundException` | `OBJSTORE_ERR_NOT_FOUND` |
| `ObjectAlreadyExistsException` | `OBJSTORE_ERR_EXISTS` |
| `ReadOnlyContainerException` | `OBJSTORE_ERR_READ_ONLY` |
| `BlockCorruptedException` / `TamperedBlockException` | `OBJSTORE_ERR_CORRUPT` |
| `IncompatibleVersionException` | `OBJSTORE_ERR_VERSION` |
| `OperationCanceledException` (from timeout) | `OBJSTORE_ERR_TIMEOUT` |
| `LockTimeoutException` | `OBJSTORE_ERR_LOCK` |
| `ArgumentException` / `ArgumentNullException` | `OBJSTORE_ERR_INVALID_ARG` |
| `TransactionAbortedException` | `OBJSTORE_ERR_TXN_ABORTED` |
| anything else | `OBJSTORE_ERR` |

2. **Stores** `ex.Message` (and `ex.ToString()` for full stack trace in debug builds) in a **`[ThreadStatic]`** slot so the caller can retrieve it after the call returns.

#### 4.3.3 Two-level error information

There are two complementary error-info functions:

| Function | What it returns |
|---|---|
| `objstore_error_message(rc, buf, len, out_len)` | **Static** description of an error code — e.g. `"Object not found"`. Same string for every call with the same `rc`. |
| `objstore_get_last_error_detail(buf, len, out_len)` | **Dynamic** message from the last failed call on this thread — e.g. `"Object 'invoice-2026-01' not found in store"` or `"Block at offset 0x1A3F00 failed checksum: expected 0xDEADBEEF got 0xCAFEBABE"`. Thread-local; overwritten by the next failing call on the same thread. Returns empty string if the last call succeeded. |

Both use the two-call buffer pattern. This mirrors the Win32 `GetLastError` + `FormatMessage`, libpq `PQerrorMessage`, and SQLite `sqlite3_errmsg` conventions.

> **Thread safety**: the last-error detail slot is `[ThreadStatic]` — each OS thread has its own slot. Callers must read the detail on the same thread that made the failing call.

### 4.4 Strings and Buffers

All string parameters are `(const char* str, size_t len)` pairs — UTF-8, may contain embedded nulls. Output strings follow the **two-call pattern**:

```c
// First call: buf = NULL → out_len receives required byte count (excluding any null terminator)
// Second call: buf != NULL, buf_len >= *out_len → buf is filled
int32_t objstore_get_name(objstore_store_t store,
                          ulong id,
                          char* buf, size_t buf_len,
                          size_t* out_len);
```

All byte-buffer outputs follow the same pattern.

### 4.5 Options Handle

Options are built with an opaque `objstore_options_t` handle, set with typed setters, then passed to open/create functions. The handle is freed after use.

```c
int32_t objstore_options_create(objstore_options_t* out_opts);
int32_t objstore_options_set_read_only(objstore_options_t opts, int32_t value);
int32_t objstore_options_set_shared_access(objstore_options_t opts, int32_t value);
int32_t objstore_options_set_cache_max_bytes(objstore_options_t opts, int64_t value);
int32_t objstore_options_set_lock_timeout_ms(objstore_options_t opts, int32_t value);
int32_t objstore_options_set_encryption_key(objstore_options_t opts,
                                             const uint8_t* key, size_t key_len);
void    objstore_options_free(objstore_options_t opts);
```

### 4.6 Blocking + Timeout

All potentially-blocking operations accept an `int32_t timeout_ms` parameter:
- `-1` = wait indefinitely
- `0` = non-blocking (return `OBJSTORE_ERR_TIMEOUT` immediately if not ready)
- `> 0` = wait up to that many milliseconds

Internally, the managed async method is awaited synchronously via `GetAwaiter().GetResult()` on a dedicated thread-pool thread, with a `CancellationToken` for the timeout.

### 4.7 Object Enumeration

```c
int32_t objstore_list_begin(objstore_store_t store, objstore_iter_t* out_iter);
int32_t objstore_iter_next(objstore_iter_t iter, objstore_object_info_t* out_info);
int32_t objstore_iter_close(objstore_iter_t iter);
```

`objstore_object_info_t` is a struct with fixed-size fields:
```c
typedef struct {
    uint64_t id;
    int64_t  size;
    int64_t  created_unix_ms;
    int64_t  modified_unix_ms;
    uint8_t  compression;      // 0=None, 1=Lz4, 2=Zstd
    uint8_t  has_name;
    char     name[256];        // UTF-8, null-terminated, valid only if has_name != 0
} objstore_object_info_t;
```

Metadata (key-value bag) is not included in `objstore_object_info_t` — access it separately via `objstore_get_metadata`.

### 4.8 Versioning

The header defines:
```c
#define OBJSTORE_API_VERSION_MAJOR  1
#define OBJSTORE_API_VERSION_MINOR  0
```

At runtime:
```c
int32_t objstore_get_version(int32_t* out_major, int32_t* out_minor);
```

Callers should verify at startup that `out_major == OBJSTORE_API_VERSION_MAJOR`. A mismatch on major version means ABI-breaking changes; the library should refuse to operate.

---

## 5. Full API Surface

### 5.1 Library Init / Version / Error Info

```c
int32_t objstore_get_version(int32_t* out_major, int32_t* out_minor);

// Static description of an error code (same string every time for the same rc).
// Two-call pattern: out_len receives required bytes when buf == NULL.
int32_t objstore_error_message(int32_t rc,
                               char* buf, size_t buf_len, size_t* out_len);

// Dynamic message from the last failed call on this thread.
// Empty string if the last call on this thread succeeded.
// Thread-local: must be read on the same thread that made the failing call.
// Two-call pattern: out_len receives required bytes when buf == NULL.
int32_t objstore_get_last_error_detail(char* buf, size_t buf_len, size_t* out_len);
```

### 5.2 Options

```c
int32_t objstore_options_create(objstore_options_t* out_opts);
int32_t objstore_options_set_read_only(objstore_options_t opts, int32_t value);
int32_t objstore_options_set_shared_access(objstore_options_t opts, int32_t value);
int32_t objstore_options_set_cache_max_bytes(objstore_options_t opts, int64_t value);
int32_t objstore_options_set_lock_timeout_ms(objstore_options_t opts, int32_t value);
int32_t objstore_options_set_encryption_key(objstore_options_t opts,
                                             const uint8_t* key, size_t key_len);
void    objstore_options_free(objstore_options_t opts);
```

### 5.3 Store Lifecycle (Tier 1)

```c
int32_t objstore_create(const char* path, size_t path_len,
                        objstore_options_t options,
                        objstore_store_t* out_store);

int32_t objstore_open(const char* path, size_t path_len,
                      objstore_options_t options,
                      objstore_store_t* out_store);

int32_t objstore_open_or_create(const char* path, size_t path_len,
                                objstore_options_t options,
                                objstore_store_t* out_store);

int32_t objstore_close(objstore_store_t store, int32_t timeout_ms);

int32_t objstore_recover(objstore_store_t store, int32_t timeout_ms);

int32_t objstore_defragment(objstore_store_t store, int32_t timeout_ms);

int32_t objstore_get_stats(objstore_store_t store, objstore_stats_t* out_stats);
```

```c
typedef struct {
    uint64_t total_bytes;
    uint64_t used_bytes;
    uint64_t free_bytes;
    uint64_t object_count;
    double   fragmentation_ratio;
} objstore_stats_t;
```

### 5.4 Store Lifecycle (Tier 2 — string-name)

```c
int32_t objstore_named_create(const char* name, size_t name_len,
                              const char* path, size_t path_len,
                              objstore_options_t options);

int32_t objstore_named_open(const char* name, size_t name_len,
                            const char* path, size_t path_len,
                            objstore_options_t options);

int32_t objstore_named_open_or_create(const char* name, size_t name_len,
                                      const char* path, size_t path_len,
                                      objstore_options_t options);

// Retrieve Tier-1 handle by name (for mixed use)
int32_t objstore_named_get_store(const char* name, size_t name_len,
                                 objstore_store_t* out_store);

int32_t objstore_named_close(const char* name, size_t name_len, int32_t timeout_ms);
```

### 5.5 Transactions (Tier 1)

```c
int32_t objstore_txn_begin(objstore_store_t store, objstore_txn_t* out_txn);
int32_t objstore_txn_commit(objstore_txn_t txn, int32_t timeout_ms);
int32_t objstore_txn_rollback(objstore_txn_t txn);
int32_t objstore_txn_close(objstore_txn_t txn);  // rollback if not committed
```

### 5.6 Transactions (Tier 2)

```c
int32_t objstore_named_txn_begin(const char* store_name, size_t store_name_len,
                                  const char* txn_name,  size_t txn_name_len);
int32_t objstore_named_txn_commit(const char* txn_name, size_t txn_name_len,
                                   int32_t timeout_ms);
int32_t objstore_named_txn_rollback(const char* txn_name, size_t txn_name_len);
int32_t objstore_named_txn_close(const char* txn_name, size_t txn_name_len);
```

### 5.7 Object Management

```c
// Create (txn may be NULL for auto-commit)
int32_t objstore_object_create(objstore_store_t store,
                               const char* name, size_t name_len,  // may be NULL/0
                               uint8_t compression,                 // 0=None,1=Lz4,2=Zstd
                               objstore_txn_t txn,
                               uint64_t* out_id);

int32_t objstore_object_delete(objstore_store_t store, uint64_t id,
                               objstore_txn_t txn);

int32_t objstore_object_delete_by_name(objstore_store_t store,
                                       const char* name, size_t name_len,
                                       objstore_txn_t txn);

int32_t objstore_object_rename(objstore_store_t store, uint64_t id,
                               const char* new_name, size_t new_name_len,
                               objstore_txn_t txn);

int32_t objstore_object_exists(objstore_store_t store, uint64_t id,
                               int32_t* out_exists);

int32_t objstore_object_exists_by_name(objstore_store_t store,
                                       const char* name, size_t name_len,
                                       int32_t* out_exists);

int32_t objstore_object_resolve_name(objstore_store_t store,
                                     const char* name, size_t name_len,
                                     uint64_t* out_id);

int32_t objstore_object_get_info(objstore_store_t store, uint64_t id,
                                 objstore_object_info_t* out_info);
```

### 5.8 Reading and Writing

```c
// Random-access read (two-call pattern for output buffer sizing)
int32_t objstore_read(objstore_store_t store, uint64_t id,
                      int64_t offset,
                      uint8_t* buf, size_t buf_len,
                      size_t* out_bytes_read,
                      objstore_txn_t txn,       // may be NULL
                      int32_t timeout_ms);

// Same-size overwrite only; returns OBJSTORE_ERR_INVALID_ARG if range doesn't fit
int32_t objstore_write_at(objstore_store_t store, uint64_t id,
                          int64_t offset,
                          const uint8_t* buf, size_t buf_len,
                          objstore_txn_t txn,
                          int32_t timeout_ms);

int32_t objstore_append(objstore_store_t store, uint64_t id,
                        const uint8_t* buf, size_t buf_len,
                        objstore_txn_t txn,
                        int32_t timeout_ms);

int32_t objstore_truncate(objstore_store_t store, uint64_t id,
                          int64_t new_length,
                          objstore_txn_t txn,
                          int32_t timeout_ms);
```

### 5.9 Streams

```c
int32_t objstore_stream_open_read(objstore_store_t store, uint64_t id,
                                  objstore_txn_t txn,
                                  objstore_stream_t* out_stream);

int32_t objstore_stream_open_write(objstore_store_t store, uint64_t id,
                                   objstore_txn_t txn,
                                   objstore_stream_t* out_stream);

int32_t objstore_stream_read(objstore_stream_t stream,
                             uint8_t* buf, size_t buf_len,
                             size_t* out_bytes_read,
                             int32_t timeout_ms);

int32_t objstore_stream_write(objstore_stream_t stream,
                              const uint8_t* buf, size_t buf_len,
                              int32_t timeout_ms);

int32_t objstore_stream_seek(objstore_stream_t stream,
                             int64_t offset, int32_t origin); // 0=Begin,1=Current,2=End

int32_t objstore_stream_get_position(objstore_stream_t stream, int64_t* out_pos);

int32_t objstore_stream_get_length(objstore_stream_t stream, int64_t* out_len);

int32_t objstore_stream_close(objstore_stream_t stream);
```

### 5.10 Metadata

```c
int32_t objstore_metadata_set(objstore_store_t store, uint64_t id,
                              const char* key, size_t key_len,
                              const uint8_t* value, size_t value_len,
                              objstore_txn_t txn);

// Two-call pattern
int32_t objstore_metadata_get(objstore_store_t store, uint64_t id,
                              const char* key, size_t key_len,
                              uint8_t* buf, size_t buf_len,
                              size_t* out_len,
                              objstore_txn_t txn);
```

### 5.11 Enumeration

```c
int32_t objstore_list_begin(objstore_store_t store,
                            objstore_txn_t txn,       // may be NULL
                            objstore_iter_t* out_iter);

int32_t objstore_iter_next(objstore_iter_t iter, objstore_object_info_t* out_info);

int32_t objstore_iter_close(objstore_iter_t iter);
```

### 5.12 String-Name Registry Management

```c
// List all registered names (two-call pattern: buf=NULL → out_len = required bytes)
// Names are returned as a sequence of length-prefixed UTF-8 strings:
//   [uint16_t len][len bytes]...
int32_t objstore_named_list(uint8_t* buf, size_t buf_len, size_t* out_len);

// Explicitly unregister a name without closing the underlying store
int32_t objstore_named_unregister(const char* name, size_t name_len);
```

---

## 6. Header File (`objstore.h`) Structure

```
objstore.h
├── Version macros (OBJSTORE_API_VERSION_MAJOR / MINOR)
├── Error code constants (OBJSTORE_OK, OBJSTORE_ERR_*, OBJSTORE_NO_MORE_ITEMS)
├── Opaque handle typedefs
├── objstore_object_info_t struct
├── objstore_stats_t struct
├── All function declarations (extern "C" guarded)
└── Convenience macros (OBJSTORE_SUCCEEDED(rc), OBJSTORE_FAILED(rc))
```

---

## 7. Distribution

The ObjectStore NuGet package includes:
```
runtimes/
  win-x64/native/objstore.dll
  linux-x64/native/libobjstore.so
  linux-arm64/native/libobjstore.so
  osx-x64/native/libobjstore.dylib
  osx-arm64/native/libobjstore.dylib
contentFiles/
  include/objstore.h
```

A .NET caller using P/Invoke loads the library via `[DllImport("objstore")]`. An FFI caller in another language loads the platform-native library by its platform path.

---

## 8. Example Usage (C)

```c
#include "objstore.h"
#include <stdio.h>
#include <assert.h>

int main(void) {
    // Check version
    int32_t major, minor;
    objstore_get_version(&major, &minor);
    assert(major == OBJSTORE_API_VERSION_MAJOR);

    // Create options
    objstore_options_t opts;
    objstore_options_create(&opts);
    objstore_options_set_cache_max_bytes(opts, 16 * 1024 * 1024);

    // Open store
    objstore_store_t store;
    int32_t rc = objstore_open_or_create("mystore.obs", 14, opts, &store);
    objstore_options_free(opts);
    if (OBJSTORE_FAILED(rc)) { /* handle error */ return 1; }

    // Create an object
    uint64_t id;
    rc = objstore_object_create(store, "hello", 5, 0 /* no compression */, NULL, &id);

    // Write data
    const char* data = "Hello, World!";
    rc = objstore_append(store, id, (const uint8_t*)data, 13, NULL, -1);

    // Read it back (two-call)
    size_t needed;
    objstore_read(store, id, 0, NULL, 0, &needed, NULL, -1);
    uint8_t* buf = malloc(needed);
    size_t got;
    objstore_read(store, id, 0, buf, needed, &got, NULL, -1);

    objstore_close(store, -1);
    free(buf);
    return 0;
}
```

---

## 9. String-Name Tier Example (Python-style pseudocode)

```python
# lib = ctypes.CDLL("libobjstore.so")
lib.objstore_named_open_or_create(b"main", 4, b"mystore.obs", 11, None)

lib.objstore_named_txn_begin(b"main", 4, b"txn1", 4)

id = ctypes.c_uint64()
lib.objstore_object_create(store_from_name(b"main"), None, 0, 0, txn_from_name(b"txn1"), ctypes.byref(id))
lib.objstore_append(store_from_name(b"main"), id, data, len(data), txn_from_name(b"txn1"), -1)

lib.objstore_named_txn_commit(b"txn1", 4, -1)
lib.objstore_named_close(b"main", 4, -1)
```

---

## 10. Future Work

- WASM/WASI target (`wasm32-wasi`): NativeAOT WASM support when .NET stabilises it.
- Auto-generated bindings: a build step that runs `bindgen` / `cffi` / etc. from `objstore.h`.
- Async C API tier: completion-port / `io_uring` style for event-loop runtimes.
