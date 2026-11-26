import { existsSync, readdirSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { ESLint, RuleTester } from 'eslint'
import type { Rule } from 'eslint'
import tseslint from 'typescript-eslint'
import { describe, expect, it, vi } from 'vitest'
import { WORKFLOW_ROUTES } from '@reeve-iot/workflow-client'
import { readOnlyApi } from '../../examples/playground/e2e/support/api-client.js'
import plugin, { CLIENT_MODULE, CLIENT_OPERATIONS, GRANTS } from './read-only-e2e.js'

const harness = vi.hoisted(() => ({ state: { baseUrl: 'http://127.0.0.1:9', operatorId: 'an-operator', hostPid: 0 } }))
vi.mock('../../examples/playground/e2e/support/state.js', () => ({ readHarnessState: () => harness.state }))

const rules: Record<string, Rule.RuleModule> = plugin.rules ?? {}
const rule = rules['read-only-api']
if (rule === undefined) throw new Error('The read-only plugin exports no read-only-api rule.')

const E2E = path.resolve('examples/playground/e2e')
const at = (file: string): string => path.join(E2E, file)
const SPEC = at('bypass.spec.ts')
const CLIENT = at(`${CLIENT_MODULE}.ts`)
const HARNESS = at('support/harness.ts')

function suiteFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(directory, entry.name)
    if (entry.isDirectory()) return entry.name === 'test-results' || entry.name === 'report' ? [] : suiteFiles(full)
    return /\.[cm]?[jt]sx?$/.test(entry.name) ? [full] : []
  })
}

const REAL_FILES = suiteFiles(E2E)

type Invalid = { code: string; filename?: string; errors: string[] }

const spec = (code: string, ...errors: string[]): Invalid => ({ code, filename: SPEC, errors })
const client = (code: string, ...errors: string[]): Invalid => ({ code, filename: CLIENT, errors })

const INVALID: Invalid[] = [
  spec("import { createWorkflowEditorClient } from '@reeve-iot/workflow-client'", 'client'),
  spec("import { WORKFLOW_EDITOR_ROUTES } from '@reeve-iot/workflow-client'", 'client'),
  spec("import { WorkflowApiError } from '@reeve-iot/workflow-client'", 'client'),
  spec("import { type WorkflowClient, createWorkflowClient } from '@reeve-iot/workflow-client'", 'client'),
  spec("import * as c from '@reeve-iot/workflow-client'\nawait c.createWorkflowEditorClient(o).updateWorkflow({})", 'client'),
  spec("import c from '@reeve-iot/workflow-client'", 'client'),
  spec("import '@reeve-iot/workflow-client'", 'client'),
  spec("const { createWorkflowEditorClient } = await import('@reeve-iot/workflow-client')", 'client'),
  spec("const c = require('@reeve-iot/workflow-client')", 'code'),
  spec("import c = require('@reeve-iot/workflow-client')", 'client'),
  spec("await page.request.fetch(url, { method: 'PUT', data })", 'member'),
  spec("await page.request.get('/api/workflows')", 'member'),
  spec("await globalThis.fetch(url, { method: 'POST' })", 'global'),
  spec("const r = new Request(url, { method: 'POST' })\nawait fetch(r)", 'network'),
  spec('await fetch(`${baseUrl}/health`)', 'network'),
  spec('await page.evaluateHandle(() => 1)', 'member'),
  spec('await locator.evaluateAll((nodes) => nodes.length)', 'member'),
  spec('await page.addInitScript(() => 1)', 'member'),
  spec("await page.routeFromHAR('recording.har')", 'member'),
  spec("test('x', async ({ request: r }) => { await r.post(url, { data }) })", 'member'),
  spec("test('x', async ({ request: r = null }) => { await r.fetch(url, { method: 'POST' }) })", 'member'),
  spec("const { fetch: f } = globalThis\nawait f(url, { method: 'POST' })", 'global'),
  spec("const { a: { request: r } } = fixtures\nawait r.patch(url, { data })", 'member'),
  spec("let r\n({ request: r } = fixtures)\nawait r.post(url, { data })", 'member'),
  spec("test('x', async ({ request }) => { const post = request.post\nawait post(url, { data }) })", 'member'),
  spec("test('x', async ({ request }) => { const post = getPost(request)\nawait post(url, { data }) })", 'member'),
  spec('const post = page.request.post\nawait post(url, { data })', 'member'),
  spec('await page.request.post.call(page.request, url)', 'member', 'member'),
  spec('const post = page.request.post.bind(page.request)', 'member', 'member'),
  spec('Reflect.apply(context.request.post, context.request, [url])', 'code', 'member', 'member'),
  spec('await page.context().request.post(url)', 'member'),
  spec("test('x', async ({ playwright }) => { await playwright.request.newContext() })", 'member'),
  spec('export const t = test.extend({ api: async ({ request }, use) => { await use(request) } })', 'member'),
  spec("const { request } = page", 'member'),
  spec("import { request } from '@playwright/test'", 'name'),
  spec("import { request as r } from '@playwright/test'\nawait r.newContext()", 'name'),
  spec("import * as pw from '@playwright/test'", 'whole'),
  spec("import pw from '@playwright/test'", 'whole'),
  spec("export * from '@playwright/test'", 'whole'),
  spec("export { request } from '@playwright/test'", 'name'),
  spec("import type { APIRequestContext } from '@playwright/test'", 'type'),
  spec("let context: import('@playwright/test').APIRequestContext", 'type'),
  spec("await page.route('**/api/**', (route) => route.fulfill({}))", 'member'),
  spec("await context.route('**', (route) => route.continue())", 'member'),
  spec("await page.routeWebSocket('**', (ws) => ws)", 'member'),
  spec("await page.exposeBinding('write', () => 1)", 'member'),
  spec("await page.exposeFunction('write', () => 1)", 'member'),
  spec("await page.evaluate(() => document.title)", 'member'),
  spec("await page.$eval('.status', (el) => el.textContent)", 'member'),
  spec("await page.$$eval('g', (els) => els.length)", 'member'),
  spec("await page.evaluate(() => fetch('/api/workflows', { method: 'POST' }))", 'member', 'network'),
  spec("await page.waitForFunction(() => fetch('/api/x', { method: 'POST' }))", 'member', 'network'),
  spec("await page.addScriptTag({ content: 'fetch()' })", 'member'),
  spec("await page.setContent('<script>fetch()</script>')", 'member'),
  spec('await context.newCDPSession(page)', 'member'),
  spec('await page._channel.send()', 'member'),
  spec('const { evaluate } = page', 'member'),
  spec('const { _channel: channel } = page', 'member'),
  spec("await page['evaluate'](fn)", 'member'),
  spec('await page[`route`](url, fn)', 'member'),
  spec("const name = 'evaluate'\nawait page[name](fn)", 'computed'),
  spec('const { [key]: taken } = page', 'computed'),
  spec("await fetch(url, { method: 'GET' })", 'network'),
  spec('const go = fetch\nawait go(url)', 'network'),
  spec('await window.fetch(url)', 'global'),
  spec('await self.fetch(url)', 'global'),
  spec('await global.fetch(url)', 'global'),
  spec('const g = globalThis\nawait g.fetch(url)', 'global'),
  spec("new XMLHttpRequest().open('POST', url)", 'network'),
  spec('new WebSocket(url)', 'network'),
  spec('new EventSource(url)', 'network'),
  spec("navigator.sendBeacon('/api/x', '{}')", 'network'),
  spec("eval('fetch')", 'code'),
  spec("new Function('return fetch')()", 'code'),
  spec("(() => 1).constructor('return fetch')()", 'member'),
  spec("Reflect.get(page, 'request')", 'code'),
  spec("process.getBuiltinModule('node:http')", 'process'),
  spec('process.kill(pid)', 'process'),
  spec('const { env } = process', 'process'),
  spec("import http from 'node:http'", 'module'),
  spec("import { request } from 'node:https'", 'module'),
  spec("import { get } from 'http'", 'module'),
  spec("import 'node:http2'", 'module'),
  spec("import { fetch } from 'undici'", 'module'),
  spec("import axios from 'axios'", 'module'),
  spec("import { spawn } from 'node:child_process'", 'module'),
  spec("import { connect } from 'node:net'", 'module'),
  spec("import { createRequire } from 'node:module'", 'module'),
  spec("import { createWorkflowOptionsSource } from '@reeve-iot/workflow-adapters'", 'module'),
  spec("import { startHarness } from './support/harness'", 'module'),
  spec("import { EditorShell } from '../src/EditorShell'", 'module'),
  spec("import { OPERATOR_HEADER } from '../src/host'", 'module'),
  spec("import { x } from './support/../../src/api'", 'module'),
  spec('const m = await import(name)', 'dynamic'),
  { code: "import { createWorkflowClient } from '@reeve-iot/workflow-client'", filename: HARNESS, errors: ['client'] },
  { code: 'await fetch(`${baseUrl}/health`)', filename: HARNESS, errors: ['network'] },
  client("import { createWorkflowEditorClient } from '@reeve-iot/workflow-client'", 'name'),
  client("import { WORKFLOW_ROUTES } from '@reeve-iot/workflow-client'", 'name'),
  client("import * as c from '@reeve-iot/workflow-client'", 'whole'),
  client("import { createWorkflowClient } from '@reeve-iot/workflow-client'\nconst c = createWorkflowClient(o)\nexport const p = () => c.previewExpression({})", 'client-call'),
  client("import { createWorkflowClient } from '@reeve-iot/workflow-client'\nconst c = createWorkflowClient(o)\nexport default c", 'client-call'),
  client("import { createWorkflowClient } from '@reeve-iot/workflow-client'\nlet c = createWorkflowClient(o)", 'client-call'),
  client("import { createWorkflowClient } from '@reeve-iot/workflow-client'\nconst make = createWorkflowClient", 'client-call'),
  client("import { createWorkflowClient } from '@reeve-iot/workflow-client'\nexport const c = () => createWorkflowClient(o)", 'client-call'),
  client("import { createWorkflowClient } from '@reeve-iot/workflow-client'\nconst c = createWorkflowClient(o)\nconst g = c.getWorkflows", 'client-call'),
  client("import { createWorkflowClient } from '@reeve-iot/workflow-client'\nconst c = createWorkflowClient(o)\nconst { getWorkflows } = c", 'client-call'),
  client("import { createWorkflowClient } from '@reeve-iot/workflow-client'\nconst c = createWorkflowClient(o)\nc[op]()", 'client-call', 'computed'),
  client("import { createWorkflowClient } from '@reeve-iot/workflow-client'\nconst c = createWorkflowClient(o)\nc.getWorkflows.call(c)", 'client-call', 'client-call'),
  client("import { createWorkflowClient as make } from '@reeve-iot/workflow-client'\nconst c = make(o)\nc.previewExpression({})", 'client-call'),
  client("await fetch(url, { method: 'POST' })", 'client-fetch'),
  client('await fetch(url)', 'client-fetch'),
  client("await fetch(url, { method: 'get' })", 'client-fetch'),
  client("await fetch(url, { method: 'GET', body: '{}' })", 'client-fetch'),
  client("await fetch(url, { method: 'GET', headers: { 'X-HTTP-Method-Override': 'POST' } })", 'client-fetch'),
  client("await fetch(url, { ...init, method: 'GET' })", 'client-fetch'),
  client('await fetch(url, init)', 'client-fetch'),
  client("await fetch(new Request(url, { method: 'POST' }), { method: 'GET' }, extra)", 'client-fetch'),
  client('const f = fetch', 'client-fetch'),
  client("await page.request.get('/api/workflows')", 'member'),
  client("new XMLHttpRequest().open('GET', url)", 'network'),
  spec("import { readOnlyApi } from './support/api-client'\nconst api = readOnlyApi('http://host:1234/api/workflows?', 'op')", 'client-bare'),
  spec("import { readOnlyApi } from './support/api-client'\nconst api = readOnlyApi", 'client-bare'),
]

new RuleTester({
  languageOptions: { ecmaVersion: 'latest', sourceType: 'module', parser: tseslint.parser },
}).run('read-only-api', rule, {
  valid: [
    ...REAL_FILES.map((file) => ({ code: readFileSync(file, 'utf8'), filename: file })),
    { code: "const fetch = (url, init) => [url, init]\nawait fetch('/x', { method: 'POST' })", filename: SPEC },
    { code: "function run(fetch) { return fetch('/x', { method: 'POST' }) }", filename: SPEC },
    { code: "await page.waitForResponse((r) => r.request().method() === 'POST')", filename: SPEC },
    { code: "page.on('request', (request) => request.url())", filename: SPEC },
    { code: 'const seen = new Map()\nseen.delete(key)\nconst first = list[0]\nconst ci = process.env[\'CI\']', filename: SPEC },
    { code: "import type { WorkflowClient } from '@reeve-iot/workflow-client'", filename: SPEC },
    { code: "import { type WorkflowExportDto } from '@reeve-iot/workflow-client'", filename: SPEC },
    { code: "import type { ChildProcess } from 'node:child_process'", filename: SPEC },
    { code: 'const run = (f: Function): Function => f', filename: SPEC },
    { code: "import { readOnlyApi } from './support/api-client'\nimport { test, expect } from './support/fixtures'\nconst api = readOnlyApi()", filename: SPEC },
    { code: "import { readFileSync } from 'node:fs'\nimport { readStoredDefinitionText } from '@reeve-iot/workflow-core'", filename: SPEC },
    { code: "import { test, expect, defineConfig, devices } from '@playwright/test'", filename: SPEC },
    { code: "import { spawn } from 'node:child_process'\nimport { createServer } from 'node:net'\nprocess.kill(pid, 'SIGTERM')", filename: HARNESS },
    { code: "import { startHarness } from './support/harness'", filename: at('global-setup.ts') },
    { code: "import { stopHarness } from './support/harness'", filename: at('global-teardown.ts') },
    {
      code: "import { createWorkflowClient } from '@reeve-iot/workflow-client'\n"
        + 'const client = createWorkflowClient({ baseUrl })\n'
        + `export const api = { ${CLIENT_OPERATIONS.map((op) => `${op}: (args) => client.${op}(args)`).join(', ')} }\n`
        + "export const probe = () => fetch(`${baseUrl}/health`, { method: 'GET', signal: AbortSignal.timeout(5) })",
      filename: CLIENT,
    },
  ],
  invalid: INVALID.map(({ code, filename, errors }) => ({
    code,
    filename,
    errors: errors.map((messageId) => ({ messageId })),
  })),
})

describe('the rule against the suite it guards', () => {
  it('finds the suite files it lints clean above', () => {
    const names = REAL_FILES.map((file) => path.relative(E2E, file))
    expect(names).toEqual(expect.arrayContaining(['smoke.spec.ts', `${CLIENT_MODULE}.ts`, 'support/harness.ts']))
  })

  it('names in each grant a file that exists', () => {
    for (const file of GRANTS.keys()) expect(existsSync(at(`${file}.ts`)), file).toBe(true)
  })

  it('lets the client module call exactly the GET routes, execute and import/validate', () => {
    const reads = Object.entries(WORKFLOW_ROUTES).filter(([, route]) => route.method === 'GET').map(([name]) => name)
    expect([...CLIENT_OPERATIONS].sort()).toEqual([...reads, 'executeWorkflow', 'validateImport'].sort())
    expect(WORKFLOW_ROUTES.executeWorkflow.path).toBe('/api/workflows/{id}/execute')
    expect(WORKFLOW_ROUTES.validateImport.path).toBe('/api/workflows/import/validate')
  })
})

describe('the origin a spec cannot steer', () => {
  it.each([
    'http://127.0.0.1:9/api/workflows',
    'http://127.0.0.1:9/api/workflows?',
    'http://127.0.0.1:9/#/workflows',
  ])('refuses the harness base URL %s, which is more than the origin it reads', (baseUrl) => {
    harness.state.baseUrl = baseUrl
    expect(() => readOnlyApi()).toThrow(/origin alone/)
  })

  it('reads the origin the harness itself writes', () => {
    harness.state.baseUrl = 'http://127.0.0.1:9'
    expect(() => readOnlyApi()).not.toThrow()
  })
})

describe('the e2e config', () => {
  const eslint = new ESLint()

  const WRITE = "import { createWorkflowEditorClient } from '@reeve-iot/workflow-client'"

  it.each(['.ts', '.js', '.mjs', '.cts', '.mts', '.tsx'])('lints a %s file under examples/playground/e2e', async (extension) => {
    const [result] = await eslint.lintText(WRITE, { filePath: `examples/playground/e2e/helper${extension}` })
    expect(result?.messages.map((message) => message.ruleId)).toContain('e2e/read-only-api')
  })

  it('reports the same file under the rule even with an eslint-disable comment in it', async () => {
    const source = `// eslint-disable-next-line e2e/read-only-api\n${WRITE}`
    const [result] = await eslint.lintText(source, { filePath: 'examples/playground/e2e/helper.ts' })
    expect(result?.messages.map((message) => message.ruleId)).toContain('e2e/read-only-api')
  })

  it('leaves the rule off for the page it observes', async () => {
    const [result] = await eslint.lintText(WRITE, { filePath: 'examples/playground/src/helper.ts' })
    expect(result?.messages.map((message) => message.ruleId)).not.toContain('e2e/read-only-api')
  })
})
