import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { expect, test } from './support/fixtures'
import { openPlayground, publishWorkflowRequest } from './support/canvas'
import { createWorkflow, latestVersionForName, openFileAndValidate, refuseOnOpen, refuseOnPublish } from './support/rules'

const validationCorpus = JSON.parse(readFileSync(fileURLToPath(new URL('../../../contracts/workflow-validation-corpus.json', import.meta.url)), 'utf8')) as {
  cases: Array<{ name: string; definition: unknown; signature?: { inputs: unknown[] } }>
}
const documentCorpus = JSON.parse(readFileSync(fileURLToPath(new URL('../../../contracts/workflow-documents.json', import.meta.url)), 'utf8')) as {
  cases: Array<{ name: string; reads: string; text: string; fault?: { rule: string; location: string; detail: string } }>
}
const corpusCase = (name: string) => {
  const found = validationCorpus.cases.find((item) => item.name === name)
  if (found === undefined) throw new Error(`Validation corpus case '${name}' is missing.`)
  return found
}

const dto = (definitionJson: string, overrides: {
  name?: string
  inputParametersJson?: string | null; outputParametersJson?: string | null
  executionTimeoutSeconds?: number | null; retryPolicyJson?: string | null
} = {}) => JSON.stringify({
  name: overrides.name ?? 'Opened lexer case',
  definitionJson,
  description: null,
  executionTimeoutSeconds: overrides.executionTimeoutSeconds ?? null,
  exportedAtUtc: '2026-09-30T00:00:00Z',
  inputParametersJson: overrides.inputParametersJson ?? null,
  maxConcurrentRuns: null,
  maxQueuedRuns: null,
  mode: null,
  outputParametersJson: overrides.outputParametersJson ?? null,
  overflowBehavior: null,
  retryPolicyJson: overrides.retryPolicyJson ?? null,
  schemaVersion: '1',
})

const storedShapeRows = [
  ['canvas.nodes#null', 'canvas.nodes null is refused'], ['canvas.nodes#absent', 'canvas.nodes absent is refused'],
  ['node.id#absent', 'node.id absent is refused'], ['node.id#empty', 'node.id empty is refused'],
  ['node.position#absent', 'node.position absent is refused'], ['position.x#absent', 'position.x absent is refused'],
  ['position.y#absent', 'position.y absent is refused'], ['node.configuration#null', 'node.configuration null is refused'],
  ['node.configuration#absent', 'node.configuration absent is refused'], ['node.configuration#value-null', 'configuration null value is refused'],
  ['node.configuration#value-blank-port-list', 'configuration blank port list is refused'],
  ['node.subCanvases#null', 'node.subCanvases null is refused'], ['node.passthrough#null', 'node.passthrough null is refused'],
  ['node.passthrough#blank', 'node.passthrough blank name is refused'], ['node.outputPorts#null', 'node.outputPorts null is refused'],
  ['declared-output.name#absent', 'declared output name absent is refused'],
  ['declared-output.sources#null', 'declared output sources null is refused'],
  ['node.errorPolicy#null', 'node.errorPolicy null is refused'], ['error-policy.policy#absent', 'error policy.policy absent is refused'],
  ['error-policy.defaultOutputJson#null', 'error policy.defaultOutputJson null is refused'],
  ['error-policy.routeToErrorPort#boolean', 'error policy routeToErrorPort is refused'],
  ['canvas.edges#null', 'canvas.edges null is refused'], ['canvas.edges#absent', 'canvas.edges absent is refused'],
  ['connection.id#absent', 'connection.id absent is refused'], ['connection.id#empty', 'connection.id empty is refused'],
  ['connection.id#duplicate duplicate-edge-id', 'duplicate edge id is refused'],
  ['connection.sourceNodeId#absent', 'connection.sourceNodeId absent is refused'], ['connection.sourceNodeId#empty', 'connection.sourceNodeId empty is refused'],
  ['connection.targetNodeId#absent', 'connection.targetNodeId absent is refused'], ['connection.targetNodeId#empty', 'connection.targetNodeId empty is refused'],
  ['connection.mappings#null', 'connection.mappings null is refused'], ['connection.mappings#absent', 'connection.mappings absent is refused'],
  ['mapping.type#null', 'mapping.type null is refused'], ['mapping.type#absent', 'mapping.type absent is refused'],
  ['mapping.type#other', 'mapping.type other is refused'],
  ['mapping.sourceOutputName#null', 'mapping.sourceOutputName null is refused'], ['mapping.sourceOutputName#blank', 'mapping.sourceOutputName blank is refused'],
  ['mapping.literalValue#null', 'mapping.literalValue null is refused'],
  ['canvas.outputMappings#array output-mappings-unread', 'canvas.outputMappings array is refused'], ['canvas.outputMappings#null', 'canvas.outputMappings null is refused'],
  ['output-mapping.sourceNodeId#string', 'output mapping.sourceNodeId string is refused'], ['output-mapping.sourceNodeId#absent', 'output mapping.sourceNodeId absent is refused'],
  ['output-mapping.sourceOutput#string', 'output mapping.sourceOutput string is refused'], ['output-mapping.sourceOutput#absent', 'output mapping.sourceOutput absent is refused'],
  ['output-mapping.workflowOutput#string', 'output mapping.workflowOutput string is refused'], ['output-mapping.workflowOutput#absent', 'output mapping.workflowOutput absent is refused'],
] as const

const OWN_CODE = ['error-policy-route-unread', 'duplicate-edge-id', 'output-mappings-unread']

for (const [rowIds, caseName] of storedShapeRows) {
  const item = documentCorpus.cases.find((candidate) => candidate.name === caseName)
  if (item === undefined) throw new Error(`Workflow-document case '${caseName}' is missing.`)
  const rule = item.fault?.rule ?? 'L7'
  const tags = rowIds.split(' ').map((id) => `@row:${id}`).join(' ')
  test(`Open file refuses ${caseName} ${tags}`, async ({ page, harness }) => {
    await openPlayground(page, harness.pageUrl)
    const response = await openFileAndValidate(page, `${rowIds.split(' ')[0]}.json`, dto(item.text))
    expect(response.status()).toBe(400)
    expect((await response.json() as { detail?: string }).detail).toContain(`rule ${rule}`)
    const findings = page.getByRole('list', { name: 'Findings' })
    await expect(findings.locator(`[data-code="${OWN_CODE.includes(rule) ? rule : 'definition-unreadable'}"]`)).toHaveCount(1)
  })
}

test('Open file refuses an unlisted key at this object @row:canvas#unlisted-key @row:node#unlisted-key @row:position#unlisted-key @row:declared-output#unlisted-key @row:error-policy#unlisted-key @row:connection#unlisted-key @row:mapping#unlisted-key @row:output-mapping#unlisted-key @row:parameter#unlisted-key @row:port-entry#unlisted-key @row:stored-key-unknown', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  const node = (extra: string) => `{"id":"a","blockTypeId":"Expression","position":{"x":0,"y":0},"configuration":{}${extra}}`
  const wire = (mappings: string) => `{"nodes":[],"edges":[{"id":"e","sourceNodeId":"a","targetNodeId":"b","mappings":${mappings}}]}`
  const documents = [
    ['canvas', '{"nodes":[],"edges":[],"unexpected":0}', null],
    ['node', `{"nodes":[${node(',"unexpected":0')}],"edges":[]}`, null],
    ['position', '{"nodes":[{"id":"a","blockTypeId":"Expression","position":{"x":0,"y":0,"unexpected":0},"configuration":{}}],"edges":[]}', null],
    ['declared-output', `{"nodes":[${node(',"outputPorts":[{"name":"o","type":"Int","unexpected":0}]')}],"edges":[]}`, null],
    ['error-policy', `{"nodes":[${node(',"errorPolicy":{"policy":"Skip","unexpected":0}')}],"edges":[]}`, null],
    ['connection', '{"nodes":[],"edges":[{"id":"e","sourceNodeId":"a","targetNodeId":"b","mappings":{},"unexpected":0}]}', null],
    ['mapping', wire('{"In":{"type":"direct","sourceOutputName":"Out","unexpected":0}}'), null],
    ['output-mapping', '{"nodes":[{"id":"a","blockTypeId":"Expression","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"WorkflowOutput","position":{"x":1,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"a","targetNodeId":"b","mappings":{"result":{"type":"direct","sourceOutputName":"Out","unexpected":0}}}]}', null],
    ['port-entry', '{"nodes":[{"id":"a","blockTypeId":"Expression","position":{"x":0,"y":0},"configuration":{"InputPorts":"[{\\"name\\":\\"p\\",\\"type\\":\\"Int\\",\\"unexpected\\":0}]"}}],"edges":[]}', null],
    ['parameter', '{"nodes":[],"edges":[]}', '[{"name":"p","type":"Int","unexpected":0}]'],
  ] as const
  for (const [name, definition, inputParametersJson] of documents) {
    const response = await openFileAndValidate(page, `unlisted-${name}.json`, dto(definition, { inputParametersJson }))
    expect(response.status(), name).toBe(400)
    expect((await response.json() as { detail?: string }).detail, name).toContain('stored-key-unknown')
  }
})

test('Open file reads optional parameter and port display names @row:parameter.displayName#absent @row:port-entry.displayName#absent', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  const response = await openFileAndValidate(page, 'optional-display-names.json', dto(
    '{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"a","blockTypeId":"dispatch.echo-json","position":{"x":240,"y":0},"configuration":{"InputPorts":"[{\\"name\\":\\"p\\",\\"type\\":\\"Int\\"}]","OutputPorts":"[]"}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"a","mappings":{}}]}',
    { inputParametersJson: '[{"name":"p","type":"Int"}]' },
  ))
  expect(response.status(), await response.text()).toBe(200)
})

const malformed: ReadonlyArray<{ rule: string; definition: string }> = [
  { rule: 'L1', definition: '' },
  { rule: 'L2', definition: '{"nodes":[{"id":"a","configuration":{"deep":' + '['.repeat(65) + '0' + ']'.repeat(65) + '}}]}' },
  { rule: 'L3', definition: '{"nodes":[],"nodes":[]}' },
  { rule: 'L4', definition: '{"nodes":[{"id":"\\ud800"}]}' },
  { rule: 'L5', definition: '{"Nodes":[]}' },
  { rule: 'L7', definition: '[]' },
]

test('an opened catalogue omits a block declaring a term that does not parse from the palette @row:block-port-declaration-invalid', async ({ page, harness }) => {
  const catalogueOrigin = new URL('/catalogue-refusal', harness.pageUrl).href
  await openPlayground(page, `${harness.pageUrl}&api=${encodeURIComponent(catalogueOrigin)}`)
  await page.getByTestId('palette-category-Catalogue refusals').click()
  await expect(page.getByTestId('workflow-block-unique-catalogue')).toBeVisible()
  await expect(page.getByTestId('workflow-block-unparseable-term-catalogue')).toHaveCount(0)
})

for (const { rule, definition } of malformed) {
  test(`Open file reports ${rule} in Findings and import/validate @row:${rule}${rule === 'L1' ? ' @row:definition-unreadable' : ''}`, async ({ page, harness }) => {
    await openPlayground(page, harness.pageUrl)
    const response = await openFileAndValidate(page, `${rule}.json`, dto(definition))

    expect(response.status()).toBe(400)
    expect((await response.json() as { detail?: string }).detail).toContain(`rule ${rule}`)
    const findings = page.getByRole('list', { name: 'Findings' })
    await expect(findings.locator('[data-code="definition-unreadable"]')).toHaveCount(1)
    await expect(findings.locator('[data-code="definition-unreadable"]')).toContainText(`(rule ${rule},`)
    await expect(findings.locator(`[data-code="${rule}"]`)).toHaveCount(0)
    await expect(page.getByTestId('status')).toContainText(`import/validate:`)
  })
}

test('Open file reports an unreadable signature @row:signature-unreadable', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  const response = await openFileAndValidate(page, 'signature.json', dto('{"nodes":[],"edges":[]}', {
    inputParametersJson: '[{"name":"Value","type":7}]',
  }))
  expect(response.status()).toBe(400)
  const findings = page.getByRole('list', { name: 'Findings' })
  await expect(findings.locator('[data-code="signature-unreadable"]')).toHaveCount(1)
  await expect(page.getByTestId('status')).toContainText('import/validate:')
})

test('Open file reports dotted names and import/validate refuses them @row:dotted-name @row:node.id#dotted', async ({ page, harness }) => {
  await openPlayground(page, harness.pageUrl)
  const cases = documentCorpus.cases.filter((item) => item.fault?.rule === 'dotted-name')
  expect(cases.length).toBeGreaterThan(0)
  for (const item of cases) {
    await test.step(item.name, async () => {
      const fault = item.fault!
      const contents = item.reads === 'parameters'
        ? dto('{"nodes":[],"edges":[]}', { inputParametersJson: item.text })
        : dto(item.text)
      const response = await openFileAndValidate(page, `${item.name}.json`, contents)
      expect(response.status()).toBe(400)
      const problem = await response.json() as { detail?: string }
      expect(problem.detail).toContain('rule dotted-name')
      expect(problem.detail).toContain(fault.location)
      const finding = page.getByRole('list', { name: 'Findings' }).locator('[data-code="dotted-name"]')
      await expect(finding).toHaveCount(1)
      await expect(finding).toContainText('rule dotted-name')
      await expect(finding).toContainText(fault.location)
      await expect(finding).toContainText(fault.detail)
      await expect(page.getByTestId('status')).toContainText('import/validate:')
    })
  }
})

test('Open file finds duplicate node ids and Save refuses them @row:duplicate-node-id', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Open duplicate node ids')
  await openFileAndValidate(page, 'duplicates.json', dto('{"nodes":['
    + '{"id":"same","blockTypeId":"Expression","position":{"x":300,"y":100},"configuration":{}},'
    + '{"id":"same","blockTypeId":"Expression","position":{"x":600,"y":100},"configuration":{}}],"edges":[]}'))
  await expect(page.getByRole('list', { name: 'Findings' }).locator('[data-code="duplicate-node-id"]')).toBeVisible()
  const writes: string[] = []
  page.on('request', (request) => { if (request.method() === 'PUT') writes.push(request.url()) })
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByTestId('status')).toContainText('Saving: The workflow definition cannot be read, so it cannot be saved')
  expect(writes).toEqual([])
})

test('Open file reports an overlong node id at Publish @row:node-id-too-long', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Open long node id')
  const longId = 'n'.repeat(101)
  const response = await openFileAndValidate(page, 'long-node-id.json', dto(JSON.stringify({
    nodes: [{ id: longId, blockTypeId: 'Expression', position: { x: 300, y: 100 }, configuration: {} }], edges: [],
  })))
  await refuseOnOpen(page, response, 'node-id-too-long')
  await refuseOnPublish(page, 'node-id-too-long')
})

test('Open file reports an unknown block type and Publish refuses it @row:block-type-unknown', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Open unknown block')
  const unknownType = 'MissingFromCatalogue'
  const response = await openFileAndValidate(page, 'unknown-block.json', dto(JSON.stringify({
    nodes: [
      { id: '__workflow_input__', blockTypeId: 'WorkflowInput', position: { x: 50, y: 100 }, configuration: {} },
      { id: 'unknown-node', blockTypeId: unknownType, position: { x: 300, y: 100 }, configuration: {} },
      { id: '__workflow_output__', blockTypeId: 'WorkflowOutput', position: { x: 800, y: 100 }, configuration: {} },
    ],
    edges: [
      { id: 'in', sourceNodeId: '__workflow_input__', targetNodeId: 'unknown-node', mappings: {} },
      { id: 'out', sourceNodeId: 'unknown-node', targetNodeId: '__workflow_output__', mappings: {} },
    ],
  })))
  expect(response.status()).toBe(200)
  const validation = await response.json() as { missingBlockTypeIds: readonly string[] }
  expect(validation.missingBlockTypeIds).toContain(unknownType)
  await refuseOnPublish(page, 'block-type-unknown')
})

test('Open file reports a node with no blockTypeId and Publish refuses it @row:node.blockTypeId#absent', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Open node without block type')
  await openFileAndValidate(page, 'node-without-block-type.json', dto(JSON.stringify({
    nodes: [{ id: 'missing-type', position: { x: 300, y: 100 }, configuration: {} }], edges: [],
  })))
  await refuseOnPublish(page, 'block-type-unknown')
})

test('Open file imports malformed port-type corpus cases and Publish refuses them @row:declared-output.type#null @row:declared-output.type#absent @row:parameter.type#null @row:parameter.type#absent @row:port-entry.type#null @row:port-entry.type#absent', async ({ page, harness }) => {
  const cases = [
    { row: 'declared-output.type#null', corpus: 'declared-output-type-null-corpus' },
    { row: 'declared-output.type#absent', corpus: 'declared-output-type-absent-corpus' },
    { row: 'port-entry.type#null', corpus: 'port-entry-type-null-corpus' },
    { row: 'port-entry.type#absent', corpus: 'port-entry-type-absent-corpus' },
    { row: 'parameter.type#null', corpus: 'parameter-type-null-corpus' },
    { row: 'parameter.type#absent', corpus: 'parameter-type-absent-corpus' },
  ] as const
  for (const item of cases) {
    const corpusEntry = corpusCase(item.corpus)
    await createWorkflow(page, harness.pageUrl, `Opened ${item.row}`)
    const response = await openFileAndValidate(page, `${item.row}.json`, dto(JSON.stringify(corpusEntry.definition), {
      inputParametersJson: corpusEntry.signature === undefined ? null : JSON.stringify(corpusEntry.signature.inputs),
    }))
    expect(response.status()).toBe(400)
    const detail = (await response.json() as { detail?: string }).detail ?? ''
    expect(detail, `Malformed port type refusal for ${item.row}`).toContain('malformed type')
    await refuseOnPublish(page, 'port-type-malformed')
  }
})

test('Open file imports vocabulary corpus cases and Publish refuses them @row:declared-output.type#unregistered @row:port-entry.type#unregistered @row:parameter.type#unregistered @row:port-type-unregistered', async ({ page, harness }) => {
  const cases = [
    { row: 'declared-output.type#unregistered', corpus: 'declared-output-type-unregistered-corpus', rule: 'port-type-unregistered' },
    { row: 'port-entry.type#unregistered', corpus: 'port-entry-type-unregistered-corpus', rule: 'port-type-unregistered' },
    { row: 'parameter.type#unregistered', corpus: 'parameter-type-unregistered-corpus', rule: 'port-type-unregistered' },
  ] as const
  for (const item of cases) {
    const corpusEntry = corpusCase(item.corpus)
    await createWorkflow(page, harness.pageUrl, `Opened ${item.row}`)
    const response = await openFileAndValidate(page, `${item.row}.json`, dto(JSON.stringify(corpusEntry.definition), {
      inputParametersJson: corpusEntry.signature === undefined ? null : JSON.stringify(corpusEntry.signature.inputs),
    }))
    await refuseOnOpen(page, response, item.rule)
    await refuseOnPublish(page, item.rule)
  }
})

test('Open file imports alias corpus cases and refuses them @row:declared-output.type#alias @row:port-entry.type#alias @row:parameter.type#alias @row:port-type-alias', async ({ page, harness }) => {
  const cases = [
    { row: 'declared-output.type#alias', corpus: 'declared-output-type-alias-corpus', publishes: false },
    { row: 'port-entry.type#alias', corpus: 'port-entry-type-alias-corpus', publishes: true },
    { row: 'parameter.type#alias', corpus: 'parameter-type-alias-corpus', publishes: false },
  ] as const
  for (const item of cases) {
    const corpusEntry = corpusCase(item.corpus)
    await createWorkflow(page, harness.pageUrl, `Opened ${item.row}`)
    const response = await openFileAndValidate(page, `${item.row}.json`, dto(JSON.stringify(corpusEntry.definition), {
      inputParametersJson: corpusEntry.signature === undefined ? null : JSON.stringify(corpusEntry.signature.inputs),
    }))
    await refuseOnOpen(page, response, 'port-type-alias')
    if (item.publishes) await refuseOnPublish(page, 'port-type-alias')
  }
})

test('Open file imports Collection<Any> corpus cases and refuses them @row:declared-output.type#collection-of-any @row:port-entry.type#collection-of-any @row:parameter.type#collection-of-any', async ({ page, harness }) => {
  const cases = [
    { row: 'declared-output.type#collection-of-any', corpus: 'declared-output-type-alias-corpus' },
    { row: 'port-entry.type#collection-of-any', corpus: 'port-entry-type-alias-corpus' },
    { row: 'parameter.type#collection-of-any', corpus: 'parameter-type-alias-corpus' },
  ] as const
  const spelled = (text: string) => text.replaceAll('"type":"string"', '"type":"Collection<Any>"').replaceAll('\\"type\\":\\"string\\"', '\\"type\\":\\"Collection<Any>\\"')
  for (const item of cases) {
    const corpusEntry = corpusCase(item.corpus)
    await createWorkflow(page, harness.pageUrl, `Opened ${item.row}`)
    const definitionJson = spelled(JSON.stringify(corpusEntry.definition))
    const inputsJson = corpusEntry.signature === undefined ? null : spelled(JSON.stringify(corpusEntry.signature.inputs))
    expect(`${definitionJson}${inputsJson ?? ''}`).toContain('Collection<Any>')
    const response = await openFileAndValidate(page, `${item.row}.json`, dto(definitionJson, { inputParametersJson: inputsJson }))
    await refuseOnOpen(page, response, 'port-type-alias')
  }
})

test('Open file refuses an export that spells Any or Float, and shows it as written', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Refused Any and Float export')
  const definition = JSON.stringify({
    nodes: [
      { id: 'start', blockTypeId: 'WorkflowInput', position: { x: 50, y: 100 }, configuration: {} },
      { id: 'a', blockTypeId: 'dispatch.echo-json', position: { x: 300, y: 100 }, configuration: { InputPorts: '[{"name":"payload","type":"Any"}]', OutputPorts: '[]' } },
    ],
    edges: [{ id: 'to-a', sourceNodeId: 'start', targetNodeId: 'a', mappings: {} }],
  })
  const inputs = JSON.stringify([{ name: 'Seed', displayName: 'Seed', isRequired: true, type: 'Float' }])
  const response = await openFileAndValidate(page, 'refused-any-float.json', dto(definition, { inputParametersJson: inputs }))
  await refuseOnOpen(page, response, 'port-type-alias')
  await page.getByTestId('workflow-node-dispatch.echo-json').click()
  await expect(page.getByLabel('Port 1 type', { exact: true })).toContainText('Any')
})

test('Open file returns a current export unchanged', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened current export')
  const definition = JSON.stringify({
    nodes: [
      { id: 'start', blockTypeId: 'WorkflowInput', position: { x: 0, y: 0 }, configuration: {} },
      { id: 'wait', blockTypeId: 'WaitForDuration', position: { x: 240, y: 0 }, configuration: { DurationMs: 5 } },
    ],
    edges: [{ id: 'w', sourceNodeId: 'start', targetNodeId: 'wait', mappings: {} }],
  })
  const inputs = JSON.stringify([{ name: 'Seed', displayName: 'Seed', isRequired: true, type: 'Number' }])
  const response = await openFileAndValidate(page, 'current-export.json', dto(definition, { inputParametersJson: inputs }))
  expect(response.status()).toBe(200)
  const answer = await response.json() as { inputParametersJson: string; definitionJson: string }
  expect(answer.definitionJson).toBe(definition)
  expect(answer.inputParametersJson).toBe(inputs)
})

test('Open file refuses workflow row settings the run would read as something else @row:workflow-row.ExecutionTimeoutSeconds#zero-or-negative @row:workflow-row.RetryPolicyJson#malformed @row:workflow-row.RetryPolicyJson#backoff-strategy-other @row:workflow-row.InputParametersJson#blank-or-null @row:workflow-row.OutputParametersJson#blank-or-null', async ({ page, harness }) => {
  const definition = JSON.stringify({ nodes: [], edges: [] })
  const cases = [
    { overrides: { executionTimeoutSeconds: 0 }, refusal: 'ExecutionTimeoutSeconds' },
    { overrides: { executionTimeoutSeconds: -3 }, refusal: 'ExecutionTimeoutSeconds' },
    { overrides: { retryPolicyJson: '{"maxAttempts":' }, refusal: 'not a retry policy' },
    { overrides: { retryPolicyJson: '{"backoffStrategy":"Exponential"}' }, refusal: "backoff strategy 'Exponential'" },
    { overrides: { inputParametersJson: '' }, refusal: 'parameter list is blank' },
    { overrides: { inputParametersJson: 'null' }, refusal: 'parameter list is the JSON null' },
    { overrides: { outputParametersJson: ' ' }, refusal: 'parameter list is blank' },
  ] as const
  await openPlayground(page, harness.pageUrl)
  for (const item of cases) {
    const response = await openFileAndValidate(page, 'row-settings.json', dto(definition, item.overrides))
    expect(response.status()).toBe(400)
    expect((await response.json() as { detail?: string }).detail).toContain(item.refusal)
  }
})

test('Open file imports type parameter corpus cases and Publish refuses them @row:type-parameter-unbound @row:type-parameter-conflict', async ({ page, harness }) => {
  const cases = [
    { corpus: 'type-parameter-unbound-absent-literal-corpus', rule: 'type-parameter-unbound' },
    { corpus: 'type-parameter-conflict-mixed-literal-corpus', rule: 'type-parameter-conflict' },
  ] as const
  for (const item of cases) {
    await createWorkflow(page, harness.pageUrl, `Opened ${item.rule}`)
    const corpusEntry = corpusCase(item.corpus)
    const response = await openFileAndValidate(page, `${item.corpus}.json`, dto(JSON.stringify(corpusEntry.definition)))
    await refuseOnOpen(page, response, item.rule)
    await refuseOnPublish(page, item.rule)
  }
})

test('Open file reports an overlong block type id at Publish @row:block-type-id-too-long', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Open long block type id')
  const response = await openFileAndValidate(page, 'long-block-type-id.json', dto(JSON.stringify({
    nodes: [{ id: 'long-type-node', blockTypeId: 'B'.repeat(101), position: { x: 300, y: 100 }, configuration: {} }], edges: [],
  })))
  await refuseOnOpen(page, response, 'block-type-id-too-long')
  await refuseOnPublish(page, 'block-type-id-too-long')
})

const wireStart = { id: 'start', blockTypeId: 'WorkflowInput', position: { x: 50, y: 100 }, configuration: {} }
const wireFromStart = [{ id: 'in', sourceNodeId: 'start', targetNodeId: 'a', mappings: {} }]

test('Open file refuses a port list on a block without dynamic ports at both doors @row:node.configuration#value-port-list-without-dynamic-ports @row:port-list-without-dynamic-ports', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened port list without dynamic ports')
  const response = await openFileAndValidate(page, 'port-list-without-dynamic-ports.json', dto(JSON.stringify({
    nodes: [wireStart, { id: 'a', blockTypeId: 'Expression', position: { x: 300, y: 100 }, configuration: { InputPorts: '[{"name":"P","type":"String"}]' } }],
    edges: wireFromStart,
  })))
  await refuseOnOpen(page, response, 'port-list-without-dynamic-ports')
  await refuseOnPublish(page, 'port-list-without-dynamic-ports')
})

test('Open file refuses declared outputs on a block that supports none at both doors @row:node.outputPorts#unlisted-port @row:declared-outputs-unsupported', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened declared outputs unsupported')
  const response = await openFileAndValidate(page, 'declared-outputs-unsupported.json', dto(JSON.stringify({
    nodes: [wireStart, {
      id: 'a', blockTypeId: 'Expression', position: { x: 300, y: 100 }, configuration: {},
      outputPorts: [{ name: 'Extra', displayName: 'Extra', type: 'String' }],
    }],
    edges: wireFromStart,
  })))
  await refuseOnOpen(page, response, 'declared-outputs-unsupported')
  await refuseOnPublish(page, 'declared-outputs-unsupported')
})

test("Open file refuses an error policy on a workflow's entry or exit node at both doors @row:error-policy-on-boundary-node", async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened error policy on boundary node')
  const response = await openFileAndValidate(page, 'error-policy-on-boundary-node.json', dto(JSON.stringify({
    nodes: [
      { ...wireStart, errorPolicy: { policy: 'Skip' } },
      { id: 'a', blockTypeId: 'Expression', position: { x: 300, y: 100 }, configuration: {} },
      { id: 'finish', blockTypeId: 'WorkflowOutput', position: { x: 550, y: 100 }, configuration: {}, errorPolicy: { policy: 'EmitError' } },
    ],
    edges: wireFromStart,
  })))
  await refuseOnOpen(page, response, 'error-policy-on-boundary-node')
  await refuseOnPublish(page, 'error-policy-on-boundary-node')
})

const echo = (configuration: Record<string, unknown>, extra: Record<string, unknown> = {}) => ({
  id: 'a', blockTypeId: 'dispatch.echo-json', position: { x: 300, y: 100 }, configuration: { InputPorts: '[]', OutputPorts: '[]', ...configuration }, ...extra,
})
const countOutput = '[{"name":"Count","type":"Int"}]'

test("Open file shows the server's finding for a default output value of the wrong term @row:literal-port-value-invalid", async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened default output value')
  const response = await openFileAndValidate(page, 'default-output-value.json', dto(JSON.stringify({
    nodes: [wireStart, echo({ OutputPorts: countOutput }, { errorPolicy: { policy: 'ContinueWithDefault', defaultOutputJson: '{"Count":"five"}' } })],
    edges: wireFromStart,
  })))
  const at = { nodeId: 'a', portName: 'Count' }
  expect((await refuseOnOpen(page, response, 'literal-port-value-invalid', at)).message).toBe("Default output 'Count' of 'Echo JSON' cannot be read as Int.")
  expect((await refuseOnPublish(page, 'literal-port-value-invalid', at)).message).toBe("Default output 'Count' of 'Echo JSON' cannot be read as Int.")
})

test("Open file shows the server's finding for a default output key that names no output @row:wire-port-unknown", async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened default output key')
  const response = await openFileAndValidate(page, 'default-output-key.json', dto(JSON.stringify({
    nodes: [wireStart, echo({ OutputPorts: countOutput }, { errorPolicy: { policy: 'ContinueWithDefault', defaultOutputJson: '{"Nope":1}' } })],
    edges: wireFromStart,
  })))
  const at = { nodeId: 'a', portName: 'Nope' }
  expect((await refuseOnOpen(page, response, 'wire-port-unknown', at)).message).toBe("Node 'Echo JSON' has no output 'Nope' for its default output to give")
  expect((await refuseOnPublish(page, 'wire-port-unknown', at)).message).toBe("Node 'Echo JSON' has no output 'Nope' for its default output to give")
})

test("Open file shows the server's finding for a DateTime literal that is a string but not a date @row:literal-port-value-invalid", async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened date time literal')
  const response = await openFileAndValidate(page, 'date-time-literal.json', dto(JSON.stringify({
    nodes: [wireStart, echo({ InputPorts: '[{"name":"At","type":"DateTime"}]' })],
    edges: [{ id: 'in', sourceNodeId: 'start', targetNodeId: 'a', mappings: { At: { type: 'literal', literalValue: 'not-a-date' } } }],
  })))
  const at = { nodeId: 'a', portName: 'At' }
  expect((await refuseOnOpen(page, response, 'literal-port-value-invalid', at)).message).toBe("Literal for input 'At' of 'Echo JSON' cannot be read as DateTime.")
  expect((await refuseOnPublish(page, 'literal-port-value-invalid', at)).message).toBe("Literal for input 'At' of 'Echo JSON' cannot be read as DateTime.")
})

test("Open file shows the server's finding for a passthrough configuration value of the wrong term @row:literal-port-value-invalid", async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened passthrough configuration value')
  const response = await openFileAndValidate(page, 'passthrough-config.json', dto(JSON.stringify({
    nodes: [
      wireStart, echo({ OutputPorts: countOutput }),
      { ...echo({ Count: 'abc' }), id: 'b', position: { x: 550, y: 100 }, passthrough: ['Count'] },
    ],
    edges: [...wireFromStart, { id: 'on', sourceNodeId: 'a', targetNodeId: 'b', mappings: { Count: { type: 'direct', sourceOutputName: 'Count' } } }],
  })))
  const at = { nodeId: 'b', portName: 'Count' }
  expect((await refuseOnOpen(page, response, 'literal-port-value-invalid', at)).message).toBe("Configuration value for input 'Count' of 'Echo JSON (b)' cannot be read as Int.")
  expect((await refuseOnPublish(page, 'literal-port-value-invalid', at)).message).toBe("Configuration value for input 'Count' of 'Echo JSON (b)' cannot be read as Int.")
})

test('Open file refuses a Headers array of field lines, which no rule rewrites, at both doors', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened Headers field lines')
  const response = await openFileAndValidate(page, 'headers-field-lines.json', dto(JSON.stringify({
    nodes: [wireStart, { id: 'a', blockTypeId: 'HttpRequest', position: { x: 300, y: 100 }, configuration: { Url: 'https://example.test', Headers: ['X-Key: 1'] } }],
    edges: wireFromStart,
  })))
  await refuseOnOpen(page, response, 'literal-port-value-invalid')
  await refuseOnPublish(page, 'literal-port-value-invalid')
})

test('Open file refuses a declared output sourced from a branch the block lacks at both doors @row:declared-output.sources#unlisted-key @row:declared-output-source-unknown', async ({ page, harness }) => {
  await createWorkflow(page, harness.pageUrl, 'Opened declared output source unknown')
  const response = await openFileAndValidate(page, 'declared-output-source-unknown.json', dto(JSON.stringify({
    nodes: [wireStart, {
      id: 'a', blockTypeId: 'IfElse', position: { x: 300, y: 100 }, configuration: {},
      outputPorts: [{ name: 'Result', displayName: 'Result', type: 'String', sources: { Nowhere: 'k' } }],
    }],
    edges: wireFromStart,
  })))
  await refuseOnOpen(page, response, 'declared-output-source-unknown')
  await refuseOnPublish(page, 'declared-output-source-unknown')
})

const echoPorts = { InputPorts: '[{"name":"p","type":"Int"}]', OutputPorts: '[{"name":"p","type":"Int"}]' }
const echoNodes = (...extra: unknown[]) => [
  { id: 'start', blockTypeId: 'WorkflowInput', position: { x: 50, y: 100 }, configuration: {} },
  { id: 'a', blockTypeId: 'dispatch.echo-json', position: { x: 300, y: 100 }, configuration: echoPorts },
  ...extra,
]
interface StoredEdge { targetNodeId: string; mappings: Record<string, Record<string, unknown>> }
interface StoredDefinition { edges: StoredEdge[] }
const rewrittenForms: ReadonlyArray<{ title: string; definition: unknown; inputs: string | null; outputs: string | null; expectStored: (stored: StoredDefinition) => void }> = [
  {
    title: 'a literal mapping in the object form with a canonical type',
    definition: { nodes: echoNodes(), edges: [{ id: 'e', sourceNodeId: 'start', targetNodeId: 'a', mappings: { p: { type: 'literal', literalValue: 7 } } }] },
    inputs: null, outputs: null,
    expectStored: (stored) => { expect(stored.edges[0]?.mappings['p']).toMatchObject({ type: 'literal', literalValue: 7 }) },
  },
  {
    title: 'a direct mapping named by sourceOutputName',
    definition: {
      nodes: echoNodes(), edges: [{ id: 'e', sourceNodeId: 'start', targetNodeId: 'a', mappings: { p: { type: 'direct', sourceOutputName: 'p' } } }],
    },
    inputs: '[{"name":"p","type":"Int"}]', outputs: null,
    expectStored: (stored) => { expect(stored.edges[0]?.mappings['p']).toMatchObject({ type: 'direct', sourceOutputName: 'p' }) },
  },
  {
    title: 'a workflow output wired into the exit node',
    definition: {
      nodes: echoNodes({ id: 'out', blockTypeId: 'WorkflowOutput', position: { x: 600, y: 100 }, configuration: {} }),
      edges: [
        { id: 'e1', sourceNodeId: 'start', targetNodeId: 'a', mappings: {} },
        { id: 'e2', sourceNodeId: 'a', targetNodeId: 'out', mappings: { result: { type: 'direct', sourceOutputName: 'p' } } },
      ],
    },
    inputs: null, outputs: '[{"name":"result","type":"Int"}]',
    expectStored: (stored) => { expect(stored.edges.find((edge) => edge.targetNodeId === 'out')?.mappings['result']).toMatchObject({ sourceOutputName: 'p' }) },
  },
  {
    title: 'empty edges and no parameter lists',
    definition: { nodes: [wireStart], edges: [] },
    inputs: null, outputs: null,
    expectStored: (stored) => { expect(stored.edges).toEqual([]) },
  },
]

for (const form of rewrittenForms) {
  test(`Open file reads the migration's rewritten form of ${form.title} and Publish accepts it`, async ({ page, harness }) => {
    await createWorkflow(page, harness.pageUrl, 'Rewritten form')
    const savedName = `Rewritten ${form.title} ${String(Date.now())}`
    const response = await openFileAndValidate(page, 'rewritten.json', dto(JSON.stringify(form.definition), {
      name: savedName, inputParametersJson: form.inputs, outputParametersJson: form.outputs,
    }))
    expect(response.status(), await response.text()).toBe(200)
    await publishWorkflowRequest(page)
    form.expectStored(JSON.parse((await latestVersionForName(harness, savedName)).definitionJson) as StoredDefinition)
  })
}
