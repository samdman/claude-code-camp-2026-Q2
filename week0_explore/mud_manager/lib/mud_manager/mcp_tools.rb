require_relative "primitives"

module MudManager
  # Builds the MCP tool table for MUD gameplay: a Hash of tool name to
  # { description:, input_schema:, handler: } exposing the same gameplay
  # surface Boukensha::Tools::Mud used to register directly, now served over
  # MCP by MudManager::McpServer instead.
  #
  # `session` is opened and logged in once by the caller (bin/mud-manager)
  # before the server starts serving requests, and is shared by every tool
  # handler via closure — mirroring how Boukensha::Tools::Mud.register used
  # to share a single Session across ~20 tools.
  module McpTools
    module_function

    def build(session, name:, password:)
      p = Primitives

      send_cmd = lambda do |command|
        session.drain
        session.send_command(command)
        session.read_until_prompt
      end

      guard = lambda do
        "error: not connected — call mud_connect first" unless session.open?
      end

      str_param = ->(desc) { { type: "string", description: desc } }
      int_param = ->(desc) { { type: "integer", description: desc } }

      {
        "mud_connect" => {
          description: "Open the connection to the MUD server and log in with the configured " \
                       "character name and password. Safe to call when already connected " \
                       "(returns current status instead of reconnecting).",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            if session.open?
              "already connected to #{session.host}:#{session.port}"
            else
              begin
                session.open
                welcome = session.login(name, password)
                "connected to #{session.host}:#{session.port}\n#{welcome}"
              rescue MudManager::Session::Error => e
                "error: #{e.message}"
              end
            end
          end
        },

        "mud_disconnect" => {
          description: "Close the connection to the MUD server gracefully.",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            if session.open?
              session.close
              "disconnected"
            else
              "already disconnected"
            end
          end
        },

        "mud_status" => {
          description: "Return whether the MUD session is currently connected.",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            session.open? ? "connected to #{session.host}:#{session.port}" : "disconnected"
          end
        },

        "look" => {
          description: "Look at the current room or at a specific target. " \
                       "Call with NO arguments to describe the current room (do NOT pass target: 'room'). " \
                       "Pass a target to inspect a specific item, mob, or player (e.g. target: 'sword'). " \
                       "Use preposition 'in' to look inside a container, 'at' to inspect something, " \
                       "or a direction (north/east/south/west/up/down) to peek into an adjacent room.",
          input_schema: {
            type: "object",
            properties: {
              "target"      => str_param.call("Item, mob, or player name to inspect. Omit entirely to describe the current room."),
              "preposition" => str_param.call("Preposition: in, at, north, east, south, west, up, down (optional)")
            },
            required: []
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.look(target: args["target"], preposition: args["preposition"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "examine" => {
          description: "Examine a target in detail (more verbose than look).",
          input_schema: {
            type: "object",
            properties: { "target" => str_param.call("The item, mob, or player to examine") },
            required: ["target"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.examine(args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "check" => {
          description: "Query information about your character or surroundings. " \
                       "Kinds: score, inventory, equipment, gold, exits, time, weather, " \
                       "levels, wimpy, toggle, where.",
          input_schema: {
            type: "object",
            properties: { "kind" => str_param.call("What to check: score | inventory | equipment | gold | exits | time | weather | levels | wimpy | toggle | where") },
            required: ["kind"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.info_self(args["kind"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "move" => {
          description: "Move in a compass direction or up/down.",
          input_schema: {
            type: "object",
            properties: { "direction" => str_param.call("Direction: north | east | south | west | up | down") },
            required: ["direction"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.move(args["direction"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "flee" => {
          description: "Attempt to flee from combat in a random available direction.",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            next guard.call if guard.call
            send_cmd.call(p.flee)
          end
        },

        "set_position" => {
          description: "Change body position. Use 'rest' or 'sleep' between fights to recover " \
                       "HP and mana. Must be standing to move or fight.",
          input_schema: {
            type: "object",
            properties: { "position" => str_param.call("Position: stand | sit | rest | sleep | wake") },
            required: ["position"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.set_position(args["position"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "track" => {
          description: "Attempt to track a mob or player by name, revealing which direction " \
                       "they are in. Requires the Track skill.",
          input_schema: {
            type: "object",
            properties: { "target" => str_param.call("Name of the mob or player to track") },
            required: ["target"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.track(args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "attack" => {
          description: "Attack a target. Style 'kill' is the standard approach; " \
                       "'murder' bypasses the mercy check; 'hit' is a one-off strike.",
          input_schema: {
            type: "object",
            properties: {
              "target" => str_param.call("Name of the mob or player to attack"),
              "style"  => str_param.call("Attack style: kill | hit | murder (default: kill)")
            },
            required: ["target"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.attack(args["style"] || "kill", args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "skill_strike" => {
          description: "Use a combat skill against a target.",
          input_schema: {
            type: "object",
            properties: {
              "skill"  => str_param.call("Skill: bash | kick | backstab | rescue | assist"),
              "target" => str_param.call("Name of the mob or player")
            },
            required: %w[skill target]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.skill_strike(args["skill"], args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "consider" => {
          description: "Assess a mob's relative strength before engaging in combat. " \
                       "Returns a phrase such as 'You could kill it easily' or " \
                       "'Death awaits you'. Always consider before attacking an unknown mob.",
          input_schema: {
            type: "object",
            properties: { "target" => str_param.call("Name of the mob to consider") },
            required: ["target"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.consider(args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "say" => {
          description: "Speak or emote in the current room.",
          input_schema: {
            type: "object",
            properties: {
              "text" => str_param.call("What to say or emote"),
              "mode" => str_param.call("Mode: say | emote | reply (default: say)")
            },
            required: ["text"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.say_local(args["mode"] || "say", args["text"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "tell" => {
          description: "Send a private message to a specific player.",
          input_schema: {
            type: "object",
            properties: {
              "target" => str_param.call("Player name to message"),
              "text"   => str_param.call("The message"),
              "mode"   => str_param.call("Mode: tell | whisper | ask (default: tell)")
            },
            required: %w[target text]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.say_targeted(args["mode"] || "tell", args["target"], args["text"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "channel_say" => {
          description: "Broadcast a message over a global channel.",
          input_schema: {
            type: "object",
            properties: {
              "channel" => str_param.call("Channel: shout | gossip | auction | grats | holler"),
              "text"    => str_param.call("The message to broadcast")
            },
            required: %w[channel text]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.say_channel(args["channel"], args["text"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "get_item" => {
          description: "Pick up an item from the room or from a container.",
          input_schema: {
            type: "object",
            properties: {
              "item"      => str_param.call("Name of the item to get"),
              "container" => str_param.call("Container to get it from (optional)"),
              "count"     => int_param.call("Number of items to get (optional)")
            },
            required: ["item"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.get(args["item"], container: args["container"], count: args["count"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "drop_item" => {
          description: "Drop, donate, or junk an item.",
          input_schema: {
            type: "object",
            properties: {
              "item"  => str_param.call("Name of the item"),
              "mode"  => str_param.call("Mode: drop | donate | junk (default: drop)"),
              "count" => int_param.call("Number of items (optional)")
            },
            required: ["item"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.drop(args["mode"] || "drop", args["item"], count: args["count"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "put_item" => {
          description: "Put an item into a container.",
          input_schema: {
            type: "object",
            properties: {
              "item"      => str_param.call("Name of the item to put"),
              "container" => str_param.call("Name of the container"),
              "count"     => int_param.call("Number of items (optional)")
            },
            required: %w[item container]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.put(args["item"], args["container"], count: args["count"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "equip_item" => {
          description: "Wear, wield, hold, grab, or remove an item.",
          input_schema: {
            type: "object",
            properties: {
              "item"     => str_param.call("Name of the item"),
              "action"   => str_param.call("Action: wear | wield | hold | grab | remove"),
              "body_loc" => str_param.call("Body location to wear on (optional, e.g. 'head', 'finger')")
            },
            required: %w[item action]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.equip(args["action"], args["item"], body_loc: args["body_loc"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "consume_item" => {
          description: "Eat, drink, taste, or sip a consumable item.",
          input_schema: {
            type: "object",
            properties: {
              "item" => str_param.call("Name of the item to consume"),
              "mode" => str_param.call("Mode: eat | drink | taste | sip (default: eat)")
            },
            required: ["item"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.consume(args["mode"] || "eat", args["item"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "cast_spell" => {
          description: "Cast a spell, optionally at a target.",
          input_schema: {
            type: "object",
            properties: {
              "spell"  => str_param.call("Full spell name (e.g. 'cure light wounds', 'magic missile')"),
              "target" => str_param.call("Target mob, player, or object (optional)")
            },
            required: ["spell"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.cast(args["spell"], target: args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "use_magic_item" => {
          description: "Activate a magic item: quaff a potion, recite a scroll, or use a wand/staff.",
          input_schema: {
            type: "object",
            properties: {
              "item"        => str_param.call("Name of the item to activate"),
              "mode"        => str_param.call("Mode: quaff | recite | use"),
              "target_args" => str_param.call("Optional target arguments (e.g. mob name for a wand)")
            },
            required: %w[item mode]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.use_magic_item(args["mode"], args["item"], target_args: args["target_args"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "shop" => {
          description: "Interact with a shop NPC: list stock, buy, sell, or get the value of an item.",
          input_schema: {
            type: "object",
            properties: {
              "action" => str_param.call("Action: list | buy | sell | value | offer"),
              "args"   => str_param.call("Item name or number (optional)")
            },
            required: ["action"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.shop(args["action"], args: args["args"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "practice" => {
          description: "List your known skills at a guildmaster, or practice a specific skill.",
          input_schema: {
            type: "object",
            properties: { "skill" => str_param.call("Skill name to practice (omit to list all)") },
            required: []
          },
          handler: lambda do |args|
            next guard.call if guard.call
            send_cmd.call(p.practice(args["skill"]))
          end
        },

        "save_character" => {
          description: "Save your character to disk so progress is not lost on disconnect.",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            next guard.call if guard.call
            send_cmd.call(p.save_char)
          end
        },

        "send_raw" => {
          description: "Send an arbitrary command string to the MUD and return the response. " \
                       "Use this as an escape hatch when no structured tool fits.",
          input_schema: {
            type: "object",
            properties: { "command" => str_param.call("The raw command to send (e.g. 'who', 'help backstab')") },
            required: ["command"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            session.send_command(args["command"])
            session.read_until_quiet
          end
        }
      }
    end
  end
end
