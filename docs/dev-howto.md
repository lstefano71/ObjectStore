# Developer How-To

Quick-reference for running tests and benchmarks.

## Prerequisites

- .NET 10 SDK
- Python 3.x (with `pytest` installed)
- Visual Studio C compiler (MSVC via vcvars64.bat)

MSVC setup (open a new terminal or run once per session):

```cmd
"D:\Program Files\Microsoft Visual Studio\2026\VC\Auxiliary\Build\vcvars64.bat"
```

---

## 1. Python Tests

Three test files in `tests/python/`:

| File | Description |
|------|-------------|
| `test_objstore.py` | Core FFI integration tests |
| `test_comprehensive.py` | Extended feature coverage |
| `test_multiprocess.py` | Multi-process concurrency tests |

**Run all Python tests:**

```powershell
cd tests/python
python -m pytest test_objstore.py test_comprehensive.py test_multiprocess.py -v
```

**Run a single file or test:**

```powershell
python -m pytest test_objstore.py -v
python -m pytest test_multiprocess.py::TestMultiProcessCorrectness::test_high_contention_many_writers -v
```

**Notes:**
- Tests require the NativeAOT DLL. They auto-discover it from `publish_native/` or `tests/c_bench/`.
- The multiprocess high-contention test can occasionally fail (~1-2%) due to lock contention timing; re-run to confirm.
- Use `-x` to stop on first failure, `-q` for quiet output.

---

## 2. C Benchmarks

Located in `tests/c_bench/`. Two programs comparing ObjectStore (via NativeAOT DLL) against SQLite.

### Build

First, publish the NativeAOT DLL:

```powershell
dotnet publish src/ObjectStore.Native -c Release -r win-x64 -o publish_native
```

Then compile the C benchmarks (requires vcvars64.bat in PATH):

```cmd
cd tests\c_bench
build.bat
```

Or manually:

```cmd
cl /O2 bench_objstore.c /Fe:bench_objstore.exe
cl /O2 /DSQLITE_THREADSAFE=0 /DSQLITE_OMIT_LOAD_EXTENSION bench_sqlite.c sqlite3.c /Fe:bench_sqlite.exe
```

bench_objstore should be compared to bench_sqlite to evaluate ObjectStore performance against SQLite on the same workload.

### Mixed workload benchmarks (bench2)

```cmd
cl /O2 bench2_objstore.c /Fe:bench2_objstore.exe
cl /O2 /DSQLITE_THREADSAFE=0 /DSQLITE_OMIT_LOAD_EXTENSION bench2_sqlite.c sqlite3.c /Fe:bench2_sqlite.exe
```

bench2 tests a more realistic workload: seed 100K objects (256B–16KB), 50K random reads with content verification, and a mixed phase with writes every 100 reads. ObjectStore uses `MetadataOnly` checksum policy for a fair comparison with SQLite (which does no per-blob checksum on reads).

### Run

```powershell
cd tests/c_bench

# Original sequential benchmarks
.\bench_objstore.exe "..\..\publish_native\ObjectStore.Native.dll" "C:\temp"
.\bench_sqlite.exe "C:\temp"

# Mixed workload benchmarks
.\bench2_objstore.exe "..\..\publish_native\ObjectStore.Native.dll" "C:\temp"
.\bench2_sqlite.exe "C:\temp"
```

**Notes:**
- Use `C:\temp` (SSD) for consistent results. `D:\temp` is HDD.
- ObjectStore runs in MultiProcessMode (AutoRefresh) by default.
- bench_objstore/bench_sqlite test: Sequential Write 256B/64KB, Sequential Read 256B/64KB, Txn Batch 256B/64KB.
- bench2_objstore/bench2_sqlite test: Seed 100K, Random Read 50K, Mixed R+W 50K.

---

## 3. C# Benchmarks (BenchmarkDotNet)

Located in `tests/ObjectStore.Benchmarks/`.

### Run

```powershell
cd tests/ObjectStore.Benchmarks
dotnet run -c Release
```

Or run specific benchmarks:

```powershell
dotnet run -c Release -- --filter "*BatchInsert*"
```

**Notes:**
- Uses BenchmarkDotNet — runs warmup iterations, multiple invocations, and reports statistical results.
- Output goes to `BenchmarkDotNet.Artifacts/` in the project directory.
- Does NOT require the NativeAOT DLL (uses managed ObjectStore directly).

---

## 4. Unit Tests (C# / xUnit)

```powershell
# Run all unit tests
dotnet test tests/ObjectStore.Tests

# Quick (no rebuild)
dotnet test tests/ObjectStore.Tests --no-build -v q

# Filter by name
dotnet test tests/ObjectStore.Tests --filter "FullyQualifiedName~Transaction"
```

---

## 5. Checksum Policy (Performance Tuning)

ObjectStore validates block checksums (xxHash3) on every disk read by default. For read-heavy workloads this can dominate CPU time. A configurable `ChecksumPolicy` controls validation behavior:

| Policy | Behavior |
|--------|----------|
| `Always` (default) | Validate checksum on every disk read. Safest. |
| `MetadataOnly` | Validate B-tree/metadata blocks. Skip checksum for object data blocks. |
| `OnFirstRead` | Validate on first disk read; skip on subsequent reads of the same block address. |
| `None` | Never validate. Fastest, least safe. |

### C# API

```csharp
var options = new ObjectStoreOptions { ChecksumPolicy = ChecksumPolicy.MetadataOnly };
using var db = ObjectStoreDatabase.Open("store.dat", options);
```

### C API (via NativeAOT DLL)

```c
#define OBJSTORE_CHECKSUM_ALWAYS      0
#define OBJSTORE_CHECKSUM_METADATA    1
#define OBJSTORE_CHECKSUM_FIRST_READ  2
#define OBJSTORE_CHECKSUM_NONE        3

objstore_options_set_checksum_policy(opts, OBJSTORE_CHECKSUM_METADATA);
```

**Recommendation:** Use `MetadataOnly` for benchmarks and read-heavy production workloads where data integrity is ensured at write time. Use `Always` when reading untrusted or shared stores.

---

## 6. Important notes

Drive C: is an SSD, D: is an HDD. Using both allows testing performance differences between storage types.