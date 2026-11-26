import js from '@eslint/js'
import tseslint from 'typescript-eslint'
import reactHooks from 'eslint-plugin-react-hooks'
import readOnlyE2e from './tools/eslint-rules/read-only-e2e.js'

export default tseslint.config(
  { ignores: ['**/dist/**', '**/coverage/**', '**/node_modules/**', '**/bin/**', '**/obj/**', 'artifacts/**', '**/dist-page/**', '**/e2e/test-results/**', '**/e2e/report/**'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      '@typescript-eslint/consistent-type-imports': 'error',
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
      'no-empty': ['error', { allowEmptyCatch: true }],
    },
  },
  {
    files: ['packages/core/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-imports': ['error', {
        patterns: [
          { group: ['@mui/*', '@emotion/*'], message: '@reeve-iot/workflow-core is headless: no design-system imports.' },
          { group: ['@reeve-iot/workflow-client', '@reeve-iot/workflow-adapters'], message: '@reeve-iot/workflow-core does not fetch: the client and its adapters depend on it, never the reverse.' },
        ],
      }],
    },
  },
  {
    files: ['packages/mui/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-imports': ['error', {
        patterns: [
          { group: ['@reeve-iot/workflow-client', '@reeve-iot/workflow-adapters'], message: '@reeve-iot/workflow-mui draws graphs: the host supplies data, not the client.' },
        ],
      }],
    },
  },
  {
    files: ['examples/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-imports': ['error', {
        patterns: [
          { group: ['**/packages/*', '../../packages/*'], message: 'The example depends on published package names only.' },
        ],
      }],
    },
  },
  {
    files: ['examples/playground/e2e/**/*.{ts,tsx,js,jsx,mjs,cjs,cts,mts}'],
    linterOptions: {
      noInlineConfig: true,
    },
    plugins: { e2e: readOnlyE2e, 'react-hooks': reactHooks },
    rules: {
      'e2e/read-only-api': 'error',
      'react-hooks/rules-of-hooks': 'off',
      'no-empty-pattern': 'off',
    },
  },
)
