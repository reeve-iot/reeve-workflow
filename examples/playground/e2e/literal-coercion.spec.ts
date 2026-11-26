import { readStoredDefinitionText } from '@reeve-iot/workflow-core'
import { expect, test } from './support/fixtures'
import { addWorkflowParameter, closeMapping, createWorkflow, drawWire, dropBlock, openFileAndValidate, refuseOnOpen } from './support/rules'
import { openPlayground, publishWorkflowRequest } from './support/canvas'
import * as ported from './support/reeve-ported'

test('a number typed into a numeric port publishes as a number', async ({ page, harness }) => {
  const name = await createWorkflow(page, harness.pageUrl, 'Parsed text literal')
  await addWorkflowParameter(page, 'input', 'Seed', 'Seed')
  await dropBlock(page, 'WaitForDuration', 'Control Flow', { x: 0.58, y: 0.48 })
  const targetId = await page.getByTestId('workflow-node-WaitForDuration').getAttribute('data-node-id')
  if (targetId === null) throw new Error('The dropped WaitForDuration node has no id.')

  await drawWire(page, page.getByTestId('workflow-port-__workflow_input__-output-Seed'),
    page.getByTestId(`workflow-port-${targetId}-input-DurationMs`))
  await page.getByLabel('Duration (ms) mapping type').click()
  await page.getByRole('option', { name: 'Literal', exact: true }).click()
  await page.getByLabel('Duration (ms) literal', { exact: true }).fill('5')
  await closeMapping(page, 'Save Mappings')

  const response = await publishWorkflowRequest(page)
  expect((await response.json() as { currentVersionNumber: number }).currentVersionNumber).toBe(2)
  const workflow = (await harness.api.getWorkflows()).find((item) => item.name === name)
  if (workflow === undefined) throw new Error(`No workflow named '${name}'.`)
  const version = await harness.api.getVersion({ id: workflow.id, versionNumber: 2 })
  const stored = readStoredDefinitionText(version.definitionJson)
  if (!stored.ok) throw new Error(`The stored document cannot be read: ${JSON.stringify(stored.fault)}`)
  const edge = stored.definition.edges.find((item) => item.targetNodeId === targetId)
  expect(edge?.mappings.DurationMs).toEqual({ type: 'literal', literalValue: 5 })
})

test('text stored for a numeric port is refused on open, with its finding code @row:literal-port-value-invalid', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rejected scalar literal')
  const definition = JSON.stringify({
    nodes: [
      { id: 'start', blockTypeId: 'WorkflowInput', position: { x: 0, y: 0 }, configuration: {} },
      { id: 'wait', blockTypeId: 'WaitForDuration', position: { x: 240, y: 0 }, configuration: { DurationMs: 'five' } },
    ],
    edges: [{ id: 'w', sourceNodeId: 'start', targetNodeId: 'wait', mappings: {} }],
  })
  const response = await openFileAndValidate(page, 'text-literal.json', JSON.stringify({
    name: 'Text literal', definitionJson: definition, description: null, executionTimeoutSeconds: null,
    exportedAtUtc: '2026-10-12T00:00:00Z', inputParametersJson: null, maxConcurrentRuns: null, maxQueuedRuns: null,
    mode: null, outputParametersJson: null, overflowBehavior: null, retryPolicyJson: null, schemaVersion: '1',
  }))

  await refuseOnOpen(page, response, 'literal-port-value-invalid')
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="literal-port-value-invalid"]').first()).toBeVisible()
})

test('an expression in a Boolean configuration field is published, not graded as text', async ({ page, harness }) => {
  const name = `Typed config expression ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await ported.createWorkflow(page, name)
  const ifElse = await ported.dropBlock(page, 'IfElse', 'Control Flow')
  await ported.setExpression(page, 'Condition', 'true')
  await ported.drawWire(page, ported.startAnchor(), ifElse)

  expect(await ported.publish(page)).toBeGreaterThan(0)
  const stored = await ported.storedDefinition(harness.api, await ported.workflowIdOf(harness.api, name))
  expect(String(ported.storedConfig(ported.onlyNode(stored, '', 'IfElse'))['Condition'])).toMatch(/^expr:/)
})

test('a read of a typed host value is published as written', async ({ page, harness }) => {
  const name = `Host value read ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await ported.createWorkflow(page, name)
  const ifElse = await ported.dropBlock(page, 'IfElse', 'Control Flow')
  await ported.setExpression(page, 'Condition', '${e2e.known}')
  await ported.drawWire(page, ported.startAnchor(), ifElse)

  expect(await ported.publish(page)).toBeGreaterThan(0)
  const stored = await ported.storedDefinition(harness.api, await ported.workflowIdOf(harness.api, name))
  expect(String(ported.storedConfig(ported.onlyNode(stored, '', 'IfElse'))['Condition'])).toBe('expr:${e2e.known}')
})
