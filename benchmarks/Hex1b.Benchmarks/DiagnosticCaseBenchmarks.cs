using System.Reflection;
using System.Text;
using BenchmarkDotNet.Attributes;
using Hex1b.Diagnostics;

namespace Hex1b.Benchmarks;

/// <summary>
/// The terminal's output pump per chunk with diagnostics unarmed and with a diagnostic case armed: the in-process
/// measurement of the instrumentation-overhead report (mean time and allocated bytes per chunk). A measurement only:
/// no threshold is asserted here; the unarmed invariant's pass/fail evidence is the test suite's fences
/// (<c>Unarmed_PumpAllocatesAsBase</c>, <c>Semantics_Unchanged</c>, <c>Overload_PumpNeverWaits</c>).
/// </summary>
/// <remarks>
/// Each iteration builds a fresh headless terminal and drives its output pump directly on the calling thread through
/// 1,000 prepared chunks that complete synchronously, as the allocation fence does. In the armed states the case
/// records to a scratch directory (<c>HEX1B_BENCH_CASE_DIR</c>, or the temp path) with its writer running, so
/// <see cref="MemoryDiagnoserAttribute"/> counts what the writer allocates too: armed recording owns retained data.
/// </remarks>
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[InvocationCount(1)]
public class DiagnosticCaseBenchmarks
{
    private const int ChunksPerInvoke = 1000;

    private static readonly MethodInfo PumpMethod =
        typeof(Hex1bTerminal).GetMethod("PumpWorkloadOutputAsync", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("The output pump was not found.");

    private byte[][] _chunks = [];
    private string _root = "";
    private PreparedWorkload _workload = null!;
    private Hex1bTerminal _terminal = null!;
    private Func<CancellationToken, Task> _pump = null!;
    private bool _armed;

    [GlobalSetup]
    public void PrepareCorpus()
    {
        // Styled, titled, wide and wrapping lines beside plain ones, so a chunk is not a plain-ASCII best case.
        string[] shapes =
        [
            "\u001b[1;31mred bold\u001b[0m plain \u001b[4:3munder\u001b[24m \u001b[38;2;1;2;3mrgb\u001b[m line {0}\r\n",
            "\u001b]0;TITLE {0}\u0007tab\tstop and a plain run of text for line {0}\r\n",
            "漢字 é 👩‍💻 wide and combining, then a long line that wraps and wraps and wraps past the margin {0}\r\n",
            "plain line {0} with nothing but ASCII in it, sixty-odd bytes long\r\n",
        ];
        _chunks = [.. Enumerable.Range(0, ChunksPerInvoke).Select(i => Encoding.UTF8.GetBytes(string.Format(shapes[i % shapes.Length], i)))];
        var parent = Environment.GetEnvironmentVariable("HEX1B_BENCH_CASE_DIR") is { Length: > 0 } configured ? configured : Path.GetTempPath();
        _root = Path.Combine(parent, "hex1b-bench-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(_root);
        else
            Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [GlobalCleanup]
    public void RemoveCases()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [IterationSetup(Target = nameof(Unarmed))]
    public void SetupUnarmed() => Build(authorizations: null);

    [IterationSetup(Target = nameof(ArmedWithoutReapplicationData))]
    public void SetupArmedWithoutReapplicationData() => Build(authorizations: []);

    [IterationSetup(Target = nameof(ArmedWithEveryAuthorization))]
    public void SetupArmedWithEveryAuthorization() => Build(Enum.GetValues<DiagnosticAuthorization>());

    [IterationCleanup]
    public void StopAndDispose()
    {
        if (_armed)
            new TerminalDiagnostics(_terminal).StopCaseAsync().GetAwaiter().GetResult();
        _terminal.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>No case: the pump as it runs when diagnostics are not armed.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = ChunksPerInvoke)]
    public void Unarmed() => RunPump();

    /// <summary>A case armed without <c>reapplication-data</c>: events are counted, no chunk is copied.</summary>
    [Benchmark(OperationsPerInvoke = ChunksPerInvoke)]
    public void ArmedWithoutReapplicationData() => RunPump();

    /// <summary>A case armed with every authorization: each chunk is copied, queued and written.</summary>
    [Benchmark(OperationsPerInvoke = ChunksPerInvoke)]
    public void ArmedWithEveryAuthorization() => RunPump();

    private void Build(IReadOnlyList<DiagnosticAuthorization>? authorizations)
    {
        _workload = new PreparedWorkload();
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(80, 24),
            WorkloadAdapter = _workload,
            Width = 80,
            Height = 24,
            ScrollbackCapacity = 100,
            DeferStart = true,
        };
        _armed = authorizations is not null;
        if (authorizations is null)
        {
            _terminal = new Hex1bTerminal(options);
        }
        else
        {
            var (terminal, started) = Hex1bTerminal.CreateWithDiagnosticCase(options,
                new DiagnosticCaseStartRequest { Directory = _root, Authorizations = authorizations });
            _terminal = terminal;
            if (started.Outcome != DiagnosticOutcome.Captured)
                throw new InvalidOperationException($"The case did not start: {started.Problem?.Code} {started.Problem?.Message}");
        }
        _pump = PumpMethod.CreateDelegate<Func<CancellationToken, Task>>(_terminal);
    }

    private void RunPump()
    {
        using var cts = new CancellationTokenSource();
        _workload.Load(_chunks, cts);
        _pump(cts.Token).GetAwaiter().GetResult();
        if (_terminal.OutputBytesRead == 0)
            throw new InvalidOperationException("The pump read nothing.");
    }

    // Returns each prepared chunk synchronously; once they are exhausted it cancels the pump.
    private sealed class PreparedWorkload : IHex1bTerminalWorkloadAdapter
    {
        private byte[][] _chunks = [];
        private int _next;
        private CancellationTokenSource? _cts;

        public void Load(byte[][] chunks, CancellationTokenSource cts) => (_chunks, _next, _cts) = (chunks, 0, cts);

        public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
        {
            if (_next < _chunks.Length)
                return new ValueTask<ReadOnlyMemory<byte>>(_chunks[_next++]);
            _cts!.Cancel();
            return ValueTask.FromCanceled<ReadOnlyMemory<byte>>(ct);
        }

        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;

        public event Action? Disconnected { add { } remove { } }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
