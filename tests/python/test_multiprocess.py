"""
Multi-process integration test for ObjectStore.

Tests true multi-process concurrent access: multiple independent processes
open the same database file, read and write simultaneously, with file locks
serializing commits.

Architecture:
- Worker processes load the native DLL independently
- Each worker creates objects, writes data, refreshes, and reads back
- Communication via JSON lines on stdout
- Orchestrator spawns workers, collects results, verifies correctness
"""
import ctypes
import hashlib
import json
import os
import platform
import signal
import subprocess
import sys
import tempfile
import time
import uuid

import pytest


def _find_native_lib():
    """Locate the ObjectStore native library."""
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if platform.system() == "Windows":
        path = os.path.join(repo_root, "src", "ObjectStore.Native", "bin", "Release",
                            "net10.0", "win-x64", "publish", "ObjectStore.Native.dll")
    else:
        path = os.path.join(repo_root, "src", "ObjectStore.Native", "bin", "Release",
                            "net10.0", "linux-x64", "publish", "ObjectStore.Native.so")
    if not os.path.exists(path):
        pytest.skip("Native library not found. Run 'dotnet publish' first.")
    return path


def _temp_path():
    return os.path.join(tempfile.gettempdir(), f"objstore_mp_{uuid.uuid4().hex}.dat")


# Path to the worker script
WORKER_SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "worker_multiprocess.py")


def _spawn_worker(db_path, worker_id, action, extra_args=None):
    """Spawn a worker subprocess."""
    cmd = [
        sys.executable, WORKER_SCRIPT,
        "--db", db_path,
        "--worker-id", str(worker_id),
        "--action", action,
        "--lib", _find_native_lib(),
    ]
    if extra_args:
        cmd.extend(extra_args)
    return subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )


def _collect_results(procs, timeout=60):
    """Wait for all processes and collect JSON results."""
    results = []
    for proc in procs:
        try:
            stdout, stderr = proc.communicate(timeout=timeout)
            result = {
                "returncode": proc.returncode,
                "stdout": stdout,
                "stderr": stderr,
                "lines": [],
            }
            for line in stdout.strip().splitlines():
                try:
                    result["lines"].append(json.loads(line))
                except json.JSONDecodeError:
                    pass
            results.append(result)
        except subprocess.TimeoutExpired:
            proc.kill()
            stdout, stderr = proc.communicate()
            results.append({
                "returncode": -1,
                "stdout": stdout,
                "stderr": stderr,
                "lines": [],
                "timeout": True,
            })
    return results


def _create_db(lib_path, db_path):
    """Create a fresh database using the C API."""
    lib = ctypes.CDLL(lib_path)
    handle = ctypes.c_void_p()
    path_bytes = db_path.encode("utf-8")
    rc = lib.objstore_open_or_create(path_bytes, None, ctypes.byref(handle))
    assert rc == 0, f"Failed to create DB: rc={rc}"
    lib.objstore_close(handle)


class TestMultiProcessCorrectness:
    """Tests verifying data correctness under multi-process concurrent access."""

    def setup_method(self):
        self.db_path = _temp_path()
        self.lib_path = _find_native_lib()
        _create_db(self.lib_path, self.db_path)

    def teardown_method(self):
        if os.path.exists(self.db_path):
            try:
                os.remove(self.db_path)
            except OSError:
                pass

    def test_concurrent_writers_no_data_loss(self):
        """N workers each write M objects. All objects verifiable after completion."""
        num_workers = 4
        objects_per_worker = 25

        procs = []
        for i in range(num_workers):
            p = _spawn_worker(
                self.db_path, i, "write_objects",
                ["--count", str(objects_per_worker)]
            )
            procs.append(p)

        results = _collect_results(procs, timeout=120)

        # All workers should succeed
        for i, r in enumerate(results):
            assert r["returncode"] == 0, (
                f"Worker {i} failed: rc={r['returncode']}, stderr={r['stderr']}"
            )

        # Collect all created object IDs
        all_ids = []
        for r in results:
            for line in r["lines"]:
                if line.get("type") == "created":
                    all_ids.append(line["id"])

        expected_count = num_workers * objects_per_worker
        assert len(all_ids) == expected_count, (
            f"Expected {expected_count} objects, got {len(all_ids)}"
        )

        # Verify all objects exist and have correct data
        verify_proc = _spawn_worker(
            self.db_path, 99, "verify_objects",
            ["--ids", json.dumps(all_ids)]
        )
        verify_results = _collect_results([verify_proc], timeout=60)
        assert verify_results[0]["returncode"] == 0, (
            f"Verify failed: {verify_results[0]['stderr']}"
        )
        for line in verify_results[0]["lines"]:
            if line.get("type") == "verify_result":
                assert line["all_ok"], f"Verification failed: {line}"

    def test_reader_sees_committed_writes_after_refresh(self):
        """Writer commits, reader refreshes and sees the new data."""
        # Writer creates objects
        writer = _spawn_worker(
            self.db_path, 0, "write_and_signal",
            ["--count", "10"]
        )
        writer_results = _collect_results([writer], timeout=30)
        assert writer_results[0]["returncode"] == 0, (
            f"Writer failed: {writer_results[0]['stderr']}"
        )

        # Get the IDs that were written
        written_ids = []
        for line in writer_results[0]["lines"]:
            if line.get("type") == "created":
                written_ids.append(line["id"])

        assert len(written_ids) == 10

        # Now spawn a reader that refreshes and verifies
        reader = _spawn_worker(
            self.db_path, 1, "refresh_and_read",
            ["--ids", json.dumps(written_ids)]
        )
        reader_results = _collect_results([reader], timeout=30)
        assert reader_results[0]["returncode"] == 0, (
            f"Reader failed: {reader_results[0]['stderr']}"
        )
        for line in reader_results[0]["lines"]:
            if line.get("type") == "read_result":
                assert line["all_found"], f"Reader couldn't find all objects: {line}"

    def test_interleaved_read_write_hybrid_clients(self):
        """Workers alternate between reading and writing, verifying consistency."""
        num_workers = 4
        rounds = 10

        procs = []
        for i in range(num_workers):
            p = _spawn_worker(
                self.db_path, i, "hybrid_read_write",
                ["--rounds", str(rounds)]
            )
            procs.append(p)

        results = _collect_results(procs, timeout=120)

        for i, r in enumerate(results):
            assert r["returncode"] == 0, (
                f"Worker {i} failed: rc={r['returncode']}, stderr={r['stderr']}"
            )

        # Check all workers reported success
        for r in results:
            for line in r["lines"]:
                if line.get("type") == "summary":
                    assert line["errors"] == 0, f"Worker reported errors: {line}"

    def test_high_contention_many_writers(self):
        """8 workers writing simultaneously — stress test lock contention."""
        num_workers = 8
        objects_per_worker = 10

        procs = []
        for i in range(num_workers):
            p = _spawn_worker(
                self.db_path, i, "write_objects",
                ["--count", str(objects_per_worker)]
            )
            procs.append(p)

        results = _collect_results(procs, timeout=120)

        for i, r in enumerate(results):
            errors = [l for l in r["lines"] if l.get("type") == "error"]
            assert r["returncode"] == 0, (
                f"Worker {i} failed: rc={r['returncode']}, "
                f"errors={errors}, stdout={r['stdout'][:500]}, stderr={r['stderr']}"
            )

        # Count total objects created
        total = sum(
            1 for r in results for line in r["lines"]
            if line.get("type") == "created"
        )
        assert total == num_workers * objects_per_worker


class TestCrashResilience:
    """Tests verifying the database remains consistent after process crashes."""

    def setup_method(self):
        self.db_path = _temp_path()
        self.lib_path = _find_native_lib()
        _create_db(self.lib_path, self.db_path)

    def teardown_method(self):
        if os.path.exists(self.db_path):
            try:
                os.remove(self.db_path)
            except OSError:
                pass

    def test_killed_writer_file_remains_valid(self):
        """Kill a writer mid-work; file should still be openable and consistent."""
        # Start a writer that writes slowly (with delays)
        writer = _spawn_worker(
            self.db_path, 0, "slow_writer",
            ["--count", "100", "--delay-ms", "10"]
        )

        # Let it run for a bit then kill it
        time.sleep(0.5)
        writer.kill()
        writer.wait()

        # File should still be valid — open and verify
        verify = _spawn_worker(self.db_path, 1, "verify_db_valid")
        verify_results = _collect_results([verify], timeout=30)
        assert verify_results[0]["returncode"] == 0, (
            f"DB validation failed after crash: {verify_results[0]['stderr']}"
        )
        for line in verify_results[0]["lines"]:
            if line.get("type") == "validation":
                assert line["valid"], f"DB invalid: {line}"

    def test_survivors_continue_after_crash(self):
        """One writer crashes, others continue without issue."""
        # Start 3 normal workers + 1 that will be killed
        procs = []
        for i in range(3):
            p = _spawn_worker(
                self.db_path, i, "write_objects",
                ["--count", "15"]
            )
            procs.append(p)

        # Start a slow writer that we'll kill
        victim = _spawn_worker(
            self.db_path, 99, "slow_writer",
            ["--count", "1000", "--delay-ms", "20"]
        )

        # Let everything run a bit
        time.sleep(0.3)
        victim.kill()
        victim.wait()

        # Normal workers should still finish
        results = _collect_results(procs, timeout=120)
        for i, r in enumerate(results):
            assert r["returncode"] == 0, (
                f"Surviving worker {i} failed after crash: rc={r['returncode']}, "
                f"stderr={r['stderr']}"
            )


class TestPerformance:
    """Measure throughput under contention (not strict pass/fail, but informational)."""

    def setup_method(self):
        self.db_path = _temp_path()
        self.lib_path = _find_native_lib()
        _create_db(self.lib_path, self.db_path)

    def teardown_method(self):
        if os.path.exists(self.db_path):
            try:
                os.remove(self.db_path)
            except OSError:
                pass

    def test_write_throughput(self):
        """Measure objects/sec with 4 concurrent writers."""
        num_workers = 4
        objects_per_worker = 50

        start = time.time()
        procs = []
        for i in range(num_workers):
            p = _spawn_worker(
                self.db_path, i, "write_objects",
                ["--count", str(objects_per_worker)]
            )
            procs.append(p)

        results = _collect_results(procs, timeout=120)
        elapsed = time.time() - start

        for i, r in enumerate(results):
            assert r["returncode"] == 0, (
                f"Worker {i} failed: {r['stderr']}"
            )

        total_objects = num_workers * objects_per_worker
        throughput = total_objects / elapsed
        print(f"\n  Write throughput: {throughput:.1f} objects/sec "
              f"({total_objects} objects, {num_workers} writers, {elapsed:.2f}s)")

    def test_read_throughput_with_writer(self):
        """Measure read performance while a writer is active."""
        # First, seed some data
        seed = _spawn_worker(self.db_path, 0, "write_objects", ["--count", "50"])
        seed_results = _collect_results([seed], timeout=60)
        assert seed_results[0]["returncode"] == 0

        seed_ids = [
            line["id"] for line in seed_results[0]["lines"]
            if line.get("type") == "created"
        ]

        # Start a background writer
        writer = _spawn_worker(
            self.db_path, 1, "slow_writer",
            ["--count", "200", "--delay-ms", "5"]
        )

        # Start readers
        start = time.time()
        readers = []
        for i in range(4):
            r = _spawn_worker(
                self.db_path, 10 + i, "read_objects",
                ["--ids", json.dumps(seed_ids), "--iterations", "3"]
            )
            readers.append(r)

        reader_results = _collect_results(readers, timeout=60)
        elapsed = time.time() - start

        # Kill the writer (it may still be running)
        writer.kill()
        writer.wait()

        for i, r in enumerate(reader_results):
            assert r["returncode"] == 0, (
                f"Reader {i} failed: {r['stderr']}"
            )

        total_reads = len(seed_ids) * 3 * 4  # ids × iterations × readers
        throughput = total_reads / elapsed
        print(f"\n  Read throughput: {throughput:.1f} reads/sec "
              f"({total_reads} reads, 4 readers + 1 writer, {elapsed:.2f}s)")
