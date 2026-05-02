using System.Text;
using BenchmarkDotNet.Attributes;
using ObjectStore;

namespace ObjectStore.Benchmarks;

[MemoryDiagnoser]
public class SingleOperationBenchmarks
{
    private string _path = null!;
    private ObjectEngine _engine = null!;
    private ulong _smallObjId;
    private ulong _largeObjId;
    private byte[] _smallData = null!;
    private byte[] _largeData = null!;
    private byte[] _readBuf = null!;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"bench_{Guid.NewGuid():N}.dat");
        _engine = ObjectEngine.Create(_path);
        _smallData = new byte[100];
        _largeData = new byte[65536];
        _readBuf = new byte[65536];
        Random.Shared.NextBytes(_smallData);
        Random.Shared.NextBytes(_largeData);

        // Pre-create objects for read/write benchmarks
        _smallObjId = _engine.CreateObject("bench_small");
        _engine.Append(_smallObjId, _smallData);

        _largeObjId = _engine.CreateObject("bench_large");
        _engine.Append(_largeObjId, _largeData);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        File.Delete(_path);
    }

    [Benchmark]
    public ulong CreateObject()
    {
        return _engine.CreateObject($"obj_{_counter++}");
    }

    [Benchmark]
    public void AppendSmall()
    {
        _engine.Append(_smallObjId, _smallData);
    }

    [Benchmark]
    public void AppendLarge()
    {
        _engine.Append(_largeObjId, _largeData);
    }

    [Benchmark]
    public int ReadSmall()
    {
        return _engine.ReadAt(_smallObjId, 0, _readBuf.AsSpan(0, 100));
    }

    [Benchmark]
    public int ReadLarge()
    {
        return _engine.ReadAt(_largeObjId, 0, _readBuf);
    }

    [Benchmark]
    public void WriteAtMiddle()
    {
        _engine.WriteAt(_largeObjId, 32000, _smallData);
    }

    [Benchmark]
    public bool DeleteObject()
    {
        var id = _engine.CreateObject($"del_{_counter++}");
        return _engine.DeleteObject(id);
    }

    [Benchmark]
    public void MetadataSetGet()
    {
        _engine.SetMetadata(_smallObjId, "bench_key", "bench_value_12345");
        _engine.GetMetadata(_smallObjId, "bench_key");
    }
}

[MemoryDiagnoser]
public class TransactionBenchmarks
{
    private string _path = null!;
    private ObjectEngine _engine = null!;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"bench_txn_{Guid.NewGuid():N}.dat");
        _engine = ObjectEngine.Create(_path);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        File.Delete(_path);
    }

    [Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(1000)]
    public void TransactionBatch(int batchSize)
    {
        _engine.BeginTransaction();
        for (int i = 0; i < batchSize; i++)
            _engine.CreateObject($"txn_{_counter++}");
        _engine.CommitTransaction();
    }
}

[MemoryDiagnoser]
public class MixedWorkloadBenchmarks
{
    private string _path = null!;
    private ObjectEngine _engine = null!;
    private List<ulong> _ids = null!;
    private byte[] _data = null!;
    private byte[] _readBuf = null!;
    private Random _rng = null!;

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"bench_mixed_{Guid.NewGuid():N}.dat");
        _engine = ObjectEngine.Create(_path);
        _data = new byte[256];
        _readBuf = new byte[256];
        Random.Shared.NextBytes(_data);
        _rng = new Random(42);

        // Pre-populate 1000 objects
        _ids = new List<ulong>(1000);
        _engine.BeginTransaction();
        for (int i = 0; i < 1000; i++)
        {
            var id = _engine.CreateObject($"mixed_{i}");
            _engine.Append(id, _data);
            _ids.Add(id);
        }
        _engine.CommitTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        File.Delete(_path);
    }

    [Benchmark(OperationsPerInvoke = 100)]
    public void MixedReadWrite_80_20()
    {
        for (int i = 0; i < 100; i++)
        {
            if (_rng.Next(100) < 80)
            {
                // Read
                var id = _ids[_rng.Next(_ids.Count)];
                _engine.ReadAt(id, 0, _readBuf);
            }
            else
            {
                // Write (append to random object)
                var id = _ids[_rng.Next(_ids.Count)];
                _engine.Append(id, _data);
            }
        }
    }

    [Benchmark(OperationsPerInvoke = 100)]
    public void MixedReadWrite_50_50()
    {
        for (int i = 0; i < 100; i++)
        {
            if (_rng.Next(100) < 50)
            {
                var id = _ids[_rng.Next(_ids.Count)];
                _engine.ReadAt(id, 0, _readBuf);
            }
            else
            {
                var id = _ids[_rng.Next(_ids.Count)];
                _engine.Append(id, _data);
            }
        }
    }
}
