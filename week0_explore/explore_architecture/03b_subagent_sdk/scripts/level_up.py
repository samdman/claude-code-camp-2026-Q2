#!/usr/bin/env python3
"""
Level up to 5 by fighting creatures in the newbie zone
"""

import socket
import time

sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect(("localhost", 4000))

def send(text):
    sock.sendall((text + "\n").encode())
    time.sleep(0.2)

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

# Login
time.sleep(1)
_ = recv(3)
send("dummy")
time.sleep(0.5)
_ = recv(1)
send("y")
time.sleep(0.5)
_ = recv(1)
send("helloworld")
time.sleep(1)
_ = recv(2)
send("")
time.sleep(0.5)
_ = recv(1)
send("1")
time.sleep(2)
_ = recv(2)

print("[Logged in]\n")

# Navigate to School area
commands = [
    "north",   # Try north to light area
    "north",   # Keep going north
    "east",    # Try east
    "east",    # More east
    "look",
]

for cmd in commands:
    send(cmd)
    time.sleep(0.4)
    output = recv(2)
    if "school" in output.lower() or "training" in output.lower() or "bright" in output.lower():
        print(f"Found good area with '{cmd}'!")
        print(output[:200])
        break
    elif "pitch black" not in output.lower():
        print(f"'{cmd}' -> not dark!")
        print(output[:150])

# Try to find and fight creatures
print("\n[Looking for creatures to fight]\n")
creatures_fought = 0
level = 1

for i in range(20):
    # Check score
    send("score")
    time.sleep(0.3)
    score_out = recv(2)

    # Extract level from score
    if "level 5" in score_out.lower():
        print("\n🎉 LEVEL 5 REACHED!")
        break
    elif "level 2" in score_out.lower():
        level = 2
        print("✓ Level 2!")
    elif "level 3" in score_out.lower():
        level = 3
        print("✓ Level 3!")
    elif "level 4" in score_out.lower():
        level = 4
        print("✓ Level 4!")

    # Try to find and kill a creature
    send("look")
    time.sleep(0.3)
    look_out = recv(2)

    # Try killing common creatures
    for mob in ["rat", "crab", "bat", "spider", "snake", "goblin"]:
        if mob in look_out.lower():
            send(f"kill {mob}")
            time.sleep(0.5)
            fight_out = recv(3)

            if "hit you" in fight_out.lower() or "you hit" in fight_out.lower():
                creatures_fought += 1
                print(f"  Fought {mob} (#{creatures_fought})")

                # Check if leveled
                if "level up" in fight_out.lower() or "congratulations" in fight_out.lower():
                    print(f"  ✨ LEVEL UP!")
            break
    else:
        # No mob found in current room
        # Try moving
        send("north")
        time.sleep(0.3)
        _ = recv(2)

print(f"\n[Session complete - Fought {creatures_fought} creatures]")
sock.close()
