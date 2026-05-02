namespace ObjectStore;

/// <summary>
/// Represents the state of an active transaction — captures the working tree root,
/// allocator snapshot, new/freed block lists for rollback and deferred-free.
/// </summary>
public sealed class TransactionState
{
    /// <summary>Nesting level (1 = top-level, 2+ = savepoint).</summary>
    public int Depth { get; set; } = 1;

    /// <summary>B-tree root address at the start of this transaction/savepoint.</summary>
    public long OriginalRootAddress { get; set; }

    /// <summary>ID index tree root address at the start of this transaction.</summary>
    public long OriginalIdTreeRootAddress { get; set; }

    /// <summary>Working B-tree root address (updated as mutations occur).</summary>
    public long WorkingRootAddress { get; set; }

    /// <summary>Buddy allocator snapshot at the start of this transaction/savepoint.</summary>
    public List<long>[] AllocatorSnapshot { get; set; } = [];
    public long AllocatorDataRegionEnd { get; set; }
    public int AllocatorFreeBlockCount { get; set; }

    /// <summary>Blocks allocated during this transaction (freed on rollback).</summary>
    public List<(long Address, int Order)> NewBlocks { get; } = new();

    /// <summary>Blocks freed during this transaction (deferred until commit).</summary>
    public List<(long Address, int Order)> PendingFree { get; } = new();

    /// <summary>NextNodeId at the start of this transaction/savepoint.</summary>
    public ulong OriginalNextNodeId { get; set; }

    /// <summary>Savepoint stack for nested transactions.</summary>
    public Stack<SavepointState> Savepoints { get; } = new();
}

/// <summary>
/// Captures the state at a savepoint for nested transaction rollback.
/// </summary>
public sealed class SavepointState
{
    public long RootAddress { get; set; }
    public long IdTreeRootAddress { get; set; }
    public List<long>[] AllocatorSnapshot { get; set; } = [];
    public long AllocatorDataRegionEnd { get; set; }
    public int AllocatorFreeBlockCount { get; set; }
    public int NewBlocksCount { get; set; }
    public int PendingFreeCount { get; set; }
    public ulong NextNodeId { get; set; }
}
