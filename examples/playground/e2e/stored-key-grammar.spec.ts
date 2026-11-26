import type { Page, Response } from '@playwright/test'
import { expect, test } from './support/fixtures'
import { openPlayground } from './support/canvas'
import { openFileAndValidate } from './support/rules'

const dto = (definition: unknown, inputParametersJson: string | null = null) => JSON.stringify({
  name: 'Unknown key case', definitionJson: JSON.stringify(definition), description: null,
  executionTimeoutSeconds: null, exportedAtUtc: '2026-09-30T00:00:00Z', inputParametersJson,
  maxConcurrentRuns: null, maxQueuedRuns: null, mode: null, outputParametersJson: null,
  overflowBehavior: null, retryPolicyJson: null, schemaVersion: '1',
})

const rows = [
  { id: 'canvas#unlisted-key', at: '/rogue', definition: { nodes: [], edges: [], rogue: true } },
  { id: 'node#unlisted-key', at: '/nodes/0/rogue', definition: { nodes: [{ id: 'n', blockTypeId: 'Expression', position: { x: 1, y: 2 }, configuration: {}, rogue: true }], edges: [] } },
  { id: 'position#unlisted-key', at: '/nodes/0/position/rogue', definition: { nodes: [{ id: 'n', blockTypeId: 'Expression', position: { x: 1, y: 2, rogue: true }, configuration: {} }], edges: [] } },
  { id: 'declared-output#unlisted-key', at: '/nodes/0/outputPorts/0/rogue', definition: { nodes: [{ id: 'n', blockTypeId: 'Expression', position: { x: 1, y: 2 }, configuration: {}, outputPorts: [{ name: 'out', type: 'String', rogue: true }] }], edges: [] } },
  { id: 'error-policy#unlisted-key', at: '/nodes/0/errorPolicy/rogue', definition: { nodes: [{ id: 'n', blockTypeId: 'Expression', position: { x: 1, y: 2 }, configuration: {}, errorPolicy: { policy: 'Skip', rogue: true } }], edges: [] } },
  { id: 'connection#unlisted-key', at: '/edges/0/rogue', definition: { nodes: [], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {}, rogue: true }] } },
  { id: 'mapping#unlisted-key', at: '/edges/0/mappings/in/rogue', definition: { nodes: [], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: { in: { type: 'direct', sourceOutputName: 'out', rogue: true } } }] } },
  { id: 'projection#unlisted-key', at: '/edges/0/mappings/in/projection/rogue', definition: { nodes: [], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: { in: { type: 'direct', sourceOutputName: 'out', projection: { path: '$', rogue: true } } } }] } },
  { id: 'output-mapping#unlisted-key', at: '/edges/0/mappings/result/rogue', definition: { nodes: [{ id: 'a', blockTypeId: 'Expression', position: { x: 1, y: 2 }, configuration: {} }, { id: 'x', blockTypeId: 'WorkflowOutput', position: { x: 3, y: 2 }, configuration: {} }], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'x', mappings: { result: { type: 'direct', sourceOutputName: 'out', rogue: true } } }] } },
  { id: 'port-entry#unlisted-key', at: '/nodes/0/configuration/OutputPorts#/0/rogue', definition: { nodes: [{ id: 'n', blockTypeId: 'Expression', position: { x: 1, y: 2 }, configuration: { OutputPorts: JSON.stringify([{ name: 'out', type: 'String', rogue: true }]) } }], edges: [] } },
] as const

async function refusedAt(page: Page, response: Response, rule: string, at: string, code = rule): Promise<void> {
  expect(response.status()).toBe(400)
  expect((await response.json() as { detail?: string }).detail).toContain(`(rule ${rule}, at '${at}')`)
  const finding = page.getByRole('list', { name: 'Findings' }).locator(`[data-code="${code}"]`)
  await expect(finding).toHaveCount(1)
  await expect(finding).toContainText(`(rule ${rule}, at '${at}')`)
}

for (const { id, at, definition } of rows) {
  test(`Open file refuses an unlisted key at this object @row:${id}`, async ({ page, harness }) => {
    await openPlayground(page, harness.pageUrl)
    await refusedAt(page, await openFileAndValidate(page, `${id}.json`, dto(definition)), 'stored-key-unknown', at)
  })
}

test('Open file refuses an unlisted key at this object @row:parameter#unlisted-key', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  const response = await openFileAndValidate(page, 'parameter.json', dto({ nodes: [], edges: [] }, '[{"name":"value","type":"String","rogue":true}]'))
  await refusedAt(page, response, 'stored-key-unknown', '/0/rogue')
})

test('Open file accepts an absent parameter display name @row:parameter.displayName#absent', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  const response = await openFileAndValidate(page, 'parameter-without-label.json', dto({ nodes: [], edges: [] }, '[{"name":"value","type":"String"}]'))
  expect(response.status()).toBe(200)
})

test('Open file refuses an empty port-list name and import/validate refuses it @row:port-entry.name#blank', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  const response = await openFileAndValidate(page, 'blank-port-name.json', dto({
    nodes: [{ id: 'n', blockTypeId: 'Expression', position: { x: 1, y: 2 }, configuration: { OutputPorts: JSON.stringify([{ name: '', type: 'String' }]) } }], edges: [],
  }))
  await refusedAt(page, response, 'L7', '/nodes/0/configuration/OutputPorts#/0/name', 'definition-unreadable')
})

test('Open file reports an unlisted stored-document key and import/validate refuses it @row:stored-key-unknown', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  await refusedAt(page, await openFileAndValidate(page, 'root-key.json', dto({ nodes: [], edges: [], rogue: true })), 'stored-key-unknown', '/rogue')
})
