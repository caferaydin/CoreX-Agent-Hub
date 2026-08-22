using System.Runtime.Versioning;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;
using CoreX.Hub.Workspace.Abstractions;

namespace CoreX.Hub.Cli.Workspace;

/// <summary>
/// Composition layer adapter: the workspace module asks for a git command, and it is run
/// through ProcessRunner like every other process in the system.
///
/// This keeps two accepted invariants true at the same time. E.1 #1: there is still a
/// single operating system process launch point. E.1 #4: the workspace module does not
/// reference the execution module, and the execution module does not reference it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessGitCommandExecutor : IGitCommandExecutor
{
    private readonly string _gitExecutablePath;
    private readonly List<(string Root, FileSystemGuard Guard, ProcessRunner Runner)> _roots = [];

    public ProcessGitCommandExecutor(string gitExecutablePath, IReadOnlyList<string> trustRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutablePath);
        ArgumentNullException.ThrowIfNull(trustRoots);

        _gitExecutablePath = gitExecutablePath;

        foreach (var root in trustRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FileSystemGuard.TryCreate(root, out var guard).IsAllowed && guard is not null)
            {
                _roots.Add((guard.TrustRoot, guard, new ProcessRunner(guard)));
            }
        }

        if (_roots.Count == 0)
        {
            throw new ArgumentException("No usable trust root was supplied for git execution.", nameof(trustRoots));
        }
    }

    public async Task<GitCommandResult> ExecuteAsync(GitCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var runner = ResolveRunner(command.WorkingDirectory);
        if (runner is null)
        {
            return new GitCommandResult
            {
                Started = false,
                FailureMessage =
                    $"'{command.WorkingDirectory}' is not inside any trust root configured for git execution.",
            };
        }

        var hard = command.Timeout ?? TimeSpan.FromMinutes(2);

        var request = new ProcessRequest
        {
            Executable = _gitExecutablePath,
            Arguments = command.Arguments,
            WorkingDirectory = command.WorkingDirectory,
            EnvironmentAllowlist = CoreX.Hub.Workspace.Git.GitEnvironment.InheritedAllowlist,
            AdditionalEnvironment = command.Environment,
            HardTimeout = hard,
            IdleTimeout = hard > TimeSpan.FromSeconds(2) ? hard - TimeSpan.FromSeconds(1) : null,
        };

        var result = await runner.RunAsync(request, cancellationToken);

        return new GitCommandResult
        {
            Started = result.Started,
            ExitCode = result.ExitCode,
            Stdout = result.NormalizedStdout,
            Stderr = result.NormalizedStderr,
            FailureMessage = result.StartFailureMessage
                             ?? (result.TerminationReason == ProcessTerminationReason.Exited
                                 ? null
                                 : result.TerminationReason.ToString()),
            Duration = result.Duration,
        };
    }

    private ProcessRunner? ResolveRunner(string workingDirectory)
    {
        foreach (var candidate in _roots)
        {
            if (candidate.Guard.ValidateExisting(workingDirectory).IsAllowed)
            {
                return candidate.Runner;
            }
        }

        return null;
    }
}
