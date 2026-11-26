import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  resolve: {
    alias: {
      '@reeve-iot/workflow-core': fileURLToPath(new URL('./packages/core/src/index.ts', import.meta.url)),
      '@reeve-iot/workflow-client': fileURLToPath(new URL('./packages/client/src/index.ts', import.meta.url)),
      '@reeve-iot/workflow-adapters': fileURLToPath(new URL('./packages/adapters/src/index.ts', import.meta.url)),
      '@reeve-iot/workflow-mui': fileURLToPath(new URL('./packages/mui/src/index.ts', import.meta.url)),
      '@reeve-iot/workflow-example-dispatch': fileURLToPath(new URL('./examples/dispatch/src/index.ts', import.meta.url)),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    testTimeout: 15_000,
    setupFiles: ['./vitest.setup.ts'],
    include: ['packages/*/src/**/*.test.{ts,tsx}', 'examples/*/src/**/*.test.{ts,tsx}', 'tools/*/*.test.ts'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov', 'cobertura'],
      include: ['packages/*/src/**/*.{ts,tsx}'],
      exclude: ['packages/*/src/**/*.test.{ts,tsx}', 'packages/*/src/**/index.ts'],
    },
  },
})
