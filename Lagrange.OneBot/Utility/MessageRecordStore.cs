using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Lagrange.Core.Message;
using Lagrange.OneBot.Database;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static Lagrange.Core.Message.MessageChain;

namespace Lagrange.OneBot.Utility;

public sealed class MessageRecordStore
{
    private const int CleanupBatchSize = 500;
    private const int MemoryCleanupBatchSize = 1000;
    private const int CurrentSchemaVersion = 1;
    private const int ContentCodecBrotliMessagePack = 1;

    private static readonly Regex DurationPartRegex = new(@"(?<amount>\d+(?:\.\d+)?)(?<unit>[a-zA-Z]+)", RegexOptions.Compiled);
    private static readonly Regex SizeRegex = new(@"^(?<amount>\d+(?:\.\d+)?)(?<unit>B|K|KB|M|MB|G|GB)$", RegexOptions.Compiled);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MemoryCleanupInterval = TimeSpan.FromSeconds(10);

    private readonly Dictionary<int, MemoryRecord> _memoryRecords = new();
    private readonly object _memoryLock = new();
    private readonly object _sqliteLock = new();
    private readonly object _cleanupLock = new();
    private readonly ILogger<MessageRecordStore> _logger;
    private readonly TimeSpan _memoryRetention;
    private readonly TimeSpan _fullContentRetention;
    private readonly RetentionPolicy _retention;
    private readonly bool _databaseEnabled;
    private readonly bool _persistRecentFullContent;
    private readonly bool _persistFullContent;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly string? _legacyDatabasePath;
    private bool _sqliteAvailable;
    private bool _legacyChecked;
    private DateTimeOffset _lastMemoryCleanup = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private int _cleanupRunning;

    public MessageRecordStore(IConfiguration configuration, ILogger<MessageRecordStore> logger)
    {
        _logger = logger;
        _databaseEnabled = configuration.GetValue("Database:Enable", true);
        _memoryRetention = TimeSpan.FromSeconds(GetPositiveInt(configuration, "Database:MemoryRetentionSeconds", 60));
        _fullContentRetention = TimeSpan.FromSeconds(GetPositiveInt(configuration, "Database:FullContentRetentionSeconds", 86400));
        _persistRecentFullContent = configuration.GetValue("Database:PersistRecentFullContent", false);
        _persistFullContent = configuration.GetValue("Database:PersistFullContent", false);
        _retention = ParseRetention(configuration["Database:Retention"]);
        _databasePath = ResolveDatabasePath(configuration);
        _legacyDatabasePath = ResolveLegacyDatabasePath(configuration);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        if (_databaseEnabled) InitializeSqlite();
    }

    public void Save(MessageChain chain) => Save((MessageRecord)chain);

    public void Save(MessageRecord record)
    {
        SaveMemory(record);
        if (!_sqliteAvailable) return;

        try
        {
            lock (_sqliteLock)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                UpsertIndex(connection, transaction, record);
                if (_persistFullContent || _persistRecentFullContent)
                {
                    UpsertContent(connection, transaction, record);
                }
                transaction.Commit();
            }

            ScheduleCleanup();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SQLite message database write failed; falling back to memory cache for this message");
        }
    }

    public MessageChain? GetMessageChain(int messageId)
    {
        if (GetMemoryRecord(messageId) is { } memory) return (MessageChain)memory;
        return GetIndex(messageId) is { } index ? (MessageChain)index : null;
    }

    public MessageContentLookup GetFullMessageChain(int messageId)
    {
        if (GetMemoryRecord(messageId) is { } memory) return MessageContentLookup.Found((MessageChain)memory);
        if (!_sqliteAvailable) return MessageContentLookup.NotFound();

        try
        {
            lock (_sqliteLock)
            {
                using var connection = OpenConnection();
                var index = GetIndex(connection, messageId);
                if (index == null) return MessageContentLookup.NotFound();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT codec, body FROM message_content WHERE id = $id";
                command.Parameters.AddWithValue("$id", messageId);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return MessageContentLookup.ContentExpired(ToAnchorChain(index));

                int codec = reader.GetInt32(0);
                byte[] body = (byte[])reader["body"];
                if (codec != ContentCodecBrotliMessagePack) return MessageContentLookup.ContentExpired(ToAnchorChain(index));

                return MessageContentLookup.Found(DecompressRecord(body));
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SQLite message database read failed");
            return MessageContentLookup.NotFound();
        }
    }

    public ulong? GetSequence(int messageId) => GetIndex(messageId)?.Sequence;

    public EmojiLikeTarget? GetEmojiLikeTarget(int messageId)
    {
        return GetIndex(messageId) is { } record
            ? new EmojiLikeTarget(record.Type, record.Sequence, record.FromUin, record.ToUin)
            : null;
    }

    public int? GetGroupMessageIdBySequence(uint groupUin, uint sequence)
    {
        if (TryFindMemory(record => record.Type == MessageType.Group && record.ToUin == groupUin && record.Sequence == sequence) is { } memory)
        {
            return memory.Id;
        }

        if (!_sqliteAvailable) return null;
        try
        {
            lock (_sqliteLock)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id FROM message_index
                    WHERE type = $type AND to_uin = $toUin AND sequence = $sequence
                    LIMIT 1
                    """;
                command.Parameters.AddWithValue("$type", (int)MessageType.Group);
                command.Parameters.AddWithValue("$toUin", (long)groupUin);
                command.Parameters.AddWithValue("$sequence", (long)sequence);
                return command.ExecuteScalar() is long id ? (int)id : null;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SQLite message database read failed");
            return null;
        }
    }

    public ulong? FindFriendSequence(uint friendUin, uint clientSequence, ulong messageId)
    {
        if (TryFindMemory(record => record.Type == MessageType.Friend
                && (record.FromUin == friendUin || record.ToUin == friendUin)
                && record.ClientSequence == clientSequence
                && record.MessageId == messageId) is { } memory)
        {
            return memory.Sequence;
        }

        if (!_sqliteAvailable) return null;
        try
        {
            lock (_sqliteLock)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT sequence FROM message_index
                    WHERE type = $type
                      AND (from_uin = $friendUin OR to_uin = $friendUin)
                      AND client_sequence = $clientSequence
                      AND message_id = $messageId
                    LIMIT 1
                    """;
                command.Parameters.AddWithValue("$type", (int)MessageType.Friend);
                command.Parameters.AddWithValue("$friendUin", (long)friendUin);
                command.Parameters.AddWithValue("$clientSequence", (long)clientSequence);
                command.Parameters.AddWithValue("$messageId", (long)messageId);
                return command.ExecuteScalar() is long sequence ? (ulong)sequence : null;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SQLite message database read failed");
            return null;
        }
    }

    public uint? GetGroupSequenceForHistory(uint groupId, int messageId)
    {
        if (messageId != 0) return GetIndex(messageId) is { } index ? (uint)index.Sequence : null;

        if (TryFindMemory(record => record.Type == MessageType.Group && record.ToUin == groupId, orderBySequence: true) is { } memory)
        {
            return (uint)memory.Sequence;
        }

        if (!_sqliteAvailable) return null;
        try
        {
            lock (_sqliteLock)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT sequence FROM message_index
                    WHERE type = $type AND to_uin = $groupId
                    ORDER BY sequence DESC
                    LIMIT 1
                    """;
                command.Parameters.AddWithValue("$type", (int)MessageType.Group);
                command.Parameters.AddWithValue("$groupId", (long)groupId);
                return command.ExecuteScalar() is long sequence ? (uint)sequence : null;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SQLite message database read failed");
            return null;
        }
    }

    public MessageChain? GetFriendHistoryChain(uint userId, int messageId)
    {
        if (messageId != 0) return GetMessageChain(messageId);

        if (TryFindMemory(record => record.Type == MessageType.Friend && (record.FromUin == userId || record.ToUin == userId), orderBySequence: true) is { } memory)
        {
            return (MessageChain)memory;
        }

        if (!_sqliteAvailable) return null;
        try
        {
            lock (_sqliteLock)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, type, sequence, client_sequence, message_id, time, from_uin, to_uin
                    FROM message_index
                    WHERE type = $type AND (from_uin = $userId OR to_uin = $userId)
                    ORDER BY sequence DESC
                    LIMIT 1
                    """;
                command.Parameters.AddWithValue("$type", (int)MessageType.Friend);
                command.Parameters.AddWithValue("$userId", (long)userId);
                using var reader = command.ExecuteReader();
                return reader.Read() ? ToAnchorChain(ReadIndex(reader)) : null;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SQLite message database read failed");
            return null;
        }
    }

    private void InitializeSqlite()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath) ?? ".");
            WarnLegacyDatabases();

            lock (_sqliteLock)
            {
                using var connection = OpenConnection();
                ExecuteNonQuery(connection, $"PRAGMA user_version = {CurrentSchemaVersion}");
                ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL");
                ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL");
                ExecuteNonQuery(connection, """
                    CREATE TABLE IF NOT EXISTS message_index (
                        id INTEGER PRIMARY KEY,
                        type INTEGER NOT NULL,
                        sequence INTEGER NOT NULL,
                        client_sequence INTEGER NOT NULL,
                        message_id INTEGER NOT NULL,
                        time INTEGER NOT NULL,
                        from_uin INTEGER NOT NULL,
                        to_uin INTEGER NOT NULL
                    )
                    """);
                ExecuteNonQuery(connection, """
                    CREATE TABLE IF NOT EXISTS message_content (
                        id INTEGER PRIMARY KEY,
                        codec INTEGER NOT NULL,
                        body BLOB NOT NULL,
                        saved_at INTEGER NOT NULL,
                        FOREIGN KEY(id) REFERENCES message_index(id) ON DELETE CASCADE
                    )
                    """);
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_message_time ON message_index(time)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_group_seq ON message_index(type, to_uin, sequence)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_friend_lookup ON message_index(type, from_uin, to_uin, client_sequence, message_id)");
                ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_content_saved_at ON message_content(saved_at)");
            }

            _sqliteAvailable = true;
        }
        catch (Exception e)
        {
            _sqliteAvailable = false;
            _logger.LogWarning(e, "SQLite message database is unavailable; falling back to memory-only message cache");
        }
    }

    private void WarnLegacyDatabases()
    {
        if (_legacyChecked) return;
        _legacyChecked = true;

        string prefix = Path.GetDirectoryName(_databasePath) ?? ".";
        string realmPath = Path.Combine(prefix, ".realm");
        bool hasRealm = File.Exists(realmPath) || Directory.EnumerateFiles(prefix, "*.realm*", SearchOption.TopDirectoryOnly).Any();
        bool hasLiteDb = Directory.EnumerateFiles(prefix, "*.db", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(prefix, "*.litedb", SearchOption.TopDirectoryOnly))
            .Any(path => !Path.GetFileName(path).Equals(Path.GetFileName(_databasePath), StringComparison.OrdinalIgnoreCase));
        if (_legacyDatabasePath != null && File.Exists(_legacyDatabasePath))
        {
            if (Path.GetExtension(_legacyDatabasePath).Equals(".realm", StringComparison.OrdinalIgnoreCase)) hasRealm = true;
            else hasLiteDb = true;
        }

        if (hasRealm)
        {
            _logger.LogWarning("Legacy Realm message database detected under {Path}. This version will not read or migrate it. Use an older build/fork to access it, or delete it manually if it is not needed.", prefix);
        }

        if (hasLiteDb)
        {
            _logger.LogWarning("Legacy LiteDB message database detected under {Path}. This version will not read or migrate it. Use an older build/fork to access it, or delete it manually if it is not needed.", prefix);
        }
    }

    private MessageRecord? GetIndex(int messageId)
    {
        if (GetMemoryRecord(messageId) is { } memory) return memory;
        if (!_sqliteAvailable) return null;

        try
        {
            lock (_sqliteLock)
            {
                using var connection = OpenConnection();
                return GetIndex(connection, messageId) is { } index ? ToRecord(index) : null;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SQLite message database read failed");
            return null;
        }
    }

    private MessageIndex? GetIndex(SqliteConnection connection, int messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, type, sequence, client_sequence, message_id, time, from_uin, to_uin
            FROM message_index
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", messageId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadIndex(reader) : null;
    }

    private void UpsertIndex(SqliteConnection connection, SqliteTransaction transaction, MessageRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_index (id, type, sequence, client_sequence, message_id, time, from_uin, to_uin)
            VALUES ($id, $type, $sequence, $clientSequence, $messageId, $time, $fromUin, $toUin)
            ON CONFLICT(id) DO UPDATE SET
                type = excluded.type,
                sequence = excluded.sequence,
                client_sequence = excluded.client_sequence,
                message_id = excluded.message_id,
                time = excluded.time,
                from_uin = excluded.from_uin,
                to_uin = excluded.to_uin
            """;
        AddIndexParameters(command, record);
        command.ExecuteNonQuery();
    }

    private void UpsertContent(SqliteConnection connection, SqliteTransaction transaction, MessageRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_content (id, codec, body, saved_at)
            VALUES ($id, $codec, $body, $savedAt)
            ON CONFLICT(id) DO UPDATE SET
                codec = excluded.codec,
                body = excluded.body,
                saved_at = excluded.saved_at
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$codec", ContentCodecBrotliMessagePack);
        command.Parameters.Add("$body", SqliteType.Blob).Value = CompressRecord(record);
        command.Parameters.AddWithValue("$savedAt", ToUnixTime(record.Time));
        command.ExecuteNonQuery();
    }

    private void ScheduleCleanup()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_cleanupLock)
        {
            if (now - _lastCleanup < CleanupInterval) return;
            _lastCleanup = now;
        }

        if (Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) != 0) return;
        _ = Task.Run(CleanupSqlite);
    }

    private void CleanupSqlite()
    {
        try
        {
            if (!_sqliteAvailable) return;
            lock (_sqliteLock)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                CleanupContentByAge(connection, transaction);
                CleanupByRetention(connection, transaction);
                transaction.Commit();
                ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SQLite message database cleanup failed");
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupRunning, 0);
        }
    }

    private void CleanupContentByAge(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (_persistFullContent) return;

        long cutoff = DateTimeOffset.UtcNow.Subtract(_fullContentRetention).ToUnixTimeSeconds();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM message_content
            WHERE id IN (
                SELECT id FROM message_content
                WHERE saved_at < $cutoff
                ORDER BY saved_at
                LIMIT $limit
            )
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$limit", CleanupBatchSize);
        command.ExecuteNonQuery();
    }

    private void CleanupByRetention(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (_retention.TimeLimit is { } retention)
        {
            long cutoff = DateTimeOffset.UtcNow.Subtract(retention).ToUnixTimeSeconds();
            DeleteOldIndexes(connection, transaction, "time < $cutoff", command => command.Parameters.AddWithValue("$cutoff", cutoff));
            return;
        }

        if (_retention.SizeLimitBytes is not { } limitBytes) return;
        long currentSize = GetDatabaseLogicalSizeBytes(connection);
        if (currentSize <= limitBytes) return;

        if (!_persistFullContent) DeleteOldContent(connection, transaction);
        currentSize = GetDatabaseLogicalSizeBytes(connection);
        if (currentSize > limitBytes)
        {
            DeleteOldIndexes(connection, transaction, "1 = 1", _ => { });
        }
    }

    private void DeleteOldContent(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM message_content
            WHERE id IN (
                SELECT c.id FROM message_content c
                JOIN message_index i ON i.id = c.id
                ORDER BY i.time
                LIMIT $limit
            )
            """;
        command.Parameters.AddWithValue("$limit", CleanupBatchSize);
        command.ExecuteNonQuery();
    }

    private void DeleteOldIndexes(SqliteConnection connection, SqliteTransaction transaction, string predicate, Action<SqliteCommand> addParameters)
    {
        using var deleteContent = connection.CreateCommand();
        deleteContent.Transaction = transaction;
        deleteContent.CommandText = $"""
            DELETE FROM message_content
            WHERE id IN (
                SELECT id FROM message_index
                WHERE {predicate}
                ORDER BY time
                LIMIT $limit
            )
            """;
        deleteContent.Parameters.AddWithValue("$limit", CleanupBatchSize);
        addParameters(deleteContent);
        deleteContent.ExecuteNonQuery();

        using var deleteIndex = connection.CreateCommand();
        deleteIndex.Transaction = transaction;
        deleteIndex.CommandText = $"""
            DELETE FROM message_index
            WHERE id IN (
                SELECT id FROM message_index
                WHERE {predicate}
                ORDER BY time
                LIMIT $limit
            )
            """;
        deleteIndex.Parameters.AddWithValue("$limit", CleanupBatchSize);
        addParameters(deleteIndex);
        deleteIndex.ExecuteNonQuery();
    }

    private long GetDatabaseSizeBytes()
    {
        long total = File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0;
        string wal = _databasePath + "-wal";
        string shm = _databasePath + "-shm";
        if (File.Exists(wal)) total += new FileInfo(wal).Length;
        if (File.Exists(shm)) total += new FileInfo(shm).Length;
        return total;
    }

    private static long GetDatabaseLogicalSizeBytes(SqliteConnection connection)
    {
        long pageSize = ExecuteScalarLong(connection, "PRAGMA page_size");
        long pageCount = ExecuteScalarLong(connection, "PRAGMA page_count");
        long freeListCount = ExecuteScalarLong(connection, "PRAGMA freelist_count");
        long usedPages = Math.Max(0, pageCount - freeListCount);
        return usedPages * pageSize;
    }

    private void SaveMemory(MessageRecord record)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_memoryLock)
        {
            CleanupMemory(now);
            _memoryRecords[record.Id] = new MemoryRecord(Clone(record), now);
        }
    }

    private MessageRecord? GetMemoryRecord(int messageId)
    {
        lock (_memoryLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CleanupMemory(now);
            return IsMemoryRecordFresh(messageId, now) ? _memoryRecords.GetValueOrDefault(messageId)?.Record : null;
        }
    }

    private MessageRecord? TryFindMemory(Func<MessageRecord, bool> predicate, bool orderBySequence = false)
    {
        lock (_memoryLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CleanupMemory(now);
            DateTimeOffset cutoff = now - _memoryRetention;
            IEnumerable<MessageRecord> records = _memoryRecords.Values
                .Where(entry => entry.StoredAtUtc >= cutoff)
                .Select(entry => entry.Record)
                .Where(predicate);
            if (orderBySequence) records = records.OrderByDescending(record => record.Sequence);
            return records.FirstOrDefault();
        }
    }

    private void CleanupMemory(DateTimeOffset now)
    {
        if (now - _lastMemoryCleanup < MemoryCleanupInterval) return;
        _lastMemoryCleanup = now;

        DateTimeOffset cutoff = now - _memoryRetention;
        foreach (int key in _memoryRecords
            .Where(pair => pair.Value.StoredAtUtc < cutoff)
            .Select(pair => pair.Key)
            .Take(MemoryCleanupBatchSize)
            .ToArray())
        {
            _memoryRecords.Remove(key);
        }
    }

    private bool IsMemoryRecordFresh(int messageId, DateTimeOffset now)
    {
        if (!_memoryRecords.TryGetValue(messageId, out MemoryRecord? entry)) return false;
        if (entry.StoredAtUtc >= now - _memoryRetention) return true;

        _memoryRecords.Remove(messageId);
        return false;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ExecuteScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AddIndexParameters(SqliteCommand command, MessageRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$type", record.TypeInt);
        command.Parameters.AddWithValue("$sequence", record.SequenceLong);
        command.Parameters.AddWithValue("$clientSequence", record.ClientSequenceLong);
        command.Parameters.AddWithValue("$messageId", record.MessageIdLong);
        command.Parameters.AddWithValue("$time", ToUnixTime(record.Time));
        command.Parameters.AddWithValue("$fromUin", record.FromUinLong);
        command.Parameters.AddWithValue("$toUin", record.ToUinLong);
    }

    private static MessageIndex ReadIndex(SqliteDataReader reader)
    {
        return new MessageIndex(
            reader.GetInt32(0),
            (MessageType)reader.GetInt32(1),
            (ulong)reader.GetInt64(2),
            (ulong)reader.GetInt64(3),
            (ulong)reader.GetInt64(4),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)),
            (ulong)reader.GetInt64(6),
            (ulong)reader.GetInt64(7));
    }

    private static MessageChain ToAnchorChain(MessageIndex index) => (MessageChain)ToRecord(index);

    private static MessageRecord ToRecord(MessageIndex index)
    {
        return new MessageRecord
        {
            Id = index.Id,
            Type = index.Type,
            Sequence = index.Sequence,
            ClientSequence = index.ClientSequence,
            MessageId = index.MessageId,
            Time = index.Time,
            FromUin = index.FromUin,
            ToUin = index.ToUin,
            Entities = Array.Empty<byte>()
        };
    }

    private static byte[] CompressRecord(MessageRecord record)
    {
        byte[] raw = MessagePackSerializer.Serialize(record, MessageRecord.OPTIONS);
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    private static MessageChain DecompressRecord(byte[] body)
    {
        using var input = new MemoryStream(body);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        return MessagePackSerializer.Deserialize<MessageRecord>(brotli, MessageRecord.OPTIONS);
    }

    private static MessageRecord Clone(MessageRecord record)
    {
        return new MessageRecord
        {
            Id = record.Id,
            Type = record.Type,
            Sequence = record.Sequence,
            ClientSequence = record.ClientSequence,
            MessageId = record.MessageId,
            Time = record.Time,
            FromUin = record.FromUin,
            ToUin = record.ToUin,
            Entities = record.Entities.ToArray(),
            Style = record.Style == null ? null : new MessageStyleRecord
            {
                BubbleId = record.Style.BubbleId,
                PendantId = record.Style.PendantId,
                FontId = record.Style.FontId,
                FontEffectId = record.Style.FontEffectId,
                BubbleDiyTextId = record.Style.BubbleDiyTextId,
                IsCsFontEffectEnabled = record.Style.IsCsFontEffectEnabled
            }
        };
    }

    private static string ResolveDatabasePath(IConfiguration configuration)
    {
        string? explicitPath = configuration["Database:Path"];
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        string? configuredPath = configuration["ConfigPath:Database"];
        string prefix = ResolveDatabaseDirectory(configuredPath, configuration);
        return Path.GetFullPath(Path.Combine(prefix, "messages.sqlite3"));
    }

    private static string ResolveDatabaseDirectory(string? configuredPath, IConfiguration configuration)
    {
        string defaultDirectory = $"./lagrange-{configuration["Account:Uin"]}-db";
        if (string.IsNullOrWhiteSpace(configuredPath)) return defaultDirectory;

        string fullPath = Path.GetFullPath(configuredPath);
        if (Directory.Exists(fullPath) || EndsWithDirectorySeparator(configuredPath)) return fullPath;
        return IsLegacyDatabaseFilePath(fullPath) || File.Exists(fullPath) ? defaultDirectory : fullPath;
    }

    private static string? ResolveLegacyDatabasePath(IConfiguration configuration)
    {
        string? configuredPath = configuration["ConfigPath:Database"];
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;

        string fullPath = Path.GetFullPath(configuredPath);
        return IsLegacyDatabaseFilePath(fullPath) || File.Exists(fullPath) ? fullPath : null;
    }

    private static bool EndsWithDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);
    }

    private static bool IsLegacyDatabaseFilePath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".db", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".litedb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".realm", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPositiveInt(IConfiguration configuration, string key, int fallback)
    {
        string? rawValue = configuration[key];
        if (string.IsNullOrWhiteSpace(rawValue)) return fallback;
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return fallback;
        return value > 0 ? value : fallback;
    }

    private static long ToUnixTime(DateTimeOffset time) => time.ToUnixTimeSeconds();

    private static RetentionPolicy ParseRetention(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return RetentionPolicy.Unlimited;

        value = value.Trim();
        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("never", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("forever", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return RetentionPolicy.Unlimited;
        }

        if (TryParseSize(value, out long bytes)) return new RetentionPolicy(null, bytes);
        if (TimeSpan.TryParse(value, out TimeSpan parsed) && parsed > TimeSpan.Zero) return new RetentionPolicy(parsed, null);
        return TryParseDuration(value, out TimeSpan duration) ? new RetentionPolicy(duration, null) : RetentionPolicy.Unlimited;
    }

    private static bool TryParseSize(string value, out long bytes)
    {
        bytes = 0;
        Match match = SizeRegex.Match(value);
        if (!match.Success) return false;
        if (!double.TryParse(match.Groups["amount"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || amount <= 0)
        {
            return false;
        }

        string unit = match.Groups["unit"].Value;
        double multiplier = unit switch
        {
            "B" => 1,
            "K" or "KB" => 1024,
            "M" or "MB" => 1024 * 1024,
            "G" or "GB" => 1024 * 1024 * 1024,
            _ => 0
        };
        bytes = (long)Math.Ceiling(amount * multiplier);
        return bytes > 0;
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        string compact = value.Replace(" ", string.Empty);
        TimeSpan total = TimeSpan.Zero;
        int consumed = 0;
        foreach (Match match in DurationPartRegex.Matches(compact))
        {
            if (match.Index != consumed)
            {
                duration = TimeSpan.Zero;
                return false;
            }

            consumed += match.Length;
            if (!double.TryParse(match.Groups["amount"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || amount <= 0)
            {
                duration = TimeSpan.Zero;
                return false;
            }

            if (ToTimeSpan(amount, match.Groups["unit"].Value) is not { } part)
            {
                duration = TimeSpan.Zero;
                return false;
            }

            total += part;
        }

        duration = total;
        return consumed == compact.Length && total > TimeSpan.Zero;
    }

    private static TimeSpan? ToTimeSpan(double amount, string unit)
    {
        unit = unit.ToLowerInvariant();
        return unit switch
        {
            "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(amount),
            "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(amount),
            "h" or "hr" or "hour" or "hours" => TimeSpan.FromHours(amount),
            "d" or "day" or "days" => TimeSpan.FromDays(amount),
            "w" or "week" or "weeks" => TimeSpan.FromDays(amount * 7),
            "m" or "mon" or "month" or "months" => TimeSpan.FromDays(amount * 30),
            "y" or "year" or "years" => TimeSpan.FromDays(amount * 365),
            _ => null
        };
    }

    private sealed record MemoryRecord(MessageRecord Record, DateTimeOffset StoredAtUtc);

    private sealed record MessageIndex(int Id, MessageType Type, ulong Sequence, ulong ClientSequence, ulong MessageId, DateTimeOffset Time, ulong FromUin, ulong ToUin);

    private sealed record RetentionPolicy(TimeSpan? TimeLimit, long? SizeLimitBytes)
    {
        public static RetentionPolicy Unlimited { get; } = new(null, null);
    }

    public sealed record EmojiLikeTarget(MessageType Type, ulong Sequence, ulong FromUin, ulong ToUin);
}

public sealed class MessageContentLookup
{
    private MessageContentLookup(MessageContentStatus status, MessageChain? chain)
    {
        Status = status;
        Chain = chain;
    }

    public MessageContentStatus Status { get; }

    public MessageChain? Chain { get; }

    public static MessageContentLookup Found(MessageChain chain) => new(MessageContentStatus.Found, chain);

    public static MessageContentLookup ContentExpired(MessageChain anchor) => new(MessageContentStatus.ContentExpired, anchor);

    public static MessageContentLookup NotFound() => new(MessageContentStatus.NotFound, null);
}

public enum MessageContentStatus
{
    Found,
    ContentExpired,
    NotFound
}
