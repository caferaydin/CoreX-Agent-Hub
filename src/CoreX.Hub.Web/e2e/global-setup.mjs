import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { host } from './prepare.mjs';
import { configPath } from './validation-hub.mjs';

/**
 * Checks that the browser acceptance has something to run against, and builds nothing.
 *
 * Playwright starts the webServer before this hook, so anything that touched the data
 * directory here would be changing state under a host that had already opened it. The two
 * preparation steps are therefore their own commands, and this only reports when one of them
 * has not been run.
 */
export default function globalSetup() {
  for (const [what, path, fix] of [
    ['the published host', join(host, 'hub-api.exe'), 'npm run e2e:prepare'],
    ['the published UI', join(host, 'wwwroot', 'index.html'), 'npm run e2e:prepare'],
    ['the validation Hub', configPath, 'node e2e/reset.mjs'],
  ]) {
    if (!existsSync(path)) {
      throw new Error(`${what} is missing (${path}). Run \`${fix}\` first.`);
    }
  }
}
