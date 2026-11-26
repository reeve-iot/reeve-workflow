import { beforeEach, describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowDefinition } from '../model/workflow.js'
import { WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_INPUT_NODE_ID, WORKFLOW_OUTPUT_NODE_ID } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import type { NodeValidationRule } from './validationRules.js'
import type { WorkflowEditorStore, WorkflowEditorStoreOptions } from './store.js'
import { createWorkflowEditorStore } from './store.js'
import { readStoredDefinitionText } from '../model/storedDocument.js'
import { validateStructureFindings } from './validateStructure.js'

const portTypes = createPortTypeRegistry([
  ...BUILTIN_PORT_TYPES,
  { name: 'Device', aliases: ['device'], literal: { gradedAs: 'String' } },
  { name: 'Room', aliases: ['room'] },
])

let store: WorkflowEditorStore

const newStore = (options: Partial<WorkflowEditorStoreOptions> = {}) =>
  createWorkflowEditorStore({ portTypes, ...options })

function resetStore() {
  store = newStore()
}

const makeBlockType = (overrides: Partial<BlockTypeDescriptor> = {}): BlockTypeDescriptor => ({
  blockTypeId: 'TestBlock',
  displayName: 'Test Block',
  description: '',
  category: 'Test',
  inputs: [],
  outputs: [],
  hasDynamicPorts: false,
  configurationSchema: [],
  subCanvasNames: [],
  subCanvasInputs: {},
  subCanvasOutputs: {},
  ...overrides
})

describe('workflowEditorStore', () => {
  beforeEach(resetStore)

  it('refuses a null literal value without changing the stored edge', () => {
    store.getState().loadWorkflow({ nodes: [], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {} }] })
    expect(() => store.getState().updateEdgeMappings('e', { In: { type: 'literal', literalValue: null } }))
      .toThrow(/null literalValue/)
    expect(store.getState().edges[0]?.mappings).toEqual({})
  })

  it.each([
    ['a non-empty array', [{ sourceOutput: 'Out', targetInput: 'In' }]],
    ['an empty array', []],
  ])('refuses %s of mappings without changing the stored edge', (_, array) => {
    store.getState().loadWorkflow({ nodes: [], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {} }] })
    expect(() => store.getState().updateEdgeMappings('e', array as never)).toThrow('Mappings must be an object, not an array (rule L7)')
    expect(store.getState().edges[0]?.mappings).toEqual({})
  })

  it('refuses a sourceOutput key as unlisted without changing the stored edge', () => {
    store.getState().loadWorkflow({ nodes: [], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {} }] })
    expect(() => store.getState().updateEdgeMappings('e', { In: { type: 'direct', sourceOutput: 'Out' } as never }))
      .toThrow("Mapping 'In' contains an unlisted key")
    expect(store.getState().edges[0]?.mappings).toEqual({})
  })

  it('refuses dotted host port names when registering block types', () => {
    expect(() => store.getState().setBlockTypes([makeBlockType({ inputs: [{ name: 'input.value', displayName: 'Input', type: 'String', isRequired: false }] })]))
      .toThrow(/dotted-name/)
  })

  it.each([
    ['enum-field-no-values', undefined, 'Enum', undefined],
    ['enum-field-no-values', [], 'Enum', undefined],
    ['enum-field-duplicate-value', ['A', 'B', 'A'], 'Enum', undefined],
    ['enum-field-blank-value', ['A', ''], 'Enum', undefined],
    ['enum-field-blank-value', ['A', '  '], 'Enum', undefined],
    ['enum-values-on-non-enum-field', ['A'], 'String', undefined],
    ['enum-field-default-not-allowed', ['A', 'B'], 'Enum', 'C'],
    ['enum-field-default-not-allowed', ['A', 'B'], 'Enum', 'a'],
  ] as const)('refuses a malformed Enum field under %s when registering block types', (rule, allowedValues, type, defaultValue) => {
    const field = { name: 'Kind', label: 'Kind', type, required: false, ...(allowedValues ? { allowedValues: [...allowedValues] } : {}), ...(defaultValue ? { defaultValue } : {}) }
    expect(() => store.getState().setBlockTypes([makeBlockType({ configurationSchema: [field] })])).toThrow(new RegExp(`\\(rule ${rule}\\)`))
  })

  it.each([
    [['A', 'a'], undefined],
    [['A', 'B'], 'B'],
  ] as const)('registers a well-formed Enum field %j', (allowedValues, defaultValue) => {
    const field = { name: 'Kind', label: 'Kind', type: 'Enum', required: false, allowedValues: [...allowedValues], ...(defaultValue ? { defaultValue } : {}) }
    store.getState().setBlockTypes([makeBlockType({ configurationSchema: [field] })])
    expect(store.getState().blockTypes).toHaveLength(1)
  })

  describe('a read the editor cannot draw', () => {
    const placed = { position: { x: 0, y: 0 }, configuration: {} }
    const arrayForm = { nodes: [{ id: 'a', blockTypeId: 'TestBlock', ...placed }], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'a', mappings: [{ sourceOutput: 'Out', targetInput: 'In' }] }] }
    const unplaced = { nodes: [{ id: 'n', blockTypeId: 'TestBlock', configuration: {} }], edges: [] }

    it.each([['an edge holding its mappings as an array', arrayForm, '/edges/0/mappings'], ['a node with no position', unplaced, '/nodes/0/position']] as const)(
      'reports %s as the one definition-undrawable finding and refuses to save', (_name, definition, location) => {
        store.getState().loadStoredWorkflow({ ok: true, sites: [], definition: definition as unknown as WorkflowDefinition })

        expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'undrawable', location })
        const findings = store.getState().validateWorkflowFindings()
        expect(findings).toHaveLength(1)
        expect(findings[0]).toMatchObject({ code: 'definition-undrawable', ruleId: 'definition-undrawable' })
        expect(() => store.getState().getWorkflowDefinition()).toThrow(/cannot be drawn by the editor/)
      },
    )
  })

  describe('duplicate reader findings', () => {
    it.each([['root', ''], ['body', " in branch 'Body/~' of 'Outer label'"], ['nested', " in branch 'Then' of 'Inner label' in branch 'Body/~' of 'Outer label'"]] as const)(
      '%s uses the same canvas phrase for a refused load and a model finding', (depth, location) => {
        const blocks = [makeBlockType({ blockTypeId: 'Outer', displayName: 'Outer label' }), makeBlockType({ blockTypeId: 'Inner', displayName: 'Inner label' })]
        const duplicate: WorkflowDefinition = { nodes: [0, 1].map(() => ({ id: 'twin', blockTypeId: 'TestBlock', position: { x: 0, y: 0 }, configuration: {} })), edges: [] }
        const nested: WorkflowDefinition = { nodes: [{ id: 'inner', blockTypeId: 'Inner', position: { x: 0, y: 0 }, configuration: {}, subCanvases: { Then: duplicate } }], edges: [] }
        const definition: WorkflowDefinition = depth === 'root' ? duplicate : { nodes: [{ id: 'outer', blockTypeId: 'Outer', position: { x: 0, y: 0 }, configuration: {}, subCanvases: { 'Body/~': depth === 'body' ? duplicate : nested } }], edges: [] }
        const model = validateStructureFindings({ ...definition, blockTypes: blocks }).find((f) => f.code === 'duplicate-node-id')
        store.getState().setBlockTypes(blocks)
        for (const load of [() => store.getState().loadWorkflow(definition), () => store.getState().loadStoredWorkflow(readStoredDefinitionText(JSON.stringify(definition)))]) {
          load()
          const finding = store.getState().validateWorkflowFindings()[0]
          expect(finding).toMatchObject({ code: 'duplicate-node-id', params: { duplicateId: 'twin', location }, message: model?.message, canvasPath: model?.canvasPath })
          expect(() => store.getState().getWorkflowDefinition()).toThrow(/duplicate-node-id/)
        }
      },
    )

    it('uses the block type id when the catalogue has no container label', () => {
      store.getState().loadWorkflow({ nodes: [{ id: 'parent', blockTypeId: 'Unknown', position: { x: 0, y: 0 }, configuration: {}, subCanvases: { Body: { nodes: [0, 1].map(() => ({ id: 'twin', blockTypeId: 'TestBlock', position: { x: 0, y: 0 }, configuration: {} })), edges: [] } } }], edges: [] })
      expect(store.getState().validateWorkflowFindings()[0]?.params).toEqual({ duplicateId: 'twin', location: " in branch 'Body' of 'Unknown'" })
    })
  })

  describe('enterSubCanvas / exitSubCanvas', () => {
    it('pushes a frame onto the stack and loads sub-canvas nodes', () => {
      const subNodes = [{ id: 'sub-1', blockTypeId: 'Noop', position: { x: 0, y: 0 }, configuration: {} }]
      const subEdges = [{ id: 'se-1', sourceNodeId: 'sub-1', targetNodeId: 'sub-1', mappings: {} }]

      store.getState().loadWorkflow({
        nodes: [
          {
            id: 'node-A',
            blockTypeId: 'IfElse',
            position: { x: 100, y: 100 },
            configuration: {},
            subCanvases: { Then: { nodes: subNodes, edges: subEdges } }
          }
        ],
        edges: []
      })

      store.getState().enterSubCanvas('node-A', 'Then')

      const state = store.getState()
      expect(state.canvasStack).toHaveLength(1)
      expect(state.canvasStack[0]!.parentNodeId).toBe('node-A')
      expect(state.canvasStack[0]!.subCanvasName).toBe('Then')
      expect(state.nodes).toEqual(subNodes)
      expect(state.edges).toEqual(subEdges)
    })

    it('initialises an empty sub-canvas when one does not exist', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'node-A', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      store.getState().enterSubCanvas('node-A', 'Else')

      const state = store.getState()
      expect(state.nodes).toEqual([])
      expect(state.edges).toEqual([])
    })

    it('round-trip: exitSubCanvas restores parent canvas with updated sub-canvas', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'node-A', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      store.getState().enterSubCanvas('node-A', 'Then')

      store.getState().addNode('Noop', { x: 50, y: 50 })
      const subNodeId = store.getState().nodes.find(n => n.blockTypeId === 'Noop')!.id

      store.getState().exitSubCanvas()

      const state = store.getState()
      expect(state.canvasStack).toHaveLength(0)
      expect(state.nodes).toHaveLength(2)
      const nodeA = state.nodes.find(n => n.id === 'node-A')
      expect(nodeA).toBeDefined()

      const subCanvas = nodeA!.subCanvases?.['Then']
      expect(subCanvas).toBeDefined()
      expect(subCanvas!.nodes).toHaveLength(1)
      expect(subCanvas!.nodes[0]!.id).toBe(subNodeId)
    })

    it('does nothing when exitSubCanvas is called with empty stack', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'node-1', blockTypeId: 'Noop', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      store.getState().exitSubCanvas()

      expect(store.getState().nodes).toHaveLength(2)
      expect(store.getState().nodes.some(n => n.id === 'node-1')).toBe(true)
      expect(store.getState().canvasStack).toHaveLength(0)
    })

    it('supports nested sub-canvases (double enter/exit round-trip)', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'outer', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      store.getState().enterSubCanvas('outer', 'Then')
      store.getState().addNode('IfElse', { x: 10, y: 10 })
      const innerId = store.getState().nodes[0]!.id

      store.getState().enterSubCanvas(innerId, 'Else')
      store.getState().addNode('Noop', { x: 20, y: 20 })

      expect(store.getState().canvasStack).toHaveLength(2)

      store.getState().exitSubCanvas()
      expect(store.getState().canvasStack).toHaveLength(1)

      store.getState().exitSubCanvas()
      expect(store.getState().canvasStack).toHaveLength(0)

      const outerNode = store.getState().nodes.find(n => n.id === 'outer')
      const innerNode = outerNode?.subCanvases?.['Then']?.nodes.find(n => n.id === innerId)
      expect(innerNode?.subCanvases?.['Else']?.nodes).toHaveLength(1)
    })
  })

  describe('exitToLevel', () => {
    it('exits multiple levels at once', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'outer', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      store.getState().enterSubCanvas('outer', 'Then')
      store.getState().addNode('IfElse', { x: 0, y: 0 })
      const innerId = store.getState().nodes[0]!.id
      store.getState().enterSubCanvas(innerId, 'Else')

      expect(store.getState().canvasStack).toHaveLength(2)

      store.getState().exitToLevel(0)

      expect(store.getState().canvasStack).toHaveLength(0)
    })
  })

  describe('getWorkflowDefinition', () => {
    it('returns nodes/edges directly when at root level', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'Noop', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      const def = store.getState().getWorkflowDefinition()
      expect(def.nodes).toHaveLength(2)
      expect(def.nodes?.some(n => n.id === 'n1')).toBe(true)
    })

    it('reconstructs root definition when inside a sub-canvas', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'outer', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      store.getState().enterSubCanvas('outer', 'Then')
      store.getState().addNode('Noop', { x: 0, y: 0 })

      const def = store.getState().getWorkflowDefinition()

      expect(def.nodes).toHaveLength(2)
      const outerNode = def.nodes?.find(n => n.id === 'outer')
      expect(outerNode).toBeDefined()
      expect(outerNode!.subCanvases?.['Then']?.nodes).toHaveLength(1)
    })
  })

  describe('updateNodeConfig', () => {
    it('updates configuration on the correct node in active canvas', () => {
      store.getState().loadWorkflow({
        nodes: [
          { id: 'n1', blockTypeId: 'CSharpScript', position: { x: 0, y: 0 }, configuration: {} },
          { id: 'n2', blockTypeId: 'Noop', position: { x: 100, y: 0 }, configuration: {} }
        ],
        edges: []
      })

      store.getState().updateNodeConfig('n1', { Code: 'return 42;' })

      const state = store.getState()
      expect(state.nodes.find(n => n.id === 'n1')?.configuration).toEqual({ Code: 'return 42;' })
      expect(state.nodes.find(n => n.id === 'n2')?.configuration).toEqual({})
    })

    it('updates node config inside a sub-canvas', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'outer', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      store.getState().enterSubCanvas('outer', 'Then')
      store.getState().addNode('CSharpScript', { x: 0, y: 0 })
      const subNodeId = store.getState().nodes[0]!.id

      store.getState().updateNodeConfig(subNodeId, { Code: 'Console.WriteLine("hi");' })

      const config = store.getState().nodes.find(n => n.id === subNodeId)?.configuration
      expect(config).toEqual({ Code: 'Console.WriteLine("hi");' })
    })
  })

  describe('patchNodeConfig', () => {
    it('writes the named keys and leaves the rest', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'Dispatch', position: { x: 0, y: 0 }, configuration: { Depot: 'WAW', Consolidated: true } }],
        edges: []
      })

      store.getState().patchNodeConfig('n1', { Depot: 'GDN' })

      expect(store.getState().nodes.find(n => n.id === 'n1')?.configuration)
        .toEqual({ Depot: 'GDN', Consolidated: true })
    })

    it('touches no other node, and marks the workflow dirty', () => {
      store.getState().loadWorkflow({
        nodes: [
          { id: 'n1', blockTypeId: 'Dispatch', position: { x: 0, y: 0 }, configuration: { Depot: 'WAW' } },
          { id: 'n2', blockTypeId: 'Dispatch', position: { x: 80, y: 0 }, configuration: { Depot: 'KTW' } }
        ],
        edges: []
      })

      store.getState().patchNodeConfig('n1', { Depot: 'GDN' })

      expect(store.getState().nodes.find(n => n.id === 'n2')?.configuration).toEqual({ Depot: 'KTW' })
      expect(store.getState().isDirty).toBe(true)
    })

    it('keeps a *** entry of a map literal as the value it is through an edit of another entry, and when nothing is edited', () => {
      const headers = { Authorization: '***', Accept: 'text/plain' }
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'Dispatch', position: { x: 0, y: 0 }, configuration: { Headers: headers } }],
        edges: []
      })
      const savedHeaders = () => store.getState().getWorkflowDefinition().nodes?.find(n => n.id === 'n1')?.configuration?.['Headers']

      expect(savedHeaders()).toEqual(headers)

      store.getState().patchNodeConfig('n1', { Headers: { ...headers, Accept: 'application/json' } })

      expect(savedHeaders()).toEqual({ Authorization: '***', Accept: 'application/json' })
    })

    it('refuses a null, from either writer, and the document saved after still opens', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'Dispatch', position: { x: 0, y: 0 }, configuration: { Depot: 'WAW' } }],
        edges: []
      })
      const loose = (value: unknown) => value as Record<string, string>

      expect(() => store.getState().patchNodeConfig('n1', loose({ Depot: null }))).toThrow(TypeError)
      expect(() => store.getState().updateNodeConfig('n1', loose({ Depot: null }))).toThrow(TypeError)

      expect(store.getState().nodes.find(n => n.id === 'n1')?.configuration).toEqual({ Depot: 'WAW' })
      expect(readStoredDefinitionText(JSON.stringify(store.getState().getWorkflowDefinition())).ok).toBe(true)
    })
  })

  describe('updateNodeErrorPolicy', () => {
    const entry = { id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 50, y: 100 }, configuration: {} }
    const withPolicies = (): WorkflowDefinition => ({
      nodes: [entry, {
        id: 'c', blockTypeId: 'Loop', position: { x: 0, y: 0 }, configuration: {}, errorPolicy: { policy: 'Skip' },
        subCanvases: { Body: { nodes: [entry, { id: 'b', blockTypeId: 'Act', position: { x: 0, y: 0 }, configuration: { k: 1 }, errorPolicy: { policy: 'ContinueWithDefault', defaultOutputJson: '{"o":1}' } }], edges: [] } },
      }],
      edges: [],
    })

    it('round-trips every node\'s policy, the root\'s and a branch\'s, as it was loaded', () => {
      store.getState().loadWorkflow(withPolicies())

      expect(store.getState().getWorkflowDefinition()).toEqual(withPolicies())
    })

    it('writes a branch node\'s policy onto the node and nothing into its configuration', () => {
      store.getState().setBlockTypes([makeBlockType({ blockTypeId: 'Loop', subCanvasNames: ['Body'] }), makeBlockType({ blockTypeId: 'Act' })])
      store.getState().loadWorkflow(withPolicies())
      store.getState().enterSubCanvas('c', 'Body')

      store.getState().updateNodeErrorPolicy('b', { policy: 'EmitError' })
      store.getState().exitSubCanvas()

      const c = store.getState().getWorkflowDefinition().nodes?.find((n) => n.id === 'c')
      const b = c?.subCanvases?.['Body']?.nodes?.find((n) => n.id === 'b')
      expect(b?.errorPolicy).toEqual({ policy: 'EmitError' })
      expect(b?.configuration).toEqual({ k: 1 })
      expect(store.getState().isDirty).toBe(true)
    })

    it('takes the key off the node on null, which the run reads as FailWorkflow', () => {
      store.getState().loadWorkflow(withPolicies())

      store.getState().updateNodeErrorPolicy('c', null)

      expect(store.getState().getWorkflowDefinition().nodes?.find((n) => n.id === 'c')).not.toHaveProperty('errorPolicy')
    })
  })

  describe('validateWorkflow', () => {
    it('reports missing required config field', () => {
      const blockType = makeBlockType({
        blockTypeId: 'CSharpScript',
        displayName: 'C# Script',
        configurationSchema: [
          { name: 'Code', label: 'C# Code', type: 'String', required: true, defaultValue: null, description: null }
        ]
      })

      store.getState().setBlockTypes([blockType])
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'CSharpScript', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      const errors = store.getState().validateWorkflow().map((f) => f.message)
      expect(errors).toContain("Node 'C# Script' is missing required configuration field 'C# Code'")
    })

    it('does not report error when required config field is filled', () => {
      const blockType = makeBlockType({
        blockTypeId: 'CSharpScript',
        displayName: 'C# Script',
        configurationSchema: [
          { name: 'Code', label: 'C# Code', type: 'String', required: true, defaultValue: null, description: null }
        ]
      })

      store.getState().setBlockTypes([blockType])
      store.getState().loadWorkflow({
        nodes: [
          { id: 'n1', blockTypeId: 'CSharpScript', position: { x: 0, y: 0 }, configuration: { Code: 'return 1;' } }
        ],
        edges: []
      })

      const errors = store.getState().validateWorkflow().map((f) => f.message)
      const configErrors = errors.filter(e => e.includes('configuration field'))
      expect(configErrors).toHaveLength(0)
    })

    it('reports missing required input mapping', () => {
      const blockType = makeBlockType({
        blockTypeId: 'StringToInt',
        displayName: 'String to Int',
        inputs: [{ name: 'Value', displayName: 'Value', type: 'String', isRequired: true }]
      })

      store.getState().setBlockTypes([blockType])
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'StringToInt', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      const errors = store.getState().validateWorkflow()
      expect(errors).toMatchObject([{ code: 'required-input-unfed', params: { nodeLabel: 'String to Int', portLabel: 'Value' }, portName: 'Value', message: "Node 'String to Int' is missing required mapping for 'Value'" }])
    })

    it('keeps a control-flow edge with empty mappings and still flags an unconfigured required input', () => {
      const blockType = makeBlockType({
        blockTypeId: 'SetDeviceState',
        displayName: 'Set Device State',
        inputs: [{ name: 'DeviceId', displayName: 'Device', type: 'String', isRequired: true }]
      })

      store.getState().setBlockTypes([blockType])
      store.getState().loadWorkflow({
        nodes: [
          { id: 'n0', blockTypeId: 'SetDeviceState', position: { x: 0, y: 0 }, configuration: { DeviceId: 'd1', CanonicalCapabilityKey: 'light.isOn' } },
          { id: 'n1', blockTypeId: 'SetDeviceState', position: { x: 0, y: 0 }, configuration: { CanonicalCapabilityKey: 'light.isOn' } }
        ],
        edges: [{ id: 'e1', sourceNodeId: 'n0', targetNodeId: 'n1', mappings: {} }]
      })

      const loadedEdge = store.getState().edges.find(e => e.id === 'e1')
      expect(loadedEdge?.mappings).toEqual({})

      let errors: string[] = []
      expect(() => { errors = store.getState().validateWorkflow().map((f) => f.message) }).not.toThrow()
      expect(errors).toEqual(["Node 'Set Device State (n1)' is missing required mapping for 'Device'"])
    })

    it('treats a required input supplied by node configuration as satisfied (config→input merge parity)', () => {
      const setDevice = makeBlockType({
        blockTypeId: 'SetDeviceState',
        displayName: 'Set Device State',
        inputs: [{ name: 'DeviceId', displayName: 'Device', type: 'String', isRequired: true }]
      })

      store.getState().setBlockTypes([setDevice])
      store.getState().loadWorkflow({
        nodes: [{ id: 'n0', blockTypeId: 'SetDeviceState', position: { x: 0, y: 0 }, configuration: { DeviceId: 'd1', CanonicalCapabilityKey: 'light.isOn' } }],
        edges: []
      })

      expect(store.getState().validateWorkflow()).toHaveLength(0)
    })

    it('treats a first-block SendNotification with inline Title/Message config as valid (seed regression)', () => {
      const notify = makeBlockType({
        blockTypeId: 'SendNotification',
        displayName: 'Send Notification',
        inputs: [
          { name: 'Title', displayName: 'Title', type: 'String', isRequired: true },
          { name: 'Message', displayName: 'Message', type: 'String', isRequired: true },
          { name: 'Level', displayName: 'Level', type: 'String', isRequired: false }
        ]
      })

      store.getState().setBlockTypes([notify])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'n0',
          blockTypeId: 'SendNotification',
          position: { x: 0, y: 0 },
          configuration: { Title: 'Doorbell', Message: 'Someone is at the front door.', Level: 'info' }
        }],
        edges: []
      })

      expect(store.getState().validateWorkflow()).toHaveLength(0)
    })

    it('still flags a required input whose configuration value is an empty string', () => {
      const setDevice = makeBlockType({
        blockTypeId: 'SetDeviceState',
        displayName: 'Set Device State',
        inputs: [{ name: 'DeviceId', displayName: 'Device', type: 'String', isRequired: true }]
      })

      store.getState().setBlockTypes([setDevice])
      store.getState().loadWorkflow({
        nodes: [{ id: 'n0', blockTypeId: 'SetDeviceState', position: { x: 0, y: 0 }, configuration: { DeviceId: '' } }],
        edges: []
      })

      expect(store.getState().validateWorkflow().map((f) => f.message))
        .toContain("Node 'Set Device State' is missing required mapping for 'Device'")
    })

    it('does not flag nodes with unknown blockTypeId', () => {
      store.getState().setBlockTypes([])
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'Unknown', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      const errors = store.getState().validateWorkflow().map((f) => f.message)
      expect(errors).toHaveLength(0)
    })

    it('validates the demo-seeded Doorbell workflow with zero errors (end-to-end seed regression)', () => {
      const blockTypes: BlockTypeDescriptor[] = [
        makeBlockType({
          blockTypeId: 'SendNotification',
          displayName: 'Send Notification',
          inputs: [
            { name: 'Title', displayName: 'Title', type: 'String', isRequired: true },
            { name: 'Message', displayName: 'Message', type: 'String', isRequired: true },
            { name: 'Level', displayName: 'Level (info, warning, error)', type: 'String', isRequired: false }
          ],
          configurationSchema: [
            { name: 'Title', label: 'Title', type: 'String', required: false, defaultValue: null, description: null },
            { name: 'Message', label: 'Message', type: 'String', required: false, defaultValue: null, description: null },
            { name: 'Level', label: 'Level', type: 'String', required: false, defaultValue: 'info', description: null }
          ]
        }),
        makeBlockType({
          blockTypeId: 'SetDeviceState',
          displayName: 'Set Device Property',
          inputs: [
            { name: 'DeviceId', displayName: 'Device', type: 'Device', isRequired: true },
            { name: 'Value', displayName: 'Value', type: 'String', isRequired: false }
          ],
          configurationSchema: [
            { name: 'DeviceId', label: 'Device', type: 'String', required: true, defaultValue: null, description: null }
          ]
        }),
        makeBlockType({
          blockTypeId: 'WaitForDuration',
          displayName: 'Wait For Duration',
          inputs: [{ name: 'DurationMs', displayName: 'Duration (ms)', type: 'Int', isRequired: true }],
          configurationSchema: [
            { name: 'DurationMs', label: 'Duration (ms)', type: 'Int', required: true, defaultValue: '1000', description: null }
          ]
        })
      ]

      store.getState().setBlockTypes(blockTypes)
      store.getState().loadWorkflow({
        nodes: [
          { id: 'n0', blockTypeId: 'SendNotification', position: { x: 160, y: 160 }, configuration: { Title: 'Doorbell', Message: 'Someone is at the front door.', Level: 'info' } },
          { id: 'n1', blockTypeId: 'SetDeviceState', position: { x: 520, y: 160 }, configuration: { DeviceId: '8c5debb4-0289-4df4-b136-fe1229ec456c', CanonicalCapabilityKey: 'light.isOn', Kind: 0, Value: 'true' } },
          { id: 'n2', blockTypeId: 'WaitForDuration', position: { x: 880, y: 160 }, configuration: { DurationMs: 3000 } },
          { id: 'n3', blockTypeId: 'SetDeviceState', position: { x: 1240, y: 160 }, configuration: { DeviceId: '8c5debb4-0289-4df4-b136-fe1229ec456c', CanonicalCapabilityKey: 'light.isOn', Kind: 0, Value: 'false' } }
        ],
        edges: [
          { id: 'e1', sourceNodeId: 'n0', targetNodeId: 'n1', mappings: {} },
          { id: 'e2', sourceNodeId: 'n1', targetNodeId: 'n2', mappings: {} },
          { id: 'e3', sourceNodeId: 'n2', targetNodeId: 'n3', mappings: {} }
        ]
      })

      expect(store.getState().validateWorkflow()).toEqual([])
    })
  })

  describe('loadWorkflow', () => {
    it('clears canvasStack when a workflow is loaded', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'outer', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })
      store.getState().enterSubCanvas('outer', 'Then')
      expect(store.getState().canvasStack).toHaveLength(1)

      store.getState().loadWorkflow({ nodes: [], edges: [] })
      expect(store.getState().canvasStack).toHaveLength(0)
    })

    it('refuses a node written with an unlisted "config" key, at any depth, and loads nothing', () => {
      for (const definition of [
        { nodes: [{ id: 'n1', blockTypeId: 'CSharpScript', position: { x: 0, y: 0 }, config: { Code: 'return 1;' } }], edges: [] },
        {
          nodes: [{
            id: 'outer', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: { Condition: 'true' },
            subCanvases: { Then: { nodes: [{ id: 'inner', blockTypeId: 'Noop', position: { x: 10, y: 10 }, config: { Delay: 100 } }], edges: [] } },
          }],
          edges: [],
        },
      ]) {
        store.getState().loadWorkflow(definition as unknown as WorkflowDefinition)

        expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'stored-key-unknown' })
        expect(store.getState().nodes).toEqual([])
      }
    })

    it.each([
      ['absent at the root', { nodes: [{ id: 'outer', blockTypeId: 'IfElse', position: { x: 0, y: 0 } }], edges: [] }, '/nodes/0/configuration'],
      ['null in a nested canvas', {
        nodes: [{ id: 'outer', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {},
          subCanvases: { Then: { nodes: [{ id: 'inner', blockTypeId: 'Noop', position: { x: 10, y: 10 }, configuration: null }], edges: [] } } }],
        edges: [],
      }, '/nodes/0/subCanvases/Then/nodes/0/configuration'],
    ])('refuses a configuration %s', (_name, definition, location) => {
      store.getState().loadWorkflow(definition as unknown as WorkflowDefinition)

      expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'L7', location })
      expect(store.getState().nodes).toEqual([])
      expect(() => store.getState().getWorkflowDefinition()).toThrow(/cannot be read/)
    })
  })

  describe('setPaletteDragActive', () => {
    it('toggles palette drag state', () => {
      store.getState().setPaletteDragActive(true)
      expect(store.getState().isPaletteDragActive).toBe(true)

      store.getState().setPaletteDragActive(false)
      expect(store.getState().isPaletteDragActive).toBe(false)
    })
  })

  describe('quick add block request', () => {
    it('stores and clears quick add block type id', () => {
      store.getState().requestQuickAddBlock('IfElse')
      expect(store.getState().quickAddBlockTypeId).toBe('IfElse')

      store.getState().clearQuickAddBlock()
      expect(store.getState().quickAddBlockTypeId).toBeNull()
    })
  })

  describe('validateStructure', () => {
    const inputNode = { id: '__workflow_input__', blockTypeId: 'WorkflowInput', position: { x: 0, y: 0 }, configuration: {} }

    it('passes for a single Workflow Input root spine', () => {
      store.getState().setBlockTypes([
        makeBlockType({ blockTypeId: 'SetDeviceState', displayName: 'Set Device State' })
      ])
      store.getState().loadWorkflow({
        nodes: [inputNode, { id: 'a', blockTypeId: 'SetDeviceState', position: { x: 1, y: 0 }, configuration: {} }],
        edges: [{ id: 'e1', sourceNodeId: '__workflow_input__', targetNodeId: 'a', mappings: {} }]
      })

      expect(store.getState().validateStructure()).toEqual([])
    })

    it('flags a second node that has no incoming edge', () => {
      store.getState().setBlockTypes([
        makeBlockType({ blockTypeId: 'SendNotification', displayName: 'Send Notification' }),
        makeBlockType({ blockTypeId: 'SetDeviceState', displayName: 'Set Device State' })
      ])
      store.getState().loadWorkflow({
        nodes: [
          inputNode,
          { id: 'notify', blockTypeId: 'SendNotification', position: { x: 1, y: 0 }, configuration: {} },
          { id: 'a', blockTypeId: 'SetDeviceState', position: { x: 2, y: 0 }, configuration: {} }
        ],
        edges: [
          { id: 'e1', sourceNodeId: '__workflow_input__', targetNodeId: 'a', mappings: {} },
          { id: 'e2', sourceNodeId: 'notify', targetNodeId: 'a', mappings: {} }
        ]
      })

      const errors = store.getState().validateStructureFindings()
      expect(errors.some(e => e.code === 'extra-root' && e.message.includes('Send Notification'))).toBe(true)
    })

    it('always has a Workflow Input node after loadWorkflow, so the "no start node" error is never produced', () => {
      store.getState().setBlockTypes([
        makeBlockType({ blockTypeId: 'SetDeviceState', displayName: 'Set Device State' })
      ])
      store.getState().loadWorkflow({
        nodes: [{ id: 'a', blockTypeId: 'SetDeviceState', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      expect(store.getState().nodes.some(n => n.id === '__workflow_input__')).toBe(true)
      expect(store.getState().validateStructureFindings().some((f) => f.code === 'no-start-node')).toBe(false)
    })

    it('flags a node that is unreachable from the Workflow Input node', () => {
      store.getState().setBlockTypes([
        makeBlockType({ blockTypeId: 'SetDeviceState', displayName: 'Set Device State' })
      ])
      store.getState().loadWorkflow({
        nodes: [
          inputNode,
          { id: 'a', blockTypeId: 'SetDeviceState', position: { x: 1, y: 0 }, configuration: {} },
          { id: 'iso1', blockTypeId: 'SetDeviceState', position: { x: 2, y: 0 }, configuration: {} },
          { id: 'iso2', blockTypeId: 'SetDeviceState', position: { x: 3, y: 0 }, configuration: {} }
        ],
        edges: [
          { id: 'e1', sourceNodeId: '__workflow_input__', targetNodeId: 'a', mappings: {} },
          { id: 'e2', sourceNodeId: 'iso1', targetNodeId: 'iso2', mappings: {} },
          { id: 'e3', sourceNodeId: 'iso2', targetNodeId: 'iso1', mappings: {} }
        ]
      })

      const errors = store.getState().validateStructureFindings()
      expect(errors.some(e => e.code === 'unreachable')).toBe(true)
    })

    it('returns no structural errors on an empty canvas', () => {
      store.getState().setBlockTypes([])
      store.getState().loadWorkflow({ nodes: [], edges: [] })
      expect(store.getState().validateStructure()).toEqual([])
    })

    it('flags a cycle that is reachable from the Workflow Input node', () => {
      store.getState().setBlockTypes([
        makeBlockType({ blockTypeId: 'SetDeviceState', displayName: 'Set Device State' })
      ])
      store.getState().loadWorkflow({
        nodes: [
          inputNode,
          { id: 'a', blockTypeId: 'SetDeviceState', position: { x: 1, y: 0 }, configuration: {} },
          { id: 'b', blockTypeId: 'SetDeviceState', position: { x: 2, y: 0 }, configuration: {} }
        ],
        edges: [
          { id: 'e1', sourceNodeId: '__workflow_input__', targetNodeId: 'a', mappings: {} },
          { id: 'e2', sourceNodeId: 'a', targetNodeId: 'b', mappings: {} },
          { id: 'e3', sourceNodeId: 'b', targetNodeId: 'a', mappings: {} }
        ]
      })

      const errors = store.getState().validateStructureFindings()
      expect(errors.some(e => e.code === 'cycle')).toBe(true)
    })

    it('flags an input fed by two literal mappings', () => {
      store.getState().setBlockTypes([
        makeBlockType({ blockTypeId: 'SendNotification', displayName: 'Send Notification' }),
        makeBlockType({ blockTypeId: 'SetDeviceState', displayName: 'Set Device State' })
      ])
      store.getState().loadWorkflow({
        nodes: [
          inputNode,
          { id: 'a', blockTypeId: 'SendNotification', position: { x: 1, y: 0 }, configuration: {} },
          { id: 'b', blockTypeId: 'SendNotification', position: { x: 1, y: 1 }, configuration: {} },
          { id: 'join', blockTypeId: 'SetDeviceState', position: { x: 2, y: 0 }, configuration: {} }
        ],
        edges: [
          { id: 'e0', sourceNodeId: '__workflow_input__', targetNodeId: 'a', mappings: {} },
          { id: 'e1', sourceNodeId: '__workflow_input__', targetNodeId: 'b', mappings: {} },
          { id: 'e2', sourceNodeId: 'a', targetNodeId: 'join', mappings: { Value: { type: 'literal', literalValue: true } } },
          { id: 'e3', sourceNodeId: 'b', targetNodeId: 'join', mappings: { Value: { type: 'literal', literalValue: false } } }
        ]
      })

      const errors = store.getState().validateStructureFindings()
      expect(errors.some(e => e.code === 'duplicate-input-mapping' && e.portName === 'Value')).toBe(true)
    })

    it('flags an input fed by two incoming edges', () => {
      store.getState().setBlockTypes([
        makeBlockType({ blockTypeId: 'SendNotification', displayName: 'Send Notification' }),
        makeBlockType({ blockTypeId: 'SetDeviceState', displayName: 'Set Device State' })
      ])
      store.getState().loadWorkflow({
        nodes: [
          inputNode,
          { id: 'a', blockTypeId: 'SendNotification', position: { x: 1, y: 0 }, configuration: {} },
          { id: 'b', blockTypeId: 'SendNotification', position: { x: 1, y: 1 }, configuration: {} },
          { id: 'join', blockTypeId: 'SetDeviceState', position: { x: 2, y: 0 }, configuration: {} }
        ],
        edges: [
          { id: 'e0', sourceNodeId: '__workflow_input__', targetNodeId: 'a', mappings: {} },
          { id: 'e1', sourceNodeId: '__workflow_input__', targetNodeId: 'b', mappings: {} },
          { id: 'e2', sourceNodeId: 'a', targetNodeId: 'join', mappings: { DeviceId: { type: 'direct', sourceOutputName: 'Device' } } },
          { id: 'e3', sourceNodeId: 'b', targetNodeId: 'join', mappings: { DeviceId: { type: 'direct', sourceOutputName: 'Device' } } }
        ]
      })

      const errors = store.getState().validateStructureFindings()
      expect(errors.some(e => e.code === 'duplicate-input-mapping' && e.portName === 'DeviceId')).toBe(true)
    })
  })

  describe('updateNodeOutputPorts', () => {
    it('updateNodeOutputPorts sets the structured outputPorts field', () => {
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })
      store.getState().updateNodeOutputPorts('n1', [
        { name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: { Then: 'warmTemp', Else: 'coolTemp' } },
      ])
      const node = store.getState().nodes.find(n => n.id === 'n1')!
      expect(node.outputPorts?.[0]!.sources?.Then).toBe('warmTemp')
    })

    it('preserves outputPorts through a loadWorkflow round-trip', () => {
      const definition = {
        nodes: [{
          id: 'n1',
          blockTypeId: 'IfElse',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [{ name: 'result', displayName: 'Result', type: 'String', sources: { Then: 'outA' } }]
        }],
        edges: []
      }
      store.getState().loadWorkflow(definition)
      const node = store.getState().nodes.find(n => n.id === 'n1')!
      expect(node.outputPorts?.[0]!.sources?.Then).toBe('outA')
    })
  })

  describe('syncSpecialNodes root input node', () => {
    it('keeps a Workflow Input node on the root canvas even with zero declared input params', () => {
      store.getState().setBlockTypes([
        makeBlockType({ blockTypeId: 'SetDeviceState', displayName: 'Set Device State' })
      ])
      store.getState().loadWorkflow({
        nodes: [{ id: 'a', blockTypeId: 'SetDeviceState', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })
      store.getState().setWorkflowParams([], [])

      expect(store.getState().nodes.some(n => n.id === '__workflow_input__')).toBe(true)
    })
  })

  describe('syncSpecialNodes sub-canvas WorkflowOutput from outputPorts', () => {
    it('injects a WorkflowOutput node into a branch when the parent declares outputPorts for that branch', () => {
      const ifElseBlockType = makeBlockType({
        blockTypeId: 'IfElse',
        displayName: 'If / Else',
        subCanvasNames: ['Then', 'Else'],
        subCanvasOutputs: {},
      })
      store.getState().setBlockTypes([ifElseBlockType])

      store.getState().loadWorkflow({
        nodes: [{
          id: 'ifnode',
          blockTypeId: 'IfElse',
          position: { x: 100, y: 100 },
          configuration: {},
          outputPorts: [
            { name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: { Then: 'warmTemp' } }
          ]
        }],
        edges: []
      })

      store.getState().enterSubCanvas('ifnode', 'Then')

      const nodes = store.getState().nodes
      expect(nodes.some(n => n.id === WORKFLOW_OUTPUT_NODE_ID)).toBe(true)
    })

    it('does NOT inject a WorkflowOutput node when the parent declares no outputPorts mapping the branch', () => {
      const ifElseBlockType = makeBlockType({
        blockTypeId: 'IfElse',
        displayName: 'If / Else',
        subCanvasNames: ['Then', 'Else'],
        subCanvasOutputs: {},
      })
      store.getState().setBlockTypes([ifElseBlockType])

      store.getState().loadWorkflow({
        nodes: [{
          id: 'ifnode',
          blockTypeId: 'IfElse',
          position: { x: 100, y: 100 },
          configuration: {}
        }],
        edges: []
      })

      store.getState().enterSubCanvas('ifnode', 'Then')

      const nodes = store.getState().nodes
      expect(nodes.some(n => n.id === WORKFLOW_OUTPUT_NODE_ID)).toBe(false)
    })

    it('does NOT inject a WorkflowOutput node when outputPorts exist but none map the current branch', () => {
      const ifElseBlockType = makeBlockType({
        blockTypeId: 'IfElse',
        displayName: 'If / Else',
        subCanvasNames: ['Then', 'Else'],
        subCanvasOutputs: {},
      })
      store.getState().setBlockTypes([ifElseBlockType])

      store.getState().loadWorkflow({
        nodes: [{
          id: 'ifnode',
          blockTypeId: 'IfElse',
          position: { x: 100, y: 100 },
          configuration: {},
          outputPorts: [
            { name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: { Else: 'coolTemp' } }
          ]
        }],
        edges: []
      })

      store.getState().enterSubCanvas('ifnode', 'Then')

      const nodes = store.getState().nodes
      expect(nodes.some(n => n.id === WORKFLOW_OUTPUT_NODE_ID)).toBe(false)
    })
  })

  describe('validateWorkflowFindings (node identity)', () => {
    it('reports two separate findings with distinct nodeIds for two same-type nodes both missing a required input', () => {
      const blockType = makeBlockType({
        blockTypeId: 'SetDeviceState',
        displayName: 'Set Device State',
        inputs: [{ name: 'DeviceId', displayName: 'Device', type: 'String', isRequired: true }]
      })

      store.getState().setBlockTypes([blockType])
      store.getState().loadWorkflow({
        nodes: [
          { id: 'a', blockTypeId: 'SetDeviceState', position: { x: 0, y: 0 }, configuration: {} },
          { id: 'b', blockTypeId: 'SetDeviceState', position: { x: 0, y: 0 }, configuration: {} }
        ],
        edges: []
      })

      const findings = store.getState().validateWorkflowFindings()
      const deviceFindings = findings.filter(f => f.message.includes("missing required mapping for 'Device'"))
      expect(deviceFindings).toHaveLength(2)
      expect(deviceFindings.map(f => f.nodeId).sort()).toEqual(['a', 'b'])
    })

    it('tags a missing workflow-output finding with the Workflow Output node id', () => {
      store.getState().setBlockTypes([])
      store.getState().loadWorkflow({ nodes: [], edges: [] })
      store.getState().setWorkflowParams(
        [],
        [{ name: 'Result', displayName: 'Result', type: 'String', isRequired: true }]
      )

      const findings = store.getState().validateWorkflowFindings()
      const outputFinding = findings.find(f => f.message.includes("Workflow output 'Result'"))
      expect(outputFinding?.nodeId).toBe(WORKFLOW_OUTPUT_NODE_ID)
    })

    it('validateWorkflow() returns coded findings with the expected message', () => {
      const blockType = makeBlockType({
        blockTypeId: 'StringToInt',
        displayName: 'String to Int',
        inputs: [{ name: 'Value', displayName: 'Value', type: 'String', isRequired: true }]
      })

      store.getState().setBlockTypes([blockType])
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'StringToInt', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      expect(store.getState().validateWorkflow()).toMatchObject([{
        code: 'required-input-unfed',
        message: "Node 'String to Int' is missing required mapping for 'Value'",
      }])
    })
  })

  describe('flashNode', () => {
    it('sets flashNodeId and bumps flashSeq each call so repeat clicks re-trigger', () => {
      const before = store.getState().flashSeq
      store.getState().flashNode('node-x')

      const after1 = store.getState()
      expect(after1.flashNodeId).toBe('node-x')
      expect(after1.flashSeq).toBe(before + 1)

      store.getState().flashNode('node-x')
      expect(store.getState().flashSeq).toBe(before + 2)
    })
  })

  describe('validateWorkflowWarnings', () => {
    const ifElseBlockType = makeBlockType({
      blockTypeId: 'IfElse',
      displayName: 'If / Else',
      subCanvasNames: ['Then', 'Else'],
      subCanvasOutputs: {},
      subCanvasInputs: {},
      outputs: [],
    })

    it('emits a must-return warning when Else branch has no sources mapping for a declared output port', () => {
      store.getState().setBlockTypes([ifElseBlockType])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'ifnode',
          blockTypeId: 'IfElse',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [
            { name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: { Then: 'warmTemp' } }
          ],
          subCanvases: {
            Then: { nodes: [], edges: [] },
            Else: { nodes: [], edges: [] }
          }
        }],
        edges: []
      })

      const warnings = store.getState().validateWorkflowWarnings().map((f) => f.message)
      expect(warnings.some(w => w.includes('setpoint') && w.includes('Else'))).toBe(true)
    })

    it('does NOT emit a must-return warning when both branches have sources mapping', () => {
      store.getState().setBlockTypes([ifElseBlockType])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'ifnode',
          blockTypeId: 'IfElse',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [
            { name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: { Then: 'warmTemp', Else: 'coolTemp' } }
          ],
          subCanvases: {
            Then: {
              nodes: [{ id: '__workflow_output__', blockTypeId: 'WorkflowOutput', position: { x: 100, y: 0 }, configuration: {} }],
              edges: [{ id: 'e1', sourceNodeId: 'src', targetNodeId: '__workflow_output__', mappings: { warmTemp: { type: 'literal', literalValue: 22 } } }]
            },
            Else: {
              nodes: [{ id: '__workflow_output__', blockTypeId: 'WorkflowOutput', position: { x: 100, y: 0 }, configuration: {} }],
              edges: [{ id: 'e2', sourceNodeId: 'src2', targetNodeId: '__workflow_output__', mappings: { coolTemp: { type: 'literal', literalValue: 18 } } }]
            }
          }
        }],
        edges: []
      })

      const warnings = store.getState().validateWorkflowWarnings().map((f) => f.message)
      const setpointWarnings = warnings.filter(w => w.includes('setpoint'))
      expect(setpointWarnings).toHaveLength(0)
    })

    const branchSources = [
      makeBlockType({
        blockTypeId: 'StringSource', displayName: 'String Source',
        outputs: [{ name: 'out', displayName: 'out', isRequired: false, type: 'String' }],
      }),
      makeBlockType({
        blockTypeId: 'GenericSource', displayName: 'Generic Source', typeParameters: [{ name: 'T' }],
        outputs: [{ name: 'out', displayName: 'out', isRequired: false, type: '$T' }],
      }),
      makeBlockType({
        blockTypeId: 'GadgetSource', displayName: 'Gadget Source',
        outputs: [{ name: 'out', displayName: 'out', isRequired: false, type: 'Gadget' }],
      }),
    ]
    const ifWithThenSource = (thenSource: { blockTypeId: string } | { literalValue: string | number }) => ({
      nodes: [{
        id: 'ifnode',
        blockTypeId: 'IfElse',
        position: { x: 0, y: 0 },
        configuration: {},
        outputPorts: [{ name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: { Then: 'warmTemp' } }],
        subCanvases: {
          Then: {
            nodes: [
              ...('blockTypeId' in thenSource ? [{ id: 'src', blockTypeId: thenSource.blockTypeId, position: { x: 0, y: 0 }, configuration: {} }] : []),
              { id: '__workflow_output__', blockTypeId: 'WorkflowOutput', position: { x: 100, y: 0 }, configuration: {} },
            ],
            edges: [{
              id: 'e1', sourceNodeId: 'src', targetNodeId: '__workflow_output__',
              mappings: { warmTemp: 'blockTypeId' in thenSource ? { type: 'direct' as const, sourceOutputName: 'out' } : { type: 'literal' as const, literalValue: thenSource.literalValue } },
            }],
          },
          Else: { nodes: [], edges: [] },
        },
      }],
      edges: [],
    })
    const mismatches = () => store.getState().validateWorkflowWarningFindings().filter((f) => f.code.includes('type-mismatch'))

    it('compares no types between a branch and the declared port: the compiler grades the wire', () => {
      store.getState().setBlockTypes([ifElseBlockType, ...branchSources])
      for (const source of [{ blockTypeId: 'StringSource' }, { blockTypeId: 'GenericSource' }, { blockTypeId: 'GadgetSource' }, { literalValue: 'hot' }]) {
        store.getState().loadWorkflow(ifWithThenSource(source))

        expect(mismatches()).toEqual([])
      }
    })

    it('emits a collision ERROR (not warning) when declared port name duplicates a static output', () => {
      const blockTypeWithOutput = makeBlockType({
        blockTypeId: 'IfElse',
        displayName: 'If / Else',
        subCanvasNames: ['Then', 'Else'],
        subCanvasOutputs: {},
        subCanvasInputs: {},
        outputs: [{ name: 'setpoint', displayName: 'Setpoint', type: 'Number', isRequired: false }],
      })
      store.getState().setBlockTypes([blockTypeWithOutput])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'ifnode',
          blockTypeId: 'IfElse',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [
            { name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: { Then: 'warmTemp', Else: 'coolTemp' } }
          ]
        }],
        edges: []
      })

      const errors = store.getState().validateWorkflow()
      expect(errors.some(e => e.code === 'declared-output-shadows-static' && e.portName === 'setpoint')).toBe(true)
    })

    it('returns no warnings for a workflow with no outputPorts', () => {
      store.getState().setBlockTypes([ifElseBlockType])
      store.getState().loadWorkflow({
        nodes: [{ id: 'n1', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {} }],
        edges: []
      })

      const warnings = store.getState().validateWorkflowWarnings().map((f) => f.message)
      expect(warnings).toHaveLength(0)
    })

    it('validateWorkflowWarnings does NOT produce errors that block save (warnings are soft)', () => {
      store.getState().setBlockTypes([ifElseBlockType])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'ifnode',
          blockTypeId: 'IfElse',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [
            { name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: { Then: 'warmTemp' } }
          ]
        }],
        edges: []
      })

      const errors = store.getState().validateWorkflow()
      expect(errors.every(e => !(e.message.includes('setpoint') && e.message.includes('Else')))).toBe(true)
    })

    it('does NOT duplicate sub-canvas warnings when a node has multiple declared output ports (no double-recursion)', () => {
      store.getState().setBlockTypes([ifElseBlockType])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'ifnode',
          blockTypeId: 'IfElse',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [
            { name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: {} },
            { name: 'label',    displayName: 'Label',    type: 'String', sources: {} },
          ],
          subCanvases: {
            Then: {
              nodes: [{
                id: 'inner-if',
                blockTypeId: 'IfElse',
                position: { x: 10, y: 0 },
                configuration: {},
                outputPorts: [
                  { name: 'innerVal', displayName: 'InnerVal', type: 'Number', sources: {} },
                ],
                subCanvases: {
                  Then: { nodes: [], edges: [] },
                  Else: { nodes: [], edges: [] },
                },
              }],
              edges: [],
            },
            Else: { nodes: [], edges: [] },
          },
        }],
        edges: []
      })

      const warnings = store.getState().validateWorkflowWarnings().map((f) => f.message)

      expect(warnings.length).toBe(new Set(warnings).size)

      const innerThen = warnings.filter(w => w.includes('innerVal') && w.startsWith("Branch 'Then'"))
      const innerElse = warnings.filter(w => w.includes('innerVal') && w.startsWith("Branch 'Else'"))
      expect(innerThen).toHaveLength(1)
      expect(innerElse).toHaveLength(1)
    })

    const tryCatchBlockType = makeBlockType({
      blockTypeId: 'TryCatch',
      displayName: 'Try / Catch',
      subCanvasNames: ['Try', 'Catch', 'Finally'],
      subCanvasOutputs: {},
      subCanvasInputs: {},
      outputs: [],
      supportsDeclaredOutputs: true,
      outputSatisfyingBranchSets: [['Finally'], ['Try', 'Catch']],
    })

    it('D1-1: TryCatch port mapped ONLY in Finally => NO must-return warning (Finally always runs last-write-wins)', () => {
      store.getState().setBlockTypes([tryCatchBlockType])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'tc',
          blockTypeId: 'TryCatch',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [
            { name: 'result', displayName: 'Result', type: 'String', sources: { Finally: 'finalResult' } }
          ],
          subCanvases: {
            Try: { nodes: [], edges: [] },
            Catch: { nodes: [], edges: [] },
            Finally: {
              nodes: [{ id: '__workflow_output__', blockTypeId: 'WorkflowOutput', position: { x: 100, y: 0 }, configuration: {} }],
              edges: [{ id: 'ef', sourceNodeId: 'src', targetNodeId: '__workflow_output__', mappings: { finalResult: { type: 'literal', literalValue: 'ok' } } }]
            }
          }
        }],
        edges: []
      })

      const warnings = store.getState().validateWorkflowWarnings().map((f) => f.message)
      const resultWarnings = warnings.filter(w => w.includes('result'))
      expect(resultWarnings).toHaveLength(0)
    })

    it('D1-2: TryCatch port mapped in BOTH Try AND Catch (no Finally) => NO must-return warning', () => {
      store.getState().setBlockTypes([tryCatchBlockType])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'tc',
          blockTypeId: 'TryCatch',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [
            { name: 'result', displayName: 'Result', type: 'String', sources: { Try: 'tryResult', Catch: 'catchResult' } }
          ],
          subCanvases: {
            Try: {
              nodes: [{ id: '__workflow_output__', blockTypeId: 'WorkflowOutput', position: { x: 100, y: 0 }, configuration: {} }],
              edges: [{ id: 'et', sourceNodeId: 'src', targetNodeId: '__workflow_output__', mappings: { tryResult: { type: 'literal', literalValue: 'success' } } }]
            },
            Catch: {
              nodes: [{ id: '__workflow_output__', blockTypeId: 'WorkflowOutput', position: { x: 100, y: 0 }, configuration: {} }],
              edges: [{ id: 'ec', sourceNodeId: 'src', targetNodeId: '__workflow_output__', mappings: { catchResult: { type: 'literal', literalValue: 'error' } } }]
            },
            Finally: { nodes: [], edges: [] }
          }
        }],
        edges: []
      })

      const warnings = store.getState().validateWorkflowWarnings().map((f) => f.message)
      const resultWarnings = warnings.filter(w => w.includes('result'))
      expect(resultWarnings).toHaveLength(0)
    })

    it('D1-3: TryCatch port mapped ONLY in Try (no Catch, no Finally) => warns (error path may be null)', () => {
      store.getState().setBlockTypes([tryCatchBlockType])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'tc',
          blockTypeId: 'TryCatch',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [
            { name: 'result', displayName: 'Result', type: 'String', sources: { Try: 'tryResult' } }
          ],
          subCanvases: {
            Try: {
              nodes: [{ id: '__workflow_output__', blockTypeId: 'WorkflowOutput', position: { x: 100, y: 0 }, configuration: {} }],
              edges: [{ id: 'et', sourceNodeId: 'src', targetNodeId: '__workflow_output__', mappings: { tryResult: { type: 'literal', literalValue: 'success' } } }]
            },
            Catch: { nodes: [], edges: [] },
            Finally: { nodes: [], edges: [] }
          }
        }],
        edges: []
      })

      const warnings = store.getState().validateWorkflowWarnings().map((f) => f.message)
      expect(warnings.some(w => w.includes('result'))).toBe(true)
    })

    it('D1-IfElse: IfElse must-return rule is UNCHANGED (per-branch, independent)', () => {
      store.getState().setBlockTypes([ifElseBlockType])
      store.getState().loadWorkflow({
        nodes: [{
          id: 'ifnode',
          blockTypeId: 'IfElse',
          position: { x: 0, y: 0 },
          configuration: {},
          outputPorts: [
            { name: 'setpoint', displayName: 'Setpoint', type: 'Number', sources: { Then: 'warmTemp' } }
          ],
          subCanvases: {
            Then: { nodes: [], edges: [] },
            Else: { nodes: [], edges: [] }
          }
        }],
        edges: []
      })

      const warnings = store.getState().validateWorkflowWarnings().map((f) => f.message)
      expect(warnings.some(w => w.includes('setpoint') && w.includes('Else'))).toBe(true)
    })
  })
})

describe('host-contributed validation rules', () => {
  const widget = makeBlockType({
    blockTypeId: 'Widget',
    displayName: 'Widget',
    inputs: [{ name: 'Value', displayName: 'Value', isRequired: false, type: 'Any' }],
    outputs: [{ name: 'Status', displayName: 'Status', isRequired: false, type: 'String' }],
  })
  const boolSource = makeBlockType({
    blockTypeId: 'BoolSource',
    displayName: 'Bool Source',
    outputs: [{ name: 'out', displayName: 'out', isRequired: false, type: 'Boolean' }],
  })
  const stringSource = makeBlockType({
    blockTypeId: 'StringSource',
    displayName: 'String Source',
    outputs: [{ name: 'out', displayName: 'out', isRequired: false, type: 'String' }],
  })

  const wiredValueMustMatchConfiguredKind: NodeValidationRule = ({ node, edges, label, resolveOutputType, portTypes: reg }) => {
    const expected = node.configuration['Kind']
    if (typeof expected !== 'string' || expected === '') return []
    const edge = edges.find((e) => e.targetNodeId === node.id && e.mappings['Value'])
    const mapping = edge?.mappings['Value']
    if (!edge || !mapping || mapping.type !== 'direct' || !mapping.sourceOutputName) return []
    const incoming = resolveOutputType(edge.sourceNodeId, mapping.sourceOutputName)
    if (incoming === undefined || reg.normalize(incoming) === reg.normalize(expected)) return []
    return [{ nodeId: node.id, code: 'host-kind-mismatch', message: `Port 'Value' of '${label}' expects type ${expected}, but the connected source has type ${reg.normalize(incoming)}` }]
  }

  const capabilityMustBeChosen: NodeValidationRule = ({ node, label }) => {
    const key = node.configuration['CapabilityKey']
    return key === undefined || key === null || key === ''
      ? [{ nodeId: node.id, code: 'host-capability-required', message: `Node '${label}' has no capability selected` }]
      : []
  }

  const loadWired = (target: WorkflowEditorStore, sourceBlockTypeId: string) => {
    target.getState().setBlockTypes([boolSource, stringSource, widget])
    target.getState().loadWorkflow({
      nodes: [
        { id: 'src', blockTypeId: sourceBlockTypeId, position: { x: 0, y: 0 }, configuration: {} },
        { id: 'w', blockTypeId: 'Widget', position: { x: 200, y: 0 }, configuration: { Kind: 'Boolean', CapabilityKey: 'x' } },
      ],
      edges: [{ id: 'e1', sourceNodeId: 'src', targetNodeId: 'w', mappings: { Value: { type: 'direct', sourceOutputName: 'out' } } }],
    })
  }

  it('lets a host warning rule report a mismatch the library could not know about', () => {
    const withRules = newStore({ nodeWarningRules: [wiredValueMustMatchConfiguredKind] })
    loadWired(withRules, 'StringSource')
    const finding = withRules.getState().validateWorkflowWarningFindings().find((f) => f.nodeId === 'w')
    expect(finding?.message).toContain('Boolean')
  })

  it('stays silent when the host rule is satisfied', () => {
    const withRules = newStore({ nodeWarningRules: [wiredValueMustMatchConfiguredKind] })
    loadWired(withRules, 'BoolSource')
    expect(withRules.getState().validateWorkflowWarningFindings().find((f) => f.nodeId === 'w')).toBeUndefined()
  })

  it('produces nothing without the rule, so the finding really comes from the host', () => {
    const withoutRules = newStore()
    loadWired(withoutRules, 'StringSource')
    expect(withoutRules.getState().validateWorkflowWarningFindings()).toEqual([])
  })

  it('lets a host error rule block a save', () => {
    const withRules = newStore({ nodeErrorRules: [capabilityMustBeChosen] })
    withRules.getState().setBlockTypes([widget])
    withRules.getState().loadWorkflow({
      nodes: [{ id: 'w', blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: { CapabilityKey: '' } }],
      edges: [],
    })
    expect(withRules.getState().validateWorkflowFindings().some((e) => /capabilit/i.test(e.message))).toBe(true)
  })

  it('clears the host error once the node satisfies it', () => {
    const withRules = newStore({ nodeErrorRules: [capabilityMustBeChosen] })
    withRules.getState().setBlockTypes([widget])
    withRules.getState().loadWorkflow({
      nodes: [{ id: 'w', blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: { CapabilityKey: 'light.isOn' } }],
      edges: [],
    })
    expect(withRules.getState().validateWorkflowFindings().some((e) => /capabilit/i.test(e.message))).toBe(false)
  })

  it('reports a throwing rule as its own finding instead of losing every other node', () => {
    const exploding: NodeValidationRule = () => {
      throw new Error('rule is broken')
    }
    const withRules = newStore({ nodeErrorRules: [exploding] })
    withRules.getState().setBlockTypes([widget])
    withRules.getState().loadWorkflow({
      nodes: [{ id: 'w', blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: {} }],
      edges: [],
    })
    expect(withRules.getState().validateWorkflowFindings().some((e) => e.message.includes('rule is broken'))).toBe(true)
  })
})

describe('adaptBlockType', () => {
  it.each(['inputs', 'outputs', 'subCanvasInputs', 'subCanvasOutputs'] as const)('reports and leaves out a dotted host adaptation in %s while registering valid descriptors', (side) => {
    const errors: unknown[] = []
    const port = { name: 'result.value', displayName: 'Result', type: 'String', isRequired: false }
    const target = newStore({
      onBlockTypeError: (error: unknown) => { errors.push(error) },
      adaptBlockType: (bt) => bt.blockTypeId !== 'Dotted' ? bt : {
        ...bt,
        ...(side === 'inputs' || side === 'outputs' ? { [side]: [port] } : { [side]: { Body: [port] } }),
      },
    })
    target.getState().setBlockTypes([makeBlockType({ blockTypeId: 'Before' }), makeBlockType({ blockTypeId: 'Dotted' }), makeBlockType({ blockTypeId: 'After' })])

    expect(target.getState().blockTypes.map((bt) => bt.blockTypeId)).toEqual(['Before', 'After'])
    expect(errors).toHaveLength(1)
    expect((errors[0] as Error).message).toMatch(/Block type 'Dotted'.*result\.value.*dotted-name/)
  })

  it('still throws a dotted host adaptation when no refusal handler is supplied', () => {
    const target = newStore({ adaptBlockType: (bt) => ({ ...bt, outputs: [{ name: 'result.value', displayName: 'Result', type: 'String', isRequired: false }] }) })
    expect(() => target.getState().setBlockTypes([makeBlockType()])).toThrow(/dotted-name/)
  })

  it('reports a failed host adaptation and continues with the next descriptor', () => {
    const errors: unknown[] = []
    const failure = new Error('Host catalogue field is missing')
    const target = newStore({
      onBlockTypeError: (error) => { errors.push(error) },
      adaptBlockType: (bt) => { if (bt.blockTypeId === 'Broken') throw failure; return bt },
    })
    target.getState().setBlockTypes([makeBlockType({ blockTypeId: 'Broken' }), makeBlockType({ blockTypeId: 'Good' })])

    expect(target.getState().blockTypes.map((bt) => bt.blockTypeId)).toEqual(['Good'])
    expect(errors).toHaveLength(1)
    expect(errors[0]).toMatchObject({ message: "Block type 'Broken' is left out of the palette: Host catalogue field is missing", cause: failure })
  })

  it('fills in a descriptor field the host catalogue does not carry', () => {
    const store = newStore({
      adaptBlockType: (bt) =>
        bt.blockTypeId === 'TryCatch' ? { ...bt, outputSatisfyingBranchSets: [['Finally'], ['Try', 'Catch']] } : bt,
    })
    store.getState().setBlockTypes([
      { blockTypeId: 'TryCatch', displayName: 'Try / Catch', inputs: [], outputs: [] },
      { blockTypeId: 'Other', displayName: 'Other', inputs: [], outputs: [] },
    ])
    expect(store.getState().blockTypes[0]?.outputSatisfyingBranchSets).toEqual([['Finally'], ['Try', 'Catch']])
    expect(store.getState().blockTypes[1]?.outputSatisfyingBranchSets).toBeUndefined()
  })

  it('leaves the catalogue alone when the host supplies no adapter', () => {
    const store = newStore()
    const descriptor = { blockTypeId: 'Plain', displayName: 'Plain', inputs: [], outputs: [] }
    store.getState().setBlockTypes([descriptor])
    expect(store.getState().blockTypes[0]).toBe(descriptor)
  })
})
