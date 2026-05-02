"""
Minimal object store backed by SQLite — for benchmarking comparison with ObjectStore.

Configuration:
- WAL mode (readers don't block writers, similar to COW)
- synchronous=FULL (fsync on every commit, same guarantee as ObjectStore)
- One connection per instance (like ObjectStore's single handle)
"""

import sqlite3
import time
import os


class SqliteObjectStore:
    """Simple blob object store using SQLite as backend."""

    def __init__(self, path: str):
        self.path = path
        self._conn = sqlite3.connect(path, isolation_level=None)
        self._conn.text_factory = bytes  # Return raw bytes for BLOB columns
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._conn.execute("PRAGMA synchronous=FULL")
        self._conn.execute("""
            CREATE TABLE IF NOT EXISTS objects (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT,
                data BLOB NOT NULL DEFAULT x'',
                created_at REAL NOT NULL,
                modified_at REAL NOT NULL
            )
        """)
        self._conn.execute("""
            CREATE UNIQUE INDEX IF NOT EXISTS idx_objects_name
            ON objects(name) WHERE name IS NOT NULL
        """)
        self._in_transaction = False

    def close(self):
        if self._in_transaction:
            self._conn.execute("ROLLBACK")
            self._in_transaction = False
        self._conn.close()

    def begin(self):
        self._conn.execute("BEGIN IMMEDIATE")
        self._in_transaction = True

    def commit(self):
        self._conn.execute("COMMIT")
        self._in_transaction = False

    def rollback(self):
        self._conn.execute("ROLLBACK")
        self._in_transaction = False

    def create_object(self, name: str | None = None) -> int:
        """Create a new empty object. Returns its ID."""
        now = time.time()
        auto = not self._in_transaction
        if auto:
            self._conn.execute("BEGIN IMMEDIATE")
        try:
            cur = self._conn.execute(
                "INSERT INTO objects (name, data, created_at, modified_at) VALUES (?, x'', ?, ?)",
                (name, now, now),
            )
            obj_id = cur.lastrowid
            if auto:
                self._conn.execute("COMMIT")
            return obj_id
        except:
            if auto:
                self._conn.execute("ROLLBACK")
            raise

    def append(self, obj_id: int, data: bytes):
        """Append data to an existing object."""
        now = time.time()
        auto = not self._in_transaction
        if auto:
            self._conn.execute("BEGIN IMMEDIATE")
        try:
            # Read current data, concatenate, write back (SQLite || on blobs can be unreliable)
            cur = self._conn.execute("SELECT data FROM objects WHERE id = ?", (obj_id,))
            row = cur.fetchone()
            if row is None:
                raise KeyError(f"Object {obj_id} not found")
            new_data = row[0] + data
            self._conn.execute(
                "UPDATE objects SET data = ?, modified_at = ? WHERE id = ?",
                (new_data, now, obj_id),
            )
            if auto:
                self._conn.execute("COMMIT")
        except:
            if auto:
                self._conn.execute("ROLLBACK")
            raise

    def read(self, obj_id: int) -> bytes:
        """Read the full data of an object."""
        cur = self._conn.execute("SELECT data FROM objects WHERE id = ?", (obj_id,))
        row = cur.fetchone()
        if row is None:
            raise KeyError(f"Object {obj_id} not found")
        return bytes(row[0]) if row[0] else b''

    def write_at(self, obj_id: int, offset: int, data: bytes):
        """Overwrite data at a specific offset."""
        now = time.time()
        auto = not self._in_transaction
        if auto:
            self._conn.execute("BEGIN IMMEDIATE")
        try:
            cur = self._conn.execute("SELECT data FROM objects WHERE id = ?", (obj_id,))
            row = cur.fetchone()
            if row is None:
                raise KeyError(f"Object {obj_id} not found")
            blob = bytearray(row[0])
            blob[offset : offset + len(data)] = data
            self._conn.execute(
                "UPDATE objects SET data = ?, modified_at = ? WHERE id = ?",
                (bytes(blob), now, obj_id),
            )
            if auto:
                self._conn.execute("COMMIT")
        except:
            if auto:
                self._conn.execute("ROLLBACK")
            raise

    def delete(self, obj_id: int):
        """Delete an object."""
        auto = not self._in_transaction
        if auto:
            self._conn.execute("BEGIN IMMEDIATE")
        try:
            self._conn.execute("DELETE FROM objects WHERE id = ?", (obj_id,))
            if auto:
                self._conn.execute("COMMIT")
        except:
            if auto:
                self._conn.execute("ROLLBACK")
            raise
