using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CodexUsageMonitor.Core.Services;

public sealed class RolloutUsageScanner(
    RolloutParser parser,
    UsageHistoryRepository repository,
    string? codexHomeOverride = null) : IRolloutUsageScanner
{
    private const int MaxScanAttempts = 2;

    internal Func<string, CancellationToken, Task>? BeforeValidationAsync { get; set; }
    internal long TotalBytesRead { get; private set; }

    public async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        TotalBytesRead = 0;
        var changedSources = 0;
        foreach (var path in DiscoverFiles(codexHomeOverride))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo file;
            try
            {
                file = new FileInfo(path);
                if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            try
            {
                changedSources += await RefreshFileAsync(path, cancellationToken) ? 1 : 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (BeforeValidationAsync is not null)
                {
                    throw;
                }

                // 正在滚动写入或暂不可读的来源留到下一次刷新。
            }
        }

        return changedSources;
    }

    private async Task<bool> RefreshFileAsync(string path, CancellationToken cancellationToken)
    {
        var sourceId = RolloutParser.CreateSourceId(path);
        for (var attempt = 0; attempt < MaxScanAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileLength = stream.Length;
            var lastWriteTicks = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
            var fileIdentity = GetFileIdentity(stream);
            var state = await repository.GetSourceStateAsync(sourceId, cancellationToken);

            if (state is not null
                && state.ProcessedLength == fileLength
                && state.LastWriteTicks == lastWriteTicks
                && state.ParserVersion == RolloutParser.ParserVersion
                && string.Equals(state.FileIdentity, fileIdentity, StringComparison.Ordinal))
            {
                if (BeforeValidationAsync is not null)
                {
                    await BeforeValidationAsync(path, cancellationToken);
                }

                if (await CurrentPathMetadataMatchesAsync(
                        path,
                        fileLength,
                        lastWriteTicks,
                        fileIdentity,
                        cancellationToken))
                {
                    return false;
                }

                continue;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string? currentProcessedFingerprint = null;
            if (state is not null
                && state.ParserVersion == RolloutParser.ParserVersion
                && state.ProcessedLength >= 0
                && state.ProcessedLength <= fileLength
                && string.Equals(state.FileIdentity, fileIdentity, StringComparison.Ordinal))
            {
                await AppendPrefixToHashAsync(
                    stream,
                    state.ProcessedLength,
                    hash,
                    bytesRead => TotalBytesRead += bytesRead,
                    cancellationToken);
                currentProcessedFingerprint = ToLowerHex(hash.GetCurrentHash());
            }

            var canAppend = state is not null
                            && state.ParserVersion == RolloutParser.ParserVersion
                            && state.ProcessedLength >= 0
                            && state.ProcessedLength < fileLength
                            && string.Equals(state.FileIdentity, fileIdentity, StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(state.ProcessedFingerprint)
                            && string.Equals(state.ProcessedFingerprint, currentProcessedFingerprint, StringComparison.Ordinal);
            var startOffset = canAppend ? state!.ProcessedLength : 0;
            if (!canAppend)
            {
                hash.GetHashAndReset();
            }

            var checkpoint = canAppend
                ? state!.Checkpoint
                : RolloutParserCheckpoint.Empty(sourceId);
            var scanId = Guid.NewGuid().ToString("N");

            await repository.BeginSourceRebuildAsync(sourceId, cancellationToken);
            stream.Seek(startOffset, SeekOrigin.Begin);
            using var hashingStream = new HashingReadStream(
                stream,
                hash,
                bytesRead => TotalBytesRead += bytesRead);
            var result = await parser.ParseAsync(
                hashingStream,
                sourceId,
                startOffset,
                checkpoint,
                (events, token) => repository.AppendStagedSourceEventsAsync(scanId, sourceId, events, token),
                cancellationToken);
            var processedFingerprint = hashingStream.Position == result.ProcessedOffset
                ? ToLowerHex(hash.GetHashAndReset())
                : await ComputeTrackedFingerprintAsync(stream, result.ProcessedOffset, cancellationToken);
            var processedLength = stream.Length;
            var processedLastWriteTicks = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;

            if (BeforeValidationAsync is not null)
            {
                await BeforeValidationAsync(path, cancellationToken);
            }

            if (!await CurrentPathMatchesAsync(
                    path,
                    processedLength,
                    processedLastWriteTicks,
                    fileIdentity,
                    cancellationToken))
            {
                continue;
            }

            if (canAppend)
            {
                await repository.CommitSourceAppendAsync(
                    scanId,
                    sourceId,
                    result.ProcessedOffset,
                    processedLastWriteTicks,
                    fileIdentity,
                    processedFingerprint,
                    result.Checkpoint,
                    cancellationToken);
            }
            else
            {
                await repository.CommitSourceRebuildAsync(
                    scanId,
                    sourceId,
                    result.ProcessedOffset,
                    processedLastWriteTicks,
                    fileIdentity,
                    processedFingerprint,
                    result.Checkpoint,
                    cancellationToken);
            }

            return true;
        }

        return false;
    }

    internal static async Task<string> ComputeProcessedFingerprintAsync(
        string path,
        long processedLength,
        CancellationToken cancellationToken = default)
    {
        if (processedLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processedLength));
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeProcessedFingerprintAsync(stream, processedLength, cancellationToken);
    }

    private static async Task<string> ComputeProcessedFingerprintAsync(
        Stream stream,
        long processedLength,
        CancellationToken cancellationToken,
        Action<int>? onBytesRead = null)
    {
        var originalPosition = stream.Position;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await AppendPrefixToHashAsync(stream, processedLength, hash, onBytesRead, cancellationToken);
            return ToLowerHex(hash.GetHashAndReset());
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static async Task<bool> CurrentPathMatchesAsync(
        string path,
        long expectedLength,
        long expectedLastWriteTicks,
        string expectedFileIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            return stream.Length == expectedLength
                   && File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks == expectedLastWriteTicks
                   && string.Equals(GetFileIdentity(stream), expectedFileIdentity, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private async Task<string> ComputeTrackedFingerprintAsync(
        Stream stream,
        long processedLength,
        CancellationToken cancellationToken)
        => await ComputeProcessedFingerprintAsync(
            stream,
            processedLength,
            cancellationToken,
            bytesRead => TotalBytesRead += bytesRead);

    private static async Task AppendPrefixToHashAsync(
        Stream stream,
        long processedLength,
        IncrementalHash hash,
        Action<int>? onBytesRead,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var remaining = processedLength;
        stream.Seek(0, SeekOrigin.Begin);
        try
        {
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("rollout 文件在指纹验证期间被截断");
                }

                hash.AppendData(buffer, 0, read);
                onBytesRead?.Invoke(read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string ToLowerHex(byte[] value)
        => Convert.ToHexString(value).ToLowerInvariant();

    private static async Task<bool> CurrentPathMetadataMatchesAsync(
        string path,
        long expectedLength,
        long expectedLastWriteTicks,
        string expectedFileIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                options: FileOptions.Asynchronous);
            return stream.Length == expectedLength
                   && File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks == expectedLastWriteTicks
                   && string.Equals(GetFileIdentity(stream), expectedFileIdentity, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string GetFileIdentity(FileStream stream)
    {
        if (OperatingSystem.IsWindows()
            && GetFileInformationByHandle(stream.SafeFileHandle, out var information))
        {
            return FormattableString.Invariant(
                $"{information.VolumeSerialNumber:x8}:{information.FileIndexHigh:x8}:{information.FileIndexLow:x8}");
        }

        return File.GetCreationTimeUtc(stream.SafeFileHandle).Ticks.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class HashingReadStream(
        Stream inner,
        IncrementalHash hash,
        Action<int> onBytesRead) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                hash.AppendData(buffer.Span[..read]);
                onBytesRead(read);
            }

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                hash.AppendData(buffer, offset, read);
                onBytesRead(read);
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    public static IEnumerable<string> DiscoverFiles(string? codexHomeOverride = null)
    {
        var configuredHome = codexHomeOverride ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        var candidateHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configuredHome;

        string codexHome;
        try
        {
            codexHome = Path.GetFullPath(candidateHome);
        }
        catch
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directoryName in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(codexHome, directoryName);
            if (!IsWithinRoot(codexHome, directory) || !Directory.Exists(directory))
            {
                continue;
            }

            IEnumerator<string>? enumerator = null;
            try
            {
                enumerator = Directory.EnumerateFiles(directory, "*.jsonl", options).GetEnumerator();
                while (true)
                {
                    string file;
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }

                        file = enumerator.Current;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        break;
                    }

                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(file);
                        if (!IsWithinRoot(codexHome, fullPath)
                            || File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                    {
                        continue;
                    }

                    if (seen.Add(fullPath))
                    {
                        yield return fullPath;
                    }
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }
    }

    public static bool IsWithinRoot(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
               || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
