using System.Diagnostics;
using System.Globalization;
using System.Text;

// Deterministic process fixture for the Phase 2 execution tests.
//
// Test infrastructure only. It lives under tests/ so the architecture source scanners,
// which read src/**, do not apply to it: spawning child processes here is exactly the
// behaviour the ProcessRunner tests need to observe.

try
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.InputEncoding = new UTF8Encoding(false);
}
catch (IOException)
{
    // No console attached; the redirected pipes already use the encoding the parent set.
}

// Some git helper surfaces (gpg.program) are invoked as a bare executable path with
// git's own arguments appended, so there is no place to pass a mode. When this variable
// is present the fixture records the invocation whatever it was asked to do.
RecordEnvironmentMarker(args);

var mode = args.Length > 0 ? args[0] : "help";

return mode switch
{
    "exit" => Exit(args),
    "stdout" => WriteStdout(args),
    "echo-args" => EchoArgs(args),
    "marker" => Marker(args),
    "stderr" => WriteStderr(args),
    "both" => WriteBoth(args),
    "flood" => await FloodAsync(args),
    "silent" => await SilentAsync(args),
    "chatty" => await ChattyAsync(args),
    "spawn-tree" => await SpawnTreeAsync(args),
    "spawn-child" => await SpawnChildAsync(args),
    "spawn-leaf" => await SpawnLeafAsync(args),
    "print-env" => PrintEnvironment(),
    "utf8" => WriteUtf8(),
    "ansi" => WriteAnsi(),
    "stdin-wait" => await StdinWaitAsync(),
    "stdin-echo" => await StdinEchoAsync(),
    "report" => WriteReport(args),
    "secrets" => WriteSecrets(args),
    "agent" => await AgentAsync(args),
    _ => Help(),
};

static void RecordAdjacentInvocationMarker()
{
    try
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        File.AppendAllText(
            Path.Combine(directory, "corex-helper-invoked.txt"),
            string.Create(CultureInfo.InvariantCulture, $"invoked pid={Environment.ProcessId}{Environment.NewLine}"),
            new UTF8Encoding(false));
    }
    catch (IOException)
    {
        // Diagnostics only.
    }
}

static void RecordEnvironmentMarker(string[] invocation)
{
    var markerPath = Environment.GetEnvironmentVariable("COREX_MARKER_PATH");
    if (string.IsNullOrWhiteSpace(markerPath))
    {
        return;
    }

    for (var attempt = 0; attempt < 50; attempt++)
    {
        try
        {
            using var stream = new FileStream(markerPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"env-marker invoked pid={Environment.ProcessId} argv={string.Join(' ', invocation)}"));
            return;
        }
        catch (IOException)
        {
            Thread.Sleep(20);
        }
    }
}

static int Help()
{
    // Recorded next to the executable so a helper git invokes as a bare path - gpg.program
    // gets no arguments and no environment of ours - can still be observed by a test.
    RecordAdjacentInvocationMarker();

    Console.Error.WriteLine(
        "modes: exit|stdout|echo-args|marker|stderr|both|flood|silent|chatty|spawn-tree|spawn-child|spawn-leaf|print-env|utf8|ansi|stdin-wait|stdin-echo");
    return 64;
}

static int Arg(string[] args, int index, int fallback) =>
    args.Length > index && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : fallback;

static int Exit(string[] args) => Arg(args, 1, 0);

static int WriteStdout(string[] args)
{
    Console.Out.Write(args.Length > 1 ? args[1] : "stdout");
    Console.Out.Flush();
    return 0;
}

/// Records that it was invoked, then behaves well enough for git to keep going.
///
/// Used as a marker for the Phase 3 git secondary-execution control tests: a repository
/// configured filter, textconv, fsmonitor or signing helper points here, and the marker
/// file is the proof that git executed a repository controlled program. Nothing
/// destructive happens: the payload is an appended line.
///
/// When git passes a file path (textconv) the file is echoed; otherwise stdin is copied
/// to stdout byte for byte so clean/smudge filters do not corrupt content.
static int Marker(string[] args)
{
    var markerPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "corex-marker.txt");
    var extra = args.Length > 2 ? args[2] : null;

    for (var attempt = 0; attempt < 50; attempt++)
    {
        try
        {
            using var stream = new FileStream(markerPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"marker invoked pid={Environment.ProcessId} extra={extra ?? "(none)"}"));
            break;
        }
        catch (IOException)
        {
            Thread.Sleep(20);
        }
    }

    using var output = Console.OpenStandardOutput();

    if (extra is not null && File.Exists(extra))
    {
        using var file = File.OpenRead(extra);
        file.CopyTo(output);
    }
    else
    {
        using var input = Console.OpenStandardInput();
        input.CopyTo(output);
    }

    output.Flush();
    return 0;
}

/// Prints every argument exactly as the process received it, one per line.
/// Used to prove that argv entries never acquire shell semantics.
static int EchoArgs(string[] args)
{
    for (var i = 1; i < args.Length; i++)
    {
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ARG[{i - 1}]={args[i]}"));
    }

    Console.Out.WriteLine("ARGS-END");
    Console.Out.Flush();
    return 0;
}

static int WriteStderr(string[] args)
{
    Console.Error.Write(args.Length > 1 ? args[1] : "stderr");
    Console.Error.Flush();
    return 0;
}

static int WriteBoth(string[] args)
{
    Console.Out.Write(args.Length > 1 ? args[1] : "out");
    Console.Error.Write(args.Length > 2 ? args[2] : "err");
    Console.Out.Flush();
    Console.Error.Flush();

    // Optional fourth argument: exit with a specific code after writing both streams, so a
    // test can observe a failing step that still produced evidence.
    return Arg(args, 3, 0);
}

/// Writes a large payload to both streams concurrently, then a sentinel on each.
static async Task<int> FloodAsync(string[] args)
{
    var totalCharacters = Arg(args, 1, 4 * 1024 * 1024);
    const int BlockSize = 4096;
    var outBlock = new string('o', BlockSize);
    var errBlock = new string('e', BlockSize);

    var stdout = Task.Run(async () =>
    {
        for (var written = 0; written < totalCharacters; written += BlockSize)
        {
            await Console.Out.WriteAsync(outBlock);
        }

        await Console.Out.WriteAsync("STDOUT-END");
        await Console.Out.FlushAsync();
    });

    var stderr = Task.Run(async () =>
    {
        for (var written = 0; written < totalCharacters; written += BlockSize)
        {
            await Console.Error.WriteAsync(errBlock);
        }

        await Console.Error.WriteAsync("STDERR-END");
        await Console.Error.FlushAsync();
    });

    await Task.WhenAll(stdout, stderr);
    return 0;
}

/// Stays alive without producing any output, so the idle deadline can fire.
static async Task<int> SilentAsync(string[] args)
{
    await Task.Delay(TimeSpan.FromSeconds(Arg(args, 1, 120)));
    return 0;
}

/// Keeps emitting output, so the idle deadline never fires but the hard one must.
static async Task<int> ChattyAsync(string[] args)
{
    var intervalMs = Arg(args, 1, 100);
    var tick = 0;

    while (true)
    {
        await Console.Out.WriteLineAsync(
            string.Create(CultureInfo.InvariantCulture, $"tick {tick++}"));
        await Console.Out.FlushAsync();
        await Task.Delay(intervalMs);
    }
}

/// parent -> child -> grandchild, all alive indefinitely.
/// Each level appends "LEVEL=pid" to the report file so the test can wait
/// deterministically instead of sleeping.
static async Task<int> SpawnTreeAsync(string[] args)
{
    var reportPath = args[1];
    Report(reportPath, "PARENT", Environment.ProcessId);
    StartSelf("spawn-child", reportPath);
    await Console.Out.WriteLineAsync("parent-ready");
    await Console.Out.FlushAsync();
    await Task.Delay(Timeout.Infinite);
    return 0;
}

static async Task<int> SpawnChildAsync(string[] args)
{
    var reportPath = args[1];
    Report(reportPath, "CHILD", Environment.ProcessId);
    StartSelf("spawn-leaf", reportPath);
    await Task.Delay(Timeout.Infinite);
    return 0;
}

static async Task<int> SpawnLeafAsync(string[] args)
{
    Report(args[1], "GRANDCHILD", Environment.ProcessId);
    await Task.Delay(Timeout.Infinite);
    return 0;
}

static void StartSelf(string mode, string reportPath)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    startInfo.ArgumentList.Add(mode);
    startInfo.ArgumentList.Add(reportPath);
    Process.Start(startInfo);
}

static void Report(string path, string level, int pid)
{
    for (var attempt = 0; attempt < 50; attempt++)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{level}={pid}"));
            return;
        }
        catch (IOException)
        {
            Thread.Sleep(20);
        }
    }
}

static int PrintEnvironment()
{
    var names = Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .Select(e => new
        {
            Name = e.Key?.ToString() ?? string.Empty,
            Value = e.Value?.ToString() ?? string.Empty,
        })
        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    foreach (var entry in names)
    {
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{entry.Name}={entry.Value}"));
    }

    Console.Out.WriteLine("ENV-END");
    Console.Out.Flush();
    return 0;
}

static int WriteUtf8()
{
    Console.Out.WriteLine("İstanbul, çalışma, doğrulama");
    Console.Out.WriteLine("ĞÜŞİÖÇ ğüşıöç");
    Console.Error.WriteLine("hata: doğrulama başarısız");
    Console.Out.Flush();
    Console.Error.Flush();
    return 0;
}

static int WriteAnsi()
{
    const char Esc = '\u001b';
    Console.Out.Write(Esc + "[31m" + "kirmizi" + Esc + "[0m");
    Console.Out.Write(Esc + "[1;32m" + "yesil" + Esc + "[0m");
    Console.Out.Write(Esc + "]0;window title" + '\u0007');
    Console.Out.WriteLine("-son");
    Console.Error.Write(Esc + "[33m" + "uyari" + Esc + "[0m");
    Console.Out.Flush();
    Console.Error.Flush();
    return 0;
}

/// Reads stdin to end of stream, then reports and exits. Used to observe graceful
/// shutdown: closing stdin is how the runner asks a child to stop on Windows.
static async Task<int> StdinWaitAsync()
{
    await Console.Out.WriteLineAsync("waiting-stdin");
    await Console.Out.FlushAsync();

    _ = await Console.In.ReadToEndAsync();

    await Console.Out.WriteLineAsync("STDIN-CLOSED");
    await Console.Out.FlushAsync();
    return 0;
}

/// Reads stdin to end and writes it back, so a test can prove what was actually delivered.
static async Task<int> StdinEchoAsync()
{
    var payload = await Console.In.ReadToEndAsync();

    await Console.Out.WriteAsync("STDIN[" + payload + "]");
    await Console.Out.FlushAsync();
    return 0;
}

// --- Phase 4 verification fixture modes -------------------------------------------
//
//   report <relative path> <kind> [exit code]
//
// Writes a deterministic machine readable report relative to the current working
// directory, which is exactly the verification working directory the engine resolved.

static int WriteReport(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: report <relative path> <kind> [exit code]");
        return 2;
    }

    var relative = args[1];
    var kind = args[2];
    var exitCode = args.Length > 3 && int.TryParse(args[3], CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : 0;

    var content = ReportContent(kind);
    if (content is null)
    {
        Console.Error.WriteLine("unknown report kind: " + kind);
        return 2;
    }

    var full = Path.GetFullPath(relative);
    var directory = Path.GetDirectoryName(full);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(full, content, new UTF8Encoding(false));
    Console.Out.WriteLine("wrote " + kind + " report to " + relative);

    return exitCode;
}

static string? ReportContent(string kind) => kind switch
{
    "trx-broken" => Trx(12, 11, 1, 0),
    "trx-fixed" => Trx(12, 12, 0, 0),
    "trx-skips" => Trx(10, 7, 1, 2),
    "trx-outcomes" => TrxOutcomes(),
    "trx-malformed" => "<TestRun><ResultSummary><Counters total=\"3\" ",
    "trx-doctype" => TrxWithDoctype(),
    "junit-pass" => JUnit(8, 0, 0, 1),
    "junit-fail" => JUnit(8, 2, 1, 1),
    "junit-nested" => JUnitNested(),
    "junit-malformed" => "<testsuites><testsuite name=\"a\"></testsuites>",
    "huge" => Huge(),
    "junit-secret" => JUnitWithSecret(),
    _ => null,
};

static string Trx(int total, int passed, int failed, int notExecuted) =>
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
    + "<TestRun id=\"11111111-1111-1111-1111-111111111111\" "
    + "xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">"
    + "<Times creation=\"2026-01-01T10:00:00.0000000+00:00\" queuing=\"2026-01-01T10:00:00.0000000+00:00\" "
    + "start=\"2026-01-01T10:00:00.0000000+00:00\" finish=\"2026-01-01T10:00:09.0000000+00:00\" />"
    + "<ResultSummary outcome=\"Completed\"><Counters total=\"" + total.ToString(CultureInfo.InvariantCulture)
    + "\" executed=\"" + (total - notExecuted).ToString(CultureInfo.InvariantCulture)
    + "\" passed=\"" + passed.ToString(CultureInfo.InvariantCulture)
    + "\" failed=\"" + failed.ToString(CultureInfo.InvariantCulture)
    + "\" error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" notExecuted=\""
    + notExecuted.ToString(CultureInfo.InvariantCulture)
    + "\" /></ResultSummary></TestRun>";

static string TrxOutcomes() =>
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
    + "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>"
    + "<UnitTestResult testName=\"a\" outcome=\"Passed\" />"
    + "<UnitTestResult testName=\"b\" outcome=\"Failed\" />"
    + "<UnitTestResult testName=\"c\" outcome=\"NotExecuted\" />"
    + "<UnitTestResult testName=\"d\" outcome=\"Interstellar\" />"
    + "</Results></TestRun>";

static string TrxWithDoctype() =>
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
    + "<!DOCTYPE TestRun [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]>"
    + "<TestRun><ResultSummary><Counters total=\"1\" passed=\"1\" /></ResultSummary>"
    + "<Comment>&xxe;</Comment></TestRun>";

static string JUnit(int tests, int failures, int errors, int skipped) =>
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
    + "<testsuites name=\"fixture\" tests=\"" + tests.ToString(CultureInfo.InvariantCulture)
    + "\" failures=\"" + failures.ToString(CultureInfo.InvariantCulture)
    + "\" errors=\"" + errors.ToString(CultureInfo.InvariantCulture)
    + "\" skipped=\"" + skipped.ToString(CultureInfo.InvariantCulture)
    + "\" time=\"4.5\"><testsuite name=\"inner\" tests=\""
    + tests.ToString(CultureInfo.InvariantCulture) + "\" failures=\""
    + failures.ToString(CultureInfo.InvariantCulture) + "\" errors=\""
    + errors.ToString(CultureInfo.InvariantCulture) + "\" skipped=\""
    + skipped.ToString(CultureInfo.InvariantCulture) + "\" /></testsuites>";

static string JUnitNested() =>
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
    + "<testsuites>"
    + "<testsuite name=\"outer\">"
    + "<testsuite name=\"leaf-1\" tests=\"5\" failures=\"1\" errors=\"0\" skipped=\"1\" />"
    + "<testsuite name=\"leaf-2\" tests=\"4\" failures=\"0\" errors=\"1\" skipped=\"0\" />"
    + "</testsuite>"
    + "</testsuites>";

static string Huge()
{
    var builder = new StringBuilder(20 * 1024 * 1024);
    builder.Append("<testsuites tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testsuite name=\"");
    builder.Append('x', 17 * 1024 * 1024);
    builder.Append("\" tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\" /></testsuites>");
    return builder.ToString();
}

static string JUnitWithSecret() =>
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
    + "<testsuites name=\"fixture\" tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\" time=\"1.0\">"
    + "<testsuite name=\"inner\" tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\">"
    + "<system-out>configured with "
    + CoreX.Hub.ProcessFixture.SyntheticSecrets.OpenAiToken
    + " and "
    + CoreX.Hub.ProcessFixture.SyntheticSecrets.ConnectionString
    + "</system-out></testsuite></testsuites>";

//   secrets <stdout|stderr|both>
//
// Emits every synthetic secret family so a test can prove the persisted evidence contains
// none of them. The values are deliberately fake and exist only in this fixture.

static int WriteSecrets(string[] args)
{
    var where = args.Length > 1 ? args[1] : "both";
    var payload = CoreX.Hub.ProcessFixture.SyntheticSecrets.Payload();

    if (where is "stdout" or "both")
    {
        Console.Out.Write(payload);
        Console.Out.Flush();
    }

    if (where is "stderr" or "both")
    {
        Console.Error.Write(payload);
        Console.Error.Flush();
    }

    return 0;
}
// --- Phase 5A scripted agent modes ------------------------------------------------
//
//   agent <mode> [--structured-output <path>]
//
// Every deterministic behaviour of ScriptedAgentRuntime lives here rather than in the
// runtime, so the runtime stays a thin driver of an external tool exactly like a real
// provider adapter, and so git behaviour never becomes workspace management code inside
// production.

static async Task<int> AgentAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: agent <mode> [--structured-output <path>]");
        return 2;
    }

    var mode = args[1];
    var structuredOutput = ReadOption(args, "--structured-output");

    Console.Out.WriteLine("scripted agent mode: " + mode);

    if (structuredOutput is not null)
    {
        WriteStructuredOutput(structuredOutput, mode);
    }

    return mode switch
    {
        "no-op" => NoOp(),
        "apply-patch" => ApplyPatch(),
        "out-of-scope-write" => OutOfScopeWrite(),
        "secret-write" => SecretWrite(),
        "partial-write" => PartialWrite(),
        "hang" => await HangAsync(),
        "crash" => Crash(),
        "unexpected-commit" => UnexpectedCommit(),
        "unexpected-commit-then-crash" => UnexpectedCommitThenCrash(),
        _ => Unknown(mode),
    };
}

static string? ReadOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal))
        {
            return args[i + 1];
        }
    }

    return null;
}

static void WriteStructuredOutput(string path, string mode)
{
    var full = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(full);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(
        full,
        "{\"runtime\":\"scripted\",\"mode\":\"" + mode + "\"}",
        new UTF8Encoding(false));
}

static int NoOp()
{
    Console.Out.WriteLine("no changes were made");
    return 0;
}

static int ApplyPatch()
{
    WriteAgentFile(Path.Combine("src", "feature.txt"), "the deterministic change the fixture applies\n");
    Console.Out.WriteLine("applied src/feature.txt");
    return 0;
}

static int OutOfScopeWrite()
{
    // Inside the workspace, outside any plausible allowed scope. Whether that is a
    // violation is a gate decision, not this fixture's and not the runtime's.
    WriteAgentFile(Path.Combine("src", "feature.txt"), "in scope change\n");
    WriteAgentFile(Path.Combine("infra", "deploy.yaml"), "out of scope change\n");
    Console.Out.WriteLine("wrote src/feature.txt and infra/deploy.yaml");
    return 0;
}

static int SecretWrite()
{
    WriteAgentFile(
        Path.Combine("src", "config.txt"),
        "token=" + CoreX.Hub.ProcessFixture.SyntheticSecrets.OpenAiToken + "\n");

    Console.Out.WriteLine("wrote src/config.txt");
    return 0;
}

static int PartialWrite()
{
    // Half the work lands and stays on disk; the run then fails. Nothing is rolled back:
    // the partial state is exactly what later evidence collection has to observe.
    WriteAgentFile(Path.Combine("src", "step-one.txt"), "first half completed\n");
    Console.Error.WriteLine("the second half could not be completed");
    return 3;
}

static async Task<int> HangAsync()
{
    Console.Out.WriteLine("waiting");
    Console.Out.Flush();

    // Bounded: the deadline belongs to the caller, and this ceiling only stops a runaway
    // fixture if every timeout above it were somehow disabled.
    await Task.Delay(TimeSpan.FromMinutes(5));
    return 0;
}

static int Crash()
{
    Console.Out.Flush();

    // Dies rather than exiting, so a crash stays distinguishable from a non zero exit.
    throw new InvalidOperationException("scripted crash");
}

static int UnexpectedCommit()
{
    WriteAgentFile(Path.Combine("src", "feature.txt"), "work the agent then committed itself\n");

    var git = Environment.GetEnvironmentVariable("COREX_FIXTURE_GIT") ?? "git";

    // An external coding agent is perfectly capable of running git itself. Simulating that
    // belongs here in the harness: production must never gain git policy or repair code.
    var add = RunGit(git, "add", "-A");
    if (add != 0)
    {
        return add;
    }

    return RunGit(
        git,
        "-c", "user.name=Scripted Agent",
        "-c", "user.email=scripted@corex.local",
        "commit", "-q", "-m", "agent: unexpected commit");
}

static int UnexpectedCommitThenCrash()
{
    var committed = UnexpectedCommit();

    if (committed != 0)
    {
        return committed;
    }

    Console.Out.Flush();

    // Dies after committing, so the collision between a terminal agent failure and an
    // unexpected commit can be exercised.
    throw new InvalidOperationException("scripted crash after committing");
}

static int RunGit(string git, params string[] arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = git,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        Console.Error.WriteLine("could not start git");
        return 9;
    }

    Console.Out.Write(process.StandardOutput.ReadToEnd());
    Console.Error.Write(process.StandardError.ReadToEnd());
    process.WaitForExit();

    return process.ExitCode;
}

static int Unknown(string mode)
{
    Console.Error.WriteLine("unknown scripted agent mode: " + mode);
    return 2;
}

static void WriteAgentFile(string relativePath, string content)
{
    var full = Path.GetFullPath(relativePath);
    var directory = Path.GetDirectoryName(full);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(full, content, new UTF8Encoding(false));
}
