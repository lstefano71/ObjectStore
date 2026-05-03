namespace ObjectStore;

/// <summary>
/// Manages transactions for the ObjectEngine — provides begin/commit/rollback
/// with savepoint nesting and deferred-free semantics.
/// </summary>
public sealed class TransactionManager
{
    private readonly ObjectEngine _engine;
    private TransactionState? _current;

    public bool HasActiveTransaction => _current != null;
    public TransactionState? Current => _current;

    public TransactionManager(ObjectEngine engine)
    {
        _engine = engine;
    }

    /// <summary>Begins a new transaction or creates a nested savepoint.</summary>
    public void Begin()
    {
        if (_current == null)
        {
            var (freeLists, dataEnd, freeCount) = _engine.Allocator.Snapshot();
            _current = new TransactionState
            {
                Depth = 1,
                OriginalRootAddress = _engine.Tree.RootAddress,
                OriginalIdTreeRootAddress = _engine.IdTree.RootAddress,
                WorkingRootAddress = _engine.Tree.RootAddress,
                AllocatorSnapshot = freeLists,
                AllocatorDataRegionEnd = dataEnd,
                AllocatorFreeBlockCount = freeCount,
                OriginalNextNodeId = _engine.NextNodeId,
            };
            _engine.Tree.BeginBatchMode();
            _engine.IdTree.BeginBatchMode();
            _engine.File.BeginBufferedWrites();
        }
        else
        {
            // Nested: push savepoint
            var (freeLists, dataEnd, freeCount) = _engine.Allocator.Snapshot();
            var sp = new SavepointState
            {
                RootAddress = _engine.Tree.RootAddress,
                IdTreeRootAddress = _engine.IdTree.RootAddress,
                AllocatorSnapshot = freeLists,
                AllocatorDataRegionEnd = dataEnd,
                AllocatorFreeBlockCount = freeCount,
                NewBlocksCount = _current.NewBlocks.Count,
                PendingFreeCount = _current.PendingFree.Count,
                NextNodeId = _engine.NextNodeId,
            };
            _current.Savepoints.Push(sp);
            _current.Depth++;

            // Clear batch-owned tracking so blocks from before the savepoint
            // get normal COW (preserving the pre-savepoint state for rollback).
            _engine.Tree.EndBatchMode();
            _engine.IdTree.EndBatchMode();
            _engine.Tree.BeginBatchMode();
            _engine.IdTree.BeginBatchMode();
        }
    }

    /// <summary>Commits the current transaction (or pops a savepoint).</summary>
    public void Commit()
    {
        if (_current == null)
            throw new InvalidOperationException("No active transaction to commit.");

        if (_current.Savepoints.Count > 0)
        {
            // Pop savepoint — merge its changes into the parent level
            _current.Savepoints.Pop();
            _current.Depth--;
        }
        else
        {
            // Top-level commit: end batch mode, flush buffered writes, then persist
            _engine.Tree.EndBatchMode();
            _engine.IdTree.EndBatchMode();
            _engine.File.DrainBufferedWrites();
            _engine.File.EndBufferedWrites();

            _engine.CommitInternal();

            // Execute deferred frees (blocks from before this transaction)
            foreach (var (addr, order) in _current.PendingFree)
                _engine.Allocator.Free(addr, order);

            _current = null;
        }
    }

    /// <summary>Rolls back the current savepoint or entire transaction.</summary>
    public void Rollback()
    {
        if (_current == null)
            throw new InvalidOperationException("No active transaction to rollback.");

        if (_current.Savepoints.Count > 0)
        {
            // Rollback to last savepoint
            var sp = _current.Savepoints.Pop();
            _current.Depth--;

            // Restore pending-free list
            while (_current.PendingFree.Count > sp.PendingFreeCount)
                _current.PendingFree.RemoveAt(_current.PendingFree.Count - 1);

            // Trim new blocks list
            while (_current.NewBlocks.Count > sp.NewBlocksCount)
                _current.NewBlocks.RemoveAt(_current.NewBlocks.Count - 1);

            // Restore allocator and tree state
            _engine.Allocator.RestoreFromSnapshot(sp.AllocatorSnapshot,
                sp.AllocatorDataRegionEnd, sp.AllocatorFreeBlockCount);
            _engine.RestoreRootAddress(sp.RootAddress);
            _engine.RestoreIdTreeRootAddress(sp.IdTreeRootAddress);
            _engine.RestoreNextNodeId(sp.NextNodeId);
            _engine.IdCacheClear();

            // Reset batch tracking (allocator rolled back, old addresses invalid)
            _engine.Tree.EndBatchMode();
            _engine.IdTree.EndBatchMode();
            _engine.Tree.BeginBatchMode();
            _engine.IdTree.BeginBatchMode();
        }
        else
        {
            // Full rollback to beginning of transaction — end batch mode
            _engine.Tree.EndBatchMode();
            _engine.IdTree.EndBatchMode();
            _engine.File.DiscardBufferedWrites();
            _engine.File.EndBufferedWrites();

            _engine.Allocator.RestoreFromSnapshot(_current.AllocatorSnapshot,
                _current.AllocatorDataRegionEnd, _current.AllocatorFreeBlockCount);
            _engine.RestoreRootAddress(_current.OriginalRootAddress);
            _engine.RestoreIdTreeRootAddress(_current.OriginalIdTreeRootAddress);
            _engine.RestoreNextNodeId(_current.OriginalNextNodeId);
            _engine.IdCacheClear();

            _current = null;
        }
    }

    /// <summary>Records a newly allocated block (for rollback tracking).</summary>
    public void TrackNewBlock(long address, int order)
    {
        _current?.NewBlocks.Add((address, order));
    }

    /// <summary>Records a block freed during transaction (deferred until commit).</summary>
    public void TrackPendingFree(long address, int order)
    {
        _current?.PendingFree.Add((address, order));
    }
}
