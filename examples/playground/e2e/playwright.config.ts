import { fileURLToPath } from 'node:url'
import { defineConfig } from '@playwright/test'

const here = fileURLToPath(new URL('.', import.meta.url))

export default defineConfig({
  testDir: here,
  testMatch: '*.spec.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: process.env['CI'] !== undefined,
  timeout: 60_000,
  globalSetup: fileURLToPath(new URL('./global-setup.ts', import.meta.url)),
  globalTeardown: fileURLToPath(new URL('./global-teardown.ts', import.meta.url)),
  outputDir: fileURLToPath(new URL('./test-results/', import.meta.url)),
  reporter: [
    ['list'],
    ['html', { outputFolder: fileURLToPath(new URL('./report/', import.meta.url)), open: 'never' }],
  ],
  use: {
    viewport: { width: 1920, height: 1080 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
})
