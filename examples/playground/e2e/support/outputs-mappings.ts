import { readStoredDefinitionText } from '@reeve-iot/workflow-core'
import type { Locator, Page } from '@playwright/test'
import type { Harness } from './fixtures'

export async function readStoredVersion(harness: Harness, name: string) {
  const workflowRow = (await harness.api.getWorkflows()).find((candidate) => candidate.name === name)
  if (!workflowRow) throw new Error(`No workflow named '${name}'.`)
  const workflow = await harness.api.getWorkflow({ id: workflowRow.id })
  const versionNumber = workflow.draftVersionNumber ?? workflow.currentVersionNumber
  if (versionNumber === null) throw new Error(`Workflow '${name}' has no stored version.`)
  const version = await harness.api.getVersion({ id: workflow.id, versionNumber })
  const result = readStoredDefinitionText(version.definitionJson)
  if (!result.ok) throw new Error(`Stored document unreadable: ${JSON.stringify(result.fault)}`)
  return result.definition
}

export async function wire(page: Page, source: Locator, target: Locator, targetXOffset?: number): Promise<void> {
  await page.locator('.MuiBackdrop-root').waitFor({ state: 'detached' })
  const from = await centre(source)
  const rect = await (targetXOffset === undefined
    ? target.boundingBox()
    : page.getByTestId(`workflow-node-body-${await target.getAttribute('data-node-id')}`).boundingBox())
  if (rect === null) throw new Error('The wire endpoint has no bounding box.')
  const to = { x: rect.x + (targetXOffset ?? rect.width / 2), y: rect.y + rect.height / 2 }
  await page.mouse.move(from.x, from.y)
  await page.mouse.down()
  await page.mouse.move(from.x + 20, from.y + 10)
  await page.mouse.move(to.x, to.y, { steps: 10 })
  await page.mouse.up()
}

async function centre(locator: Locator): Promise<{ x: number; y: number }> {
  const rect = await locator.boundingBox()
  if (rect === null) throw new Error('The wire endpoint has no bounding box.')
  return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 }
}
