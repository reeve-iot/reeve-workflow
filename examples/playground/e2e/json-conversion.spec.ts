import type { Page } from '@playwright/test'
import { expect, test } from './support/fixtures'
import type { Harness } from './support/fixtures'
import { readStoredVersion, wire } from './support/outputs-mappings'
import {
  createWorkflowRequest, dropPaletteBlock, DROP_POINT, openPlayground, publishWorkflowRequest, runWorkflowRequest,
} from './support/canvas'

const START = 'workflow-port-__workflow_input__-output-__start__'

test('JsonToText inserted after a typed value writes that value as JSON text', async ({ page, harness }) => {
  const name = `JSON text ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflowRequest(page, name)
  await expect(page.getByTestId('workflow-node-WorkflowInput')).toBeVisible()

  await dropPaletteBlock(page, 'StringToInt', 'Conversion', DROP_POINT)
  const mathId = await nodeId(page, 'StringToInt')
  await wire(page, page.getByTestId(START), page.getByTestId(`workflow-port-${mathId}-input-Value`))
  await page.getByRole('combobox', { name: 'Value mapping type' }).click()
  await page.getByRole('option', { name: 'Literal', exact: true }).click()
  await page.getByLabel('Value literal', { exact: true }).fill('5')
  await page.getByRole('button', { name: 'Save Mappings' }).click()

  await dropPaletteBlock(page, 'JsonToText', 'Conversion', { x: 720, y: 320 })
  const jsonId = await nodeId(page, 'JsonToText')
  await wire(page, page.getByTestId(`workflow-port-${mathId}-output-Result`), page.getByTestId(`workflow-port-${jsonId}-input-Value`))
  await page.getByRole('combobox', { name: 'Value mapping type' }).click()
  await page.getByRole('option', { name: 'Direct', exact: true }).click()
  await page.getByRole('combobox', { name: 'Value source' }).click()
  await page.getByRole('option', { name: /Result/ }).click()
  await page.getByRole('button', { name: 'Save Mappings' }).click()

  await publishWorkflowRequest(page)
  const stored = await readStoredVersion(harness, name)
  const edge = stored.edges.find((candidate) => candidate.sourceNodeId === mathId && candidate.targetNodeId === jsonId)
  expect(edge?.mappings).toEqual({ Value: { type: 'direct', sourceOutputName: 'Result' } })

  await runWorkflowRequest(page)
  await expect(page.getByTestId('status')).toContainText('Ran the workflow')
  const step = await lastStep(harness, name, 'JsonToText')
  expect(step?.status).toBe('Succeeded')
  expect(step?.outputsJson).toMatch(/"Text"\s*:\s*"5"/)
})

async function nodeId(page: Page, blockTypeId: string): Promise<string> {
  const id = await page.getByTestId(`workflow-node-${blockTypeId}`).getAttribute('data-node-id')
  if (id === null) throw new Error(`The '${blockTypeId}' node has no id.`)
  return id
}

async function lastStep(harness: Harness, name: string, nodeType: string) {
  const workflow = (await harness.api.getWorkflows()).find((candidate) => candidate.name === name)
  if (!workflow) throw new Error(`No workflow named '${name}'.`)
  const latest = (await harness.api.getExecutions({ id: workflow.id, take: 1 })).items[0]
  if (!latest) throw new Error('Run did not create an execution record.')
  const detail = await harness.api.getExecution({ id: workflow.id, executionId: latest.id })
  return detail.steps.find((entry) => entry.nodeType === nodeType)
}
