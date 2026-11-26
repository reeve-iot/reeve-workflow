import { expect, test } from './support/fixtures'
import * as ported from './support/reeve-ported'
import { openPlayground, saveWorkflowRequest } from './support/canvas'

const SECRET = 'acct-8841-plain'

function sentHeaders(request: { postDataJSON(): unknown }): unknown {
  const definition = ported.definitionFromJson((request.postDataJSON() as { definitionJson: string }).definitionJson)
  return ported.storedConfig(ported.onlyNode(definition, '', 'HttpRequest'))['Headers']
}

test('a hidden header is saved protected, reads back as stars, is saved untouched, and the run sends its value', async ({ page, harness }) => {
  const name = `Headers hidden ${String(Date.now())}`
  const url = `${harness.state.baseUrl}/e2e-echo/x-account`
  await openPlayground(page, harness.pageUrl)
  await ported.createWorkflow(page, name)
  const nodeId = await ported.dropBlock(page, 'HttpRequest', 'Network')
  await ported.selectNode(page, nodeId)
  await ported.setField(page, 'Url', url)

  await page.getByRole('button', { name: 'Add Headers entry' }).click()
  await page.getByLabel('Headers key 1').fill('X-Account')
  await page.getByLabel('Headers value 1').fill(SECRET)
  await page.getByRole('switch', { name: 'Hide value of Headers entry 1' }).check()
  await expect(page.getByLabel('Headers value 1')).toHaveAttribute('type', 'password')

  await ported.drawWire(page, ported.startAnchor(), nodeId)
  await ported.selectNode(page, nodeId)
  const id = await ported.workflowIdOf(harness.api, name)
  const draftHeaders = async (): Promise<unknown> =>
    ported.storedConfig(ported.onlyNode(await ported.draftDefinition(harness.api, id), '', 'HttpRequest'))['Headers']

  const first = await saveWorkflowRequest(page)
  expect(sentHeaders(first.request())).toEqual({ 'X-Account': `protect:${SECRET}` })
  expect(await draftHeaders()).toEqual({ 'X-Account': '***' })
  expect(JSON.stringify(await ported.draftDefinition(harness.api, id))).not.toContain(SECRET)

  await openPlayground(page, harness.pageUrl)
  await page.getByTestId(`workflow-${id}`).click()
  await expect(page.getByTestId(`workflow-node-body-${nodeId}`)).toBeVisible()
  await ported.selectNode(page, nodeId)
  const masked = page.getByLabel('Headers value 1')
  await expect(masked).toHaveValue('***')
  await expect(masked).toHaveAttribute('type', 'password')
  await expect(masked).toHaveAttribute('readonly', '')
  await expect(page.getByRole('button', { name: 'Replace value of Headers entry 1' })).toBeVisible()

  await ported.setField(page, 'Url', `${url}?again`)
  const second = await saveWorkflowRequest(page)
  expect(sentHeaders(second.request())).toEqual({ 'X-Account': '***' })
  expect(await draftHeaders()).toEqual({ 'X-Account': '***' })

  expect(await ported.publish(page)).toBeGreaterThan(0)
  const stored = await ported.storedDefinition(harness.api, id)
  expect(ported.storedConfig(ported.onlyNode(stored, '', 'HttpRequest'))['Headers']).toEqual({ 'X-Account': '***' })

  await ported.runWorkflow(page)
  await expect(ported.runStatus(page)).toHaveText(/Status: Succeeded/)
  const latest = (await harness.api.getExecutions({ id, take: 1 })).items[0]
  if (latest === undefined) throw new Error('The run left no execution record.')
  const step = (await harness.api.getExecution({ id, executionId: latest.id })).steps.find((entry) => entry.nodeId === nodeId)
  expect(step?.outputsJson).toContain(SECRET)
  expect(step?.inputsJson).not.toContain(SECRET)
  expect(step?.inputsJson).toContain('***')
})
