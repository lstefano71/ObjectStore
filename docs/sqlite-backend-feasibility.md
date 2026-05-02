# Feasibility Report: SQLite-Backed ObjectStore

## Executive Summary

This report analyzes the feasibility of reimplementing ObjectStore's complete feature set using SQLite as the storage backend. The conclusion is that SQLite can implement **all** of ObjectStore's features with significantly less code, better small-record performance, and mature tooling — at the cost of large-blob read performance and some loss of control over crash-recovery semantics.

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

**Note:** SQLite's Blob I/O API (`sqlite3_blob_open/read/write/close`) provides random-access byte-level I/O without reading the entire blob — ideal for large objects.

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

### 1.8 Multi-Process Concurrency

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| File-level write lock | SQLite WAL handles this natively | None (built-in) |
| Reader doesn't block writer | WAL mode: readers see snapshot, writer doesn't block | Built-in |
| Lock timeout | `sqlite3_busy_timeout(ms)` | Trivial |
| Refresh (see latest committed) | Close and reopen connection, or use `PRAGMA wal_checkpoint` | Low |

SQLite's WAL mode provides MVCC semantics superior to ObjectStore's manual file locking.

### 1.9 Crash Recovery

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| Dual superblock (atomic commit) | SQLite WAL (atomic commit built-in) | None |
| Recovery from dirty close | `PRAGMA integrity_check` + automatic WAL replay | Built-in |
| Allocator rebuild | Not needed (SQLite manages space internally) | N/A |

SQLite's crash recovery is battle-tested across billions of deployments. It's arguably more robust than a custom implementation.

### 1.10 Maintenance

| ObjectStore Feature | SQLite Implementation | Complexity |
|--------------------|-----------------------|:----------:|
| Defragmentation | `VACUUM` | Built-in |
| Statistics | `SELECT count(*), sum(length(data))` | Trivial |
| File size | OS stat or `PRAGMA page_count * PRAGMA page_size` | Trivial |

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
| Large blob batch writes | 1.04× faster (when I/O dominates) |
| Single-file simplicity | One file, no WAL/SHM auxiliaries |
| Snapshot isolation control | Explicit refresh vs SQLite's "begin reads a snapshot" |

### Where They're Equal

- Crash safety guarantees (both offer atomic commits)
- Large blob batch writes on HDD (I/O bound dominates both)
- Multi-process read throughput under contention

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

**Mitigation:** Accept WAL mode (3 files) or use DELETE mode with reduced concurrency.

### 5.3 Custom Block-Level Encryption
ObjectStore encrypts individual blocks with AES-256-GCM. SQLite encryption (SQLCipher) encrypts at the page level — you can't have per-object encryption keys.

**Mitigation:** Application-layer encryption per object (encrypt blob before storage). This is actually how most real systems work.

### 5.4 Explicit MVCC Refresh Control
ObjectStore's `Refresh()` lets readers choose when to see new commits. SQLite snapshots are bound to transaction start.

**Mitigation:** Use `BEGIN`/`COMMIT` to control when the snapshot advances, or `PRAGMA wal_checkpoint` to force visibility.

### 5.5 Predictable Allocation Patterns
The buddy allocator gives deterministic block placement. SQLite's internal allocator is opaque.

**Mitigation:** Not typically needed for application-level concerns.

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
| Minimum code complexity | → Use pure SQLite backend |
| Best crash safety confidence | → Use pure SQLite backend |
| Single-file deployment required | → Keep current ObjectStore |
| Need per-object encryption | → Application-layer encryption with either |
| Best overall balance | → **Hybrid approach** |

---

## 9. Conclusion

Reimplementing ObjectStore's full feature set with SQLite as a backend is **fully feasible** and would require approximately 500-800 lines of C# wrapper code (vs ~3,500 lines of custom engine code). The result would be:

- **Faster** for small records and transaction batches (3-8×)
- **Slower** for large blob sequential reads (2.5×)
- **More reliable** (leveraging SQLite's exhaustive testing)
- **More maintainable** (no custom B-tree, allocator, or crash recovery)
- **More capable** (SQL queries, FTS, JSON, backup API for free)

The current ObjectStore's main advantage is contiguous block storage for large blobs. If large-blob read performance is critical, either keep the current engine or adopt the hybrid approach outlined in Section 7.

---

*Report generated: 2026-05-02*  
*Based on benchmark data from Sections 11-13 of docs/benchmark-report.md*
