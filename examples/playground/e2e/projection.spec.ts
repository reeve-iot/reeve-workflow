import type { Page } from '@playwright/test'
import { expect, test } from './support/fixtures'
import { readStoredVersion } from './support/outputs-mappings'
import { saveWorkflowRequest } from './support/canvas'
import { saveNodes } from './support/nodes'
import { closeMapping, createWorkflow, drawWire, dropBlock, refuseOnPublish } from './support/rules'

const START = 'workflow-port-__workflow_input__-output-__start__'

interface Authored {
  readonly path: string
  readonly jsonText?: boolean
  readonly type?: string
  readonly clear?: boolean
}

const DROP_AT = { x: 0.45, y: 0.2 } as const

const AUTHORED: readonly { readonly at: { x: number; y: number }; readonly a: Authored; readonly b: Authored }[] = [
  { at: DROP_AT, a: { path: '$.a', jsonText: true, type: 'Int' }, b: { path: '$.b', type: 'Collection<Int>' } },
  { at: { x: 0.45, y: 0.6 }, a: { path: '$.c', type: 'Collection<Collection<Int>>' }, b: { path: '$.d', type: 'int' } },
  { at: { x: 0.72, y: 0.2 }, a: { path: '$.e', type: 'Widget' }, b: { path: '$.f', type: 'Collection<Any>' } },
  { at: { x: 0.72, y: 0.6 }, a: { path: '$.g' }, b: { path: '$.h', type: 'Int', clear: true } },
]

const ROWS = [
  'mapping.projection#object', 'mapping.projection#absent', 'projection.path#string', 'projection.mode#jsonText',
  'projection.mode#absent', 'projection.type#atom', 'projection.type#collection', 'projection.type#nested-collection',
  'projection.type#alias', 'projection.type#unregistered', 'projection.type#collection-of-any', 'projection.type#absent',
]

test(`the host projection control stores a projection on a wire from a Json output ${ROWS.map((row) => `@row:${row}`).join(' ')}`, async ({ page, harness }) => {
  const name = await createWorkflow(page, harness.pageUrl, 'Projection wires')
  const sourceId = await jsonSource(page)
  const mathIds: string[] = []
  for (const { at, a, b } of AUTHORED) {
    await dropBlock(page, 'Math', 'Math', at)
    const mathId = await nodeId(page, 'Math', mathIds.length)
    mathIds.push(mathId)
    await drawWire(page, page.getByTestId(`workflow-port-${sourceId}-output-Value`), page.getByTestId(`workflow-port-${mathId}-input-A`))
    await project(page, 'A', a)
    await project(page, 'B', b)
    await closeMapping(page, 'Save Mappings')
  }

  await saveWorkflowRequest(page)
  const stored = await readStoredVersion(harness, name)
  const mappingsInto = (mathId: string) => stored.edges.find((edge) => edge.sourceNodeId === sourceId && edge.targetNodeId === mathId)?.mappings
  const direct = { type: 'direct', sourceOutputName: 'Value' }
  expect(mappingsInto(mathIds[0] ?? '')).toEqual({
    A: { ...direct, projection: { path: '$.a', mode: 'jsonText', type: 'Int' } },
    B: { ...direct, projection: { path: '$.b', type: 'Collection<Int>' } },
  })
  expect(mappingsInto(mathIds[1] ?? '')).toEqual({
    A: { ...direct, projection: { path: '$.c', type: 'Collection<Collection<Int>>' } },
    B: { ...direct, projection: { path: '$.d', type: 'int' } },
  })
  expect(mappingsInto(mathIds[2] ?? '')).toEqual({
    A: { ...direct, projection: { path: '$.e', type: 'Widget' } },
    B: { ...direct, projection: { path: '$.f', type: 'Collection<Any>' } },
  })
  expect(mappingsInto(mathIds[3] ?? '')).toEqual({ A: { ...direct, projection: { path: '$.g' } }, B: direct })
})

test('the mapping dialog offers _error as a Json source and stores a projection on it', async ({ page, harness }) => {
  const name = await createWorkflow(page, harness.pageUrl, 'Projection error wire')
  await dropBlock(page, 'StringToInt', 'Conversion', { x: 0.3, y: 0.4 })
  const sourceId = await nodeId(page, 'StringToInt')
  await page.getByTestId('workflow-node-StringToInt').click({ position: { x: 20, y: 10 } })
  await page.getByText('Error Handling', { exact: true }).click()
  await page.getByRole('combobox', { name: 'Error Policy' }).click()
  await page.getByRole('option', { name: 'Emit Error to Virtual Port' }).click()
  await expect(page.locator('.MuiBackdrop-root')).toHaveCount(0)
  await dropBlock(page, 'Math', 'Math', { x: 0.7, y: 0.4 })
  const mathId = await nodeId(page, 'Math')
  await drawWire(page, page.getByTestId(`workflow-port-${sourceId}-output-_error`), page.getByTestId(`workflow-port-${mathId}-input-A`))

  await page.getByRole('combobox', { name: 'A mapping type' }).click()
  await page.getByRole('option', { name: 'Direct', exact: true }).click()
  await page.getByRole('combobox', { name: 'A source' }).click()
  await page.getByRole('option', { name: 'Error (Json)', exact: true }).click()
  await page.getByLabel('A projection path', { exact: true }).fill('$.code')
  await closeMapping(page, 'Save Mappings')

  await saveWorkflowRequest(page)
  const stored = await readStoredVersion(harness, name)
  expect(stored.edges.find((edge) => edge.sourceNodeId === sourceId && edge.targetNodeId === mathId)?.mappings)
    .toEqual({ A: { type: 'direct', sourceOutputName: '_error', projection: { path: '$.code' } } })
})

test('a projection type on an input whose term names no parameter is refused at Publish @row:json-projection-invalid', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Projection refused')
  const sourceId = await jsonSource(page)
  await dropBlock(page, 'WaitForDuration', 'Control Flow', DROP_AT)
  const waitId = await nodeId(page, 'WaitForDuration')
  await drawWire(page, page.getByTestId(`workflow-port-${sourceId}-output-Value`), page.getByTestId(`workflow-port-${waitId}-input-DurationMs`))
  await project(page, 'DurationMs', { path: '$.n', type: 'Int' }, 'Duration (ms)')
  await closeMapping(page, 'Save Mappings')
  await refuseOnPublish(page, 'json-projection-invalid')
})

test('the host projection control writes a jsonPath read into a configuration expression', async ({ page, harness }) => {
  const name = await createWorkflow(page, harness.pageUrl, 'Projection expression')
  const sourceId = await jsonSource(page)
  await dropBlock(page, 'Expression', 'Control Flow', DROP_AT)
  const expressionId = await nodeId(page, 'Expression')
  await drawWire(page, page.getByTestId(`workflow-port-${sourceId}-output-Value`), page.getByTestId(`workflow-port-${expressionId}-input-Expression`))
  await closeMapping(page, 'Cancel')

  await page.getByTestId('workflow-node-Expression').click()
  await page.getByLabel('Expression expression projection path', { exact: true }).fill('$.count')
  await page.getByLabel('Expression expression projection type', { exact: true }).fill('Int')
  await page.getByRole('button', { name: 'Insert Expression projection' }).click()
  const written = `jsonPath(\${previous.${sourceId}.Value}, "$.count", "Int")`
  await expect(page.getByTestId('host-expression-editor-Expression')).toHaveValue(written)

  const nodes = await saveNodes(page, name, harness)
  expect(nodes.find((node) => node.id === expressionId)?.configuration['Expression']).toBe(`expr:${written}`)
})

test('the host projection control writes jsonPathText with a type and an escaped path', async ({ page, harness }) => {
  const name = await createWorkflow(page, harness.pageUrl, 'Projection expression text')
  const sourceId = await jsonSource(page)
  await dropBlock(page, 'Expression', 'Control Flow', DROP_AT)
  const expressionId = await nodeId(page, 'Expression')
  await drawWire(page, page.getByTestId(`workflow-port-${sourceId}-output-Value`), page.getByTestId(`workflow-port-${expressionId}-input-Expression`))
  await closeMapping(page, 'Cancel')

  await page.getByTestId('workflow-node-Expression').click()
  await page.getByLabel('Expression expression projection path', { exact: true }).fill('$["name"]')
  await page.getByLabel('Expression expression projection jsonText', { exact: true }).check()
  await page.getByRole('button', { name: 'Insert Expression projection' }).click()
  const written = `jsonPathText(\${previous.${sourceId}.Value}, "$[\\"name\\"]", "String")`
  await expect(page.getByTestId('host-expression-editor-Expression')).toHaveValue(written)

  const nodes = await saveNodes(page, name, harness)
  expect(nodes.find((node) => node.id === expressionId)?.configuration['Expression']).toBe(`expr:${written}`)
})

async function jsonSource(page: Page): Promise<string> {
  await dropBlock(page, 'TextToJson', 'Conversion', { x: 0.2, y: 0.4 })
  const sourceId = await nodeId(page, 'TextToJson')
  await drawWire(page, page.getByTestId(START), page.getByTestId(`workflow-port-${sourceId}-input-Text`))
  await closeMapping(page, 'Cancel')
  return sourceId
}

async function project(page: Page, input: string, authored: Authored, displayName = input): Promise<void> {
  await page.getByRole('combobox', { name: `${displayName} mapping type` }).click()
  await page.getByRole('option', { name: 'Direct', exact: true }).click()
  await page.getByRole('combobox', { name: `${displayName} source` }).click()
  await page.getByRole('option', { name: 'Value (Json?)', exact: true }).click()
  await page.getByLabel(`${input} projection path`, { exact: true }).fill(authored.path)
  if (authored.jsonText === true) await page.getByLabel(`${input} projection jsonText`, { exact: true }).check()
  if (authored.type !== undefined) await page.getByLabel(`${input} projection type`, { exact: true }).fill(authored.type)
  if (authored.clear === true) await page.getByRole('button', { name: `Clear ${input} projection` }).click()
}

async function nodeId(page: Page, blockTypeId: string, index = 0): Promise<string> {
  const id = await page.getByTestId(`workflow-node-${blockTypeId}`).nth(index).getAttribute('data-node-id')
  if (id === null) throw new Error(`The '${blockTypeId}' node has no id.`)
  return id
}
