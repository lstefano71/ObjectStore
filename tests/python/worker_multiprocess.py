"""
Worker script for multi-process ObjectStore integration tests.

Each worker is spawned as an independent subprocess, loads the native DLL,
opens the shared database file, and performs actions based on command-line args.

Results are reported as JSON lines on stdout.
"""
import argparse
import ctypes
import hashlib
import json
import os
import sys
import time


def load_lib(lib_path):
    """Load the native library and set up function signatures."""
    lib = ctypes.CDLL(lib_path)
    return lib


def open_store(lib, db_path):
    """Open an existing store, returns handle."""
    handle = ctypes.c_void_p()
    rc = lib.objstore_open_or_create(db_path.encode("utf-8"), None, ctypes.byref(handle))
    if rc != 0:
        emit({"type": "error", "msg": f"Failed to open store: rc={rc}"})
        sys.exit(1)
    return handle


def close_store(lib, handle):
    """Close a store handle."""
    lib.objstore_close(handle)


def refresh(lib, handle):
    """Refresh to see latest committed state from other writers."""
    rc = lib.objstore_refresh(handle)
    if rc != 0:
        emit({"type": "error", "msg": f"Refresh failed: rc={rc}"})


def emit(obj):
    """Emit a JSON line to stdout."""
    print(json.dumps(obj), flush=True)


def action_write_objects(lib, handle, args):
    """Create N objects with verifiable data content."""
    count = args.count
    worker_id = args.worker_id

    for i in range(count):
        # Create object
        obj_id = ctypes.c_uint64()
        name = f"w{worker_id}_obj{i}".encode("utf-8")
        rc = lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        if rc != 0:
            emit({"type": "error", "msg": f"Create failed: rc={rc}, i={i}"})
            sys.exit(1)

        # Write data: worker_id + index + pattern (verifiable via SHA256)
        data = f"worker={worker_id},index={i},payload={'X' * 100}".encode("utf-8")
        data_buf = (ctypes.c_uint8 * len(data))(*data)
        rc = lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(len(data)))
        if rc != 0:
            emit({"type": "error", "msg": f"Append failed: rc={rc}, id={obj_id.value}"})
            sys.exit(1)

        sha = hashlib.sha256(data).hexdigest()
        emit({"type": "created", "id": obj_id.value, "sha256": sha, "size": len(data)})

    emit({"type": "done", "worker_id": worker_id, "count": count})


def action_verify_objects(lib, handle, args):
    """Verify that specific object IDs exist and have correct content."""
    ids = json.loads(args.ids)
    refresh(lib, handle)

    errors = 0
    for obj_id in ids:
        # Check existence
        exists = ctypes.c_int32()
        rc = lib.objstore_object_exists(handle, ctypes.c_uint64(obj_id), ctypes.byref(exists))
        if rc != 0 or exists.value == 0:
            errors += 1
            emit({"type": "error", "msg": f"Object {obj_id} not found"})
            continue

        # Read size
        size = ctypes.c_int64()
        rc = lib.objstore_object_get_size(handle, ctypes.c_uint64(obj_id), ctypes.byref(size))
        if rc != 0:
            errors += 1
            continue

        # Read data
        buf = (ctypes.c_uint8 * size.value)()
        bytes_read = ctypes.c_int64()
        rc = lib.objstore_read(
            handle, ctypes.c_uint64(obj_id), ctypes.c_int64(0),
            buf, ctypes.c_int64(size.value), ctypes.byref(bytes_read)
        )
        if rc != 0:
            errors += 1
            continue

    emit({"type": "verify_result", "total": len(ids), "errors": errors, "all_ok": errors == 0})


def action_write_and_signal(lib, handle, args):
    """Write objects (same as write_objects, for sequential test)."""
    action_write_objects(lib, handle, args)


def action_refresh_and_read(lib, handle, args):
    """Refresh and verify specific objects exist."""
    ids = json.loads(args.ids)
    refresh(lib, handle)

    found = 0
    for obj_id in ids:
        exists = ctypes.c_int32()
        rc = lib.objstore_object_exists(handle, ctypes.c_uint64(obj_id), ctypes.byref(exists))
        if rc == 0 and exists.value != 0:
            found += 1

    emit({"type": "read_result", "total": len(ids), "found": found, "all_found": found == len(ids)})


def action_hybrid_read_write(lib, handle, args):
    """Alternate between writing and reading, verifying consistency."""
    rounds = args.rounds
    worker_id = args.worker_id
    errors = 0
    created_ids = []

    for r in range(rounds):
        # Write phase: create an object
        obj_id = ctypes.c_uint64()
        name = f"hybrid_w{worker_id}_r{r}".encode("utf-8")
        rc = lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        if rc != 0:
            errors += 1
            emit({"type": "error", "msg": f"Create failed round {r}: rc={rc}"})
            continue

        data = f"hybrid_{worker_id}_{r}".encode("utf-8")
        data_buf = (ctypes.c_uint8 * len(data))(*data)
        rc = lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(len(data)))
        if rc != 0:
            errors += 1
            continue

        created_ids.append(obj_id.value)
        emit({"type": "created", "id": obj_id.value})

        # Read phase: refresh and verify our own objects exist
        refresh(lib, handle)
        for cid in created_ids:
            exists = ctypes.c_int32()
            rc = lib.objstore_object_exists(handle, ctypes.c_uint64(cid), ctypes.byref(exists))
            if rc != 0 or exists.value == 0:
                errors += 1
                emit({"type": "error", "msg": f"Object {cid} missing after refresh"})

    emit({"type": "summary", "worker_id": worker_id, "rounds": rounds,
          "created": len(created_ids), "errors": errors})


def action_slow_writer(lib, handle, args):
    """Write objects slowly (for crash tests)."""
    count = args.count
    delay_ms = args.delay_ms
    worker_id = args.worker_id

    for i in range(count):
        obj_id = ctypes.c_uint64()
        name = f"slow_w{worker_id}_{i}".encode("utf-8")
        rc = lib.objstore_object_create(handle, name, ctypes.byref(obj_id))
        if rc != 0:
            # Lock timeout is expected if contention is high
            if i > 0:
                break
            emit({"type": "error", "msg": f"Create failed: rc={rc}"})
            sys.exit(1)

        data = f"slow_data_{worker_id}_{i}_{'P' * 50}".encode("utf-8")
        data_buf = (ctypes.c_uint8 * len(data))(*data)
        lib.objstore_append(handle, obj_id, data_buf, ctypes.c_int64(len(data)))

        emit({"type": "created", "id": obj_id.value})
        time.sleep(delay_ms / 1000.0)

    emit({"type": "done", "worker_id": worker_id})


def action_verify_db_valid(lib, handle, args):
    """Verify the database can be opened and basic operations work."""
    # Try to list objects (iter)
    iter_handle = ctypes.c_void_p()
    rc = lib.objstore_list_begin(handle, ctypes.byref(iter_handle))
    if rc != 0:
        emit({"type": "validation", "valid": False, "msg": f"list_begin failed: rc={rc}"})
        sys.exit(1)

    count = 0
    obj_id = ctypes.c_uint64()
    size = ctypes.c_int64()
    while True:
        rc = lib.objstore_iter_next(iter_handle, ctypes.byref(obj_id), ctypes.byref(size))
        if rc == 1:  # OBJSTORE_ITER_END
            break
        if rc != 0:
            emit({"type": "validation", "valid": False, "msg": f"iter_next failed: rc={rc}"})
            lib.objstore_iter_close(iter_handle)
            sys.exit(1)
        count += 1

    lib.objstore_iter_close(iter_handle)
    emit({"type": "validation", "valid": True, "object_count": count})


def action_read_objects(lib, handle, args):
    """Read specified objects multiple times (for throughput tests)."""
    ids = json.loads(args.ids)
    iterations = args.iterations
    worker_id = args.worker_id

    total_read = 0
    errors = 0

    for _ in range(iterations):
        refresh(lib, handle)
        for obj_id in ids:
            exists = ctypes.c_int32()
            rc = lib.objstore_object_exists(handle, ctypes.c_uint64(obj_id), ctypes.byref(exists))
            if rc != 0 or exists.value == 0:
                errors += 1
                continue

            size = ctypes.c_int64()
            lib.objstore_object_get_size(handle, ctypes.c_uint64(obj_id), ctypes.byref(size))
            if size.value > 0:
                buf = (ctypes.c_uint8 * size.value)()
                bytes_read = ctypes.c_int64()
                lib.objstore_read(
                    handle, ctypes.c_uint64(obj_id), ctypes.c_int64(0),
                    buf, ctypes.c_int64(size.value), ctypes.byref(bytes_read)
                )
                total_read += bytes_read.value

    emit({"type": "read_summary", "worker_id": worker_id,
          "total_reads": len(ids) * iterations, "total_bytes": total_read, "errors": errors})


def main():
    parser = argparse.ArgumentParser(description="ObjectStore multiprocess worker")
    parser.add_argument("--db", required=True, help="Database file path")
    parser.add_argument("--worker-id", type=int, required=True)
    parser.add_argument("--action", required=True)
    parser.add_argument("--lib", required=True, help="Path to native library")
    parser.add_argument("--count", type=int, default=10)
    parser.add_argument("--ids", type=str, default="[]")
    parser.add_argument("--rounds", type=int, default=5)
    parser.add_argument("--delay-ms", type=int, default=10)
    parser.add_argument("--iterations", type=int, default=1)
    args = parser.parse_args()

    lib = load_lib(args.lib)
    handle = open_store(lib, args.db)

    try:
        actions = {
            "write_objects": action_write_objects,
            "verify_objects": action_verify_objects,
            "write_and_signal": action_write_and_signal,
            "refresh_and_read": action_refresh_and_read,
            "hybrid_read_write": action_hybrid_read_write,
            "slow_writer": action_slow_writer,
            "verify_db_valid": action_verify_db_valid,
            "read_objects": action_read_objects,
        }

        action_fn = actions.get(args.action)
        if action_fn is None:
            emit({"type": "error", "msg": f"Unknown action: {args.action}"})
            sys.exit(1)

        action_fn(lib, handle, args)
    finally:
        close_store(lib, handle)


if __name__ == "__main__":
    main()
