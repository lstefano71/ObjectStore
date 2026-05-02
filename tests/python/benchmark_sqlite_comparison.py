#!/usr/bin/env python3
"""
Comparison benchmark: ObjectStore (C ABI) vs SQLite-backed object store.

Runs identical workloads against both backends and prints side-by-side results.
Usage:
    python benchmark_sqlite_comparison.py [--tmpdir PATH] [--quick]
"""

import argparse
import ctypes
import multiprocessing
import os
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(__file__))
from sqlite_object_store import SqliteObjectStore

# ---------------------------------------------------------------------------
# ObjectStore C ABI helpers (same as benchmark_multiprocess.py)
# ---------------------------------------------------------------------------

def find_native_lib():
    base = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    candidates = [
        os.path.join(base, "src", "ObjectStore.Native", "bin", "Release", "net10.0", "win-x64", "publish", "ObjectStore.Native.dll"),
        os.path.join(base, "src", "ObjectStore.Native", "bin", "Release", "net9.0", "win-x64", "publish", "ObjectStore.Native.dll"),
    ]
    for c in candidates:
        if os.path.exists(c):
            return c
    return None


def load_objstore_lib(lib_path):
    lib = ctypes.CDLL(lib_path)
    # Store management
    lib.objstore_open_or_create.argtypes = [ctypes.c_char_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    lib.objstore_open_or_create.restype = ctypes.c_int
    lib.objstore_close.argtypes = [ctypes.c_void_p]
    lib.objstore_close.restype = ctypes.c_int
    lib.objstore_refresh.argtypes = [ctypes.c_void_p]
    lib.objstore_refresh.restype = ctypes.c_int
    # Object operations
    lib.objstore_object_create.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.POINTER(ctypes.c_uint64)]
    lib.objstore_object_create.restype = ctypes.c_int
    lib.objstore_append.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int64]
    lib.objstore_append.restype = ctypes.c_int
    lib.objstore_read.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_int64, ctypes.c_void_p, ctypes.c_int64, ctypes.POINTER(ctypes.c_int64)]
    lib.objstore_read.restype = ctypes.c_int
    lib.objstore_object_delete.argtypes = [ctypes.c_void_p, ctypes.c_uint64]
    lib.objstore_object_delete.restype = ctypes.c_int
    # Transactions
    lib.objstore_txn_begin.argtypes = [ctypes.c_void_p]
    lib.objstore_txn_begin.restype = ctypes.c_int
    lib.objstore_txn_commit.argtypes = [ctypes.c_void_p]
    lib.objstore_txn_commit.restype = ctypes.c_int
    return lib


class ObjStoreWrapper:
    """Wrapper matching SqliteObjectStore interface for ObjectStore C ABI."""

    def __init__(self, path: str, lib_path: str):
        self.lib = load_objstore_lib(lib_path)
        self.handle = ctypes.c_void_p()
        self._read_buf = (ctypes.c_uint8 * (1024 * 1024))()  # Pre-allocate 1MB read buffer
        rc = self.lib.objstore_open_or_create(path.encode(), None, ctypes.byref(self.handle))
        if rc != 0:
            raise RuntimeError(f"objstore_open_or_create failed: {rc}")

    def close(self):
        if self.handle:
            self.lib.objstore_close(self.handle)
            self.handle = None

    def begin(self):
        rc = self.lib.objstore_txn_begin(self.handle)
        if rc != 0:
            raise RuntimeError(f"txn_begin failed: {rc}")

    def commit(self):
        rc = self.lib.objstore_txn_commit(self.handle)
        if rc != 0:
            raise RuntimeError(f"txn_commit failed: {rc}")

    def create_object(self, name=None):
        obj_id = ctypes.c_uint64()
        name_b = name.encode() if name else None
        rc = self.lib.objstore_object_create(self.handle, name_b, ctypes.byref(obj_id))
        if rc != 0:
            raise RuntimeError(f"object_create failed: {rc}")
        return obj_id.value

    def append(self, obj_id, data: bytes):
        buf = (ctypes.c_uint8 * len(data))(*data)
        rc = self.lib.objstore_append(self.handle, ctypes.c_uint64(obj_id), buf, ctypes.c_int64(len(data)))
        if rc != 0:
            raise RuntimeError(f"append failed: {rc}")

    def read(self, obj_id, buf=None) -> bytes:
        if buf is None:
            buf = self._read_buf
        bytes_read = ctypes.c_int64()
        rc = self.lib.objstore_read(self.handle, ctypes.c_uint64(obj_id), ctypes.c_int64(0),
                                    buf, ctypes.c_int64(ctypes.sizeof(buf)), ctypes.byref(bytes_read))
        if rc != 0:
            raise RuntimeError(f"read failed: {rc}")
        return bytes(buf[: bytes_read.value])

    def delete(self, obj_id):
        rc = self.lib.objstore_object_delete(self.handle, ctypes.c_uint64(obj_id))
        if rc != 0:
            raise RuntimeError(f"delete failed: {rc}")


# ---------------------------------------------------------------------------
# Benchmark runners
# ---------------------------------------------------------------------------

def bench_sequential_write(backend_factory, count, payload_size):
    """Create objects and append data sequentially."""
    store = backend_factory()
    payload = os.urandom(payload_size)
    try:
        start = time.perf_counter()
        for i in range(count):
            obj_id = store.create_object(f"obj_{i}")
            store.append(obj_id, payload)
        elapsed = time.perf_counter() - start
        return count / elapsed, elapsed
    finally:
        store.close()


def bench_sequential_read(backend_factory, count, payload_size):
    """Pre-populate then read sequentially."""
    store = backend_factory()
    payload = os.urandom(payload_size)
    ids = []
    try:
        # Setup
        if hasattr(store, 'begin'):
            store.begin()
        for i in range(count):
            obj_id = store.create_object(f"rd_{i}")
            store.append(obj_id, payload)
            ids.append(obj_id)
        if hasattr(store, 'commit'):
            store.commit()

        # Benchmark reads
        start = time.perf_counter()
        for obj_id in ids:
            data = store.read(obj_id)
            assert len(data) == payload_size
        elapsed = time.perf_counter() - start
        return count / elapsed, elapsed
    finally:
        store.close()


def bench_transaction_batch(backend_factory, count, payload_size):
    """Create N objects in a single transaction."""
    store = backend_factory()
    payload = os.urandom(payload_size)
    try:
        start = time.perf_counter()
        store.begin()
        for i in range(count):
            obj_id = store.create_object(f"batch_{i}")
            store.append(obj_id, payload)
        store.commit()
        elapsed = time.perf_counter() - start
        return count / elapsed, elapsed
    finally:
        store.close()


def _worker_write(args):
    """Worker for concurrent write benchmark."""
    backend_type, path, extra, worker_id, count, payload_size = args
    payload = os.urandom(payload_size)
    if backend_type == "sqlite":
        store = SqliteObjectStore(path)
    else:
        store = ObjStoreWrapper(path, extra)
    try:
        start = time.perf_counter()
        for i in range(count):
            obj_id = store.create_object(f"w{worker_id}_{i}")
            store.append(obj_id, payload)
        return time.perf_counter() - start
    finally:
        store.close()


def _worker_read(args):
    """Worker for concurrent read benchmark."""
    backend_type, path, extra, start_id, count, payload_size = args
    if backend_type == "sqlite":
        store = SqliteObjectStore(path)
    else:
        store = ObjStoreWrapper(path, extra)
    try:
        start = time.perf_counter()
        for i in range(count):
            obj_id = start_id + (i % count)
            try:
                data = store.read(obj_id)
            except:
                pass
        return time.perf_counter() - start
    finally:
        store.close()


def bench_concurrent_write(backend_type, path, extra, num_workers, ops_per_worker, payload_size):
    """Multi-process concurrent write."""
    args = [(backend_type, path, extra, w, ops_per_worker, payload_size) for w in range(num_workers)]
    start = time.perf_counter()
    with multiprocessing.Pool(num_workers) as pool:
        times = pool.map(_worker_write, args)
    elapsed = time.perf_counter() - start
    total_ops = num_workers * ops_per_worker
    return total_ops / elapsed, elapsed


def bench_concurrent_read(backend_type, path, extra, num_readers, ops_per_reader, payload_size):
    """Multi-process concurrent read (pre-populated)."""
    # Pre-populate
    if backend_type == "sqlite":
        store = SqliteObjectStore(path)
    else:
        store = ObjStoreWrapper(path, extra)
    payload = os.urandom(payload_size)
    store.begin()
    ids = []
    for i in range(ops_per_reader):
        obj_id = store.create_object(f"pre_{i}")
        store.append(obj_id, payload)
        ids.append(obj_id)
    store.commit()
    store.close()

    start_id = ids[0]
    args = [(backend_type, path, extra, start_id, ops_per_reader, payload_size) for _ in range(num_readers)]
    start = time.perf_counter()
    with multiprocessing.Pool(num_readers) as pool:
        times = pool.map(_worker_read, args)
    elapsed = time.perf_counter() - start
    total_ops = num_readers * ops_per_reader
    return total_ops / elapsed, elapsed


def _worker_mixed(args):
    """Worker for mixed read/write benchmark."""
    backend_type, path, extra, worker_id, count, payload_size, write_ratio = args
    payload = os.urandom(payload_size)
    if backend_type == "sqlite":
        store = SqliteObjectStore(path)
    else:
        store = ObjStoreWrapper(path, extra)
    try:
        # Create some objects to read from
        read_ids = []
        store.begin()
        for i in range(10):
            obj_id = store.create_object(f"mix_pre_{worker_id}_{i}")
            store.append(obj_id, payload)
            read_ids.append(obj_id)
        store.commit()

        import random
        rng = random.Random(worker_id)
        ops = 0
        start = time.perf_counter()
        for i in range(count):
            if rng.random() < write_ratio:
                obj_id = store.create_object(f"mix_w_{worker_id}_{i}")
                store.append(obj_id, payload)
            else:
                obj_id = rng.choice(read_ids)
                store.read(obj_id)
            ops += 1
        return time.perf_counter() - start, ops
    finally:
        store.close()


def bench_mixed(backend_type, path, extra, num_workers, ops_per_worker, payload_size, write_ratio):
    """Multi-process mixed workload."""
    args = [(backend_type, path, extra, w, ops_per_worker, payload_size, write_ratio)
            for w in range(num_workers)]
    start = time.perf_counter()
    with multiprocessing.Pool(num_workers) as pool:
        results = pool.map(_worker_mixed, args)
    elapsed = time.perf_counter() - start
    total_ops = sum(r[1] for r in results)
    return total_ops / elapsed, elapsed


# ---------------------------------------------------------------------------
# Main benchmark orchestrator
# ---------------------------------------------------------------------------

def run_comparison(lib_path, tmpdir, quick=False):
    payload_small = 256
    payload_large = 65536

    if quick:
        seq_count = 100
        batch_count = 200
        conc_ops = 25
        read_count = 500
    else:
        seq_count = 500
        batch_count = 500
        conc_ops = 100
        read_count = 2000

    results = []

    def run_scenario(name, bench_fn, *args, **kwargs):
        ops_sec, elapsed = bench_fn(*args, **kwargs)
        return (name, ops_sec, elapsed)

    def make_sqlite_factory(path):
        return lambda: SqliteObjectStore(path)

    def make_objstore_factory(path):
        return lambda: ObjStoreWrapper(path, lib_path)

    scenarios = [
        ("Sequential Write (256B)", bench_sequential_write, seq_count, payload_small),
        ("Sequential Write (64KB)", bench_sequential_write, seq_count, payload_large),
        ("Sequential Read (256B)", bench_sequential_read, read_count, payload_small),
        ("Sequential Read (64KB)", bench_sequential_read, read_count, payload_large),
        ("Transaction Batch (200x256B)", bench_transaction_batch, batch_count, payload_small),
    ]

    print("=" * 78)
    print("  ObjectStore vs SQLite Comparison Benchmark")
    print("=" * 78)
    print(f"  Library: {lib_path}")
    print(f"  Temp dir: {tmpdir}")
    print(f"  Mode: {'Quick' if quick else 'Full'}")
    print()

    # Single-process benchmarks
    print("--- Single-Process Benchmarks ---")
    print()
    print(f"  {'Scenario':<35} {'ObjectStore':>12} {'SQLite':>12} {'Ratio':>8}")
    print(f"  {'-'*35} {'-'*12} {'-'*12} {'-'*8}")

    for scenario_name, bench_fn, count, payload_size in scenarios:
        # ObjectStore
        obj_path = os.path.join(tmpdir, "bench_obj.db")
        if os.path.exists(obj_path):
            os.remove(obj_path)
        obj_factory = make_objstore_factory(obj_path)
        obj_ops, _ = bench_fn(obj_factory, count, payload_size)

        # SQLite
        sql_path = os.path.join(tmpdir, "bench_sql.db")
        for ext in ["", "-wal", "-shm"]:
            p = sql_path + ext
            if os.path.exists(p):
                os.remove(p)
        sql_factory = make_sqlite_factory(sql_path)
        sql_ops, _ = bench_fn(sql_factory, count, payload_size)

        ratio = obj_ops / sql_ops if sql_ops > 0 else float('inf')
        results.append((scenario_name, obj_ops, sql_ops, ratio))
        print(f"  {scenario_name:<35} {obj_ops:>9.0f}/s {sql_ops:>9.0f}/s {ratio:>7.2f}x")

        # Cleanup
        for f in [obj_path, sql_path, sql_path + "-wal", sql_path + "-shm"]:
            if os.path.exists(f):
                os.remove(f)

    # Multi-process benchmarks
    print()
    print("--- Multi-Process Benchmarks ---")
    print()
    print(f"  {'Scenario':<35} {'ObjectStore':>12} {'SQLite':>12} {'Ratio':>8}")
    print(f"  {'-'*35} {'-'*12} {'-'*12} {'-'*8}")

    worker_counts = [1, 2, 4] if quick else [1, 2, 4, 8]

    for num_workers in worker_counts:
        scenario_name = f"Concurrent Write ({num_workers}w, 256B)"

        # ObjectStore
        obj_path = os.path.join(tmpdir, "bench_obj_mp.db")
        if os.path.exists(obj_path):
            os.remove(obj_path)
        # Create the file first
        s = ObjStoreWrapper(obj_path, lib_path)
        s.close()
        obj_ops, _ = bench_concurrent_write("objstore", obj_path, lib_path, num_workers, conc_ops, payload_small)

        # SQLite
        sql_path = os.path.join(tmpdir, "bench_sql_mp.db")
        for ext in ["", "-wal", "-shm"]:
            p = sql_path + ext
            if os.path.exists(p):
                os.remove(p)
        s = SqliteObjectStore(sql_path)
        s.close()
        sql_ops, _ = bench_concurrent_write("sqlite", sql_path, None, num_workers, conc_ops, payload_small)

        ratio = obj_ops / sql_ops if sql_ops > 0 else float('inf')
        results.append((scenario_name, obj_ops, sql_ops, ratio))
        print(f"  {scenario_name:<35} {obj_ops:>9.0f}/s {sql_ops:>9.0f}/s {ratio:>7.2f}x")

        for f in [obj_path, sql_path, sql_path + "-wal", sql_path + "-shm"]:
            if os.path.exists(f):
                os.remove(f)

    for num_readers in worker_counts:
        scenario_name = f"Concurrent Read ({num_readers}r, 256B)"

        # ObjectStore
        obj_path = os.path.join(tmpdir, "bench_obj_mp.db")
        if os.path.exists(obj_path):
            os.remove(obj_path)
        s = ObjStoreWrapper(obj_path, lib_path)
        s.close()
        obj_ops, _ = bench_concurrent_read("objstore", obj_path, lib_path, num_readers, read_count // 2, payload_small)

        # SQLite
        sql_path = os.path.join(tmpdir, "bench_sql_mp.db")
        for ext in ["", "-wal", "-shm"]:
            p = sql_path + ext
            if os.path.exists(p):
                os.remove(p)
        s = SqliteObjectStore(sql_path)
        s.close()
        sql_ops, _ = bench_concurrent_read("sqlite", sql_path, None, num_readers, read_count // 2, payload_small)

        ratio = obj_ops / sql_ops if sql_ops > 0 else float('inf')
        results.append((scenario_name, obj_ops, sql_ops, ratio))
        print(f"  {scenario_name:<35} {obj_ops:>9.0f}/s {sql_ops:>9.0f}/s {ratio:>7.2f}x")

        for f in [obj_path, sql_path, sql_path + "-wal", sql_path + "-shm"]:
            if os.path.exists(f):
                os.remove(f)

    # Mixed workload
    for write_ratio, label in [(0.2, "20W/80R"), (0.5, "50W/50R")]:
        scenario_name = f"Mixed {label} (4w, 256B)"

        obj_path = os.path.join(tmpdir, "bench_obj_mix.db")
        if os.path.exists(obj_path):
            os.remove(obj_path)
        s = ObjStoreWrapper(obj_path, lib_path)
        s.close()
        obj_ops, _ = bench_mixed("objstore", obj_path, lib_path, 4, conc_ops, payload_small, write_ratio)

        sql_path = os.path.join(tmpdir, "bench_sql_mix.db")
        for ext in ["", "-wal", "-shm"]:
            p = sql_path + ext
            if os.path.exists(p):
                os.remove(p)
        s = SqliteObjectStore(sql_path)
        s.close()
        sql_ops, _ = bench_mixed("sqlite", sql_path, None, 4, conc_ops, payload_small, write_ratio)

        ratio = obj_ops / sql_ops if sql_ops > 0 else float('inf')
        results.append((scenario_name, obj_ops, sql_ops, ratio))
        print(f"  {scenario_name:<35} {obj_ops:>9.0f}/s {sql_ops:>9.0f}/s {ratio:>7.2f}x")

        for f in [obj_path, sql_path, sql_path + "-wal", sql_path + "-shm"]:
            if os.path.exists(f):
                os.remove(f)

    # Summary
    print()
    print("=" * 78)
    print("  Summary")
    print("=" * 78)
    print(f"  {'Scenario':<35} {'ObjectStore':>12} {'SQLite':>12} {'Winner':>10}")
    print(f"  {'-'*35} {'-'*12} {'-'*12} {'-'*10}")
    for name, obj_ops, sql_ops, ratio in results:
        winner = "ObjStore" if ratio >= 1.0 else "SQLite"
        ratio_str = f"{ratio:.2f}x" if ratio >= 1.0 else f"{1/ratio:.2f}x"
        print(f"  {name:<35} {obj_ops:>9.0f}/s {sql_ops:>9.0f}/s {winner:>6} {ratio_str}")

    return results


if __name__ == "__main__":
    multiprocessing.freeze_support()

    parser = argparse.ArgumentParser(description="ObjectStore vs SQLite comparison benchmark")
    parser.add_argument("--tmpdir", default=None, help="Directory for temp DB files")
    parser.add_argument("--quick", action="store_true", help="Fewer iterations for faster feedback")
    parser.add_argument("--lib", default=None, help="Path to ObjectStore.Native.dll")
    args = parser.parse_args()

    lib_path = args.lib or find_native_lib()
    if not lib_path or not os.path.exists(lib_path):
        print("ERROR: Cannot find ObjectStore.Native.dll. Use --lib to specify path.")
        sys.exit(1)

    tmpdir = args.tmpdir or tempfile.gettempdir()
    os.makedirs(tmpdir, exist_ok=True)

    run_comparison(lib_path, tmpdir, quick=args.quick)
