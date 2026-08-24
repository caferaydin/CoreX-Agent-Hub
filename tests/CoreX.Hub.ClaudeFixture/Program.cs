using System.Text;

// Deterministic stand-in for the real provider executable.
//
// Test infrastructure only. It lives under tests/ so the architecture source scanners,
// which read src/**, do not apply to it.
//
// Two jobs. It records exactly what the operating system handed it - the argument vector
// and stdin - so a test can assert the command surface the adapter really produced rather
// than the one it says it produces. And it emits a chosen event stream shape, so every
// way a run can end is provable without a network, an account or a paid request.
//
// The scenario is read from a file in the working directory because the adapter decides
// the argument vector and the child environment, and neither is a place for a test to
// smuggle in an instruction.

try
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.InputEncoding = new UTF8Encoding(false);
}
catch (IOException)
{
    // No console attached; the redirected pipes already use the encoding the parent set.
}

const string ArgumentsFileName = ".claude-argv.txt";
const string StandardInputFileName = ".claude-stdin.txt";
const string ScenarioFileName = ".claude-scenario";

var utf8 = new UTF8Encoding(false);

File.WriteAllLines(ArgumentsFileName, args, utf8);

// Only a print run is given a prompt on stdin. A probe such as --version is not, and its
// stdin stays open until the run ends, so reading it would hang rather than prove anything.
if (args.Contains("-p", StringComparer.Ordinal))
{
    File.WriteAllText(StandardInputFileName, await Console.In.ReadToEndAsync(), utf8);
}

var scenario = File.Exists(ScenarioFileName)
    ? File.ReadAllText(ScenarioFileName).Trim()
    : "success";

// Reflected back in the init event so a test can prove the value reached the child rather
// than merely that the adapter intended to send it.
var sessionId = Option("--session-id") ?? "00000000-0000-0000-0000-000000000000";
var tools = Option("--tools") ?? string.Empty;
var permissionMode = Option("--permission-mode") ?? "unset";
var model = Option("--model") ?? "unset";

return await RunAsync(scenario);

string? Option(string name)
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

async Task<int> RunAsync(string chosen)
{
    switch (chosen)
    {
        case "hang":
            Init(sessionId);
            await Task.Delay(TimeSpan.FromMinutes(10));
            return 0;

        case "no-init":
            Assistant("no init event was emitted");
            Result(sessionId, "success", isError: false);
            return 0;

        case "init-session-mismatch":
            Init("11111111-2222-3333-4444-555555555555");
            Assistant("done");
            Result(sessionId, "success", isError: false);
            return 0;

        case "result-session-mismatch":
            Init(sessionId);
            Assistant("done");
            Result("11111111-2222-3333-4444-555555555555", "success", isError: false);
            return 0;

        case "wrong-permission-mode":
            Init(sessionId, permissionModeOverride: "bypassPermissions");
            Assistant("done");
            Result(sessionId, "success", isError: false);
            return 0;

        case "mcp-active":
            Init(sessionId, mcpServers: ["{\"name\":\"unexpected\",\"status\":\"connected\"}"]);
            Assistant("done");
            Result(sessionId, "success", isError: false);
            return 0;

        case "extra-tool":
            Init(sessionId, toolsOverride: "Read,Edit,Write,Glob,Grep,Bash");
            Assistant("done");
            Result(sessionId, "success", isError: false);
            return 0;

        case "unknown-event":
            Init(sessionId);
            Write("{\"type\":\"a_future_event\",\"detail\":\"nothing here yet\"}");
            Assistant("done");
            Result(sessionId, "success", isError: false);
            return 0;

        case "unknown-property":
            Write(
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + sessionId
                + "\",\"permissionMode\":\"" + permissionMode + "\",\"model\":\"" + model
                + "\",\"tools\":[" + ToolArray(tools) + "],\"mcp_servers\":[],"
                + "\"a_property_from_the_future\":{\"nested\":[1,2,3]}}");
            Assistant("done");
            Write(
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"" + sessionId
                + "\",\"result\":\"done\",\"another_future_property\":42,"
                + "\"usage\":{\"input_tokens\":11,\"output_tokens\":7}}");
            return 0;

        case "malformed-then-result":
            Init(sessionId);
            Write("{\"type\":\"assistant\", this is not json");
            Assistant("done");
            Result(sessionId, "success", isError: false);
            return 0;

        case "no-result":
            Init(sessionId);
            Assistant("i said something and then stopped");
            return 0;

        case "is-error":
            Init(sessionId);
            Assistant("i said something reassuring first");
            Result(sessionId, "error_during_execution", isError: true);
            return 0;

        case "nonzero-exit":
            Init(sessionId);
            Assistant("everything looked fine");
            Result(sessionId, "success", isError: false);
            return 3;

        case "partial-then-crash":
            Init(sessionId);
            Assistant("halfway through");
            return -1073741819;

        case "mixed-content":
            Init(sessionId);
            Write(
                "{\"type\":\"assistant\",\"session_id\":\"" + sessionId + "\",\"message\":{\"role\":\"assistant\","
                + "\"content\":["
                + "{\"type\":\"thinking\",\"thinking\":\"THIS IS REASONING\"},"
                + "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{\"file_path\":\"C:/secret\"}},"
                + "{\"type\":\"text\",\"text\":\"first sentence. \"},"
                + "{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"THIS IS TOOL OUTPUT\"},"
                + "{\"type\":\"text\",\"text\":\"second sentence.\"}]}}");
            Result(sessionId, "success", isError: false);
            return 0;

        case "multi-model":
            Init(sessionId);
            Assistant("done");
            Write(
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"" + sessionId
                + "\",\"result\":\"done\",\"total_cost_usd\":0.0425,"
                + "\"usage\":{\"input_tokens\":120,\"output_tokens\":45,"
                + "\"cache_read_input_tokens\":900,\"cache_creation_input_tokens\":300},"
                + "\"modelUsage\":{"
                + "\"claude-opus-5\":{\"inputTokens\":100,\"outputTokens\":40,\"cacheReadInputTokens\":900,"
                + "\"cacheCreationInputTokens\":300,\"costUSD\":0.04},"
                + "\"claude-haiku-4-5-20251001\":{\"inputTokens\":20,\"outputTokens\":5,\"cacheReadInputTokens\":0,"
                + "\"cacheCreationInputTokens\":0,\"costUSD\":0.0025}}}");
            return 0;

        case "slow":
            // Emits with real gaps so a test can prove the stream was observed while the run
            // was still going, rather than reconstructed once the child had exited.
            Init(sessionId);
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            Assistant("done");
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            Result(sessionId, "success", isError: false);
            return 0;

        case "rate-limit-then-success":
            Init(sessionId);
            Write("{\"type\":\"rate_limit_event\",\"rate_limit\":{\"status\":\"allowed_warning\"}}");
            Assistant("done");
            Result(sessionId, "success", isError: false);
            return 0;

        default:
            Init(sessionId);
            Assistant("done");
            Result(sessionId, "success", isError: false);
            return 0;
    }
}

void Init(
    string session,
    string? permissionModeOverride = null,
    string? toolsOverride = null,
    IReadOnlyList<string>? mcpServers = null) =>
    Write(
        "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"" + Json(Directory.GetCurrentDirectory())
        + "\",\"session_id\":\"" + session
        + "\",\"permissionMode\":\"" + (permissionModeOverride ?? permissionMode)
        + "\",\"model\":\"" + model
        + "\",\"tools\":[" + ToolArray(toolsOverride ?? tools)
        + "],\"mcp_servers\":[" + string.Join(',', mcpServers ?? []) + "],\"apiKeySource\":\"none\"}");

void Assistant(string text) =>
    Write(
        "{\"type\":\"assistant\",\"session_id\":\"" + sessionId + "\",\"message\":{\"role\":\"assistant\","
        + "\"content\":[{\"type\":\"text\",\"text\":\"" + Json(text) + "\"}]}}");

void Result(string session, string subtype, bool isError) =>
    Write(
        "{\"type\":\"result\",\"subtype\":\"" + subtype
        + "\",\"is_error\":" + (isError ? "true" : "false")
        + ",\"session_id\":\"" + session
        + "\",\"result\":\"done\",\"duration_ms\":12,\"num_turns\":1,\"total_cost_usd\":0.01,"
        + "\"usage\":{\"input_tokens\":11,\"output_tokens\":7,\"cache_read_input_tokens\":3,"
        + "\"cache_creation_input_tokens\":2},"
        + "\"modelUsage\":{\"" + model + "\":{\"inputTokens\":11,\"outputTokens\":7,\"cacheReadInputTokens\":3,"
        + "\"cacheCreationInputTokens\":2,\"costUSD\":0.01}}}");

static string ToolArray(string commaSeparated) =>
    string.Join(
        ',',
        commaSeparated
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => "\"" + t + "\""));

static string Json(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

static void Write(string line)
{
    Console.Out.Write(line);
    Console.Out.Write('\n');
    Console.Out.Flush();
}
