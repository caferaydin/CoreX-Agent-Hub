using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardEvidence;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Per-organization evidence, against real operating system principals.
///
/// Two claims, and they are separable. The first is that evidence belongs to the trusted control
/// plane: the agent whose execution produced it cannot list it, read it or alter it - because a
/// party that can edit the record of its own run has a record that means nothing. The second is
/// that one organization's store is physically its own: identical bytes in two organizations are
/// two files, and neither principal can see the other's.
///
/// The denials below are real. Four distinct uids, four real permission checks, no mocking of the
/// operating system. What they are not is a real deployment - both organizations live in one
/// distribution here, so cross-<i>environment</i> separation is not what is being shown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardEvidenceTests : IAsyncLifetime
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly HardEvidenceFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        if (_fixture.Available)
        {
            Assert.True(await _fixture.GatewayA().EnsureAsync(CancellationToken.None));
            Assert.True(await _fixture.GatewayB().EnsureAsync(CancellationToken.None));
        }
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text)));

    // -- placement --------------------------------------------------------------------------

    /// <summary>The store is on the environment's own filesystem, not a mounted host drive.</summary>
    [Fact]
    public async Task The_hard_evidence_root_is_linux_native()
    {
        if (!_fixture.Available)
        {
            return;
        }

        Assert.StartsWith("/", _fixture.EvidenceRootA, StringComparison.Ordinal);
        Assert.DoesNotContain("/mnt/", _fixture.EvidenceRootA, StringComparison.Ordinal);
        Assert.DoesNotContain(":", _fixture.EvidenceRootA, StringComparison.Ordinal);

        var filesystem = await _fixture.ControlAsync(
            ["/usr/bin/stat", "-f", "-c", "%T", _fixture.EvidenceRootA]);

        Assert.Contains(
            filesystem.Stdout.Trim(),
            new[] { "ext2/ext3", "ext4", "xfs", "btrfs", "overlayfs" });
    }

    /// <summary>A root that is not the environment's own storage is refused before anything is built.</summary>
    [Theory]
    [InlineData("/mnt/c/corex/evidence")]
    [InlineData(@"C:\corex\evidence")]
    [InlineData("srv/corex/evidence")]
    [InlineData("/srv/corex/../../etc")]
    public void An_evidence_root_that_is_not_linux_native_storage_is_refused(string root)
    {
        Assert.Throws<ArgumentException>(() => HardEvidenceLayout.For(root));
    }

    // -- the agent cannot reach its own evidence ---------------------------------------------

    /// <summary>
    /// The agent cannot list, read, write or delete its own organization's evidence.
    ///
    /// Its own is the interesting word. An agent has an obvious claim on the output of its own
    /// execution, and that claim is exactly what has to be refused: evidence is what a human
    /// decision is made from, and its subject is not entitled to edit it.
    ///
    /// Note the read attempt names a hash the test knows. Unpredictable names are not the
    /// mechanism - a hash is not a secret - the mechanism is that the directory is not the
    /// agent's to open.
    /// </summary>
    [Fact]
    public async Task The_agent_cannot_reach_its_own_organizations_evidence()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var stored = await _fixture.StoreA().WriteTextAsync("evidence about the agent's own run", CancellationToken.None);

        Assert.NotNull(stored);

        var blob = HardEvidenceLayout.For(_fixture.EvidenceRootA).Blob(stored!.Sha256);

        var listed = await _fixture.ControlAsync(["/usr/bin/ls", _fixture.EvidenceRootA], HardEvidenceFixture.AgentA);
        var read = await _fixture.ControlAsync(["/usr/bin/cat", blob], HardEvidenceFixture.AgentA);
        var written = await _fixture.ControlAsync(["/usr/bin/tee", blob], HardEvidenceFixture.AgentA, "rewritten\n");
        var created = await _fixture.ControlAsync(
            ["/usr/bin/tee", _fixture.EvidenceRootA + "/planted"], HardEvidenceFixture.AgentA, "planted\n");
        var deleted = await _fixture.ControlAsync(["/usr/bin/rm", "-f", blob], HardEvidenceFixture.AgentA);

        Assert.NotEqual(0, listed.ExitCode);
        Assert.NotEqual(0, read.ExitCode);
        Assert.NotEqual(0, written.ExitCode);
        Assert.NotEqual(0, created.ExitCode);
        Assert.NotEqual(0, deleted.ExitCode);

        // And the blob is still exactly what the Hub stored.
        Assert.Equal(
            "evidence about the agent's own run",
            await _fixture.StoreA().ReadTextAsync(stored.Sha256, CancellationToken.None));
    }

    // -- cross-organization -------------------------------------------------------------------

    /// <summary>
    /// Neither organization's principals can reach the other's evidence, in any direction.
    ///
    /// Four real uids and four real refusals. The service identities matter as much as the
    /// agents: a single Linux group able to read every hard store would satisfy every agent test
    /// here and would be precisely the coupling this design exists to remove.
    /// </summary>
    [Fact]
    public async Task No_principal_of_one_organization_can_reach_the_others_evidence()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var inA = await _fixture.StoreA().WriteTextAsync("A's evidence", CancellationToken.None);
        var inB = await _fixture.StoreB().WriteTextAsync("B's evidence", CancellationToken.None);

        Assert.NotNull(inA);
        Assert.NotNull(inB);

        var blobA = HardEvidenceLayout.For(_fixture.EvidenceRootA).Blob(inA!.Sha256);
        var blobB = HardEvidenceLayout.For(_fixture.EvidenceRootB).Blob(inB!.Sha256);

        foreach (var (principal, forbidden) in new[]
                 {
                     (HardEvidenceFixture.AgentA, blobB),
                     (HardEvidenceFixture.ServiceA, blobB),
                     (HardEvidenceFixture.AgentB, blobA),
                     (HardEvidenceFixture.ServiceB, blobA),
                 })
        {
            var read = await _fixture.ControlAsync(["/usr/bin/cat", forbidden], principal);
            var listed = await _fixture.ControlAsync(
                ["/usr/bin/ls", forbidden.Contains("org-a", StringComparison.Ordinal)
                    ? _fixture.EvidenceRootA
                    : _fixture.EvidenceRootB],
                principal);

            Assert.NotEqual(0, read.ExitCode);
            Assert.NotEqual(0, listed.ExitCode);
        }

        // Each service still manages its own.
        Assert.Equal("A's evidence", await _fixture.StoreA().ReadTextAsync(inA.Sha256, CancellationToken.None));
        Assert.Equal("B's evidence", await _fixture.StoreB().ReadTextAsync(inB.Sha256, CancellationToken.None));
    }

    /// <summary>
    /// Identical bytes in two organizations are two physically independent blobs.
    ///
    /// This is the deduplication that was deliberately given up. A shared content addressed store
    /// answers "do these bytes already exist" to whoever asks, and that answer is about somebody
    /// else's content - so the two files have the same name in different trees, the same hash,
    /// and no relationship at all.
    /// </summary>
    [Fact]
    public async Task The_same_content_in_two_organizations_is_two_independent_blobs()
    {
        if (!_fixture.Available)
        {
            return;
        }

        const string Shared = "identical evidence in both organizations";

        var a = await _fixture.StoreA().WriteTextAsync(Shared, CancellationToken.None);
        var b = await _fixture.StoreB().WriteTextAsync(Shared, CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Sha256, b!.Sha256);

        // Neither write saw the other: both are new.
        Assert.False(a.Deduplicated);
        Assert.False(b.Deduplicated);

        var pathA = HardEvidenceLayout.For(_fixture.EvidenceRootA).Blob(a.Sha256);
        var pathB = HardEvidenceLayout.For(_fixture.EvidenceRootB).Blob(b.Sha256);

        Assert.NotEqual(pathA, pathB);

        // Different inodes: two files, not one linked twice.
        var inodes = await _fixture.ControlAsync(["/usr/bin/stat", "-c", "%i", pathA, pathB]);
        var values = inodes.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        Assert.Equal(2, values.Count);
        Assert.NotEqual(values[0], values[1]);

        // And removing one leaves the other. Physical independence, demonstrated rather than
        // asserted from the paths.
        await _fixture.ControlAsync(["/usr/bin/rm", "-f", pathA]);

        Assert.False(await _fixture.StoreA().ExistsAsync(a.Sha256, CancellationToken.None));
        Assert.True(await _fixture.StoreB().ExistsAsync(b.Sha256, CancellationToken.None));
    }

    /// <summary>Dedup still works inside one organization, where it costs nothing.</summary>
    [Fact]
    public async Task Identical_content_inside_one_organization_is_stored_once()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var store = _fixture.StoreA();

        var first = await store.WriteTextAsync("stored twice by the same organization", CancellationToken.None);
        var second = await store.WriteTextAsync("stored twice by the same organization", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Sha256, second!.Sha256);
        Assert.False(first.Deduplicated);
        Assert.True(second.Deduplicated);
    }

    // -- content identity ---------------------------------------------------------------------

    /// <summary>A blob's name is the hash of the bytes in it, measured where they landed.</summary>
    [Fact]
    public async Task A_blobs_name_is_the_hash_of_its_own_bytes()
    {
        if (!_fixture.Available)
        {
            return;
        }

        const string Text = "content whose name is its hash";

        var stored = await _fixture.StoreA().WriteTextAsync(Text, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(HashOf(Text), stored!.Sha256);

        var measured = await _fixture.ControlAsync(
            ["/usr/bin/sha256sum", HardEvidenceLayout.For(_fixture.EvidenceRootA).Blob(stored.Sha256)],
            HardEvidenceFixture.ServiceA);

        Assert.StartsWith(stored.Sha256, measured.Stdout.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A read is refused when the bytes are not the bytes the hash names.
    ///
    /// Nothing should be able to produce this - the write path verifies before publishing - which
    /// is why the test injects it with the trusted identity. A content addressed store that
    /// returns whatever is at a path is not one.
    /// </summary>
    [Fact]
    public async Task A_blob_whose_content_no_longer_matches_its_name_is_not_returned()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var stored = await _fixture.StoreA().WriteTextAsync("original content", CancellationToken.None);

        Assert.NotNull(stored);

        var blob = HardEvidenceLayout.For(_fixture.EvidenceRootA).Blob(stored!.Sha256);

        await _fixture.ControlAsync(["/usr/bin/tee", blob], HardEvidenceFixture.ServiceA, "tampered content\n");

        Assert.Null(await _fixture.StoreA().ReadTextAsync(stored.Sha256, CancellationToken.None));
    }

    /// <summary>
    /// Nothing but a hash reaches a path.
    ///
    /// The traversal cases below are not refused by a filter: a caller cannot express them,
    /// because the only thing a caller supplies is sixty-four hexadecimal characters.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("0123456789abcdef")]
    public async Task A_read_that_does_not_name_a_hash_resolves_nothing(string candidate)
    {
        if (!_fixture.Available)
        {
            return;
        }

        Assert.Null(await _fixture.StoreA().ReadTextAsync(candidate, CancellationToken.None));
        Assert.False(await _fixture.StoreA().ExistsAsync(candidate, CancellationToken.None));
        Assert.Throws<ArgumentException>(
            () => HardEvidenceLayout.For(_fixture.EvidenceRootA).Blob(candidate));
    }

    // -- transport ----------------------------------------------------------------------------

    /// <summary>
    /// Bytes survive the channel exactly, including the ones a text pipe would ruin.
    ///
    /// The control channel carries text, and the obvious shortcut - hand the evidence over as a
    /// string - silently mangles anything that is not clean UTF-8. So the payload here is
    /// deliberately hostile: a NUL, multi-byte characters, a lone carriage return and the byte
    /// sequences that make a naive encoder guess.
    /// </summary>
    [Fact]
    public async Task Bytes_survive_the_channel_exactly()
    {
        if (!_fixture.Available)
        {
            return;
        }

        byte[] payload =
        [
            0x00, 0x01, 0x02, 0xFF, 0xFE, 0x0D, 0x0A,
            .. Utf8.GetBytes("düğün — ünicode ✓ 日本語"),
            0x00, 0x7F, 0x80, 0xC3, 0x28,
        ];

        var stored = await _fixture.StoreA().WriteAsync(payload, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), stored!.Sha256);
        Assert.Equal(payload.LongLength, stored.SizeBytes);

        var read = await _fixture.StoreA().ReadAsync(stored.Sha256, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(payload, read!);
    }

    /// <summary>
    /// An artifact past one transport window stores whole, and reads back byte for byte.
    ///
    /// The window is the channel's business. An artifact the Windows store would accept has to be
    /// an artifact this one accepts, or the isolation backend would be deciding what counts as
    /// evidence - and the same execution would hold a different record depending on which side of
    /// the boundary it ran on.
    /// </summary>
    [Fact]
    public async Task An_artifact_past_one_transport_window_stores_whole()
    {
        if (!_fixture.Available)
        {
            return;
        }

        // Deliberately not a multiple of the window: the trailing partial one is where an
        // off-by-one in the accounting would show.
        var payload = new byte[(HardEvidenceStore.TransferChunkBytes * 2) + 12_345];

        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 31) % 256);
        }

        var store = _fixture.StoreA();

        var stored = await store.WriteAsync(payload, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(payload.LongLength, stored.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), stored.Sha256);

        var read = await store.ReadAsync(stored.Sha256, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(payload, read!);

        // The environment agrees about the size, so nothing was truncated on the way in.
        Assert.Equal(payload.LongLength, await store.SizeAsync(stored.Sha256, CancellationToken.None));
    }

    // -- publication --------------------------------------------------------------------------

    /// <summary>
    /// Concurrent writers of identical content leave exactly one valid blob.
    ///
    /// Publication is a rename that refuses to replace, so the losers converge on the winner's
    /// file rather than overwriting it - and no reader ever sees a half-written one, because the
    /// name only comes into existence complete.
    /// </summary>
    [Fact]
    public async Task Concurrent_writers_of_identical_content_leave_one_valid_blob()
    {
        if (!_fixture.Available)
        {
            return;
        }

        const string Text = "written by several writers at once";

        var store = _fixture.StoreA();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => store.WriteTextAsync(Text, CancellationToken.None)));

        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.Equal(HashOf(Text), r!.Sha256));

        Assert.Equal(Text, await store.ReadTextAsync(HashOf(Text), CancellationToken.None));

        // One file, and nothing left unpublished.
        var pending = await _fixture.ControlAsync(
            ["/usr/bin/ls", HardEvidenceLayout.For(_fixture.EvidenceRootA).PendingRoot],
            HardEvidenceFixture.ServiceA);

        Assert.Equal(string.Empty, pending.Stdout.Trim());
    }

    /// <summary>
    /// A write that is interrupted leaves no blob at all.
    ///
    /// The unpublished file is inside the blob root so the rename is atomic, and it can never be
    /// mistaken for evidence: only the rename creates a content addressed name.
    /// </summary>
    [Fact]
    public async Task An_interrupted_write_publishes_nothing()
    {
        if (!_fixture.Available)
        {
            return;
        }

        const string Text = "a write that will not finish";

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var stored = await _fixture.StoreA().WriteTextAsync(Text, cancellation.Token);

        Assert.Null(stored);
        Assert.False(await _fixture.StoreA().ExistsAsync(HashOf(Text), CancellationToken.None));
    }

    // -- redaction ----------------------------------------------------------------------------

    /// <summary>
    /// The shared redaction runs before the bytes are hashed, so the raw secret is never stored.
    ///
    /// The same catalogue the Windows store uses. A second list for hard organizations would be a
    /// second definition of what a secret is, and the weaker one would decide.
    /// </summary>
    [Fact]
    public async Task Evidence_is_redacted_by_the_shared_catalogue_before_it_is_hashed()
    {
        if (!_fixture.Available)
        {
            return;
        }

        const string Raw = "authorization: Bearer sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        var written = await _fixture.GatewayA().WriteAsync(
            Raw, EvidenceKind.AgentStdout, "text/plain", CancellationToken.None);

        Assert.NotNull(written);
        Assert.True(written!.RedactionCount > 0);

        // The stored bytes are the redacted ones, and the name is their hash - not the raw text's.
        Assert.NotEqual(HashOf(Raw), written.Sha256);

        var stored = await _fixture.GatewayA().ReadTextAsync(written.Sha256, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.DoesNotContain("sk-ant-api03", stored!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_evidence_is_not_stored()
    {
        if (!_fixture.Available)
        {
            return;
        }

        Assert.Null(await _fixture.GatewayA().WriteAsync(
            string.Empty, EvidenceKind.AgentStdout, "text/plain", CancellationToken.None));
    }

    // -- what this does not change ------------------------------------------------------------

    /// <summary>The environment is still not hard-ready, and the network is still unproven.</summary>
    [Fact]
    public async Task Storing_evidence_here_does_not_make_this_environment_hard_ready()
    {
        if (!_fixture.Available)
        {
            return;
        }

        await _fixture.StoreA().WriteTextAsync("some evidence", CancellationToken.None);

        var readiness = await WslReadinessProbe.Create(Path.GetTempPath()).ProbeAsync(
            "fixture", _fixture.ProfileA(), CancellationToken.None);

        Assert.False(readiness.IsReady);

        // The network question is untouched by any of this and still has no answer.
        Assert.NotEqual(
            ReadinessStatus.Pass,
            readiness.Checks.Single(c => c.Id == HardIsolationChecks.ControlPlaneUnreachable).Status);
    }

    [Fact]
    public void An_isolated_evidence_store_does_not_make_hard_execution_available()
    {
        var plane = new UnavailableHardExecutionPlane();

        Assert.False(plane.IsAvailable);
        Assert.Contains("on the host", plane.UnavailableReason, StringComparison.Ordinal);
    }
}
