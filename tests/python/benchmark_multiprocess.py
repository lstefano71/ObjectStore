"""
ObjectStore Multi-Process Benchmarks

Standalone benchmark script that measures throughput and latency for
single-process and multi-process scenarios via the C ABI (NativeAOT DLL).

Usage:
    python benchmark_multiprocess.py [--lib PATH] [--quick]

Reports structured results: ops/sec, MB/s, latency, scaling efficiency.
"""
import argparse
import ctypes
import json
import multiprocessing
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path


# ─── Native library helpers ───────────────────────────────────────────────────

def find_native_lib():
    """Locate the ObjectStore native DLL."""
    candidates = [
        Path(__file__).parent.parent.parent / "src" / "ObjectStore.Native" / "bin" / "Release" / "net10.0" / "win-x64" / "publish" / "ObjectStore.Native.dll",
        Path(__file__).parent.parent.parent / "src" / "ObjectStore.Native" / "bin" / "Release" / "net10.0" / "win-x64" / "native" / "ObjectStore.Native.dll",
    ]
    for p in candidates:
        if p.exists():
            return str(p)
    # Try publish directory pattern
    base = Path(__file__).parent.parent.parent / "src" / "ObjectStore.Native" / "bin"
    for dll in base.rglob("ObjectStore.Native.dll"):
        if "publish" in str(dll) or "native" in str(dll):
            return str(dll)
    raise FileNotFoundError("Cannot find ObjectStore.Native.dll. Run: dotnet publish src/ObjectStore.Native -c Release -r win-x64 --self-contained")


def load_lib(lib_path):
    """Load and return the native library."""
    return ctypes.CDLL(lib_path)


def create_db(lib, db_path):
    """Create a fresh database."""
    handle = ctypes.c_void_p()
    rc = lib.objstore_open_or_create(db_path.encode("utf-8"), None, ctypes.byref(handle))
    if rc != 0:
        raise RuntimeError(f"Failed to create DB: rc={rc}")
    lib.objstore_close(handle)


def open_db(lib, db_path):
    """Open existing database, return handle."""
    handle = ctypes.c_void_p()
    rc = lib.objstore_open_or_create(db_path.encode("utf-8"), None, ctypes.byref(handle))
    if rc != 0:
        raise RuntimeError(f"Failed to open DB: rc={rc}")
    return handle


def close_db(lib, handle):
    lib.objstore_close(handle)


def refresh_db(lib, handle):
    lib.objstore_refresh(handle)


# ─── Benchmark utilities ──────────────────────────────────────────────────────

class BenchmarkResult:
    def __init__(self, name, total_ops, elapsed_sec, total_bytes=0, workers=1):
        self.name = name
        self.total_ops = total_ops
        self.elapsed_sec = elapsed_sec
        self.total_bytes = total_bytes
        self.workers = workers

    @property
    def ops_per_sec(self):
        return self.total_ops / self.elapsed_sec if self.elapsed_sec > 0 else 0

    @property
    def mb_per_sec(self):
        if self.total_bytes == 0:
            return 0
        return (self.total_bytes / (1024 * 1024)) / self.elapsed_sec

    def __str__(self):
        parts = [f"  {self.name}:"]
        parts.append(f"    {self.ops_per_sec:,.0f} ops/sec")
        if self.total_bytes > 0:
            parts.append(f"    {self.mb_per_sec:.2f} MB/s")
        parts.append(f"    ({self.total_ops:,} ops in {self.elapsed_sec:.3f}s, {self.workers} worker(s))")
        return "\n".join(parts)


def temp_db_path(tmpdir=None):
    base = tmpdir or tempfile.gettempdir()
    return os.path.join(base, f"bench_{os.getpid()}_{time.time_ns()}.dat")


# ─── Worker process entry point (for multiprocessing) ─────────────────────────

def worker_write(lib_path, db_path, worker_id, count, data_size):
    """Worker: create and append objects. Returns (ops, bytes, elapsed)."""
    lib = load_lib(lib_path)
    handle = open_db(lib, db_path)
    data = os.urandom(data_size)
    data_buf = (ctypes.c_uint8 * data_size)(*data)

    start = time.perf_counter()
    for i in range(count):
        obj_id = ctypes.c_uint64()
        name = f"w{worker_id}_{i}".encode("utf-8")
        rc = lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        if rc != 0:
            continue
        rc = lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(data_size))
    elapsed = time.perf_counter() - start

    close_db(lib, handle)
    return (count, count * data_size, elapsed)


def worker_read(lib_path, db_path, worker_id, obj_ids, iterations):
    """Worker: read objects repeatedly. Returns (ops, bytes, elapsed)."""
    lib = load_lib(lib_path)
    handle = open_db(lib, db_path)
    refresh_db(lib, handle)

    total_ops = 0
    total_bytes = 0

    start = time.perf_counter()
    for _ in range(iterations):
        for obj_id in obj_ids:
            size = ctypes.c_int64()
            rc = lib.objstore_object_get_size(handle, ctypes.c_uint64(obj_id), ctypes.byref(size))
            if rc != 0 or size.value <= 0:
                continue
            buf = (ctypes.c_uint8 * size.value)()
            bytes_read = ctypes.c_int64()
            rc = lib.objstore_read(
                handle, ctypes.c_uint64(obj_id), ctypes.c_int64(0),
                buf, ctypes.c_int64(size.value), ctypes.byref(bytes_read)
            )
            if rc == 0:
                total_ops += 1
                total_bytes += bytes_read.value
    elapsed = time.perf_counter() - start

    close_db(lib, handle)
    return (total_ops, total_bytes, elapsed)


def worker_mixed(lib_path, db_path, worker_id, obj_ids, ops_count, write_ratio, data_size):
    """Worker: mixed read/write. Returns (reads, writes, bytes_read, bytes_written, elapsed)."""
    import random
    lib = load_lib(lib_path)
    handle = open_db(lib, db_path)
    refresh_db(lib, handle)

    data = os.urandom(data_size)
    data_buf = (ctypes.c_uint8 * data_size)(*data)
    rng = random.Random(worker_id)

    reads = 0
    writes = 0
    bytes_read = 0
    bytes_written = 0

    start = time.perf_counter()
    for i in range(ops_count):
        if rng.random() < write_ratio:
            # Write: create new object
            obj_id = ctypes.c_uint64()
            name = f"mx{worker_id}_{i}".encode("utf-8")
            rc = lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
            if rc == 0:
                lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(data_size))
                writes += 1
                bytes_written += data_size
        else:
            # Read: random existing object
            if obj_ids:
                oid = rng.choice(obj_ids)
                refresh_db(lib, handle)
                size = ctypes.c_int64()
                rc = lib.objstore_object_get_size(handle, ctypes.c_uint64(oid), ctypes.byref(size))
                if rc == 0 and size.value > 0:
                    buf = (ctypes.c_uint8 * size.value)()
                    br = ctypes.c_int64()
                    rc = lib.objstore_read(
                        handle, ctypes.c_uint64(oid), ctypes.c_int64(0),
                        buf, ctypes.c_int64(size.value), ctypes.byref(br)
                    )
                    if rc == 0:
                        reads += 1
                        bytes_read += br.value

    elapsed = time.perf_counter() - start
    close_db(lib, handle)
    return (reads, writes, bytes_read, bytes_written, elapsed)


# ─── Single-process benchmarks ────────────────────────────────────────────────

def bench_sequential_write(lib_path, count, data_size, tmpdir=None):
    """Single-process sequential write benchmark."""
    db_path = temp_db_path(tmpdir)
    lib = load_lib(lib_path)
    create_db(lib, db_path)
    handle = open_db(lib, db_path)

    data = os.urandom(data_size)
    data_buf = (ctypes.c_uint8 * data_size)(*data)

    start = time.perf_counter()
    for i in range(count):
        obj_id = ctypes.c_uint64()
        name = f"seq_{i}".encode("utf-8")
        lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(data_size))
    elapsed = time.perf_counter() - start

    close_db(lib, handle)
    os.remove(db_path)
    return BenchmarkResult(
        f"Sequential Write ({data_size}B payload)",
        count, elapsed, count * data_size
    )


def bench_sequential_read(lib_path, count, data_size, tmpdir=None):
    """Single-process sequential read benchmark (pre-populated DB)."""
    db_path = temp_db_path(tmpdir)
    lib = load_lib(lib_path)
    create_db(lib, db_path)
    handle = open_db(lib, db_path)

    # Populate
    data = os.urandom(data_size)
    data_buf = (ctypes.c_uint8 * data_size)(*data)
    obj_ids = []
    for i in range(count):
        obj_id = ctypes.c_uint64()
        name = f"rd_{i}".encode("utf-8")
        lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(data_size))
        obj_ids.append(obj_id.value)

    # Benchmark reads
    buf = (ctypes.c_uint8 * data_size)()
    bytes_read_out = ctypes.c_int64()

    start = time.perf_counter()
    for oid in obj_ids:
        lib.objstore_read(
            handle, ctypes.c_uint64(oid), ctypes.c_int64(0),
            buf, ctypes.c_int64(data_size), ctypes.byref(bytes_read_out)
        )
    elapsed = time.perf_counter() - start

    close_db(lib, handle)
    os.remove(db_path)
    return BenchmarkResult(
        f"Sequential Read ({data_size}B payload)",
        count, elapsed, count * data_size
    )


def bench_transaction_batch(lib_path, batch_size, data_size, tmpdir=None):
    """Single-process: create N objects in one transaction."""
    db_path = temp_db_path(tmpdir)
    lib = load_lib(lib_path)
    create_db(lib, db_path)
    handle = open_db(lib, db_path)

    data = os.urandom(data_size)
    data_buf = (ctypes.c_uint8 * data_size)(*data)

    start = time.perf_counter()
    lib.objstore_txn_begin(handle)
    for i in range(batch_size):
        obj_id = ctypes.c_uint64()
        name = f"txn_{i}".encode("utf-8")
        lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(data_size))
    lib.objstore_txn_commit(handle)
    elapsed = time.perf_counter() - start

    close_db(lib, handle)
    os.remove(db_path)
    return BenchmarkResult(
        f"Transaction Batch ({batch_size} objects, {data_size}B each)",
        batch_size, elapsed, batch_size * data_size
    )


# ─── Multi-process benchmarks ─────────────────────────────────────────────────

def bench_concurrent_write(lib_path, num_workers, objects_per_worker, data_size, tmpdir=None):
    """Multi-process concurrent write benchmark."""
    db_path = temp_db_path(tmpdir)
    lib = load_lib(lib_path)
    create_db(lib, db_path)
    close_db(lib, open_db(lib, db_path))  # ensure file exists properly

    with multiprocessing.Pool(num_workers) as pool:
        start = time.perf_counter()
        results = pool.starmap(worker_write, [
            (lib_path, db_path, i, objects_per_worker, data_size)
            for i in range(num_workers)
        ])
        elapsed = time.perf_counter() - start

    total_ops = sum(r[0] for r in results)
    total_bytes = sum(r[1] for r in results)

    os.remove(db_path)
    return BenchmarkResult(
        f"Concurrent Write ({num_workers} workers, {data_size}B)",
        total_ops, elapsed, total_bytes, num_workers
    )


def bench_concurrent_read(lib_path, num_readers, objects_count, data_size, iterations, tmpdir=None):
    """Multi-process concurrent read benchmark."""
    db_path = temp_db_path(tmpdir)
    lib = load_lib(lib_path)
    create_db(lib, db_path)
    handle = open_db(lib, db_path)

    # Populate
    data = os.urandom(data_size)
    data_buf = (ctypes.c_uint8 * data_size)(*data)
    obj_ids = []
    for i in range(objects_count):
        obj_id = ctypes.c_uint64()
        name = f"cr_{i}".encode("utf-8")
        lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(data_size))
        obj_ids.append(obj_id.value)
    close_db(lib, handle)

    with multiprocessing.Pool(num_readers) as pool:
        start = time.perf_counter()
        results = pool.starmap(worker_read, [
            (lib_path, db_path, i, obj_ids, iterations)
            for i in range(num_readers)
        ])
        elapsed = time.perf_counter() - start

    total_ops = sum(r[0] for r in results)
    total_bytes = sum(r[1] for r in results)

    os.remove(db_path)
    return BenchmarkResult(
        f"Concurrent Read ({num_readers} readers, {data_size}B)",
        total_ops, elapsed, total_bytes, num_readers
    )


def bench_mixed_workload(lib_path, num_workers, ops_per_worker, write_ratio, data_size, tmpdir=None):
    """Multi-process mixed read/write benchmark."""
    db_path = temp_db_path(tmpdir)
    lib = load_lib(lib_path)
    create_db(lib, db_path)
    handle = open_db(lib, db_path)

    # Pre-populate some objects for reading
    data = os.urandom(data_size)
    data_buf = (ctypes.c_uint8 * data_size)(*data)
    obj_ids = []
    for i in range(100):
        obj_id = ctypes.c_uint64()
        name = f"pre_{i}".encode("utf-8")
        lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(data_size))
        obj_ids.append(obj_id.value)
    close_db(lib, handle)

    with multiprocessing.Pool(num_workers) as pool:
        start = time.perf_counter()
        results = pool.starmap(worker_mixed, [
            (lib_path, db_path, i, obj_ids, ops_per_worker, write_ratio, data_size)
            for i in range(num_workers)
        ])
        elapsed = time.perf_counter() - start

    total_reads = sum(r[0] for r in results)
    total_writes = sum(r[1] for r in results)
    total_bytes = sum(r[2] + r[3] for r in results)

    os.remove(db_path)
    return BenchmarkResult(
        f"Mixed {int(write_ratio*100)}W/{int((1-write_ratio)*100)}R ({num_workers} workers, {data_size}B)",
        total_reads + total_writes, elapsed, total_bytes, num_workers
    )


# ─── Main ─────────────────────────────────────────────────────────────────────

def run_benchmarks(lib_path, quick=False, tmpdir=None):
    """Run all benchmarks and print results."""
    results = []

    # Scale factors
    if quick:
        single_count = 200
        multi_count = 50
        read_iters = 3
    else:
        single_count = 1000
        multi_count = 200
        read_iters = 10

    effective_tmpdir = tmpdir or tempfile.gettempdir()
    print("=" * 70)
    print("  ObjectStore Benchmarks")
    print("=" * 70)
    print(f"  Mode: {'Quick' if quick else 'Full'}")
    print(f"  Library: {lib_path}")
    print(f"  Temp dir: {effective_tmpdir}")
    print(f"  CPU cores: {multiprocessing.cpu_count()}")
    print()

    # ─── Single-process ───
    print("─── Single-Process Benchmarks ───")
    print()

    r = bench_sequential_write(lib_path, single_count, 256, tmpdir)
    results.append(r); print(r); print()

    r = bench_sequential_write(lib_path, single_count // 4, 65536, tmpdir)
    results.append(r); print(r); print()

    r = bench_sequential_read(lib_path, single_count, 256, tmpdir)
    results.append(r); print(r); print()

    r = bench_sequential_read(lib_path, single_count // 4, 65536, tmpdir)
    results.append(r); print(r); print()

    r = bench_transaction_batch(lib_path, single_count, 256, tmpdir)
    results.append(r); print(r); print()

    # ─── Multi-process write scaling ───
    print("─── Multi-Process Write Scaling ───")
    print()

    baseline_ops = None
    for n_workers in [1, 2, 4, 8]:
        r = bench_concurrent_write(lib_path, n_workers, multi_count, 256, tmpdir)
        results.append(r)
        if baseline_ops is None:
            baseline_ops = r.ops_per_sec
        efficiency = (r.ops_per_sec / baseline_ops) * 100 if baseline_ops else 0
        print(r)
        print(f"    Scaling efficiency: {efficiency:.0f}% vs single-worker")
        print()

    # ─── Multi-process read scaling ───
    print("─── Multi-Process Read Scaling ───")
    print()

    baseline_ops = None
    for n_readers in [1, 2, 4, 8]:
        r = bench_concurrent_read(lib_path, n_readers, 200, 256, read_iters, tmpdir)
        results.append(r)
        if baseline_ops is None:
            baseline_ops = r.ops_per_sec
        efficiency = (r.ops_per_sec / baseline_ops) * 100 if baseline_ops else 0
        print(r)
        print(f"    Scaling efficiency: {efficiency:.0f}% vs single-reader")
        print()

    # ─── Mixed workload ───
    print("─── Multi-Process Mixed Workload ───")
    print()

    r = bench_mixed_workload(lib_path, 4, multi_count, 0.2, 256, tmpdir)
    results.append(r); print(r); print()

    r = bench_mixed_workload(lib_path, 4, multi_count, 0.5, 256, tmpdir)
    results.append(r); print(r); print()

    r = bench_mixed_workload(lib_path, 8, multi_count, 0.2, 256, tmpdir)
    results.append(r); print(r); print()

    # ─── Large object throughput ───
    print("─── Large Object Throughput ───")
    print()

    r = bench_sequential_write(lib_path, 50 if quick else 200, 1048576, tmpdir)
    results.append(r); print(r); print()

    r = bench_sequential_read(lib_path, 50 if quick else 200, 1048576, tmpdir)
    results.append(r); print(r); print()

    # ─── Summary table ───
    print("=" * 70)
    print("  Summary")
    print("=" * 70)
    print(f"  {'Benchmark':<55} {'ops/sec':>10} {'MB/s':>8}")
    print(f"  {'-'*55} {'-'*10} {'-'*8}")
    for r in results:
        mb = f"{r.mb_per_sec:.1f}" if r.total_bytes > 0 else "-"
        print(f"  {r.name:<55} {r.ops_per_sec:>10,.0f} {mb:>8}")
    print()

    return results


if __name__ == "__main__":
    multiprocessing.freeze_support()

    parser = argparse.ArgumentParser(description="ObjectStore multi-process benchmarks")
    parser.add_argument("--lib", type=str, help="Path to native library DLL")
    parser.add_argument("--quick", action="store_true", help="Run with reduced iterations")
    parser.add_argument("--tmpdir", type=str, help="Directory for temp DB files (controls target disk)")
    args = parser.parse_args()

    lib_path = args.lib or find_native_lib()
    run_benchmarks(lib_path, quick=args.quick, tmpdir=args.tmpdir)
