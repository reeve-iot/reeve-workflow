import type { Page } from '@playwright/test'
import { expect, test } from './support/fixtures'
import { readStoredDefinitionText } from '@reeve-iot/workflow-core'
import { publishWorkflowRequest } from './support/canvas'
import { saveNodes } from './support/nodes'
import { closeMapping, createWorkflow, drawWire, dropBlock, latestVersionForName } from './support/rules'

const ANALYSIS_PATH = '/api/workflows/analysis'

async function nodeId(page: Page, blockTypeId: string): Promise<string> {
  const id = await page.getByTestId(`workflow-node-${blockTypeId}`).first().getAttribute('data-node-id')
  if (id === null) throw new Error(`The '${blockTypeId}' node has no id.`)
  return id
}

async function mapResult(page: Page, port: string): Promise<void> {
  await page.getByRole('combobox', { name: `${port} mapping type` }).click()
  await page.getByRole('option', { name: 'Direct', exact: true }).click()
  await page.getByRole('combobox', { name: `${port} source` }).click()
  await page.getByRole('option', { name: /Result/ }).click()
}

const inputLabel = (page: Page, id: string, port: string) => page.getByTestId(`workflow-port-label-${id}-input-${port}`)

test('a parameter-typed port shows T (unbound), then the effective term once a wire gives the server evidence', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Draft analysis wire')
  await dropBlock(page, 'StringToInt', 'Conversion', { x: 0.3, y: 0.4 })
  await dropBlock(page, 'Math', 'Math', { x: 0.7, y: 0.4 })
  const sourceId = await nodeId(page, 'StringToInt')
  const mathId = await nodeId(page, 'Math')

  await expect(inputLabel(page, mathId, 'A')).toContainText('T (unbound)')

  const analysed = page.waitForResponse((response) => response.url().endsWith(ANALYSIS_PATH) && response.status() === 200
    && response.request().postData()?.includes('"definitionJson"') === true && response.request().postData()?.includes(sourceId) === true
    && response.request().postData()?.includes('sourceOutputName') === true)
  await drawWire(page, page.getByTestId(`workflow-port-${sourceId}-output-Result`), page.getByTestId(`workflow-port-${mathId}-input-A`))
  await mapResult(page, 'A')
  await closeMapping(page, 'Save Mappings')
  await analysed

  await expect(inputLabel(page, mathId, 'A')).toHaveText(/\(Int\)/)
  await expect(inputLabel(page, mathId, 'A')).toHaveAttribute('data-term-knowledge', 'bound')
  await expect(inputLabel(page, mathId, 'A')).toHaveAttribute('data-analysis', 'current')
})

test('deleting the block that gave the evidence returns the port to T (unbound)', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Draft analysis unwire')
  await dropBlock(page, 'StringToInt', 'Conversion', { x: 0.3, y: 0.4 })
  await dropBlock(page, 'Math', 'Math', { x: 0.7, y: 0.4 })
  const sourceId = await nodeId(page, 'StringToInt')
  const mathId = await nodeId(page, 'Math')
  await drawWire(page, page.getByTestId(`workflow-port-${sourceId}-output-Result`), page.getByTestId(`workflow-port-${mathId}-input-A`))
  await mapResult(page, 'A')
  await closeMapping(page, 'Save Mappings')
  await expect(inputLabel(page, mathId, 'A')).toHaveText(/\(Int\)/)

  await page.getByTestId('workflow-node-StringToInt').click({ position: { x: 20, y: 10 } })
  await page.keyboard.press('Delete')
  await expect(page.getByTestId('workflow-node-StringToInt')).toHaveCount(0)
  await expect(inputLabel(page, mathId, 'A')).toContainText('T (unbound)')
})

test('a literal on a parameter-typed port is written as text while the parameter is unbound', async ({ page, harness }) => {
  const name = await createWorkflow(page, harness.pageUrl, 'Draft analysis text literal')
  await dropBlock(page, 'Math', 'Math', { x: 0.7, y: 0.4 })
  const mathId = await nodeId(page, 'Math')
  await page.getByTestId('workflow-node-Math').click({ position: { x: 20, y: 10 } })
  await page.getByTestId('port-value-input-B').fill('7')
  const nodes = await saveNodes(page, name, harness)
  expect(nodes.find((node) => node.id === mathId)?.configuration['B']).toBe('7')
})

test('a literal on a parameter-typed port is written as a number once a wire makes the parameter Int, and bad text is not written', async ({ page, harness }) => {
  const name = await createWorkflow(page, harness.pageUrl, 'Draft analysis number literal')
  await dropBlock(page, 'Math', 'Math', { x: 0.7, y: 0.4 })
  await dropBlock(page, 'StringToInt', 'Conversion', { x: 0.3, y: 0.4 })
  const mathId = await nodeId(page, 'Math')
  const sourceId = await nodeId(page, 'StringToInt')
  await drawWire(page, page.getByTestId(`workflow-port-${sourceId}-output-Result`), page.getByTestId(`workflow-port-${mathId}-input-A`))
  await mapResult(page, 'A')
  await closeMapping(page, 'Save Mappings')
  await expect(inputLabel(page, mathId, 'A')).toHaveText(/\(Int\)/)

  await page.getByTestId('workflow-node-Math').click({ position: { x: 20, y: 10 } })
  await page.getByTestId('port-value-input-B').fill('5.5')
  await expect(page.getByTestId('port-value-error-B')).toHaveAttribute('data-rule-id', 'literal-port-value-invalid')
  await page.getByTestId('port-value-input-B').fill('')
  await page.getByTestId('port-value-input-B').pressSequentially('12')
  await expect(page.getByTestId('port-value-error-B')).toHaveCount(0)
  const nodes = await saveNodes(page, name, harness)
  expect(nodes.find((node) => node.id === mathId)?.configuration['B']).toBe(12)
})

async function pickTypeArgument(page: Page, parameter: string, term: string): Promise<void> {
  await page.getByRole('combobox', { name: `Type argument ${parameter}` }).click()
  await page.getByRole('option', { name: term, exact: true }).click()
}

test('a Numeric parameter offers only numeric terms, and Clear returns the port to T (unbound) @row:node.typeArguments#absent', async ({ page, harness }) => {
  const name = await createWorkflow(page, harness.pageUrl, 'Type argument clear')
  await dropBlock(page, 'Math', 'Math', { x: 0.7, y: 0.4 })
  const mathId = await nodeId(page, 'Math')
  await page.getByTestId('workflow-node-Math').click({ position: { x: 20, y: 10 } })

  await page.getByRole('combobox', { name: 'Type argument T' }).click()
  await expect(page.getByRole('option')).toHaveCount(3)
  for (const term of ['Inferred', 'Number', 'Int']) await expect(page.getByRole('option', { name: term, exact: true })).toBeVisible()
  await page.getByRole('option', { name: 'Int', exact: true }).click()
  await expect(inputLabel(page, mathId, 'A')).toHaveText(/\(Int\)/)
  const picked = await saveNodes(page, name, harness, 'Math')
  expect(picked.find((node) => node.id === mathId)?.typeArguments).toEqual({ T: 'Int' })

  await page.getByRole('button', { name: 'Clear type argument T' }).click()
  await expect(inputLabel(page, mathId, 'A')).toContainText('T (unbound)')
  const cleared = await saveNodes(page, name, harness)
  expect(cleared.find((node) => node.id === mathId)).not.toHaveProperty('typeArguments')
})

test('with T picked as Int, literal operands typed key by key publish with no finding and are stored as numbers @row:node.typeArguments#object', async ({ page, harness }) => {
  const name = await createWorkflow(page, harness.pageUrl, 'Type argument literals')
  await dropBlock(page, 'Math', 'Math', { x: 0.7, y: 0.4 })
  const mathId = await nodeId(page, 'Math')
  await page.getByTestId('workflow-node-Math').click({ position: { x: 20, y: 10 } })
  await pickTypeArgument(page, 'T', 'Int')
  await expect(inputLabel(page, mathId, 'A')).toHaveText(/\(Int\)/)

  await drawWire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'), page.getByTestId(`workflow-port-${mathId}-input-A`))
  for (const [port, text] of [['A', '12'], ['B', '34']] as const) {
    await page.getByRole('combobox', { name: `${port} mapping type` }).click()
    await page.getByRole('option', { name: 'Literal', exact: true }).click()
    await page.getByLabel(`${port} literal`, { exact: true }).pressSequentially(text)
  }
  await closeMapping(page, 'Save Mappings')

  const response = await publishWorkflowRequest(page)
  expect(response.ok()).toBe(true)
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code]')).toHaveCount(0)
  const stored = readStoredDefinitionText((await latestVersionForName(harness, name)).definitionJson)
  if (!stored.ok) throw new Error(`The stored document cannot be read: ${JSON.stringify(stored.fault)}`)
  expect(stored.definition.nodes.find((node) => node.id === mathId)?.typeArguments).toEqual({ T: 'Int' })
  expect(stored.definition.edges.find((edge) => edge.targetNodeId === mathId)?.mappings).toMatchObject({
    A: { type: 'literal', literalValue: 12 }, B: { type: 'literal', literalValue: 34 },
  })
})
