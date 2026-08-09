using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Boukensha.Core.Knowledge;

public sealed record RoomRecord(int Id, string Fingerprint, string Name, string Description, int VisitCount);

public sealed record ExitRecord(string Direction, string State, string? ToRoomName, string? Hint, int? ToRoomId);

public sealed class KnowledgeStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _changeLogPath;
    private readonly Lock _changeLogLock = new();
    private readonly string? _sessionId;

    public KnowledgeStore(string path, string? sessionId = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _sessionId = sessionId;

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        CreateSchema();

        _changeLogPath = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "knowledge_changes.jsonl");
    }

    public RoomRecord UpsertRoom(string name, string description)
    {
        var fingerprint = ComputeFingerprint(name, description);
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var upsert = _connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO rooms (fingerprint, name, description, first_seen_at, last_seen_at, visit_count)
            VALUES ($fingerprint, $name, $description, $now, $now, 1)
            ON CONFLICT(fingerprint) DO UPDATE SET
                last_seen_at = $now,
                visit_count = visit_count + 1
            RETURNING id, visit_count;
            """;
        upsert.Parameters.AddWithValue("$fingerprint", fingerprint);
        upsert.Parameters.AddWithValue("$name", name);
        upsert.Parameters.AddWithValue("$description", description);
        upsert.Parameters.AddWithValue("$now", now);

        using var reader = upsert.ExecuteReader();
        reader.Read();
        var id = reader.GetInt32(0);
        var visitCount = reader.GetInt32(1);

        RecordChange("room_upserted",
            before: visitCount == 1 ? null : new { id, visit_count = visitCount - 1 },
            after: new { id, name, description, visit_count = visitCount });

        return new RoomRecord(id, fingerprint, name, description, visitCount);
    }

    public void RecordExits(int roomId, IReadOnlyDictionary<string, string?> directionToDestinationHint)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (direction, hint) in directionToDestinationHint)
        {
            var previousState = GetExitState(roomId, direction);
            if (previousState == "walked") continue;

            using var upsert = _connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO exits (room_id, direction, to_room_name_hint, state, updated_at)
                VALUES ($roomId, $direction, $hint, 'frontier', $now)
                ON CONFLICT(room_id, direction) DO UPDATE SET
                    to_room_name_hint = excluded.to_room_name_hint,
                    updated_at = $now;
                """;
            upsert.Parameters.AddWithValue("$roomId", roomId);
            upsert.Parameters.AddWithValue("$direction", direction);
            upsert.Parameters.AddWithValue("$hint", (object?)hint ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();

            RecordChange("exit_recorded",
                before: new { room_id = roomId, direction, state = previousState },
                after: new { room_id = roomId, direction, state = "frontier", hint });
        }
    }

    public void LinkExit(int fromRoomId, string direction, int toRoomId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var previousState = GetExitState(fromRoomId, direction);

        using var upsert = _connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO exits (room_id, direction, to_room_id, state, updated_at)
            VALUES ($roomId, $direction, $toRoomId, 'walked', $now)
            ON CONFLICT(room_id, direction) DO UPDATE SET
                to_room_id = $toRoomId,
                state = 'walked',
                to_room_name_hint = NULL,
                updated_at = $now;
            """;
        upsert.Parameters.AddWithValue("$roomId", fromRoomId);
        upsert.Parameters.AddWithValue("$direction", direction);
        upsert.Parameters.AddWithValue("$toRoomId", toRoomId);
        upsert.Parameters.AddWithValue("$now", now);
        upsert.ExecuteNonQuery();

        RecordChange("exit_linked",
            before: new { room_id = fromRoomId, direction, state = previousState },
            after: new { room_id = fromRoomId, direction, state = "walked", to_room_id = toRoomId });
    }

    public RoomRecord? GetCurrentRoom()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.fingerprint, r.name, r.description, r.visit_count
            FROM location l JOIN rooms r ON r.id = l.current_room_id
            WHERE l.id = 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new RoomRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4));
    }

    public IReadOnlyList<RoomRecord> ListRooms()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, fingerprint, name, description, visit_count FROM rooms ORDER BY last_seen_at DESC;";
        using var reader = cmd.ExecuteReader();
        var rooms = new List<RoomRecord>();
        while (reader.Read())
        {
            rooms.Add(new RoomRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }
        return rooms;
    }

    public IReadOnlyList<ExitRecord> ListExits(int roomId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.direction, e.state, dest.name, e.to_room_name_hint, e.to_room_id
            FROM exits e LEFT JOIN rooms dest ON dest.id = e.to_room_id
            WHERE e.room_id = $roomId ORDER BY e.direction;
            """;
        cmd.Parameters.AddWithValue("$roomId", roomId);
        using var reader = cmd.ExecuteReader();
        var exits = new List<ExitRecord>();
        while (reader.Read())
        {
            exits.Add(new ExitRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }
        return exits;
    }

    public void SetCurrentRoom(int roomId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var previousRoomId = GetCurrentRoom()?.Id;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO location (id, current_room_id, updated_at) VALUES (1, $roomId, $now)
            ON CONFLICT(id) DO UPDATE SET current_room_id = $roomId, updated_at = $now;
            """;
        cmd.Parameters.AddWithValue("$roomId", roomId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        RecordChange("location_changed",
            before: previousRoomId is null ? null : new { room_id = previousRoomId },
            after: new { room_id = roomId });
    }

    /// <summary>
    /// Marks the current location as unknown -- used when a transition (move/flee)
    /// lands somewhere unparseable (e.g. a dark room), so a stale current_room_id
    /// doesn't cause later tool results to be misattributed to a room the player
    /// has actually already left.
    /// </summary>
    public void ClearCurrentRoom()
    {
        var previousRoomId = GetCurrentRoom()?.Id;
        if (previousRoomId is null) return;

        var now = DateTimeOffset.UtcNow.ToString("O");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO location (id, current_room_id, updated_at) VALUES (1, NULL, $now)
            ON CONFLICT(id) DO UPDATE SET current_room_id = NULL, updated_at = $now;
            """;
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        RecordChange("location_cleared", before: new { room_id = previousRoomId }, after: null);
    }

    public string BuildHereBlock()
    {
        var current = GetCurrentRoom();
        if (current is null) return string.Empty;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.direction, e.state, dest.name
            FROM exits e LEFT JOIN rooms dest ON dest.id = e.to_room_id
            WHERE e.room_id = $roomId ORDER BY e.direction;
            """;
        cmd.Parameters.AddWithValue("$roomId", current.Id);

        var exitParts = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var direction = reader.GetString(0);
                var state = reader.GetString(1);
                var letter = direction[0];
                exitParts.Add(state == "walked" && !reader.IsDBNull(2)
                    ? $"{letter}→{reader.GetString(2)} ✓"
                    : $"{letter}→?");
            }
        }

        var exitsLine = exitParts.Count > 0 ? string.Join(" | ", exitParts) : "(none surveyed)";
        return $"[here] {current.Name} (visit {current.VisitCount})\nexits: {exitsLine}";
    }

    public void Dispose() => _connection.Dispose();

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rooms (
                id INTEGER PRIMARY KEY,
                fingerprint TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                visit_count INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS exits (
                room_id INTEGER NOT NULL REFERENCES rooms(id),
                direction TEXT NOT NULL,
                to_room_id INTEGER REFERENCES rooms(id),
                to_room_name_hint TEXT,
                state TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (room_id, direction)
            );
            CREATE TABLE IF NOT EXISTS location (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                current_room_id INTEGER REFERENCES rooms(id),
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private string? GetExitState(int roomId, string direction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT state FROM exits WHERE room_id = $roomId AND direction = $direction;";
        cmd.Parameters.AddWithValue("$roomId", roomId);
        cmd.Parameters.AddWithValue("$direction", direction);
        return cmd.ExecuteScalar() as string;
    }

    private void RecordChange(string kind, object? before, object? after)
    {
        var evt = new Dictionary<string, object?>
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["session_id"] = _sessionId,
            ["kind"] = kind,
            ["before"] = before,
            ["after"] = after,
        };
        lock (_changeLogLock)
        {
            using var writer = new StreamWriter(new FileStream(_changeLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
            writer.WriteLine(JsonSerializer.Serialize(evt));
        }
    }

    private static string ComputeFingerprint(string name, string description)
    {
        var normalized = $"{Normalize(name)}\n{Normalize(description)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");
}
