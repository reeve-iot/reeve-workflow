import type { Locator, Page } from '@playwright/test'
import type { ReadOnlyApi } from './api-client'
import type { WorkflowDefinition, WorkflowNode } from '@reeve-iot/workflow-core'
import {
  WORKFLOW_INPUT_NODE_ID,
  WORKFLOW_START_ANCHOR_PORT_NAME,
  readStoredDefinitionText,
} from '@reeve-iot/workflow-core'
import { expect } from './fixtures'
import { dropPaletteBlock, DROP_POINT, NODE_HEADER_POINT } from './canvas'
import { createWorkflowRequest, publishWorkflowRequest, runWorkflowRequest, saveWorkflowRequest } from './canvas'
import { openWorkflowSettings } from './settings'

export { DROP_POINT }

export interface PortRef {
  readonly nodeId: string
  readonly port: string
}

export const button = (page: Page, name: string): Locator => page.getByRole('button', { name, exact: true })

export async function createWorkflow(page: Page, name: string): Promise<void> {
  await createWorkflowRequest(page, name)
  await expect(page.getByTestId('workflow-node-WorkflowInput')).toBeVisible()
  await expect(button(page, 'Publish')).toBeEnabled()
}

export async function dropBlock(
  page: Page, blockTypeId: string, category: string, point: { x: number; y: number } = DROP_POINT,
): Promise<string> {
  await clearSelection(page)
  const before = await page.getByTestId(`workflow-node-${blockTypeId}`).count()
  await dropPaletteBlock(page, blockTypeId, category, point)
  const node = page.getByTestId(`workflow-node-${blockTypeId}`).nth(before)
  const id = await node.getAttribute('data-node-id')
  if (id === null) throw new Error(`The dropped '${blockTypeId}' node carries no data-node-id.`)
  return id
}

export async function selectNode(page: Page, nodeId: string): Promise<void> {
  await page.getByTestId(`workflow-node-body-${nodeId}`).click({ position: NODE_HEADER_POINT })
  await expect(page.getByTestId('node-config-panel')).toBeVisible()
}

export async function clearSelection(page: Page): Promise<void> {
  const closeAlert = page.getByRole('alert').getByRole('button', { name: 'Close' })
  if (await closeAlert.count()) await closeAlert.click()
  const canvas = page.getByTestId('workflow-canvas')
  const bounds = await canvas.boundingBox()
  if (bounds === null) throw new Error('The canvas has no box to clear selection on.')
  await canvas.click({ position: { x: 60, y: Math.floor(bounds.height * 0.6) } })
  await expect(page.getByTestId('node-config-panel')).toHaveCount(0)
}

export const startAnchor = (): PortRef => ({ nodeId: WORKFLOW_INPUT_NODE_ID, port: WORKFLOW_START_ANCHOR_PORT_NAME })

export const outputPort = (nodeId: string, port: string): PortRef => ({ nodeId, port })

export async function drawWire(
  page: Page, source: PortRef, targetNodeId: string,
  mapping?: { readonly input: string; readonly source: string },
): Promise<void> {
  await clearSelection(page)
  const port = page.getByTestId(`workflow-port-${source.nodeId}-output-${source.port}`)
  const body = page.getByTestId(`workflow-node-body-${targetNodeId}`)
  await expect(port).toBeVisible()
  const from = await centre(port)
  const to = await topCentre(body)

  await page.mouse.move(from.x, from.y)
  await page.mouse.down()
  await page.mouse.move(from.x + 20, from.y + 10)
  await page.mouse.move(to.x, to.y, { steps: 10 })
  await page.mouse.up()

  await expect(page.getByTestId(`workflow-edge-${source.nodeId}-${targetNodeId}`)).toBeVisible()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  if (mapping) {
    await dialog.getByLabel(`${mapping.input} source`).click()
    await page.getByRole('option', { name: new RegExp(`^${mapping.source} \\(`) }).click()
    await dialog.getByRole('button', { name: 'Save Mappings' }).click()
  } else {
    await dialog.getByRole('button', { name: 'Cancel' }).click()
  }
  await expect(dialog).toHaveCount(0)
}

export async function enterSubCanvas(page: Page, nodeId: string, branch: string): Promise<void> {
  await clearSelection(page)
  await page.locator(`[data-subcanvas-entry="${nodeId}:${branch}"]`).click()
  await expect(page.getByTestId('workflow-node-WorkflowInput')).toBeVisible()
}

export async function setField(page: Page, fieldName: string, value: string): Promise<void> {
  const field = page.getByTestId(`port-value-input-${fieldName}`)
  await expect(field).toBeVisible()
  await field.fill(value)
}

export async function setSwitch(page: Page, label: string, on: boolean): Promise<void> {
  const toggle = page.getByRole('switch', { name: label })
  if (on) await toggle.check()
  else await toggle.uncheck()
  await expect(toggle).toBeChecked({ checked: on })
}

export async function setExpression(page: Page, fieldName: string, body: string): Promise<void> {
  const mode = page.getByTestId(`mode-expression-${fieldName}`)
  if ((await mode.getAttribute('aria-pressed')) !== 'true') await mode.click()
  const editor = page.getByTestId(`host-expression-editor-${fieldName}`)
  await expect(editor).toBeVisible()
  await editor.fill(body)
}

export async function addConfigValue(page: Page, name: string, value: string): Promise<void> {
  await page.getByLabel('Add variable name').fill(name)
  await button(page, 'Add passthrough variable').click()
  await setField(page, name, value)
}

export async function save(page: Page): Promise<void> {
  await saveWorkflowRequest(page)
}

export async function publish(page: Page): Promise<number> {
  const response = await publishWorkflowRequest(page)
  const published = await response.json() as { currentVersionNumber: number | null; draftVersionId: string | null }
  expect(published.draftVersionId, 'a published version leaves no draft behind').toBeNull()
  return published.currentVersionNumber ?? 0
}

export async function runWorkflow(page: Page, inputs: Record<string, string> = {}): Promise<void> {
  await runWorkflowRequest(page, inputs)
  await expect(runStatus(page)).toBeVisible()
}

export async function chooseSubWorkflow(page: Page, nodeId: string, workflowName: string, outputPort: string): Promise<void> {
  await page.getByRole('combobox', { name: 'Sub-workflow' }).click()
  const option = page.getByRole('option', { name: workflowName, exact: true })
  await expect(option, `the picker offers '${workflowName}'`).toBeVisible({ timeout: 0 })
  await option.click()
  await expect(page.getByTestId(`workflow-port-${nodeId}-output-${outputPort}`), `the node draws '${outputPort}'`)
    .toBeVisible({ timeout: 0 })
}

export const runStatus = (page: Page): Locator => page.getByTestId('run-status')

export const traceStep = (page: Page, nodeId: string, status?: string): Locator =>
  page.locator(`[data-testid="run-steps"] li[data-node-id="${nodeId}"]${status === undefined ? '' : `[data-status="${status}"]`}`)

export async function expectTraceSteps(page: Page, nodeId: string, count: number, status?: string): Promise<void> {
  const named = status === undefined ? `the '${nodeId}' node` : `the '${nodeId}' node as ${status}`
  await expect(traceStep(page, nodeId, status), `${named}, ${String(count)} step(s) - trace: ${await traceText(page)}`)
    .toHaveCount(count)
}

async function traceText(page: Page): Promise<string> {
  return (await page.getByTestId('run-steps').innerText()).replace(/\s+/g, ' ').trim()
}

export async function setConcurrencyMode(page: Page, mode: string, maxConcurrentRuns?: number): Promise<void> {
  await openWorkflowSettings(page)
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('tab', { name: 'Concurrency' }).click()
  await page.getByLabel('Concurrency mode').click()
  await page.getByRole('option', { name: mode }).click()
  if (maxConcurrentRuns !== undefined) {
    await page.getByTestId('concurrency-max-concurrent-input').fill(String(maxConcurrentRuns))
  }
  await button(page, 'Done').click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
}

export async function declareOutputParameter(page: Page, name: string, type?: string): Promise<void> {
  await openWorkflowSettings(page)
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('tab', { name: 'Outputs' }).click()
  await button(page, 'Add Parameter').click()
  await page.getByLabel('output 0 name').fill(name)
  await page.getByLabel('output 0 display name').fill(name)
  if (type !== undefined) {
    await page.getByLabel('output 0 type', { exact: true }).click()
    await page.getByRole('option', { name: type, exact: true }).click()
  }
  await button(page, 'Done').click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
}

export async function declareCollectionInput(page: Page): Promise<void> {
  await openWorkflowSettings(page)
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('tab', { name: 'Inputs' }).click()
  await button(page, 'Add Parameter').click()
  await page.getByLabel('input 0 name').fill('items')
  await page.getByLabel('input 0 display name').fill('items')
  await page.getByLabel('input 0 type', { exact: true }).click()
  await page.getByRole('option', { name: 'String', exact: true }).click()
  await page.getByLabel('input 0 type wrapped in a collection').click()
  await button(page, 'Done').click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
}

export async function workflowIdOf(api: ReadOnlyApi, name: string): Promise<string> {
  const found = (await api.getWorkflows()).find((workflow) => workflow.name === name)
  if (found === undefined) throw new Error(`No workflow named '${name}': the page did not store it.`)
  return found.id
}

export async function storedDefinition(api: ReadOnlyApi, id: string): Promise<WorkflowDefinition> {
  const workflow = await api.getWorkflow({ id })
  return readDefinition(workflow.definitionJson)
}

export async function draftDefinition(api: ReadOnlyApi, id: string): Promise<WorkflowDefinition> {
  const row = await api.getWorkflow({ id })
  const versionNumber = row.draftVersionNumber ?? row.currentVersionNumber
  if (versionNumber === null) throw new Error(`Workflow '${id}' has no saved or current version.`)
  return versionDefinition(api, id, versionNumber)
}

export async function versionDefinition(api: ReadOnlyApi, id: string, versionNumber: number): Promise<WorkflowDefinition> {
  const version = await api.getVersion({ id, versionNumber })
  return readDefinition(version.definitionJson)
}

export function definitionFromJson(json: string): WorkflowDefinition {
  return readDefinition(json)
}

function readDefinition(json: string): WorkflowDefinition {
  const stored = readStoredDefinitionText(json)
  if (!stored.ok) throw new Error(`The stored definition cannot be read: ${JSON.stringify(stored.fault)}`)
  return stored.definition
}

export function walkNodes(definition: WorkflowDefinition): { node: WorkflowNode; canvas: string }[] {
  const found: { node: WorkflowNode; canvas: string }[] = []
  const visit = (canvas: WorkflowDefinition, path: string): void => {
    for (const node of canvas.nodes) {
      found.push({ node, canvas: path })
      for (const [branch, sub] of Object.entries(node.subCanvases ?? {})) {
        visit(sub, path === '' ? `${node.id}:${branch}` : `${path}/${node.id}:${branch}`)
      }
    }
  }
  visit(definition, '')
  return found
}

export function canvasNodes(
  definition: WorkflowDefinition, canvas: string, blockTypeId?: string,
): WorkflowNode[] {
  return walkNodes(definition)
    .filter((entry) => entry.canvas === canvas && (blockTypeId === undefined || entry.node.blockTypeId === blockTypeId))
    .map((entry) => entry.node)
}

export function onlyNode(definition: WorkflowDefinition, canvas: string, blockTypeId: string): WorkflowNode {
  const [node, ...rest] = canvasNodes(definition, canvas, blockTypeId)
  if (node === undefined || rest.length > 0) {
    throw new Error(`Expected one '${blockTypeId}' node on canvas '${canvas}', found ${String(rest.length + (node === undefined ? 0 : 1))}.`)
  }
  return node
}

export function storedConfig(node: WorkflowNode): Record<string, unknown> {
  return { ...node.configuration }
}

async function box(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  const measured = await locator.boundingBox()
  if (measured === null) throw new Error('The element has no box to aim at.')
  return measured
}

async function centre(locator: Locator): Promise<{ x: number; y: number }> {
  const measured = await box(locator)
  return { x: measured.x + measured.width / 2, y: measured.y + measured.height / 2 }
}

async function topCentre(locator: Locator): Promise<{ x: number; y: number }> {
  const measured = await box(locator)
  return { x: measured.x + measured.width / 2, y: measured.y + 10 }
}
