namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Fixture configuration used by the integration tests.
/// Contains no real company, repository, credential or path outside the temp directory
/// contract (ADR-0017: no Kronos data anywhere in the repository).
/// </summary>
internal static class FixtureConfig
{
    public const string Default = """
        schemaVersion: 1
        workspace:
          root: 'D:\cx'
          fixtureRoot: 'D:\cx\_fixtures'
        organizations:
          - slug: hub-dev
            name: Hub Development Fixture
            filesystemRoot: 'D:\cx\_fixtures\hub-dev'
            isolationLevel: Logical
            requiresHardIsolation: false
            environmentAllowlist: [PATH, SystemRoot]
            agentProfiles:
              - name: scripted-implementer
                role: Implementer
                provider: Scripted
                runtimeKey: scripted
                permissionProfile: WorkspaceWrite
                softTimeoutSeconds: 300
                idleTimeoutSeconds: 600
                hardTimeoutSeconds: 1800
            projects:
              - slug: hub-sample
                name: Hub Sample Fixture
                taskKeyPrefix: HUB
                repositories:
                  - slug: hub-sample-repo
                    name: hub-sample
                    localPath: 'D:\cx\_fixtures\hub-dev\hub-sample'
                    defaultBranch: main
                    protectedBranches: [main]
                    verificationProfiles:
                      - name: dotnet-default
                        isDefault: true
                        workingDirectory: '.'
                        steps:
                          - name: build
                            executable: dotnet
                            args: ['build']
                            timeoutSeconds: 600
                            required: true
                            resultParser: none
                          - name: test
                            executable: dotnet
                            args: ['test']
                            timeoutSeconds: 1800
                            required: true
                            resultParser: trx
                            resultPath: 'results/results.trx'
          - slug: isolation-gate-fixture
            name: Hard Isolation Gate Fixture
            filesystemRoot: 'D:\cx\_fixtures\isolation-gate-fixture'
            isolationLevel: Logical
            requiresHardIsolation: true
            environmentAllowlist: [PATH]
            agentProfiles: []
            projects:
              - slug: gate-sample
                name: Gate Sample Fixture
                taskKeyPrefix: GATE
                repositories:
                  - slug: gate-sample-repo
                    name: gate-sample
                    localPath: 'D:\cx\_fixtures\isolation-gate-fixture\gate-sample'
                    defaultBranch: main
                    protectedBranches: [main]
                    verificationProfiles:
                      - name: noop
                        isDefault: true
                        workingDirectory: '.'
                        steps:
                          - name: build
                            executable: dotnet
                            args: ['build']
                            timeoutSeconds: 300
                            required: true
                            resultParser: none
        """;
}
