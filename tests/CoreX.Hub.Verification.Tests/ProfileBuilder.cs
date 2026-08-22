using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Verification.Tests;

/// <summary>Builds the accepted Phase 1 profile aggregate without repeating identifiers.</summary>
internal static class ProfileBuilder
{
    public static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid RepositoryId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static VerificationProfile Profile(
        string workingDirectory,
        params VerificationStep[] steps) =>
        new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            OrganizationId,
            RepositoryId,
            "fixture-profile",
            true,
            workingDirectory,
            steps,
            DateTimeOffset.UnixEpoch);

    public static VerificationStep Step(
        string name,
        IReadOnlyList<string> args,
        bool required = true,
        int timeoutSeconds = 30,
        VerificationResultParser parser = VerificationResultParser.None,
        string? resultPath = null,
        string executable = "fixture") =>
        new(name, executable, args, timeoutSeconds, required, parser, resultPath);
}
