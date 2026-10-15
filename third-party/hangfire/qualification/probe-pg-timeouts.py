#!/usr/bin/env python3
"""Qualify PostgreSQL 18.4 timeout semantics in an owned disposable container.

This is a decision experiment, not production startup or a regression-suite gate.
Only synthetic data and loopback random ports are used; no local DB is accessed.
"""

import json
import signal
import socket
import struct
import subprocess
import time
import uuid


def command(*args, timeout=15):
    result = subprocess.run(args, check=True, capture_output=True, text=True,
                            timeout=timeout)
    return result.stdout.strip()


def until(predicate, timeout=5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.05)
    raise AssertionError("bounded observation expired")


def read_message(connection):
    def exact(length):
        result = b""
        while len(result) < length:
            chunk = connection.recv(length - len(result))
            if not chunk:
                raise EOFError("probe connection closed")
            result += chunk
        return result

    tag = exact(1)
    length = struct.unpack("!I", exact(4))[0]
    if not 4 <= length <= 65536:
        raise AssertionError("unexpected probe protocol length")
    return tag, exact(length - 4)


def connect(port, options):
    connection = socket.create_connection(("127.0.0.1", port), timeout=5)
    try:
        fields = (b"user\0postgres\0database\0postgres\0application_name\0"
                  b"maintenance-timeout-probe\0options\0")
        payload = struct.pack("!I", 196608) + fields + options.encode() + b"\0\0"
        connection.sendall(struct.pack("!I", len(payload) + 4) + payload)
        while read_message(connection)[0] != b"Z":
            pass
        return connection
    except BaseException:
        connection.close()
        raise


def send(connection, sql):
    payload = sql.encode() + b"\0"
    connection.sendall(b"Q" + struct.pack("!I", len(payload) + 4) + payload)


def collect(connection):
    errors = []
    while True:
        tag, payload = read_message(connection)
        if tag == b"E":
            errors.append(payload.decode(errors="replace"))
        if tag == b"Z":
            return errors, payload.decode()


def interrupted(_number, _frame):
    raise KeyboardInterrupt()


def require(condition):
    if not condition:
        raise AssertionError("timeout qualification failed")


def main():
    name = "learnstack-p4-timeout-" + uuid.uuid4().hex[:10]
    container_id = None
    start_requested = False
    connections = []

    def observe(sql):
        return command("docker", "exec", container_id, "psql", "-U", "postgres",
                       "-d", "postgres", "-tA", "-v", "ON_ERROR_STOP=1", "-c", sql)

    def sessions(extra=""):
        return observe("SELECT count(*) FROM pg_stat_activity WHERE "
                       "application_name='maintenance-timeout-probe' " + extra)

    def relation_locks():
        return int(observe("SELECT count(*) FROM pg_locks WHERE "
                           "relation='probe_lock'::regclass AND granted AND pid "
                           "IN (SELECT pid FROM pg_stat_activity WHERE "
                           "application_name='maintenance-timeout-probe')"))

    try:
        # Trust is confined to a throwaway DB with a random loopback host port.
        start_requested = True
        container_id = command("docker", "run", "--detach", "--name", name,
                               "-e", "POSTGRES_HOST_AUTH_METHOD=trust", "-p",
                               "127.0.0.1::5432", "postgres:18.4", timeout=90)
        until(lambda: subprocess.run(
            ["docker", "exec", container_id, "pg_isready", "-h", "127.0.0.1",
             "-U", "postgres"], capture_output=True, timeout=5).returncode == 0, 20)
        port = int(command("docker", "port", container_id, "5432/tcp").rsplit(":", 1)[1])
        observe("CREATE TABLE probe_lock(id int PRIMARY KEY, value int NOT NULL); "
                "INSERT INTO probe_lock VALUES(1,0);")

        connection = connect(port, "-c statement_timeout=1000 -c lock_timeout=1000")
        connections.append(connection)
        send(connection, "BEGIN; UPDATE probe_lock SET value=1 WHERE id=1; "
                         "SELECT pg_sleep(20); COMMIT;")
        # Deliberately do not read responses until the observer proves abort.
        until(lambda: sessions("AND state='active' AND wait_event='PgSleep'") == "1")
        before = relation_locks()
        require(before > 0)  # Positive control must hold a relation lock.
        until(lambda: sessions("AND state='idle in transaction (aborted)'") == "1")
        after = relation_locks()
        independent = observe("BEGIN; SET LOCAL lock_timeout='100ms'; "
                              "UPDATE probe_lock SET value=2 WHERE id=1; COMMIT; "
                              "SELECT value FROM probe_lock;")
        require(after == 0 and independent.splitlines()[-1] == "2")
        errors, state = collect(connection)
        require(state == "E" and any("57014" in error for error in errors))
        connection.close()

        connection = connect(port, "-c statement_timeout=500")
        connections.append(connection)
        started = time.monotonic()
        send(connection, "BEGIN; SELECT pg_sleep(.35); SELECT pg_sleep(.35); COMMIT;")
        errors, state = collect(connection)
        elapsed = time.monotonic() - started
        require(not errors and state == "I" and elapsed > 0.65)
        connection.close()

        connection = connect(port, "-c statement_timeout=100 "
                             "-c idle_in_transaction_session_timeout=500")
        connections.append(connection)
        send(connection, "BEGIN; SELECT pg_sleep(20); COMMIT;")
        until(lambda: sessions("AND state='idle in transaction (aborted)'") == "1")
        until(lambda: sessions() == "0")
        connection.close()
        return {
            "postgres": observe("SHOW server_version"),
            "active_relation_locks": before,
            "aborted_relation_locks": after,
            "independent_update_after_abort": True,
            "two_350ms_statements_under_500ms_each_succeeded": True,
            "batch_elapsed_s": round(elapsed, 3),
            "idle_timeout_terminated_aborted_backend": True,
        }
    finally:
        for connection in connections:
            connection.close()
        if start_requested:
            subprocess.run(["docker", "rm", "--force", container_id or name],
                           capture_output=True, timeout=15, check=True)


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    print(json.dumps(main(), indent=2))
