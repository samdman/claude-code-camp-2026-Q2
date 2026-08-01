#!/usr/bin/env python3
"""
Test MUD connection with debugging
"""

import socket
import time

sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect(("localhost", 4000))

def send(text):
    sock.sendall((text + "\n").encode())
    time.sleep(0.3)

def recv(timeout=2):
    sock.settimeout(timeout)
    data = b""
    try:
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            data += chunk
    except socket.timeout:
        pass
    return data.decode('utf-8', errors='ignore')

# Sequences
print("1. Wait for protocol")
time.sleep(1)
r = recv(3)
print(f"Got {len(r)} bytes\n")

print("2. Send 'dummy'")
send("dummy")
r = recv(2)
print(f"Got {len(r)} bytes\n")

print("3. Send 'y'")
send("y")
r = recv(2)
print(f"Got {len(r)} bytes\n")

print("4. Send 'helloworld'")
send("helloworld")
r = recv(3)
print(f"Got {len(r)} bytes")
print(f"Contains 'PRESS RETURN': {'press return' in r.lower()}\n")

print("4b. Send empty line (press return)")
send("")
r = recv(2)
print(f"Got {len(r)} bytes")
print(f"Contains 'make your choice': {'make your choice' in r.lower()}\n")

print("5. Send '1'")
send("1")
time.sleep(2)
r = recv(3)
print(f"Got {len(r)} bytes")
print(f"Contains 'make your choice': {'make your choice' in r.lower()}\n")

print("6. Send 'look'")
send("look")
r = recv(2)
print(f"Got {len(r)} bytes")
print("Output:")
print(r[:500])

sock.close()
