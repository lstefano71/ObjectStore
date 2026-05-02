namespace ObjectStore;

/// <summary>
/// Defragments a container by rewriting objects into contiguous blocks,
/// reducing fragmentation and potentially shrinking the file.
/// </summary>
public static class Defragmenter
{
    /// <summary>
    /// Defragments the store by rewriting all objects' data blocks contiguously.
    /// This frees fragmented blocks and allows buddy coalescing.
    /// Returns the number of objects defragmented.
    /// </summary>
    public static int Defragment(ObjectEngine engine)
    {
        int count = 0;
        var objects = engine.ListObjects().ToList();

        engine.BeginTransaction();
        try
        {
            foreach (var record in objects)
            {
                if (record.Size == 0) continue;
                if (record.ExtentListAddress == 0) continue;

                // Read all object data
                byte[] data = new byte[record.Size];
                engine.ReadAt(record.Id, 0, data);

                // Truncate to 0 (frees all extents)
                engine.Truncate(record.Id, 0);

                // Re-append all data (allocates fresh contiguous blocks)
                engine.Append(record.Id, data);

                count++;
            }

            engine.CommitTransaction();
        }
        catch
        {
            engine.RollbackTransaction();
            throw;
        }

        return count;
    }
}
