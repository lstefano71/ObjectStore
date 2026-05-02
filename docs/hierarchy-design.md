# ObjectStore — Node Hierarchy Design

This document records the design decisions made when adding a hierarchical node tree to ObjectStore. The decisions are integrated into `prd.md` (FR-42–FR-55) and `plan.md` (Phase 10), but this file provides the rationale and trade-offs in one place.

---

## Decision Log

### 1. Node Model: Unified

**Decision**: A node may simultaneously hold a binary data payload (`HasData`) and contain child nodes (`HasChildren`). Neither attribute is required. A node with neither flag is a structural placeholder; a node with both is a container-with-content.

**Rationale**: A strict "file vs directory" split would require callers to choose at creation time and prohibit future migration. The unified model is more general, costs only one `u8` flags field, and follows the principle that restrictions are easier to add than to remove. The API makes capabilities discoverable at runtime via `HasData` / `HasChildren` rather than statically via type.

---

### 2. Root Structure: Single Implicit Root

**Decision**: Every store has exactly one root node. ID = 1, `parent_id = 0`, name = `/`. All absolute paths start from `/`.

**Rationale**: A single root is the simplest model, consistent with every major filesystem and the OLE compound file structure. Multiple named roots add complexity with little benefit for the target use cases.

---

### 3. Navigation API: Both Path String and Step-by-Step

**Decision**: Both `store.GetNodeAsync("/a/b/c")` (one call) and `node.GetChildAsync("b")` (chained calls) are supported.

**Rationale**: Path strings are ergonomic for deep access; chained traversal is useful for incremental or interactive navigation. The cost of providing both is low — path resolution is already composed of the same B-tree lookups.

---

### 4. Node Identity: Shared uint64 Counter with `node_type_flags`

**Decision**: All nodes (root, groups, data nodes) draw their ID from the same monotonically-increasing `uint64` counter already used for object IDs. A `node_type_flags (u8)` field in `NodeRecord` distinguishes their capabilities.

**Rationale**: Keeps the B-tree machinery uniform. A single counter guarantees globally unique IDs across all node types, enabling direct lookup by ID without knowing the node's type in advance.

---

### 5. Index Structure: Composite B-Tree Key `(parent_id, name_hash)`

**Decision**: The primary B-tree uses a 16-byte composite key `(parent_id: u64, name_hash: u64)`. `parent_id = 0` is the sentinel for root's children. A range scan on a fixed `parent_id` returns all direct children.

**Rationale**: Extends the existing B-tree without a second tree. Ordering by `(parent_id, name_hash)` groups all siblings together, making `ListChildren` a single range scan. The alternative — a separate hierarchy B-tree — adds a second tree to keep in sync on every create/delete/move.

**Hash collision handling**: The full node name is stored in `NodeRecord`. On lookup, after the hash match, the name string is compared for equality. Collisions within the same parent are vanishingly rare but handled correctly.

---

### 6. Recursive Deletion: Soft-Delete + Deferred-Free

**Decision**: Deleting a node tombstones its `NodeRecord` (sets `IsDeleted` flag via COW update). At commit time, a post-order walk of tombstoned subtrees enqueues all descendant blocks into the deferred-free queue. MVCC snapshots predating the delete continue to see the full subtree until their generation is reclaimed.

**Rationale**: Consistent with the existing deferred-free design. The COW write of the tombstone is O(log n) in the B-tree depth; the post-order walk at commit is proportional to the subtree size, which is unavoidable. Immediate recursive free (walk + free at delete time) holds the write lock for the full duration of the walk — problematic for large subtrees.

**Cycle guard**: `MoveAsync` checks that the new parent's ancestor chain does not contain the moved node's ID before committing, preventing `A` from becoming a descendant of itself.

---

### 7. Atomic Move and Rename

**Decision**: `INode.MoveAsync(newParentId, newName)` performs a B-tree delete of the old `(parent_id, name_hash)` key and an insert of the new composite key within a single transaction. The node's `uint64` ID is unchanged.

**Rationale**: ID stability is essential for any external reference (a caller caching an ID, a C API handle). A rename should not invalidate those references. The COW B-tree makes this a natural atomic operation.

---

### 8. Path Resolution Algorithm

**Decision**: `GetNodeAsync("/a/b/c")` splits on `/`, then for each segment computes `FNV-1a(segment_utf8)` and performs a point lookup on `(current_parent_id, name_hash)`. After the hash lookup, the name string in the returned `NodeRecord` is verified for equality (collision handling).

**Rationale**: O(depth × log n) — one B-tree lookup per path segment. No additional storage required beyond what is already in `NodeRecord`. `GetFullPath(id)` is computed by walking `parent_id` pointers up to the root.

---

### 9. Enumeration: Shallow Default, Optional Recursive

**Decision**: `INode.ListChildrenAsync(recursive: false)` (default) returns direct children only via a B-tree range scan on `(parent_id = node.Id, *)`. `recursive: true` performs a depth-first async iteration over all descendants.

**Rationale**: Shallow enumeration is O(k log n) for k children; callers who want it will always use it. Recursive enumeration should be opt-in to avoid accidentally enumerating huge subtrees.

---

### 10. Group Metadata: Full Parity with Lazy Modified

**Decision**: Every node (group or data) carries the same metadata: `size` (own payload size; 0 if `HasData = false`), `created`, `modified`, and a user key-value metadata bag. The `modified` timestamp follows **POSIX `mtime` semantics**: it updates when the node's own payload changes **or** when a direct child is added, removed, or renamed.

**Performance**: The timestamp update is **lazy**: `TransactionState` tracks a `DirtyParents: HashSet<ulong>`. At commit time, each affected parent's `NodeRecord` is read and its `modified` timestamp is written once — regardless of how many child operations occurred in the transaction. This reduces the overhead to one extra B-tree write per affected parent per transaction.

---

### 11. Case Sensitivity: Case-Sensitive

**Decision**: Node names within a parent are case-sensitive. `FNV-1a` is computed over the raw UTF-8 bytes of the name — no folding.

**Rationale**: Consistent with Linux filesystem semantics and with most developer expectations for programmatic stores. Avoids storing a canonical form separately. Users on Windows who want case-insensitive behaviour should fold names themselves at the application layer.

---

### 12. On-Disk Record: `NodeRecord` Replaces `ObjectRecord`

**Decision**: A new `NodeRecord` type replaces `ObjectRecord`. It adds `parent_id`, `name_hash`, `node_type_flags`, and `child_count` to the existing fields, and removes the now-redundant flat-index fields.

**Fields**:

| Field | Type | Notes |
|---|---|---|
| `id` | u64 | Global monotonic node ID |
| `parent_id` | u64 | Part of B-tree key |
| `name_hash` | u64 | FNV-1a(name UTF-8); part of B-tree key |
| `name_offset` | u32 | Offset in string block |
| `name_length` | u16 | Byte length of name |
| `node_type_flags` | u8 | HasData (0), HasChildren (1), IsDeleted (2) |
| `size` | u64 | Own payload size |
| `child_count` | u32 | Direct child count |
| `created` | i64 | Unix epoch ms |
| `modified` | i64 | Unix epoch ms |
| `extent_list_address` | u64 | 0 if no data payload |
| `metadata_block_address` | u64 | Key-value metadata |
| `compression_codec` | u8 | 0=None, 1=LZ4, 2=Zstd |
| reserved | 3B | Padding |

---

### 13. C# API Handles: `INode` + `IDataNode`

**Decision**: Two interfaces:

- `INode` — base interface for all nodes. Exposes ID, name, full path, flags, metadata, navigation (`GetChildAsync`, `ListChildrenAsync`, `GetParentAsync`), and mutation (`CreateChildAsync`, `DeleteAsync`, `MoveAsync`, `RenameAsync`). Returned by all methods where the node type is not statically known (path lookup, enumeration results).
- `IDataNode : INode` — extends `INode` with data payload access: `Size`, `ReadAtAsync`, `WriteAtAsync`, `AppendAsync`, `TruncateAsync`, `OpenReadStreamAsync`, `OpenWriteStreamAsync`.

`INode` exposes two type-query methods:
- `IDataNode? AsDataNode()` — returns `this` (cast) if `HasData`, otherwise `null`.
- `bool TryGetDataNode(out IDataNode? dataNode)` — null-safe pattern-match style.

**Rationale**: The COM-inspired `QueryInterface` pattern, but idiomatic to C#. A caller receiving an `INode` from enumeration can check `HasData` (cheap flag read) or attempt `TryGetDataNode()` before accessing data. Since a node can be both a container and a data carrier, a single-type hierarchy without branching is cleaner than two parallel class hierarchies.

---

## Interaction with Existing Design

| Area | Change |
|---|---|
| B-tree key | `ulong` → `(parent_id: u64, name_hash: u64)` (16 bytes) |
| `ObjectRecord` | Replaced by `NodeRecord` |
| `ObjectHandle` / `ObjectInfo` | Replaced by `NodeHandle` (impl `INode`/`IDataNode`) and `NodeInfo` record |
| Flat API methods | Kept as convenience wrappers; internally they target `parent_id = root.Id` |
| Transactions / MVCC | No change to semantics; `DirtyParents` is a new field on `TransactionState` |
| Deferred-free | Extended to support post-order subtree sweep on tombstoned node delete |
| Superblock | Add `root_node_id (u64)` field (= 1); no other structural change |
| Recovery | Reachability scan now follows `NodeRecord.parent_id` chains; semantics unchanged |
