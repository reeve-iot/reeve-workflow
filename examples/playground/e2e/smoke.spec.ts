import { WORKFLOW_INPUT_NODE_ID, readStoredDefinitionText } from '@reeve-iot/workflow-core'
import type { Locator, Page } from '@playwright/test'
import { expect, test } from './support/fixtures'
import { closeMapping, drawWire } from './support/rules'
import { createWorkflowRequest, dropPaletteBlock, openPlayground, publishWorkflowRequest } from './support/canvas'

const BLOCK = 'WaitForDuration'
const DURATION_MS = 1500

const NUMERIC_PORT_TYPES: readonly string[] = ['Int', 'Number']

const DROP_POINT = { x: 440, y: 160 }

const publishButton = (page: Page): Locator => page.getByRole('button', { name: 'Publish', exact: true })

const ROWS = [
  'canvas.nodes#array', 'canvas.edges#array',
  'node.id#string', 'node.blockTypeId#string', 'node.configuration#value-number',
  'connection.sourceNodeId#string', 'connection.targetNodeId#string', 'connection.mappings#object',
]

test(`a dropped block, a wire and Publish reach the server and the stored document ${ROWS.map((row) => `@row:${row}`).join(' ')}`, async ({ page, harness }) => {
  const name = `Smoke ${String(Date.now())}`

  await openPlayground(page, harness.pageUrl)

  await createWorkflowRequest(page, name)

  await expect(page.getByTestId('workflow-node-WorkflowInput')).toBeVisible()
  await expect(publishButton(page)).toBeEnabled()

  const nodes = page.locator('svg g[data-node-id]')
  await expect(nodes).toHaveCount(1)

  await dropBlock(page, BLOCK, 'Control Flow')

  await expect(nodes).toHaveCount(2)
  const node = page.getByTestId(`workflow-node-${BLOCK}`)
  await expect(node).toHaveCount(1)

  const duration = page.getByTestId('port-value-input-DurationMs')
  await expect(duration).toBeVisible()
  expect(await duration.inputValue(), 'the declared default is a hint, not a value in the box').toBe('')
  await duration.fill(String(DURATION_MS))

  const nodeId = await node.getAttribute('data-node-id')
  if (nodeId === null) throw new Error(`The dropped '${BLOCK}' node carries no data-node-id.`)
  await drawWire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'), page.getByTestId(`workflow-port-${nodeId}-input-DurationMs`))
  await expect(page.getByTestId(`workflow-edge-__workflow_input__-${nodeId}`)).toBeVisible()
  await closeMapping(page, 'Cancel')

  const response = await publishWorkflowRequest(page)

  const published = await response.json() as { currentVersionNumber: number | null; draftVersionId: string | null }
  expect(published.currentVersionNumber, 'the published version is the one this test built').toBe(2)
  expect(published.draftVersionId, 'a published version leaves no draft behind').toBeNull()

  const created = (await harness.api.getWorkflows()).find((workflow) => workflow.name === name)
  if (created === undefined) throw new Error(`No workflow named '${name}': Create did not reach the server.`)

  const version = await harness.api.getVersion({ id: created.id, versionNumber: 2 })
  expect(version.isPublished).toBe(true)

  const stored = readStoredDefinitionText(version.definitionJson)
  if (!stored.ok) throw new Error(`The stored definition cannot be read: ${JSON.stringify(stored.fault)}`)

  const drawnNodes = stored.definition.nodes.filter((n) => n.blockTypeId === BLOCK)
  expect(drawnNodes.map((n) => n.id), `exactly one stored '${BLOCK}' node, the drawn one`).toEqual([nodeId])
  const drawn = drawnNodes[0]
  if (drawn === undefined) throw new Error(`The stored document holds no '${BLOCK}' node: ${version.definitionJson}`)

  const blockTypes = await harness.api.getBlockTypes()
  const declaredPort = blockTypes.find((entry) => entry.blockTypeId === BLOCK)
    ?.inputs.find((port) => port.name === 'DurationMs')
  if (declaredPort === undefined) throw new Error(`The catalogue declares no '${BLOCK}.DurationMs' input.`)
  expect(NUMERIC_PORT_TYPES, `'${BLOCK}.DurationMs' is declared numeric`).toContain(declaredPort.type)
  expect(drawn.configuration['DurationMs'], 'the stored value is the number this test typed').toBe(DURATION_MS)

  const wires = stored.definition.edges.filter((edge) => edge.targetNodeId === drawn.id)
  expect(wires, 'exactly one stored wire into the drawn node').toHaveLength(1)
  const wire = wires[0]
  if (wire === undefined) throw new Error(`The stored document holds no wire into '${drawn.id}'.`)
  expect(wire.sourceNodeId, 'the wire starts at the entry node').toBe(WORKFLOW_INPUT_NODE_ID)
  expect(wire.mappings, 'an edge from the start anchor carries no mappings').toEqual({})
})

const WIRE_HIT_PX = 7
const WIRE_MISS_PX = 14
const BESIDE_INPUT_DROP = { x: 440, y: 140 }
const BELOW_RIGHT_OF_INPUT_DROP = { x: 560, y: 440 }
const LOBE_CLEARANCE_PX = 18

interface Point { readonly x: number; readonly y: number }

function wirePoint(from: Point, to: Point, t: number): Point {
  const dx = Math.abs(to.x - from.x) / 2
  const u = 1 - t
  return {
    x: u ** 3 * from.x + 3 * u * u * t * (from.x + dx) + 3 * u * t * t * (to.x - dx) + t ** 3 * to.x,
    y: u ** 3 * from.y + 3 * u * u * t * from.y + 3 * u * t * t * to.y + t ** 3 * to.y,
  }
}

function distanceToWire(point: Point, from: Point, to: Point): number {
  let nearest = Number.POSITIVE_INFINITY
  for (let step = 0; step <= 1000; step += 1) {
    const on = wirePoint(from, to, step / 1000)
    nearest = Math.min(nearest, Math.hypot(on.x - point.x, on.y - point.y))
  }
  return nearest
}

async function canvasZoom(page: Page): Promise<number> {
  const transform = await page.getByTestId('workflow-canvas').locator('svg > g').first().getAttribute('transform')
  const scale = /scale\(([\d.]+)\)/.exec(transform ?? '')?.[1]
  if (scale === undefined) throw new Error(`The canvas transform '${String(transform)}' carries no scale.`)
  return Number(scale)
}

async function wireEnds(page: Page, line: Locator): Promise<{ from: Point; to: Point }> {
  const d = await line.getAttribute('d')
  const numbers = (d ?? '').match(/-?\d+(?:\.\d+)?(?:e-?\d+)?/g)?.map(Number) ?? []
  const [fromX, fromY, , , , , toX, toY] = numbers
  if (numbers.length !== 8 || fromX === undefined || fromY === undefined || toX === undefined || toY === undefined) {
    throw new Error(`The wire path '${String(d)}' is not one cubic.`)
  }
  const box = await line.boundingBox()
  if (box === null) throw new Error('The wire has no bounding box.')
  const zoom = await canvasZoom(page)
  const centre = { x: box.x + box.width / 2, y: box.y + box.height / 2 }
  const half = { x: (toX - fromX) * zoom / 2, y: (toY - fromY) * zoom / 2 }
  return { from: { x: centre.x - half.x, y: centre.y - half.y }, to: { x: centre.x + half.x, y: centre.y + half.y } }
}

function besideWireMiddle(from: Point, to: Point, distance: number): Point {
  const before = wirePoint(from, to, 0.499)
  const after = wirePoint(from, to, 0.501)
  const along = { x: after.x - before.x, y: after.y - before.y }
  const length = Math.hypot(along.x, along.y)
  const mid = wirePoint(from, to, 0.5)
  return { x: mid.x - (along.y / length) * distance, y: mid.y + (along.x / length) * distance }
}

async function clickBesideWire(page: Page, wire: Locator, line: Locator): Promise<void> {
  const { from, to } = await wireEnds(page, line)
  const miss = besideWireMiddle(from, to, WIRE_MISS_PX)
  const hit = besideWireMiddle(from, to, WIRE_HIT_PX)
  expect(distanceToWire(miss, from, to), 'the far click lies its offset from the drawn line').toBeCloseTo(WIRE_MISS_PX, 0)
  expect(distanceToWire(hit, from, to), 'the near click lies its offset from the drawn line').toBeCloseTo(WIRE_HIT_PX, 0)
  await page.mouse.click(miss.x, miss.y)
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(line).toHaveAttribute('stroke-width', '2')
  await page.mouse.click(hit.x, hit.y)
  await expect(page.getByRole('dialog')).toBeVisible()
  await expect(line).toHaveAttribute('stroke-width', '4')
  await closeMapping(page, 'Cancel')
  await expect(wire).toBeVisible()
}

test('a click a few pixels beside a wire opens its mapping, zoomed in or out, and one further off does not', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  await createWorkflowRequest(page, `Wire hit band ${String(Date.now())}`)
  await expect(page.getByTestId('workflow-node-WorkflowInput')).toBeVisible()
  await dropPaletteBlock(page, BLOCK, 'Control Flow', BESIDE_INPUT_DROP)
  const nodeId = await page.getByTestId(`workflow-node-${BLOCK}`).getAttribute('data-node-id')
  if (nodeId === null) throw new Error(`The dropped '${BLOCK}' node carries no data-node-id.`)
  await drawWire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'), page.getByTestId(`workflow-port-${nodeId}-input-DurationMs`))
  await closeMapping(page, 'Cancel')

  const wire = page.getByTestId(`workflow-edge-__workflow_input__-${nodeId}`)
  const line = wire.locator('path').first()
  const box = await line.boundingBox()
  if (box === null) throw new Error('The wire has no bounding box.')
  expect(box.width, 'the wire is long enough to click beside its middle').toBeGreaterThan(40)
  await clickBesideWire(page, wire, line)

  const { from, to } = await wireEnds(page, line)
  const mid = wirePoint(from, to, 0.5)
  await page.mouse.move(mid.x, mid.y)
  for (let step = 0; step < 7; step += 1) await page.mouse.wheel(0, 100)
  await expect.poll(() => canvasZoom(page)).toBeCloseTo(1 / 1.1 ** 7, 3)
  await clickBesideWire(page, wire, line)
})

function towardChord(point: Point, from: Point, to: Point, share: number): Point {
  const along = { x: to.x - from.x, y: to.y - from.y }
  const t = ((point.x - from.x) * along.x + (point.y - from.y) * along.y) / (along.x ** 2 + along.y ** 2)
  const foot = { x: from.x + t * along.x, y: from.y + t * along.y }
  return { x: point.x + (foot.x - point.x) * share, y: point.y + (foot.y - point.y) * share }
}

test('a click between a curved wire and the chord of its ends does not open its mapping', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  await createWorkflowRequest(page, `Wire lobe ${String(Date.now())}`)
  await expect(page.getByTestId('workflow-node-WorkflowInput')).toBeVisible()
  await dropPaletteBlock(page, BLOCK, 'Control Flow', BELOW_RIGHT_OF_INPUT_DROP)
  const nodeId = await page.getByTestId(`workflow-node-${BLOCK}`).getAttribute('data-node-id')
  if (nodeId === null) throw new Error(`The dropped '${BLOCK}' node carries no data-node-id.`)
  await drawWire(page, page.getByTestId('workflow-port-__workflow_input__-output-__start__'), page.getByTestId(`workflow-port-${nodeId}-input-DurationMs`))
  await closeMapping(page, 'Cancel')

  const line = page.getByTestId(`workflow-edge-__workflow_input__-${nodeId}`).locator('path').first()
  const box = await line.boundingBox()
  if (box === null) throw new Error('The wire has no bounding box.')
  expect(box.width, 'the wire runs right of the input node').toBeGreaterThan(120)
  expect(box.height, 'the wire drops well below the input node').toBeGreaterThan(200)
  const centre = { x: box.x + box.width / 2, y: box.y + box.height / 2 }
  const from = { x: centre.x - box.width / 2, y: centre.y - box.height / 2 }
  const to = { x: centre.x + box.width / 2, y: centre.y + box.height / 2 }
  const onCurve = wirePoint(from, to, 0.2)
  const inLobe = towardChord(onCurve, from, to, 0.5)

  await page.mouse.move(inLobe.x, inLobe.y)
  for (let step = 0; step < 8; step += 1) await page.mouse.wheel(0, -100)
  const zoom = 1.1 ** 8
  await expect.poll(() => canvasZoom(page)).toBeCloseTo(zoom, 3)
  expect(distanceToWire(inLobe, from, to) * zoom, 'the lobe point lies outside the wire\'s band').toBeGreaterThan(LOBE_CLEARANCE_PX)

  await page.mouse.click(inLobe.x, inLobe.y)
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(line).toHaveAttribute('stroke-width', '2')
  await page.mouse.click(inLobe.x + (onCurve.x - inLobe.x) * zoom, inLobe.y + (onCurve.y - inLobe.y) * zoom)
  await expect(page.getByRole('dialog')).toBeVisible()
  await expect(line).toHaveAttribute('stroke-width', '4')
  await closeMapping(page, 'Cancel')
})

async function dropBlock(page: Page, blockTypeId: string, category: string): Promise<void> {
  await dropPaletteBlock(page, blockTypeId, category, DROP_POINT)
}
