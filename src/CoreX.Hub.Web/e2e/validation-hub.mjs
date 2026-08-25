import { execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { fixture } from './prepare.mjs';

/**
 * A complete, disposable Hub installation for the browser acceptance to work in.
 *
 * Its own data directory, its own configuration, its own git repository, and nothing shared
 * with the machine it runs on or with the operator's real Hub. Everything inside it is real:
 * a real repository, the real scripted runtime driving the real fixture executable, real
 * verification processes, and the shipped API host in front of it.
 *
 * What it is not is a mock. No workflow state is written into the database by these tests -
 * every task, execution, verification result and decision in the browser acceptance is
 * produced by the application doing its actual job. The only thing seeded here is
 * configuration, which is what an operator would have written before starting.
 *
 * Shaped after tests/CoreX.Hub.LoopZero.Tests/LoopZeroHub.cs, which is the existing convention
 * for exactly this: the same config file, the same scripted runtime key, the same fixture
 * binary, expressed where the browser harness can reach it.
 */
const here = dirname(fileURLToPath(import.meta.url));

export const root = join(here, '.artifacts', 'validation');
export const dataDirectory = join(root, 'data');
export const workspaceRoot = join(root, 'cx');
export const organizationRoot = join(workspaceRoot, '_fixtures', 'hub-e2e');
export const repositoryPath = join(organizationRoot, 'hub-sample');
export const configPath = join(dataDirectory, 'hub.config.yaml');

const git = (args) =>
  execFileSync('git', args, {
    cwd: repositoryPath,
    stdio: 'pipe',
    env: {
      ...process.env,
      GIT_AUTHOR_NAME: 'CoreX Validation',
      GIT_AUTHOR_EMAIL: 'validation@localhost',
      GIT_COMMITTER_NAME: 'CoreX Validation',
      GIT_COMMITTER_EMAIL: 'validation@localhost',
    },
  });

/**
 * Builds the installation from nothing.
 *
 * Torn down and rebuilt on every run rather than migrated forward: a browser acceptance that
 * started from yesterday's database would pass or fail for reasons nobody could reconstruct.
 */
export function createValidationHub() {
  rmSync(root, { recursive: true, force: true });

  mkdirSync(dataDirectory, { recursive: true });
  mkdirSync(repositoryPath, { recursive: true });

  git(['init', '-q', '-b', 'main']);
  git(['config', 'user.name', 'CoreX Validation']);
  git(['config', 'user.email', 'validation@localhost']);

  mkdirSync(join(repositoryPath, 'docs'), { recursive: true });
  writeFileSync(join(repositoryPath, 'README.md'), '# validation fixture\n');
  writeFileSync(join(repositoryPath, 'docs', 'notes.md'), 'notes\n');

  git(['add', '-A']);
  git(['commit', '-q', '-m', 'base']);

  writeFileSync(configPath, configuration(), 'utf8');

  return { root, dataDirectory, configPath, repositoryPath };
}

/**
 * The configuration an operator would have written.
 *
 * Two scripted runtimes rather than one, because two of the scenarios need an agent that does
 * not finish immediately: `apply-patch` is the ordinary case, and `hang` is what a cancellation
 * has to be able to interrupt. Both are declarations in a config file, exactly as a real
 * provider would be - the Hub is not told which one is "the test".
 *
 * There is deliberately no reviewer profile. This repository is configured as unreviewed,
 * which is a supported and tested composition, and a clean run therefore ends Passed.
 */
const configuration = () => `schemaVersion: 1
workspace:
  root: '${yaml(workspaceRoot)}'
  fixtureRoot: '${yaml(join(workspaceRoot, '_fixtures'))}'
verification:
  executables:
    fixture: '${yaml(fixture)}'
agents:
  scriptedRuntimes:
    - key: scripted
      fixturePath: '${yaml(fixture)}'
      mode: apply-patch
    - key: scripted-slow
      fixturePath: '${yaml(fixture)}'
      mode: hang
organizations:
  - slug: hub-e2e
    name: CoreX Validation
    filesystemRoot: '${yaml(organizationRoot)}'
    isolationLevel: Logical
    requiresHardIsolation: false
    environmentAllowlist: [PATH, SystemRoot, TEMP, TMP]
    agentProfiles:
      - name: scripted-implementer
        role: Implementer
        provider: Scripted
        runtimeKey: scripted
        permissionProfile: WorkspaceWrite
        softTimeoutSeconds: 120
        idleTimeoutSeconds: 180
        hardTimeoutSeconds: 240

      # The one that stays alive, so a cancellation has something to interrupt.
      - name: scripted-slow-implementer
        role: Implementer
        provider: Scripted
        runtimeKey: scripted-slow
        permissionProfile: WorkspaceWrite
        softTimeoutSeconds: 600
        idleTimeoutSeconds: 600
        hardTimeoutSeconds: 900
    projects:
      - slug: hub-sample
        name: Validation Sample
        taskKeyPrefix: E2E
        repositories:
          - slug: hub-sample-repo
            name: hub-sample
            localPath: '${yaml(repositoryPath)}'
            defaultBranch: main
            protectedBranches: [main]
            verificationProfiles:
              - name: fixture-verification
                isDefault: true
                workingDirectory: '.'
                steps:
                  - name: build
                    executable: fixture
                    args: ['exit', '0']
                    timeoutSeconds: 120
                    required: true
                    resultParser: none
                  - name: test
                    executable: fixture
                    args: ['report', 'results/results.trx', 'trx-fixed', '0']
                    timeoutSeconds: 120
                    required: true
                    resultParser: trx
                    resultPath: 'results/results.trx'
`;

/** A Windows path inside single quotes. Backslashes are literal in YAML single quotes. */
const yaml = (path) => resolve(path).replace(/'/g, "''");
