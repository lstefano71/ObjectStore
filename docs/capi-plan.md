# ObjectStore C API — Implementation Plan

## Problem Statement

Produce a NativeAOT-compiled native shared library that exports the ObjectStore functionality through a stable C ABI. The C API is a projection layer on top of the existing C# ObjectStore library — it adds no storage logic of its own.

## Approach

The C API layer is built as a separate C# project (`ObjectStore.Native`) that references the core `ObjectStore` library. It has no logic of its own beyond: pinning managed objects as opaque handles, bridging the async C# API to synchronous blocking calls, serialising/deserialising options, and maintaining the string-name registry.

> **Prerequisite**: the core `ObjectStore` library must already be NativeAOT-compatible (no unconstrained reflection, no `dynamic`, trimming-safe generics). This is enforced as NFR-11 in the core PRD (`prd.md`). Without it, the C API cannot be compiled. Run `dotnet publish -p:PublishAot=true` as a CI gate on the core library from Phase 1 onward.

Implement in 4 phases after the core C# library is complete (or at least Phase 7 of the main plan).

---

## Phase C1 — NativeAOT Project Setup & Plumbing

**Goal**: A compilable `ObjectStore.Native` project that produces a native shared library with a single exported function. All boilerplate and infrastructure in place.

### Todos

- `capi-project` — Create `ObjectStore.Native` C# project: target `net10.0`, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, `<PublishAot>true</PublishAot>`, `<NativeLib>Shared</NativeLib>`. Reference `ObjectStore` core project. Add publish profiles for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.
- `capi-version-export` — Implement and export `objstore_get_version(int32_t* out_major, int32_t* out_minor)` as the first `[UnmanagedCallersOnly]` function. Verify the library builds and the export is visible with `nm` / `dumpbin`.
- `capi-handle-infra` — Implement `GCHandle`-backed opaque handle system: `HandleStore<T>` — allocates a `GCHandle.Alloc(obj, GCHandleType.Normal)`, returns the address as an `IntPtr` cast to the opaque pointer type. `HandleStore<T>.Free(ptr)` frees the handle. One `HandleStore<T>` per handle type (`ObjectStore`, `ITransaction`, `Stream`, `IAsyncEnumerator`, `ObjectStoreOptions`).
- `capi-error-codes` — Define all `OBJSTORE_*` error code constants as a C# `enum NativeError : int`. Implement `NativeErrorHelper.Capture(Exception ex)`: maps exception types to error codes (see §4.3.2 of `capi-prd.md`) and stores `ex.Message` + optional `ex.ToString()` in a `[ThreadStatic]` slot. Implement `objstore_error_message` (static code→string, two-call) and `objstore_get_last_error_detail` (thread-local dynamic message, two-call). Clear the thread-local slot at the start of every successful export call.
- `capi-async-bridge` — Implement `SyncBridge` utility: `T RunSync<T>(ValueTask<T> task, int timeoutMs)` — blocks the current (native) thread by calling `task.AsTask().Wait(timeout)`. On `TimeoutException` returns `OBJSTORE_ERR_TIMEOUT`. On any exception, maps to the appropriate `NativeError`.
- `capi-string-helpers` — Implement `NativeString` helpers: `ReadUtf8(char* ptr, nuint len)` → `string`; `WriteUtf8(string s, char* buf, nuint bufLen, nuint* outLen)` implementing the two-call pattern (returns `OBJSTORE_ERR_BUFFER_TOO_SMALL` with `*outLen` = required bytes when `buf == null` or `bufLen` is too small).
- `capi-options-handle` — Implement `NativeOptions` (the managed backing type for `objstore_options_t`): a mutable class holding all `ObjectStoreOptions` fields. Export `objstore_options_create`, all `objstore_options_set_*` functions, and `objstore_options_free`. `objstore_options_free` calls `HandleStore<NativeOptions>.Free`.

### Dependencies
Core C# library Phase 7 complete.

---

## Phase C2 — Store Lifecycle & Transactions

**Goal**: Full store open/create/close, transactions, and the string-name registry.

### Todos

- `capi-store-open` — Export `objstore_create`, `objstore_open`, `objstore_open_or_create`: unpack path and options from native params, call the corresponding C# factory (`await`-bridged via `SyncBridge`), wrap the resulting `ObjectStore` in a `HandleStore<ObjectStore>` handle, write to `*out_store`.
- `capi-store-close` — Export `objstore_close`: retrieve `ObjectStore` from handle, call `await store.DisposeAsync()` via `SyncBridge(timeout_ms)`, free the handle.
- `capi-store-maintenance` — Export `objstore_recover` and `objstore_defragment` (both blocking+timeout). Export `objstore_get_stats`: populate `objstore_stats_t` from `ObjectStoreStats`.
- `capi-txn` — Export `objstore_txn_begin`, `objstore_txn_commit`, `objstore_txn_rollback`, `objstore_txn_close`: bridge `BeginTransaction` / `CommitAsync` / `RollbackAsync`. Wrap `ITransaction` in `HandleStore<ITransaction>`. `objstore_txn_close` rolls back if not committed, then frees handle.
- `capi-name-registry` — Implement `NameRegistry`: a `ConcurrentDictionary<string, (objstore_store_t storeHandle, objstore_txn_t? txnHandle)>` plus methods `Register(name, handle)`, `Unregister(name)`, `GetStore(name)`, `GetTxn(name)`. Thread-safe; held as a process-global singleton.
- `capi-named-store` — Export all `objstore_named_*` store/txn functions: delegate to `capi-store-open` / `capi-txn` variants and register results in `NameRegistry`. Export `objstore_named_list` and `objstore_named_unregister`.

### Dependencies
Phase C1 complete.

---

## Phase C3 — Objects, IO, Streams, Enumeration

**Goal**: Full object CRUD, read/write/append/truncate, stream API, metadata, and iterator.

### Todos

- `capi-object-crud` — Export `objstore_object_create`, `_delete`, `_delete_by_name`, `_rename`, `_exists`, `_exists_by_name`, `_resolve_name`, `_get_info`. Map `ObjectInfo` → `objstore_object_info_t` (fixed-size struct; name truncated at 255 bytes, `has_name` flag set accordingly).
- `capi-read-write` — Export `objstore_read` (two-call pattern), `objstore_write_at`, `objstore_append`, `objstore_truncate`. Each bridges the corresponding `ObjectStore` async method via `SyncBridge`.
- `capi-streams` — Export `objstore_stream_open_read`, `objstore_stream_open_write`, `objstore_stream_read`, `objstore_stream_write`, `objstore_stream_seek`, `objstore_stream_get_position`, `objstore_stream_get_length`, `objstore_stream_close`. Wrap `Stream` in `HandleStore<Stream>`.
- `capi-metadata` — Export `objstore_metadata_set` and `objstore_metadata_get` (two-call pattern for `get`).
- `capi-iterator` — Export `objstore_list_begin`, `objstore_iter_next`, `objstore_iter_close`. The iterator handle wraps a synchronous `IEnumerator<ObjectInfo>` (use `ListObjects()` not the async variant). `objstore_iter_next` returns `OBJSTORE_NO_MORE_ITEMS` when exhausted.

### Dependencies
Phase C2 complete.

---

## Phase C4 — Header File, NuGet Packaging, Tests

**Goal**: A shippable, versioned `objstore.h`, a NuGet package containing all RID artifacts, and a test suite that exercises the C API from C and from a scripting language.

### Todos

- `capi-header` — Write `objstore.h` by hand (not generated): version macros, all error code `#define`s, opaque typedef declarations, `objstore_object_info_t` and `objstore_stats_t` struct definitions, all function declarations in `extern "C"` block, `OBJSTORE_SUCCEEDED(rc)` / `OBJSTORE_FAILED(rc)` convenience macros. Include a `#pragma once` guard. Validate that all exported symbols match the header using a CI step (`nm -D` / `dumpbin /EXPORTS`).
- `capi-nuget` — Author `ObjectStore.Native.nuspec` / `ObjectStore.Native.csproj` packaging targets: copy publish outputs for each RID into `runtimes/{rid}/native/`; copy `objstore.h` into `contentFiles/include/`. Produce `.nupkg` from CI. Version the NuGet package as `major.minor.patch` where `major.minor` tracks `OBJSTORE_API_VERSION_MAJOR.MINOR`.
- `capi-c-tests` — Write a C99 test program (`tests/c_api_test.c`) using `objstore.h` that exercises: open/create, object CRUD, read/write/append/truncate, transaction commit and rollback, iterator, metadata, stats, close. Build with `gcc`/`clang` + link against the native library. Run as part of CI.
- `capi-python-tests` — Write a Python 3 test (`tests/test_objstore.py`) using `ctypes` that exercises the full API including the string-name tier. Verifies all error codes, two-call buffer sizing, and iterator exhaustion. Run as part of CI via `pytest`.
- `capi-abi-check` — Add a CI step using `abi-compliance-checker` (Linux) or similar to compare the exported ABI between the last released version and the current build. Fails the build if any breaking changes are detected on a minor-version bump.

### Dependencies
Phase C3 complete.

---

## Key Invariants

1. **No managed memory crosses the FFI boundary.** All data is copied to/from caller-supplied buffers; no GC-managed pointers are exposed.
2. **Every `[UnmanagedCallersOnly]` function is wrapped in a top-level `try/catch(Exception)`** that calls `NativeErrorHelper.Capture(ex)`. This maps the exception to an error code, stores `ex.Message` in the thread-local last-error slot, and returns the code. An unhandled exception in NativeAOT causes immediate process termination — there must be no escape path. On a successful call the thread-local slot is cleared.
3. **Handles are freed exactly once.** `HandleStore<T>.Free` asserts the handle is valid before freeing. Double-free is caught in debug builds.
4. **The string-name registry holds strong references.** A named store is kept alive until `objstore_named_close` or `objstore_named_unregister` is called.
5. **`SyncBridge` never blocks the .NET thread pool.** It runs the async work on a `Task.Run` thread pool item and blocks the *native caller's* thread, not a managed thread pool thread.

---

## Risk Register

| Risk | Mitigation |
|---|---|
| NativeAOT trims types used reflectively inside ObjectStore | Annotate with `[DynamicallyAccessedMembers]`; add `rd.xml` rooted types; run trimming analysis in CI |
| `GCHandle` leaks if caller forgets to close handles | Add a finalizer-based leak detector in debug builds; document that all handles must be closed |
| `SyncBridge` deadlock if managed code awaits a UI thread | ObjectStore has no SynchronizationContext dependency — document that library is context-free |
| ABI break between minor versions | `capi-abi-check` CI step catches this before release |
| Platform-specific alignment differences in `objstore_object_info_t` | Use `[StructLayout(LayoutKind.Sequential, Pack=8)]` in C# and `#pragma pack(8)` in the header; verify with static_assert in C test |
