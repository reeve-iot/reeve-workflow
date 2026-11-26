import { expect, test } from './support/fixtures'
import { openFileRequest } from './support/canvas'
import { clearNodeSelection, createWorkflow, dropBlock, DROP_POINT, saveNodes } from './support/nodes'

const BLOCK = 'WaitForDuration'
const JSON_BLOCK = 'dispatch.echo-json'
const ROWS = ['node.position#object', 'position.x#number', 'position.y#number', 'node.configuration#object', 'node.subCanvases#absent', 'node.passthrough#absent']

test(`a dropped node keeps its canvas position and configuration ${ROWS.map((row) => `@row:${row}`).join(' ')}`, async ({ page, harness }) => {
  const name = `Node position ${String(Date.now())}`
  await createWorkflow(page, name, harness)
  await dropBlock(page, BLOCK, 'Control Flow')
  const node = page.getByTestId(`workflow-node-${BLOCK}`)
  await expect(node).toBeVisible()

  const saved = (await saveNodes(page, name, harness)).find((entry) => entry.blockTypeId === BLOCK)
  if (saved === undefined) throw new Error(`The stored document has no '${BLOCK}' node.`)
  expect(Math.abs(saved.position.x - (DROP_POINT.x - 100))).toBeLessThanOrEqual(20)
  expect(Math.abs(saved.position.y - (DROP_POINT.y - 40))).toBeLessThanOrEqual(20)
  expect(saved.configuration).not.toBeNull()
  expect(typeof saved.configuration).toBe('object')
  expect(Array.isArray(saved.configuration)).toBe(false)
  expect(saved).not.toHaveProperty('subCanvases')
  expect(saved).not.toHaveProperty('passthrough')
})

test('configuration scalar fields and empty timeout round-trip @row:node.configuration#value-boolean @row:node.configuration#value-string @row:node.configuration#value-absent', async ({ page, harness }) => {
  const name = `Config scalars ${String(Date.now())}`
  await createWorkflow(page, name, harness)
  await dropBlock(page, 'IfElse', 'Control Flow')
  await page.getByTestId('port-value-input-Condition').click()
  await dropBlock(page, 'Expression', 'Control Flow')
  await page.getByTestId('mode-value-Expression').click()
  await page.getByTestId('port-value-input-Expression').fill('hello from editor')
  await dropBlock(page, 'WaitForDuration', 'Control Flow')
  await page.getByLabel('Timeout (seconds)').fill('12')
  await page.getByLabel('Timeout (seconds)').press('Tab')
  await page.getByLabel('Timeout (seconds)').fill('')
  await page.getByLabel('Timeout (seconds)').press('Tab')
  const nodes = await saveNodes(page, name, harness)
  expect(nodes.find((node) => node.blockTypeId === 'IfElse')?.configuration['Condition']).toBe(true)
  expect(nodes.find((node) => node.blockTypeId === 'Expression')?.configuration['Expression']).toBe('hello from editor')
  const wait = nodes.find((node) => node.blockTypeId === 'WaitForDuration')
  expect(wait?.configuration).not.toHaveProperty('timeoutSeconds')
})

test('declared ports accept names, labels, required and composed types @row:port-entry.name#string @row:port-entry.name#blank @row:port-entry.name#dotted @row:port-entry.type#atom @row:port-entry.type#collection @row:port-entry.type#nested-collection @row:port-entry.type#null @row:port-entry.type#absent @row:port-entry.isRequired#boolean @row:port-entry.displayName#string @row:port-entry.displayName#absent', async ({ page, harness }) => {
  const name = `Dynamic ports ${String(Date.now())}`
  await createWorkflow(page, name, harness)
  await dropBlock(page, JSON_BLOCK, 'Freight')
  await page.getByRole('button', { name: 'Add Port', exact: true }).first().click()
  const portName = page.getByLabel('Port 1 name')
  await portName.fill('payload')
  let nodes = await saveNodes(page, name, harness, JSON_BLOCK)
  let saved = nodes.find((node) => node.blockTypeId === JSON_BLOCK)
  let ports = JSON.parse(String(saved?.configuration['InputPorts'])) as Array<{ name: string; displayName: string; type: string; isRequired: boolean }>
  expect(ports[0]?.name).toBe('payload')

  await portName.fill('')
  await expect(page.getByText(/stored-document reader refuses a blank name/i)).toBeVisible()
  nodes = await saveNodes(page, name, harness, JSON_BLOCK)
  saved = nodes.find((node) => node.blockTypeId === JSON_BLOCK)
  ports = JSON.parse(String(saved?.configuration['InputPorts'])) as Array<{ name: string; displayName: string; type: string; isRequired: boolean }>
  expect(ports[0]?.name).toBe('payload')

  await portName.fill('bad.name')
  await expect(page.getByText('A name may not hold a dot: the expression reader splits ${…} paths on dots.')).toBeVisible()
  nodes = await saveNodes(page, name, harness, JSON_BLOCK)
  saved = nodes.find((node) => node.blockTypeId === JSON_BLOCK)
  ports = JSON.parse(String(saved?.configuration['InputPorts'])) as Array<{ name: string; displayName: string; type: string; isRequired: boolean }>
  expect(ports[0]?.name).toBe('payload')

  await portName.fill('payload')
  await page.getByLabel('Port 1 label').fill('')
  await page.getByLabel('Port 1 required').check()
  await page.getByLabel('Port 1 type', { exact: true }).click()
  const offeredPortTypes = await page.getByRole('option').allTextContents()
  expect(offeredPortTypes.map((type) => type.trim())).not.toContain('')
  expect(offeredPortTypes.map((type) => type.trim())).not.toContain('null')
  await page.getByRole('option', { name: 'String', exact: true }).click()
  await expect(page.locator('.MuiBackdrop-root')).toHaveCount(0)
  await page.getByLabel('Port 1 type wrapped in a collection').click()
  await page.getByLabel('Port 1 type wrapped in a collection').click()
  await page.getByRole('button', { name: 'Add Port', exact: true }).nth(1).click()
  await page.getByLabel('Port 1 name').nth(1).fill('result')
  await page.getByLabel('Port 1 label').nth(1).fill('Result label')
  await page.getByLabel('Port 1 type', { exact: true }).nth(1).click()
  await page.getByRole('option', { name: 'Int', exact: true }).click()
  await expect(page.locator('.MuiBackdrop-root')).toHaveCount(0)
  await page.getByLabel('Port 1 required').nth(1).check()
  nodes = await saveNodes(page, name, harness)
  saved = nodes.find((node) => node.blockTypeId === JSON_BLOCK)
  if (!saved) throw new Error('The dynamic-port block was not stored.')
  ports = JSON.parse(String(saved.configuration['InputPorts'])) as Array<{ name: string; displayName: string; type: string; isRequired: boolean }>
  expect(ports).toEqual([{ name: 'payload', type: 'Collection<Collection<String>>', isRequired: true }])
  const outputs = JSON.parse(String(saved.configuration['OutputPorts'])) as Array<{ name: string; displayName: string; type: string; isRequired: boolean }>
  expect(outputs).toEqual([{ name: 'result', displayName: 'Result label', type: 'Int', isRequired: true }])
})

test('opening and saving a dynamic port normalises absent isRequired @row:port-entry.isRequired#absent', async ({ page, harness }) => {
  const name = `Absent port required ${String(Date.now())}`
  await createWorkflow(page, name, harness)
  await openFileRequest(page, {
    name: 'absent-port-required.json',
    contents: JSON.stringify({
      schemaVersion: '1', exportedAtUtc: new Date().toISOString(), name,
      description: null,
      definitionJson: JSON.stringify({ nodes: [
        { id: '__workflow_input__', blockTypeId: 'WorkflowInput', position: { x: 50, y: 100 }, configuration: {} },
        {
          id: 'json', blockTypeId: JSON_BLOCK, position: { x: 300, y: 100 },
          configuration: { InputPorts: JSON.stringify([{ name: 'Value', displayName: 'Value', type: 'String' }]), OutputPorts: '[]' },
        },
      ], edges: [{ id: 'input-json', sourceNodeId: '__workflow_input__', targetNodeId: 'json', mappings: {} }] }),
      inputParametersJson: null, outputParametersJson: null, executionTimeoutSeconds: null,
      retryPolicyJson: null, checkpointPolicyJson: null, mode: null, maxConcurrentRuns: null,
      maxQueuedRuns: null, overflowBehavior: null,
    }),
  })
  await expect(page.getByRole('alert')).toContainText('Nothing was stored')
  await page.getByTestId(`workflow-node-${JSON_BLOCK}`).click()
  await page.getByLabel('Port 1 label').fill('Normalized Value')
  const nodes = await saveNodes(page, name, harness, JSON_BLOCK)
  const node = nodes.find((entry) => entry.blockTypeId === JSON_BLOCK)
  if (!node) throw new Error('The stored document has no JSON block.')
  expect(JSON.parse(String(node.configuration['InputPorts']))).toEqual([
    { name: 'Value', displayName: 'Normalized Value', type: 'String', isRequired: false },
  ])
})

test('a nested canvas can be entered, filled and saved @row:node.subCanvases#object', async ({ page, harness }) => {
  const name = `Nested canvases ${String(Date.now())}`
  await createWorkflow(page, name, harness)
  await dropBlock(page, 'IfElse', 'Control Flow')
  await clearNodeSelection(page)
  await page.locator('[data-subcanvas-entry$=":Then"]').click()
  await dropBlock(page, 'IfElse', 'Control Flow')
  await clearNodeSelection(page)
  await page.locator('[data-subcanvas-entry$=":Then"]').click()
  await dropBlock(page, 'Expression', 'Control Flow')
  await page.getByRole('button', { name: 'Main Workflow' }).click()
  const nodes = await saveNodes(page, name, harness)
  const outer = nodes.find((node) => node.blockTypeId === 'IfElse')
  expect(outer?.subCanvases?.['Then']?.nodes.some((node) => node.blockTypeId === 'IfElse')).toBe(true)
  const inner = outer?.subCanvases?.['Then']?.nodes.find((node) => node.blockTypeId === 'IfElse')
  expect(inner?.subCanvases?.['Then']?.nodes.some((node) => node.blockTypeId === 'Expression')).toBe(true)
})

test('a custom passthrough value can be added and unticked @row:node.passthrough#array @row:node.passthrough#absent @row:node.passthrough#dotted', async ({ page, harness }) => {
  const name = `Passthrough ${String(Date.now())}`
  await createWorkflow(page, name, harness)
  await dropBlock(page, 'WaitForDuration', 'Control Flow')
  const custom = page.getByLabel('Add variable name')
  await custom.fill('bad.name')
  await page.getByRole('button', { name: 'Add passthrough variable' }).click()
  await expect(page.getByText('A name may not hold a dot: the expression reader splits ${…} paths on dots.')).toBeVisible()
  await custom.fill('customKey')
  await page.getByRole('button', { name: 'Add passthrough variable' }).click()
  await page.getByTestId('port-value-input-customKey').fill('custom value')
  let nodes = await saveNodes(page, name, harness)
  let saved = nodes.find((node) => node.blockTypeId === 'WaitForDuration')
  expect(saved?.passthrough).toEqual(['customKey'])
  expect(saved?.configuration['customKey']).toBe('custom value')
  await page.getByTestId('workflow-node-WaitForDuration').click()
  await page.getByLabel('Forward customKey').click()
  nodes = await saveNodes(page, name, harness)
  saved = nodes.find((node) => node.blockTypeId === 'WaitForDuration')
  expect(saved).not.toHaveProperty('passthrough')
})
