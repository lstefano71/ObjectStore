# Feasibility Report: SQLite-Backed ObjectStore

## Executive Summary

This report analyzes the feasibility of reimplementing ObjectStore's complete feature set using SQLite as the storage backend. SQLite can implement **most** of ObjectStore's features with significantly less code, better small-record performance, and mature tooling — at the cost of large-blob read/append performance, per-block data integrity verification, true single-file deployment, and an additional native dependency. Several PRD requirements (per-block checksums, no external dependencies, efficient large-object append) present fundamental architectural mismatches that application-layer workarounds cannot fully resolve.

---

## 1. Feature Mapping

### 1.1 Core Object Operations

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| CreateObject(name) → ID | `INSERT INTO objects(name) VALUES(?)` returning ROWID | Trivial |
| DeleteObject(id) | `DELETE FROM objects WHERE id = ?` | Trivial |
| Exists(id) | `SELECT 1 FROM objects WHERE id = ?` | Trivial |
| GetInfo(id) | `SELECT * FROM objects WHERE id = ?` | Trivial |
| ListObjects() | `SELECT * FROM objects` | Trivial |

### 1.2 Data I/O

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| Append(id, data) | `UPDATE objects SET data = data \|\| ? WHERE id = ?` | Low |
| ReadAt(id, offset, buf) | `substr(data, offset+1, length)` or Blob I/O API | Low |
| WriteAt(id, offset, data) | Blob I/O API (`sqlite3_blob_write`) | Low |
| Truncate(id, newLen) | `UPDATE SET data = substr(data, 1, newLen)` | Low |

**Note:** SQLite's Blob I/O API (`sqlite3_blob_open/read/write/close`) provides random-access byte-level I/O without reading the entire blob — ideal for large object *reads* and in-place *writes that don't change size*.

**⚠️ Critical limitation:** The Blob I/O API **cannot resize** blobs. `sqlite3_blob_write` fails if writing past the end. This means:
- **Append** requires `data || ?` which reads the entire existing blob, concatenates, and rewrites it. For a 100MB object + 256B append, SQLite must read, copy, and rewrite all 100MB.
- **Truncate** requires `substr(data, 1, newLen)` which similarly rewrites the blob.
- ObjectStore, by contrast, only allocates a new extent block for append — the existing data is never copied.

This makes SQLite fundamentally unsuitable for append-heavy workloads on large objects (a core use case in the PRD).

### 1.3 Transactions & Savepoints

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| BeginTransaction | `BEGIN IMMEDIATE` | Trivial |
| Commit | `COMMIT` | Trivial |
| Rollback | `ROLLBACK` | Trivial |
| Nested savepoints | `SAVEPOINT sp_N` / `RELEASE` / `ROLLBACK TO` | Trivial |

SQLite natively supports nested savepoints — no custom implementation needed.

### 1.4 Node Hierarchy

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| Parent-child tree | `parent_id` column + recursive CTE queries | Low |
| Move node | `UPDATE SET parent_id = ?, name = ?` | Low |
| Delete subtree | Recursive CTE delete or `ON DELETE CASCADE` | Low |
| Path resolution | Recursive CTE: `WITH RECURSIVE path AS (...)` | Medium |
| List children | `SELECT * FROM objects WHERE parent_id = ?` | Trivial |

### 1.5 Per-Object Metadata

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| Set key-value metadata | Separate `metadata(object_id, key, value)` table | Low |
| Get/Delete metadata | Standard SQL queries | Trivial |

### 1.6 Compression

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| Per-object Deflate/Brotli | Compress in application layer before INSERT/UPDATE | Low |
| Transparent decompression | Decompress in application layer after SELECT | Low |

SQLite doesn't have built-in compression, but implementing it at the application layer is straightforward. Store a `compression_codec` column and compress/decompress in the API wrapper.

### 1.7 Encryption

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| AES-256-GCM per block | SQLite Encryption Extension (SEE) or SQLCipher | Medium |
| Key management | Provided by SEE/SQLCipher | Medium |

Options:
- **SQLCipher** (open source): Full-database AES-256 encryption, widely used
- **SEE** (commercial SQLite extension): Official, per-license
- **Application-layer**: Encrypt/decrypt blobs in the wrapper (like compression)

Per-block encryption is harder; SQLite encrypts at the page level. Application-layer encryption gives per-object granularity.

**⚠️ Critical interaction:** Application-layer encryption is **mutually exclusive** with the Blob I/O API. Once a blob is encrypted as a whole (e.g., AES-256-GCM with per-object nonce), `sqlite3_blob_read` at an arbitrary offset returns ciphertext that cannot be independently decrypted. Every partial read becomes a full-blob read + full decrypt. ObjectStore's per-block encryption model allows reading and decrypting individual blocks independently — a 10-byte read from a 100MB encrypted object touches only one block.

### 1.8 Multi-Process Concurrency

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| File-level write lock | SQLite WAL handles this natively | None (built-in) |
| Reader doesn't block writer | WAL mode: readers see snapshot, writer doesn't block | Built-in |
| Lock timeout | `sqlite3_busy_timeout(ms)` | Trivial |
| Refresh (see latest committed) | End and restart the read transaction | Low |

SQLite's WAL mode provides MVCC semantics similar to ObjectStore's manual file locking.

**Note on snapshot control:** ObjectStore's `Refresh()` advances a reader's visible snapshot without releasing resources. In SQLite, a reader's snapshot is bound to the start of its transaction. To see newly committed data, the reader must `COMMIT` (or `ROLLBACK`) its current transaction and `BEGIN` a new one. `PRAGMA wal_checkpoint` is a **maintenance** operation (copies WAL pages back to the main database) — it does **not** advance any reader's snapshot visibility.

### 1.9 Crash Recovery

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| Dual superblock (atomic commit) | SQLite WAL (atomic commit built-in) | None |
| Recovery from dirty close | `PRAGMA integrity_check` + automatic WAL replay | Built-in |
| Allocator rebuild | Not needed (SQLite manages space internally) | N/A |
| Per-block xxHash3 checksum | **No equivalent** — see §5.6 | N/A |

SQLite's crash recovery is battle-tested across billions of deployments. However, SQLite does **not** provide per-page data integrity checksums by default (see Section 5.6 for implications).

### 1.10 Maintenance

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| Defragmentation | `VACUUM` (with caveats — see below) | Built-in |
| Statistics | `SELECT count(*), sum(length(data))` | Trivial |
| File size | OS stat or `PRAGMA page_count * PRAGMA page_size` | Trivial |

**⚠️ VACUUM limitations:** SQLite's `VACUUM` requires **2× the database file size** in temporary disk space (it rebuilds the entire database into a new file). It also requires an **exclusive lock** for its entire duration — no concurrent readers or writers. For a 10GB database, this means 10GB of free space and potentially minutes of downtime.

ObjectStore's `Defragmenter` works incrementally within normal transactions (object-by-object), requires no extra disk space, and allows concurrent readers. `VACUUM INTO` can write to a new file without an exclusive lock, but produces a separate file rather than reclaiming space in-place.

`auto_vacuum` mode avoids full VACUUM but only reclaims pages freed by DELETE — it does not defragment or compact existing data.

### 1.11 C ABI / NativeAOT

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| C header + DLL | Thin C wrapper around sqlite3 API | Medium |
| Cross-language FFI | SQLite already has C API everywhere | Trivial |

The C ABI could either:
- Expose sqlite3 directly (consumers use SQL) — zero wrapper code
- Provide the same `objstore_*` API wrapping SQLite calls — moderate code

---

## 2. Schema Design

```sql
CREATE TABLE objects (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_id INTEGER REFERENCES objects(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    node_type INTEGER DEFAULT 1,  -- 1=data, 2=container, 3=both
    data BLOB DEFAULT X'',
    compression_codec INTEGER DEFAULT 0,
    created_at INTEGER NOT NULL,  -- Unix ms
    modified_at INTEGER NOT NULL,
    UNIQUE(parent_id, name)
);

CREATE TABLE metadata (
    object_id INTEGER NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
    key TEXT NOT NULL,
    value TEXT,
    PRIMARY KEY(object_id, key)
);

CREATE INDEX idx_objects_parent ON objects(parent_id);

-- Root node (id=1, parent_id=NULL)
INSERT INTO objects(id, parent_id, name, node_type, data, compression_codec, created_at, modified_at)
VALUES (1, NULL, '/', 2, X'', 0, 0, 0);
```

---

## 3. Performance Analysis

### What SQLite Does Better

| Aspect | Advantage |
|--------|-----------|
| Small record writes | 2.7× faster (no COW B-tree + allocator overhead) |
| Transaction batches (small) | 7.8× faster (WAL append-only vs scattered COW blocks) |
| Small reads | 2.7× faster (optimized page cache vs SIEVE + block header parsing) |
| Multi-process concurrency | Built-in WAL MVCC (no manual file locking) |
| Crash recovery | Automatic WAL replay (no manual allocator rebuild) |
| Tooling | CLI, EXPLAIN, .dump, backup API, dozens of GUI tools |

### What ObjectStore Does Better

| Aspect | Advantage |
|--------|-----------|
| Large blob reads (64KB+) | 2.5× faster (contiguous blocks vs overflow page reassembly) |
| Large blob append | O(1) new block vs O(n) full rewrite in SQLite |
| Large blob batch writes | 1.04× faster (when I/O dominates) |
| Single-file simplicity | One file, no WAL/SHM auxiliaries (no auxiliary file loss risk) |
| Per-block checksums | xxHash3 on every block detects bit-rot; SQLite has none by default |
| Per-block encryption | Read/decrypt individual blocks; SQLite app-layer encryption requires full-blob decrypt |
| Snapshot isolation control | Explicit refresh vs SQLite's "begin reads a snapshot" |
| Incremental defragmentation | Object-by-object, no 2× disk space or exclusive lock |
| Zero external dependencies | Pure .NET BCL (NFR-10 compliant) |

### Where They're Equal

- Large blob batch writes on HDD (I/O bound dominates both)
- Multi-process read throughput under contention
- Transaction semantics (ACID guarantees)

### Key Architectural Mismatches

These are **fundamental** differences that cannot be bridged by application-layer workarounds:

| Feature | ObjectStore | SQLite | Workaround? |
|---------|-------------|--------|:-----------:|
| Append to large blob | O(1) — new extent block | O(n) — full blob rewrite | ❌ None |
| Per-block checksum on read | Built-in (xxHash3) | Not available by default | ⚠️ Partial (app-layer) |
| Encrypted partial read | Decrypt one block | Decrypt entire blob | ❌ None |
| Single-file atomicity | Dual superblock | WAL file loss = data loss | ⚠️ DELETE mode (loses concurrency) |
| No native dependency | Pure .NET | Requires sqlite3 native lib | ❌ None |

---

## 4. Implementation Complexity Comparison

| Metric | ObjectStore (current) | SQLite-backed |
|--------|----------------------:|:--------------:|
| Core engine LOC | ~3,500 | ~500-800 |
| Total C# LOC (incl. tests) | ~8,000 | ~2,000 |
| Data structures implemented | COW B-tree, Buddy allocator, SIEVE cache, ExtentList, Superblock | None (SQLite provides all) |
| Crash recovery code | ~150 lines (manual) | 0 (built-in) |
| Concurrency code | ~100 lines (file locking) | 0 (built-in WAL) |
| Defragmentation | ~80 lines | 0 (VACUUM) |
| NativeAOT C ABI | Required for DLL | Could just use sqlite3 directly |

**Estimated development effort:** 2-3 days for a complete SQLite-backed implementation with the same API surface, vs the ~2 weeks invested in the custom engine.

---

## 5. What Would Be Lost

### 5.1 Large Blob Performance
SQLite stores blobs across 4KB overflow pages. For a 64KB object, that's 16 page reads requiring B-tree traversal for each. ObjectStore reads one contiguous 64KB block in a single `pread`. This is a fundamental architectural difference that can't be worked around in SQLite.

**Mitigation:** Use SQLite's Blob I/O API for streaming reads. For objects >1MB, consider storing data in external files with paths in the database (common pattern).

### 5.2 True Single-File Deployment
SQLite in WAL mode creates `-wal` and `-shm` auxiliary files. In DELETE journal mode (single file), concurrent readers block the writer.

**⚠️ Data loss risk:** The WAL file contains **committed-but-uncheckpointed transactions**. If the WAL file is lost (manual deletion, partial backup that captures `.db` but not `-wal`, filesystem snapshot of only the main file), those committed transactions are permanently lost. This is not a theoretical risk — it is a documented source of SQLite data loss in production deployments where backup tools or file management don't account for the auxiliary files.

ObjectStore's dual-superblock model stores all committed data in a single file with atomic pointer-swap semantics. There are no auxiliary files whose loss could cause committed data to disappear.

**Mitigation:** Use DELETE journal mode (accepts reduced concurrency), or enforce strict operational discipline around WAL file handling in backups and file management.

### 5.3 Custom Block-Level Encryption
ObjectStore encrypts individual blocks with AES-256-GCM. SQLite encryption (SQLCipher) encrypts at the page level — you can't have per-object encryption keys.

**Mitigation:** Application-layer encryption per object (encrypt blob before storage).

**⚠️ However:** Application-layer encryption is mutually exclusive with the Blob I/O API (see §1.7). If encryption is enabled, every read — regardless of requested offset or length — must read and decrypt the entire blob. For a 100MB encrypted object where the caller wants 256 bytes at offset 50MB, SQLite must read all 100MB and decrypt it. ObjectStore reads and decrypts a single block.

### 5.4 Explicit MVCC Refresh Control
ObjectStore's `Refresh()` lets readers choose when to see new commits without releasing any held resources. SQLite snapshots are bound to transaction start.

**Mitigation:** End the current read transaction and begin a new one. This is functionally equivalent but has different semantics — any cursors, blob handles, or prepared statements from the previous transaction are invalidated.

### 5.5 Predictable Allocation Patterns
The buddy allocator gives deterministic block placement. SQLite's internal allocator is opaque.

**Mitigation:** Not typically needed for application-level concerns.

### 5.6 Per-Block Data Integrity Checksums
ObjectStore computes an xxHash3 checksum for every block on write and verifies it on every read (PRD NFR-02). This detects:
- Silent bit-rot (storage media degradation)
- Partial/torn writes that passed OS-level fsync
- File corruption from external processes or copy errors

SQLite has **no per-page checksums** by default. `PRAGMA integrity_check` validates structural consistency (B-tree pointers, freelist) but does not detect corrupted page *content*. If a page's data bytes flip due to bit-rot, SQLite will happily return the corrupted data with no error.

The `SQLITE_DBCONFIG_ENABLE_CHECKSUM` extension (available since 3.34.0) adds per-page checksums but is:
- Not enabled by default
- Incompatible with some SQLite tools that don't understand the checksum format
- A compile-time option (not universally available in prebuilt binaries)

**Impact:** For safety-critical stores where data integrity must be verified on every access, this is a significant gap. Application-layer checksums could be added (store hash alongside blob), but this adds complexity and doesn't protect against corruption of the hash itself.

### 5.7 External Dependency (NFR-10 Violation)
The PRD Non-Functional Requirement NFR-10 specifies: *"No external dependencies beyond .NET BCL."*

SQLite introduces a **native unmanaged dependency** (sqlite3.dll / libsqlite3.so). This:
- Complicates NativeAOT single-binary deployment (must ship the native library alongside)
- Introduces platform-specific build matrix concerns (x64, ARM64, Linux, macOS, Windows)
- Requires tracking SQLite upstream security patches independently
- May conflict with other SQLite instances loaded in the same process (version skew)

Using `Microsoft.Data.Sqlite` (the .NET wrapper) partially mitigates distribution but still bundles native `e_sqlite3.dll` — it is not a pure managed dependency.

**Impact:** This is a hard violation of NFR-10 that cannot be worked around without changing the requirement.

---

## 6. What Would Be Gained

### 6.1 Proven Reliability
SQLite is used in billions of devices, tested with millions of test cases (100% branch coverage). ObjectStore is newly written with ~320 tests.

### 6.2 Rich Query Capabilities
With SQL, you can add full-text search, JSON queries, window functions, CTEs — without modifying the engine.

### 6.3 Ecosystem & Tooling
SQLite has bindings in every language, GUI browsers, backup tools, replication extensions (Litestream, rqlite, LiteFS), and extensive documentation.

### 6.4 Reduced Maintenance Burden
No need to maintain a custom B-tree, allocator, cache, compression pipeline, or crash recovery system. SQLite handles all of this with upstream updates.

### 6.5 Better Small-Record Performance
For typical workloads (metadata, configs, small documents), SQLite is 3-8× faster.

---

## 7. Hybrid Approach (Recommended)

The benchmarks reveal a clear performance crossover point:
- **Objects ≤ 4KB:** SQLite is significantly faster (3-8×)
- **Objects ≥ 64KB:** ObjectStore is significantly faster (2.5×)

A **hybrid architecture** could combine both:
```
┌─────────────────────────────────────────┐
│          SQLite (metadata + small blobs) │
│  objects table: id, name, parent_id,    │
│    metadata, size, storage_type         │
│  inline_data: for objects ≤ threshold   │
├─────────────────────────────────────────┤
│        External blob file (large data)  │
│  Simple append-only with extent map     │
│  Contiguous reads for 64KB+ objects     │
└─────────────────────────────────────────┘
```

This gives:
- SQLite's transaction/query/concurrency for metadata and small objects
- Contiguous I/O for large blob reads
- Single logical store with two physical files

---

## 8. Decision Matrix

| Priority | Recommendation |
|----------|---------------|
| Maximum small-record throughput | → Use pure SQLite backend |
| Maximum large-blob throughput | → Keep current ObjectStore |
| Append-heavy workload on large objects | → Keep current ObjectStore (SQLite is O(n) per append) |
| Minimum code complexity | → Use pure SQLite backend |
| Per-block data integrity verification | → Keep current ObjectStore |
| Single-file deployment required | → Keep current ObjectStore |
| Need per-object encryption + partial reads | → Keep current ObjectStore |
| No external dependencies (NFR-10) | → Keep current ObjectStore |
| Best overall balance | → **Hybrid approach** (but adds complexity and a dependency) |

---

## 9. Conclusion

Reimplementing ObjectStore's feature set with SQLite as a backend is **partially feasible** with significant caveats. The wrapper would require approximately 500-800 lines of C# code (vs ~3,500 lines of custom engine), but several PRD requirements create fundamental architectural mismatches:

**What SQLite does well:**
- **Faster** for small records and transaction batches (3-8×)
- **Less code** to maintain (no custom B-tree, allocator, or crash recovery)
- **Rich ecosystem** (SQL queries, FTS, JSON, backup API, extensive tooling)

**What SQLite cannot provide:**
- **Per-block data integrity checksums** (PRD NFR-02) — no detection of silent corruption
- **Efficient append to large objects** — O(n) full rewrite vs O(1) new block
- **Per-block encryption with partial reads** — app-layer encryption requires full-blob decrypt
- **Zero external dependencies** (PRD NFR-10) — sqlite3 is a native dependency
- **Single-file atomicity** — WAL file loss causes committed data loss

**Bottom line:** For workloads dominated by small records with simple CRUD operations, SQLite is clearly superior in both performance and simplicity. However, for the PRD's specified requirements — particularly large-object append, per-block integrity checksums, per-block encryption, and zero external dependencies — ObjectStore's custom architecture provides capabilities that SQLite cannot replicate at any complexity level. The hybrid approach (Section 7) could combine strengths but introduces additional complexity and still violates NFR-10.

---

*Report generated: 2026-05-02*  
*Based on benchmark data from Sections 11-13 of docs/benchmark-report.md*
