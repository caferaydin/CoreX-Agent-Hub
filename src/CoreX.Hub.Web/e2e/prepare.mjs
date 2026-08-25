import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Builds the things the browser acceptance runs against.
 *
 * Three artifacts, in the order they depend on each other: the production UI bundle, a
 * published API host that carries that bundle in its own wwwroot, and the deterministic
 * fixture executable the validation Hub uses as its agent and its verification tool.
 *
 * This is a separate command on purpose. `dotnet build` never runs npm and `npm test` never
 * runs dotnet - a compile that reached a package registry, or a unit test run that needed the
 * .NET SDK, would tie two toolchains together for no reason. Publishing is where the two meet,
 * because publishing is the step whose whole job is producing something runnable.
 */
const here = dirname(fileURLToPath(import.meta.url));
const web = resolve(here, '..');
const repo = resolve(web, '..', '..');

export const artifacts = join(here, '.artifacts');
export const host = join(artifacts, 'host');
export const fixture = join(
  repo,
  'tests',
  'CoreX.Hub.ProcessFixture',
  'bin',
  'Release',
  'net10.0',
  'corex-fixture.exe',
);

const run = (file, args, cwd) => {
  process.stdout.write(`\n$ ${file} ${args.join(' ')}\n`);
  execFileSync(file, args, { cwd, stdio: 'inherit', shell: true });
};

export function prepare() {
  mkdirSync(artifacts, { recursive: true });

  // The UI first: publishing the host refuses to proceed without a dist, deliberately.
  run('npm', ['run', 'build'], web);

  // A clean output every time. A stale hashed bundle left beside a new index.html is exactly
  // the failure the cache policy is designed around, and it must never be what a test proves.
  rmSync(host, { recursive: true, force: true });

  run(
    'dotnet',
    [
      'publish',
      join('src', 'CoreX.Hub.Api', 'CoreX.Hub.Api.csproj'),
      '-c',
      'Release',
      '-o',
      `"${host}"`,
      '--nologo',
    ],
    repo,
  );

  // The deterministic agent. Real process, real git, no provider and no network.
  run(
    'dotnet',
    [
      'build',
      join('tests', 'CoreX.Hub.ProcessFixture', 'CoreX.Hub.ProcessFixture.csproj'),
      '-c',
      'Release',
      '--nologo',
    ],
    repo,
  );

  for (const [what, path] of [
    ['the published host', join(host, 'hub-api.exe')],
    ['the published UI', join(host, 'wwwroot', 'index.html')],
    ['the process fixture', fixture],
  ]) {
    if (!existsSync(path)) {
      throw new Error(`${what} is missing after prepare: ${path}`);
    }
  }

  process.stdout.write(`\nready: ${host}\n`);
}

if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith('prepare.mjs')) {
  prepare();
}
