import type { Locator, Page, Request, Response } from '@playwright/test'
import type { CompilationDiagnostic } from '@reeve-iot/workflow-client'
import { validationMessage } from '@reeve-iot/workflow-core'
import { VALIDATION_MESSAGES } from '../../src/validationMessages'
import { expect } from './fixtures'
import type { Harness } from './fixtures'
import {
  createWorkflowRequest, dropPaletteBlock, openFileRequest, openPlayground, publishWorkflowRequest, saveWorkflowRequest,
} from './canvas'
import { openWorkflowSettings } from './settings'

export async function createWorkflow(page: Page, pageUrl: string, prefix: string): Promise<string> {
  const name = `${prefix} ${String(Date.now())}`
  await openPlayground(page, pageUrl)
  await createWorkflowRequest(page, name)
  await expect(page.getByTestId('workflow-node-WorkflowInput')).toBeVisible()
  return name
}

export async function latestVersionForName(harness: Harness, name: string) {
  const workflow = (await harness.api.getWorkflows()).find((item) => item.name === name)
  if (workflow === undefined) throw new Error(`No workflow named '${name}'.`)
  const versions = await harness.api.getVersions({ id: workflow.id })
  const latest = Math.max(...versions.map((item) => item.versionNumber))
  return harness.api.getVersion({ id: workflow.id, versionNumber: latest })
}

export async function addWorkflowParameter(page: Page, side: 'input' | 'output', name: string, displayName = name, index = 0): Promise<void> {
  await openWorkflowSettings(page)
  const dialog = page.getByRole('dialog', { name: 'Workflow settings' })
  await dialog.getByRole('tab', { name: side === 'input' ? 'Inputs' : 'Outputs' }).click()
  await dialog.getByRole('button', { name: 'Add Parameter' }).click()
  await dialog.getByRole('textbox', { name: `${side} ${String(index)} name` }).fill(name)
  await dialog.getByRole('textbox', { name: `${side} ${String(index)} display name` }).fill(displayName)
  await dialog.getByRole('button', { name: 'Done' }).click()
  await expect(page.locator('.MuiBackdrop-root')).toHaveCount(0)
}

export async function removeWorkflowParameter(page: Page, side: 'input' | 'output', index: number): Promise<void> {
  await openWorkflowSettings(page)
  const dialog = page.getByRole('dialog', { name: 'Workflow settings' })
  await dialog.getByRole('tab', { name: side === 'input' ? 'Inputs' : 'Outputs' }).click()
  await dialog.getByRole('button', { name: `Remove ${side} ${String(index)}` }).click()
  await dialog.getByRole('button', { name: 'Done' }).click()
  await expect(page.locator('.MuiBackdrop-root')).toHaveCount(0)
}

export async function openFileAndValidate(page: Page, fileName: string, contents: string): Promise<Response> {
  return openFileRequest(page, { name: fileName, contents }, 'either')
}

export interface FindingAt { nodeId: string; portName: string }

export async function refuseOnPublish(page: Page, ruleId: string, at?: FindingAt): Promise<CompilationDiagnostic> {
  const response = await publishWorkflowRequest(page, 'refused')
  expect(response.status(), `publish should refuse ${ruleId}`).toBe(400)
  return showsRefusal(page, response, ruleId, at)
}

export async function refuseOnOpen(page: Page, response: Response, ruleId: string, at?: FindingAt): Promise<CompilationDiagnostic> {
  expect(response.status(), `import/validate should refuse ${ruleId}`).toBe(400)
  const shown = await showsRefusal(page, response, ruleId, at)
  await expect(page.getByTestId('status')).toContainText('import/validate:')
  return shown
}

async function showsRefusal(page: Page, response: Response, ruleId: string, at?: FindingAt): Promise<CompilationDiagnostic> {
  const problem = await response.json() as { diagnostics?: readonly CompilationDiagnostic[] }
  const diagnostic = problem.diagnostics?.find((candidate) => candidate.ruleId === ruleId
    && (at === undefined || (candidate.nodeId === at.nodeId && candidate.portName === at.portName)))
  if (diagnostic === undefined) throw new Error(`The refusal carries no '${ruleId}' diagnostic${at === undefined ? '' : ` at ${at.nodeId}.${at.portName}`}.`)
  const expected = validationMessage({ ...diagnostic, code: diagnostic.ruleId, portName: diagnostic.portName ?? undefined }, VALIDATION_MESSAGES)
  await expect(page.getByRole('list', { name: 'Findings' }).locator(`[data-source="publish"][data-code="${ruleId}"]`)
    .filter({ hasText: expected }).first()).toBeVisible()
  return diagnostic
}

export async function refuseOnSave(page: Page, detail: RegExp): Promise<void> {
  let publishRequested = false
  const trackPublish = (request: Request) => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/publish')) publishRequested = true
  }
  page.on('request', trackPublish)
  try {
    const response = await saveWorkflowRequest(page, 'refused')
    expect(response.status(), 'Save should refuse the draft').toBe(400)
    const problem = await response.json() as { detail?: string }
    expect(problem.detail).toMatch(detail)
    await expect(page.getByRole('alert')).toContainText('Saving: updateWorkflow answered 400')
    expect(publishRequested, 'Save admission refusal must happen before any Publish request').toBe(false)
  } finally {
    page.off('request', trackPublish)
  }
}

export async function clearSelection(page: Page): Promise<void> {
  await page.getByTestId('workflow-canvas').click({ position: { x: 8, y: 8 } })
  await expect(page.getByTestId('node-config-panel')).toHaveCount(0)
}

export async function dropBlock(
  page: Page, blockTypeId: string, category: string, targetPosition: { x: number; y: number },
): Promise<void> {
  await clearSelection(page)
  const canvas = page.getByTestId('workflow-canvas')
  const bounds = await canvas.boundingBox()
  if (bounds === null) throw new Error('The workflow canvas has no bounding box.')
  await dropPaletteBlock(page, blockTypeId, category, {
    x: bounds.width * targetPosition.x, y: bounds.height * targetPosition.y,
  })
}

export async function drawWire(
  page: Page,
  source: Locator,
  target: Locator,
  options: { readonly expectMappingDialog?: boolean } = {},
): Promise<void> {
  await clearSelection(page)
  await expect(page.locator('.MuiBackdrop-root')).toHaveCount(0)
  await expect(source).toBeVisible()
  await expect(target).toBeVisible()
  const from = await centre(source)
  const to = await centre(target)
  const canvasBox = await page.getByTestId('workflow-canvas').boundingBox()
  if (canvasBox === null) throw new Error('The workflow canvas has no bounding box.')
  const nudge = {
    x: Math.min(canvasBox.x + canvasBox.width - 1, from.x + 20),
    y: Math.min(canvasBox.y + canvasBox.height - 1, from.y + 10),
  }

  await page.mouse.move(from.x, from.y)
  await page.mouse.down()
  await page.mouse.move(nudge.x, nudge.y)
  await page.mouse.move(to.x, to.y, { steps: 10 })
  await page.mouse.up()
  if (options.expectMappingDialog !== false) await expect(page.getByRole('dialog')).toBeVisible()
}

export async function closeMapping(page: Page, action: 'Cancel' | 'Save Mappings'): Promise<void> {
  await page.getByRole('button', { name: action, exact: true }).click()
  await expect(page.locator('.MuiBackdrop-root')).toHaveCount(0)
}

async function centre(locator: Locator): Promise<{ x: number; y: number }> {
  const box = await locator.boundingBox()
  if (box === null) throw new Error('A wire port has no bounding box.')
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 }
}
