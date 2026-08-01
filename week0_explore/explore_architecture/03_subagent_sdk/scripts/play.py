#!/usr/bin/env python3
"""
Simple MUD player - handles menu properly and executes commands
"""

import socket
import time
import sys

def mud_play(commands):
    """Connect to MUD, login, enter game, and execute commands"""

    # Connect
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(10)
    sock.connect(("localhost", 4000))
    print("[Connected to MUD]")

    def send(text):
        """Send command"""
        sock.sendall((text + "\n").encode())
        time.sleep(0.2)

    def recv(timeout=2):
        """Receive data"""
        sock.settimeout(timeout)
        data = b""
        try:
            while True:
                chunk = sock.recv(2048)
                if not chunk:
                    break
                data += chunk
        except socket.timeout:
            pass
        return data.decode('utf-8', errors='ignore')

    # Skip initial welcome and protocol detection
    print("[Waiting for prompt...]")
    time.sleep(1)
    resp = recv(timeout=3)
    print(f"[Initial response length: {len(resp)} chars]")

    # Send username
    print("[Sending username: dummy]")
    send("dummy")
    time.sleep(1)
    resp = recv(timeout=2)

    # Confirm name with 'y'
    print("[Confirming name with 'y']")
    send("y")
    time.sleep(1)
    resp = recv(timeout=2)

    # Send password
    print("[Sending password]")
    send("helloworld")
    time.sleep(1)
    resp = recv(timeout=2)

    # Send empty line for "PRESS RETURN" prompt
    print("[Pressing return...]")
    send("")
    time.sleep(0.5)
    resp = recv(timeout=1)

    # Send "1" to enter the game
    print("[Entering game with '1']")
    send("1")
    time.sleep(2)
    resp = recv(timeout=2)
    print("[Successfully logged in and entered game!]")

    # Execute commands
    print(f"\n[Executing {len(commands)} commands]\n")
    for cmd in commands:
        if cmd.strip():
            send(cmd)
            time.sleep(0.4)
            output = recv(timeout=2)
            print(f"{output}")

    # Quit
    print("\n[Closing connection]")
    send("quit")
    time.sleep(0.5)
    sock.close()

if __name__ == "__main__":
    commands = sys.argv[1:] if len(sys.argv) > 1 else []

    if not commands:
        # Default: find bakery and check level
        commands = [
            "look",
            "score",
            "shops",
            "go bakery",
            "list"
        ]

    mud_play(commands)
