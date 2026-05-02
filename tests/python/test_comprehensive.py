"""
Comprehensive Python FFI integration tests — Batch 5.
Exercises advanced scenarios: MVCC, gap writes, read-only via options,
multi-handle lifecycle, large data, many objects.
"""
import ctypes
import hashlib
import os
import platform
import tempfile
import uuid

import pytest


def _find_native_lib():
    """Locate the ObjectStore native library."""
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
    return os.path.join(tempfile.gettempdir(), f"objstore_comprehensive_{uuid.uuid4().hex}.dat")


# Error codes
OK = 0
ERR = -1
NOT_FOUND = -2
EXISTS = -3
READ_ONLY = -4
INVALID_ARG = -5
BUFFER_TOO_SMALL = -6
NO_MORE_ITEMS = -7


class TestGapWriteRejection:
    """Fix 10: Gap writes (offset > size) should be rejected via C API."""

    @pytest.fixture(autouse=True)
    def setup(self, lib):
        self.lib = lib
        self.path = _temp_path()
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path.encode("utf-8"), None, ctypes.byref(self.handle))
        assert rc == OK
        yield
        lib.objstore_close(self.handle)
        if os.path.exists(self.path):
            os.remove(self.path)

    def test_gap_write_returns_invalid_arg(self):
        # Create an object
        obj_id = ctypes.c_uint64()
        rc = self.lib.objstore_object_create(self.handle, b"gapobj", ctypes.byref(obj_id))
        assert rc == OK

        # Write initial data (50 bytes)
        data = (ctypes.c_byte * 50)(*([0x41] * 50))
        rc = self.lib.objstore_write(self.handle, obj_id, 0, data, 50)
        assert rc == OK

        # Try gap write at offset 100 (size is 50) → should fail
        gap_data = (ctypes.c_byte * 10)(*([0x42] * 10))
        rc = self.lib.objstore_write(self.handle, obj_id, 100, gap_data, 10)
        assert rc == INVALID_ARG

    def test_append_at_size_succeeds(self):
        obj_id = ctypes.c_uint64()
        rc = self.lib.objstore_object_create(self.handle, b"appendobj", ctypes.byref(obj_id))
        assert rc == OK

        # Write 20 bytes
        data = (ctypes.c_byte * 20)(*([0x41] * 20))
        rc = self.lib.objstore_write(self.handle, obj_id, 0, data, 20)
        assert rc == OK

        # Append at offset=20 (== size) → should succeed
        more = (ctypes.c_byte * 10)(*([0x42] * 10))
        rc = self.lib.objstore_write(self.handle, obj_id, 20, more, 10)
        assert rc == OK

        # Verify size is now 30
        size = ctypes.c_int64()
        rc = self.lib.objstore_object_get_size(self.handle, obj_id, ctypes.byref(size))
        assert rc == OK
        assert size.value == 30

    def test_overwrite_within_bounds_succeeds(self):
        obj_id = ctypes.c_uint64()
        rc = self.lib.objstore_object_create(self.handle, b"overwrite", ctypes.byref(obj_id))
        assert rc == OK

        # Write 100 bytes of 0xAA
        data = (ctypes.c_byte * 100)(*([0xAA] * 100))
        rc = self.lib.objstore_write(self.handle, obj_id, 0, data, 100)
        assert rc == OK

        # Overwrite bytes 50-59 with 0xBB
        patch = (ctypes.c_byte * 10)(*([0xBB] * 10))
        rc = self.lib.objstore_write(self.handle, obj_id, 50, patch, 10)
        assert rc == OK

        # Read back and verify
        buf = (ctypes.c_byte * 100)()
        rc = self.lib.objstore_read(self.handle, obj_id, 0, buf, 100)
        assert rc == OK
        # c_byte is signed, so 0xAA=-86, 0xBB=-69
        assert buf[49] == ctypes.c_byte(0xAA).value
        assert buf[50] == ctypes.c_byte(0xBB).value
        assert buf[59] == ctypes.c_byte(0xBB).value
        assert buf[60] == ctypes.c_byte(0xAA).value


class TestReadOnlyViaOptions:
    """Fix 8: ReadOnly option wired through C API."""

    def test_readonly_prevents_create_object(self, lib):
        path = _temp_path()
        try:
            # Create a store first
            handle = ctypes.c_void_p()
            rc = lib.objstore_create(path.encode("utf-8"), None, ctypes.byref(handle))
            assert rc == OK
            # Create one object
            obj_id = ctypes.c_uint64()
            lib.objstore_object_create(handle, b"existing", ctypes.byref(obj_id))
            lib.objstore_close(handle)

            # Reopen with ReadOnly option
            opts = ctypes.c_void_p()
            rc = lib.objstore_options_create(ctypes.byref(opts))
            assert rc == OK
            rc = lib.objstore_options_set_read_only(opts, 1)
            assert rc == OK

            ro_handle = ctypes.c_void_p()
            rc = lib.objstore_open(path.encode("utf-8"), opts, ctypes.byref(ro_handle))
            assert rc == OK

            # Try to create object → should fail
            new_id = ctypes.c_uint64()
            rc = lib.objstore_object_create(ro_handle, b"fail", ctypes.byref(new_id))
            assert rc == READ_ONLY

            # Reading existing object should work
            size = ctypes.c_int64()
            rc = lib.objstore_object_get_size(ro_handle, obj_id, ctypes.byref(size))
            assert rc == OK

            lib.objstore_close(ro_handle)
            lib.objstore_options_free(opts)
        finally:
            if os.path.exists(path):
                os.remove(path)


class TestLargeDataFFI:
    """Large data round-trip through C API."""

    @pytest.fixture(autouse=True)
    def setup(self, lib):
        self.lib = lib
        self.path = _temp_path()
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path.encode("utf-8"), None, ctypes.byref(self.handle))
        assert rc == OK
        yield
        lib.objstore_close(self.handle)
        if os.path.exists(self.path):
            os.remove(self.path)

    def test_256kb_roundtrip_sha256(self):
        import random
        random.seed(12345)

        obj_id = ctypes.c_uint64()
        rc = self.lib.objstore_object_create(self.handle, b"bigdata", ctypes.byref(obj_id))
        assert rc == OK

        # Write 256KB in 32KB chunks
        total_size = 256 * 1024
        chunk_size = 32 * 1024
        all_data = bytearray(random.getrandbits(8) for _ in range(total_size))
        expected_hash = hashlib.sha256(bytes(all_data)).hexdigest()

        offset = 0
        while offset < total_size:
            chunk = all_data[offset:offset + chunk_size]
            buf = (ctypes.c_byte * len(chunk))(*chunk)
            rc = self.lib.objstore_write(self.handle, obj_id, offset, buf, len(chunk))
            assert rc == OK, f"Write failed at offset {offset}: rc={rc}"
            offset += chunk_size

        # Read back
        read_buf = (ctypes.c_byte * total_size)()
        rc = self.lib.objstore_read(self.handle, obj_id, 0, read_buf, total_size)
        assert rc == OK

        actual_data = bytes(read_buf)
        actual_hash = hashlib.sha256(actual_data).hexdigest()
        assert actual_hash == expected_hash


class TestManyObjectsFFI:
    """Many objects via C API."""

    @pytest.fixture(autouse=True)
    def setup(self, lib):
        self.lib = lib
        self.path = _temp_path()
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path.encode("utf-8"), None, ctypes.byref(self.handle))
        assert rc == OK
        yield
        lib.objstore_close(self.handle)
        if os.path.exists(self.path):
            os.remove(self.path)

    def test_200_objects_create_and_verify(self):
        ids = []
        for i in range(200):
            obj_id = ctypes.c_uint64()
            name = f"obj_{i:04d}".encode("utf-8")
            rc = self.lib.objstore_object_create(self.handle, name, ctypes.byref(obj_id))
            assert rc == OK, f"Failed to create object {i}: rc={rc}"

            # Write index as data
            data = (ctypes.c_byte * 4)(*list(i.to_bytes(4, 'little')))
            rc = self.lib.objstore_write(self.handle, obj_id, 0, data, 4)
            assert rc == OK
            ids.append(obj_id.value)

        # Random-access verify 50 objects
        import random
        rng = random.Random(42)
        for _ in range(50):
            idx = rng.randint(0, 199)
            buf = (ctypes.c_byte * 4)()
            rc = self.lib.objstore_read(self.handle, ids[idx], 0, buf, 4)
            assert rc == OK
            val = int.from_bytes(bytes(buf), 'little')
            assert val == idx, f"Object {idx} has wrong data: {val}"


class TestTransactionsFFI:
    """Transaction rollback via C API."""

    @pytest.fixture(autouse=True)
    def setup(self, lib):
        self.lib = lib
        self.path = _temp_path()
        self.handle = ctypes.c_void_p()
        rc = lib.objstore_create(self.path.encode("utf-8"), None, ctypes.byref(self.handle))
        assert rc == OK
        yield
        lib.objstore_close(self.handle)
        if os.path.exists(self.path):
            os.remove(self.path)

    def test_rollback_removes_objects(self):
        # Begin transaction
        rc = self.lib.objstore_txn_begin(self.handle)
        assert rc == OK

        # Create objects in transaction
        ids = []
        for i in range(10):
            obj_id = ctypes.c_uint64()
            name = f"txn_obj_{i}".encode("utf-8")
            rc = self.lib.objstore_object_create(self.handle, name, ctypes.byref(obj_id))
            assert rc == OK
            ids.append(obj_id.value)

        # Rollback
        rc = self.lib.objstore_txn_rollback(self.handle)
        assert rc == OK

        # Objects should not exist
        for obj_id in ids:
            size = ctypes.c_int64()
            rc = self.lib.objstore_object_get_size(self.handle, obj_id, ctypes.byref(size))
            assert rc == NOT_FOUND

    def test_commit_preserves_objects(self):
        rc = self.lib.objstore_txn_begin(self.handle)
        assert rc == OK

        obj_id = ctypes.c_uint64()
        rc = self.lib.objstore_object_create(self.handle, b"committed", ctypes.byref(obj_id))
        assert rc == OK

        data = (ctypes.c_byte * 5)(*list(b"hello"))
        rc = self.lib.objstore_write(self.handle, obj_id, 0, data, 5)
        assert rc == OK

        rc = self.lib.objstore_txn_commit(self.handle)
        assert rc == OK

        # Object should still exist
        size = ctypes.c_int64()
        rc = self.lib.objstore_object_get_size(self.handle, obj_id, ctypes.byref(size))
        assert rc == OK
        assert size.value == 5


class TestMVCCSequentialWriters:
    """Multiple sequential writers → reader sees all."""

    def test_two_writers_then_reader(self, lib):
        path = _temp_path()
        try:
            # Writer 1
            h1 = ctypes.c_void_p()
            rc = lib.objstore_create(path.encode("utf-8"), None, ctypes.byref(h1))
            assert rc == OK

            id1 = ctypes.c_uint64()
            lib.objstore_object_create(h1, b"from_w1", ctypes.byref(id1))
            data1 = (ctypes.c_byte * 3)(*list(b"aaa"))
            lib.objstore_write(h1, id1, 0, data1, 3)
            lib.objstore_close(h1)

            # Writer 2
            h2 = ctypes.c_void_p()
            rc = lib.objstore_open(path.encode("utf-8"), None, ctypes.byref(h2))
            assert rc == OK

            id2 = ctypes.c_uint64()
            lib.objstore_object_create(h2, b"from_w2", ctypes.byref(id2))
            data2 = (ctypes.c_byte * 3)(*list(b"bbb"))
            lib.objstore_write(h2, id2, 0, data2, 3)
            lib.objstore_close(h2)

            # Reader
            opts = ctypes.c_void_p()
            lib.objstore_options_create(ctypes.byref(opts))
            lib.objstore_options_set_read_only(opts, 1)

            reader = ctypes.c_void_p()
            rc = lib.objstore_open(path.encode("utf-8"), opts, ctypes.byref(reader))
            assert rc == OK

            # Both objects should be visible
            buf = (ctypes.c_byte * 3)()
            rc = lib.objstore_read(reader, id1, 0, buf, 3)
            assert rc == OK
            assert bytes(buf) == b"aaa"

            rc = lib.objstore_read(reader, id2, 0, buf, 3)
            assert rc == OK
            assert bytes(buf) == b"bbb"

            lib.objstore_close(reader)
            lib.objstore_options_free(opts)
        finally:
            if os.path.exists(path):
                os.remove(path)


class TestErrorCodes:
    """Verify various error code scenarios."""

    def test_read_nonexistent_object(self, lib):
        path = _temp_path()
        try:
            handle = ctypes.c_void_p()
            lib.objstore_create(path.encode("utf-8"), None, ctypes.byref(handle))

            buf = (ctypes.c_byte * 10)()
            rc = lib.objstore_read(handle, 99999, 0, buf, 10)
            assert rc == NOT_FOUND

            lib.objstore_close(handle)
        finally:
            if os.path.exists(path):
                os.remove(path)

    def test_write_null_data(self, lib):
        path = _temp_path()
        try:
            handle = ctypes.c_void_p()
            lib.objstore_create(path.encode("utf-8"), None, ctypes.byref(handle))

            obj_id = ctypes.c_uint64()
            lib.objstore_object_create(handle, b"x", ctypes.byref(obj_id))

            rc = lib.objstore_write(handle, obj_id, 0, None, 10)
            assert rc == INVALID_ARG

            lib.objstore_close(handle)
        finally:
            if os.path.exists(path):
                os.remove(path)

    def test_negative_length(self, lib):
        path = _temp_path()
        try:
            handle = ctypes.c_void_p()
            lib.objstore_create(path.encode("utf-8"), None, ctypes.byref(handle))

            obj_id = ctypes.c_uint64()
            lib.objstore_object_create(handle, b"y", ctypes.byref(obj_id))

            data = (ctypes.c_byte * 5)()
            rc = lib.objstore_write(handle, obj_id, 0, data, -1)
            assert rc == INVALID_ARG

            lib.objstore_close(handle)
        finally:
            if os.path.exists(path):
                os.remove(path)

    def test_delete_nonexistent(self, lib):
        path = _temp_path()
        try:
            handle = ctypes.c_void_p()
            lib.objstore_create(path.encode("utf-8"), None, ctypes.byref(handle))

            rc = lib.objstore_object_delete(handle, 99999)
            assert rc == NOT_FOUND

            lib.objstore_close(handle)
        finally:
            if os.path.exists(path):
                os.remove(path)
