import { describe, expect, it } from 'vitest'
import type { BlockPort, BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowEdge, WorkflowNode, WorkflowParameter } from '../model/workflow.js'
import { WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_OUTPUT_BLOCK_TYPE_ID } from '../model/workflow.js'
import { buildPortTypeScopes, createPortTypeResolver } from './portTypeResolver.js'

type Port = { name: string; type: string }

const port = (p: Port): BlockPort => ({ name: p.name, displayName: p.name, type: p.type, isRequired: false })

function blockType(
  blockTypeId: string,
  opts: {
    inputs?: Port[]
    outputs?: Port[]
    hasDynamicPorts?: boolean
    subCanvasInputs?: Record<string, Port[]>
    subCanvasOutputs?: Record<string, Port[]>
  } = {},
): BlockTypeDescriptor {
  return {
    blockTypeId,
    displayName: blockTypeId,
    description: '',
    category: 'test',
    inputs: (opts.inputs ?? []).map(port),
    outputs: (opts.outputs ?? []).map(port),
    hasDynamicPorts: opts.hasDynamicPorts ?? false,
    configurationSchema: [],
    subCanvasNames: Object.keys(opts.subCanvasInputs ?? opts.subCanvasOutputs ?? {}),
    subCanvasInputs: Object.fromEntries(
      Object.entries(opts.subCanvasInputs ?? {}).map(([k, v]) => [k, v.map(port)]),
    ),
    subCanvasOutputs: Object.fromEntries(
      Object.entries(opts.subCanvasOutputs ?? {}).map(([k, v]) => [k, v.map(port)]),
    ),
  }
}

const node = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode => ({
  id,
  blockTypeId,
  position: { x: 0, y: 0 },
  configuration: {},
  ...extra,
})

const directEdge = (source: string, target: string, mappings: Record<string, string>): WorkflowEdge => ({
  id: `e_${source}_${target}`,
  sourceNodeId: source,
  targetNodeId: target,
  mappings: Object.fromEntries(
    Object.entries(mappings).map(([targetInput, sourceOutput]) => [
      targetInput,
      { type: 'direct' as const, sourceOutputName: sourceOutput },
    ]),
  ),
})

const WAIT = blockType('WaitForDuration', { inputs: [{ name: 'DurationMs', type: 'number' }] })
const SET = blockType('SetDeviceState', {
  inputs: [{ name: 'DeviceId', type: 'Device' }, { name: 'Value', type: 'Json' }],
})
const IFELSE = blockType('IfElse')
const blockTypes = [WAIT, SET, IFELSE]

describe('a name forwarded through a top-level passthrough', () => {
  const nodes = [
    node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID),
    node('w', 'WaitForDuration', { passthrough: ['Lock'] }),
    node('lk', 'SetDeviceState'),
  ]
  const edges = [
    directEdge('__workflow_input__', 'w', { DurationMs: 'DelayMs', Lock: 'Lock' }),
    directEdge('w', 'lk', { DeviceId: 'Lock' }),
  ]
  const workflowInputParams: WorkflowParameter[] = [
    { name: 'Lock', displayName: 'Lock', type: 'Device', isRequired: true },
    { name: 'DelayMs', displayName: 'DelayMs', type: 'number', isRequired: true },
  ]
  const resolver = createPortTypeResolver({
    scopes: buildPortTypeScopes({ nodes, edges, canvasStack: [] }),
    blockTypes,
    workflowInputParams,
  })

  it('has no type on the forwarding node virtual input, though the wire into it comes from a typed source', () => {
    expect(resolver.resolveInputType(0, 'w', 'Lock')).toBeUndefined()
  })

  it('has no type on the forwarding node virtual output', () => {
    expect(resolver.resolveOutputType(0, 'w', 'Lock')).toBeUndefined()
  })

  it('reads the declared input of the downstream consumer from its block type', () => {
    expect(resolver.resolveInputType(0, 'lk', 'DeviceId')).toBe('Device')
  })

  it('still reports a declared input from the block type', () => {
    expect(resolver.resolveInputType(0, 'w', 'DurationMs')).toBe('number')
  })
})

describe('a declared input that a passthrough entry also forwards', () => {
  const resolver = createPortTypeResolver({
    scopes: buildPortTypeScopes({
      nodes: [node('w', 'WaitForDuration', { passthrough: ['DurationMs'] })],
      edges: [],
      canvasStack: [],
    }),
    blockTypes,
    workflowInputParams: [],
  })

  it('is re-emitted as an output under its declared term', () => {
    expect(resolver.resolveOutputType(0, 'w', 'DurationMs')).toBe('number')
  })

  it('is emitted by the entry node of a sub-canvas under its declared term', () => {
    const SEEDED = blockType('Seeded', { inputs: [{ name: 'Seed', type: 'Room' }], subCanvasInputs: { Body: [] } })
    const parent = node('s', 'Seeded', { passthrough: ['Seed'] })
    const inside = createPortTypeResolver({
      scopes: buildPortTypeScopes({
        nodes: [node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID)], edges: [],
        canvasStack: [{ parentNodeId: 's', subCanvasName: 'Body', nodes: [parent], edges: [] }],
      }),
      blockTypes: [SEEDED],
      workflowInputParams: [],
    })

    expect(inside.resolveOutputType(0, '__workflow_input__', 'Seed')).toBe('Room')
  })
})

describe('a name forwarded into a sub-canvas', () => {
  const topNodes = [
    node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID),
    node('ifn', 'IfElse', { passthrough: ['Radiator', 'Setpoint'] }),
  ]
  const topEdges = [directEdge('__workflow_input__', 'ifn', { Radiator: 'Radiator', Setpoint: 'Setpoint' })]
  const thenNodes = [node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID), node('t_set', 'SetDeviceState')]
  const thenEdges = [directEdge('__workflow_input__', 't_set', { DeviceId: 'Radiator', Value: 'Setpoint' })]

  const resolver = createPortTypeResolver({
    scopes: buildPortTypeScopes({
      nodes: thenNodes,
      edges: thenEdges,
      canvasStack: [{ parentNodeId: 'ifn', subCanvasName: 'Then', nodes: topNodes, edges: topEdges }],
    }),
    blockTypes,
    workflowInputParams: [
      { name: 'Radiator', displayName: 'Radiator', type: 'Device', isRequired: true },
      { name: 'Setpoint', displayName: 'Setpoint', type: 'number', isRequired: true },
    ],
  })

  it('resolves a consumer inside the branch across the sub-canvas boundary', () => {
    expect(resolver.resolveInputType(0, 't_set', 'DeviceId')).toBe('Device')
  })

  it('answers nothing for a forwarded value on the branch entry node, which the block does not declare', () => {
    expect(resolver.resolveOutputType(0, '__workflow_input__', 'Radiator')).toBeUndefined()
    expect(resolver.resolveOutputType(0, '__workflow_input__', 'Setpoint')).toBeUndefined()
  })

  it('answers nothing for a name the parent neither declares nor forwards', () => {
    expect(resolver.resolveOutputType(0, '__workflow_input__', 'NeverForwarded')).toBeUndefined()
  })
})

describe('a block that declares its ports in its own configuration', () => {
  const DYNAMIC = blockType('Dynamic', { hasDynamicPorts: true })
  const resolver = createPortTypeResolver({
    scopes: buildPortTypeScopes({
      nodes: [
        node('d', 'Dynamic', {
          configuration: {
            InputPorts: '[{"name":"In","type":"Room","isRequired":true}]',
            OutputPorts: '[{"name":"Out","type":"Scene","isRequired":false}]',
          },
        }),
      ],
      edges: [],
      canvasStack: [],
    }),
    blockTypes: [DYNAMIC],
    workflowInputParams: [],
  })

  it('reads a dynamic input port type', () => {
    expect(resolver.resolveInputType(0, 'd', 'In')).toBe('Room')
  })

  it('reads a dynamic output port type', () => {
    expect(resolver.resolveOutputType(0, 'd', 'Out')).toBe('Scene')
  })
})

describe('robustness', () => {
  const resolver = createPortTypeResolver({
    scopes: buildPortTypeScopes({
      nodes: [
        node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID),
        node('w', 'WaitForDuration', { passthrough: ['Note'] }),
      ],
      edges: [
        {
          id: 'e_lit',
          sourceNodeId: '__workflow_input__',
          targetNodeId: 'w',
          mappings: { Note: { type: 'literal', literalValue: 'hello' } },
        },
      ],
      canvasStack: [],
    }),
    blockTypes,
    workflowInputParams: [],
  })

  it.each([
    ['text', { type: 'literal', literalValue: 'hello' }],
    ['a number', { type: 'literal', literalValue: 7 }],
    ['null', { type: 'literal', literalValue: null }],
    ['a list', { type: 'literal', literalValue: [1, 2] }],
    ['a wire from a typed source', { type: 'direct', sourceOutputName: 'Count' }],
    ['a projection that states a closed term', { type: 'direct', sourceOutputName: 'Payload', projection: { path: '$.n', type: 'Int' } }],
    ['a projection that states none', { type: 'direct', sourceOutputName: 'Payload', projection: { path: '$.n' } }],
  ])('answers nothing for a passthrough name fed by %s', (_why, mapping) => {
    const fed = createPortTypeResolver({
      scopes: buildPortTypeScopes({
        nodes: [
          node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID),
          node('w', 'WaitForDuration', { passthrough: ['Note'] }),
        ],
        edges: [{ id: 'e_feed', sourceNodeId: '__workflow_input__', targetNodeId: 'w', mappings: { Note: mapping as never } }],
        canvasStack: [],
      }),
      blockTypes,
      workflowInputParams: [
        { name: 'Payload', displayName: 'Payload', isRequired: true, type: 'Json' },
        { name: 'Count', displayName: 'Count', isRequired: true, type: 'Int' },
      ],
    })

    expect(fed.resolveInputType(0, 'w', 'Note')).toBeUndefined()
    expect(fed.resolveOutputType(0, 'w', 'Note')).toBeUndefined()
  })

  it('lets a declared input type win over the feeding wire', () => {
    expect(resolver.resolveInputType(0, 'w', 'DurationMs')).toBe('number')
  })

  it('answers nothing for a port that exists nowhere', () => {
    expect(resolver.resolveOutputType(0, 'w', 'Nonexistent')).toBeUndefined()
  })

  it('answers nothing for a scope index past the end of the chain', () => {
    expect(resolver.resolveInputType(9, 'w', 'Note')).toBeUndefined()
  })
})

describe('a declared term that names a type parameter', () => {
  const GENERIC = blockType('Generic', {
    inputs: [{ name: 'Item', type: '$T' }, { name: 'Items', type: 'Collection<$T?>' }, { name: 'Count', type: 'Int' }],
    outputs: [
      { name: 'Echo', type: '$T' }, { name: 'Many', type: 'Collection<$T?>' },
      { name: 'Total', type: 'Int' }, { name: 'Maybe', type: 'Int?' }, { name: 'Rooms', type: 'Collection<Room>' },
    ],
    subCanvasInputs: { Body: [{ name: 'Current', type: '$T' }, { name: 'Room', type: 'Room' }] },
  })
  const nodes = [node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID), node('g', 'Generic')]
  const resolver = createPortTypeResolver({
    scopes: buildPortTypeScopes({ nodes, edges: [], canvasStack: [] }), blockTypes: [GENERIC], workflowInputParams: [],
  })

  it.each(['Echo', 'Many'])('answers nothing for the declared output %s', (name) => {
    expect(resolver.resolveOutputType(0, 'g', name)).toBeUndefined()
  })

  it.each(['Item', 'Items'])('answers nothing for the declared input %s', (name) => {
    expect(resolver.resolveInputType(0, 'g', name)).toBeUndefined()
  })

  it('answers a closed declared term as written, nullable and collection included', () => {
    expect(resolver.resolveOutputType(0, 'g', 'Total')).toBe('Int')
    expect(resolver.resolveOutputType(0, 'g', 'Maybe')).toBe('Int?')
    expect(resolver.resolveOutputType(0, 'g', 'Rooms')).toBe('Collection<Room>')
    expect(resolver.resolveInputType(0, 'g', 'Count')).toBe('Int')
  })

  it('answers nothing for a sub-canvas input that names the parameter, and the term of one that does not', () => {
    const body = [node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID)]
    const inside = createPortTypeResolver({
      scopes: buildPortTypeScopes({
        nodes: body, edges: [], canvasStack: [{ parentNodeId: 'g', subCanvasName: 'Body', nodes, edges: [] }],
      }),
      blockTypes: [GENERIC],
      workflowInputParams: [],
    })

    expect(inside.resolveOutputType(0, '__workflow_input__', 'Current')).toBeUndefined()
    expect(inside.resolveOutputType(0, '__workflow_input__', 'Room')).toBe('Room')
  })

  it('answers nothing for a passthrough name fed from an output that names the parameter', () => {
    const SINK = blockType('Sink', { inputs: [{ name: 'Count', type: 'Int' }] })
    const fed = createPortTypeResolver({
      scopes: buildPortTypeScopes({
        nodes: [node('g', 'Generic'), node('s', 'Sink', { passthrough: ['Echo'] })],
        edges: [directEdge('g', 's', { Echo: 'Echo' })],
        canvasStack: [],
      }),
      blockTypes: [GENERIC, SINK],
      workflowInputParams: [],
    })

    expect(fed.resolveInputType(0, 's', 'Echo')).toBeUndefined()
    expect(fed.resolveOutputType(0, 's', 'Echo')).toBeUndefined()
  })

  it('does not instantiate a parameter from what is wired in: the compiler binds it', () => {
    const ROOMS = blockType('Rooms', { outputs: [{ name: 'Rooms', type: 'Collection<Room>' }] })
    const fed = createPortTypeResolver({
      scopes: buildPortTypeScopes({
        nodes: [node('src', 'Rooms'), node('g', 'Generic')],
        edges: [directEdge('src', 'g', { Items: 'Rooms' })],
        canvasStack: [],
      }),
      blockTypes: [GENERIC, ROOMS],
      workflowInputParams: [],
    })

    expect(fed.resolveOutputType(0, 'g', 'Many')).toBeUndefined()
    expect(fed.resolveInputType(0, 'g', 'Items')).toBeUndefined()
  })
})

describe('a stored term the editor does not read as one', () => {
  const DYNAMIC = blockType('Dynamic', { hasDynamicPorts: true })
  const dynamic = (inputs: string, outputs: string) => createPortTypeResolver({
    scopes: buildPortTypeScopes({
      nodes: [node('d', 'Dynamic', { configuration: { InputPorts: inputs, OutputPorts: outputs } })], edges: [], canvasStack: [],
    }),
    blockTypes: [DYNAMIC],
    workflowInputParams: [],
  })

  it.each(['$T', 'Collection', 'Int??', ''])('answers nothing for a dynamic port typed %j', (type) => {
    const resolver = dynamic(`[{"name":"In","type":"${type}","isRequired":false}]`, `[{"name":"Out","type":"${type}","isRequired":false}]`)

    expect(resolver.resolveInputType(0, 'd', 'In')).toBeUndefined()
    expect(resolver.resolveOutputType(0, 'd', 'Out')).toBeUndefined()
  })

  it.each(['$T', 'Collection', 'Int??'])('answers nothing for a workflow input parameter typed %j', (type) => {
    const resolver = createPortTypeResolver({
      scopes: buildPortTypeScopes({ nodes: [node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID)], edges: [], canvasStack: [] }),
      blockTypes: [],
      workflowInputParams: [{ name: 'P', displayName: 'P', type, isRequired: true }],
    })

    expect(resolver.resolveOutputType(0, '__workflow_input__', 'P')).toBeUndefined()
  })
})

describe('a declared output of the node', () => {
  const declaring = (declaredOutputsCollect: boolean) => ({
    ...blockType('Loop', { outputs: [{ name: 'Fixed', type: 'Int' }] }),
    supportsDeclaredOutputs: true,
    declaredOutputsCollect,
  })
  const resolve = (declaredOutputsCollect: boolean, outputPorts: WorkflowNode['outputPorts'], name: string) => createPortTypeResolver({
    scopes: buildPortTypeScopes({ nodes: [node('l', 'Loop', { outputPorts })], edges: [], canvasStack: [] }),
    blockTypes: [declaring(declaredOutputsCollect)],
    workflowInputParams: [],
  }).resolveOutputType(0, 'l', name)

  it('has its stored term on a block that does not collect', () => {
    expect(resolve(false, [{ name: 'Seen', type: 'Room' }], 'Seen')).toBe('Room')
  })

  it('has the collected term on a block that collects: the stored term is the body exit, the output a nullable collection of it', () => {
    expect(resolve(true, [{ name: 'Seen', type: 'Room' }], 'Seen')).toBe('Collection<Room?>')
    expect(resolve(true, [{ name: 'Seen', type: 'Room?' }], 'Seen')).toBe('Collection<Room?>')
  })

  it('has none where the stored term is malformed', () => {
    expect(resolve(false, [{ name: 'Seen', type: 'Collection' }], 'Seen')).toBeUndefined()
    expect(resolve(true, [{ name: 'Seen', type: 'Int??' }], 'Seen')).toBeUndefined()
  })

  it('has none on a node whose block the catalogue does not know', () => {
    const unknown = createPortTypeResolver({
      scopes: buildPortTypeScopes({ nodes: [node('l', 'Nowhere', { outputPorts: [{ name: 'Seen', type: 'Room' }] })], edges: [], canvasStack: [] }),
      blockTypes: [],
      workflowInputParams: [],
    })

    expect(unknown.resolveOutputType(0, 'l', 'Seen')).toBeUndefined()
  })

  it('leaves the static output of that name in charge', () => {
    expect(resolve(false, [{ name: 'Fixed', type: 'Room' }], 'Fixed')).toBe('Int')
  })
})

describe('the error output', () => {
  const resolve = (errorPolicy: WorkflowNode['errorPolicy'], blockTypeId = 'WaitForDuration') => {
    const nodes = [node('__workflow_input__', WORKFLOW_INPUT_BLOCK_TYPE_ID), node('w', blockTypeId, { errorPolicy })]
    return createPortTypeResolver({ scopes: buildPortTypeScopes({ nodes, edges: [], canvasStack: [] }), blockTypes, workflowInputParams: [] })
      .resolveOutputType(0, 'w', '_error')
  }

  it('is Json under EmitError', () => {
    expect(resolve({ policy: 'EmitError' })).toBe('Json')
  })

  it.each([undefined, null, { policy: 'Skip' as const }, { policy: 'FailWorkflow' as const, routeToErrorPort: true }])(
    'is no output under %j', (policy) => {
      expect(resolve(policy)).toBeUndefined()
    })

  it('is no output of a boundary node, which never runs a block', () => {
    expect(resolve({ policy: 'EmitError' }, WORKFLOW_OUTPUT_BLOCK_TYPE_ID)).toBeUndefined()
  })
})
