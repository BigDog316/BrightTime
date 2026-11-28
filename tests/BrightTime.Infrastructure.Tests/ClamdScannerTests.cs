using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BrightTime.Abstractions.Storage;
using BrightTime.Infrastructure.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BrightTime.Infrastructure.Tests;

/// <summary>
/// The clamd client (D123), against a stand-in that speaks clamd's INSTREAM protocol: what's sent
/// on the wire, each answer read as a verdict, and every way it can't be done failing closed.
/// </summary>
public sealed class ClamdScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Listens once, reads one INSTREAM, answers it with <paramref name="answer"/>, and hands back what came.</summary>
    private static (int Port, Task<(string Command, byte[] Content)> Received) StandIn(Func<byte[], string> answer, bool close = false)
    {
#pragma warning disable CA2000 // Owned by the task below, which disposes of it.
        TcpListener listener = new(IPAddress.Loopback, 0);
#pragma warning restore CA2000
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<(string, byte[])> received = Task.Run(async () =>
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(Ct);
                NetworkStream stream = client.GetStream();
                byte[] command = new byte[10];
                await stream.ReadExactlyAsync(command, Ct);
                using MemoryStream content = new();
                byte[] length = new byte[4];
                while (true)
                {
                    await stream.ReadExactlyAsync(length, Ct);
                    int size = (int)BinaryPrimitives.ReadUInt32BigEndian(length);
                    if (size == 0)
                    {
                        break;
                    }

                    size.ShouldBeLessThanOrEqualTo(64 * 1024, "sent in chunks");
                    byte[] chunk = new byte[size];
                    await stream.ReadExactlyAsync(chunk, Ct);
                    content.Write(chunk);
                }

                if (!close)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(answer(content.ToArray()) + "\0"), Ct);
                }

                return (Encoding.ASCII.GetString(command), content.ToArray());
            }
            finally
            {
                listener.Dispose();
            }
        }, Ct);
        return (port, received);
    }

    private static ClamdScanner Scanner(int port, int timeoutSeconds = 5) =>
        new(Options.Create(new ClamdOptions { Host = "127.0.0.1", Port = port, TimeoutSeconds = timeoutSeconds }), NullLogger<ClamdScanner>.Instance);

    [Fact]
    public async Task A_file_goes_as_INSTREAM_in_chunks_and_OK_is_clean()
    {
        byte[] file = [.. Enumerable.Range(0, 200_000).Select(i => (byte)i)];
        (int port, Task<(string Command, byte[] Content)> received) = StandIn(_ => "stream: OK");

        ScanResult result = await Scanner(port).ScanAsync(file, Ct);

        result.ShouldBe(new ScanResult(ScanVerdict.Clean, ClamdScanner.Engine));
        (string command, byte[] content) = await received;
        command.ShouldBe("zINSTREAM\0");
        content.ShouldBe(file, "the whole file, as it came");
    }

    [Fact]
    public async Task FOUND_is_infected_with_the_signatures_name()
    {
        (int port, Task<(string, byte[])> received) = StandIn(_ => "stream: Win.Test.EICAR_HDB-1 FOUND");

        ScanResult result = await Scanner(port).ScanAsync(TestPatternScanner.TestFile, Ct);

        result.ShouldBe(new ScanResult(ScanVerdict.Infected, ClamdScanner.Engine, "Win.Test.EICAR_HDB-1"));
        await received;
    }

    [Theory]
    [InlineData("INSTREAM size limit exceeded. ERROR")]
    [InlineData("stream: Can't allocate memory ERROR")]
    [InlineData("")]
    [InlineData("stream: FOUND")]
    [InlineData("something else entirely")]
    public void Any_other_answer_is_a_failure_never_clean(string answer) =>
        ClamdScanner.Interpret(answer).Verdict.ShouldBe(ScanVerdict.Failed);

    [Fact]
    public async Task No_scanner_listening_no_answer_or_none_configured_all_fail_closed()
    {
        // Nothing listening.
        int nobody;
        using (TcpListener taken = new(IPAddress.Loopback, 0))
        {
            taken.Start();
            nobody = ((IPEndPoint)taken.LocalEndpoint).Port;
        }

        (await Scanner(nobody).ScanAsync(new byte[10], Ct)).ShouldBe(new ScanResult(ScanVerdict.Failed, ClamdScanner.Engine, "unreachable"));

        // It takes the file and hangs up without answering.
        (int port, Task<(string, byte[])> received) = StandIn(_ => "", close: true);
        (await Scanner(port).ScanAsync(new byte[10], Ct)).Verdict.ShouldBe(ScanVerdict.Failed);
        await received;

        // Not configured.
        ClamdScanner unset = new(Options.Create(new ClamdOptions()), NullLogger<ClamdScanner>.Instance);
        (await unset.ScanAsync(new byte[10], Ct)).ShouldBe(new ScanResult(ScanVerdict.Failed, ClamdScanner.Engine, "not configured"));
    }

    [Fact]
    public async Task A_scanner_that_never_answers_times_out_as_a_failure()
    {
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            Task<TcpClient> accepted = silent.AcceptTcpClientAsync(Ct).AsTask();
            ScanResult result = await Scanner(port, timeoutSeconds: 1).ScanAsync(new byte[10], Ct);
            result.ShouldBe(new ScanResult(ScanVerdict.Failed, ClamdScanner.Engine, "timed out"));
            (await accepted).Dispose();
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public async Task Developments_scanner_finds_the_test_file_and_nothing_else()
    {
        TestPatternScanner scanner = new();
        (await scanner.ScanAsync(new byte[] { 1, 2, 3 }, Ct)).Verdict.ShouldBe(ScanVerdict.Clean);
        byte[] carrying = [.. "a picture"u8.ToArray(), .. TestPatternScanner.TestFile.ToArray(), .. "after"u8.ToArray()];
        (await scanner.ScanAsync(carrying, Ct)).ShouldBe(new ScanResult(ScanVerdict.Infected, TestPatternScanner.Engine, TestPatternScanner.Signature));
        TestPatternScanner.TestFile.Length.ShouldBe(68, "the EICAR test file");
    }
}
