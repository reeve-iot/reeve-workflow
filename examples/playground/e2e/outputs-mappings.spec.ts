import type { Page } from '@playwright/test'
import { expect, test } from './support/fixtures'
import type { Harness } from './support/fixtures'
import { readStoredVersion } from './support/outputs-mappings'
import { openWorkflowSettings } from './support/settings'
import { wire } from './support/outputs-mappings'
import {
  createWorkflowRequest, dropPaletteBlock, DROP_POINT, openPlayground, publishWorkflowRequest, runWorkflowRequest, saveWorkflowRequest,
} from './support/canvas'

const outputRows = [
  'node.outputPorts#array', 'declared-output.name#string', 'declared-output.displayName#string',
  'declared-output.type#atom', 'declared-output.sources#object', 'node.errorPolicy#object', 'error-policy.policy#Skip',
  'declared-output.type#collection', 'declared-output.type#nested-collection',
  'declared-output.type#null', 'declared-output.type#absent',
  'declared-output.name#blank', 'declared-output.name#dotted',
]

test(`declared outputs are stored and invalid names are refused ${outputRows.map((row) => `@row:${row}`).join(' ')}`, async ({ page, harness }) => {
  const name = await create(page, harness, 'Outputs')
  await dropBlock(page, 'TryCatch', 'Control Flow')
  await page.getByRole('button', { name: 'Add Output Port' }).click()
  const outputName = page.getByLabel('Name', { exact: true })
  await outputName.fill('Result')
  await outputName.fill('')
  await expect(page.getByText('The stored-document reader accepts a blank name, but this field writes none.')).toBeVisible()
  await outputName.fill('Result.part')
  await expect(page.getByText('A name may not hold a dot: the expression reader splits ${…} paths on dots.')).toBeVisible()
  await page.getByLabel('Label', { exact: true }).nth(0).fill('Final result')
  await page.getByRole('combobox', { name: 'Output 1 type' }).click()
  const offeredOutputTypes = await page.getByRole('option').allTextContents()
  expect(offeredOutputTypes.map((type) => type.trim())).not.toContain('')
  expect(offeredOutputTypes.map((type) => type.trim())).not.toContain('null')
  await page.getByRole('option', { name: 'String', exact: true }).click()
  const collection = page.getByRole('button', { name: 'Output 1 type wrapped in a collection' })
  await collection.click()
  await collection.click()
  await page.getByLabel('Try →', { exact: true }).nth(0).fill('TryResult')
  await page.getByLabel('Catch →', { exact: true }).nth(0).fill('CatchResult')
  await page.getByLabel('Finally →', { exact: true }).nth(0).fill('FinallyResult')
  await page.getByRole('button', { name: 'Add Output Port' }).click()
  await page.getByLabel('Name', { exact: true }).nth(1).fill('AtomResult')
  await page.getByLabel('Label', { exact: true }).nth(1).fill('Atom result')
  await page.getByRole('combobox', { name: 'Output 2 type' }).click()
  await page.getByRole('option', { name: 'Number', exact: true }).click()
  await page.getByLabel('Try →', { exact: true }).nth(1).fill('TryAtom')
  await page.getByLabel('Catch →', { exact: true }).nth(1).fill('CatchAtom')
  await page.getByLabel('Finally →', { exact: true }).nth(1).fill('FinallyAtom')
  await page.getByRole('button', { name: 'Add Output Port' }).click()
  await page.getByLabel('Name', { exact: true }).nth(2).fill('CollectionResult')
  await page.getByLabel('Label', { exact: true }).nth(2).fill('Collection result')
  await page.getByRole('combobox', { name: 'Output 3 type' }).click()
  await page.getByRole('option', { name: 'Boolean', exact: true }).click()
  await page.getByRole('button', { name: 'Output 3 type wrapped in a collection' }).click()
  await page.getByLabel('Try →', { exact: true }).nth(2).fill('TryCollection')
  await page.getByLabel('Catch →', { exact: true }).nth(2).fill('CatchCollection')
  await page.getByLabel('Finally →', { exact: true }).nth(2).fill('FinallyCollection')
  await page.getByText('Error Handling').click()
  await page.getByRole('combobox', { name: 'Error Policy' }).click()
  await page.getByRole('option', { name: 'Skip Node & Downstream' }).click()
  const node = page.getByTestId('workflow-node-TryCatch')
  await wire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'), node, 20)
  await page.getByRole('button', { name: 'Cancel' }).click()
  const stored = await publishAndRead(page, harness, name)
  const storedNode = stored.nodes.find((candidate) => candidate.blockTypeId === 'TryCatch')
  expect(storedNode?.outputPorts).toEqual([
    {
      name: 'Result', displayName: 'Final result', type: 'Collection<Collection<String>>',
      sources: { Try: 'TryResult', Catch: 'CatchResult', Finally: 'FinallyResult' },
    },
    {
      name: 'AtomResult', displayName: 'Atom result', type: 'Number',
      sources: { Try: 'TryAtom', Catch: 'CatchAtom', Finally: 'FinallyAtom' },
    },
    {
      name: 'CollectionResult', displayName: 'Collection result', type: 'Collection<Boolean>',
      sources: { Try: 'TryCollection', Catch: 'CatchCollection', Finally: 'FinallyCollection' },
    },
  ])
  expect(storedNode?.errorPolicy).toEqual({ policy: 'Skip' })
})

const policyCases = [
  { policy: 'FailWorkflow', row: 'error-policy.policy#FailWorkflow', status: 'Failed' },
  { policy: 'ContinueWithDefault', row: 'error-policy.policy#ContinueWithDefault', status: 'Succeeded' },
  { policy: 'EmitError', row: 'error-policy.policy#EmitError', status: 'Succeeded' },
  { policy: 'Skip', row: 'error-policy.policy#Skip', status: 'Skipped' },
] as const
for (const item of policyCases) {
  const policyRows = [`error-policy.policy#${item.policy}`,
    ...(item.policy === 'ContinueWithDefault' ? ['error-policy.defaultOutputJson#string', 'error-policy.defaultOutputJson#not-an-object'] : []),
    'error-policy.routeToErrorPort#absent']
  test(`a failing StringToInt follows ${item.policy} ${policyRows.map((row) => `@row:${row}`).join(' ')}`, async ({ page, harness }) => {
    const name = await create(page, harness, `Policy ${item.policy}`)
    await dropBlock(page, 'StringToInt', 'Conversion')
    const node = page.getByTestId('workflow-node-StringToInt')
    await page.getByText('Error Handling').click()
    await page.getByRole('combobox', { name: 'Error Policy' }).click()
    await page.getByRole('option', { name: policyLabel(item.policy) }).click()
    if (item.policy === 'ContinueWithDefault') {
      const field = page.getByLabel('Default Output (JSON)')
      await field.fill('{"Result":23}')
      await expect(field).not.toHaveAttribute('aria-invalid', 'true')
      await field.fill('[23]')
      await expect(page.getByText('Must be a JSON object')).toBeVisible()
    }
    await wire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'),
      page.getByTestId(`workflow-port-${await node.getAttribute('data-node-id')}-input-Value`))
    await page.getByRole('combobox', { name: 'Value mapping type' }).click()
    await page.getByRole('option', { name: 'Literal', exact: true }).click()
    await page.getByLabel('Value literal', { exact: true }).fill('abc')
    await page.getByRole('button', { name: 'Save Mappings' }).click()
    await saveWorkflowRequest(page)
    await publishAndRead(page, harness, name)
    await runWorkflowRequest(page)
    await expect(page.getByTestId('status')).toContainText('Ran the workflow')
    const workflow = (await harness.api.getWorkflows()).find((candidate) => candidate.name === name)
    if (!workflow) throw new Error(`No workflow named '${name}'.`)
    const executions = await harness.api.getExecutions({ id: workflow.id, take: 1 })
    const latest = executions.items[0]
    if (!latest) throw new Error('Run did not create an execution record.')
    const detail = await harness.api.getExecution({ id: workflow.id, executionId: latest.id })
    const step = detail.steps.find((entry) => entry.nodeType === 'StringToInt')
    expect(step?.status).toBe(item.status)
    if (item.policy === 'ContinueWithDefault') expect(step?.outputsJson).toContain('23')
    if (item.policy === 'EmitError') expect(step?.outputsJson).toContain('_error')
    const saved = await readStoredVersion(harness, name)
    const savedNode = saved.nodes.find((entry) => entry.blockTypeId === 'StringToInt')
    expect(savedNode?.errorPolicy?.policy).toBe(item.policy)
    if (item.policy === 'ContinueWithDefault') expect(savedNode?.errorPolicy?.defaultOutputJson).toBe('{"Result":23}')
    expect(savedNode?.errorPolicy).not.toHaveProperty('routeToErrorPort')
  })
}

const mappingRows = [
  'connection.id#string', 'mapping.type#direct', 'mapping.type#literal', 'mapping.sourceOutputName#string',
  'mapping.sourceOutputName#absent',
  'mapping.literalValue#boolean', 'mapping.literalValue#number',
  'mapping.literalValue#string', 'mapping.literalValue#array',
  'mapping.literalValue#object', 'mapping.literalValue#absent', 'canvas.outputMappings#absent',
]
test(`typed literals and a direct mapping are stored on the exit wire ${mappingRows.map((row) => `@row:${row}`).join(' ')}`, async ({ page, harness }) => {
  const name = await create(page, harness, 'Mapping values')
  await addOutputParameter(page, 'direct', 'Number')
  await addOutputParameter(page, 'flag', 'Boolean')
  await addOutputParameter(page, 'count', 'Number')
  await addOutputParameter(page, 'text', 'String')
  await addOutputParameter(page, 'list', 'Number', 1)
  await addOutputParameter(page, 'object', 'Json')
  await dropBlock(page, 'StringToInt', 'Conversion')
  const math = page.getByTestId('workflow-node-StringToInt')
  await wire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'),
    page.getByTestId(`workflow-port-${await math.getAttribute('data-node-id')}-input-Value`))
  await page.getByRole('combobox', { name: 'Value mapping type' }).click()
  await page.getByRole('option', { name: 'Literal', exact: true }).click()
  await page.getByLabel('Value literal', { exact: true }).fill('2')
  await page.getByRole('button', { name: 'Save Mappings' }).click()
  await wire(page, page.getByTestId(`workflow-port-${await math.getAttribute('data-node-id')}-output-Result`),
    page.getByTestId('workflow-port-__workflow_output__-input-direct'))
  await page.getByRole('combobox', { name: 'direct mapping type' }).click()
  await page.getByRole('option', { name: 'Direct', exact: true }).click()
  await page.getByRole('combobox', { name: 'direct source' }).click()
  await page.getByRole('option', { name: /Result/ }).click()
  await page.getByRole('combobox', { name: 'flag mapping type' }).click()
  await page.getByRole('option', { name: 'Literal', exact: true }).click()
  await page.getByLabel('flag literal', { exact: true }).check()
  await page.getByRole('combobox', { name: 'count mapping type' }).click()
  await page.getByRole('option', { name: 'Literal', exact: true }).click()
  await page.getByLabel('count literal', { exact: true }).fill('7')
  await page.getByRole('combobox', { name: 'text mapping type' }).click()
  await page.getByRole('option', { name: 'Literal', exact: true }).click()
  await page.getByLabel('text literal', { exact: true }).fill('typed text')
  await page.getByRole('combobox', { name: 'list mapping type' }).click()
  await page.getByRole('option', { name: 'Literal', exact: true }).click()
  await page.getByLabel('list literal', { exact: true }).fill('[1, 2]')
  await page.getByRole('combobox', { name: 'object mapping type' }).click()
  await page.getByRole('option', { name: 'Literal', exact: true }).click()
  await page.getByLabel('object literal', { exact: true }).fill('{"answer":42}')
  await page.getByRole('button', { name: 'Save Mappings' }).click()
  await dropBlock(page, 'WaitForDuration', 'Control Flow', { x: 720, y: 320 })
  await page.getByTestId('port-value-input-DurationMs').fill('25')
  const wait = page.getByTestId('workflow-node-WaitForDuration')
  await wire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'),
    page.getByTestId(`workflow-port-${await wait.getAttribute('data-node-id')}-input-DurationMs`))
  await page.getByRole('button', { name: 'Cancel' }).click()
  const stored = await publishAndRead(page, harness, name)
  const mathNode = stored.nodes.find((candidate) => candidate.blockTypeId === 'StringToInt')
  const edge = stored.edges.find((candidate) => candidate.sourceNodeId === mathNode?.id && candidate.targetNodeId === '__workflow_output__')
  expect(typeof edge?.id).toBe('string')
  expect(edge?.mappings).toMatchObject({
    direct: { type: 'direct', sourceOutputName: 'Result' },
    flag: { type: 'literal', literalValue: true },
    count: { type: 'literal', literalValue: 7 },
    text: { type: 'literal', literalValue: 'typed text' },
    list: { type: 'literal', literalValue: [1, 2] },
    object: { type: 'literal', literalValue: { answer: 42 } },
  })
  for (const mapping of Object.values(edge?.mappings ?? {}).filter((entry) => entry.type === 'literal')) {
    expect(mapping).not.toHaveProperty('sourceOutputName')
  }
  const orderOnly = stored.edges.find((candidate) => candidate.targetNodeId === (stored.nodes.find((candidate) => candidate.blockTypeId === 'WaitForDuration')?.id))
  expect(orderOnly?.mappings).toEqual({})
  expect(stored).not.toHaveProperty('outputMappings')
  expect(Object.values(edge?.mappings ?? {}).map((mapping) => mapping.type)).toEqual(expect.arrayContaining(['direct', 'literal']))
  const direct = edge?.mappings.direct
  expect(direct).not.toHaveProperty('literalValue')
  expect(direct).not.toHaveProperty('sourceOutput')
})

test(`an empty default output removes the key from a ContinueWithDefault policy @row:error-policy.defaultOutputJson#absent`, async ({ page, harness }) => {
  const name = await create(page, harness, 'Absent default outputs')
  await dropBlock(page, 'StringToInt', 'Conversion')
  await page.getByText('Error Handling').click()
  await page.getByRole('combobox', { name: 'Error Policy' }).click()
  await page.getByRole('option', { name: 'Continue With Default Output' }).click()
  const field = page.getByLabel('Default Output (JSON)')
  await field.fill('{"Result":9}')
  await field.fill('')
  await authorInvalidStringInput(page)
  await publishAndRead(page, harness, name)
  const stored = await readStoredVersion(harness, name)
  const node = stored.nodes.find((candidate) => candidate.blockTypeId === 'StringToInt')
  expect(node?.errorPolicy).toEqual({ policy: 'ContinueWithDefault' })
})

const absentPolicyRow = 'node.errorPolicy#absent'
test(`resetting the policy omits errorPolicy after Save @row:${absentPolicyRow}`, async ({ page, harness }) => {
  const name = await create(page, harness, 'Absent policy')
  await dropBlock(page, 'StringToInt', 'Conversion')
  await page.getByText('Error Handling').click()
  await page.getByRole('combobox', { name: 'Error Policy' }).click()
  await page.getByRole('option', { name: 'Skip Node & Downstream' }).click()
  await page.getByRole('button', { name: 'Reset to default' }).click()
  await authorInvalidStringInput(page)
  const stored = await publishAndRead(page, harness, name)
  const node = stored.nodes.find((candidate) => candidate.blockTypeId === 'StringToInt')
  expect(node).not.toHaveProperty('errorPolicy')
})

const absentOutputRows = ['node.outputPorts#absent']
test(`an untouched TryCatch stores no declared outputs ${absentOutputRows.map((row) => `@row:${row}`).join(' ')}`, async ({ page, harness }) => {
  const name = await create(page, harness, 'Absent outputs')
  await dropBlock(page, 'TryCatch', 'Control Flow')
  const node = page.getByTestId('workflow-node-TryCatch')
  await wire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'), node, 20)
  await page.getByRole('button', { name: 'Cancel' }).click()
  const stored = await publishAndRead(page, harness, name)
  const storedNode = stored.nodes.find((candidate) => candidate.blockTypeId === 'TryCatch')
  expect(storedNode).not.toHaveProperty('outputPorts')
})

test('an empty declared output label is absent from the stored document @row:declared-output.displayName#absent @row:declared-output.type#blank', async ({ page, harness }) => {
  const name = await create(page, harness, 'Absent output label')
  await dropBlock(page, 'TryCatch', 'Control Flow')
  await page.getByRole('button', { name: 'Add Output Port' }).click()
  await page.getByLabel('Label', { exact: true }).nth(0).fill('')
  await saveWorkflowRequest(page)
  const stored = await readStoredVersion(harness, name)
  const output = stored.nodes.find((candidate) => candidate.blockTypeId === 'TryCatch')?.outputPorts?.[0]
  expect(output).not.toHaveProperty('displayName')
  expect(output).toHaveProperty('type', '')
})

test('emptying every declared output source drops the sources key @row:declared-output.sources#absent', async ({ page, harness }) => {
  const name = await create(page, harness, 'Empty output sources')
  await dropBlock(page, 'TryCatch', 'Control Flow')
  await page.getByRole('button', { name: 'Add Output Port' }).click()
  for (const branch of ['Try', 'Catch', 'Finally']) {
    await page.getByLabel(`${branch} →`, { exact: true }).nth(0).fill('')
  }
  await saveWorkflowRequest(page)
  const stored = await readStoredVersion(harness, name)
  const output = stored.nodes.find((candidate) => candidate.blockTypeId === 'TryCatch')?.outputPorts?.[0]
  expect(output).not.toHaveProperty('sources')
})

async function create(page: Page, harness: Harness, prefix: string): Promise<string> {
  const name = `${prefix} ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflowRequest(page, name)
  await expect(page.getByTestId('workflow-node-WorkflowInput')).toBeVisible()
  return name
}

async function addOutputParameter(page: Page, name: string, type: string, collectionDepth = 0): Promise<void> {
  await openWorkflowSettings(page)
  await page.getByRole('tab', { name: 'Outputs' }).click()
  await page.getByRole('button', { name: 'Add Parameter' }).click()
  const index = await page.getByRole('textbox', { name: /^output \d+ name$/ }).count() - 1
  await page.getByLabel(`output ${String(index)} name`).fill(name)
  await page.getByLabel(`output ${String(index)} display name`).fill(name)
  await page.getByRole('combobox', { name: `output ${String(index)} type` }).click()
  await page.getByRole('option', { name: type, exact: true }).click()
  for (let depth = 0; depth < collectionDepth; depth += 1) {
    await page.getByRole('button', { name: `output ${String(index)} type wrapped in a collection` }).click()
  }
  await page.getByRole('button', { name: 'Done' }).click()
}

async function dropBlock(page: Page, block: string, category: string, targetPosition = DROP_POINT): Promise<void> {
  await dropPaletteBlock(page, block, category, targetPosition)
}

async function authorInvalidStringInput(page: Page): Promise<void> {
  const node = page.getByTestId('workflow-node-StringToInt')
  await wire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'),
    page.getByTestId(`workflow-port-${await node.getAttribute('data-node-id')}-input-Value`))
  await page.getByRole('combobox', { name: 'Value mapping type' }).click()
  await page.getByRole('option', { name: 'Literal', exact: true }).click()
  await page.getByLabel('Value literal', { exact: true }).fill('abc')
  await page.getByRole('button', { name: 'Save Mappings' }).click()
}

async function publishAndRead(page: Page, harness: Harness, name: string) {
  await publishWorkflowRequest(page)
  return readStoredVersion(harness, name)
}

function policyLabel(policy: string): string {
  switch (policy) {
    case 'FailWorkflow': return 'Fail Workflow (Default)'
    case 'ContinueWithDefault': return 'Continue With Default Output'
    case 'EmitError': return 'Emit Error to Virtual Port'
    case 'Skip': return 'Skip Node & Downstream'
    default: return policy
  }
}
