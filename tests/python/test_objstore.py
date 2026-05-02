"""
ObjectStore C API integration tests using Python ctypes.
This file grows incrementally with each implementation phase.

Phase 1: objstore_get_version
"""
import ctypes
import os
import sys
import platform

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
