using System.Text.Json;
using CodexUsageMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexUsageMonitor.Core.Services;

public sealed record SourceState(
    long ProcessedLength,
    long LastWriteTicks,
    int ParserVersion,
    string? ProcessedFingerprint,
    string? FileIdentity,
    RolloutParserCheckpoint Checkpoint);

public sealed record StoredQuotaSnapshot(
    AccountSnapshot? Account,
    QuotaSnapshot Quota,
    DateTimeOffset? AccountCapturedAt,
    DateTimeOffset? GeneralCapturedAt,
    DateTimeOffset? SparkCapturedAt,
    DateTimeOffset? ResetCreditsCapturedAt)
{
    public DateTimeOffset? CapturedAt
    {
        get
        {
            var values = new[]
            {
                AccountCapturedAt, GeneralCapturedAt, SparkCapturedAt, ResetCreditsCapturedAt
            }.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            return values.Length == 0 ? null : values.Max();
        }
    }
}

public sealed record StoredOfficialUsageSnapshot(
    OfficialUsageSnapshot Usage,
    DateTimeOffset? SummaryCapturedAt,
    DateTimeOffset? DailyUsageCapturedAt)
{
    public DateTimeOffset? CapturedAt
        => SummaryCapturedAt.HasValue && DailyUsageCapturedAt.HasValue
            ? (SummaryCapturedAt.Value > DailyUsageCapturedAt.Value ? SummaryCapturedAt : DailyUsageCapturedAt)
            : SummaryCapturedAt ?? DailyUsageCapturedAt;
}

public sealed class UsageHistoryRepository(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public string DatabasePath { get; } = databasePath;
    internal Action? QueryEventsObserver { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS usage_sources (
                source_id TEXT PRIMARY KEY,
                file_length INTEGER NOT NULL,
                last_write_ticks INTEGER NOT NULL,
                parser_version INTEGER NOT NULL,
                processed_fingerprint TEXT,
                file_identity TEXT,
                parser_checkpoint_json TEXT
            );
            CREATE TABLE IF NOT EXISTS usage_events (
                source_id TEXT NOT NULL,
                event_key TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL,
                model TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_tokens INTEGER NOT NULL,
                total_tokens INTEGER NOT NULL,
                PRIMARY KEY (source_id, event_key)
            );
            CREATE INDEX IF NOT EXISTS idx_usage_events_timestamp ON usage_events(timestamp_utc);
            CREATE TABLE IF NOT EXISTS usage_events_staging (
                rebuild_id TEXT NOT NULL,
                source_id TEXT NOT NULL,
                event_key TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL,
                model TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_tokens INTEGER NOT NULL,
                total_tokens INTEGER NOT NULL,
                PRIMARY KEY (rebuild_id, source_id, event_key)
            );
            CREATE TABLE IF NOT EXISTS quota_snapshots (
                captured_at TEXT PRIMARY KEY,
                plan_type TEXT,
                account_captured_at TEXT,
                general_captured_at TEXT,
                spark_captured_at TEXT,
                reset_credits_captured_at TEXT,
                payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS reset_credits (
                credit_id TEXT PRIMARY KEY,
                granted_at TEXT NOT NULL,
                expires_at TEXT,
                title TEXT,
                description TEXT,
                status TEXT NOT NULL,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS reset_observations (
                captured_at TEXT PRIMARY KEY,
                available_count INTEGER,
                details_complete INTEGER NOT NULL,
                credit_ids_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS reset_events (
                event_key TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                count INTEGER NOT NULL,
                is_inferred INTEGER NOT NULL,
                credit_id TEXT
            );
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS official_usage_summary (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                lifetime_tokens INTEGER,
                peak_daily_tokens INTEGER,
                current_streak_days INTEGER,
                longest_streak_days INTEGER,
                captured_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS official_daily_usage (
                usage_date TEXT PRIMARY KEY,
                tokens INTEGER NOT NULL,
                captured_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "usage_sources", "parser_checkpoint_json", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "usage_sources", "processed_fingerprint", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "usage_sources", "file_identity", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "quota_snapshots", "account_captured_at", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "quota_snapshots", "general_captured_at", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "quota_snapshots", "spark_captured_at", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "quota_snapshots", "reset_credits_captured_at", "TEXT", cancellationToken);

        var cleanup = connection.CreateCommand();
        cleanup.CommandText = "DELETE FROM usage_events_staging";
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SourceState?> GetSourceStateAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT file_length, last_write_ticks, parser_version, processed_fingerprint, file_identity, parser_checkpoint_json FROM usage_sources WHERE source_id = $id";
        command.Parameters.AddWithValue("$id", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var parserVersion = reader.GetInt32(2);
        RolloutParserCheckpoint checkpoint;
        try
        {
            checkpoint = reader.IsDBNull(5)
                ? RolloutParserCheckpoint.Empty(sourceId)
                : JsonSerializer.Deserialize<RolloutParserCheckpoint>(reader.GetString(5))
                  ?? RolloutParserCheckpoint.Empty(sourceId);
        }
        catch (JsonException)
        {
            checkpoint = RolloutParserCheckpoint.Empty(sourceId);
            parserVersion = 0;
        }

        return new SourceState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            parserVersion,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            checkpoint);
    }

    public async Task BeginSourceRebuildAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM usage_events_staging WHERE source_id = $source";
        command.Parameters.AddWithValue("$source", sourceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendStagedSourceEventsAsync(
        string rebuildId,
        string sourceId,
        IReadOnlyList<TokenUsageEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in events)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO usage_events_staging (
                    rebuild_id, source_id, event_key, timestamp_utc, model, input_tokens,
                    cached_input_tokens, output_tokens, reasoning_tokens, total_tokens)
                VALUES ($rebuild, $source, $event, $timestamp, $model, $input, $cached, $output, $reasoning, $total)
                """;
            insert.Parameters.AddWithValue("$rebuild", rebuildId);
            AddEventParameters(insert, sourceId, item);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CommitSourceRebuildAsync(
        string rebuildId,
        string sourceId,
        long processedLength,
        long lastWriteTicks,
        string fileIdentity,
        string processedFingerprint,
        RolloutParserCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
        => await CommitStagedSourceAsync(
            rebuildId,
            sourceId,
            processedLength,
            lastWriteTicks,
            fileIdentity,
            processedFingerprint,
            checkpoint,
            replaceExisting: true,
            cancellationToken);

    public async Task CommitSourceAppendAsync(
        string rebuildId,
        string sourceId,
        long processedLength,
        long lastWriteTicks,
        string fileIdentity,
        string processedFingerprint,
        RolloutParserCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
        => await CommitStagedSourceAsync(
            rebuildId,
            sourceId,
            processedLength,
            lastWriteTicks,
            fileIdentity,
            processedFingerprint,
            checkpoint,
            replaceExisting: false,
            cancellationToken);

    private async Task CommitStagedSourceAsync(
        string rebuildId,
        string sourceId,
        long processedLength,
        long lastWriteTicks,
        string fileIdentity,
        string processedFingerprint,
        RolloutParserCheckpoint checkpoint,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var replace = connection.CreateCommand();
        replace.Transaction = (SqliteTransaction)transaction;
        replace.CommandText = (replaceExisting ? "DELETE FROM usage_events WHERE source_id = $source;\n" : string.Empty) + """
            INSERT OR IGNORE INTO usage_events (
                source_id, event_key, timestamp_utc, model, input_tokens, cached_input_tokens,
                output_tokens, reasoning_tokens, total_tokens)
            SELECT source_id, event_key, timestamp_utc, model, input_tokens, cached_input_tokens,
                   output_tokens, reasoning_tokens, total_tokens
            FROM usage_events_staging
            WHERE rebuild_id = $rebuild AND source_id = $source;
            """;
        replace.Parameters.AddWithValue("$rebuild", rebuildId);
        replace.Parameters.AddWithValue("$source", sourceId);
        await replace.ExecuteNonQueryAsync(cancellationToken);

        var upsert = connection.CreateCommand();
        upsert.Transaction = (SqliteTransaction)transaction;
        upsert.CommandText = """
            INSERT INTO usage_sources (
                source_id, file_length, last_write_ticks, parser_version, processed_fingerprint, file_identity, parser_checkpoint_json)
            VALUES ($id, $length, $ticks, $version, $fingerprint, $identity, $checkpoint)
            ON CONFLICT(source_id) DO UPDATE SET
                file_length = excluded.file_length,
                last_write_ticks = excluded.last_write_ticks,
                parser_version = excluded.parser_version,
                processed_fingerprint = excluded.processed_fingerprint,
                file_identity = excluded.file_identity,
                parser_checkpoint_json = excluded.parser_checkpoint_json
            """;
        upsert.Parameters.AddWithValue("$id", sourceId);
        upsert.Parameters.AddWithValue("$length", processedLength);
        upsert.Parameters.AddWithValue("$ticks", lastWriteTicks);
        upsert.Parameters.AddWithValue("$version", RolloutParser.ParserVersion);
        upsert.Parameters.AddWithValue("$fingerprint", processedFingerprint);
        upsert.Parameters.AddWithValue("$identity", fileIdentity);
        upsert.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint));
        await upsert.ExecuteNonQueryAsync(cancellationToken);

        var cleanup = connection.CreateCommand();
        cleanup.Transaction = (SqliteTransaction)transaction;
        cleanup.CommandText = "DELETE FROM usage_events_staging WHERE source_id = $source";
        cleanup.Parameters.AddWithValue("$source", sourceId);
        await cleanup.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TokenUsageEvent>> QueryEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        QueryEventsObserver?.Invoke();
        var result = new List<TokenUsageEvent>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(source_id), event_key, MIN(timestamp_utc), MIN(model),
                   MAX(input_tokens), MAX(cached_input_tokens), MAX(output_tokens),
                   MAX(reasoning_tokens), MAX(total_tokens)
            FROM usage_events
            WHERE timestamp_utc >= $from AND timestamp_utc < $to
            GROUP BY event_key
            ORDER BY MIN(timestamp_utc)
            """;
        command.Parameters.AddWithValue("$from", from.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", to.ToUniversalTime().ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TokenUsageEvent(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8)));
        }

        return result;
    }

    public async Task SaveQuotaSnapshotAsync(
        AccountSnapshot? account,
        QuotaSnapshot quota,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default)
        => await SaveQuotaSnapshotPartitionsAsync(
            account,
            quota,
            capturedAt,
            account is null ? null : capturedAt,
            quota.General is null ? null : capturedAt,
            quota.Spark is null ? null : capturedAt,
            quota.ResetCreditCount.HasValue || quota.ResetCredits.Count > 0 ? capturedAt : null,
            quota,
            cancellationToken);

    public async Task SaveQuotaSnapshotPartitionsAsync(
        AccountSnapshot? account,
        QuotaSnapshot quota,
        DateTimeOffset capturedAt,
        DateTimeOffset? accountCapturedAt,
        DateTimeOffset? generalCapturedAt,
        DateTimeOffset? sparkCapturedAt,
        DateTimeOffset? resetCreditsCapturedAt,
        QuotaSnapshot? resetObservation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var quotaCommand = connection.CreateCommand();
        quotaCommand.Transaction = (SqliteTransaction)transaction;
        quotaCommand.CommandText = """
            INSERT OR REPLACE INTO quota_snapshots (
                captured_at, plan_type, account_captured_at, general_captured_at,
                spark_captured_at, reset_credits_captured_at, payload_json)
            VALUES ($at, $plan, $accountAt, $generalAt, $sparkAt, $resetAt, $json)
            """;
        quotaCommand.Parameters.AddWithValue("$at", capturedAt.ToUniversalTime().ToString("O"));
        quotaCommand.Parameters.AddWithValue("$plan", (object?)account?.PlanType ?? DBNull.Value);
        quotaCommand.Parameters.AddWithValue("$accountAt", ToDatabaseValue(accountCapturedAt));
        quotaCommand.Parameters.AddWithValue("$generalAt", ToDatabaseValue(generalCapturedAt));
        quotaCommand.Parameters.AddWithValue("$sparkAt", ToDatabaseValue(sparkCapturedAt));
        quotaCommand.Parameters.AddWithValue("$resetAt", ToDatabaseValue(resetCreditsCapturedAt));
        quotaCommand.Parameters.AddWithValue("$json", JsonSerializer.Serialize(quota));
        await quotaCommand.ExecuteNonQueryAsync(cancellationToken);

        if (resetObservation is not null)
        {
            await SaveResetHistoryAsync(connection, (SqliteTransaction)transaction, resetObservation, capturedAt, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<StoredQuotaSnapshot?> GetLatestQuotaSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at, plan_type, account_captured_at, general_captured_at,
                   spark_captured_at, reset_credits_captured_at, payload_json
            FROM quota_snapshots ORDER BY captured_at DESC LIMIT 1
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        try
        {
            var capturedAt = DateTimeOffset.Parse(reader.GetString(0));
            var planType = reader.IsDBNull(1) ? null : reader.GetString(1);
            var quota = JsonSerializer.Deserialize<QuotaSnapshot>(reader.GetString(6));
            return quota is null
                ? null
                : new StoredQuotaSnapshot(
                    string.IsNullOrWhiteSpace(planType) ? null : new AccountSnapshot("chatgpt", planType),
                    quota,
                    ReadCapturedAt(reader, 2) ?? (string.IsNullOrWhiteSpace(planType) ? null : capturedAt),
                    ReadCapturedAt(reader, 3) ?? (quota.General is null ? null : capturedAt),
                    ReadCapturedAt(reader, 4) ?? (quota.Spark is null ? null : capturedAt),
                    ReadCapturedAt(reader, 5) ?? (quota.ResetCreditCount.HasValue || quota.ResetCredits.Count > 0 ? capturedAt : null));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }

    public async Task SaveOfficialUsageAsync(
        OfficialUsageSnapshot usage,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default)
        => await SaveOfficialUsagePartitionsAsync(usage, capturedAt, saveSummary: true, saveDailyUsage: true, cancellationToken);

    public async Task SaveOfficialUsagePartitionsAsync(
        OfficialUsageSnapshot usage,
        DateTimeOffset capturedAt,
        bool saveSummary,
        bool saveDailyUsage,
        CancellationToken cancellationToken = default)
    {
        if (!saveSummary && !saveDailyUsage)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (saveSummary)
        {
            var summary = connection.CreateCommand();
            summary.Transaction = (SqliteTransaction)transaction;
            summary.CommandText = """
                INSERT INTO official_usage_summary (
                    singleton_id, lifetime_tokens, peak_daily_tokens, current_streak_days, longest_streak_days, captured_at)
                VALUES (1, $lifetime, $peak, $current, $longest, $captured)
                ON CONFLICT(singleton_id) DO UPDATE SET
                    lifetime_tokens = excluded.lifetime_tokens,
                    peak_daily_tokens = excluded.peak_daily_tokens,
                    current_streak_days = excluded.current_streak_days,
                    longest_streak_days = excluded.longest_streak_days,
                    captured_at = excluded.captured_at
                """;
            summary.Parameters.AddWithValue("$lifetime", (object?)usage.LifetimeTokens ?? DBNull.Value);
            summary.Parameters.AddWithValue("$peak", (object?)usage.PeakDailyTokens ?? DBNull.Value);
            summary.Parameters.AddWithValue("$current", (object?)usage.CurrentStreakDays ?? DBNull.Value);
            summary.Parameters.AddWithValue("$longest", (object?)usage.LongestStreakDays ?? DBNull.Value);
            summary.Parameters.AddWithValue("$captured", capturedAt.ToUniversalTime().ToString("O"));
            await summary.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var point in saveDailyUsage ? usage.DailyUsage : [])
        {
            if (point.Tokens < 0)
            {
                continue;
            }

            var daily = connection.CreateCommand();
            daily.Transaction = (SqliteTransaction)transaction;
            daily.CommandText = """
                INSERT INTO official_daily_usage (usage_date, tokens, captured_at)
                VALUES ($date, $tokens, $captured)
                ON CONFLICT(usage_date) DO UPDATE SET
                    tokens = excluded.tokens,
                    captured_at = excluded.captured_at
                """;
            daily.Parameters.AddWithValue("$date", point.Date.ToString("yyyy-MM-dd"));
            daily.Parameters.AddWithValue("$tokens", point.Tokens);
            daily.Parameters.AddWithValue("$captured", capturedAt.ToUniversalTime().ToString("O"));
            await daily.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<StoredOfficialUsageSnapshot?> GetLatestOfficialUsageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var summaryCommand = connection.CreateCommand();
        summaryCommand.CommandText = """
            SELECT lifetime_tokens, peak_daily_tokens, current_streak_days, longest_streak_days, captured_at
            FROM official_usage_summary WHERE singleton_id = 1
            """;
        DateTimeOffset? summaryCapturedAt = null;
        long? lifetime = null;
        long? peak = null;
        int? current = null;
        int? longest = null;
        await using (var reader = await summaryCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                summaryCapturedAt = DateTimeOffset.Parse(reader.GetString(4));
                lifetime = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                peak = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                current = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                longest = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            }
        }

        var points = new List<DailyUsagePoint>();
        DateTimeOffset? dailyCapturedAt = null;
        var daily = connection.CreateCommand();
        daily.CommandText = "SELECT usage_date, tokens, captured_at FROM official_daily_usage ORDER BY usage_date";
        await using var dailyReader = await daily.ExecuteReaderAsync(cancellationToken);
        while (await dailyReader.ReadAsync(cancellationToken))
        {
            if (DateOnly.TryParseExact(dailyReader.GetString(0), "yyyy-MM-dd", out var date))
            {
                points.Add(new DailyUsagePoint(date, dailyReader.GetInt64(1)));
                var pointCapturedAt = DateTimeOffset.Parse(dailyReader.GetString(2));
                dailyCapturedAt = !dailyCapturedAt.HasValue || pointCapturedAt > dailyCapturedAt
                    ? pointCapturedAt
                    : dailyCapturedAt;
            }
        }

        if (!summaryCapturedAt.HasValue && !dailyCapturedAt.HasValue)
        {
            return null;
        }

        return new StoredOfficialUsageSnapshot(
            new OfficialUsageSnapshot(lifetime, peak, current, longest, points),
            summaryCapturedAt,
            dailyCapturedAt);
    }

    public async Task<IReadOnlyList<ResetHistoryItem>> GetResetHistoryAsync(
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ResetHistoryItem>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, occurred_at, count, is_inferred, credit_id
            FROM reset_events
            ORDER BY occurred_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ResetHistoryItem(
                Enum.Parse<ResetHistoryKind>(reader.GetString(0), ignoreCase: true),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetInt32(3) == 1,
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    public async Task<bool> GetBoolSettingAsync(string key, bool fallback, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    public async Task SetBoolSettingAsync(string key, bool value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO app_settings (key, value) VALUES ($key, $value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SaveResetHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuotaSnapshot quota,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var previous = await ReadPreviousObservationAsync(connection, transaction, cancellationToken);
        var currentIds = quota.ResetCredits.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var previousIds = previous.Ids.ToHashSet(StringComparer.Ordinal);

        foreach (var credit in quota.ResetCredits)
        {
            var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO reset_credits (
                    credit_id, granted_at, expires_at, title, description, status, first_seen, last_seen)
                VALUES ($id, $granted, $expires, $title, $description, $status, $seen, $seen)
                ON CONFLICT(credit_id) DO UPDATE SET
                    expires_at = excluded.expires_at,
                    title = excluded.title,
                    description = excluded.description,
                    status = excluded.status,
                    last_seen = excluded.last_seen
                """;
            upsert.Parameters.AddWithValue("$id", credit.Id);
            upsert.Parameters.AddWithValue("$granted", credit.GrantedAt.ToUniversalTime().ToString("O"));
            upsert.Parameters.AddWithValue("$expires", (object?)credit.ExpiresAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$title", (object?)credit.Title ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$description", (object?)credit.Description ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$status", credit.Status);
            upsert.Parameters.AddWithValue("$seen", capturedAt.ToUniversalTime().ToString("O"));
            await upsert.ExecuteNonQueryAsync(cancellationToken);

            if (!previousIds.Contains(credit.Id))
            {
                await InsertResetEventAsync(
                    connection,
                    transaction,
                    $"granted:{credit.Id}",
                    ResetHistoryKind.Granted,
                    credit.GrantedAt,
                    1,
                    false,
                    credit.Id,
                    cancellationToken);
            }
        }

        if (quota.ResetCreditDetailsComplete && previous.DetailsComplete)
        {
            foreach (var disappearedId in previousIds.Except(currentIds))
            {
                var expiry = await ReadCreditExpiryAsync(connection, transaction, disappearedId, cancellationToken);
                var kind = expiry.HasValue && expiry.Value <= capturedAt
                    ? ResetHistoryKind.Expired
                    : ResetHistoryKind.Used;
                await InsertResetEventAsync(
                    connection,
                    transaction,
                    $"{kind}:{disappearedId}:{capturedAt:O}",
                    kind,
                    capturedAt,
                    1,
                    true,
                    disappearedId,
                    cancellationToken);
            }
        }
        else if (currentIds.Count == 0
                 && previousIds.Count == 0
                 && previous.Count.HasValue
                 && quota.ResetCreditCount.HasValue
                 && quota.ResetCreditCount < previous.Count)
        {
            var difference = previous.Count.Value - quota.ResetCreditCount.Value;
            await InsertResetEventAsync(
                connection,
                transaction,
                $"used:count:{capturedAt:O}",
                ResetHistoryKind.Used,
                capturedAt,
                difference,
                true,
                null,
                cancellationToken);
        }

        var observation = connection.CreateCommand();
        observation.Transaction = transaction;
        observation.CommandText = """
            INSERT INTO reset_observations (captured_at, available_count, details_complete, credit_ids_json)
            VALUES ($at, $count, $complete, $ids)
            """;
        observation.Parameters.AddWithValue("$at", capturedAt.ToUniversalTime().ToString("O"));
        observation.Parameters.AddWithValue("$count", (object?)quota.ResetCreditCount ?? DBNull.Value);
        observation.Parameters.AddWithValue("$complete", quota.ResetCreditDetailsComplete ? 1 : 0);
        observation.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(currentIds.OrderBy(id => id)));
        await observation.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(int? Count, bool DetailsComplete, IReadOnlyList<string> Ids)> ReadPreviousObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT available_count, details_complete, credit_ids_json FROM reset_observations ORDER BY captured_at DESC LIMIT 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, false, Array.Empty<string>());
        }

        var ids = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? Array.Empty<string>();
        return (reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.GetInt32(1) == 1, ids);
    }

    private static async Task<DateTimeOffset?> ReadCreditExpiryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string creditId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT expires_at FROM reset_credits WHERE credit_id = $id";
        command.Parameters.AddWithValue("$id", creditId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text ? DateTimeOffset.Parse(text) : null;
    }

    private static async Task InsertResetEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventKey,
        ResetHistoryKind kind,
        DateTimeOffset occurredAt,
        int count,
        bool inferred,
        string? creditId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO reset_events (event_key, kind, occurred_at, count, is_inferred, credit_id)
            VALUES ($key, $kind, $at, $count, $inferred, $credit)
            """;
        command.Parameters.AddWithValue("$key", eventKey);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$at", occurredAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$inferred", inferred ? 1 : 0);
        command.Parameters.AddWithValue("$credit", (object?)creditId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddEventParameters(SqliteCommand insert, string sourceId, TokenUsageEvent item)
    {
        insert.Parameters.AddWithValue("$source", sourceId);
        insert.Parameters.AddWithValue("$event", item.EventKey);
        insert.Parameters.AddWithValue("$timestamp", item.TimestampUtc.ToUniversalTime().ToString("O"));
        insert.Parameters.AddWithValue("$model", item.Model);
        insert.Parameters.AddWithValue("$input", item.InputTokens);
        insert.Parameters.AddWithValue("$cached", item.CachedInputTokens);
        insert.Parameters.AddWithValue("$output", item.OutputTokens);
        insert.Parameters.AddWithValue("$reasoning", item.ReasoningTokens);
        insert.Parameters.AddWithValue("$total", item.TotalTokens);
    }

    private static object ToDatabaseValue(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToUniversalTime().ToString("O") : DBNull.Value;

    private static DateTimeOffset? ReadCapturedAt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
