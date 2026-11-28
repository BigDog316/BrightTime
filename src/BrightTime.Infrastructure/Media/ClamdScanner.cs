using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using BrightTime.Abstractions.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrightTime.Infrastructure.Media;

/// <summary>Where clamd listens (D123): <c>Media:Clamd</c>.</summary>
public sealed class ClamdOptions
{
    public const string SectionName = "Media:Clamd";

    /// <summary>clamd's host. Required outside Development: uploads aren't kept unscanned.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 3310;

    /// <summary>How long a scan may take, connecting included, before the file is refused as unchecked.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// Scans with ClamAV's daemon over TCP (D123), by its <c>INSTREAM</c> command: the bytes go in
/// chunks, each after its length as four bytes, big-endian, and a zero length ends them. clamd
/// answers <c>stream: OK</c>, <c>stream: {signature} FOUND</c>, or an error. Nothing is written to
/// disk on our side, and no package is involved.
/// </summary>
/// <remarks>
/// It fails closed: a refused connection, a timeout, a dropped stream or an answer it doesn't know
/// is <see cref="ScanVerdict.Failed"/>, and the upload is refused as unchecked. clamd's own
/// <c>StreamMaxLength</c> (25 MB by default) is above every feature's limit.
/// </remarks>
public sealed partial class ClamdScanner(IOptions<ClamdOptions> options, ILogger<ClamdScanner> logger) : IVirusScanner
{
    /// <summary>The engine's name in <c>scan_detail</c>.</summary>
    public const string Engine = "clamd";

    private const int Chunk = 64 * 1024;

    private const int MaxReply = 1024;

    public async Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ClamdOptions o = options.Value;
        if (!o.IsConfigured)
        {
            return new ScanResult(ScanVerdict.Failed, Engine, "not configured");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(o.Host!, o.Port, timeout.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                byte[] length = new byte[4];
                for (int at = 0; at < content.Length; at += Chunk)
                {
                    ReadOnlyMemory<byte> part = content.Slice(at, Math.Min(Chunk, content.Length - at));
                    BinaryPrimitives.WriteUInt32BigEndian(length, (uint)part.Length);
                    await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                    await stream.WriteAsync(part, timeout.Token).ConfigureAwait(false);
                }

                BinaryPrimitives.WriteUInt32BigEndian(length, 0);
                await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

                ScanResult result = Interpret(await ReadReplyAsync(stream, timeout.Token).ConfigureAwait(false));
                if (result.Verdict == ScanVerdict.Failed)
                {
                    LogFailed(logger, result.Detail ?? "");
                }

                return result;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, "timed out");
            return new ScanResult(ScanVerdict.Failed, Engine, "timed out");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            LogFailed(logger, ex.GetType().Name);
            return new ScanResult(ScanVerdict.Failed, Engine, "unreachable");
        }
    }

    /// <summary>clamd's answer, read as a verdict. Anything that isn't OK or FOUND is a failure, never clean.</summary>
    public static ScanResult Interpret(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        string answer = reply.Trim('\0', ' ', '\n', '\r');
        if (answer.StartsWith("stream: ", StringComparison.Ordinal))
        {
            answer = answer["stream: ".Length..];
        }

        if (answer == "OK")
        {
            return new ScanResult(ScanVerdict.Clean, Engine);
        }

        if (answer.EndsWith(" FOUND", StringComparison.Ordinal) && answer.Length > " FOUND".Length)
        {
            return new ScanResult(ScanVerdict.Infected, Engine, answer[..^" FOUND".Length]);
        }

        return new ScanResult(ScanVerdict.Failed, Engine, answer.Length == 0 ? "no answer" : answer[..Math.Min(answer.Length, 200)]);
    }

    /// <summary>Up to the terminating zero byte (the <c>z</c> commands' form), or the end of the stream.</summary>
    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxReply];
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int end = Array.IndexOf(buffer, (byte)0, filled, read);
            filled += read;
            if (end >= 0)
            {
                filled = end;
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, filled);
    }

    [LoggerMessage(EventId = 6110, Level = LogLevel.Error, Message = "clamd couldn't scan an upload ({Problem}); it was refused as unchecked.")]
    private static partial void LogFailed(ILogger logger, string problem);
}
