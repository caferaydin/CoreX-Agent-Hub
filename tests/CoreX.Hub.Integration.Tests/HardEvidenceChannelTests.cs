using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardEvidence;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// What the store does when the channel does not deliver what was sent.
///
/// The real environment tests cannot ask this. A working pipe always delivers the bytes it was
/// given, so the guest side hash always agrees with the Hub's and the check that compares them is
/// never the reason anything happens - which is exactly the shape of a safeguard that has been
/// removed without a single test noticing.
///
/// So the channel is scripted here, and made to lie. Nothing in this file talks to an environment;
/// what it establishes is that the store's central claim - a blob's name is the hash of the bytes
/// inside it - is enforced rather than assumed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardEvidenceChannelTests
{
    private const string EvidenceRoot = "/srv/corex/evidence";

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("hard evidence over a channel");

    private static string ExpectedHash => Convert.ToHexStringLower(SHA256.HashData(Payload));

    private static HardIsolationProfile Profile()
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = "corex-fixture",
            AgentUser = "corex-agent",
            ServiceUser = "corex-hub",
            ExecutionRoot = "/srv/corex/exec",
            EvidenceRoot = EvidenceRoot,
            ProcessLimit = 64,
            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    /// <summary>
    /// A channel that behaves: nothing is stored yet, the write is received, and it agrees.
    /// </summary>
    private static ScriptedExecutionEnvironment Honest(string reportedHash)
    {
        var script = new ScriptedExecutionEnvironment();

        // Nothing stored yet, so the existence probe fails and a write is attempted.
        script.AnswerPrefix("/usr/bin/stat -c %s ", string.Empty, exitCode: 1);
        script.AnswerPrefix("/usr/bin/mkdir ", string.Empty);
        script.AnswerPrefix("/usr/bin/openssl base64 ", string.Empty);
        script.AnswerPrefix("/usr/bin/dd ", string.Empty);
        script.AnswerPrefix("/usr/bin/sha256sum ", reportedHash + "  /pending");
        script.AnswerPrefix("/usr/bin/mv ", string.Empty);
        script.AnswerPrefix("/usr/bin/rm ", string.Empty);

        return script;
    }

    /// <summary>
    /// A channel that delivers what it was given produces a blob, which is the control.
    ///
    /// Without it the refusal below would also be satisfied by a store that refused everything.
    /// </summary>
    [Fact]
    public async Task An_honest_channel_publishes_the_blob()
    {
        var script = Honest(ExpectedHash);

        var stored = await HardEvidenceStore
            .Scripted(script, Profile())
            .WriteAsync(Payload, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(ExpectedHash, stored.Sha256);

        Assert.Contains(script.Calls, c => c.Count > 0 && c[0] == "/usr/bin/mv");
    }

    /// <summary>
    /// A channel that reports a different hash publishes nothing at all.
    ///
    /// The Hub measured the bytes it sent; the environment measured the bytes it received. When
    /// those disagree the file that arrived is not the file that was sent, and giving it the
    /// expected name would make every later read return content under a hash that is not its own.
    /// </summary>
    [Fact]
    public async Task A_channel_that_delivers_other_bytes_publishes_nothing()
    {
        // A valid hash, of something else entirely.
        var wrong = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("not what was sent")));

        var script = Honest(wrong);

        var stored = await HardEvidenceStore
            .Scripted(script, Profile())
            .WriteAsync(Payload, CancellationToken.None);

        Assert.Null(stored);

        // No rename happened, so the content addressed name was never created.
        Assert.DoesNotContain(script.Calls, c => c.Count > 0 && c[0] == "/usr/bin/mv");

        // And what did arrive was removed rather than left behind under a name nobody checks.
        Assert.Contains(script.Calls, c => c.Count > 0 && c[0] == "/usr/bin/rm");
    }

    /// <summary>
    /// A channel that reports nothing at all is a disagreement too.
    ///
    /// An empty answer is the shape a missing tool produces, and the failure mode worth naming is
    /// treating "could not measure" as "measured and agreed".
    /// </summary>
    [Fact]
    public async Task A_channel_that_measures_nothing_publishes_nothing()
    {
        var script = Honest(string.Empty);

        var stored = await HardEvidenceStore
            .Scripted(script, Profile())
            .WriteAsync(Payload, CancellationToken.None);

        Assert.Null(stored);
        Assert.DoesNotContain(script.Calls, c => c.Count > 0 && c[0] == "/usr/bin/mv");
    }

    /// <summary>
    /// The bytes never land under the content addressed name; only the rename puts them there.
    ///
    /// This is what makes a partial transfer harmless. Writing straight to the final name would
    /// leave a truncated file under a hash that describes something else, and a later reader has
    /// no way to know the difference - the name is the only claim it has.
    /// </summary>
    [Fact]
    public async Task Bytes_never_land_under_the_content_addressed_name()
    {
        var script = Honest(ExpectedHash);

        await HardEvidenceStore.Scripted(script, Profile()).WriteAsync(Payload, CancellationToken.None);

        var blob = HardEvidenceLayout.For(EvidenceRoot).Blob(ExpectedHash);

        var received = script.Calls.Single(c => c.Count > 0 && c[0] == "/usr/bin/openssl");

        Assert.DoesNotContain(blob, received);

        // Nor does the append that grows the unpublished file target it.
        var appended = script.Calls.Single(c => c.Count > 0 && c[0] == "/usr/bin/dd");

        Assert.DoesNotContain("of=" + blob, appended);

        // The one command that names it is the rename, as its destination.
        var moved = script.Calls.Single(c => c.Count > 0 && c[0] == "/usr/bin/mv");

        Assert.Equal(blob, moved[^1]);
        Assert.Contains("-n", moved);
    }

    /// <summary>
    /// A read that comes back as other bytes resolves to nothing.
    ///
    /// The same property from the other direction: a store that returns whatever is at a path is
    /// not a content addressed store, and the caller has no way to tell the difference.
    /// </summary>
    [Fact]
    public async Task A_read_that_returns_other_bytes_resolves_nothing()
    {
        var script = new ScriptedExecutionEnvironment();

        // The blob exists and reports a size, so a read is attempted.
        script.AnswerPrefix("/usr/bin/stat -c %s ", "19");
        script.AnswerPrefix("/usr/bin/dd ", string.Empty);
        script.AnswerPrefix("/usr/bin/rm ", string.Empty);
        script.AnswerPrefix(
            "/usr/bin/base64 ", Convert.ToBase64String(Encoding.UTF8.GetBytes("substituted content")));

        var bytes = await HardEvidenceStore
            .Scripted(script, Profile())
            .ReadAsync(ExpectedHash, CancellationToken.None);

        Assert.Null(bytes);
    }

    /// <summary>
    /// An artifact larger than one transport window crosses in several, and still publishes once.
    ///
    /// The window is a property of the channel, not of the evidence. What proves the two are
    /// separate is that the number of transfers grows with the artifact while the number of
    /// published names stays one.
    /// </summary>
    [Fact]
    public async Task An_artifact_larger_than_one_window_crosses_in_several()
    {
        var payload = new byte[(HardEvidenceStore.TransferChunkBytes * 2) + 1];

        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        var expected = Convert.ToHexStringLower(SHA256.HashData(payload));

        var script = Honest(expected);

        var stored = await HardEvidenceStore
            .Scripted(script, Profile())
            .WriteAsync(payload, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(expected, stored.Sha256);
        Assert.Equal(payload.LongLength, stored.SizeBytes);

        // Three windows: two full and the single trailing byte.
        Assert.Equal(3, script.Calls.Count(c => c.Count > 0 && c[0] == "/usr/bin/openssl"));
        Assert.Equal(3, script.Calls.Count(c => c.Count > 0 && c[0] == "/usr/bin/dd"));

        // And one name, created once.
        Assert.Single(script.Calls, c => c.Count > 0 && c[0] == "/usr/bin/mv");
    }

    /// <summary>
    /// The raw window and its encoded form are different numbers, and only one bounds the artifact.
    ///
    /// Base64 turns three bytes into four characters. Judging a raw artifact against a bound
    /// measured after that expansion would refuse a legitimate artifact for the size of its
    /// representation, so the two are kept apart deliberately.
    /// </summary>
    [Fact]
    public void The_encoded_window_is_larger_than_the_raw_window()
    {
        Assert.Equal(
            (HardEvidenceStore.TransferChunkBytes + 2) / 3 * 4,
            HardEvidenceStore.EncodedChunkCharacters);

        Assert.True(HardEvidenceStore.EncodedChunkCharacters > HardEvidenceStore.TransferChunkBytes);

        // A full window's encoded form fits in what one call may capture.
        var full = new byte[HardEvidenceStore.TransferChunkBytes];

        Assert.True(Convert.ToBase64String(full).Length <= HardEvidenceStore.EncodedChunkCharacters);
    }

    /// <summary>
    /// An artifact of exactly one window is one transfer, not two.
    ///
    /// The boundary case an off-by-one would make silently wrong: an extra empty window would
    /// still hash correctly and still publish, so nothing else here would notice.
    /// </summary>
    [Fact]
    public async Task An_artifact_of_exactly_one_window_crosses_once()
    {
        var payload = new byte[HardEvidenceStore.TransferChunkBytes];

        var script = Honest(Convert.ToHexStringLower(SHA256.HashData(payload)));

        Assert.NotNull(
            await HardEvidenceStore.Scripted(script, Profile()).WriteAsync(payload, CancellationToken.None));

        Assert.Single(script.Calls, c => c.Count > 0 && c[0] == "/usr/bin/openssl");
    }

    /// <summary>
    /// Every command the store issues enters as the organization's service identity.
    ///
    /// The agent has no access to the evidence tree, so a single call made as the agent would not
    /// merely be untidy - it would be the one operation the boundary exists to prevent.
    /// </summary>
    [Fact]
    public async Task Every_stored_blob_is_written_by_the_service_identity()
    {
        var script = Honest(ExpectedHash);

        await HardEvidenceStore.Scripted(script, Profile()).WriteAsync(Payload, CancellationToken.None);

        Assert.NotEmpty(script.Identities);
        Assert.All(script.Identities, u => Assert.Equal("corex-hub", u));
    }
}
