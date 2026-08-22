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

var mode = args.Length > 0 ? args[0] : "help";

return mode switch
{
    "exit" => Exit(args),
    "stdout" => WriteStdout(args),
    "echo-args" => EchoArgs(args),
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
    _ => Help(),
};

static int Help()
{
    Console.Error.WriteLine(
        "modes: exit|stdout|echo-args|stderr|both|flood|silent|chatty|spawn-tree|spawn-child|spawn-leaf|print-env|utf8|ansi|stdin-wait");
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
    return 0;
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
