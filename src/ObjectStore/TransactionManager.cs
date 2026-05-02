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
                WorkingRootAddress = _engine.Tree.RootAddress,
                AllocatorSnapshot = freeLists,
                AllocatorDataRegionEnd = dataEnd,
                AllocatorFreeBlockCount = freeCount,
                OriginalNextNodeId = _engine.NextNodeId,
            };
        }
        else
        {
            // Nested: push savepoint
            var (freeLists, dataEnd, freeCount) = _engine.Allocator.Snapshot();
            var sp = new SavepointState
            {
                RootAddress = _engine.Tree.RootAddress,
                AllocatorSnapshot = freeLists,
                AllocatorDataRegionEnd = dataEnd,
                AllocatorFreeBlockCount = freeCount,
                NewBlocksCount = _current.NewBlocks.Count,
                PendingFreeCount = _current.PendingFree.Count,
                NextNodeId = _engine.NextNodeId,
            };
            _current.Savepoints.Push(sp);
            _current.Depth++;
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
            // Top-level commit: persist to disk
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
            _engine.RestoreNextNodeId(sp.NextNodeId);
        }
        else
        {
            // Full rollback to beginning of transaction
            // Restore allocator state from snapshot (this undoes all allocations)
            _engine.Allocator.RestoreFromSnapshot(_current.AllocatorSnapshot,
                _current.AllocatorDataRegionEnd, _current.AllocatorFreeBlockCount);
            _engine.RestoreRootAddress(_current.OriginalRootAddress);
            _engine.RestoreNextNodeId(_current.OriginalNextNodeId);

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
