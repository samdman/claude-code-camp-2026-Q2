using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Boukensha.Core.Knowledge;

public sealed record RoomRecord(int Id, string Fingerprint, string Name, string Description, int VisitCount);

public sealed class KnowledgeStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public KnowledgeStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        CreateSchema();
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
        return new RoomRecord(reader.GetInt32(0), fingerprint, name, description, reader.GetInt32(1));
    }

    public void RecordExits(int roomId, IReadOnlyDictionary<string, string?> directionToDestinationHint)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (direction, hint) in directionToDestinationHint)
        {
            using var upsert = _connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO exits (room_id, direction, to_room_name_hint, state, updated_at)
                VALUES ($roomId, $direction, $hint, 'frontier', $now)
                ON CONFLICT(room_id, direction) DO UPDATE SET
                    to_room_name_hint = CASE WHEN state = 'frontier' THEN excluded.to_room_name_hint ELSE to_room_name_hint END,
                    updated_at = CASE WHEN state = 'frontier' THEN $now ELSE updated_at END;
                """;
            upsert.Parameters.AddWithValue("$roomId", roomId);
            upsert.Parameters.AddWithValue("$direction", direction);
            upsert.Parameters.AddWithValue("$hint", (object?)hint ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();
        }
    }

    public void LinkExit(int fromRoomId, string direction, int toRoomId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
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

    public void SetCurrentRoom(int roomId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO location (id, current_room_id, updated_at) VALUES (1, $roomId, $now)
            ON CONFLICT(id) DO UPDATE SET current_room_id = $roomId, updated_at = $now;
            """;
        cmd.Parameters.AddWithValue("$roomId", roomId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
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

    private static string ComputeFingerprint(string name, string description)
    {
        var normalized = $"{Normalize(name)}\n{Normalize(description)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");
}
