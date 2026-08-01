#!/usr/bin/env python3
"""mud.py - manage a persistent telnet session to a MUD (tbaMUD/CircleMUD).

A MUD is an interactive, stateful telnet session. A single tool call can't hold
that connection open, so this script runs a small background daemon that owns the
socket: it streams everything the server sends into a log file and forwards your
commands to the server over a local TCP control channel. You then drive the game
with short, stateless calls (send / read) that talk to that daemon.

Why a control *socket* and not a named pipe: this client runs on Windows, where
os.mkfifo / os.fork don't exist and select() only works on sockets. A loopback
TCP connection gives the same "stateless client, persistent daemon" shape while
staying portable to POSIX too.

Subcommands:
  start    Connect to the MUD and start the background session.
  send     Send one or more command lines to the MUD.
  read     Print server output. By default only what's new since the last read.
  status   Show whether the session is alive and the most recent output.
  stop     Disconnect and shut the session down.
  login    Convenience: send name + password and walk the MOTD/menu screens.

Session state lives under --session-dir (default $MUD_SESSION_DIR or a
mud-session folder in the OS temp dir). Output is stored raw; `read` strips
ANSI color by default for readability (use --raw to keep it).

Previous flakiness came from a fundamentally different, non-persistent design:
every invocation opened a brand-new socket, logged in from scratch, ran one
batch of commands, then closed the connection. That meant every call raced the
server's "already connected" reconnect handling, repeated the full login
handshake, and lost all session state the instant the process exited - so
"navigating the MUD" across multiple tool calls was never actually possible.
This version fixes that by separating "own the socket" (the daemon, started
once) from "drive the game" (many cheap send/read calls against that daemon).
"""
import argparse
import os
import re
import select
import socket
import subprocess
import sys
import tempfile
import time

DEFAULT_DIR = os.environ.get(
    "MUD_SESSION_DIR", os.path.join(tempfile.gettempdir(), "mud-session")
)

# Telnet protocol bytes (RFC 854)
IAC, DONT, DO, WONT, WILL, SB, SE = 255, 254, 253, 252, 251, 250, 240
ANSI_RE = re.compile(rb"\x1b\[[0-9;?]*[a-zA-Z]")

# Matches the in-game status prompt, e.g. "21H 100M 31V (news) (motd) >"
GAME_PROMPT_RE = re.compile(r"[0-9]+H [0-9]+M [0-9]+V")


def paths(d):
    return {
        "dir": d,
        "log": os.path.join(d, "session.log"),
        "port": os.path.join(d, "control.port"),
        "pid": os.path.join(d, "daemon.pid"),
        "offset": os.path.join(d, "read.offset"),
        "meta": os.path.join(d, "meta.txt"),
        "err": os.path.join(d, "daemon.err"),
    }


class Telnet:
    """Stream filter: removes telnet IAC negotiation and refuses all options.

    Refusing every option (we reply WONT to DO, DONT to WILL) keeps the byte
    stream clean and readable. We aren't a real terminal, so we don't need to
    agree to anything the server asks for (echo, window size, MSDP, etc.)."""

    def __init__(self):
        self.state = "normal"
        self.cmd = None

    def feed(self, data):
        clean = bytearray()
        resp = bytearray()
        for b in data:
            if self.state == "normal":
                if b == IAC:
                    self.state = "iac"
                else:
                    clean.append(b)
            elif self.state == "iac":
                if b == IAC:  # escaped 0xFF -> literal byte
                    clean.append(IAC)
                    self.state = "normal"
                elif b in (DO, DONT, WILL, WONT):
                    self.cmd = b
                    self.state = "opt"
                elif b == SB:
                    self.state = "sb"
                else:  # standalone command, ignore
                    self.state = "normal"
            elif self.state == "opt":
                if self.cmd == DO:
                    resp += bytes([IAC, WONT, b])
                elif self.cmd == WILL:
                    resp += bytes([IAC, DONT, b])
                self.state = "normal"
            elif self.state == "sb":
                if b == IAC:
                    self.state = "sb_iac"
            elif self.state == "sb_iac":
                self.state = "normal" if b == SE else "sb"
        return bytes(clean), bytes(resp)


# --------------------------------------------------------------------------- #
# Daemon: owns the socket. Runs detached. Not called directly by the user.
# --------------------------------------------------------------------------- #
def _cleanup(p):
    for key in ("pid", "port"):
        try:
            os.remove(p[key])
        except OSError:
            pass


def run_daemon(d, host, port):
    p = paths(d)
    with open(p["pid"], "w") as f:
        f.write(str(os.getpid()))
    try:
        mud_sock = socket.create_connection((host, port), timeout=15)
    except Exception as e:
        with open(p["log"], "ab") as logf:
            logf.write(f"[connect failed: {e}]\n".encode())
        _cleanup(p)
        return
    mud_sock.setblocking(False)

    # Loopback control channel: short-lived client connections write command
    # lines here; we forward each line to the MUD socket. Binding port 0 lets
    # the OS pick a free port, which we hand back to clients via a file.
    ctrl = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    ctrl.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    ctrl.bind(("127.0.0.1", 0))
    ctrl.listen(8)
    ctrl.setblocking(False)
    with open(p["port"], "w") as f:
        f.write(str(ctrl.getsockname()[1]))

    tel = Telnet()
    logf = open(p["log"], "ab", buffering=0)
    conns = []
    bufs = {}

    try:
        while True:
            r, _, _ = select.select([mud_sock, ctrl] + conns, [], [], 1.0)

            if mud_sock in r:
                try:
                    data = mud_sock.recv(8192)
                except (BlockingIOError, OSError):
                    data = b""
                if data == b"":
                    logf.write(b"\n[connection closed by server]\n")
                    break
                clean, resp = tel.feed(data)
                if clean:
                    logf.write(clean)
                if resp:
                    try:
                        mud_sock.sendall(resp)
                    except OSError:
                        pass

            if ctrl in r:
                try:
                    conn, _ = ctrl.accept()
                    conn.setblocking(False)
                    conns.append(conn)
                    bufs[conn] = bytearray()
                except OSError:
                    pass

            for conn in [c for c in conns if c in r]:
                try:
                    chunk = conn.recv(8192)
                except (BlockingIOError, OSError):
                    chunk = None
                if chunk is None:
                    continue
                if chunk == b"":
                    conns.remove(conn)
                    bufs.pop(conn, None)
                    try:
                        conn.close()
                    except OSError:
                        pass
                    continue
                buf = bufs[conn]
                buf += chunk
                while b"\n" in buf:
                    line, _, buf = buf.partition(b"\n")
                    line = line.rstrip(b"\r")
                    if line == b"__QUIT__":
                        try:
                            mud_sock.sendall(b"quit\r\n")
                        except OSError:
                            pass
                        raise SystemExit
                    try:
                        mud_sock.sendall(line + b"\r\n")
                    except OSError:
                        logf.write(b"\n[send failed: socket closed]\n")
                        raise SystemExit
                bufs[conn] = buf
    finally:
        try:
            mud_sock.close()
        finally:
            logf.close()
            try:
                ctrl.close()
            except OSError:
                pass
            for conn in conns:
                try:
                    conn.close()
                except OSError:
                    pass
            _cleanup(p)


# --------------------------------------------------------------------------- #
# Client-side helpers
# --------------------------------------------------------------------------- #
def _control_port(p):
    try:
        return int(open(p["port"]).read().strip())
    except (FileNotFoundError, ValueError):
        return None


def is_alive(p):
    port = _control_port(p)
    if port is None:
        return False
    try:
        s = socket.create_connection(("127.0.0.1", port), timeout=1)
        s.close()
        return True
    except OSError:
        return False


def _send_lines(p, lines):
    port = _control_port(p)
    if port is None:
        return False
    s = socket.create_connection(("127.0.0.1", port), timeout=3)
    try:
        for line in lines:
            s.sendall(line.encode() + b"\n")
    finally:
        s.close()
    return True


def cmd_start(args):
    p = paths(args.session_dir)
    os.makedirs(p["dir"], exist_ok=True)
    if is_alive(p):
        print(f"Session already running (control port {_control_port(p)}). "
              f"Use 'stop' first to reconnect.")
        return 0
    for f in (p["port"], p["pid"]):
        if os.path.exists(f):
            os.remove(f)
    open(p["log"], "wb").close()
    with open(p["offset"], "w") as f:
        f.write("0")
    with open(p["meta"], "w") as f:
        f.write(f"{args.host}:{args.port}\n")

    err = open(p["err"], "ab")
    kwargs = dict(stdout=err, stderr=err, stdin=subprocess.DEVNULL)
    if os.name == "nt":
        kwargs["creationflags"] = (
            subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
        )
    else:
        kwargs["start_new_session"] = True
    subprocess.Popen(
        [sys.executable, os.path.abspath(__file__),
         "--session-dir", args.session_dir, "_daemon",
         "--host", args.host, "--port", str(args.port)],
        **kwargs,
    )
    # Give the daemon a moment to connect and receive the banner.
    deadline = time.time() + 5
    while time.time() < deadline:
        if is_alive(p):
            break
        time.sleep(0.2)
    if not is_alive(p):
        print("Session failed to start. Recent log:")
        if os.path.exists(p["log"]):
            with open(p["log"], "rb") as f:
                sys.stdout.write(_clean(f.read(), raw=False))
        return 1
    print(f"Started session to {args.host}:{args.port} (session-dir: {p['dir']}).")
    time.sleep(0.6)
    _print_new(p, raw=False, update=True)
    return 0


def _clean(data, raw):
    if raw:
        return data.decode("utf-8", "replace")
    return ANSI_RE.sub(b"", data).decode("utf-8", "replace")


def _get_new(p, raw, update):
    """Return output appended since the last read, advancing the marker."""
    try:
        offset = int(open(p["offset"]).read().strip())
    except (FileNotFoundError, ValueError):
        offset = 0
    size = os.path.getsize(p["log"]) if os.path.exists(p["log"]) else 0
    if size < offset:  # log was truncated/restarted
        offset = 0
    with open(p["log"], "rb") as f:
        f.seek(offset)
        data = f.read()
    if update:
        with open(p["offset"], "w") as f:
            f.write(str(size))
    return _clean(data, raw)


def _print_new(p, raw, update):
    sys.stdout.write(_get_new(p, raw, update))
    sys.stdout.flush()


def cmd_send(args):
    p = paths(args.session_dir)
    if not is_alive(p):
        print("No live session. Run 'start' first.")
        return 1
    for line in args.command:
        _send_lines(p, [line])
        if len(args.command) > 1:
            time.sleep(args.delay)
    time.sleep(args.wait)
    _print_new(p, raw=args.raw, update=True)
    return 0


def cmd_read(args):
    p = paths(args.session_dir)
    if not os.path.exists(p["log"]):
        print("No session log. Run 'start' first.")
        return 1
    if args.all:
        with open(p["log"], "rb") as f:
            data = f.read()
        sys.stdout.write(_clean(data, args.raw))
        if not args.no_update:
            with open(p["offset"], "w") as f:
                f.write(str(os.path.getsize(p["log"])))
        return 0
    # Optionally wait for new output to appear.
    if args.wait > 0:
        try:
            offset = int(open(p["offset"]).read().strip())
        except (FileNotFoundError, ValueError):
            offset = 0
        deadline = time.time() + args.wait
        while time.time() < deadline:
            if os.path.getsize(p["log"]) > offset:
                time.sleep(0.3)  # let a full burst land
                break
            time.sleep(0.2)
    _print_new(p, raw=args.raw, update=not args.no_update)
    return 0


def cmd_status(args):
    p = paths(args.session_dir)
    alive = is_alive(p)
    target = open(p["meta"]).read().strip() if os.path.exists(p["meta"]) else "?"
    print(f"Session dir : {p['dir']}")
    print(f"Target      : {target}")
    print(f"Status      : {'ALIVE' if alive else 'not running'}")
    if os.path.exists(p["log"]):
        size = os.path.getsize(p["log"])
        print(f"Log size    : {size} bytes")
        with open(p["log"], "rb") as f:
            f.seek(max(0, size - 1200))
            tail = f.read()
        print("--- recent output ---")
        sys.stdout.write(_clean(tail, raw=False))
    return 0


def cmd_stop(args):
    p = paths(args.session_dir)
    if not is_alive(p):
        print("No live session.")
        return 0
    try:
        _send_lines(p, ["__QUIT__"])
    except OSError:
        pass
    time.sleep(0.8)
    if is_alive(p):
        pid = None
        try:
            pid = int(open(p["pid"]).read().strip())
        except (FileNotFoundError, ValueError):
            pid = None
        if pid:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(pid), "/F"],
                                capture_output=True)
            else:
                try:
                    os.kill(pid, 15)
                except ProcessLookupError:
                    pass
    print("Session stopped.")
    return 0


def cmd_login(args):
    """Send name + password and walk the MOTD/menu screens.

    tbaMUD's post-credentials flow has three possible shapes:
      1. Reconnecting - a session for this character is already connected;
         the server drops straight into the game with no menu at all.
      2. MOTD ending in "*** PRESS RETURN:" - dismiss with an empty line.
      3. Main menu ("0) ... 1) Enter the game ... Make your choice:") -
         send "1".
    This loops on those signals until the real in-game status prompt shows
    up (or a few rounds pass). Because the session is persistent, an
    incomplete login here isn't fatal - `send` can finish the job by hand.
    """
    p = paths(args.session_dir)
    if not is_alive(p):
        print("No live session. Run 'start' first.")
        return 1

    _get_new(p, raw=True, update=True)  # drain anything already on screen
    _send_lines(p, [args.name])
    time.sleep(1.0)
    out = _get_new(p, raw=False, update=True)

    if "password" in out.lower():
        _send_lines(p, [args.password])
        time.sleep(1.0)
        out = _get_new(p, raw=False, update=True)
        if "retype" in out.lower() or "again" in out.lower():
            _send_lines(p, [args.password])
            time.sleep(1.2)
            out = _get_new(p, raw=False, update=True)

    full = out
    for _ in range(6):
        if GAME_PROMPT_RE.search(full):
            break
        lower = out.lower()
        if "press return" in lower or "press enter" in lower:
            _send_lines(p, [""])
            time.sleep(1.0)
        elif "make your choice" in lower:
            _send_lines(p, ["1"])
            time.sleep(1.2)
        elif "reconnecting" in lower:
            time.sleep(1.0)
        else:
            break
        out = _get_new(p, raw=False, update=True)
        full += out

    sys.stdout.write(full)
    if "ncorrect" in full or "wrong" in full.lower():
        print("\n[login may have failed - check the output above]")
    elif not GAME_PROMPT_RE.search(full):
        print("\n[game prompt not detected yet - try 'read' or 'send look']")
    return 0


def build_parser():
    ap = argparse.ArgumentParser(description="Manage a telnet MUD session.")
    ap.add_argument("--session-dir", default=DEFAULT_DIR,
                     help=f"Session state dir (default {DEFAULT_DIR})")
    sub = ap.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("start", help="connect and start the session")
    s.add_argument("--host", default="localhost")
    s.add_argument("--port", type=int, default=4000)
    s.set_defaults(func=cmd_start)

    s = sub.add_parser("send", help="send command line(s) to the MUD")
    s.add_argument("command", nargs="+", help="one or more command lines")
    s.add_argument("--wait", type=float, default=1.0,
                    help="seconds to wait for output after sending (default 1.0)")
    s.add_argument("--delay", type=float, default=0.6,
                    help="seconds between multiple commands (default 0.6)")
    s.add_argument("--raw", action="store_true", help="keep ANSI color codes")
    s.set_defaults(func=cmd_send)

    s = sub.add_parser("read", help="print server output")
    s.add_argument("--all", action="store_true", help="print whole log, not just new")
    s.add_argument("--wait", type=float, default=0.0,
                    help="wait up to N seconds for new output")
    s.add_argument("--raw", action="store_true", help="keep ANSI color codes")
    s.add_argument("--no-update", action="store_true",
                    help="don't advance the read marker")
    s.set_defaults(func=cmd_read)

    s = sub.add_parser("status", help="show session status + recent output")
    s.set_defaults(func=cmd_status)

    s = sub.add_parser("stop", help="disconnect and shut down")
    s.set_defaults(func=cmd_stop)

    s = sub.add_parser("login", help="send name + password, walk MOTD/menu")
    s.add_argument("name", nargs="?", default="dummy")
    s.add_argument("password", nargs="?", default="helloworld")
    s.set_defaults(func=cmd_login)

    s = sub.add_parser("_daemon", help=argparse.SUPPRESS)
    s.add_argument("--host", required=True)
    s.add_argument("--port", type=int, required=True)
    s.set_defaults(func=lambda a: run_daemon(a.session_dir, a.host, a.port))

    return ap


def main():
    args = build_parser().parse_args()
    sys.exit(args.func(args))


if __name__ == "__main__":
    main()
