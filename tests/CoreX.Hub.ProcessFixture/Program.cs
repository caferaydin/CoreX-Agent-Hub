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
    "report" => WriteReport(args),
    "secrets" => WriteSecrets(args),
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
        "modes: exit|stdout|echo-args|marker|stderr|both|flood|silent|chatty|spawn-tree|spawn-child|spawn-leaf|print-env|utf8|ansi|stdin-wait");
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

namespace CoreX.Hub.ProcessFixture
{
    /// <summary>
    /// Synthetic credentials used only by the redaction tests. Nothing here is real, and
    /// nothing here is a credential for any system: the values exist so a test can prove
    /// they never reach a stored evidence blob.
    /// </summary>
    public static class SyntheticSecrets
    {
        public const string OpenAiToken = "sk-corexSYNTHETIC0000000000000000abcd";

        public const string GitHubToken = "ghp_corexSYNTHETIC0000000000000000abcd";

        public const string AwsAccessKey = "AKIACOREXSYNTHETIC00";

        public const string Jwt =
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJjb3JleC1zeW50aGV0aWMifQ.c29tZXN5bnRoZXRpY3NpZ25hdHVyZQ";

        public const string PasswordValue = "SyntheticCorexPassw0rd";

        public const string ConnectionString =
            "Server=localhost;Database=corex;User Id=corex;Password=" + PasswordValue + ";";

        public const string AuthorizationHeaderValue = "Bearer synthetic-corex-header-value";

        public const string PrivateKeyBody = "U1lOVEhFVElDLUNPUkVYLUtFWS1NQVRFUklBTA==";

        /// <summary>Text that only looks like a secret and must survive redaction unchanged.</summary>
        public static readonly string[] NearMisses =
        [
            "sketch-book was updated",
            "skip-this-step",
            "ghost_writer signed off",
            "AKIA is a prefix and AKIASHORT is not a key",
            "the user requested a password reset",
            "eyJ is not a token on its own",
            "Authorized: yes",
        ];

        public static string PrivateKeyBlock() =>
            "-----BEGIN RSA PRIVATE KEY-----\n"
            + PrivateKeyBody + "\n"
            + PrivateKeyBody + "\n"
            + "-----END RSA PRIVATE KEY-----";

        public static string[] AllSecretValues() =>
        [
            OpenAiToken,
            GitHubToken,
            AwsAccessKey,
            Jwt,
            PasswordValue,
            AuthorizationHeaderValue,
            PrivateKeyBody,
        ];

        public static string Payload()
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("openai=" + OpenAiToken);
            builder.AppendLine("github=" + GitHubToken);
            builder.AppendLine("aws=" + AwsAccessKey);
            builder.AppendLine("jwt=" + Jwt);
            builder.AppendLine("connection=" + ConnectionString);
            builder.AppendLine("Authorization: " + AuthorizationHeaderValue);
            builder.AppendLine("repeated=" + OpenAiToken);
            builder.AppendLine(PrivateKeyBlock());

            foreach (var line in NearMisses)
            {
                builder.AppendLine(line);
            }

            return builder.ToString();
        }
    }
}
