using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Moving the Hub's own bytes into an environment, and bounded results back out.
///
/// Two directions with different trust, tested apart from the pinned source transfer on purpose.
/// The source path means committed git objects and nothing else, which is what keeps a developer's
/// uncommitted work out of an isolated environment; this path carries what the Hub itself
/// generated, and a result the execution produced. Nothing here may become a general way to put
/// files into WSL, because the next person to reach for it would inherit a name that promises the
/// content was reviewed.
///
/// The environment is scripted so the decisions can be tested. Whether a symlink redirects a read
/// out of the workspace, or an oversized document is refused rather than truncated, are questions
/// with answers that must not depend on which machine the suite runs on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrustedGuestFileTransferTests
{
    private const string Root = "/srv/corex/exec/_corex-context/run-1";
    private const string Workspace = "/srv/corex/exec/workspace";

    private static HardIsolationProfile Profile()
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = "corex-fixture",
            AgentUser = "corex-agent",
            ServiceUser = "corex-hub",
            ExecutionRoot = "/srv/corex/exec",
            EvidenceRoot = "/srv/corex/evidence",
            ProcessLimit = 64,
            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    private static TrustedGuestFileTransfer Transfer(ScriptedExecutionEnvironment environment) =>
        new(environment, Profile());

    /// <summary>An environment where the paths resolve where they claim to.</summary>
    private static ScriptedExecutionEnvironment Honest()
    {
        var script = new ScriptedExecutionEnvironment();

        script.AnswerPrefix("/usr/bin/mkdir -p ", string.Empty);
        script.AnswerPrefix("/usr/bin/chown ", string.Empty);
        script.AnswerPrefix("/usr/bin/chmod ", string.Empty);
        script.AnswerPrefix("/usr/bin/openssl base64 -d ", string.Empty);
        script.Answer("/usr/bin/realpath -m " + Root, Root);

        return script;
    }

    // -- inward: the Hub's own bytes ---------------------------------------------------------

    /// <summary>
    /// The bytes that arrive are the bytes that were sent, exactly.
    ///
    /// Checked by hash rather than by eye, and over content chosen to break a channel that is not
    /// binary safe: every byte value, a UTF-8 sequence, and the newline and null that a text
    /// oriented transport would mangle or truncate at.
    /// </summary>
    [Fact]
    public async Task The_bytes_that_arrive_are_the_bytes_that_were_sent()
    {
        var environment = Honest();

        var content = new byte[512];

        for (var i = 0; i < 256; i++)
        {
            content[i] = (byte)i;
        }

        Encoding.UTF8.GetBytes("context pack\nline two\0trailing — ünïcode").CopyTo(content, 256);

        var written = await Transfer(environment).WriteAsync(Root, "context.json", content, CancellationToken.None);

        Assert.True(written.Ok, written.Detail);

        // The channel carries base64, so what the environment received is decoded and compared
        // against the original by hash: a transport that dropped or re-encoded a byte fails here.
        var encoded = environment.LastStdin;

        Assert.NotNull(encoded);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(content)),
            Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(encoded!))));

        Assert.Equal(Root + "/context.json", written.Detail);
    }

    /// <summary>
    /// The destination is composed by the Hub, so there is nothing to traverse with.
    ///
    /// A leaf is one segment. Every one of these is a way to name somewhere else, and the answer
    /// is refusal rather than sanitising - a name that had to be repaired is not the name anybody
    /// meant.
    /// </summary>
    [Theory]
    [InlineData("../escape.json")]
    [InlineData("nested/context.json")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctx\nname.json")]
    [InlineData("C:\\windows\\context.json")]
    public async Task A_leaf_that_names_somewhere_else_is_refused(string leaf)
    {
        var written = await Transfer(Honest()).WriteAsync(Root, leaf, new byte[] { 1 }, CancellationToken.None);

        Assert.False(written.Ok);
        Assert.NotEmpty(written.Detail);
    }

    /// <summary>
    /// A destination that physically resolves outside its root is refused before it is written.
    ///
    /// Path text is not the authority. A symlink placed where the context is about to be written
    /// keeps every character of the path inside the root while pointing anywhere, so the machine is
    /// asked where it really goes - and asked first, so nothing is written and then noticed.
    /// </summary>
    [Fact]
    public async Task A_destination_that_resolves_outside_its_root_is_refused()
    {
        var environment = Honest();

        // The directory claims to be the context root and resolves into the agent's home.
        environment.Answer("/usr/bin/realpath -m " + Root, "/home/corex-agent/.ssh");

        var written = await Transfer(environment)
            .WriteAsync(Root, "context.json", new byte[] { 1 }, CancellationToken.None);

        Assert.False(written.Ok);
        Assert.Contains("physically resolve", written.Detail, StringComparison.Ordinal);

        // And nothing was written through the redirection.
        Assert.DoesNotContain(
            environment.Calls,
            call => call.Count > 0 && call[0] == "/usr/bin/openssl");
    }

    // -- outward: a bounded result ------------------------------------------------------------

    /// <summary>An ordinary structured result comes back exactly as the execution wrote it.</summary>
    [Fact]
    public async Task A_structured_result_comes_back_unchanged()
    {
        const string Document = "{\"summary\":\"done\",\"files\":[\"a.cs\"]}";

        var environment = new ScriptedExecutionEnvironment();
        var path = Workspace + "/result.json";

        environment.Answer("/usr/bin/realpath -m " + path, path);
        environment.Answer("/usr/bin/stat -c %s " + path, Document.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        environment.Answer("/usr/bin/base64 -w 0 " + path, Convert.ToBase64String(Encoding.UTF8.GetBytes(Document)));

        var read = await Transfer(environment).ReadAsync(Workspace, path, CancellationToken.None);

        Assert.True(read.Ok, read.Detail);
        Assert.Equal(Document, read.Content);
    }

    /// <summary>
    /// A result that is not there is absence, not failure.
    ///
    /// Every runtime already understands "no structured result was produced"; reporting it as an
    /// error would turn an ordinary run into a broken one.
    /// </summary>
    [Fact]
    public async Task A_result_that_was_never_written_is_absence_rather_than_failure()
    {
        var environment = new ScriptedExecutionEnvironment();
        var path = Workspace + "/result.json";

        environment.Answer("/usr/bin/realpath -m " + path, path);
        environment.Answer("/usr/bin/stat -c %s " + path, string.Empty, exitCode: 1);

        var read = await Transfer(environment).ReadAsync(Workspace, path, CancellationToken.None);

        Assert.True(read.Ok);
        Assert.Null(read.Content);
    }

    /// <summary>Just under the ceiling is read; the boundary is inclusive on the safe side.</summary>
    [Fact]
    public async Task A_result_just_under_the_ceiling_is_read()
    {
        var environment = new ScriptedExecutionEnvironment();
        var path = Workspace + "/result.json";
        var content = new string('a', 4096);

        environment.Answer("/usr/bin/realpath -m " + path, path);
        environment.Answer(
            "/usr/bin/stat -c %s " + path,
            (TrustedGuestFileTransfer.MaximumReadBytes - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        environment.Answer("/usr/bin/base64 -w 0 " + path, Convert.ToBase64String(Encoding.UTF8.GetBytes(content)));

        var read = await Transfer(environment).ReadAsync(Workspace, path, CancellationToken.None);

        Assert.True(read.Ok, read.Detail);
        Assert.Equal(content, read.Content);
    }

    /// <summary>
    /// Over the ceiling is an explicit failure, and nothing is read.
    ///
    /// The three ways this could go wrong are all worse than failing: a truncated document is
    /// invalid JSON that looks like a bad result, a partial one is valid JSON that is untrue, and
    /// reporting absence would say the execution produced nothing when it produced too much. The
    /// file is never read at all - the size decides before any bytes cross the channel.
    /// </summary>
    [Fact]
    public async Task A_result_over_the_ceiling_fails_and_is_not_read()
    {
        var environment = new ScriptedExecutionEnvironment();
        var path = Workspace + "/result.json";

        environment.Answer("/usr/bin/realpath -m " + path, path);
        environment.Answer(
            "/usr/bin/stat -c %s " + path,
            (TrustedGuestFileTransfer.MaximumReadBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var read = await Transfer(environment).ReadAsync(Workspace, path, CancellationToken.None);

        Assert.False(read.Ok);
        Assert.Null(read.Content);
        Assert.Contains("ceiling", read.Detail, StringComparison.Ordinal);

        // Not read, not truncated, not partially decoded.
        Assert.DoesNotContain(
            environment.Calls,
            call => call.Count > 0 && call[0] == "/usr/bin/base64");
    }

    /// <summary>
    /// Containment is decided before size, so a redirected read is refused on the better ground.
    ///
    /// A symlink pointing at something enormous outside the root could plausibly be reported as
    /// over the ceiling. It is reported as a containment failure instead, because that is what it
    /// is, and because shrinking the target must not turn it into an allowed read.
    /// </summary>
    [Fact]
    public async Task A_result_that_resolves_outside_the_root_is_refused_as_containment()
    {
        var environment = new ScriptedExecutionEnvironment();
        var path = Workspace + "/result.json";

        environment.Answer("/usr/bin/realpath -m " + path, "/srv/corex/evidence/blobs/secret");
        environment.Answer("/usr/bin/stat -c %s " + path, "16");

        var read = await Transfer(environment).ReadAsync(Workspace, path, CancellationToken.None);

        Assert.False(read.Ok);
        Assert.Contains("physically resolve", read.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(
            environment.Calls,
            call => call.Count > 0 && call[0] == "/usr/bin/base64");
    }

    /// <summary>
    /// A path outside the execution's own area is refused on its text before the machine is asked.
    ///
    /// The shared WSL surface, a peer's workspace, the evidence root, an auth home and a Windows
    /// path are all names for somewhere this read has no business going.
    /// </summary>
    [Theory]
    [InlineData("/mnt/wsl/marker.json")]
    [InlineData("/mnt/wslg/marker.json")]
    [InlineData("/mnt/c/Users/dev/result.json")]
    [InlineData("/srv/corex/evidence/blobs/aa/bb")]
    [InlineData("/home/corex-agent/.corex-auth")]
    [InlineData("/proc/self/environ")]
    [InlineData("/srv/corex/exec/other-org/result.json")]
    [InlineData("C:\\Users\\dev\\result.json")]
    public async Task A_path_outside_the_execution_area_is_refused(string path)
    {
        var environment = new ScriptedExecutionEnvironment();

        var read = await Transfer(environment).ReadAsync(Workspace, path, CancellationToken.None);

        Assert.False(read.Ok);
        Assert.Null(read.Content);

        // Refused on the path, so the environment was never asked anything at all.
        Assert.Empty(environment.Calls);
    }

    /// <summary>
    /// Nothing on this path reaches the environment through a host mount.
    ///
    /// Every command is an absolute Linux executable run through the trusted gateway. A transfer
    /// that used a drive letter, a UNC share or a mounted guest path would be moving bytes through
    /// exactly the surfaces the mount boundary exists to close.
    /// </summary>
    [Fact]
    public async Task Every_transfer_command_runs_inside_the_environment()
    {
        var environment = Honest();

        await Transfer(environment).WriteAsync(Root, "context.json", new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.NotEmpty(environment.Calls);

        foreach (var call in environment.Calls)
        {
            Assert.StartsWith("/usr/bin/", call[0], StringComparison.Ordinal);

            foreach (var argument in call)
            {
                Assert.DoesNotContain("\\\\wsl$", argument, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("/mnt/c", argument, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(":\\", argument, StringComparison.Ordinal);
            }

            // No shell anywhere: the executable and its arguments stay separate vector entries.
            Assert.DoesNotContain(call, a => a is "/bin/sh" or "/bin/bash" or "-c");
        }
    }
}
