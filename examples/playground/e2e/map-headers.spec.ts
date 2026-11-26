import { expect, test } from './support/fixtures'
import * as ported from './support/reeve-ported'
import { openPlayground, saveWorkflowRequest } from './support/canvas'

async function openHeaders(page: Parameters<typeof openPlayground>[0], pageUrl: string, name: string): Promise<string> {
  await openPlayground(page, pageUrl)
  await ported.createWorkflow(page, name)
  const nodeId = await ported.dropBlock(page, 'HttpRequest', 'Network')
  await ported.selectNode(page, nodeId)
  await ported.setField(page, 'Url', 'https://example.test/hook')
  await expect(page.getByRole('button', { name: 'Add Headers entry' })).toBeVisible()
  return nodeId
}

async function addEntry(page: Parameters<typeof openPlayground>[0], position: number, key: string, value: string): Promise<void> {
  await page.getByRole('button', { name: 'Add Headers entry' }).click()
  await page.getByLabel(`Headers key ${String(position)}`).fill(key)
  await page.getByLabel(`Headers value ${String(position)}`).fill(value)
}

test('HttpRequest headers built in key/value rows are saved and published as the object the rows hold @row:node.configuration#value-object', async ({ page, harness }) => {
  const name = `Headers rows ${String(Date.now())}`
  const nodeId = await openHeaders(page, harness.pageUrl, name)
  await addEntry(page, 1, 'X-Trace', 'abc')
  await addEntry(page, 2, 'Accept', 'application/json')
  await ported.drawWire(page, ported.startAnchor(), nodeId)
  await ported.selectNode(page, nodeId)

  const sent = (await saveWorkflowRequest(page)).request().postDataJSON() as { definitionJson: string }
  const sentNode = ported.onlyNode(ported.definitionFromJson(sent.definitionJson), '', 'HttpRequest')
  expect(ported.storedConfig(sentNode)['Headers']).toEqual({ 'X-Trace': 'abc', Accept: 'application/json' })
  expect(sentNode.passthrough ?? []).toEqual([])
  const id = await ported.workflowIdOf(harness.api, name)
  const draft = ported.storedConfig(ported.onlyNode(await ported.draftDefinition(harness.api, id), '', 'HttpRequest'))
  expect(draft['Headers']).toEqual({ 'X-Trace': 'abc', Accept: 'application/json' })

  expect(await ported.publish(page)).toBeGreaterThan(0)
  const config = ported.storedConfig(ported.onlyNode(await ported.storedDefinition(harness.api, id), '', 'HttpRequest'))
  expect(config['Headers']).toEqual({ 'X-Trace': 'abc', Accept: 'application/json' })
  expect(ported.onlyNode(await ported.storedDefinition(harness.api, id), '', 'HttpRequest').passthrough ?? []).toEqual([])
})

test('a repeated header key is marked on both rows, both stay, and the document keeps the last whole map', async ({ page, harness }) => {
  const name = `Headers duplicate ${String(Date.now())}`
  const nodeId = await openHeaders(page, harness.pageUrl, name)
  await addEntry(page, 1, 'Accept', 'one')
  await addEntry(page, 2, 'Accept', 'two')

  await expect(page.getByText('Another row has this key')).toHaveCount(2)
  await expect(page.getByLabel('Headers value 1')).toHaveValue('one')
  await expect(page.getByLabel('Headers value 2')).toHaveValue('two')

  await ported.drawWire(page, ported.startAnchor(), nodeId)
  await ported.selectNode(page, nodeId)
  await ported.save(page)
  const draft = ported.storedConfig(ported.onlyNode(await ported.draftDefinition(harness.api, await ported.workflowIdOf(harness.api, name)), '', 'HttpRequest'))
  expect(draft['Headers']).toEqual({ Accept: 'one' })
})

test('a header the name rule catches is saved protected and reads back as stars, the rows stay, and a second save sending the stars is accepted', async ({ page, harness }) => {
  const name = `Headers protected ${String(Date.now())}`
  const nodeId = await openHeaders(page, harness.pageUrl, name)
  await addEntry(page, 1, 'Authorization', 'Bearer t0ken')
  await addEntry(page, 2, 'Accept', 'application/json')
  await ported.drawWire(page, ported.startAnchor(), nodeId)
  await ported.selectNode(page, nodeId)
  await ported.save(page)

  const id = await ported.workflowIdOf(harness.api, name)
  const draft = async () => ported.draftDefinition(harness.api, id)
  const headers = async (): Promise<Record<string, string>> =>
    ported.storedConfig(ported.onlyNode(await draft(), '', 'HttpRequest'))['Headers'] as Record<string, string>
  expect(await headers()).toEqual({ Authorization: '***', Accept: 'application/json' })
  expect(JSON.stringify(await draft())).not.toContain('t0ken')
  await ported.selectNode(page, nodeId)
  const keys = page.locator('[data-testid^="kv-key-Headers-"]')
  const values = page.locator('[data-testid^="kv-value-Headers-"]')
  await expect(keys).toHaveCount(2)
  expect([await keys.nth(0).inputValue(), await keys.nth(1).inputValue()].sort()).toEqual(['Accept', 'Authorization'])
  expect([await values.nth(0).inputValue(), await values.nth(1).inputValue()].sort()).toEqual(['***', 'application/json'])

  await ported.setField(page, 'Url', 'https://example.test/other')
  await ported.save(page)
  expect(await headers()).toEqual({ Authorization: '***', Accept: 'application/json' })
  expect(ported.storedConfig(ported.onlyNode(await draft(), '', 'HttpRequest'))['Url']).toBe('https://example.test/other')
})

test('a header written as a line is marked on its row and written nowhere @row:secret-input-key-invalid', async ({ page, harness }) => {
  const name = `Headers line ${String(Date.now())}`
  const nodeId = await openHeaders(page, harness.pageUrl, name)
  await addEntry(page, 1, 'Authorization: Bearer t0ken', 'x')

  await expect(page.getByLabel('Headers key 1')).toHaveAttribute('aria-invalid', 'true')
  await expect(page.getByText(/A name is letters, digits/)).toBeVisible()
  await expect(page.getByLabel('Headers value 1')).toHaveValue('x')

  await ported.drawWire(page, ported.startAnchor(), nodeId)
  await ported.selectNode(page, nodeId)
  await ported.save(page)
  const draft = await ported.draftDefinition(harness.api, await ported.workflowIdOf(harness.api, name))
  expect(ported.storedConfig(ported.onlyNode(draft, '', 'HttpRequest'))).not.toHaveProperty('Headers')
  expect(JSON.stringify(draft)).not.toContain('t0ken')
})
