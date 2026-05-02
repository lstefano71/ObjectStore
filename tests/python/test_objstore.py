"""
ObjectStore C API integration tests using Python ctypes.
This file grows incrementally with each implementation phase.

Phase 1: objstore_get_version
Phase 2: options handle
Phase 3: store lifecycle + object CRUD + read/write
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

