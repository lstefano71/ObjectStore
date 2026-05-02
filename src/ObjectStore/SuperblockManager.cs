namespace ObjectStore;

/// <summary>
/// Manages the dual superblock system. Reads both on open, selects the valid one
/// with the highest generation, and writes to the inactive slot on commit.
/// </summary>
public sealed class SuperblockManager
{
    private readonly ContainerFile _file;
    private int _activeSlot; // 0 = A, 1 = B
    private Superblock _active;

    public Superblock Active => _active;
    public int ActiveSlot => _activeSlot;

    public SuperblockManager(ContainerFile file)
    {
        _file = file;
    }

    /// <summary>
    /// Reads both superblocks and selects the valid one with the highest generation.
    /// Returns false if neither superblock is valid (new file or fully corrupt).
    /// </summary>
    public bool TryLoad()
    {
        Span<byte> bufA = stackalloc byte[FormatConstants.SuperblockSize];
        Span<byte> bufB = stackalloc byte[FormatConstants.SuperblockSize];

        _file.ReadRaw(FormatConstants.SuperblockAOffset, bufA);
        _file.ReadRaw(FormatConstants.SuperblockBOffset, bufB);

        bool validA = Superblock.TryReadFrom(bufA, out var sbA);
        bool validB = Superblock.TryReadFrom(bufB, out var sbB);

        if (validA && validB)
        {
            if (sbA.Generation >= sbB.Generation)
            {
                _active = sbA;
                _activeSlot = 0;
            }
            else
            {
                _active = sbB;
                _activeSlot = 1;
            }
            return true;
        }

        if (validA)
        {
            _active = sbA;
            _activeSlot = 0;
            return true;
        }

        if (validB)
        {
            _active = sbB;
            _activeSlot = 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Writes the given superblock to the inactive slot, increments the generation,
    /// and swaps the active slot pointer.
    /// </summary>
    public void Commit(Superblock sb)
    {
        int inactiveSlot = _activeSlot == 0 ? 1 : 0;
        sb.Generation = _active.Generation + 1;

        Span<byte> buf = stackalloc byte[FormatConstants.SuperblockSize];
        sb.WriteTo(buf);

        long offset = inactiveSlot == 0
            ? FormatConstants.SuperblockAOffset
            : FormatConstants.SuperblockBOffset;

        _file.WriteRaw(offset, buf);
        _file.Flush();

        _active = sb;
        _activeSlot = inactiveSlot;
    }

    /// <summary>
    /// Initializes both superblock slots with the given initial superblock (for new containers).
    /// </summary>
    public void Initialize(Superblock sb)
    {
        sb.Generation = 1;

        Span<byte> buf = stackalloc byte[FormatConstants.SuperblockSize];
        sb.WriteTo(buf);

        _file.WriteRaw(FormatConstants.SuperblockAOffset, buf);
        _file.WriteRaw(FormatConstants.SuperblockBOffset, buf);
        _file.Flush();

        _active = sb;
        _activeSlot = 0;
    }
}
