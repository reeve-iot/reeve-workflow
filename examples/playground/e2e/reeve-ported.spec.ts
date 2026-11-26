import type { WorkflowDefinition } from '@reeve-iot/workflow-core'
import { WORKFLOW_INPUT_NODE_ID, WORKFLOW_OUTPUT_NODE_ID } from '@reeve-iot/workflow-core'
import { expect, test } from './support/fixtures'
import { openPlayground, rollbackWorkflowRequest } from './support/canvas'
import {
  addConfigValue, canvasNodes, chooseSubWorkflow, clearSelection, createWorkflow, declareCollectionInput, declareOutputParameter, definitionFromJson,
  drawWire, dropBlock, enterSubCanvas, expectTraceSteps, onlyNode, outputPort, publish, runStatus, runWorkflow,
  save, selectNode, setConcurrencyMode, setExpression, setField, setSwitch, startAnchor, storedConfig,
  draftDefinition, storedDefinition, versionDefinition, workflowIdOf,
} from './support/reeve-ported'

const LITERAL_ONE = '1 + 1'
const LITERAL_TWO = '1 + 2'

const expressionOf = (definition: WorkflowDefinition): unknown =>
  storedConfig(onlyNode(definition, '', 'Expression'))['Expression']

test('#1 IfElse switched on runs the Then leaf', async ({ page, harness }) => {
  const name = `Ported 1 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const ifElse = await dropBlock(page, 'IfElse', 'Control Flow')
  await setSwitch(page, 'Condition', true)
  await drawWire(page, startAnchor(), ifElse)

  await enterSubCanvas(page, ifElse, 'Then')
  const leaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await publish(page)

  const id = await workflowIdOf(harness.api, name)
  const stored = await storedDefinition(harness.api, id)
  expect(storedConfig(onlyNode(stored, '', 'IfElse'))['Condition'], 'the switch the test set is what was stored').toBe(true)
  expect(onlyNode(stored, `${ifElse}:Then`, 'Expression').id, 'the leaf is on the branch canvas').toBe(leaf)
  expect(canvasNodes(stored, `${ifElse}:Else`), 'the branch the test never built holds nothing').toHaveLength(0)

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, leaf, 1)
})

test('#2 IfElse → ForLoop runs its Body leaf once per index', async ({ page, harness }) => {
  const name = `Ported 2 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const ifElse = await dropBlock(page, 'IfElse', 'Control Flow')
  await setSwitch(page, 'Condition', true)
  await drawWire(page, startAnchor(), ifElse)
  await enterSubCanvas(page, ifElse, 'Then')

  const forLoop = await dropBlock(page, 'ForLoop', 'Control Flow')
  await setField(page, 'To', '2')
  await enterSubCanvas(page, forLoop, 'Body')

  const leaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await publish(page)

  const id = await workflowIdOf(harness.api, name)
  const stored = await storedDefinition(harness.api, id)
  expect(storedConfig(onlyNode(stored, `${ifElse}:Then`, 'ForLoop'))['To'], 'the bound the test typed').toBe(2)
  expect(onlyNode(stored, `${ifElse}:Then/${forLoop}:Body`, 'Expression').id, 'the leaf is on the loop body').toBe(leaf)

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, leaf, 2)
})

test('#3 IfElse with both branches runs only the chosen one', async ({ page, harness }) => {
  const name = `Ported 3 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const ifElse = await dropBlock(page, 'IfElse', 'Control Flow')
  await setSwitch(page, 'Condition', true)
  await drawWire(page, startAnchor(), ifElse)

  await enterSubCanvas(page, ifElse, 'Then')
  const thenLeaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await save(page)

  await enterSubCanvas(page, ifElse, 'Else')
  const elseLeaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_TWO)
  await publish(page)

  const id = await workflowIdOf(harness.api, name)
  const stored = await storedDefinition(harness.api, id)
  expect(onlyNode(stored, `${ifElse}:Then`, 'Expression').id).toBe(thenLeaf)
  expect(onlyNode(stored, `${ifElse}:Else`, 'Expression').id).toBe(elseLeaf)

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, thenLeaf, 1)
  await expectTraceSteps(page, elseLeaf, 0)

  await selectNode(page, ifElse)
  await setSwitch(page, 'Condition', false)
  await publish(page)

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, elseLeaf, 1)
  await expectTraceSteps(page, thenLeaf, 0)
})

test('#4 ForLoop at the root runs its Body leaf To times', async ({ page, harness }) => {
  const name = `Ported 4 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const forLoop = await dropBlock(page, 'ForLoop', 'Control Flow')
  await setField(page, 'To', '2')
  await drawWire(page, startAnchor(), forLoop)

  await enterSubCanvas(page, forLoop, 'Body')
  const leaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await publish(page)

  const id = await workflowIdOf(harness.api, name)
  const stored = await storedDefinition(harness.api, id)
  expect(storedConfig(onlyNode(stored, '', 'ForLoop'))['To'], 'the bound the test typed').toBe(2)
  expect(onlyNode(stored, `${forLoop}:Body`, 'Expression').id, 'the leaf is on the loop body').toBe(leaf)

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, leaf, 2)
})

test('#5 IterateOver runs its Body once per Collection input item', async ({ page, harness }) => {
  const name = `Ported 5 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)
  await declareCollectionInput(page)

  const iterate = await dropBlock(page, 'IterateOver', 'Control Flow')
  await drawWire(page, outputPort(WORKFLOW_INPUT_NODE_ID, 'items'), iterate, { input: 'Collection', source: 'items' })
  await enterSubCanvas(page, iterate, 'Body')
  const leaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await publish(page)

  const id = await workflowIdOf(harness.api, name)
  const stored = await storedDefinition(harness.api, id)
  expect(stored.edges.find((edge) => edge.targetNodeId === iterate)?.mappings['Collection'], 'the declared collection input is wired')
    .toEqual({ type: 'direct', sourceOutputName: 'items' })
  expect(onlyNode(stored, `${iterate}:Body`, 'Expression').id).toBe(leaf)

  await runWorkflow(page, { items: '["first", "second", "third"]' })
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, leaf, 3)
})

test('Coalesce is dropped from the palette and saved on the root canvas', async ({ page, harness }) => {
  const name = `Ported Coalesce ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const coalesce = await dropBlock(page, 'Coalesce', 'Control Flow')
  await save(page)

  const stored = await draftDefinition(harness.api, await workflowIdOf(harness.api, name))
  expect(onlyNode(stored, '', 'Coalesce').id, 'the dropped block is the saved one').toBe(coalesce)
})

test('IfPresent is dropped from the palette and keeps a leaf on each of its Present and Absent branches', async ({ page, harness }) => {
  const name = `Ported IfPresent ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const ifPresent = await dropBlock(page, 'IfPresent', 'Control Flow')
  await enterSubCanvas(page, ifPresent, 'Present')
  const presentLeaf = await dropBlock(page, 'Expression', 'Control Flow')
  await save(page)

  await enterSubCanvas(page, ifPresent, 'Absent')
  const absentLeaf = await dropBlock(page, 'Expression', 'Control Flow')
  await save(page)

  const stored = await draftDefinition(harness.api, await workflowIdOf(harness.api, name))
  expect(onlyNode(stored, `${ifPresent}:Present`, 'Expression').id).toBe(presentLeaf)
  expect(onlyNode(stored, `${ifPresent}:Absent`, 'Expression').id).toBe(absentLeaf)
})

test('#6 TryCatch happy path runs Try and Finally, never Catch', async ({ page, harness }) => {
  const name = `Ported 6 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const tryCatch = await dropBlock(page, 'TryCatch', 'Control Flow')
  await drawWire(page, startAnchor(), tryCatch)

  await enterSubCanvas(page, tryCatch, 'Try')
  const tryLeaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await save(page)

  await enterSubCanvas(page, tryCatch, 'Catch')
  const catchLeaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_TWO)
  await save(page)

  await enterSubCanvas(page, tryCatch, 'Finally')
  const finallyLeaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await publish(page)

  const id = await workflowIdOf(harness.api, name)
  const stored = await storedDefinition(harness.api, id)
  expect(onlyNode(stored, `${tryCatch}:Try`, 'Expression').id).toBe(tryLeaf)
  expect(onlyNode(stored, `${tryCatch}:Catch`, 'Expression').id).toBe(catchLeaf)
  expect(onlyNode(stored, `${tryCatch}:Finally`, 'Expression').id).toBe(finallyLeaf)

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, tryLeaf, 1)
  await expectTraceSteps(page, finallyLeaf, 1)
  await expectTraceSteps(page, catchLeaf, 0)
})

test('#7 TryCatch routes a throw in Try to Catch', async ({ page, harness }) => {
  const name = `Ported 7 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const tryCatch = await dropBlock(page, 'TryCatch', 'Control Flow')
  await drawWire(page, startAnchor(), tryCatch)

  await enterSubCanvas(page, tryCatch, 'Try')
  const converter = await dropBlock(page, 'StringToInt', 'Conversion')
  await addConfigValue(page, 'Value', 'abc')
  await save(page)

  await enterSubCanvas(page, tryCatch, 'Catch')
  const caught = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await save(page)

  await enterSubCanvas(page, tryCatch, 'Finally')
  const afterwards = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_TWO)
  await publish(page)

  const id = await workflowIdOf(harness.api, name)
  const stored = await storedDefinition(harness.api, id)
  expect(storedConfig(onlyNode(stored, `${tryCatch}:Try`, 'StringToInt'))['Value'], 'the literal the test typed').toBe('abc')

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, converter, 1, 'Failed')
  await expectTraceSteps(page, caught, 1)
  await expectTraceSteps(page, afterwards, 1)
})

test('#8 WhileLoop runs its Body leaf once, its Continue signal unfed', async ({ page, harness }) => {
  const name = `Ported 8 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const whileLoop = await dropBlock(page, 'WhileLoop', 'Control Flow')
  await drawWire(page, startAnchor(), whileLoop)

  await enterSubCanvas(page, whileLoop, 'Body')
  const leaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await publish(page)

  const id = await workflowIdOf(harness.api, name)
  const stored = await storedDefinition(harness.api, id)
  expect(onlyNode(stored, `${whileLoop}:Body`, 'Expression').id, 'the leaf is on the loop body').toBe(leaf)

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, leaf, 1)
})

test('#9 SubWorkflow runs the inner workflow', async ({ page, harness }) => {
  const innerName = `Ported 9 inner ${String(Date.now())}`
  const outerName = `Ported 9 outer ${String(Date.now())}`

  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, innerName)
  await declareOutputParameter(page, 'result', 'Int')
  const innerLeaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await clearSelection(page)
  await drawWire(page, startAnchor(), innerLeaf)
  await drawWire(page, outputPort(innerLeaf, 'Result'), WORKFLOW_OUTPUT_NODE_ID, { input: 'result', source: 'Result' })
  await publish(page)

  const innerId = await workflowIdOf(harness.api, innerName)

  await createWorkflow(page, outerName)
  await declareOutputParameter(page, 'result', 'Int')
  const subWorkflow = await dropBlock(page, 'SubWorkflow', 'Workflow')
  await chooseSubWorkflow(page, subWorkflow, innerName, 'result')
  await drawWire(page, startAnchor(), subWorkflow)
  await clearSelection(page)
  await drawWire(page, outputPort(subWorkflow, 'result'), WORKFLOW_OUTPUT_NODE_ID, { input: 'result', source: 'result' })
  await publish(page)

  const outerId = await workflowIdOf(harness.api, outerName)
  const stored = await storedDefinition(harness.api, outerId)
  expect(storedConfig(onlyNode(stored, '', 'SubWorkflow'))['WorkflowId'], 'the workflow the picker chose').toBe(innerId)

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expect(page.getByTestId('run-output')).toHaveText(/"result": 2/)
  await expectTraceSteps(page, subWorkflow, 1)
})

test('#10 publishing twice then rolling back restores the earlier document', async ({ page, harness }) => {
  const name = `Ported 10 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const leaf = await dropBlock(page, 'Expression', 'Control Flow')
  await setExpression(page, 'Expression', LITERAL_ONE)
  await drawWire(page, startAnchor(), leaf)
  const first = await publish(page)

  await selectNode(page, leaf)
  await setExpression(page, 'Expression', LITERAL_TWO)
  const second = await publish(page)
  expect(second, 'each publish stores another version').toBe(first + 1)

  const id = await workflowIdOf(harness.api, name)
  expect(expressionOf(await versionDefinition(harness.api, id, first)), 'the first version kept its literal').toBe(`expr:${LITERAL_ONE}`)
  expect(expressionOf(await versionDefinition(harness.api, id, second)), 'the second version kept its own').toBe(`expr:${LITERAL_TWO}`)

  const rolled = await rollbackWorkflowRequest(page, first)
  expect(rolled.status(), 'the rollback must be accepted').toBeLessThan(300)
  const row = await rolled.json() as { currentVersionNumber: number | null; definitionJson: string }
  expect(row.currentVersionNumber, 'rollback appends a new current version').toBe(second + 1)
  expect(definitionFromJson(row.definitionJson), 'the appended version copies the first version').toEqual(
    await versionDefinition(harness.api, id, first),
  )
  expect(expressionOf(definitionFromJson(row.definitionJson)), 'the document went back with it').toBe(`expr:${LITERAL_ONE}`)

  const current = await harness.api.getWorkflow({ id })
  await expect(page.locator('[data-testid="versions"] li[data-current="true"]')).toHaveCount(1)
  await expect(page.locator('[data-testid="versions"] li[data-current="true"]'))
    .toHaveAttribute('data-version', String(current.currentVersionNumber))

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expectTraceSteps(page, leaf, 1)
})

test('#11 Single mode is set in the settings dialog and records a single run', async ({ page, harness }) => {
  const name = `Ported 11 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const wait = await dropBlock(page, 'WaitForDuration', 'Control Flow')
  await setField(page, 'DurationMs', '2000')
  await drawWire(page, startAnchor(), wait)

  await setConcurrencyMode(page, 'Parallel', 4)
  await publish(page)
  const parallel = await harness.api.getWorkflow({ id: await workflowIdOf(harness.api, name) })
  expect(parallel.mode).toBe('Parallel')
  expect(parallel.maxConcurrentRuns).toBe(4)

  await setConcurrencyMode(page, 'Single')
  await save(page)
  const id = await workflowIdOf(harness.api, name)
  const single = await harness.api.getWorkflow({ id })
  expect(single.mode).toBe('Single')
  expect(single.maxConcurrentRuns).toBe(1)

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expect.poll(async () => (await harness.api.getExecutions({ id, take: 20 })).totalCount, { timeout: 0 }).toBe(1)
  const executions = await harness.api.getExecutions({ id, take: 20 })
  expect(executions.items.map((execution) => execution.status), 'the one run finished').toEqual(['Succeeded'])
})

test('#12 Restart mode is set in the settings dialog and records a single run', async ({ page, harness }) => {
  const name = `Ported 12 ${String(Date.now())}`
  await openPlayground(page, harness.pageUrl)
  await createWorkflow(page, name)

  const wait = await dropBlock(page, 'WaitForDuration', 'Control Flow')
  await setField(page, 'DurationMs', '3000')
  await drawWire(page, startAnchor(), wait)

  await setConcurrencyMode(page, 'Restart')
  await publish(page)

  const id = await workflowIdOf(harness.api, name)
  expect((await harness.api.getWorkflow({ id })).mode).toBe('Restart')

  await runWorkflow(page)
  await expect(runStatus(page)).toHaveText(/Status: Succeeded/)
  await expect.poll(async () => (await harness.api.getExecutions({ id, take: 20 })).totalCount, { timeout: 0 }).toBe(1)
  const executions = await harness.api.getExecutions({ id, take: 20 })
  expect(executions.items.map((execution) => execution.status), 'the one run finished').toEqual(['Succeeded'])
})
