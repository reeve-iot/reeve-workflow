import type { Locator, Page } from '@playwright/test'
import { expect, test } from './support/fixtures'
import {
  addWorkflowParameter, clearSelection, closeMapping, createWorkflow, drawWire, dropBlock, refuseOnPublish, refuseOnSave, removeWorkflowParameter,
} from './support/rules'

test('dropping an unconnected block reports its editor findings @row:extra-root @row:required-input-unfed', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule extra root')

  await dropBlock(page, 'WaitForDuration', 'Control Flow', { x: 0.44, y: 0.36 })
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="extra-root"]')).toBeVisible()
  const requiredInput = page.getByRole('list', { name: 'Findings' }).locator('[data-code="required-input-unfed"]')
  await expect(requiredInput).toBeVisible()
  await expect(requiredInput).toContainText('Host translation: connect this required input.')
  await clearSelection(page)
  await requiredInput.click()
  await expect(page.getByTestId('node-config-panel')).toBeVisible()
  await refuseOnPublish(page, 'extra-root')
  await refuseOnPublish(page, 'required-input-unfed')
})

test('a detached loop reports cycle and unreachable @row:cycle @row:unreachable', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule loop')
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.3, y: 0.3 })
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.64, y: 0.65 })

  const nodes = page.getByTestId('workflow-node-Expression')
  await expect(nodes).toHaveCount(2)
  const first = await nodes.nth(0).getAttribute('data-node-id')
  const second = await nodes.nth(1).getAttribute('data-node-id')
  if (first === null || second === null) throw new Error('The dropped expression node has no id.')
  await drawWire(page, page.getByTestId(`workflow-port-${first}-output-Result`), page.getByTestId(`workflow-port-${second}-input-Expression`))
  await closeMapping(page, 'Cancel')
  await drawWire(page, page.getByTestId(`workflow-port-${second}-output-Result`), page.getByTestId(`workflow-port-${first}-input-Expression`))
  await closeMapping(page, 'Cancel')
  const findings = page.getByRole('list', { name: 'Findings' })
  await expect(findings.locator('[data-code="cycle"]')).toBeVisible()
  await expect(findings.locator(`[data-code="unreachable"][data-node-id="${first}"]`)).toHaveCount(1)
  await expect(findings.locator(`[data-code="unreachable"][data-node-id="${second}"]`)).toHaveCount(1)
  await refuseOnPublish(page, 'cycle')
  await refuseOnPublish(page, 'unreachable')
})

test('an incoming connection to Workflow Input is reported @row:input-has-incoming', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule incoming entry')
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.4, y: 0.35 })
  const nodeId = await page.getByTestId('workflow-node-Expression').getAttribute('data-node-id')
  if (nodeId === null) throw new Error('The dropped expression node has no id.')
  await drawWire(page, page.getByTestId(`workflow-port-${nodeId}-output-Result`), page.getByTestId('workflow-node-body-__workflow_input__'), { expectMappingDialog: false })
  await expect(page.getByTestId(`workflow-edge-${nodeId}-__workflow_input__`)).toBeVisible()
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="input-has-incoming"]')).toBeVisible()
  await refuseOnPublish(page, 'input-has-incoming')
})

test('an unselected sub-workflow reports unresolved ports @row:node-ports-unresolved', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule unresolved sub-workflow')
  await dropBlock(page, 'SubWorkflow', 'Workflow', { x: 0.4, y: 0.35 })
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="node-ports-unresolved"]')).toBeVisible()
  await refuseOnSave(page, /must have a workflow selected/)
})

test('an invalid expression reports syntax @row:expression-syntax', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule expression syntax')
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.4, y: 0.35 })
  await page.getByTestId('host-expression-editor-Expression').fill('1 +')
  await refuseOnPublish(page, 'expression-syntax')
})

test('declaring a static output name reports the collision @row:declared-output-shadows-static', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule declared output collision')
  await dropBlock(page, 'TryCatch', 'Control Flow', { x: 0.4, y: 0.35 })
  await page.getByRole('button', { name: 'Add Output Port' }).click()
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill('CaughtError')
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="declared-output-shadows-static"]')).toBeVisible()
  await refuseOnPublish(page, 'declared-output-shadows-static')
})

test('two mapped edges into one input report the collision @row:duplicate-input-mapping', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule duplicate input mapping')
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.24, y: 0.3 })
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.49, y: 0.62 })
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.73, y: 0.3 })
  const nodes = page.getByTestId('workflow-node-Expression')
  await expect(nodes).toHaveCount(3)
  const first = await nodes.nth(0).getAttribute('data-node-id')
  const second = await nodes.nth(1).getAttribute('data-node-id')
  const third = await nodes.nth(2).getAttribute('data-node-id')
  if (first === null || second === null || third === null) throw new Error('A dropped expression node has no id.')

  await drawWire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'), page.getByTestId(`workflow-port-${first}-input-Expression`))
  await closeMapping(page, 'Cancel')
  await mapOutput(page, page.getByTestId(`workflow-port-${first}-output-Result`), page.getByTestId(`workflow-port-${second}-input-Expression`))
  await mapOutput(page, page.getByTestId(`workflow-port-${second}-output-Result`), page.getByTestId(`workflow-port-${third}-input-Expression`))
  await mapOutput(page, page.getByTestId(`workflow-port-${first}-output-Result`), page.getByTestId(`workflow-port-${third}-input-Expression`))

  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="duplicate-input-mapping"]')).toBeVisible()
  await refuseOnPublish(page, 'duplicate-input-mapping')
})

test('a boolean output mapped to an integer input is refused @row:port-type-mismatch', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule mismatched port types')
  await dropBlock(page, 'StringToBool', 'Conversion', { x: 0.35, y: 0.34 })
  await dropBlock(page, 'WaitForDuration', 'Control Flow', { x: 0.65, y: 0.62 })
  const sourceId = await page.getByTestId('workflow-node-StringToBool').getAttribute('data-node-id')
  const targetId = await page.getByTestId('workflow-node-WaitForDuration').getAttribute('data-node-id')
  if (sourceId === null || targetId === null) throw new Error('A dropped block has no id.')
  await drawWire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'), page.getByTestId(`workflow-port-${sourceId}-input-Value`))
  await closeMapping(page, 'Cancel')
  await mapOutput(page, page.getByTestId(`workflow-port-${sourceId}-output-Result`), page.getByTestId(`workflow-port-${targetId}-input-DurationMs`), 'Duration (ms)', 'Result (Boolean)')
  await refuseOnPublish(page, 'port-type-mismatch')
})

test('an undeclared parameter reference is refused @row:expression-out-of-scope', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule expression out of scope')
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.4, y: 0.35 })
  await page.getByTestId('host-expression-editor-Expression').fill('${parameter.DoesNotExist}')
  await refuseOnPublish(page, 'expression-out-of-scope')
})

test('an unknown host expression namespace is refused @row:expression-unknown-namespace', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule unknown namespace')
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.4, y: 0.35 })
  await page.getByTestId('host-expression-editor-Expression').fill('${unregistered.nope}')
  await refuseOnPublish(page, 'expression-unknown-namespace')
})

test('a member read out of a scalar is refused @row:expression-field-access-invalid', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule field of a scalar')
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.4, y: 0.35 })
  await page.getByTestId('host-expression-editor-Expression').fill('${now.year.name}')
  await refuseOnPublish(page, 'expression-field-access-invalid')
})

test('a required configuration field is reported when left empty @row:required-config-missing', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule required config')
  await dropBlock(page, 'dispatch.require-label', 'Freight', { x: 0.4, y: 0.35 })
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="required-config-missing"]')).toBeVisible()
  await refuseOnPublish(page, 'required-config-missing')
})

test('forwarding a workflow reference into a SubWorkflow is refused @row:reference-input-mapped', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule mapped workflow reference')
  await addWorkflowParameter(page, 'input', 'WorkflowId', 'Workflow ID')
  await dropBlock(page, 'SubWorkflow', 'Workflow', { x: 0.4, y: 0.35 })
  const subWorkflow = page.getByTestId('workflow-node-SubWorkflow')
  const nodeId = await subWorkflow.getAttribute('data-node-id')
  if (nodeId === null) throw new Error('The SubWorkflow node has no id.')

  await drawWire(page, page.getByTestId('workflow-port-__workflow_input__-output-WorkflowId'), page.getByTestId(`workflow-node-body-${nodeId}`))
  await closeMapping(page, 'Cancel')
  await subWorkflow.click({ position: { x: 20, y: 10 } })
  await page.getByRole('checkbox', { name: 'Forward WorkflowId' }).check()
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="reference-input-mapped"]')).toBeVisible()
  await refuseOnSave(page, /wiring an edge into the WorkflowId input is not allowed/)
})

test('removing a mapped workflow output reports the unknown wire port @row:wire-port-unknown', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule removed output mapping')
  await addWorkflowParameter(page, 'output', 'FirstOutput', 'First Output')
  await expect(page.getByTestId('workflow-node-WorkflowOutput')).toBeVisible()
  await addWorkflowParameter(page, 'output', 'SecondOutput', 'Second Output', 1)
  await dropBlock(page, 'Expression', 'Control Flow', { x: 0.4, y: 0.35 })
  const expression = page.getByTestId('workflow-node-Expression')
  const nodeId = await expression.getAttribute('data-node-id')
  if (nodeId === null) throw new Error('The Expression node has no id.')
  await mapOutput(page, page.getByTestId(`workflow-port-${nodeId}-output-Result`), page.getByTestId('workflow-port-__workflow_output__-input-FirstOutput'), 'First Output')

  await removeWorkflowParameter(page, 'output', 0)
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="wire-port-unknown.input"]')).toBeVisible()
  await refuseOnPublish(page, 'wire-port-unknown')
})

test('a dynamic input typed Json in the port table is refused at Publish @row:port-type-json-position', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule Json input port')
  await dropBlock(page, 'dispatch.echo-json', 'Freight', { x: 0.4, y: 0.35 })
  await page.getByRole('button', { name: 'Add Port', exact: true }).first().click()
  await page.getByLabel('Port 1 name').fill('payload')
  await page.getByLabel('Port 1 type', { exact: true }).click()
  await page.getByRole('option', { name: 'Json', exact: true }).click()
  await expect(page.locator('.MuiBackdrop-root')).toHaveCount(0)
  await refuseOnPublish(page, 'port-type-json-position')
})

async function mapOutput(page: Page, source: Locator, target: Locator, inputLabel = 'Expression', outputLabel = 'Result ($T)'): Promise<void> {
  await drawWire(page, source, target)
  await page.getByRole('combobox', { name: `${inputLabel} source` }).click()
  await page.getByRole('option', { name: outputLabel }).click()
  await expect(page.locator('.MuiBackdrop-root')).toHaveCount(1)
  await closeMapping(page, 'Save Mappings')
}

test('forwarding a secret-capable input by passthrough is refused on publish @row:secret-input-passthrough', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Rule secret passthrough')
  await dropBlock(page, 'HttpRequest', 'Network', { x: 0.4, y: 0.35 })
  await page.getByLabel('Add variable name').fill('Headers')
  await page.getByRole('button', { name: 'Add passthrough variable' }).click()
  await refuseOnPublish(page, 'secret-input-passthrough')
})
