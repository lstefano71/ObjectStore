# Code Review Findings — ObjectStore

** ALL OF THE FINDINGS SHOULD NOW BE FIXED IN THE REPO. THIS DOCUMENT IS FOR HISTORICAL REFERENCE ONLY. **

**Date:** 2026-05-02  
**Scope:** Full implementation (src/ObjectStore, src/ObjectStore.Native)  
**Test status at review time:** 180 xUnit + 49 Python FFI tests passing

---

## Critical Severity

### Issue 1: WriteAt silently drops data on multi-extent writes

**File:** `src/ObjectStore/ObjectEngine.cs` (WriteAt method, ~line 320)

**Problem:** The `dataOffset` variable is declared but never updated (always 0). For the second extent onward in a multi-extent write, the formula `remaining - startInData + dataOffset` computes incorrectly, yielding 0 or negative values. This causes the loop to write nothing beyond the first extent.

**Example:** Object with two 100-byte extents (200 bytes total). `WriteAt(id, 50, data)` where `data.Length = 100`. First extent writes 50 bytes correctly. Second extent: `remaining=50`, `startInData=50`, so `toWrite = min(100, 50-50+0) = 0`. The last 50 bytes are silently dropped.

**Impact:** Silent data corruption for any write spanning more than one extent.

---

### Issue 2: B-tree node block too small — crashes at ~40 objects per leaf

**File:** `src/ObjectStore/BTree.cs` (lines 19-20)

**Problem:** `DefaultOrder = 32` means leaves split at `2t-1 = 63` keys. But `DefaultNodeBlockOrder = 6` gives 4096-byte blocks (4080 payload). A NodeRecord is ~80-105 bytes depending on name length, plus 16 bytes per BTreeKey. At ~40 entries, serialized node exceeds block capacity, throwing an exception *before* the split threshold is reached.

**Math:** `(4080 - 4 header) / (16 key + 95 min value) = ~36` entries max. Split threshold = 63. Gap of ~27 entries means the B-tree reliably crashes as it grows.

**Impact:** Any store with more than ~40 objects under the same parent crashes.

---

## High Severity

### Issue 3: FindById is O(n) full tree scan

**File:** `src/ObjectStore/ObjectEngine.cs` (FindById, ~line 761)

**Problem:** `FindById(ulong id)` calls `_tree.ScanAll()` which deserializes every leaf node and every NodeRecord, comparing IDs one by one. This is called by: DeleteObject, Exists, GetInfo, Append, ReadAt, WriteAt, Truncate, SetMetadata, GetMetadata, DeleteMetadata, MoveNode, DeleteSubtree, IsDescendantOf, UpdateChildCount — nearly every operation.

**Impact:** All operations are O(n) where n = total objects. Performance degrades linearly and becomes unusable at scale.

---

### Issue 4: Hash collision blocks sibling creation

**File:** `src/ObjectStore/ObjectEngine.cs` (CreateChild, ~line 410)

**Problem:** `CreateChild` uses `BTreeKey(parentId, nameHash)`. If two children under the same parent have different names but the same FNV-1a hash, the second insert throws `ObjectAlreadyExistsException`. The read path (PathResolver) handles collisions via range scan, but the write path does not.

**Impact:** At scale with thousands of siblings, FNV-1a birthday collisions become likely, preventing insertion of legitimately different-named objects.

---

### Issue 5: Buddy state block leaked on every commit

**File:** `src/ObjectStore/ObjectEngine.cs` (CommitInternal, ~line 582)

**Problem:** `CommitInternal` allocates a new block for the serialized buddy state and records its address in the superblock, but never frees the *previous* buddy state block (the old `sb.BuddyRootAddress`). Every commit permanently leaks one allocator block.

**Impact:** Unbounded space leak proportional to commit count. Unrecoverable without defrag.

---

### Issue 6: Allocator serialized before its own storage block is allocated

**File:** `src/ObjectStore/ObjectEngine.cs` (CommitInternal, lines 586-589)

**Problem:** The buddy allocator state is serialized (`_allocator.Serialize()`), then a block is allocated to store it (`_allocator.Allocate()`). The allocation modifies free list heads and data region end, but these changes aren't captured in the already-serialized bytes. On reload, the allocator state doesn't know its own storage block is allocated.

**Impact:** Potential double-allocation of the buddy state block after reopen — silent corruption.

---

### Issue 7: No fsync barrier before superblock write

**File:** `src/ObjectStore/SuperblockManager.cs` (Commit, ~line 84)

**Problem:** `CommitInternal` writes data blocks (B-tree nodes, buddy state), then `SuperblockManager.Commit` writes the new superblock and flushes. But there's no flush *between* data writes and the superblock write. The OS/disk may persist the superblock before the data it references.

**Impact:** On crash, valid superblock may reference unwritten data blocks — corrupt on recovery.

---

### Issue 8: Options parameter completely ignored in C API

**File:** `src/ObjectStore.Native/NativeExports.Store.cs` (all 4 store functions)

**Problem:** All store open/create C API functions accept an `IntPtr options` parameter but never dereference it. The `NativeOptions` object (populated via `set_read_only`, `set_compression`, `set_encryption_key`, etc.) is silently discarded. Additionally, `BlockCompression` and `BlockEncryption` exist but are never called in any data path.

**Impact:** C API consumers set options that have no effect. Compression and encryption are dead code.

---

### Issue 9: TrackNewBlock is never called — rollback leaks blocks

**File:** `src/ObjectStore/TransactionManager.cs` (TrackNewBlock, line 127)

**Problem:** `TrackNewBlock` exists to record blocks allocated during a transaction for freeing on rollback, but no code ever calls it. During rollback, `_current.NewBlocks` is always empty. The allocator snapshot restore partially handles this, but newly allocated blocks that grew the file persist as garbage.

**Impact:** Rollback doesn't properly reclaim all allocated blocks; file grows without bounds after repeated rollbacks.

---

## Medium Severity

### Issue 10: C API `objstore_write` at offset > size appends at wrong position

**File:** `src/ObjectStore.Native/NativeExports.Objects.cs` (~line 103)

**Problem:** When `offset >= info.Size`, the C API calls `engine.Append(objectId, span)` which appends at the *current end*, not at the requested offset. No gap-fill is performed.

**Impact:** Caller writes at offset 1000 (object size 500) — data lands at offset 500, not 1000. Silent position error.

---

### Issue 11: Buddy free-list in-place writes violate COW model

**File:** `src/ObjectStore/BuddyAllocator.cs` (PushFreeBlock, ~line 167)

**Problem:** `PushFreeBlock` and `PopFreeBlock` write next-pointers directly to disk (in-place mutation). This violates the copy-on-write model. If the system crashes after these writes but before the superblock commit, the on-disk free-list structure is inconsistent with the committed superblock.

**Impact:** Crash during allocation/free operations (between mutating free list and committing) leaves free list corrupted. Recovery (rebuilding from reachable blocks) mitigates this, but only if triggered.

---

## Summary Table

| # | Severity | Component | One-line |
|---|----------|-----------|----------|
| 1 | Critical | ObjectEngine.WriteAt | Multi-extent writes drop data silently |
| 2 | Critical | BTree defaults | Block too small, crashes at ~40 objects |
| 3 | High | ObjectEngine.FindById | O(n) scan on every operation |
| 4 | High | ObjectEngine.CreateChild | Hash collision blocks valid insertions |
| 5 | High | ObjectEngine.CommitInternal | Buddy state block leaked per commit |
| 6 | High | ObjectEngine.CommitInternal | Stale allocator state written to disk |
| 7 | High | SuperblockManager.Commit | No fsync before pointer-swap |
| 8 | High | NativeExports.Store | Options parameter ignored |
| 9 | High | TransactionManager | TrackNewBlock never called |
| 10 | Medium | NativeExports.Objects | Write at offset > size mispositions data |
| 11 | Medium | BuddyAllocator | In-place free-list writes break COW |
