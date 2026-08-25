import { expect, test } from './fixtures';

/**
 * The host an operator actually starts.
 *
 * One process, one loopback origin, one port: the built bundle and the API answering from the
 * same place, with no Vite anywhere. These run first because everything after them assumes it.
 */
test.describe('Production host', () => {
  test('serves the application at the root', async ({ page }) => {
    const response = await page.goto('/');

    expect(response?.status()).toBe(200);
    expect(response?.headers()['content-type']).toContain('text/html');

    await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible();
    await expect(page).toHaveURL(/\/tasks$/);
  });

  test('serves the API from the same origin', async ({ request, baseURL }) => {
    const response = await request.get('/api/v1/health');

    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toContain('application/json');

    const health = await response.json();

    expect(health.status).toBe('healthy');
    expect(health.databaseReachable).toBe(true);
    expect(health.configurationLoaded).toBe(true);

    // Same origin means the same authority - there is no second one to point at.
    expect(new URL(baseURL!).hostname).toBe('127.0.0.1');
  });

  test('survives direct navigation and refresh on every browser route', async ({ page }) => {
    for (const route of ['/tasks', '/repositories', '/agents']) {
      const first = await page.goto(route);

      expect(first?.status(), route).toBe(200);
      await expect(page).toHaveURL(new RegExp(`${route}$`));

      const again = await page.reload();

      expect(again?.status(), `${route} after refresh`).toBe(200);
      await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible();
    }
  });

  test('answers an unknown API route as an API and never as a page', async ({ request }) => {
    for (const path of [
      '/api/v1/definitely-not-a-route',
      '/api/v1/tasks/E2E-999/definitely-not-a-route',
      '/api/nope',
    ]) {
      const response = await request.get(path);

      expect(response.status(), path).toBe(404);
      expect(response.headers()['content-type'] ?? '', path).not.toContain('text/html');
      expect(await response.text(), path).not.toContain('<div id="root">');
    }
  });

  test('answers a missing asset as missing, never with the document', async ({ request }) => {
    for (const asset of ['/assets/index-nothashed.js', '/assets/gone.css', '/tasks/thing.js']) {
      const response = await request.get(asset);

      expect(response.status(), asset).toBe(404);
      expect(await response.text(), asset).not.toContain('<div id="root">');
    }
  });

  test('serves hashed assets that a browser may keep, and a document it may not', async ({
    page,
    request,
  }) => {
    await page.goto('/tasks');

    const script = await page.locator('script[src]').first().getAttribute('src');

    expect(script).toMatch(/\/assets\/.+\.js$/);

    const asset = await request.get(script!);

    expect(asset.status()).toBe(200);
    expect(asset.headers()['content-type']).toContain('javascript');
    expect(asset.headers()['cache-control']).toContain('max-age=31536000');

    const document = await request.get('/tasks');

    expect(document.headers()['cache-control']).toContain('no-cache');
  });
});
