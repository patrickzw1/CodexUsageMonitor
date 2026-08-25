using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.Core.Services;

public sealed record RolloutParserCheckpoint(
    string CurrentModel,
    string SessionId,
    string LastCumulativeSignature,
    long? PreviousInput,
    long? PreviousCachedInput,
    long? PreviousOutput,
    long? PreviousReasoning,
    long? PreviousTotal)
{
    public static RolloutParserCheckpoint Empty(string sourceId)
        => new("unknown", sourceId, string.Empty, null, null, null, null, null);
}

public sealed record RolloutParseResult(
    long ProcessedOffset,
    RolloutParserCheckpoint Checkpoint,
    int OversizedLineCount,
    int InvalidLineCount);

public sealed class RolloutParser
{
    public const int ParserVersion = 2;
    public const int MaxLineBytes = 1024 * 1024;
    public const int BatchSize = 256;
    public const long MaxTokenValue = 10_000_000_000_000;
    public const int MaxSessionIdChars = 256;

    private readonly ArrayPool<byte> _bufferPool;

    public RolloutParser()
        : this(ArrayPool<byte>.Shared)
    {
    }

    internal RolloutParser(ArrayPool<byte> bufferPool)
    {
        _bufferPool = bufferPool;
    }

    public IReadOnlyList<TokenUsageEvent> Parse(Stream stream, string sourceId)
    {
        var events = new List<TokenUsageEvent>();
        ParseAsync(
                stream,
                sourceId,
                stream.Position,
                RolloutParserCheckpoint.Empty(sourceId),
                (batch, _) =>
                {
                    events.AddRange(batch);
                    return Task.CompletedTask;
                })
            .GetAwaiter()
            .GetResult();
        return events;
    }

    public async Task<RolloutParseResult> ParseAsync(
        Stream stream,
        string sourceId,
        long baseOffset,
        RolloutParserCheckpoint checkpoint,
        Func<IReadOnlyList<TokenUsageEvent>, CancellationToken, Task> writeBatchAsync,
        CancellationToken cancellationToken = default)
    {
        var currentModel = checkpoint.CurrentModel;
        var sessionId = checkpoint.SessionId;
        var lastCumulativeSignature = checkpoint.LastCumulativeSignature;
        UsageNumbers? previousTotal = checkpoint.PreviousInput.HasValue
            ? new UsageNumbers(
                checkpoint.PreviousInput.Value,
                checkpoint.PreviousCachedInput ?? 0,
                checkpoint.PreviousOutput ?? 0,
                checkpoint.PreviousReasoning ?? 0,
                checkpoint.PreviousTotal ?? 0)
            : null;
        var batch = new List<TokenUsageEvent>(BatchSize);
        var processedOffset = baseOffset;
        var oversizedLines = 0;
        var invalidLines = 0;
        using var reader = new BoundedUtf8LineReader(stream, MaxLineBytes, baseOffset, _bufferPool);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.IsOversized)
            {
                oversizedLines++;
                if (!line.IsTerminated)
                {
                    break;
                }

                processedOffset = line.EndOffset;
                continue;
            }

            if (IsJsonWhitespace(line.Bytes.Span))
            {
                processedOffset = line.EndOffset;
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line.Bytes);
            }
            catch (JsonException)
            {
                invalidLines++;
                if (!line.IsTerminated)
                {
                    break;
                }

                processedOffset = line.EndOffset;
                continue;
            }

            using (document)
            {
                try
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                    {
                        if (ReadString(payload, "model") is { Length: > 0 } model)
                        {
                            currentModel = UsageCalculator.NormalizeModel(model);
                        }

                        if (ReadString(payload, "id") is { Length: > 0 } foundSessionId
                            && ReadString(root, "type") == "session_meta")
                        {
                            sessionId = foundSessionId.Length <= MaxSessionIdChars
                                ? foundSessionId
                                : foundSessionId[..MaxSessionIdChars];
                        }
                    }

                    if (!IsTokenCount(root, out var tokenPayload)
                        || !tokenPayload.TryGetProperty("info", out var info)
                        || info.ValueKind != JsonValueKind.Object)
                    {
                        processedOffset = line.EndOffset;
                        continue;
                    }

                    var timestamp = ReadTimestamp(root);
                    if (timestamp is null)
                    {
                        invalidLines++;
                        processedOffset = line.EndOffset;
                        continue;
                    }

                    var total = ReadUsage(info, "total_token_usage");
                    var last = ReadUsage(info, "last_token_usage");
                    var cumulativeSignature = total?.Signature ?? $"offset:{line.StartOffset}";
                    if (total is not null && cumulativeSignature == lastCumulativeSignature)
                    {
                        processedOffset = line.EndOffset;
                        continue;
                    }

                    UsageNumbers? usage = last;
                    if (usage is null && total is not null)
                    {
                        usage = previousTotal is null ? total : total.DifferenceFrom(previousTotal);
                    }

                    if (total is not null)
                    {
                        previousTotal = total;
                        lastCumulativeSignature = cumulativeSignature;
                    }

                    if (usage is null || usage.IsEmpty || !TryResolveTotal(usage, out var totalTokens))
                    {
                        processedOffset = line.EndOffset;
                        continue;
                    }

                    var eventKey = Hash($"{sessionId}|{cumulativeSignature}|{timestamp.Value:O}|{currentModel}");
                    batch.Add(new TokenUsageEvent(
                        sourceId,
                        eventKey,
                        timestamp.Value.ToUniversalTime(),
                        currentModel,
                        usage.Input,
                        Math.Clamp(usage.CachedInput, 0, usage.Input),
                        usage.Output,
                        Math.Clamp(usage.Reasoning, 0, usage.Output),
                        totalTokens));

                    if (batch.Count >= BatchSize)
                    {
                        await writeBatchAsync(batch.ToArray(), cancellationToken);
                        batch.Clear();
                    }
                }
                catch (Exception exception) when (exception is JsonException or FormatException or ArgumentOutOfRangeException or OverflowException)
                {
                    invalidLines++;
                }
            }

            processedOffset = line.EndOffset;
        }

        if (batch.Count > 0)
        {
            await writeBatchAsync(batch, cancellationToken);
        }

        var finalCheckpoint = new RolloutParserCheckpoint(
            currentModel,
            sessionId,
            lastCumulativeSignature,
            previousTotal?.Input,
            previousTotal?.CachedInput,
            previousTotal?.Output,
            previousTotal?.Reasoning,
            previousTotal?.Total);
        return new RolloutParseResult(processedOffset, finalCheckpoint, oversizedLines, invalidLines);
    }

    public static string CreateSourceId(string path)
        => Hash(Path.GetFullPath(path).ToUpperInvariant());

    private static bool IsTokenCount(JsonElement root, out JsonElement payload)
    {
        payload = default;
        return ReadString(root, "type") == "event_msg"
               && root.TryGetProperty("payload", out payload)
               && payload.ValueKind == JsonValueKind.Object
               && ReadString(payload, "type") == "token_count";
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        var text = ReadString(root, "timestamp") ?? ReadString(root, "created_at");
        if (DateTimeOffset.TryParse(text, out var parsed))
        {
            return parsed;
        }

        if (!root.TryGetProperty("timestamp", out var value) || !value.TryGetInt64(out var unixMilliseconds))
        {
            return null;
        }

        var minimum = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
        var maximum = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        return unixMilliseconds >= minimum && unixMilliseconds <= maximum
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            : null;
    }

    private static UsageNumbers? ReadUsage(JsonElement info, string name)
    {
        if (!info.TryGetProperty(name, out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryReadFirstLong(usage, out var input, "input_tokens", "prompt_tokens", "input")
               && TryReadFirstLong(usage, out var cached, "cached_input_tokens", "cache_read_input_tokens", "cached_tokens")
               && TryReadFirstLong(usage, out var output, "output_tokens", "completion_tokens", "output")
               && TryReadFirstLong(usage, out var reasoning, "reasoning_output_tokens", "reasoning_tokens")
               && TryReadFirstLong(usage, out var total, "total_tokens")
            ? new UsageNumbers(input, cached, output, reasoning, total)
            : null;
    }

    private static bool TryReadFirstLong(JsonElement element, out long result, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (!value.TryGetInt64(out var number) || number < 0 || number > MaxTokenValue)
            {
                result = 0;
                return false;
            }

            result = number;
            return true;
        }

        result = 0;
        return true;
    }

    private static bool TryResolveTotal(UsageNumbers usage, out long total)
    {
        if (usage.Total > 0)
        {
            total = usage.Total;
            return true;
        }

        try
        {
            total = checked(usage.Input + usage.Output);
            return total <= MaxTokenValue;
        }
        catch (OverflowException)
        {
            total = 0;
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsJsonWhitespace(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record UsageNumbers(long Input, long CachedInput, long Output, long Reasoning, long Total)
    {
        public bool IsEmpty => Input == 0 && Output == 0 && Total == 0;
        public string Signature => $"{Input}:{CachedInput}:{Output}:{Reasoning}:{Total}";

        public UsageNumbers DifferenceFrom(UsageNumbers previous)
        {
            if (Input < previous.Input || Output < previous.Output || Total < previous.Total)
            {
                return this;
            }

            return new UsageNumbers(
                Input - previous.Input,
                Math.Max(CachedInput - previous.CachedInput, 0),
                Output - previous.Output,
                Math.Max(Reasoning - previous.Reasoning, 0),
                Math.Max(Total - previous.Total, 0));
        }
    }

    private sealed record Utf8Line(ReadOnlyMemory<byte> Bytes, long StartOffset, long EndOffset, bool IsTerminated, bool IsOversized);

    private sealed class BoundedUtf8LineReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly int _maxLineBytes;
        private readonly ArrayPool<byte> _bufferPool;
        private readonly byte[] _readBuffer;
        private readonly byte[] _lineBuffer;
        private int _readPosition;
        private int _readLength;
        private int _lineLength;
        private bool _oversized;
        private long _offset;
        private long _lineStart;

        public BoundedUtf8LineReader(Stream stream, int maxLineBytes, long baseOffset, ArrayPool<byte> bufferPool)
        {
            _stream = stream;
            _maxLineBytes = maxLineBytes;
            _bufferPool = bufferPool;
            _readBuffer = bufferPool.Rent(64 * 1024);
            try
            {
                _lineBuffer = bufferPool.Rent(maxLineBytes);
            }
            catch
            {
                bufferPool.Return(_readBuffer, clearArray: true);
                throw;
            }

            _offset = baseOffset;
            _lineStart = baseOffset;
        }

        public async ValueTask<Utf8Line?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_readPosition >= _readLength)
                {
                    _readLength = await _stream.ReadAsync(_readBuffer, cancellationToken);
                    _readPosition = 0;
                    if (_readLength == 0)
                    {
                        if (_lineLength == 0 && !_oversized)
                        {
                            return null;
                        }

                        return CompleteLine(isTerminated: false);
                    }
                }

                var value = _readBuffer[_readPosition++];
                _offset++;
                if (value == (byte)'\n')
                {
                    return CompleteLine(isTerminated: true);
                }

                if (_oversized)
                {
                    continue;
                }

                if (_lineLength >= _maxLineBytes)
                {
                    _oversized = true;
                    continue;
                }

                _lineBuffer[_lineLength++] = value;
            }
        }

        private Utf8Line CompleteLine(bool isTerminated)
        {
            var length = _lineLength;
            if (length > 0 && _lineBuffer[length - 1] == (byte)'\r')
            {
                length--;
            }

            var line = new Utf8Line(
                _oversized ? ReadOnlyMemory<byte>.Empty : _lineBuffer.AsMemory(0, length),
                _lineStart,
                _offset,
                isTerminated,
                _oversized);
            _lineLength = 0;
            _oversized = false;
            _lineStart = _offset;
            return line;
        }

        public void Dispose()
        {
            _bufferPool.Return(_lineBuffer, clearArray: true);
            _bufferPool.Return(_readBuffer, clearArray: true);
        }
    }
}
