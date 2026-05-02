"""
ObjectStore C API integration tests using Python ctypes.
This file grows incrementally with each implementation phase.

Phase 1: objstore_get_version
Phase 2: options handle
Phase 3: store lifecycle + object CRUD + read/write
Phase 4: transactions
Phase 5: SIEVE cache (transparent, tested via workload)
Phase 6: compression & encryption options
"""
import ctypes
import os
import sys
import platform
import tempfile
import uuid

import pytest


def _find_native_lib():
    """Locate the ObjectStore native library."""
    # Look relative to repository root (tests/python/ -> tests/ -> repo root)
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if platform.system() == "Windows":
        candidates = [
            os.path.join(repo_root, "src", "ObjectStore.Native", "bin", "Release",
                         "net10.0", "win-x64", "publish", "ObjectStore.Native.dll"),
        ]
    else:
        candidates = [
            os.path.join(repo_root, "src", "ObjectStore.Native", "bin", "Release",
                         "net10.0", "linux-x64", "publish", "ObjectStore.Native.so"),
        ]

    for path in candidates:
        if os.path.exists(path):
            return path

    pytest.skip("Native library not found. Run 'dotnet publish' first.")


@pytest.fixture(scope="session")
def lib():
    """Load the ObjectStore native library."""
    path = _find_native_lib()
    return ctypes.CDLL(path)


def _temp_path():
    """Generate a temp file path for a test store."""
    return os.path.join(tempfile.gettempdir(), f"objstore_pytest_{uuid.uuid4().hex}.dat")


# ============================================================
# Phase 1 Tests
# ============================================================

class TestPhase1Version:
    def test_get_version_returns_ok(self, lib):
        major = ctypes.c_int32()
        minor = ctypes.c_int32()
        rc = lib.objstore_get_version(ctypes.byref(major), ctypes.byref(minor))
        assert rc == 0, f"objstore_get_version failed with rc={rc}"

    def test_get_version_values(self, lib):
        major = ctypes.c_int32()
        minor = ctypes.c_int32()
        lib.objstore_get_version(ctypes.byref(major), ctypes.byref(minor))
        assert major.value == 1
        assert minor.value == 0

    def test_get_version_null_pointer_returns_error(self, lib):
        # Passing NULL for outMajor
        major = ctypes.c_int32()
        rc = lib.objstore_get_version(None, ctypes.byref(major))
        assert rc == -5  # OBJSTORE_ERR_INVALID_ARG


# ============================================================
# Phase 2 Tests — Options Handle
# ============================================================

class TestPhase2Options:
    def test_options_create_and_free(self, lib):
        opts = ctypes.c_void_p()
        rc = lib.objstore_options_create(ctypes.byref(opts))
        assert rc == 0
        assert opts.value is not None

        lib.objstore_options_free(opts)

    def test_options_set_read_only(self, lib):
        opts = ctypes.c_void_p()
        lib.objstore_options_create(ctypes.byref(opts))

        rc = lib.objstore_options_set_read_only(opts, 1)
        assert rc == 0

        lib.objstore_options_free(opts)

    def test_options_set_shared_access(self, lib):
        opts = ctypes.c_void_p()
        lib.objstore_options_create(ctypes.byref(opts))

        rc = lib.objstore_options_set_shared_access(opts, 1)
        assert rc == 0

        lib.objstore_options_free(opts)

    def test_options_set_cache_max_bytes(self, lib):
        opts = ctypes.c_void_p()
        lib.objstore_options_create(ctypes.byref(opts))

        rc = lib.objstore_options_set_cache_max_bytes(opts, 64 * 1024 * 1024)
        assert rc == 0

        lib.objstore_options_free(opts)

    def test_options_set_lock_timeout(self, lib):
        opts = ctypes.c_void_p()
        lib.objstore_options_create(ctypes.byref(opts))

        rc = lib.objstore_options_set_lock_timeout_ms(opts, 5000)
        assert rc == 0

        lib.objstore_options_free(opts)

    def test_options_invalid_handle(self, lib):
        rc = lib.objstore_options_set_read_only(ctypes.c_void_p(0), 1)
        assert rc == -5  # OBJSTORE_ERR_INVALID_ARG


# ============================================================
# Phase 3 Tests — Store Lifecycle + Object CRUD + Read/Write
# ============================================================

class TestPhase3Store:
    def test_create_and_close(self, lib):
        path = _temp_path()
        try:
            handle = ctypes.c_void_p()
            rc = lib.objstore_create(path.encode("utf-8"), None, ctypes.byref(handle))
            assert rc == 0, f"objstore_create failed: {rc}"
            assert handle.value is not None

            rc = lib.objstore_close(handle)
            assert rc == 0
        finally:
            if os.path.exists(path):
                os.remove(path)

    def test_open_existing(self, lib):
        path = _temp_path()
        try:
            # Create first
            handle = ctypes.c_void_p()
            lib.objstore_create(path.encode("utf-8"), None, ctypes.byref(handle))
            lib.objstore_close(handle)

            # Re-open
            handle2 = ctypes.c_void_p()
            rc = lib.objstore_open(path.encode("utf-8"), None, ctypes.byref(handle2))
            assert rc == 0
            lib.objstore_close(handle2)
        finally:
            if os.path.exists(path):
                os.remove(path)

    def test_open_or_create_new(self, lib):
        path = _temp_path()
        try:
            handle = ctypes.c_void_p()
            rc = lib.objstore_open_or_create(path.encode("utf-8"), None, ctypes.byref(handle))
            assert rc == 0
            lib.objstore_close(handle)
            assert os.path.exists(path)
        finally:
            if os.path.exists(path):
                os.remove(path)

    def test_open_nonexistent_fails(self, lib):
        path = _temp_path()
        handle = ctypes.c_void_p()
        rc = lib.objstore_open(path.encode("utf-8"), None, ctypes.byref(handle))
        assert rc != 0  # Should fail

    def test_close_null_handle(self, lib):
        rc = lib.objstore_close(ctypes.c_void_p(0))
        assert rc == -5  # INVALID_ARG


class TestPhase3Objects:
    @pytest.fixture(autouse=True)
    def setup_store(self, lib):
        self.path = _temp_path()
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path.encode("utf-8"), None, ctypes.byref(self.handle))
        assert rc == 0
        yield
        lib.objstore_close(self.handle)
        if os.path.exists(self.path):
            os.remove(self.path)

    def test_object_create(self, lib):
        obj_id = ctypes.c_uint64()
        rc = lib.objstore_object_create(self.handle, b"test-obj", ctypes.byref(obj_id))
        assert rc == 0
        assert obj_id.value > 0

    def test_object_exists(self, lib):
        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"exists-obj", ctypes.byref(obj_id))

        exists = ctypes.c_int32()
        rc = lib.objstore_object_exists(self.handle, obj_id, ctypes.byref(exists))
        assert rc == 0
        assert exists.value == 1

    def test_object_delete(self, lib):
        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"del-obj", ctypes.byref(obj_id))

        rc = lib.objstore_object_delete(self.handle, obj_id)
        assert rc == 0

        exists = ctypes.c_int32()
        lib.objstore_object_exists(self.handle, obj_id, ctypes.byref(exists))
        assert exists.value == 0

    def test_object_delete_nonexistent(self, lib):
        rc = lib.objstore_object_delete(self.handle, ctypes.c_uint64(9999))
        assert rc == -2  # NOT_FOUND

    def test_append_and_read(self, lib):
        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"rw-obj", ctypes.byref(obj_id))

        # Write data
        data = b"Hello, ObjectStore!"
        rc = lib.objstore_append(self.handle, obj_id, data, len(data))
        assert rc == 0

        # Check size
        size = ctypes.c_int64()
        rc = lib.objstore_object_get_size(self.handle, obj_id, ctypes.byref(size))
        assert rc == 0
        assert size.value == len(data)

        # Read back
        buf = ctypes.create_string_buffer(len(data))
        bytes_read = ctypes.c_int32()
        rc = lib.objstore_read(self.handle, obj_id, ctypes.c_int64(0),
                               buf, len(data), ctypes.byref(bytes_read))
        assert rc == 0
        assert bytes_read.value == len(data)
        assert buf.raw == data

    def test_write_at_overwrite(self, lib):
        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"overwrite-obj", ctypes.byref(obj_id))

        # Initial data
        data = b"Hello, World!"
        lib.objstore_append(self.handle, obj_id, data, len(data))

        # Overwrite "World!" with "Earth!"
        new_data = b"Earth!"
        rc = lib.objstore_write(self.handle, obj_id, ctypes.c_int64(7), new_data, len(new_data))
        assert rc == 0

        # Read back
        buf = ctypes.create_string_buffer(13)
        bytes_read = ctypes.c_int32()
        lib.objstore_read(self.handle, obj_id, ctypes.c_int64(0),
                          buf, 13, ctypes.byref(bytes_read))
        assert buf.raw == b"Hello, Earth!"

    def test_truncate(self, lib):
        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"trunc-obj", ctypes.byref(obj_id))

        data = b"Long data that will be truncated"
        lib.objstore_append(self.handle, obj_id, data, len(data))

        rc = lib.objstore_truncate(self.handle, obj_id, ctypes.c_int64(9))
        assert rc == 0

        size = ctypes.c_int64()
        lib.objstore_object_get_size(self.handle, obj_id, ctypes.byref(size))
        assert size.value == 9

    def test_multiple_objects(self, lib):
        """Create multiple objects and verify they're independent."""
        ids = []
        for i in range(5):
            obj_id = ctypes.c_uint64()
            name = f"obj-{i}".encode("utf-8")
            lib.objstore_object_create(self.handle, name, ctypes.byref(obj_id))
            ids.append(obj_id.value)

            content = f"Content for object {i}".encode("utf-8")
            lib.objstore_append(self.handle, obj_id, content, len(content))

        # Verify each object
        for i, oid in enumerate(ids):
            expected = f"Content for object {i}".encode("utf-8")
            buf = ctypes.create_string_buffer(len(expected))
            bytes_read = ctypes.c_int32()
            rc = lib.objstore_read(self.handle, ctypes.c_uint64(oid), ctypes.c_int64(0),
                                   buf, len(expected), ctypes.byref(bytes_read))
            assert rc == 0
            assert buf.raw == expected


# ============================================================
# Phase 4 Tests — Transactions
# ============================================================

class TestPhase4Transactions:
    @pytest.fixture(autouse=True)
    def setup_store(self, lib):
        self.path = _temp_path()
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path.encode("utf-8"), None, ctypes.byref(self.handle))
        assert rc == 0
        yield
        lib.objstore_close(self.handle)
        if os.path.exists(self.path):
            os.remove(self.path)

    def test_txn_commit_persists(self, lib):
        rc = lib.objstore_txn_begin(self.handle)
        assert rc == 0

        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"txn-obj", ctypes.byref(obj_id))
        data = b"transaction data"
        lib.objstore_append(self.handle, obj_id, data, len(data))

        rc = lib.objstore_txn_commit(self.handle)
        assert rc == 0

        # Verify exists
        exists = ctypes.c_int32()
        lib.objstore_object_exists(self.handle, obj_id, ctypes.byref(exists))
        assert exists.value == 1

    def test_txn_rollback_reverts(self, lib):
        rc = lib.objstore_txn_begin(self.handle)
        assert rc == 0

        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"rollback-obj", ctypes.byref(obj_id))
        data = b"will be rolled back"
        lib.objstore_append(self.handle, obj_id, data, len(data))

        rc = lib.objstore_txn_rollback(self.handle)
        assert rc == 0

        # Verify gone
        exists = ctypes.c_int32()
        lib.objstore_object_exists(self.handle, obj_id, ctypes.byref(exists))
        assert exists.value == 0

    def test_txn_rollback_preserves_preexisting(self, lib):
        # Create an object before transaction
        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"pre-obj", ctypes.byref(obj_id))
        data = b"keep this"
        lib.objstore_append(self.handle, obj_id, data, len(data))

        # Start transaction, create another, rollback
        lib.objstore_txn_begin(self.handle)
        obj_id2 = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"discard-obj", ctypes.byref(obj_id2))
        lib.objstore_txn_rollback(self.handle)

        # Pre-existing object still there
        exists = ctypes.c_int32()
        lib.objstore_object_exists(self.handle, obj_id, ctypes.byref(exists))
        assert exists.value == 1

        buf = ctypes.create_string_buffer(len(data))
        bytes_read = ctypes.c_int32()
        lib.objstore_read(self.handle, obj_id, ctypes.c_int64(0),
                          buf, len(data), ctypes.byref(bytes_read))
        assert buf.raw == data

    def test_nested_savepoint(self, lib):
        lib.objstore_txn_begin(self.handle)
        obj_id1 = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"outer-obj", ctypes.byref(obj_id1))

        # Nested
        lib.objstore_txn_begin(self.handle)
        obj_id2 = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"inner-obj", ctypes.byref(obj_id2))
        lib.objstore_txn_rollback(self.handle)  # rollback inner only

        lib.objstore_txn_commit(self.handle)  # commit outer

        # Outer object exists, inner doesn't
        exists1 = ctypes.c_int32()
        lib.objstore_object_exists(self.handle, obj_id1, ctypes.byref(exists1))
        assert exists1.value == 1

        exists2 = ctypes.c_int32()
        lib.objstore_object_exists(self.handle, obj_id2, ctypes.byref(exists2))
        assert exists2.value == 0


# ============================================================
# Phase 6 Tests — Compression & Encryption Options
# ============================================================

class TestPhase6Options:
    def test_set_compression_deflate(self, lib):
        opts = ctypes.c_void_p()
        lib.objstore_options_create(ctypes.byref(opts))
        rc = lib.objstore_options_set_compression(opts, 1)  # Deflate
        assert rc == 0
        lib.objstore_options_free(opts)

    def test_set_compression_brotli(self, lib):
        opts = ctypes.c_void_p()
        lib.objstore_options_create(ctypes.byref(opts))
        rc = lib.objstore_options_set_compression(opts, 2)  # Brotli
        assert rc == 0
        lib.objstore_options_free(opts)

    def test_set_compression_invalid(self, lib):
        opts = ctypes.c_void_p()
        lib.objstore_options_create(ctypes.byref(opts))
        rc = lib.objstore_options_set_compression(opts, 99)
        assert rc == -5  # INVALID_ARG
        lib.objstore_options_free(opts)

    def test_set_encryption_key(self, lib):
        opts = ctypes.c_void_p()
        lib.objstore_options_create(ctypes.byref(opts))
        key = bytes(range(32))  # 32-byte key
        rc = lib.objstore_options_set_encryption_key(opts, key, len(key))
        assert rc == 0
        lib.objstore_options_free(opts)

    def test_clear_encryption_key(self, lib):
        opts = ctypes.c_void_p()
        lib.objstore_options_create(ctypes.byref(opts))
        # Set a key
        key = bytes(range(32))
        lib.objstore_options_set_encryption_key(opts, key, len(key))
        # Clear it
        rc = lib.objstore_options_set_encryption_key(opts, None, 0)
        assert rc == 0
        lib.objstore_options_free(opts)


# ============================================================
# Phase 7 Tests — Metadata, Iterator, Stats
# ============================================================

class TestPhase7Metadata:
    @pytest.fixture(autouse=True)
    def setup(self, lib, tmp_path):
        self.lib = lib
        self.path = str(tmp_path / "phase7_meta.db").encode("utf-8")
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path, None, ctypes.byref(self.handle))
        assert rc == 0
        # Create an object
        self.obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"meta-obj", ctypes.byref(self.obj_id))
        yield
        lib.objstore_close(self.handle)

    def test_set_and_get_metadata(self):
        lib = self.lib
        rc = lib.objstore_metadata_set(self.handle, self.obj_id, b"author", b"Alice")
        assert rc == 0

        # Get length first (two-call pattern)
        out_len = ctypes.c_int32()
        rc = lib.objstore_metadata_get(self.handle, self.obj_id, b"author", None, 0, ctypes.byref(out_len))
        assert rc == 0
        assert out_len.value == 5  # "Alice"

        # Get value
        buf = ctypes.create_string_buffer(out_len.value)
        rc = lib.objstore_metadata_get(self.handle, self.obj_id, b"author", buf, out_len.value, ctypes.byref(out_len))
        assert rc == 0
        assert buf.raw == b"Alice"

    def test_get_missing_key(self):
        lib = self.lib
        out_len = ctypes.c_int32()
        rc = lib.objstore_metadata_get(self.handle, self.obj_id, b"nope", None, 0, ctypes.byref(out_len))
        assert rc == -2  # NOT_FOUND

    def test_delete_metadata(self):
        lib = self.lib
        lib.objstore_metadata_set(self.handle, self.obj_id, b"key", b"val")
        rc = lib.objstore_metadata_delete(self.handle, self.obj_id, b"key")
        assert rc == 0

        # Verify gone
        out_len = ctypes.c_int32()
        rc = lib.objstore_metadata_get(self.handle, self.obj_id, b"key", None, 0, ctypes.byref(out_len))
        assert rc == -2

    def test_buffer_too_small(self):
        lib = self.lib
        lib.objstore_metadata_set(self.handle, self.obj_id, b"k", b"longvalue")
        buf = ctypes.create_string_buffer(3)  # too small for "longvalue" (9 bytes)
        out_len = ctypes.c_int32()
        rc = lib.objstore_metadata_get(self.handle, self.obj_id, b"k", buf, 3, ctypes.byref(out_len))
        assert rc == -6  # BUFFER_TOO_SMALL
        assert out_len.value == 9


class TestPhase7Iterator:
    @pytest.fixture(autouse=True)
    def setup(self, lib, tmp_path):
        self.lib = lib
        self.path = str(tmp_path / "phase7_iter.db").encode("utf-8")
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path, None, ctypes.byref(self.handle))
        assert rc == 0
        yield
        lib.objstore_close(self.handle)

    def test_iterate_objects(self):
        lib = self.lib
        # Create 3 objects with data
        ids = []
        for name in [b"obj-a", b"obj-b", b"obj-c"]:
            obj_id = ctypes.c_uint64()
            lib.objstore_object_create(self.handle, name, ctypes.byref(obj_id))
            data = b"x" * 50
            lib.objstore_append(self.handle, obj_id, data, 50)
            ids.append(obj_id.value)

        # Iterate
        iter_handle = ctypes.c_void_p()
        rc = lib.objstore_list_begin(self.handle, ctypes.byref(iter_handle))
        assert rc == 0

        found_ids = []
        while True:
            out_id = ctypes.c_uint64()
            out_size = ctypes.c_int64()
            rc = lib.objstore_iter_next(iter_handle, ctypes.byref(out_id), ctypes.byref(out_size))
            if rc == 1:  # end
                break
            assert rc == 0
            found_ids.append(out_id.value)
            assert out_size.value == 50

        assert sorted(found_ids) == sorted(ids)

        rc = lib.objstore_iter_close(iter_handle)
        assert rc == 0

    def test_iterate_empty_store(self):
        lib = self.lib
        iter_handle = ctypes.c_void_p()
        rc = lib.objstore_list_begin(self.handle, ctypes.byref(iter_handle))
        assert rc == 0

        out_id = ctypes.c_uint64()
        out_size = ctypes.c_int64()
        rc = lib.objstore_iter_next(iter_handle, ctypes.byref(out_id), ctypes.byref(out_size))
        assert rc == 1  # end immediately

        lib.objstore_iter_close(iter_handle)


class TestPhase7Stats:
    @pytest.fixture(autouse=True)
    def setup(self, lib, tmp_path):
        self.lib = lib
        self.path = str(tmp_path / "phase7_stats.db").encode("utf-8")
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path, None, ctypes.byref(self.handle))
        assert rc == 0
        yield
        lib.objstore_close(self.handle)

    def test_get_stats(self):
        lib = self.lib
        # Create 2 objects with known sizes
        id1 = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"s1", ctypes.byref(id1))
        lib.objstore_append(self.handle, id1, b"a" * 100, 100)

        id2 = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"s2", ctypes.byref(id2))
        lib.objstore_append(self.handle, id2, b"b" * 200, 200)

        count = ctypes.c_int32()
        total_size = ctypes.c_int64()
        file_size = ctypes.c_int64()
        rc = lib.objstore_get_stats(self.handle, ctypes.byref(count), ctypes.byref(total_size), ctypes.byref(file_size))
        assert rc == 0
        assert count.value == 2
        assert total_size.value == 300
        assert file_size.value > 0


# ============================================================
# Phase 8 Tests — Defragmentation & Recovery
# ============================================================

class TestPhase8Defragment:
    @pytest.fixture(autouse=True)
    def setup(self, lib, tmp_path):
        self.lib = lib
        self.path = str(tmp_path / "phase8_defrag.db").encode("utf-8")
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path, None, ctypes.byref(self.handle))
        assert rc == 0
        yield
        lib.objstore_close(self.handle)

    def test_defragment_basic(self):
        lib = self.lib
        # Create objects, delete some, defragment
        # Use 1024 bytes to exceed inline threshold (512B) and force extent-based storage
        ids = []
        for i in range(5):
            obj_id = ctypes.c_uint64()
            lib.objstore_object_create(self.handle, f"obj{i}".encode(), ctypes.byref(obj_id))
            lib.objstore_append(self.handle, obj_id, b"x" * 1024, 1024)
            ids.append(obj_id.value)

        # Delete odd-indexed
        lib.objstore_object_delete(self.handle, ids[1])
        lib.objstore_object_delete(self.handle, ids[3])

        # Defragment
        out_count = ctypes.c_int32()
        rc = lib.objstore_defragment(self.handle, ctypes.byref(out_count))
        assert rc == 0
        assert out_count.value >= 2

        # Verify remaining objects still readable
        for idx in [0, 2, 4]:
            buf = ctypes.create_string_buffer(1024)
            bytes_read = ctypes.c_int32()
            rc = lib.objstore_read(self.handle, ids[idx], 0, buf, 1024, ctypes.byref(bytes_read))
            assert rc == 0
            assert bytes_read.value == 1024

    def test_recover_clean_store(self):
        lib = self.lib
        # Create an object
        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(self.handle, b"rec-obj", ctypes.byref(obj_id))
        lib.objstore_append(self.handle, obj_id, b"data", 4)

        # Recover on clean store
        out_needed = ctypes.c_int32()
        rc = lib.objstore_recover(self.handle, ctypes.byref(out_needed))
        assert rc == 0

        # Verify data still intact
        buf = ctypes.create_string_buffer(4)
        bytes_read = ctypes.c_int32()
        rc = lib.objstore_read(self.handle, obj_id, 0, buf, 4, ctypes.byref(bytes_read))
        assert rc == 0
        assert buf.raw == b"data"


# ============================================================
# Phase 9 Tests — Read-Only Mode
# ============================================================

class TestPhase9ReadOnly:
    def test_open_readonly_can_read(self, lib, tmp_path):
        path = str(tmp_path / "phase9_ro.db").encode("utf-8")

        # Create store and write data
        handle = ctypes.c_void_p()
        rc = lib.objstore_create(path, None, ctypes.byref(handle))
        assert rc == 0
        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(handle, b"ro-obj", ctypes.byref(obj_id))
        lib.objstore_append(handle, obj_id, b"readonly-data", 13)
        lib.objstore_close(handle)

        # Open read-only
        ro_handle = ctypes.c_void_p()
        rc = lib.objstore_open_readonly(path, None, ctypes.byref(ro_handle))
        assert rc == 0

        # Read data
        buf = ctypes.create_string_buffer(13)
        bytes_read = ctypes.c_int32()
        rc = lib.objstore_read(ro_handle, obj_id, 0, buf, 13, ctypes.byref(bytes_read))
        assert rc == 0
        assert bytes_read.value == 13
        assert buf.raw == b"readonly-data"

        lib.objstore_close(ro_handle)

    def test_readonly_rejects_write(self, lib, tmp_path):
        path = str(tmp_path / "phase9_ro_wr.db").encode("utf-8")

        # Create store
        handle = ctypes.c_void_p()
        lib.objstore_create(path, None, ctypes.byref(handle))
        obj_id = ctypes.c_uint64()
        lib.objstore_object_create(handle, b"obj", ctypes.byref(obj_id))
        lib.objstore_close(handle)

        # Open read-only
        ro_handle = ctypes.c_void_p()
        lib.objstore_open_readonly(path, None, ctypes.byref(ro_handle))

        # Try to create an object (should fail)
        new_id = ctypes.c_uint64()
        rc = lib.objstore_object_create(ro_handle, b"fail", ctypes.byref(new_id))
        assert rc == -4  # READONLY

        lib.objstore_close(ro_handle)

    def test_readonly_can_iterate(self, lib, tmp_path):
        path = str(tmp_path / "phase9_ro_iter.db").encode("utf-8")

        # Create store with objects
        handle = ctypes.c_void_p()
        lib.objstore_create(path, None, ctypes.byref(handle))
        for name in [b"a", b"b", b"c"]:
            obj_id = ctypes.c_uint64()
            lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        lib.objstore_close(handle)

        # Open read-only and iterate
        ro_handle = ctypes.c_void_p()
        lib.objstore_open_readonly(path, None, ctypes.byref(ro_handle))

        iter_h = ctypes.c_void_p()
        rc = lib.objstore_list_begin(ro_handle, ctypes.byref(iter_h))
        assert rc == 0

        count = 0
        while True:
            out_id = ctypes.c_uint64()
            out_size = ctypes.c_int64()
            rc = lib.objstore_iter_next(iter_h, ctypes.byref(out_id), ctypes.byref(out_size))
            if rc == 1:
                break
            count += 1
        assert count == 3

        lib.objstore_iter_close(iter_h)
        lib.objstore_close(ro_handle)


# ============================================================
# Phase 10 Tests — Node Hierarchy
# ============================================================

class TestPhase10Hierarchy:
    @pytest.fixture(autouse=True)
    def setup(self, lib, tmp_path):
        self.lib = lib
        self.path = str(tmp_path / "phase10_hier.db").encode("utf-8")
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path, None, ctypes.byref(self.handle))
        assert rc == 0
        yield
        lib.objstore_close(self.handle)

    def test_create_child_and_resolve_path(self):
        lib = self.lib
        # Create folder under root (ID=1)
        folder_id = ctypes.c_uint64()
        rc = lib.objstore_create_child(self.handle, 1, b"myfolder", 1, ctypes.byref(folder_id))
        assert rc == 0

        # Resolve path
        resolved_id = ctypes.c_uint64()
        rc = lib.objstore_resolve_path(self.handle, b"/myfolder", ctypes.byref(resolved_id))
        assert rc == 0
        assert resolved_id.value == folder_id.value

    def test_nested_hierarchy(self):
        lib = self.lib
        # Create /parent/child
        parent_id = ctypes.c_uint64()
        lib.objstore_create_child(self.handle, 1, b"parent", 1, ctypes.byref(parent_id))

        child_id = ctypes.c_uint64()
        lib.objstore_create_child(self.handle, parent_id, b"child", 0, ctypes.byref(child_id))

        # Resolve /parent/child
        resolved = ctypes.c_uint64()
        rc = lib.objstore_resolve_path(self.handle, b"/parent/child", ctypes.byref(resolved))
        assert rc == 0
        assert resolved.value == child_id.value

    def test_list_children(self):
        lib = self.lib
        # Create children under root
        ids = []
        for name in [b"alpha", b"beta", b"gamma"]:
            child_id = ctypes.c_uint64()
            lib.objstore_create_child(self.handle, 1, name, 0, ctypes.byref(child_id))
            ids.append(child_id.value)

        # List children of root
        iter_h = ctypes.c_void_p()
        rc = lib.objstore_list_children_begin(self.handle, 1, ctypes.byref(iter_h))
        assert rc == 0

        found = []
        while True:
            out_id = ctypes.c_uint64()
            out_size = ctypes.c_int64()
            rc = lib.objstore_iter_next(iter_h, ctypes.byref(out_id), ctypes.byref(out_size))
            if rc == 1:
                break
            found.append(out_id.value)

        assert sorted(found) == sorted(ids)
        lib.objstore_iter_close(iter_h)

    def test_move_node(self):
        lib = self.lib
        folder1 = ctypes.c_uint64()
        lib.objstore_create_child(self.handle, 1, b"src", 1, ctypes.byref(folder1))
        folder2 = ctypes.c_uint64()
        lib.objstore_create_child(self.handle, 1, b"dst", 1, ctypes.byref(folder2))
        file_id = ctypes.c_uint64()
        lib.objstore_create_child(self.handle, folder1, b"file", 0, ctypes.byref(file_id))

        # Move file from src to dst
        rc = lib.objstore_move_node(self.handle, file_id, folder2, None)
        assert rc == 0

        # Verify new path
        resolved = ctypes.c_uint64()
        rc = lib.objstore_resolve_path(self.handle, b"/dst/file", ctypes.byref(resolved))
        assert rc == 0
        assert resolved.value == file_id.value

        # Old path gone
        rc = lib.objstore_resolve_path(self.handle, b"/src/file", ctypes.byref(resolved))
        assert rc == -2  # NOT_FOUND

    def test_delete_subtree(self):
        lib = self.lib
        folder_id = ctypes.c_uint64()
        lib.objstore_create_child(self.handle, 1, b"todelete", 1, ctypes.byref(folder_id))
        child_id = ctypes.c_uint64()
        lib.objstore_create_child(self.handle, folder_id, b"inner", 0, ctypes.byref(child_id))

        # Delete subtree
        rc = lib.objstore_delete_subtree(self.handle, folder_id)
        assert rc == 0

        # Both gone
        resolved = ctypes.c_uint64()
        rc = lib.objstore_resolve_path(self.handle, b"/todelete", ctypes.byref(resolved))
        assert rc == -2
        exists = ctypes.c_int32()
        lib.objstore_object_exists(self.handle, child_id, ctypes.byref(exists))
        assert exists.value == 0

    def test_get_root_id(self):
        lib = self.lib
        root_id = ctypes.c_uint64()
        rc = lib.objstore_get_root_id(self.handle, ctypes.byref(root_id))
        assert rc == 0
        assert root_id.value == 1
