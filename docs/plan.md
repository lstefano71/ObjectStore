# ObjectStore — Implementation Plan

## Problem Statement

Build a crash-safe, MVCC-capable, single-file binary object store in C# 14. The container uses a power-of-two buddy allocator (64B–8MB blocks), a COW B-tree indexed on composite `(parent_id, name_hash)` keys for a single-rooted node hierarchy, dual superblocks for atomic commits, SIEVE block cache, ambient transactions with savepoint-based nesting, optional per-object compression (LZ4/Zstd), and optional whole-container AES-256-GCM encryption.

## Approach

Implement in 9 phases, each building on the previous. Phases 1–4 form the non-negotiable core (allocator → B-tree → object layer → transactions). Phases 5–9 layer on caching, safety features, public-API polish, maintenance, and multi-process support.

> **AOT compatibility is a hard constraint throughout all phases.** Every class, generic type, and serialisation path must be NativeAOT-safe: no unconstrained reflection, no `dynamic`, no `Emit`. Use `[DynamicallyAccessedMembers]` annotations where reflection is unavoidable. Run `dotnet publish -p:PublishAot=true` as a CI gate from Phase 1 onward so violations are caught immediately rather than accumulated.

---

## Phase 1 — File Format & Buddy Allocator

**Goal**: A working buddy allocator that can allocate and free power-of-two blocks within a growable container file. No objects yet — just raw block management.

### Todos

- `format-constants` — Define all on-disk constants: magic bytes (`OBJSTORE\0`), format version (1.0), superblock size (512B), data region offset (1KB), min block size (64B = order 0), max block size (8MB = order 17), block header layout (16B: size u32, xxHash3 u64, flags u8, reserved 3B).
- `superblock-struct` — Implement `Superblock` struct: magic, version (major/minor u16), generation counter (u64), B-tree root address (u64), buddy root address (u64), container size (u64), flags (u32), checksum (u64). Serialise/deserialise to exactly 512 bytes. Include checksum validation.
- `dual-superblock` — Implement `SuperblockManager`: reads both superblocks on open, selects the one with the higher generation counter and valid checksum as the committed root. `Write(superblock)` always writes to the inactive slot and increments the generation counter.
- `buddy-allocator` — Implement `BuddyAllocator`: 18 free lists (one per order), each a linked list of free block addresses stored in the blocks themselves (intrusive free list). `Allocate(order)` finds the smallest available order ≥ requested, splits down. `Free(address, order)` coalesces with buddy if free.
- `buddy-persistence` — Serialize/deserialize buddy free-list state to/from a dedicated block region inside the container file. The buddy root block address is stored in the superblock.
- `file-growth` — Implement `ContainerFile`: wraps `FileStream`, handles exponential growth (double up to 256 MB, then grow by 64 MB increments), exposes `ReadBlock` / `WriteBlock` by (address, length).
- `block-checksum` — On `WriteBlock`, compute xxHash3 over block payload and write into the 16-byte block header. On `ReadBlock`, validate checksum; throw `BlockCorruptedException` on mismatch.

### Dependencies
None.

---

## Phase 2 — B-Tree

**Goal**: A persistent, COW-friendly B-tree stored inside the container using buddy-allocated blocks. Supports ordered key lookup, insert, update, delete, and sequential iteration.

### Todos

- `btree-node-layout` — Define B-tree node on-disk layout: node type (leaf/internal, u8), key count (u16), keys array (composite `(parent_id: u64, name_hash: u64)` = 16 bytes per key), values array (leaf) or child pointers array (internal). Use order-4 minimum (tunable). Node size = one buddy block (configurable, default 4KB = order 6).
- `btree-cow-ops` — Implement COW semantics: every mutation allocates a new block for the modified node, propagates new addresses up to the root, and returns a new root address. Old blocks are not freed until the transaction commits.
- `btree-crud` — Implement `BTree<TKey, TValue>` with `Get`, `Insert`, `Update`, `Delete`, `TryGet`. Primary key is `(parent_id: u64, name_hash: u64)`. Range scan on fixed `parent_id` returns all direct children.
- `btree-split-merge` — Implement node split (on insert overflow) and merge/redistribute (on delete underflow).
- `btree-iteration` — Implement ordered forward iteration via a cursor (stack of (node, index) pairs). Powers `ListChildrenAsync()` and recursive descent.
- `name-index` — Name lookup is now integral to the primary index: given a `parent_id` and a name, compute `FNV-1a(name_utf8)` and do a point lookup on `(parent_id, name_hash)`. Collisions (same hash, different names) resolved by storing the full name in the `NodeRecord` and doing a secondary equality check on the name string.

### Dependencies
Phase 1 complete.

---

## Phase 3 — Object Layer

**Goal**: Create, read, write, append, truncate, and delete objects. No transactions yet — each operation is auto-committed.

### Todos

- `object-record` — Define `NodeRecord` (replaces `ObjectRecord`, stored as B-tree leaf value): `id (u64)`, `parent_id (u64)`, `name_hash (u64)`, `name_offset (u32)`, `name_length (u16)`, `node_type_flags (u8)` (bit 0 = HasData, bit 1 = HasChildren, bit 2 = IsDeleted), `size (u64)`, `child_count (u32)`, `created (i64)`, `modified (i64)`, `extent_list_address (u64)`, `metadata_block_address (u64)`, `compression_codec (u8)`, reserved (3B).
- `extent-list` — Implement `ExtentList`: a buddy-allocated block containing an array of `(block_address: u64, order: u8)` entries. Supports append (add extent), truncate (remove trailing extents, free blocks), and iteration for sequential read/write.
- `object-read` — Implement `ReadAt(objectRecord, offset, Memory<byte>)`: resolves offset to the correct extent, reads from the container file, validates block checksum.
- `object-write` — Implement `WriteAt(objectRecord, offset, ReadOnlyMemory<byte>)`: same-size overwrite of existing extent blocks (block-level COW — allocate new block, write new data, update extent list entry, old block address queued for deferred free).
- `object-append` — Implement `Append(objectRecord, ReadOnlyMemory<byte>)`: allocates new buddy block(s) of the right order, writes data, appends extents to the extent list, updates object size.
- `object-truncate` — Implement `Truncate(objectRecord, newLength)`: frees trailing extents until size ≤ newLength; zeros out partial trailing block if needed.
- `object-create-delete` — Implement `CreateObject` (assigns new ID via atomic counter in superblock, inserts B-tree record) and `DeleteObject` (removes B-tree record, frees all extents and the extent-list block).
- `metadata-block` — Implement per-object key-value metadata: a buddy-allocated block containing (key: u16-length-prefixed UTF-8, value: u32-length-prefixed bytes) pairs. Reads/writes via `GetMetadata` / `SetMetadata`.
- `stream-adapter` — Implement `ObjectReadStream : Stream` and `ObjectWriteStream : Stream` wrapping the read/write primitives with position tracking. `ObjectWriteStream.Write()` on data past the current end triggers `Append`; within bounds triggers `WriteAt`.

### Dependencies
Phase 2 complete.

---

## Phase 4 — COW Transactions & MVCC

**Goal**: Full transactional semantics. Ambient transactions, savepoint-based nesting, MVCC snapshot isolation, crash-safe commit via dual superblock swap.

### Todos

- `transaction-state` — Implement `TransactionState`: holds the working B-tree root address, buddy allocator state snapshot, list of newly allocated blocks (to commit), list of old blocks pending free (freed only on commit, not on rollback).
- `ambient-transaction` — Implement `AmbientTransaction`: thread-local (or `AsyncLocal<T>`) current transaction reference. `BeginTransaction()` on an empty slot creates a new `TransactionState`; on an active slot creates a `SavepointState` (captures the current B-tree root and pending-free list at that point).
- `savepoint-rollback` — On `Rollback()` within a savepoint: free all blocks allocated since the savepoint, restore B-tree root to savepoint root, remove those blocks from the pending-commit list.
- `commit-protocol` — On outermost `Commit()`: (1) flush all dirty blocks to disk; (2) flush buddy allocator state; (3) build new superblock with incremented generation counter and new B-tree root; (4) write to inactive superblock slot; (5) `FileStream.Flush(flushToDisk: true)`; (6) update the active-slot pointer.
- `mvcc-snapshot` — On `BeginTransaction()` (or any auto-commit read), capture the current committed superblock generation + B-tree root as a read snapshot. Reads use this snapshot's root; writes fork off COW copies. Reference-count open snapshots (atomic integer per generation).
- `deferred-free` — Blocks freed within a transaction are not returned to the buddy allocator until the transaction commits AND no older snapshot still references that generation. Implement a pending-free queue per generation, swept when `snapshot_ref_count[generation]` reaches zero.
- `auto-commit` — Wrap each public API call (outside an explicit transaction) in an implicit `BeginTransaction` / `Commit` / `Rollback`-on-exception.

### Dependencies
Phase 3 complete.

---

## Phase 5 — SIEVE Block Cache

**Goal**: An in-process block cache using the SIEVE eviction algorithm to avoid redundant IO on hot B-tree nodes and object extents.

### Todos

- `sieve-cache` — Implement `SieveCache<TKey, TValue>`: a dictionary + a FIFO queue of entries, each with a `visited` bit. Eviction scans the queue from the tail; entries with `visited=1` are cleared and skipped; the first `visited=0` entry is evicted. `Get` sets `visited=1`. Configurable max byte capacity (approximate, by summing block sizes).
- `cache-integration` — Integrate `SieveCache` into `ContainerFile.ReadBlock`: check cache first; on miss, read from disk and insert. `WriteBlock` inserts/updates the cache with the new block data. Invalidate on deferred-free.
- `cache-mvcc-safety` — Under COW, a block address is immutable once written; no invalidation needed for reads. Only blocks in the deferred-free queue need cache eviction (remove by address when freed).

### Dependencies
Phase 4 complete.

---

## Phase 6 — Compression & Encryption

**Goal**: Transparent per-object LZ4/Zstd compression and whole-container AES-256-GCM encryption.

### Todos

- `compression-write` — On `Append` / `WriteAt` for a compressed object, compress the data using the object's codec before writing to the block. Store uncompressed size in the block header flags/metadata area. Use `System.IO.Compression` for Zstd; bundle a small managed LZ4 or use `System.IO.Compression.Brotli` as LZ4 proxy (or reference a NuGet LZ4 package if permitted).
- `compression-read` — On `ReadAt` for a compressed object, read the compressed block, decompress to the requested range. Handle the case where a read spans multiple compressed extents.
- `encryption-layer` — When `EncryptionKey` is provided, wrap `ContainerFile` in an `EncryptedContainerFile` decorator. Each block write: generate a random 96-bit IV, encrypt payload with AES-256-GCM, write `(IV[12] | ciphertext | tag[16])` to disk. Each block read: extract IV and tag, decrypt, validate GCM tag (throws `TamperedBlockException` on failure). Store encryption-enabled flag in superblock flags.
- `key-derivation` — Derive the actual AES key using HKDF-SHA256 from the caller-supplied key material + a per-container salt stored in the superblock. This allows the same passphrase to produce different per-container keys.

### Dependencies
Phase 5 complete.

---

## Phase 7 — Public API & Stream Polish

**Goal**: Complete the `ObjectStore` public surface, wire everything together, and implement all ergonomic API methods.

### Todos

- `objectstore-class` — Implement the final `ObjectStore` class with all methods from the PRD API section. Wire `BeginTransaction`, `CommitAsync`, `RollbackAsync` through `AmbientTransaction`. Implement `IAsyncDisposable` (flushes and closes the file). Implement both the hierarchy API (`GetRootAsync`, `GetNodeAsync`, `CreateNodeAsync`) and the flat convenience API (which routes to the root node).
- `open-create-factory` — Implement `ObjectStore.CreateAsync`, `OpenAsync`, `OpenOrCreateAsync`. On open: read both superblocks, select active, validate version, run recovery if `cleanly_closed` flag is not set. On create: bootstrap the root node (ID=1, parent_id=0) and write the initial superblock.
- `list-objects` — Implement `ListObjects()` and `ListObjectsAsync()` by doing a range scan over `(parent_id=root, *)` in the B-tree under the current snapshot.
- `object-handle` — Implement `NodeHandle` (returned from `CreateNodeAsync` / `CreateObjectAsync`): carries assigned ID, name, full path, `NodeFlags`; implements both `INode` and (when `HasData`) `IDataNode`.
- `progress-reporting` — `DefragmentAsync` and `RecoverAsync` accept `IProgress<T>` for reporting progress back to callers.

### Dependencies
Phase 6 complete.

---

## Phase 8 — Defragmentation & Recovery

**Goal**: `Defragment()` reclaims unreachable blocks and consolidates free space. `Recover()` heals a container opened after a crash.

### Todos

- `recover` — On open with dirty-close flag: scan all blocks reachable from the committed superblock root (B-tree traversal + extent lists). Mark all reachable blocks in a temporary bitmap. Any block in the data region not reachable and not in the buddy free list is leaked; add to the free list. Clear dirty-close flag and write a new superblock.
- `defragment` — Walk all objects in B-tree order. For each object, check if its extents are maximally coalesced (i.e., no two adjacent extents could be merged into a larger buddy block). If not, read the object data, allocate a fresh contiguous (or less fragmented) layout, rewrite, update extent list, free old blocks. After all objects, run buddy coalescing to merge adjacent free blocks. Commit a new superblock.
- `dirty-close-flag` — Set a `dirty_open` flag in the superblock at open time (write to the active superblock without incrementing generation, so it's a soft dirty marker). Clear it on clean close. Recovery checks this flag.

### Dependencies
Phase 7 complete.

---

## Phase 9 — Multi-Process Shared Access & Testing

**Goal**: Multi-process safety, a full test suite, and benchmarks.

### Todos

- `readonly-mode` — When `ReadOnly = true`: open `FileStream` with `FileAccess.Read`; set `FileShare` based on `SharedAccess` (exclusive = `FileShare.None`, shared = `FileShare.Read`). Guard every mutating code path with a check; throw `ReadOnlyContainerException` with a clear message. Skip dirty-close flag write and recovery on open. `CreateAsync` / `OpenOrCreateAsync` throw `ArgumentException` if `ReadOnly = true`.
- `shared-access` — When `OpenOptions.SharedAccess = true`: before any write transaction commit, acquire an exclusive OS advisory lock on a dedicated lock byte range in the file header. Readers never acquire locks. On commit, re-read the superblock (another process may have committed since our transaction started); if the generation has advanced, replay or abort with `ConcurrentModificationException`. Release lock after superblock swap.
- `lock-retry` — Implement exponential back-off retry for lock acquisition with jitter. Respect `LockTimeout`. Throw `LockTimeoutException` if the timeout expires.
- `unit-tests` — Write unit tests for: buddy allocator (alloc/free/coalesce), B-tree (insert/delete/split/merge/iterate), transaction rollback, savepoint rollback, crash simulation (truncate file mid-write, reopen, verify), checksum corruption detection, encryption round-trip, compression round-trip.
- `integration-tests` — End-to-end tests: create 10k objects, random read/write, delete half, defragment, verify all remaining objects intact. Multi-threaded concurrent readers + writer. Multi-process shared access (two processes, interleaved writes).
- `benchmarks` — BenchmarkDotNet benchmarks: sequential write throughput, random read throughput, 1k-object B-tree lookup, cache hit vs miss ratio.
- `stats` — Implement `GetStats()`: walk the buddy free lists to compute total free bytes per order; count B-tree entries for object count; compute fragmentation ratio (free_block_count / total_block_count weighted by order).

### Dependencies
Phase 8 complete.

---

## Phase 10 — Node Hierarchy

**Goal**: Extend the flat object store into a full single-rooted node tree. Every element (root, group, data node) is a `NodeRecord`. The B-tree primary key becomes `(parent_id, name_hash)`. Callers interact via `INode` / `IDataNode` interfaces.

### Todos

- `node-record` — Replace `ObjectRecord` with `NodeRecord`: add `parent_id (u64)`, `name_hash (u64)`, `node_type_flags (u8)` (HasData / HasChildren / IsDeleted), `child_count (u32)`. Update all serialise/deserialise paths. Total record size must remain within a single B-tree leaf slot.
- `btree-composite-key` — Update the B-tree key type from `ulong` to `(parent_id: ulong, name_hash: ulong)`. Update all comparison, split, and iteration logic to use the composite key. Range scan on fixed `parent_id` yields direct children.
- `root-bootstrap` — On container creation, insert the root node: ID = 1, `parent_id = 0`, `name = "/"`, `HasChildren = true`. Store root ID in the superblock.
- `path-resolver` — Implement `PathResolver.ResolveAsync(path, snapshot)`: split on `/`, iteratively look up `(current_parent_id, FNV-1a(segment))` in the B-tree, return the final `NodeRecord`. Cache intermediate lookups within the same transaction.
- `inode-impl` — Implement `NodeHandle` / `NodeProxy` that implements both `INode` and (conditionally) `IDataNode`. `AsDataNode()` returns `this` if `HasData`, else `null`. `TryGetDataNode(out var dn)` sets `dn` accordingly.
- `node-create` — Implement `CreateChildAsync(name, options)` on `INode` and `ObjectStore.CreateNodeAsync(path, options)`. Inserts a new `NodeRecord` under the given parent. If `HasData`, allocates an empty extent list. If `HasChildren`, sets the `HasChildren` flag. Updates parent's `child_count` and records the parent ID in `TransactionState.DirtyParents` for lazy `modified` timestamp update.
- `node-delete-recursive` — Implement `DeleteAsync()` on `INode`. Tombstones the node in the B-tree (sets `IsDeleted` flag via COW update). At commit time, a post-order recursive walk of tombstoned subtrees queues all descendant `NodeRecord` blocks, extent lists, and metadata blocks into the deferred-free queue.
- `node-move` — Implement `MoveAsync(newParentId, newName)`: updates `parent_id` and `name_hash` in the `NodeRecord` via a B-tree delete + re-insert with the new composite key. Atomically within the current transaction. Updates `child_count` on old and new parents; records both in `DirtyParents`.
- `lazy-modified` — In `TransactionState`, maintain `DirtyParents: HashSet<ulong>`. On commit, for each ID in `DirtyParents`, read the parent's `NodeRecord`, update `modified = DateTime.UtcNow`, COW-write back. Apply before the final superblock swap.
- `node-enumerate` — Implement `ListChildrenAsync(recursive: false)`: B-tree range scan on `(parent_id = node.Id, *)`, yielding `NodeInfo` per direct child. `recursive: true`: depth-first async iterator, recursing into children with `HasChildren = true`.
- `hierarchy-api` — Wire `GetRootAsync`, `GetNodeAsync(path)`, `GetNodeAsync(id)` on `ObjectStore`. Validate paths (no `..`, no leading double-slash). Update flat convenience API to target `parent_id = root.Id`.
- `hierarchy-tests` — Unit tests: create/navigate deep tree (10 levels), rename node, move node across subtrees, delete subtree, verify deferred-free, path resolution with hash collisions, concurrent reads during subtree delete.

### Dependencies
Phase 9 complete. (Can be developed against Phase 7 in parallel with Phase 9 if desired.)

---

## Key Structural Invariants

1. **A block address is immutable after first write.** COW never overwrites an existing block in place.
2. **Old blocks are never freed before commit.** The deferred-free queue holds them until the transaction commits and no older snapshot references their generation.
3. **The active superblock always points to a self-consistent tree.** The inactive superblock may be stale — that's fine.
4. **The buddy allocator state is always consistent with the committed superblock.** On crash, the buddy state is reconstructed from the committed root during recovery if needed.
5. **Block-level COW for writes, not object-level.** Only the specific buddy block(s) touched by a write are copied.
6. **A node's `modified` timestamp is written at most once per transaction per node** via the lazy `DirtyParents` set — never on every child operation.

---

## Risk Register

| Risk | Mitigation |
|---|---|
| B-tree splits propagate many COW copies to root | Use path-copying only on the write path; reads are zero-copy |
| Deferred-free queue grows unbounded under long transactions | Warn (log) if deferred-free queue exceeds a threshold; document that long transactions hold space |
| SIEVE cache coherence with deferred-free | Remove freed blocks from cache on deferred-free enqueue, not on commit |
| Multi-process: reader reads stale superblock | Readers always read the superblock fresh at snapshot start; no caching of the superblock across transactions |
| Encryption IV reuse | Use `RandomNumberGenerator.GetBytes(12)` per block write; statistically negligible collision probability |
| Hash collision in `(parent_id, name_hash)` key | Full name stored in `NodeRecord`; on lookup, verify name string equality after hash match |
| Recursive delete of large subtrees at commit | Cap the commit-time tombstone sweep to a configurable batch; continue in subsequent auto-commits if needed |
| Move creates a cycle (node moved under itself) | Before committing a `MoveAsync`, walk the new parent's ancestor chain and verify it does not contain the moved node's ID |
