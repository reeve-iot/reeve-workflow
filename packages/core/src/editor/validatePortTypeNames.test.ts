import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { ValidationFinding, WorkflowDefinition, WorkflowNode, WorkflowParameter } from '../model/workflow.js'
import { WorkflowDocumentUnreadableError, createWorkflowEditorStore } from './store.js'
import { readParameterList, readStoredDefinition, readStoredDefinitionText } from '../model/storedDocument.js'
import { validationMessage } from './messageCatalogue.js'
import { validatePortTypeNames } from './validatePortTypeNames.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'

const blockType = (blockTypeId: string, overrides: Partial<BlockTypeDescriptor> = {}): BlockTypeDescriptor => ({
  blockTypeId,
  displayName: blockTypeId,
  inputs: [],
  outputs: [],
  ...overrides,
})

const DYNAMIC = blockType('Script', { hasDynamicPorts: true })
const BRANCHING = blockType('Branching', { subCanvasNames: ['Body'], supportsDeclaredOutputs: true })

const node = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode => ({
  id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra,
})

const dynamic = (id: string, inputs: unknown[], outputs: unknown[] = []): WorkflowNode =>
  node(id, 'Script', { configuration: { InputPorts: JSON.stringify(inputs), OutputPorts: JSON.stringify(outputs) } })

const param = (name: string, type: unknown): WorkflowParameter =>
  ({ name, displayName: name, isRequired: false, type }) as WorkflowParameter

const validate = (
  definition: WorkflowDefinition,
  params: { inputs?: WorkflowParameter[]; outputs?: WorkflowParameter[] } = {},
) => validatePortTypeNames({
  definition,
  workflowInputParams: params.inputs ?? [],
  workflowOutputParams: params.outputs ?? [],
})

const generic = blockType('Generic', { typeParameters: [{ name: 'T' }] })
const genericFinding = (term: string, key = 'T') => validatePortTypeNames({
  definition: { nodes: [node('n', 'Generic', { typeArguments: { [key]: term } })], edges: [] },
  workflowInputParams: [], workflowOutputParams: [], blockTypes: [generic],
})

describe('validatePortTypeNames', () => {
  it('grades malformed type arguments but leaves vocabulary findings to the compiler', () => {
    expect(genericFinding('Int<').map((finding) => finding.ruleId)).toContain('port-type-malformed')
    expect(genericFinding('device').map((finding) => finding.ruleId)).not.toContain('port-type-alias')
    expect(genericFinding('Unregistered').map((finding) => finding.ruleId)).not.toContain('port-type-unregistered')
  })

  it('leaves Json-position grading to the compiler', () => {
    expect(genericFinding('Json').map((finding) => finding.ruleId)).not.toContain('port-type-json-position')
  })

  it('reports a type argument key that the block does not declare', () => {
    expect(genericFinding('Int', 'Unknown').map((finding) => finding.ruleId)).toContain('type-argument-unknown')
  })

  it('does not report a declared type argument key as unknown', () => {
    expect(genericFinding('Int').map((finding) => finding.ruleId)).not.toContain('type-argument-unknown')
  })

  it('reports a malformed workflow input parameter, with the hint to write Collection<T>', () => {
    expect(validate({ nodes: [], edges: [] }, { inputs: [param('Rooms', 'CollectionRoom')] })).toEqual([{
      canvasPath: [],
      nodeId: null,
      ruleId: 'port-type-malformed',
      code: 'port-type-malformed',
      params: { portName: 'Rooms', rawType: 'CollectionRoom', detail: expect.stringContaining("'CollectionRoom'") as unknown, origin: 'a workflow input parameter' },
      portName: 'Rooms',
      message: "Port type of 'Rooms' is not a well-formed name: 'CollectionRoom' is not a port type name: names " +
        "starting with 'collection' are reserved at 0. write Collection<T> with the type of its elements. " +
        'It is a workflow input parameter.',
    }])
  })

  it('reports a map whose key is not String as port-type-map-key, and any other map fault as malformed', () => {
    const at = (type: string) => validate({ nodes: [], edges: [] }, { inputs: [param('Headers', type)] })
    expect(at('Map<Int,String>')).toEqual([expect.objectContaining({
      ruleId: 'port-type-map-key',
      code: 'port-type-map-key',
      portName: 'Headers',
      message: "Port type of 'Headers' has a Map type whose key is not String: 'Map<Int,String>' is not a port type name: " +
        'the key of a Map must be String at 4. It is a workflow input parameter.',
    })])
    expect(at('Map<String?,Int>').map((f) => f.ruleId)).toEqual(['port-type-map-key'])
    for (const malformed of ['Map', 'Map<String>', 'Map<String,Int,Int>', 'Gauge<Int,Int>']) {
      expect(at(malformed).map((f) => f.ruleId), malformed).toEqual(['port-type-malformed'])
    }
    for (const fine of ['Map<String,Int>', 'Map<string,Collection<Int>>?']) expect(at(fine), fine).toEqual([])
  })

  it('gives every refused Map row of the grammar corpus the rule its fault belongs to', () => {
    const unescape = (field: string): string => field.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
    const rows = readFileSync(resolve(__dirname, '../../../../contracts/port-type-names.tsv'), 'utf8').split('\n').flatMap((text) => {
      const [name = '', kind, a = '', , c = ''] = text.split('\t')
      if (kind === 'map:refused' || kind === 'map:syntax') return [{ name: unescape(name), fault: a }]
      return kind === 'map:declrefused' ? [{ name: unescape(name), fault: c }] : []
    })
    const wrong = rows.flatMap(({ name, fault }) => {
      const expected = fault === 'MapKey' ? ['port-type-map-key'] : ['port-type-malformed']
      const actual = validate({ nodes: [], edges: [] }, { inputs: [param('Headers', name)] }).map((f) => f.ruleId)
      return JSON.stringify(actual) === JSON.stringify(expected) ? [] : [{ name, expected, actual }]
    })
    expect(rows.length).toBeGreaterThan(150)
    expect(rows.some((row) => row.fault === 'MapKey')).toBe(true)
    expect(wrong).toEqual([])
  })

  it('reports a workflow output parameter with no type as missing, never as Any', () => {
    const findings = validate({ nodes: [], edges: [] }, { outputs: [param('Result', undefined)] })
    expect(findings.map((f) => f.message)).toEqual([
      "Port type of 'Result' is not a well-formed name: '' is not a port type name: the type is missing at 0. " +
        'It is a workflow output parameter.',
    ])
  })

  it('reports a dynamic input port with a blank type by its node', () => {
    const findings = validate({ nodes: [dynamic('s', [{ name: 'A', type: '' }])], edges: [] })
    expect(findings).toEqual([{
      canvasPath: [],
      nodeId: 's',
      ruleId: 'port-type-malformed',
      code: 'port-type-malformed',
      params: { portName: 'A', rawType: '', detail: expect.stringContaining('type is blank') as unknown, origin: "a dynamic input port of node 's'" },
      portName: 'A',
      message: "Port type of 'A' is not a well-formed name: '' is not a port type name: the type is blank at 0. " +
        "It is a dynamic input port of node 's'.",
    }])
  })

  it('reports a type that is not a string as one unreadable document, as the backend does (rule L7)', () => {
    const findings = validate({ nodes: [dynamic('s', [{ name: 'A', type: '' }, { name: 'B', type: 5 }])], edges: [] })
    expect(findings).toEqual([{
      canvasPath: [],
      nodeId: 's',
      ruleId: 'definition-unreadable',
      code: 'definition-unreadable',
      params: { subject: 'The workflow definition', fault: expect.stringContaining('rule L7') as unknown },
      message: 'The workflow definition cannot be read: a number where a port type name is required ' +
        "(rule L7, at '/nodes/0/configuration/InputPorts#/1/type').",
    }])
  })

  it('reports a dynamic output port with no type as missing', () => {
    const findings = validate({ nodes: [dynamic('s', [], [{ name: 'Out' }])], edges: [] })
    expect(findings.map((f) => f.message)).toEqual([
      "Port type of 'Out' is not a well-formed name: '' is not a port type name: the type is missing at 0. " +
        "It is a dynamic output port of node 's'.",
    ])
  })

  it('reports a declared output with an unclosed bracket', () => {
    const findings = validate({
      nodes: [node('b', 'Branching', { outputPorts: [{ name: 'Items', displayName: 'Items', type: 'Collection<Foo' }] })],
      edges: [],
    })
    expect(findings).toEqual([{
      canvasPath: [],
      nodeId: 'b',
      ruleId: 'port-type-malformed',
      code: 'port-type-malformed',
      params: { portName: 'Items', rawType: 'Collection<Foo', detail: expect.stringContaining("closing '>'") as unknown, origin: "a declared output of node 'b'" },
      portName: 'Items',
      message: "Port type of 'Items' is not a well-formed name: 'Collection<Foo' is not a port type name: a closing " +
        "'>' was expected at 14. It is a declared output of node 'b'.",
    }])
  })

  it('reports a malformed name inside a sub-canvas, at any depth', () => {
    const inner = node('inner', 'Branching', { outputPorts: [{ name: 'X', displayName: 'X', type: 'Pt ' }] })
    const middle = node('middle', 'Branching', { subCanvases: { Body: { nodes: [dynamic('deep', [{ name: 'In', type: 'Map<K,V>' }]), inner], edges: [] } } })
    const outer = node('outer', 'Branching', { subCanvases: { Body: { nodes: [middle], edges: [] } } })
    const down = [{ nodeId: 'outer', branchName: 'Body' }, { nodeId: 'middle', branchName: 'Body' }]
    expect(validate({ nodes: [outer], edges: [] }).map((f) => [f.nodeId, f.canvasPath])).toEqual([['deep', down], ['inner', down]])
  })

  it('names the canvas of a finding by the branch as written, a slash or a tilde in it included', () => {
    const inner = node('inner', 'Branching', { outputPorts: [{ name: 'X', displayName: 'X', type: 'Pt ' }] })
    const outer = node('outer', 'Branching', { subCanvases: { 'a/b~c': { nodes: [inner], edges: [] } } })
    expect(validate({ nodes: [node('first', 'Fixed'), outer], edges: [] }).map((f) => f.canvasPath))
      .toEqual([[{ nodeId: 'outer', branchName: 'a/b~c' }]])
  })

  it('places a document the reader refuses on the canvas where it stopped reading', () => {
    const outer = node('outer', 'Branching', { subCanvases: { Body: { nodes: [dynamic('s', [5])], edges: [] } } })
    expect(validate({ nodes: [outer], edges: [] }).map((f) => [f.nodeId, f.canvasPath]))
      .toEqual([['s', [{ nodeId: 'outer', branchName: 'Body' }]]])
  })

  it('accepts every well-formed name, a type this build never heard of included', () => {
    const findings = validate(
      {
        nodes: [
          dynamic('s', [{ name: 'A', type: 'number' }], [{ name: 'B', type: 'Collection<Collection<Thermostat>>' }]),
          node('b', 'Branching', { outputPorts: [{ name: 'O', displayName: 'O', type: 'Json' }] }),
        ],
        edges: [],
      },
      { inputs: [param('I', 'Collection<device>')], outputs: [param('R', 'Thermostat')] },
    )
    expect(findings).toEqual([])
  })

  it('reads InputPorts on every node, a block that declares no dynamic ports included', () => {
    const fixed = node('f', 'Fixed', { configuration: { InputPorts: JSON.stringify([{ name: 'A', type: 'Foo Bar' }]) } })
    expect(validate({ nodes: [fixed], edges: [] }).map((f) => f.nodeId)).toEqual(['f'])
  })

  it('reports a dynamic port entry that is not an object as one unreadable document', () => {
    expect(validate({ nodes: [dynamic('s', [null, 5, 'Int'])], edges: [] })).toEqual([{
      canvasPath: [],
      nodeId: 's',
      ruleId: 'definition-unreadable',
      code: 'definition-unreadable',
      params: { subject: 'The workflow definition', fault: expect.stringContaining('rule L7') as unknown },
      message: "The workflow definition cannot be read: null where a port entry is required (rule L7, at '/nodes/0/configuration/InputPorts#/0').",
    }])
  })

  it('reports a dynamic port name that is not a string as unreadable, never reading it as blank', () => {
    const findings = validate({ nodes: [dynamic('s', [], [{ name: 7, type: 'Int' }])], edges: [] })
    expect(findings).toEqual([{
      canvasPath: [],
      nodeId: 's',
      ruleId: 'definition-unreadable',
      code: 'definition-unreadable',
      params: { subject: 'The workflow definition', fault: expect.stringContaining('rule L7') as unknown },
      message: "The workflow definition cannot be read: a number where a string is required (rule L7, at '/nodes/0/configuration/OutputPorts#/0/name').",
    }])
  })

  it('reports a parameter list it cannot read as one finding for the list', () => {
    const unreadable = validate({ nodes: [], edges: [] }, { outputs: [param('R', 5)] })
    expect(validationMessage(unreadable[0] as ValidationFinding)).toBe(unreadable[0]?.message)
    expect(unreadable).toEqual([{
      canvasPath: [],
      nodeId: null,
      ruleId: 'signature-unreadable',
      code: 'signature-unreadable',
      params: { subject: "The workflow's declared output parameters", fault: expect.stringContaining('rule L7') as unknown },
      message: "The workflow's declared output parameters cannot be read: a number where a port type name is required " +
        "(rule L7, at '/0/type').",
    }])
  })

  it('quotes the name exactly as written, so nothing trimmed or normalised it on the way', () => {
    const findings = validate({ nodes: [], edges: [] }, { inputs: [param('P', ' Device')] })
    expect(findings[0]?.message).toContain("' Device' is not a port type name: an illegal character at 0. white space.")
  })
})

describe('the store', () => {
  const portTypes = createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Device', aliases: ['device'] }])

  const loaded = () => {
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().setBlockTypes([DYNAMIC, BRANCHING])
    store.getState().loadWorkflow({
      nodes: [
        node('__workflow_input__', 'WorkflowInput'),
        dynamic('s', [{ name: 'DynIn', type: 'CollectionDevice' }], [{ name: 'DynOut', type: 'Collection' }]),
        node('b', 'Branching', {
          outputPorts: [{ name: 'Declared', displayName: 'Declared', type: 'collection<Device>' }],
          subCanvases: { Body: { nodes: [dynamic('nested', [], [{ name: 'Deep', type: 'Device>' }])], edges: [] } },
        }),
      ],
      edges: [
        { id: 'e1', sourceNodeId: '__workflow_input__', targetNodeId: 's', mappings: {} },
        { id: 'e2', sourceNodeId: 's', targetNodeId: 'b', mappings: {} },
      ],
    })
    store.getState().setWorkflowParams([param('Param', 'Collection<device')], [])
    return store
  }

  const malformedPorts = (findings: readonly { message: string }[]) =>
    findings.flatMap(({ message }) => /^Port type of '([^']*)' is not a well-formed name:/.exec(message)?.[1] ?? [])

  it('blocks a save with one finding per malformed name, from every origin', () => {
    const findings = loaded().getState().validateWorkflow()
    expect(malformedPorts(findings)).toEqual(['Param', 'DynIn', 'DynOut', 'Declared', 'Deep'])
    for (const finding of findings) expect(validationMessage(finding), finding.code).toBe(finding.message)
  })

  it('makes a loaded document it cannot read the one finding, an empty canvas that refuses to be saved', () => {
    const store = createWorkflowEditorStore({ portTypes })
    const pascal = { Nodes: [node('a', 'Harmless')], edges: [] } as unknown as WorkflowDefinition
    store.getState().loadWorkflow(pascal)
    expect(store.getState().validateWorkflowFindings()).toEqual([{
      nodeId: null,
      ruleId: 'definition-unreadable',
      code: 'definition-unreadable',
      params: { subject: 'The stored workflow', fault: expect.stringContaining('rule L5') as unknown },
      message: "The stored workflow cannot be read: a key 'Nodes' where 'nodes' is meant; keys match exactly (rule L5, at '/Nodes').",
    }])
    expect(store.getState().nodes).toEqual([])
    expect(store.getState().validateWorkflowWarningFindings()).toEqual([])
    store.getState().addNode('Harmless', { x: 0, y: 0 })
    expect(() => store.getState().getWorkflowDefinition()).toThrow(WorkflowDocumentUnreadableError)
    store.getState().loadWorkflow({ nodes: [], edges: [] })
    expect(store.getState().loadedDocumentFault).toBeNull()
    expect(store.getState().getWorkflowDefinition().nodes?.map((n) => n.id)).toEqual(['__workflow_input__'])
  })

  it.each([
    ['nodes that are a number', { nodes: 5 }, 'L7', '/nodes'],
    ['a null node', { nodes: [null] }, 'L7', '/nodes/0'],
    ['a null sub-canvas', { nodes: [{ id: 'p', blockTypeId: 'x', position: { x: 0, y: 0 }, subCanvases: { Body: null } }] }, 'L7', '/nodes/0/subCanvases/Body'],
    ['a document that is null', null, 'L7', ''],
  ])('loads %s as its fault, never throwing', (_, definition, rule, location) => {
    const store = createWorkflowEditorStore({ portTypes })
    expect(() => { store.getState().loadWorkflow(definition as unknown as WorkflowDefinition) }).not.toThrow()
    expect(store.getState().loadedDocumentFault).toMatchObject({ rule, location })
  })

  it('refuses a loaded node carrying an unlisted config beside configuration', () => {
    const store = createWorkflowEditorStore({ portTypes })
    const both = { nodes: [{ ...node('a', 'Script'), config: { Mode: 'x' } }], edges: [] } as unknown as WorkflowDefinition
    store.getState().loadWorkflow(both)
    expect(store.getState().validateWorkflowFindings().map((f) => f.nodeId)).toEqual(['a'])
    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'stored-key-unknown', location: '/nodes/0/config' })
  })

  it('keeps a fault only the text shows when the text is what was read', () => {
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().loadStoredWorkflow(readStoredDefinitionText('{"nodes":[{"id":"a","blockTypeId":"Harmless","blockTypeId":"RunScript"}]}'))
    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'L3', location: '/nodes/0/blockTypeId' })
    expect(store.getState().nodes).toEqual([])
  })

  it('loads an unreadable document over an open, edited sub-canvas as an empty, clean root', () => {
    const store = loaded()
    store.getState().enterSubCanvas('b', 'Body')
    store.getState().addNode('Harmless', { x: 0, y: 0 })
    expect(store.getState().canvasStack).toHaveLength(1)
    expect(store.getState().isDirty).toBe(true)

    store.getState().loadStoredWorkflow(readStoredDefinitionText('{"Nodes":[]}'))

    expect(store.getState()).toMatchObject({
      nodes: [], edges: [], canvasStack: [], selectedNodeId: null, selectedEdgeId: null, isDirty: false,
      loadedDocumentFault: { rule: 'L5', location: '/Nodes' },
    })
  })

  it('refuses a stored-document read handed to loadWorkflow as the wrong-shape object', () => {
    for (const text of ['{"Nodes":[]}', '{"nodes":[{"id":"a","blockTypeId":"Harmless","position":{"x":0,"y":0}}]}']) {
      const read = readStoredDefinitionText(text)
      const misused = createWorkflowEditorStore({ portTypes })
      misused.getState().loadWorkflow(read as unknown as WorkflowDefinition)
      expect(misused.getState().loadedDocumentFault, text).toMatchObject({ rule: 'stored-key-unknown' })
      expect(() => misused.getState().getWorkflowDefinition(), text).toThrow(WorkflowDocumentUnreadableError)

      const used = createWorkflowEditorStore({ portTypes })
      used.getState().loadStoredWorkflow(read)
      if (read.ok) expect(used.getState().nodes.map((n) => n.id)).toContain('a')
      else expect(() => used.getState().getWorkflowDefinition()).toThrow(WorkflowDocumentUnreadableError)
    }
  })

  it('adds no entry or exit node to the canvas of a document that could not be read', () => {
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().setWorkflowParams([{ name: 'In', type: 'Int' } as WorkflowParameter], [{ name: 'Out', type: 'Int' } as WorkflowParameter])
    store.getState().loadStoredWorkflow(readStoredDefinitionText('{"Nodes":[]}'))
    store.getState().syncSpecialNodes()
    store.getState().setWorkflowParams([{ name: 'In', type: 'Int' } as WorkflowParameter], [])

    expect(store.getState().nodes).toEqual([])
    expect(store.getState().edges).toEqual([])
    expect(() => store.getState().getWorkflowDefinition()).toThrow(WorkflowDocumentUnreadableError)
  })

  it('still reports every origin while a sub-canvas is open', () => {
    const store = loaded()
    store.getState().enterSubCanvas('b', 'Body')
    expect(malformedPorts(store.getState().validateWorkflow())).toEqual(['Param', 'DynIn', 'DynOut', 'Declared', 'Deep'])
  })
})

describe('each site that reports a refused stored document, by the code the rule-parity contract gives the rule', () => {
  const REPO = resolve(__dirname, '../../../..')
  const portTypes = createPortTypeRegistry(BUILTIN_PORT_TYPES)
  const contract = <T,>(file: string) => JSON.parse(readFileSync(resolve(REPO, 'contracts', file), 'utf8')) as T
  const parity = new Set(contract<{ rules: { id: string }[] }>('rule-parity.json').rules.map((r) => r.id))
  const rows = contract<{ cases: { name: string; reads: 'definition' | 'parameters'; text: string; fault?: { rule: string } }[] }>('workflow-documents.json').cases
  const units = (text: string): string => text.replace(/⟨([0-9A-F]{4})⟩/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
  const parsed = (text: string): unknown => { try { return JSON.parse(text) } catch { return undefined } }
  const codeOf = (rule: string, reads: 'definition' | 'parameters') =>
    parity.has(rule) ? rule : reads === 'definition' ? 'definition-unreadable' : 'signature-unreadable'

  it.each(rows.flatMap((r) => (r.fault ? [[r.name, r, r.fault.rule] as const] : [])))('%s', (_, row, rule) => {
    const text = units(row.text)
    const value = parsed(text)
    if (row.reads === 'definition') {
      const store = createWorkflowEditorStore({ portTypes })
      store.getState().loadStoredWorkflow(readStoredDefinitionText(text))
      expect(store.getState().validateWorkflowFindings().map((f) => f.code)).toEqual([codeOf(rule, 'definition')])
      const read = value !== null && typeof value === 'object' ? readStoredDefinition(value as WorkflowDefinition) : undefined
      if (read && !read.ok) {
        expect(validatePortTypeNames({ definition: value as WorkflowDefinition, workflowInputParams: [], workflowOutputParams: [] }).map((f) => f.code))
          .toEqual([codeOf(read.fault.rule, 'definition')])
      }
      return
    }
    const read = Array.isArray(value) ? readParameterList(value as WorkflowParameter[], 'input') : undefined
    if (read && !read.ok) {
      expect(validatePortTypeNames({ definition: { nodes: [], edges: [] }, workflowInputParams: value as WorkflowParameter[], workflowOutputParams: [] }).map((f) => f.code))
        .toEqual([codeOf(read.fault.rule, 'parameters')])
    }
  })

  it('reports an unlisted key by stored-key-unknown in a definition and in a parameter list, as the backend does', () => {
    const rogue = { nodes: [], edges: [], rogue: 1 } as unknown as WorkflowDefinition
    expect(validatePortTypeNames({ definition: rogue, workflowInputParams: [], workflowOutputParams: [] }).map((f) => f.code)).toEqual(['stored-key-unknown'])
    const parameter = [{ name: 'a', type: 'String', rogue: 1 }] as unknown as WorkflowParameter[]
    expect(validatePortTypeNames({ definition: { nodes: [], edges: [] }, workflowInputParams: parameter, workflowOutputParams: [] })[0])
      .toMatchObject({ code: 'stored-key-unknown', params: { subject: "The workflow's declared input parameters" } })
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().loadStoredWorkflow(readStoredDefinitionText('{"nodes":[],"rogue":1}'))
    const [finding] = store.getState().validateWorkflowFindings()
    expect(finding).toMatchObject({ code: 'stored-key-unknown', ruleId: 'stored-key-unknown' })
    expect(validationMessage(finding as ValidationFinding)).toBe(finding?.message)
  })
})
