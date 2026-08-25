import { createValidationHub } from './validation-hub.mjs';

/**
 * Rebuilds the disposable Hub the browser acceptance runs in.
 *
 * A separate command, run before Playwright starts anything, because Playwright starts its
 * webServer *before* globalSetup - so a setup hook that recreated the data directory would be
 * deleting the database out from under a host that had already opened it.
 */
const { dataDirectory, repositoryPath } = createValidationHub();

process.stdout.write(`validation hub: ${dataDirectory}\n`);
process.stdout.write(`validation repository: ${repositoryPath}\n`);
