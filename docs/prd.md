# ObjectStore — Product Requirements Document

## 1. Overview

**ObjectStore** is a self-contained, single-file object storage container implemented in C# 14 targeting .NET 10+. It stores an arbitrary number of named binary objects inside one container file, supports efficient random reads and writes, and is safe under concurrent multi-threaded and (optionally) multi-process access.

> **AOT compatibility is a first-class constraint.** The entire core library must be .NET NativeAOT-compatible from the start: no unconstrained reflection, no `dynamic`, no runtime code generation, and trimming-safe use of generics throughout. This is required to support the optional C API layer (see `capi-prd.md`), which publishes the library as a native shared library via NativeAOT. Retrofitting AOT compatibility after the fact is prohibitively costly.

---

## 2. Problem Statement

Applications often need to store a collection of related binary blobs durably — think embedded databases, game asset bundles, document stores, or build artifact caches. Options today require either a full RDBMS (heavy), a directory of loose files (no atomicity, OS limits), or ad-hoc binary formats (fragile). ObjectStore fills the gap: a lightweight, crash-safe, single-file container with a clean .NET API.

---

## 3. Goals

- **Single container file** — everything (data, index, metadata) lives in one file.
- **Efficient space reuse** — deleted or replaced objects free space that is immediately reusable.
- **Fragmentation control** — buddy allocation limits fragmentation; explicit defragmentation available.
- **Crash safety** — COW + dual superblock ensures the container is never corrupted by a crash.
- **MVCC concurrency** — multiple readers and writers operate concurrently without blocking each other.
- **Ambient transactions** — callers compose operations without needing to know if a transaction is already active.
- **Optional multi-process sharing** — multiple processes can share a container file safely.
- **Optional encryption** — whole-container AES-256-GCM.
- **Optional compression** — per-object LZ4 or Zstd.

---

## 4. Non-Goals

- Not a relational database — no SQL, no query planner, no schemas.
- Not a distributed store — single machine, single file.
- Not a general-purpose filesystem — no symlinks, no POSIX access-control semantics.
- No built-in replication or remote access.
- No compression of metadata (only object data).

---

## 5. Users / Consumers

| Persona | Use Case |
|---|---|
| Library/application developer | Embed ObjectStore to store application data (configs, assets, blobs) in a portable single-file bundle |
| Tool builder | Use as a persistent build cache, snapshot store, or artifact archive |
| Game developer | Pack game assets into a single deployable file with fast random access |
| Data pipeline engineer | Store intermediate binary objects between pipeline stages |

---

## 6. Functional Requirements

### 6.1 Container Lifecycle

| Requirement | Description |
|---|---|
| FR-01 | `ObjectStore.CreateAsync(path, options)` — creates a new container file; fails if file exists |
| FR-02 | `ObjectStore.OpenAsync(path, options)` — opens an existing container; validates magic bytes and version |
| FR-03 | `ObjectStore.OpenOrCreateAsync(path, options)` — opens if exists, creates otherwise |
| FR-04 | `ObjectStore` implements `IAsyncDisposable` and `IDisposable` |
| FR-05 | On open, perform recovery if the container was not cleanly closed |

### 6.2 Object Lifecycle

| Requirement | Description |
|---|---|
| FR-06 | Create a new object by name and/or receive an assigned 64-bit ID |
| FR-07 | Delete an object by ID or name; space is returned to the free pool |
| FR-08 | Rename an object (change its string name alias) |
| FR-09 | Check existence of an object by ID or name |
| FR-10 | Get an object's `ObjectInfo` (ID, name, size, created, modified, metadata, compression codec) |

### 6.3 Reading

| Requirement | Description |
|---|---|
| FR-11 | Open an object for reading as a `Stream` via `OpenReadAsync(id)` |
| FR-12 | Read a byte range via `ReadAsync(id, offset, Memory<byte>)` |
| FR-13 | All reads within a transaction see a consistent snapshot |

### 6.4 Writing

| Requirement | Description |
|---|---|
| FR-14 | Open an object for writing as a `Stream` via `OpenWriteAsync(id)` |
| FR-15 | Append bytes to an object: `AppendAsync(id, ReadOnlyMemory<byte>)` |
| FR-16 | Truncate an object to a given length: `TruncateAsync(id, length)` |
| FR-17 | Overwrite a fixed-size section: `WriteAtAsync(id, offset, ReadOnlyMemory<byte>)` — replacement data must be the same length as the overwritten range |
| FR-18 | Full object replacement: write a new complete payload |

### 6.5 Transactions

| Requirement | Description |
|---|---|
| FR-19 | `BeginTransaction()` / `BeginTransactionAsync()` — starts or joins an ambient transaction |
| FR-20 | `Commit()` / `CommitAsync()` — commits the outermost transaction to disk; inner commits release savepoints only |
| FR-21 | `Rollback()` / `RollbackAsync()` — rolls back to the savepoint created by the matching `BeginTransaction()` at any nesting depth |
| FR-22 | All API calls outside an explicit transaction auto-commit as individual atomic operations |
| FR-23 | `ITransaction` is `IDisposable`; disposing without committing auto-rolls back |

### 6.6 Metadata

| Requirement | Description |
|---|---|
| FR-24 | Each object stores: ID (uint64), optional name (UTF-8 string), size (uint64), created timestamp (UTC), last-modified timestamp (UTC) |
| FR-25 | User-defined key-value metadata: string keys, byte-array values, stored per object |
| FR-26 | Metadata updates are transactional |

### 6.7 Enumeration

| Requirement | Description |
|---|---|
| FR-27 | `IEnumerable<ObjectInfo> ListObjects()` — synchronous enumeration of all objects |
| FR-28 | `IAsyncEnumerable<ObjectInfo> ListObjectsAsync()` — async streaming enumeration |
| FR-29 | Enumeration sees the snapshot of the calling transaction |

### 6.8 Maintenance

| Requirement | Description |
|---|---|
| FR-30 | `Defragment()` / `DefragmentAsync()` — rewrites objects to consolidate free space; reclaims unreachable COW blocks |
| FR-31 | `Recover()` / `RecoverAsync()` — explicit recovery scan (also runs automatically on open if needed) |
| FR-32 | `GetStats()` — returns container statistics: total size, used bytes, free bytes, object count, fragmentation ratio |

### 6.9 Multi-Process Access

| Requirement | Description |
|---|---|
| FR-33 | When `OpenOptions.SharedAccess = true`, multiple processes may open the same container simultaneously |
| FR-34 | Cross-process write coordination uses OS advisory file locks with configurable retry/timeout |
| FR-35 | Readers under shared access never block |

### 6.10 Read-Only Access

The `ReadOnly` and `SharedAccess` flags are orthogonal axes, yielding four open modes:


| | `SharedAccess = false` (exclusive) | `SharedAccess = true` (shared) |
|---|---|---|
| `ReadOnly = false` | Default: single R/W opener | Multi-process R/W |
| `ReadOnly = true` | Exclusive read — no other openers permitted | Multi-reader — multiple concurrent read-only openers |

| Requirement | Description |
|---|---|
| FR-36 | When `OpenOptions.ReadOnly = true`, the container file is opened with OS read-only permissions (`FileAccess.Read`); no write permissions are required or requested |
| FR-37 | Attempting any mutating operation (write, append, truncate, delete, create object, set metadata, defragment) on a read-only store throws `ReadOnlyContainerException` |
| FR-38 | A read-only open does not set the dirty-close flag and does not run or trigger recovery |
| FR-39 | `ReadOnly = true, SharedAccess = false`: exclusive read — the OS file lock prevents any other opener (read or write) while this handle is open |
| FR-40 | `ReadOnly = true, SharedAccess = true`: shared read — multiple processes may hold concurrent read-only opens; a writer attempting to open in this mode is rejected |
| FR-41 | `ObjectStore.OpenAsync` is the only factory that accepts `ReadOnly = true`; `CreateAsync` and `OpenOrCreateAsync` reject it with `ArgumentException` |

### 6.11 Node Hierarchy

ObjectStore organises all content in a single-rooted tree. Every element in the tree is a **node**. A node may carry a binary data payload, contain child nodes, or both — there is no distinction between "file" and "directory" at the type level; capability is determined by the `HasData` / `HasChildren` flags.

| Requirement | Description |
|---|---|
| FR-42 | The store contains a single implicit root node (reserved ID = 1, `parent_id = 0`, name = `/`); all absolute paths start from `/` |
| FR-43 | Unified node model: a node may simultaneously hold a binary data payload (`HasData`), child nodes (`HasChildren`), or both; neither attribute is required |
| FR-44 | All nodes — root, interior, and leaf — share the same monotonically-increasing `uint64` ID counter; ID 0 is null/invalid; ID 1 is the root |
| FR-45 | `NodeRecord` (replaces `ObjectRecord`) contains a `node_type_flags (u8)` field with bits: `HasData` (0), `HasChildren` (1), `IsDeleted` (2) |
| FR-46 | The primary B-tree index uses a composite key `(parent_id: u64, name_hash: u64)`; a range scan on a fixed `parent_id` yields all direct children efficiently |
| FR-47 | Path-based navigation: `store.GetNodeAsync("/a/b/c")` resolves by splitting on `/` and performing one B-tree lookup per segment using `FNV-1a(segment_utf8)` as `name_hash` |
| FR-48 | Step-by-step traversal: `INode.GetChildAsync(name)`, `INode.ListChildrenAsync()`, `INode.GetParentAsync()` navigate one level at a time |
| FR-49 | `ListChildrenAsync(recursive: false)` (default) returns direct children only; `recursive: true` performs a depth-first walk yielding all descendants |
| FR-50 | `INode.MoveAsync(newParentId, newName)` atomically changes a node's `parent_id` and `name_hash` in a single COW B-tree update within the current transaction; the node's `uint64` ID is stable across moves and renames |
| FR-51 | Recursive deletion: deleting a node tombstones the subtree root in the B-tree; a post-order walk at commit time enqueues all descendant blocks into the deferred-free queue; MVCC snapshots predating the delete continue to see the full subtree until their generation is reclaimed |
| FR-52 | A node's `modified` timestamp is updated when its own payload changes **or** when a direct child is added, removed, or renamed; the timestamp write is **lazily batched** — once per affected parent per transaction — at commit time (POSIX-style `mtime` semantics) |
| FR-53 | Node names within a parent are **case-sensitive**; `name_hash` is `FNV-1a` computed over raw UTF-8 bytes |
| FR-54 | `INode` exposes: `Id`, `Name`, `FullPath`, `Flags`, `HasData`, `HasChildren`, `Created`, `Modified`, metadata bag, `TryGetDataNode()` / `AsDataNode()` query methods, plus navigation and mutation methods |
| FR-55 | `IDataNode : INode` additionally exposes: `Size`, `ReadAtAsync`, `WriteAtAsync`, `AppendAsync`, `TruncateAsync`, `OpenReadStreamAsync`, `OpenWriteStreamAsync` |



## 7. Non-Functional Requirements

| ID | Requirement |
|---|---|
| NFR-01 | **Crash safety**: container is never corrupted by process termination at any point during a write |
| NFR-02 | **Data integrity**: every block carries a checksum (xxHash3 or CRC32C); read validates checksum and throws `CorruptionException` on mismatch |
| NFR-03 | **Scalability**: supports up to 16 TB container size; millions of objects |
| NFR-04 | **Memory**: SIEVE block cache with configurable max size (default 32 MB); no unbounded in-memory growth |
| NFR-05 | **Threading**: thread-safe for concurrent readers and a single writer per transaction epoch (MVCC) |
| NFR-06 | **Async**: all IO-bound public methods have `Async` overloads returning `ValueTask` or `Task` |
| NFR-07 | **Encryption**: when enabled, AES-256-GCM with a caller-supplied key; IV stored per block |
| NFR-08 | **Compression**: LZ4 or Zstd per object, flagged in the object's B-tree record; transparent on read |
| NFR-09 | **Versioning**: file format carries a major/minor version; breaking changes bump major; forward-incompatible opens throw `IncompatibleVersionException` |
| NFR-10 | **No external dependencies** beyond the .NET 10+ BCL (compression and crypto use `System.IO.Compression` and `System.Security.Cryptography`) |
| NFR-11 | **NativeAOT compatibility**: the entire library must be AOT-safe — no unconstrained reflection, no `dynamic`, no `Emit`/runtime code generation; all generic usage must be trimming-safe (`[DynamicallyAccessedMembers]` where needed). Verified by publishing with `<PublishAot>true</PublishAot>` as part of CI. |

---

## 8. API Surface (C# sketch)

```csharp
// Top-level container
public sealed class ObjectStore : IAsyncDisposable, IDisposable
{
    public static Task<ObjectStore> CreateAsync(string path, ObjectStoreOptions options = default);
    public static Task<ObjectStore> OpenAsync(string path, ObjectStoreOptions options = default);
    public static Task<ObjectStore> OpenOrCreateAsync(string path, ObjectStoreOptions options = default);

    // Transactions
    public ITransaction BeginTransaction();
    public ValueTask<ITransaction> BeginTransactionAsync();

    // Hierarchy — primary node API
    public ValueTask<INode> GetRootAsync(CancellationToken ct = default);
    public ValueTask<INode?> GetNodeAsync(string path, CancellationToken ct = default);
    public ValueTask<INode?> GetNodeAsync(ulong id, CancellationToken ct = default);
    public ValueTask<INode> CreateNodeAsync(string path, NodeCreateOptions options = default, CancellationToken ct = default);

    // Flat convenience API (operates on root's direct children; kept for simple use-cases)
    public ValueTask<IDataNode> CreateObjectAsync(string? name = null, ObjectCreateOptions options = default);
    public ValueTask DeleteObjectAsync(ulong id);
    public ValueTask DeleteObjectAsync(string name);
    public ValueTask<bool> ExistsAsync(ulong id);
    public ValueTask<bool> ExistsAsync(string name);
    public ValueTask<NodeInfo> GetInfoAsync(ulong id);
    public ValueTask<NodeInfo> GetInfoAsync(string name);

    // Flat read/write convenience API (root-level data nodes)
    public ValueTask<Stream> OpenReadAsync(ulong id);
    public ValueTask ReadAsync(ulong id, long offset, Memory<byte> buffer);
    public ValueTask<Stream> OpenWriteAsync(ulong id);
    public ValueTask AppendAsync(ulong id, ReadOnlyMemory<byte> data);
    public ValueTask TruncateAsync(ulong id, long newLength);
    public ValueTask WriteAtAsync(ulong id, long offset, ReadOnlyMemory<byte> data); // same-size only

    // Metadata (flat)
    public ValueTask SetMetadataAsync(ulong id, string key, ReadOnlyMemory<byte> value);
    public ValueTask<ReadOnlyMemory<byte>> GetMetadataAsync(ulong id, string key);

    // Enumeration (flat — root-level nodes only)
    public IEnumerable<NodeInfo> ListObjects();
    public IAsyncEnumerable<NodeInfo> ListObjectsAsync();

    // Maintenance
    public ValueTask DefragmentAsync(IProgress<DefragmentProgress>? progress = null);
    public ValueTask RecoverAsync();
    public ObjectStoreStats GetStats();
}

// Node interfaces — returned by hierarchy API
public interface INode
{
    ulong Id { get; }
    string Name { get; }
    string FullPath { get; }
    NodeFlags Flags { get; }
    bool HasData { get; }
    bool HasChildren { get; }
    DateTimeOffset Created { get; }
    DateTimeOffset Modified { get; }

    // Type query
    IDataNode? AsDataNode();
    bool TryGetDataNode(out IDataNode? dataNode);

    // Navigation
    ValueTask<INode?> GetParentAsync(CancellationToken ct = default);
    ValueTask<INode?> GetChildAsync(string name, CancellationToken ct = default);
    IAsyncEnumerable<INode> ListChildrenAsync(bool recursive = false, CancellationToken ct = default);

    // Mutation
    ValueTask<INode> CreateChildAsync(string name, NodeCreateOptions options = default, CancellationToken ct = default);
    ValueTask DeleteAsync(CancellationToken ct = default);
    ValueTask MoveAsync(ulong newParentId, string newName, CancellationToken ct = default);
    ValueTask MoveAsync(string newParentPath, string newName, CancellationToken ct = default);
    ValueTask RenameAsync(string newName, CancellationToken ct = default);

    // Metadata
    ValueTask SetMetadataAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default);
    ValueTask<ReadOnlyMemory<byte>> GetMetadataAsync(string key, CancellationToken ct = default);
}

public interface IDataNode : INode
{
    long Size { get; }
    ValueTask ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default);
    ValueTask WriteAtAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask TruncateAsync(long newLength, CancellationToken ct = default);
    ValueTask<Stream> OpenReadStreamAsync(CancellationToken ct = default);
    ValueTask<Stream> OpenWriteStreamAsync(CancellationToken ct = default);
}

public interface ITransaction : IDisposable, IAsyncDisposable
{
    ValueTask CommitAsync();
    ValueTask RollbackAsync();
    void Commit();
    void Rollback();
}

public record NodeInfo(
    ulong Id,
    string? Name,
    string FullPath,
    NodeFlags Flags,
    long Size,            // own payload size; 0 if HasData is false
    uint ChildCount,      // direct children count; 0 if HasChildren is false
    DateTimeOffset Created,
    DateTimeOffset Modified,
    CompressionCodec Compression,
    IReadOnlyDictionary<string, byte[]> Metadata
);

[Flags]
public enum NodeFlags : byte
{
    None        = 0,
    HasData     = 1,
    HasChildren = 2,
    IsDeleted   = 4,    // internal — not exposed through normal API
}

public sealed class ObjectStoreOptions
{
    public int CacheMaxBytes { get; init; } = 32 * 1024 * 1024;
    public bool ReadOnly { get; init; } = false;       // open with OS read-only permissions
    public bool SharedAccess { get; init; } = false;   // allow concurrent openers
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public byte[]? EncryptionKey { get; init; }        // 32 bytes for AES-256
}

public sealed class NodeCreateOptions
{
    public bool HasData { get; init; } = true;
    public bool HasChildren { get; init; } = false;
    public CompressionCodec Compression { get; init; } = CompressionCodec.None;
}

public sealed class ObjectCreateOptions  // legacy alias for flat API
{
    public CompressionCodec Compression { get; init; } = CompressionCodec.None;
}

public enum CompressionCodec { None, Lz4, Zstd }
```

---

## 9. File Format Overview

```
[Offset 0]   Superblock A  (512 bytes, fixed)
[Offset 512] Superblock B  (512 bytes, fixed)
[Offset 1KB] Data region   (buddy-allocated blocks)
```

**Superblock fields**: magic (`OBJSTORE\0`), format version (major/minor), generation counter (u64), root B-tree block address (u64), buddy allocator root block address (u64), container size (u64), flags (encryption enabled, etc.), checksum.

**Block header** (16 bytes, prepended to every buddy block): block size (u32), checksum (u64 xxHash3), flags (u8), reserved.

**B-tree primary key** (16 bytes): composite `(parent_id: u64, name_hash: u64)`. Range scan on a fixed `parent_id` returns all direct children in hash order. The root node's children use `parent_id = 0`.

**NodeRecord** (stored as B-tree leaf value; replaces the original `ObjectRecord`):

| Field | Type | Description |
|---|---|---|
| `id` | u64 | Globally unique node ID (monotonic counter; root = 1) |
| `parent_id` | u64 | Parent node ID (0 = root's parent sentinel) |
| `name_hash` | u64 | FNV-1a of `name` UTF-8 bytes (part of B-tree key) |
| `name_offset` | u32 | Offset into string block |
| `name_length` | u16 | Byte length of name in string block |
| `node_type_flags` | u8 | Bit 0 = HasData, Bit 1 = HasChildren, Bit 2 = IsDeleted |
| `size` | u64 | Size of own data payload (0 if HasData = 0) |
| `child_count` | u32 | Direct child count (0 if HasChildren = 0) |
| `created` | i64 | Unix epoch milliseconds (UTC) |
| `modified` | i64 | Unix epoch milliseconds (UTC) |
| `extent_list_address` | u64 | Buddy address of extent list block (0 if no data) |
| `metadata_block_address` | u64 | Buddy address of key-value metadata block |
| `compression_codec` | u8 | 0=None, 1=LZ4, 2=Zstd |
| reserved | 3B | Padding |

**Extent list entry**: `(block_offset: u64, order: u8)` — address and buddy order of each allocated block.

---

## 10. Future Work / Out of Scope

- Streaming compression (compress across block boundaries)
- Object versioning / history
- Remote/network backends
- Replication / sync
- Import/export to standard archive formats (tar, zip)
- Symlinks or hard-links between nodes
- POSIX access-control semantics (owner, group, permissions bits)
