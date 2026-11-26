import { readStoredDefinitionText } from '@reeve-iot/workflow-core'
import type { WorkflowNode } from '@reeve-iot/workflow-core'
import type { Page } from '@playwright/test'
import type { Harness } from './fixtures'
import { createWorkflowRequest, dropPaletteBlock, DROP_POINT, openPlayground, saveWorkflowRequest } from './canvas'
import { expect } from './fixtures'

export { DROP_POINT }

export async function clearNodeSelection(page: Page): Promise<void> {
  await page.getByTestId('workflow-canvas').click({ position: { x: 20, y: 20 } })
}

export async function createWorkflow(page: Page, name: string, harness: Harness): Promise<void> {
  await openPlayground(page, harness.pageUrl)
  await createWorkflowRequest(page, name)
  await expect(page.getByTestId('workflow-node-WorkflowInput')).toBeVisible()
}

export async function dropBlock(page: Page, block: string, category: string): Promise<void> {
  await clearNodeSelection(page)
  await dropPaletteBlock(page, block, category, DROP_POINT)
}

export async function saveNodes(
  page: Page,
  name: string,
  harness: Harness,
  reselectBlockTypeId?: string,
): Promise<WorkflowNode[]> {
  await saveWorkflowRequest(page)
  const workflow = (await harness.api.getWorkflows()).find((entry) => entry.name === name)
  if (workflow === undefined) throw new Error(`Created workflow '${name}' was not stored.`)
  const versions = await harness.api.getVersions({ id: workflow.id })
  const latest = versions.reduce((highest, version) => Math.max(highest, version.versionNumber), 0)
  if (latest === 0) throw new Error(`Workflow '${name}' has no saved version.`)
  const version = await harness.api.getVersion({ id: workflow.id, versionNumber: latest })
  const stored = readStoredDefinitionText(version.definitionJson)
  if (!stored.ok) throw new Error(`Stored definition was unreadable: ${JSON.stringify(stored.fault)}`)
  if (reselectBlockTypeId !== undefined) {
    await page.getByTestId(`workflow-node-${reselectBlockTypeId}`).click()
    await expect(page.getByTestId('node-config-panel')).toBeVisible()
  }
  return stored.definition.nodes
}
