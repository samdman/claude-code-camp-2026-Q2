#!/usr/bin/env python3
"""
tbaMUD Client - Manage telnet connection and explore game world
Connects to localhost:4000, tracks state in data/player.md and data/world.md
"""

import socket
import time
import sys
import os
import re
from datetime import datetime
from pathlib import Path

class MUDClient:
    def __init__(self, host="localhost", port=4000, username="dummy", password="helloworld"):
        self.host = host
        self.port = port
        self.username = username
        self.password = password
        self.sock = None
        self.data_dir = Path("data")
        self.player_file = self.data_dir / "player.md"
        self.world_file = self.data_dir / "world.md"
        self.data_dir.mkdir(exist_ok=True)

    def connect(self):
        """Connect to MUD via socket"""
        try:
            print(f"Connecting to {self.host}:{self.port}...")
            self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.sock.settimeout(10)
            self.sock.connect((self.host, self.port))
            print("Connected!")
            return True
        except Exception as e:
            print(f"Connection failed: {e}")
            return False

    def send(self, text):
        """Send text to MUD"""
        try:
            self.sock.sendall((text + "\n").encode())
            time.sleep(0.2)
        except Exception as e:
            print(f"Send error: {e}")

    def receive(self, timeout=2):
        """Receive from MUD"""
        try:
            self.sock.settimeout(timeout)
            data = b""
            while True:
                try:
                    chunk = self.sock.recv(1024)
                    if not chunk:
                        break
                    data += chunk
                except socket.timeout:
                    break
            return data.decode('utf-8', errors='ignore')
        except:
            return ""

    def interact(self, prompt_text, response_text, timeout=1):
        """Wait for prompt and send response"""
        data = self.receive(timeout=timeout)
        return data

    # Matches the in-game status prompt, e.g. "21H 100M 31V (news) (motd) >"
    GAME_PROMPT_RE = re.compile("[0-9]+H [0-9]+M [0-9]+V")

    def login(self):
        """Handle login and enter game.

        tbaMUD login has several possible screens after username/password:
          1. If another session for this character is already connected, the
             server just prints "Reconnecting." and drops straight into the
             game (no menu at all).
          2. Otherwise it shows an MOTD screen ending in "*** PRESS RETURN:"
             that must be dismissed with an empty keystroke.
          3. After the MOTD, the "0) ... 5) Delete this character / Make your
             choice:" main menu appears and needs "1" to enter the game.

        The previous implementation assumed the menu would appear right after
        the password and never dismissed the MOTD "PRESS RETURN" screen, so
        the first real game command typed by the caller was silently
        swallowed as the "return" keystroke and the menu choice "1" was
        never actually sent - the session would appear to hang at the menu.
        This version drives an explicit state machine until it sees the
        real in-game status prompt (e.g. "21H 100M 31V ... >").
        """
        print("Reading welcome...")
        time.sleep(0.5)
        welcome = self.receive(timeout=2)
        print(welcome[:200])

        print(f"\nSending username: {self.username}")
        self.send(self.username)
        time.sleep(1.0)

        response = self.receive(timeout=2)
        print(f"Response: {response[:150]}\n")

        # Handle password setup or login
        if "password" in response.lower():
            print("Sending password...")
            self.send(self.password)
            time.sleep(1.0)

            response = self.receive(timeout=2)
            print(f"Response: {response[:150]}\n")

            # If asked to retype, send password again
            if "retype" in response.lower() or "again" in response.lower():
                print("Retyping password...")
                self.send(self.password)
                time.sleep(1.2)
                response = self.receive(timeout=2)
                print(f"Response: {response[:150]}\n")

        # Drive the post-password screens (MOTD press-return, main menu,
        # reconnect banner) until the real in-game prompt shows up.
        max_steps = 8
        for step in range(max_steps):
            if self.GAME_PROMPT_RE.search(response):
                print(f"Step {step}: Game prompt detected - login complete!")
                return True

            lower = response.lower()

            if "press return" in lower or "press enter" in lower:
                print(f"Step {step}: Dismissing MOTD (press return)...")
                self.send("")
                time.sleep(1.0)
                response = self.receive(timeout=2)
                continue

            if "make your choice" in lower:
                print(f"Step {step}: At main menu - entering game (sending '1')...")
                self.send("1")
                time.sleep(1.5)
                response = self.receive(timeout=3)
                continue

            if "reconnecting" in lower:
                print(f"Step {step}: Reconnecting message seen, waiting for game prompt...")
                time.sleep(1.0)
                response = self.receive(timeout=2)
                continue

            # Unrecognized/empty state - poll a bit more before giving up.
            time.sleep(1.0)
            more = self.receive(timeout=2)
            if not more:
                print(f"Step {step}: No further data; assuming already in game.")
                return True
            response = more

        print("Login sequence exhausted max steps; proceeding anyway.")
        return True

    def explore_game(self):
        """Explore game systematically to find bakery"""
        print("="*50)
        print("EXPLORING GAME TO FIND BAKERY")
        print("="*50)

        # Current location
        print("\n>>> look")
        self.send("look")
        time.sleep(0.5)
        location = self.receive(timeout=2)
        print(location)
        self.save_world(f"## Starting Location: Some Muddy Ground\n\n```\n{location}\n```")

        # Try to find help on shops/trading
        print("\n>>> help shop")
        self.send("help shop")
        time.sleep(0.5)
        help_response = self.receive(timeout=2)
        print(help_response)

        # Get available directions
        directions = ["north", "south", "east", "west"]
        visited_locations = {}

        print("\n>>> Exploring map systematically...")
        for direction in directions:
            print(f"\n>>> {direction}")
            self.send(direction)
            time.sleep(0.5)
            location_data = self.receive(timeout=2)
            print(location_data[:300])

            visited_locations[direction] = location_data

            # Check if this location mentions "bakery" or "shop"
            if "bakery" in location_data.lower() or "shop" in location_data.lower() or "merchant" in location_data.lower():
                print(f"\n[FOUND] Bakery/Shop reference in {direction}!")
                self.save_world(f"## Found Shop Reference Going {direction}\n\n```\n{location_data}\n```")

                # Try to interact with shop
                print("\n>>> list")
                self.send("list")
                time.sleep(0.5)
                inventory = self.receive(timeout=2)
                print(inventory)
                self.save_player(f"## Shop Inventory (from {direction})\n\n```\n{inventory}\n```")
                break

            # Go back
            print(f"\n>>> back")
            self.send("back")
            time.sleep(0.3)
            _ = self.receive(timeout=1)

        print("\n[Done exploring]")
        self.save_world(f"## Exploration Summary\n\n**Directions explored:** {', '.join(directions)}")
        return visited_locations

    def save_world(self, data):
        """Save to world.md"""
        try:
            with open(self.world_file, 'a') as f:
                f.write(f"\n### Update - {datetime.now().isoformat()}\n\n")
                f.write(data + "\n")
        except Exception as e:
            print(f"Save error: {e}")

    def save_player(self, data):
        """Save to player.md"""
        try:
            with open(self.player_file, 'a') as f:
                f.write(f"\n### Update - {datetime.now().isoformat()}\n\n")
                f.write(data + "\n")
        except Exception as e:
            print(f"Save error: {e}")

    def levelup_quest(self):
        """Level up to 5 - newbie arena quest"""
        print("="*60)
        print("LEVELING UP QUEST - Newbie Arena")
        print("="*60)
        print("\nObjective: Reach Level 5")
        print("Methods: Defeat creatures, complete tasks, training")
        print("\nStarting interactive mode for leveling...\n")

        # Get current status
        self.send("score")
        time.sleep(0.4)
        status = self.receive(timeout=2)
        print("Current Status:")
        print(status)
        self.save_player("## Leveling Quest Started\n\n**Initial Status:**\n```\n" + status[:500] + "\n```")

        # Navigate to newbie area
        print("\n>>> Navigating to newbie arena...")
        self.send("help newbie")
        time.sleep(0.4)
        help_text = self.receive(timeout=2)
        print(help_text[:300])

        # Start interactive leveling session
        print("\n" + "="*60)
        print("INTERACTIVE LEVELING MODE")
        print("="*60)
        print("\nCommands to use:")
        print("  kill <mob>      - Fight creatures")
        print("  score           - Check level/exp")
        print("  look            - See location")
        print("  north/south/etc - Navigate")
        print("  help training   - Training info")
        print("  /done           - End session when level 5 reached")
        print("  /quit           - Exit\n")

        level = 1
        while level < 5:
            try:
                cmd = input("> ")
                if cmd == "/quit":
                    print("\n[Exiting quest]")
                    break
                if cmd == "/done":
                    print("\n[Checking level...]")
                    self.send("score")
                    time.sleep(0.4)
                    score = self.receive(timeout=2)
                    if "level 5" in score.lower() or "5" in score:
                        print("\n🎉 LEVEL 5 REACHED!")
                        self.save_player("## LEVEL 5 ACHIEVED!\n\n```\n" + score + "\n```")
                        break
                    else:
                        print("Not level 5 yet. Keep going!\n")
                        print(score[:200])

                if cmd.strip():
                    # Log combat actions
                    if 'kill' in cmd.lower():
                        self.save_player(f"**Combat:** {cmd}")

                    self.send(cmd)
                    time.sleep(0.4)
                    response = self.receive(timeout=2)
                    print(response[:400])

                    # Check for level up
                    if "level up" in response.lower() or "congratulations" in response.lower():
                        print("\n🎉 LEVEL UP!")
                        self.save_player(f"**LEVEL UP!** {cmd}\n\n{response[:300]}")
                        level += 1

            except KeyboardInterrupt:
                print("\n\n[Session interrupted]")
                break

    def close(self):
        """Close connection"""
        if self.sock:
            try:
                self.send("quit")
                time.sleep(0.5)
            except:
                pass
            finally:
                self.sock.close()

def main():
    import argparse

    parser = argparse.ArgumentParser(description="tbaMUD Client")
    parser.add_argument("--bakery", action="store_true", help="Find bakery")
    parser.add_argument("--interactive", "-i", action="store_true", help="Interactive")
    parser.add_argument("--levelup", "-l", action="store_true", help="Level up quest - newbie arena")
    parser.add_argument("--command", "-c", help="Send command")
    parser.add_argument("--host", default="localhost", help="MUD host (default: localhost)")
    parser.add_argument("--port", type=int, default=4000, help="MUD port (default: 4000)")
    parser.add_argument("--user", default="dummy", help="Username (default: dummy)")
    parser.add_argument("--password", default="helloworld", help="Password (default: helloworld)")

    args = parser.parse_args()

    client = MUDClient(host=args.host, port=args.port, username=args.user, password=args.password)

    if not client.connect():
        sys.exit(1)

    if not client.login():
        client.close()
        sys.exit(1)

    try:
        if args.bakery:
            client.explore_game()
        elif args.levelup:
            client.levelup_quest()
        elif args.command:
            client.send(args.command)
            time.sleep(0.5)
            response = client.receive(timeout=2)
            print(response)
        elif args.interactive:
            # Make sure we're in the game (send 1 if we see menu)
            time.sleep(0.5)
            check = client.receive(timeout=1)
            if "make your choice" in check.lower():
                print("Entering game...")
                client.send("1")
                time.sleep(2)
                client.receive(timeout=2)  # Clear buffer

            print("="*60)
            print("INTERACTIVE GAMEPLAY MODE - tbaMUD")
            print("="*60)
            print("\nQuick Commands:")
            print("  look          - Examine location")
            print("  score         - Show character stats")
            print("  inventory     - Check inventory")
            print("  kill <mob>    - Attack a creature")
            print("  help leveling - Get leveling tips")
            print("  help newbie   - Newbie information")
            print("  quit          - Exit the game")
            print("  /quit         - Exit this session")
            print("\nType commands freely:\n")

            while True:
                try:
                    cmd = input("> ")
                    if cmd == "/quit" or cmd.lower() == "quit":
                        break
                    if cmd.strip():
                        # Log important commands
                        if any(x in cmd.lower() for x in ['kill', 'cast', 'quest', 'training']):
                            client.save_player(f"**Action:** {cmd}")

                        client.send(cmd)
                        time.sleep(0.4)
                        response = client.receive(timeout=2)
                        print(response)

                        # Track level ups
                        if "congratulations" in response.lower() or "level up" in response.lower():
                            client.save_player(f"**LEVEL UP!** {response[:200]}")
                except KeyboardInterrupt:
                    print("\n\n[Session interrupted]")
                    break
    finally:
        client.close()

if __name__ == "__main__":
    main()
