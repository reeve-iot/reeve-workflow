import type { Page } from '@playwright/test'
import { expect, test } from './support/fixtures'
import { createWorkflow, openFileAndValidate, refuseOnOpen } from './support/rules'
import * as ported from './support/reeve-ported'
import { openPlayground } from './support/canvas'

const BLOCK = 'dispatch.enum-field'
const enumFindings = (page: Page) =>
  page.getByRole('list', { name: 'Findings' }).locator('[data-code^="literal-port-value-invalid"]')

test('an Enum configuration field is a select that stores the chosen name and raises no finding', async ({ page, harness }) => {
  const name = `Enum field picked ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await ported.createWorkflow(page, name)
  const nodeId = await ported.dropBlock(page, BLOCK, 'Freight')
  await ported.selectNode(page, nodeId)

  const select = page.getByTestId('port-value-input-Kind')
  await expect(select).toHaveJSProperty('tagName', 'SELECT')
  await expect(select.locator('option')).toHaveText(['', 'Toggle', 'Dim', 'Scene'])
  await select.selectOption('Dim')
  await expect(select).toHaveValue('Dim')
  await expect(enumFindings(page)).toHaveCount(0)

  await ported.drawWire(page, ported.startAnchor(), nodeId)
  expect(await ported.publish(page)).toBeGreaterThan(0)
  const stored = await ported.storedDefinition(harness.api, await ported.workflowIdOf(harness.api, name))
  expect(ported.storedConfig(ported.onlyNode(stored, '', BLOCK))['Kind']).toBe('Dim')
})

test('a stored ordinal in an Enum field is refused on open and shows its finding in the editor', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Enum field stored ordinal')
  const definition = JSON.stringify({
    nodes: [
      { id: 'start', blockTypeId: 'WorkflowInput', position: { x: 0, y: 0 }, configuration: {} },
      { id: 'e', blockTypeId: BLOCK, position: { x: 240, y: 0 }, configuration: { Kind: 1 } },
    ],
    edges: [{ id: 'w', sourceNodeId: 'start', targetNodeId: 'e', mappings: {} }],
  })
  const response = await openFileAndValidate(page, 'enum-ordinal.json', JSON.stringify({
    name: 'Enum ordinal', definitionJson: definition, description: null, executionTimeoutSeconds: null,
    exportedAtUtc: '2026-10-04T00:00:00Z', inputParametersJson: null, maxConcurrentRuns: null, maxQueuedRuns: null,
    mode: null, outputParametersJson: null, overflowBehavior: null, retryPolicyJson: null, schemaVersion: '1',
  }))

  await refuseOnOpen(page, response, 'literal-port-value-invalid')
  await expect(enumFindings(page).filter({ hasText: "must set 'Kind' to one of 'Toggle', 'Dim', 'Scene'" })).toBeVisible()
})
