using System.Globalization;
using System.Text.RegularExpressions;
using Lagrange.Core.Message;
using Lagrange.OneBot.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static Lagrange.Core.Message.MessageChain;
#if !ONEBOT_DISABLE_REALM
using Realms;
#endif

namespace Lagrange.OneBot.Utility;

public sealed class MessageRecordStore
{
    private static readonly Regex RetentionPartRegex = new(@"(?<amount>\d+(?:\.\d+)?)(?<unit>[a-zA-Z]+)", RegexOptions.Compiled);
    private static readonly TimeSpan MemoryRetention = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MemoryCleanupInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private const int MemoryCleanupBatchSize = 1000;
    private const int CleanupBatchSize = 500;

    private readonly Dictionary<int, MemoryRecord> _memoryRecords = new();
    private readonly object _memoryLock = new();
    private readonly object _cleanupLock = new();
    private readonly ILogger<MessageRecordStore> _logger;
#if !ONEBOT_DISABLE_REALM
    private readonly RealmHelper? _realm;
#endif
    private readonly TimeSpan? _retention;
    private DateTimeOffset _lastMemoryCleanup = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private int _cleanupRunning;

    public MessageRecordStore(IConfiguration configuration, ILogger<MessageRecordStore> logger,
#if !ONEBOT_DISABLE_REALM
        RealmHelper? realm = null
#else
        RealmHelper? realm = null
#endif
    )
    {
        _logger = logger;
#if !ONEBOT_DISABLE_REALM
        _realm = FeatureFlags.DisableRealm ? null : realm;
#endif
        _retention = ParseRetention(configuration["Database:Retention"]);
    }

#if !ONEBOT_DISABLE_REALM
    private bool UseRealm => _realm is not null;
#else
    private bool UseRealm => false;
#endif

    public void Save(MessageChain chain) => Save((MessageRecord)chain);

    public void Save(MessageRecord record)
    {
        if (UseRealm)
        {
#if !ONEBOT_DISABLE_REALM
            _realm!.Do(realm =>
            {
                realm.Write(() => realm.Add(record, update: true));
            });
            ScheduleCleanupExpired();
#endif
            return;
        }

        SaveMemory(record);
    }

    public MessageChain? GetMessageChain(int messageId)
    {
        if (UseRealm)
        {
#if !ONEBOT_DISABLE_REALM
            return _realm!.Do(realm =>
            {
                MessageRecord? record = realm.All<MessageRecord>().FirstOrDefault(record => record.Id == messageId);
                return record == null ? null : (MessageChain)record;
            });
#else
            return null;
#endif
        }

        return GetMemoryRecord(messageId) is { } record ? (MessageChain)record : null;
    }

    public ulong? GetSequence(int messageId)
    {
        if (UseRealm)
        {
#if !ONEBOT_DISABLE_REALM
            return _realm!.Do(realm => realm.All<MessageRecord>()
                .FirstOrDefault(record => record.Id == messageId)?
                .Sequence);
#else
            return null;
#endif
        }

        return GetMemoryRecord(messageId)?.Sequence;
    }

    public EmojiLikeTarget? GetEmojiLikeTarget(int messageId)
    {
        if (UseRealm)
        {
#if !ONEBOT_DISABLE_REALM
            return _realm!.Do(realm =>
            {
                MessageRecord? record = realm.All<MessageRecord>().FirstOrDefault(record => record.Id == messageId);
                return record == null
                    ? null
                    : new EmojiLikeTarget(record.Type, record.Sequence, record.FromUin, record.ToUin);
            });
#else
            return null;
#endif
        }

        return GetMemoryRecord(messageId) is { } record
            ? new EmojiLikeTarget(record.Type, record.Sequence, record.FromUin, record.ToUin)
            : null;
    }

    public int? GetGroupMessageIdBySequence(uint groupUin, uint sequence)
    {
        if (UseRealm)
        {
#if !ONEBOT_DISABLE_REALM
            return _realm!.Do(realm => realm.All<MessageRecord>()
                .FirstOrDefault(record => record.TypeInt == (int)MessageType.Group
                    && record.ToUinLong == groupUin
                    && record.SequenceLong == sequence)?
                .Id);
#else
            return null;
#endif
        }

        lock (_memoryLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CleanupMemory(now);
            DateTimeOffset cutoff = now - MemoryRetention;
            return _memoryRecords.Values
                .Where(entry => entry.StoredAtUtc >= cutoff)
                .Select(entry => entry.Record)
                .FirstOrDefault(record => record.Type == MessageType.Group
                    && record.ToUin == groupUin
                    && record.Sequence == sequence)?
                .Id;
        }
    }

    public ulong? FindFriendSequence(uint friendUin, uint clientSequence, ulong messageId)
    {
        if (UseRealm)
        {
#if !ONEBOT_DISABLE_REALM
            return _realm!.Do(realm => realm.All<MessageRecord>()
                .FirstOrDefault(record => record.TypeInt == (int)MessageType.Friend
                    && (record.FromUinLong == friendUin || record.ToUinLong == friendUin)
                    && record.ClientSequenceLong == clientSequence
                    && record.MessageIdLong == (long)messageId)?
                .Sequence);
#else
            return null;
#endif
        }

        lock (_memoryLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CleanupMemory(now);
            DateTimeOffset cutoff = now - MemoryRetention;
            return _memoryRecords.Values
                .Where(entry => entry.StoredAtUtc >= cutoff)
                .Select(entry => entry.Record)
                .FirstOrDefault(record => record.Type == MessageType.Friend
                    && (record.FromUin == friendUin || record.ToUin == friendUin)
                    && record.ClientSequence == clientSequence
                    && record.MessageId == messageId)?
                .Sequence;
        }
    }

    public uint? GetGroupSequenceForHistory(uint groupId, int messageId)
    {
        if (UseRealm)
        {
#if !ONEBOT_DISABLE_REALM
            return _realm!.Do(realm => messageId == 0
                ? realm.All<MessageRecord>()
                    .Where(record => record.ToUinLong == groupId)
                    .OrderByDescending(record => record.SequenceLong)
                    .FirstOrDefault()?
                    .Sequence
                : realm.All<MessageRecord>()
                    .FirstOrDefault(record => record.Id == messageId)?
                    .Sequence) is { } sequence ? (uint)sequence : null;
#else
            return null;
#endif
        }

        lock (_memoryLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CleanupMemory(now);
            DateTimeOffset cutoff = now - MemoryRetention;
            MessageRecord? record = messageId == 0
                ? _memoryRecords.Values
                    .Where(entry => entry.StoredAtUtc >= cutoff)
                    .Select(entry => entry.Record)
                    .Where(record => record.ToUin == groupId)
                    .OrderByDescending(record => record.Sequence)
                    .FirstOrDefault()
                : _memoryRecords.GetValueOrDefault(messageId)?.Record;
            return record == null ? null : (uint)record.Sequence;
        }
    }

    public MessageChain? GetFriendHistoryChain(uint userId, int messageId)
    {
        if (UseRealm)
        {
#if !ONEBOT_DISABLE_REALM
            return _realm!.Do(realm =>
            {
                MessageRecord? record = messageId == 0
                    ? realm.All<MessageRecord>()
                        .Where(record => record.FromUinLong == userId || record.ToUinLong == userId)
                        .OrderByDescending(record => record.SequenceLong)
                        .FirstOrDefault()
                    : realm.All<MessageRecord>().FirstOrDefault(record => record.Id == messageId);
                return record == null ? null : (MessageChain)record;
            });
#else
            return null;
#endif
        }

        lock (_memoryLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CleanupMemory(now);
            DateTimeOffset cutoff = now - MemoryRetention;
            MessageRecord? record = messageId == 0
                ? _memoryRecords.Values
                    .Where(entry => entry.StoredAtUtc >= cutoff)
                    .Select(entry => entry.Record)
                    .Where(record => record.FromUin == userId || record.ToUin == userId)
                    .OrderByDescending(record => record.Sequence)
                    .FirstOrDefault()
                : _memoryRecords.GetValueOrDefault(messageId)?.Record;
            return record == null ? null : (MessageChain)record;
        }
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

    private void CleanupMemory(DateTimeOffset now)
    {
        if (now - _lastMemoryCleanup < MemoryCleanupInterval) return;
        _lastMemoryCleanup = now;

        DateTimeOffset cutoff = now - MemoryRetention;
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
        if (entry.StoredAtUtc >= now - MemoryRetention) return true;

        _memoryRecords.Remove(messageId);
        return false;
    }

    private void ScheduleCleanupExpired()
    {
        if (_retention is not { } retention) return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_cleanupLock)
        {
            if (now - _lastCleanup < CleanupInterval) return;
            _lastCleanup = now;
        }

        if (Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) != 0) return;

        _ = Task.Run(() => CleanupExpired(retention, now));
    }

    private void CleanupExpired(TimeSpan retention, DateTimeOffset now)
    {

#if !ONEBOT_DISABLE_REALM
        try
        {
            DateTimeOffset cutoff = now - retention;
            _realm!.Do(realm =>
            {
                var expired = realm.All<MessageRecord>()
                    .Where(record => record.Time < cutoff)
                    .OrderBy(record => record.Time)
                    .Take(CleanupBatchSize)
                    .ToArray();
                if (expired.Length == 0) return;

                realm.Write(() =>
                {
                    foreach (MessageRecord record in expired)
                    {
                        realm.Remove(record);
                    }
                });
                _logger.LogInformation(
                    "Pruned {Count} expired message records older than {Cutoff:u}; batch_limit={BatchLimit}",
                    expired.Length,
                    cutoff,
                    CleanupBatchSize);
            });
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to prune expired message records");
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupRunning, 0);
        }
#endif
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

    private static TimeSpan? ParseRetention(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        value = value.Trim();
        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("never", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("forever", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, out TimeSpan parsed) && parsed > TimeSpan.Zero) return parsed;

        string compact = value.Replace(" ", string.Empty);
        TimeSpan total = TimeSpan.Zero;
        int consumed = 0;
        foreach (Match match in RetentionPartRegex.Matches(compact))
        {
            if (match.Index != consumed) return null;
            consumed += match.Length;

            if (!double.TryParse(match.Groups["amount"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || amount <= 0)
            {
                return null;
            }

            if (ToTimeSpan(amount, match.Groups["unit"].Value) is not { } part) return null;
            total += part;
        }

        return consumed == compact.Length && total > TimeSpan.Zero ? total : null;
    }

    private static TimeSpan? ToTimeSpan(double amount, string unit)
    {
        return unit.ToLowerInvariant() switch
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

    public sealed record EmojiLikeTarget(MessageType Type, ulong Sequence, ulong FromUin, ulong ToUin);
}
