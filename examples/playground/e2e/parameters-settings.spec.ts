import { readParameterListText } from '@reeve-iot/workflow-core'
import type { Page } from '@playwright/test'
import { expect, test } from './support/fixtures'
import type { Harness } from './support/fixtures'
import { createWorkflowRequest, openFileRequest, openPlayground, publishWorkflowRequest, saveWorkflowRequest } from './support/canvas'
import { openWorkflowSettings } from './support/settings'

async function savedVersion(harness: Harness, name: string) {
  const summary = (await harness.api.getWorkflows()).find((item) => item.name === name)
  if (summary === undefined) throw new Error(`Workflow '${name}' was not stored.`)
  const row = await harness.api.getWorkflow({ id: summary.id })
  const versionNumber = row.draftVersionNumber ?? row.currentVersionNumber
  if (versionNumber === null) throw new Error(`Workflow '${name}' has no saved or current version.`)
  return {
    row,
    version: await harness.api.getVersion({ id: row.id, versionNumber }),
  }
}

async function createWorkflow(page: Page, harness: Harness, name: string): Promise<void> {
  await openPlayground(page, harness.pageUrl)
  await createWorkflowRequest(page, name)
  await expect(page.getByRole('button', { name: 'Settings', exact: true })).toBeEnabled()
}

const PARAM_ROWS = [
  'parameter.name#string', 'parameter.name#blank', 'parameter.name#dotted',
  'parameter.type#atom', 'parameter.type#collection', 'parameter.type#nested-collection',
  'parameter.type#null', 'parameter.type#absent',
  'parameter.isRequired#boolean', 'parameter.displayName#string',
  'workflow-row.InputParametersJson#string', 'workflow-row.OutputParametersJson#string',
]

test(`workflow parameters are authored in Settings and read back @row:parameter.displayName#string ${PARAM_ROWS.map((r) => `@row:${r}`).join(' ')}`, async ({ page, harness }) => {
  const name = `Parameters ${Date.now()}`
  await createWorkflow(page, harness, name)
  await openWorkflowSettings(page)

  await page.getByRole('tab', { name: 'Inputs' }).click()
  await page.getByRole('button', { name: 'Add Parameter' }).click()
  await page.getByLabel('input 0 name').fill('inputValue')
  await page.getByLabel('input 0 display name').fill('Input value')
  await page.getByLabel('input 0 type', { exact: true }).click()
  await expect(page.getByRole('listbox')).toBeVisible()
  const offeredTypes = await page.getByRole('option').allTextContents()
  expect(offeredTypes.map((type) => type.trim())).not.toContain('')
  expect(offeredTypes.map((type) => type.trim())).not.toContain('null')
  await page.getByRole('option', { name: 'Collection<String>', exact: true }).click()
  await page.getByRole('checkbox', { name: 'input 0 required' }).check()
  await page.getByRole('button', { name: 'Add Parameter' }).click()
  await page.getByLabel('input 1 name').fill('temporary')
  await page.getByLabel('input 1 name').fill('')
  await expect(page.getByText(/stored-document reader refuses a blank name/i)).toBeVisible()
  await page.getByLabel('input 1 name').fill('has.dot')
  await expect(page.getByText(/may not hold a dot/i)).toBeVisible()
  await page.getByLabel('input 1 name').fill('nestedItems')
  await page.getByLabel('input 1 display name').fill('Nested items')
  await page.getByLabel('input 1 type', { exact: true }).click()
  await page.getByRole('option', { name: 'String', exact: true }).click()
  await page.getByRole('button', { name: 'input 1 type wrapped in a collection' }).click()
  await page.getByRole('button', { name: 'input 1 type wrapped in a collection' }).click()
  await page.getByRole('checkbox', { name: 'input 1 required' }).check()
  await page.getByRole('checkbox', { name: 'input 1 required' }).uncheck()

  await page.getByRole('tab', { name: 'Outputs' }).click()
  await page.getByRole('button', { name: 'Add Parameter' }).click()
  await page.getByLabel('output 0 name').fill('result')
  await page.getByLabel('output 0 display name').fill('Result')
  await page.getByLabel('output 0 type', { exact: true }).click()
  await page.getByRole('option', { name: 'String', exact: true }).click()
  await page.getByRole('checkbox', { name: 'output 0 required' }).check()
  await page.getByRole('button', { name: 'Done' }).click()
  await saveWorkflowRequest(page)

  const { version: saved } = await savedVersion(harness, name)
  const inputs = readParameterListText(saved.inputParametersJson, 'input')
  const outputs = readParameterListText(saved.outputParametersJson, 'output')
  expect(inputs.ok).toBe(true)
  expect(outputs.ok).toBe(true)
  if (!inputs.ok || !outputs.ok) throw new Error('Stored parameter lists could not be read.')
  expect(inputs.entries.map((p) => [p.name, p.displayName, p.type, p.isRequired])).toEqual([
    ['inputValue', 'Input value', 'Collection<String>', true],
    ['nestedItems', 'Nested items', 'Collection<Collection<String>>', false],
  ])
  expect(outputs.entries.map((p) => [p.name, p.displayName, p.type, p.isRequired])).toEqual([
    ['result', 'Result', 'String', true],
  ])
})

test('Publish saves a parameter-only edit before publishing', async ({ page, harness }) => {
  const name = `Publish parameter ${Date.now()}`
  await createWorkflow(page, harness, name)
  await openWorkflowSettings(page)
  await page.getByRole('tab', { name: 'Inputs' }).click()
  await page.getByRole('button', { name: 'Add Parameter' }).click()
  await page.getByLabel('input 0 name').fill('publishValue')
  await page.getByLabel('input 0 display name').fill('Publish value')
  await page.getByLabel('input 0 type', { exact: true }).click()
  await page.getByRole('option', { name: 'String', exact: true }).click()
  await page.getByRole('button', { name: 'Done' }).click()

  await expect(page.getByTestId('dirty')).toHaveText('Unsaved changes')
  await publishWorkflowRequest(page)
  await expect(page.getByText(/^Published version /)).toBeVisible()
  const summary = (await harness.api.getWorkflows()).find((item) => item.name === name)
  if (summary === undefined) throw new Error(`Workflow '${name}' was not stored.`)
  const stored = await harness.api.getWorkflow({ id: summary.id })
  if (stored.currentVersionNumber === null) throw new Error(`Workflow '${name}' has no published version.`)
  const current = await harness.api.getVersion({ id: stored.id, versionNumber: stored.currentVersionNumber })
  const currentInputs = readParameterListText(current.inputParametersJson, 'input')
  expect(currentInputs.ok).toBe(true)
  if (!currentInputs.ok) throw new Error('Published input parameters could not be read.')
  expect(currentInputs.entries).toEqual([
    expect.objectContaining({ name: 'publishValue', displayName: 'Publish value', type: 'String' }),
  ])
})

const PARAMETER_ROWS = [
  'parameter.isRequired#absent',
]

test(`opening and saving parameter fields keeps the reader's empty spelling ${PARAMETER_ROWS.map((r) => `@row:${r}`).join(' ')}`, async ({ page, harness }) => {
  const name = `Parameter spellings ${Date.now()}`
  await createWorkflow(page, harness, name)
  const dto = {
    schemaVersion: '1.0', exportedAtUtc: new Date().toISOString(), name,
    description: null, definitionJson: JSON.stringify({ nodes: [], edges: [] }),
    inputParametersJson: JSON.stringify([
      { name: 'absentRequired', displayName: 'Absent required', type: 'String' },
    ]),
    outputParametersJson: null, executionTimeoutSeconds: null, retryPolicyJson: null,
    mode: null, maxConcurrentRuns: null, maxQueuedRuns: null, overflowBehavior: null,
  }
  await openFileRequest(page, { name: 'parameter-spellings.json', contents: JSON.stringify(dto) })
  await expect(page.getByRole('alert')).toContainText('Nothing was stored')
  await saveWorkflowRequest(page)
  const { version: stored } = await savedVersion(harness, name)
  expect(JSON.parse(stored.inputParametersJson ?? 'null')).toEqual([
    { name: 'absentRequired', displayName: 'Absent required', type: 'String', isRequired: false },
  ])
})

const SETTINGS_ROWS = [
  'workflow-row.ExecutionTimeoutSeconds#number', 'workflow-row.RetryPolicyJson#string',
  'workflow-row.RetryPolicyJson#max-attempts-below-1', 'workflow-row.RetryPolicyJson#initial-delay-below-1',
  'workflow-row.CheckpointPolicyJson#string', 'workflow-row.Mode#string',
  'workflow-row.MaxConcurrentRuns#number', 'workflow-row.MaxQueuedRuns#number',
  'workflow-row.OverflowBehavior#string',
]

test(`workflow execution and concurrency settings persist through Save ${SETTINGS_ROWS.map((r) => `@row:${r}`).join(' ')}`, async ({ page, harness }) => {
  const name = `Settings ${Date.now()}`
  await createWorkflow(page, harness, name)
  await page.getByRole('button', { name: 'Settings', exact: true }).click()
  await page.getByRole('tab', { name: 'Concurrency' }).click()
  await page.getByLabel('Concurrency mode').click()
  await page.getByRole('option', { name: 'Queued' }).click()
  await page.getByLabel('Max concurrent runs', { exact: true }).fill('3')
  await page.getByLabel('Max queued runs', { exact: true }).fill('7')
  await page.getByLabel('Overflow behavior').click()
  await page.getByRole('option', { name: 'Error' }).click()
  await page.getByLabel('Execution timeout seconds', { exact: true }).fill('90')
  await page.getByLabel('Execution timeout seconds', { exact: true }).press('Enter')
  await page.getByLabel('Retry backoff strategy').click()
  await page.getByRole('option', { name: 'Exponential backoff' }).click()
  await page.getByLabel('Retry max attempts', { exact: true }).fill('0')
  await page.getByLabel('Retry max attempts', { exact: true }).press('Enter')
  await expect(page.getByText(/run makes one attempt whatever a lower number says/i)).toBeVisible()
  await page.getByLabel('Retry max attempts', { exact: true }).fill('4')
  await page.getByLabel('Retry max attempts', { exact: true }).press('Enter')
  await page.getByLabel('Retry initial delay ms', { exact: true }).fill('0')
  await page.getByLabel('Retry initial delay ms', { exact: true }).press('Enter')
  await expect(page.getByText(/run reads a delay of 0 or less as no delay at all/i)).toBeVisible()
  await page.getByLabel('Retry initial delay ms', { exact: true }).fill('250')
  await page.getByLabel('Retry initial delay ms', { exact: true }).press('Enter')
  await page.getByLabel('Checkpoint policy mode').click()
  await page.getByRole('option', { name: 'Every block' }).click()
  await page.getByRole('button', { name: 'Done' }).click()
  await saveWorkflowRequest(page)
  const row = (await harness.api.getWorkflows()).find((item) => item.name === name)
  if (row === undefined) throw new Error(`Workflow '${name}' was not stored.`)
  const saved = await harness.api.getWorkflow({ id: row.id })
  expect(saved.executionTimeoutSeconds).toBe(90)
  expect(JSON.parse(saved.retryPolicyJson ?? 'null')).toMatchObject({ BackoffStrategy: 'exponential', MaxAttempts: 4, InitialDelayMs: 250 })
  expect(JSON.parse(saved.checkpointPolicyJson ?? 'null')).toMatchObject({ Mode: 'EveryBlock' })
  expect([saved.mode, saved.maxConcurrentRuns, saved.maxQueuedRuns, saved.overflowBehavior]).toEqual(['Queued', 3, 7, 'Error'])
})

const ABSENT_ROWS = [
  'workflow-row.ExecutionTimeoutSeconds#absent', 'workflow-row.RetryPolicyJson#absent',
  'workflow-row.CheckpointPolicyJson#absent', 'workflow-row.Mode#absent',
  'workflow-row.MaxConcurrentRuns#absent', 'workflow-row.MaxQueuedRuns#absent',
  'workflow-row.OverflowBehavior#absent', 'workflow-row.InputParametersJson#absent',
  'workflow-row.OutputParametersJson#absent',
]

test(`new workflow settings keep the empty spelling ${ABSENT_ROWS.map((r) => `@row:${r}`).join(' ')}`, async ({ page, harness }) => {
  const name = `Empty settings ${Date.now()}`
  await createWorkflow(page, harness, name)
  const { row: saved } = await savedVersion(harness, name)
  expect(saved.executionTimeoutSeconds).toBeNull()
  expect(saved.retryPolicyJson).toBeNull()
  expect(saved.checkpointPolicyJson).toBeNull()
  expect(saved.inputParametersJson).toBeNull()
  expect(saved.outputParametersJson).toBeNull()
  expect([saved.mode, saved.maxConcurrentRuns, saved.maxQueuedRuns, saved.overflowBehavior]).toEqual(['Single', 1, 10, 'Drop'])
})
