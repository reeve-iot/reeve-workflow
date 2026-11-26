import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type {
  CanvasPathSegment,
  ValidationFinding,
  VariableMapping,
  WorkflowDefinition,
  WorkflowEdge,
  WorkflowNode,
  WorkflowParameter,
} from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import { validationMessage } from './messageCatalogue.js'
import { readStoredDefinitionText } from '../model/storedDocument.js'
import { createWorkflowEditorStore } from './store.js'
import { validateStructureFindings } from './validateStructure.js'

type Depth = 'root' | 'body' | 'nested'
type Variant = (typeof VARIANTS)[number]
interface Spec { depth: Depth; variant: Variant | undefined }
interface Signature { inputs: { name: string; isRequired: boolean }[]; outputs: { name: string; isRequired: boolean }[] }
interface Case { name: string; definition: WorkflowDefinition; signature?: Signature; expect: string[]; compilerOnly?: string[]; editor?: string[] }
interface PortRow { name: string; isRequired: boolean; type?: string }
interface BlockRow {
  blockTypeId: string
  displayName: string
  inputs: PortRow[]
  outputs: string[]
  requiredConfigFields: string[]
  enumFields?: { name: string; values: string[]; required?: boolean; defaultValue?: string }[]
  typeParameters?: { name: string; constraints: string[] }[]
  subCanvases: string[]
  subCanvasInputs: Record<string, PortRow[]>
  subCanvasOutputs: Record<string, PortRow[]>
  runsWorkflowFrom: string | null
  hasDynamicPorts: boolean
  supportsDeclaredOutputs: boolean
  declaredOutputsCollect?: boolean
}
interface ParityRow { id: string; class: 'editor' | 'compiler-only'; depth?: 'every' | 'root' | 'document' | 'registration'; agreement?: string }

const DEPTHS: readonly Depth[] = ['root', 'body', 'nested']
const VARIANTS = [
  'unfed', 'configured', 'configured-empty', 'configured-number', 'configured-bool', 'literal', 'literal-object', 'wired',
  'wired-whitespace', 'wired-unnamed', 'wired-from-another-canvas', 'config-missing', 'config-empty',
  'shadow', 'declared', 'literal-from-another-canvas', 'literal-object-from-another-canvas',
  'cycle', 'cycle-unfed', 'cycle-wire-unknown', 'cycle-configured-number', 'extra-root', 'unreachable', 'no-start', 'input-has-incoming', 'duplicate-mapping', 'duplicate-edge-pair',
  'duplicate-id', 'duplicate-id-unfed', 'duplicate-id-two-types', 'duplicate-id-unknown-type', 'undeclared-branch', 'uncatalogued-container',
  'uncatalogued-outer-container', 'duplicate-id-shared-branch',
  'reference-mapped', 'reference-mapped-inactive', 'reference-missing', 'reference-empty', 'reference-configured',
  'timeout-text', 'timeout-empty', 'timeout-too-long', 'timeout-longest', 'retry-fraction', 'retry-huge',
  'retry-enabled-text', 'backoff-text', 'backoff-fraction',
  'branch-output-unfed', 'branch-output-fed', 'branch-output-config', 'branch-output-unknown', 'branch-output-cycle',
  'branch-ports',
  'malformed-declared', 'malformed-projection-type', 'wire-unknown-output', 'wire-unknown-input', 'wire-declared-output', 'wire-entry-unknown',
  'wire-passthrough', 'wire-passthrough-config-number', 'wire-passthrough-config-text', 'wire-malformed-end', 'wire-reference-end', 'wire-unknown-block-end', 'dynamic-unfed', 'dynamic-fed', 'dynamic-unknown',
  'dynamic-kept-keys',
  'wired-no-break-space', 'wired-next-line', 'wired-line-separator', 'wired-zero-width-space',
  'passthrough-untraced', 'passthrough-config', 'passthrough-config-object', 'passthrough-config-empty-list',
  'passthrough-config-mixed-list', 'passthrough-chain',
  'passthrough-chain-untraced', 'passthrough-unknown-source', 'branch-passthrough-untraced',
  'branch-passthrough-config', 'duplicate-id-wired-from-another-canvas', 'extra-root-two',
  'passthrough-declared-input', 'passthrough-dynamic-input', 'passthrough-last-mapping', 'passthrough-blank-last',
  'passthrough-from-entry-unknown', 'passthrough-from-declared-output', 'passthrough-kept-back', 'passthrough-from-reference',
  'branch-passthrough-from-entry', 'branch-passthrough-parent-cycle',
  'error-policy-in-configuration', 'error-policy-on-entry', 'error-policy-in-unknown-block',
  'error-policy-on-entry-node', 'error-policy-on-exit-node', 'error-policy-route', 'error-output-wired', 'error-output-unoffered', 'error-output-passthrough', 'error-output-on-entry-node',
  'branch-absent', 'branch-absent-required',
  'error-policy-other-case', 'error-output-as-input', 'passthrough-design-time',
  'port-list-input', 'port-list-output', 'declared-unsupported', 'declared-source-unknown',
  'port-list-on-entry', 'declared-on-entry', 'port-list-on-exit', 'declared-on-exit',
  'type-argument-unknown', 'type-argument-malformed', 'type-argument-declared',
  'default-output-text', 'default-output-number', 'default-output-null', 'default-output-declared-int', 'default-output-declared-fraction', 'default-output-unknown', 'default-output-passthrough',
  'collect-shadow', 'collect-json', 'collect-default-array', 'collect-default-scalar',
] as const
const RESERVED = { entry: '__workflow_input__', exit: '__workflow_output__' }
const ID_SETS = [{ entry: 'start', exit: 'wo' }, { entry: 'in-7', exit: 'out-7' }] as const
const ID_VARIANTS: readonly (Variant | undefined)[] = [
  undefined, 'extra-root', 'extra-root-two', 'unreachable', 'no-start', 'input-has-incoming', 'wire-entry-unknown',
  'passthrough-from-entry-unknown', 'branch-passthrough-from-entry', 'branch-output-unfed', 'branch-output-fed',
  'branch-output-unknown', 'branch-absent', 'branch-absent-required', 'error-policy-on-entry', 'branch-ports',
  'error-policy-on-entry-node', 'error-output-on-entry-node', 'error-policy-on-exit-node', 'port-list-on-entry', 'declared-on-entry', 'port-list-on-exit', 'declared-on-exit',
]
let INPUT = RESERVED.entry
let OUTPUT = RESERVED.exit
function withIds<T>(ids: { entry: string; exit: string }, build: () => T): T {
  ;[INPUT, OUTPUT] = [ids.entry, ids.exit]
  try { return build() } finally { [INPUT, OUTPUT] = [RESERVED.entry, RESERVED.exit] }
}
const CONTAINERS = ['loop', 'loop2'] as const
const DYNAMIC_P = JSON.stringify([{ name: 'P', displayName: 'P', type: 'String', isRequired: true }])

const port = (name: string, isRequired = true, type?: string): PortRow => ({ name, isRequired, ...(type && { type }) })
const row = (o: Partial<BlockRow> & Pick<BlockRow, 'blockTypeId' | 'displayName'>): BlockRow => ({
  inputs: [], outputs: [], requiredConfigFields: [], subCanvases: [], subCanvasInputs: {}, subCanvasOutputs: {}, runsWorkflowFrom: null,
  hasDynamicPorts: false, supportsDeclaredOutputs: false, ...o,
})
const BLOCKS: BlockRow[] = [
  row({ blockTypeId: 'notify', displayName: 'Notify', inputs: [port('Message')], outputs: ['Text'], requiredConfigFields: ['Channel'], supportsDeclaredOutputs: true }),
  row({ blockTypeId: 'loop.for', displayName: 'For Loop', subCanvases: ['Body'] }),
  row({ blockTypeId: 'loop.req', displayName: 'Collect', subCanvases: ['Body'], subCanvasInputs: { Body: [port('Item', false)] },
    subCanvasOutputs: { Body: [port('Done'), port('Note', false)] }, supportsDeclaredOutputs: true }),
  row({ blockTypeId: 'run.ref', displayName: 'Run', inputs: [port('Ref')], runsWorkflowFrom: 'Ref' }),
  row({ blockTypeId: 'dyn', displayName: 'Dynamic', hasDynamicPorts: true }),
  row({ blockTypeId: 'gather', displayName: 'Gather', inputs: [port('Items', false, 'Collection<Int>')] }),
  row({ blockTypeId: 'predicate', displayName: 'Predicate', inputs: [port('Predicate', false, 'Expression<Boolean>')] }),
  row({ blockTypeId: 'plain', displayName: 'Plain' }),
  row({ blockTypeId: 'setter', displayName: 'Setter', enumFields: [{ name: 'Kind', values: ['Toggle', 'Dim', 'Scene'] }] }),
  row({ blockTypeId: 'setter.port', displayName: 'Setter with port', inputs: [port('Kind', false)], enumFields: [{ name: 'Kind', values: ['Toggle', 'Dim', 'Scene'] }] }),
  row({ blockTypeId: 'setter.required', displayName: 'Setter required', enumFields: [{ name: 'Kind', values: ['Toggle', 'Dim', 'Scene'], required: true }] }),
  row({ blockTypeId: 'decl', displayName: 'Declares', subCanvases: ['Body'], supportsDeclaredOutputs: true }),
  row({ blockTypeId: 'loop.collect', displayName: 'Collecting loop', outputs: ['Iterations'], subCanvases: ['Body'], supportsDeclaredOutputs: true, declaredOutputsCollect: true }),
  row({ blockTypeId: 'generic', displayName: 'Generic', typeParameters: [{ name: 'T', constraints: [] }] }),
]

const node = (id: string, blockTypeId: string, configuration: WorkflowNode['configuration'] = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration })
const wire = (id: string, from: string, to: string, mappings: Record<string, VariableMapping> = {}): WorkflowEdge =>
  ({ id, sourceNodeId: from, targetNodeId: to, mappings })
const pathText = (depth: Depth): string => ['root', 'loop/Body', 'loop/Body>loop2/Body'][DEPTHS.indexOf(depth)] as string
const below = (at: string, step: string) => (at === 'root' ? step : `${at}>${step}`)
const configured = () => ({ Message: 'configured', Channel: 'c' })

function withBranch(c: Built, id: string, blockTypeId: string, branch: string, inner: WorkflowDefinition, extra: Partial<WorkflowNode> = {}) {
  c.nodes.push({ ...node(id, blockTypeId), ...extra, subCanvases: { [branch]: inner } })
  c.edges.push(wire(`${c.depth}-${id}`, INPUT, id))
}
interface Built { depth: Depth; nodes: WorkflowNode[]; edges: WorkflowEdge[]; expect: string[]; compilerOnly: string[]; later: (() => void)[] }

function caseCanvas(depth: Depth, variant: Variant | undefined): Built {
  const at = pathText(depth)
  const root = depth === 'root'
  const config: WorkflowNode['configuration'] = configured()
  const mapping: Record<string, VariableMapping> = {}
  const reach: Record<string, VariableMapping> = {}
  let source = 'src'
  const c: Built = { depth, nodes: [], edges: [], expect: [], compilerOnly: [], later: [] }
  const expect = (...keys: string[]) => c.expect.push(...keys)
  const compilerOnly = (...keys: string[]) => c.compilerOnly.push(...keys)
  const n1 = node('n1', 'notify', config)
  const later = c.later
  switch (variant) {
    case undefined: case 'configured': case 'declared': break
    case 'unfed': delete config.Message; expect(`required-input-unfed ${at} n1 Message`); break
    case 'configured-empty': config.Message = ''; expect(`required-input-unfed ${at} n1 Message`); break
    case 'configured-number': config.Message = 5; expect(`literal-port-value-invalid ${at} n1 Message`); break
    case 'configured-bool': config.Message = true; expect(`literal-port-value-invalid ${at} n1 Message`); break
    case 'literal': delete config.Message; mapping.Message = { type: 'literal', literalValue: 'hello' }; break
    case 'literal-object': delete config.Message; mapping.Message = { type: 'literal', literalValue: { a: 1 } }; expect(`literal-port-value-invalid ${at} n1 Message`); break
    case 'wired': delete config.Message; mapping.Message = { type: 'direct', sourceOutputName: 'Text' }; break
    case 'wired-whitespace': case 'wired-unnamed':
      delete config.Message
      mapping.Message = variant === 'wired-unnamed' ? { type: 'direct' } : { type: 'direct', sourceOutputName: '  ' }
      expect(`required-input-unfed ${at} n1 Message`)
      break
    case 'wired-no-break-space': case 'wired-next-line': case 'wired-line-separator': case 'wired-zero-width-space': {
      const name = { 'wired-no-break-space': '\u00a0', 'wired-next-line': '\u0085', 'wired-line-separator': '\u2028', 'wired-zero-width-space': '\u200b' }[variant]
      delete config.Message
      mapping.Message = { type: 'direct', sourceOutputName: name }
      expect(variant === 'wired-zero-width-space' ? `wire-port-unknown ${at} src ${name}` : `required-input-unfed ${at} n1 Message`)
      break
    }
    case 'wired-from-another-canvas':
      delete config.Message
      mapping.Message = { type: 'direct', sourceOutputName: 'Text' }
      source = 'away'
      expect(`edge-endpoint-unknown ${at} ${depth}-feed source`, `required-input-unfed ${at} n1 Message`)
      break
    case 'literal-from-another-canvas': case 'literal-object-from-another-canvas':
      delete config.Message
      mapping.Message = { type: 'literal', literalValue: variant === 'literal-from-another-canvas' ? 'hello' : { a: 1 } }
      source = 'away'
      expect(`edge-endpoint-unknown ${at} ${depth}-feed source`)
      break
    case 'config-missing': delete config.Channel; expect(`required-config-missing ${at} n1 Channel`); break
    case 'config-empty': config.Channel = ''; expect(`required-config-missing ${at} n1 Channel`); break
    case 'shadow': n1.outputPorts = [{ name: 'Text', displayName: 'Text', type: 'String' }]; expect(`declared-output-shadows-static ${at} n1 Text`); break
    case 'cycle': case 'cycle-unfed': case 'cycle-wire-unknown': case 'cycle-configured-number':
      if (variant === 'cycle-unfed') delete config.Message
      if (variant === 'cycle-configured-number') config.Message = 5
      if (variant === 'cycle-wire-unknown') mapping.Bogus = { type: 'literal', literalValue: 'x' }
      later.push(() => c.edges.push(wire(`${depth}-back`, 'n1', 'src')))
      expect(`cycle ${at}`)
      break
    case 'extra-root':
      later.push(() => { c.edges = c.edges.filter((e) => e.targetNodeId !== 'n1') })
      if (root) expect('extra-root root n1')
      break
    case 'extra-root-two':
      later.push(() => { c.edges = c.edges.filter((e) => e.targetNodeId !== 'n1'); c.nodes.push(node('u', 'notify', configured())) })
      if (root) expect('extra-root root n1', 'extra-root root u')
      break
    case 'unreachable':
      later.push(() => {
        c.nodes.push(node('u1', 'notify', configured()), node('u2', 'notify', configured()))
        c.edges.push(wire(`${depth}-u12`, 'u1', 'u2'), wire(`${depth}-u21`, 'u2', 'u1'))
      })
      expect(`cycle ${at}`)
      if (root) expect('unreachable root u1', 'unreachable root u2')
      break
    case 'no-start':
      later.push(() => {
        c.nodes = c.nodes.filter((n) => n.id !== INPUT)
        c.edges = c.edges.filter((e) => e.sourceNodeId !== INPUT)
      })
      if (root) expect('no-start-node root')
      break
    case 'input-has-incoming':
      later.push(() => {
        c.nodes.push(node('w', 'notify', configured()))
        c.edges.push(wire(`${depth}-w`, 'w', INPUT))
      })
      if (root) expect('input-has-incoming root')
      break
    case 'duplicate-mapping':
      mapping.Message = { type: 'direct', sourceOutputName: 'Text' }
      reach.Message = { type: 'literal', literalValue: 'L' }
      expect(`duplicate-input-mapping ${at} n1 Message`)
      break
    case 'duplicate-edge-pair':
      later.push(() => {
        const first = c.edges.find((edge) => edge.sourceNodeId === INPUT && edge.targetNodeId === 'src')
        if (first) c.edges.push({ ...first, id: `${depth}-repeat` })
      })
      expect(`duplicate-edge-pair ${at}`)
      break
    case 'duplicate-id-wired-from-another-canvas':
      source = 'away'
      mapping.Message = { type: 'direct', sourceOutputName: 'Text' }
      later.push(() => c.nodes.push(node('src', 'notify', configured())))
      expect(`duplicate-node-id ${at} src`)
      break
    case 'duplicate-id': case 'duplicate-id-unfed':
      if (variant === 'duplicate-id-unfed') delete config.Message
      later.push(() => c.nodes.push(node('src', 'notify', configured())))
      expect(`duplicate-node-id ${at} src`)
      break
    case 'duplicate-id-unknown-type':
      later.push(() => c.nodes.push(node('src', 'plugin.gone')))
      expect(`duplicate-node-id ${at} src`)
      break
    case 'duplicate-id-two-types':
      later.push(() => c.nodes.push(node('src', 'run.ref')))
      expect(`duplicate-node-id ${at} src`)
      break
    case 'duplicate-id-shared-branch':
      for (let i = 0; i < 2; i++) {
        const x = { ...node('x', 'notify'), outputPorts: [{ name: 'Extra', displayName: 'Extra', type: 'Collection<' }] }
        later.push(() => withBranch(c, 'ext', 'loop.for', 'Body', {
          nodes: [node(INPUT, 'WorkflowInput'), x], edges: [wire(`${depth}-ext-x`, INPUT, 'x')],
        }))
      }
      expect(`duplicate-node-id ${at} ext`)
      break
    case 'uncatalogued-outer-container':
      later.push(() => withBranch(c, 'ext', 'plugin.gone', 'Body', {
        nodes: [node(INPUT, 'WorkflowInput'), {
          ...node('inner', 'loop.for'),
          subCanvases: {
            Body: { nodes: [node(INPUT, 'WorkflowInput'), node('x', 'notify', { Channel: 'c' })], edges: [wire(`${depth}-inner-x`, INPUT, 'x')] },
            Other: { nodes: [], edges: [] },
          },
        }],
        edges: [wire(`${depth}-ext-inner`, INPUT, 'inner')],
      }))
      break
    case 'undeclared-branch': case 'uncatalogued-container': {
      const branch = variant === 'undeclared-branch' ? 'Other' : 'Body'
      later.push(() => withBranch(c, 'ext', variant === 'undeclared-branch' ? 'loop.for' : 'plugin.gone', branch, {
        nodes: [node(INPUT, 'WorkflowInput'), node('x', 'notify')], edges: [wire(`${depth}-ext-x`, INPUT, 'x')],
      }))
      if (variant === 'undeclared-branch') expect(`sub-canvas-name-unknown ${at} ext`)
      expect(`required-config-missing ${below(at, `ext/${branch}`)} x Channel`)
      break
    }
    case 'reference-mapped': case 'reference-mapped-inactive': case 'reference-missing': case 'reference-empty': case 'reference-configured': {
      const refConfig: WorkflowNode['configuration'] = variant === 'reference-missing' ? {} : { Ref: variant === 'reference-empty' ? '' : 'wf' }
      const into: Record<string, VariableMapping> = variant === 'reference-mapped' ? { Ref: { type: 'literal', literalValue: 'other' } }
        : variant === 'reference-mapped-inactive' ? { Ref: { type: 'direct' } } : {}
      later.push(() => { c.nodes.push(node('r', 'run.ref', refConfig)); c.edges.push(wire(`${depth}-r`, INPUT, 'r', into)) })
      if (variant === 'reference-mapped') expect(`reference-input-mapped ${at} r`)
      if (variant === 'reference-missing' || variant === 'reference-empty') expect(`node-ports-unresolved ${at} r`)
      break
    }
    case 'timeout-text': config.timeoutSeconds = '5'; expect(`resilience-config-invalid ${at} n1 timeoutSeconds`); break
    case 'timeout-empty': config.timeoutSeconds = ''; expect(`resilience-config-invalid ${at} n1 timeoutSeconds`); break
    case 'timeout-too-long': config.timeoutSeconds = 86401; expect(`resilience-config-invalid ${at} n1 timeoutSeconds`); break
    case 'timeout-longest': config.timeoutSeconds = 86400; break
    case 'retry-fraction': config.retryCount = 1.5; expect(`resilience-config-invalid ${at} n1 retryCount`); break
    case 'retry-huge': config.retryCount = 3000000000; expect(`resilience-config-invalid ${at} n1 retryCount`); break
    case 'retry-enabled-text': config.retryEnabled = 'true'; expect(`resilience-config-invalid ${at} n1 retryEnabled`); break
    case 'backoff-text': config.retryBackoffSeconds = 'x'; expect(`resilience-config-invalid ${at} n1 retryBackoffSeconds`); break
    case 'backoff-fraction': Object.assign(config, { retryBackoffSeconds: 0.5, retryCount: 2, retryEnabled: false, timeoutSeconds: 30 }); break
    case 'branch-output-unfed': case 'branch-output-fed': case 'branch-output-config': case 'branch-output-unknown': case 'branch-output-cycle': {
      const inner = below(at, 'req/Body')
      const exit = node(OUTPUT, 'WorkflowOutput', variant === 'branch-output-config' ? { Done: 'done' } : {})
      const into: Record<string, VariableMapping> = variant === 'branch-output-fed' ? { Done: { type: 'direct', sourceOutputName: 'Text' } }
        : variant === 'branch-output-unknown' ? { Gone: { type: 'direct', sourceOutputName: 'Text' } } : {}
      later.push(() => withBranch(c, 'req', 'loop.req', 'Body', {
        nodes: [node(INPUT, 'WorkflowInput'), node('f', 'notify', configured()), exit],
        edges: [
          wire(`${depth}-req-f`, INPUT, 'f'), wire(`${depth}-req-out`, 'f', OUTPUT, into),
          ...(variant === 'branch-output-cycle' ? [wire(`${depth}-req-back`, OUTPUT, 'f')] : []),
        ],
      }))
      if (variant === 'branch-output-cycle') expect(`cycle ${inner}`)
      if (variant === 'branch-output-unfed' || variant === 'branch-output-unknown') expect(`required-input-unfed ${inner} ${OUTPUT} Done`)
      if (variant === 'branch-output-unknown') expect(`wire-port-unknown ${inner} ${OUTPUT} Gone`)
      break
    }
    case 'branch-absent': case 'branch-absent-required': {
      const id = variant === 'branch-absent' ? 'ext' : 'req'
      later.push(() => { c.nodes.push(node(id, variant === 'branch-absent' ? 'loop.for' : 'loop.req')); c.edges.push(wire(`${depth}-${id}`, INPUT, id)) })
      if (variant === 'branch-absent-required') expect(`required-input-unfed ${below(at, 'req/Body')} ${RESERVED.exit} Done`)
      break
    }
    case 'branch-ports':
      later.push(() => withBranch(c, 'req', 'loop.req', 'Body', {
        nodes: [node(INPUT, 'WorkflowInput'), node('f', 'notify', { Channel: 'c' }), node('g', 'notify', { Channel: 'c' }), node(OUTPUT, 'WorkflowOutput')],
        edges: [
          wire(`${depth}-req-f`, INPUT, 'f', { Message: { type: 'direct', sourceOutputName: 'Item' } }),
          wire(`${depth}-req-g`, INPUT, 'g', { Message: { type: 'direct', sourceOutputName: 'Carry' } }),
          wire(`${depth}-req-out`, 'f', OUTPUT, { Done: { type: 'direct', sourceOutputName: 'Text' }, Sum: { type: 'direct', sourceOutputName: 'Text' } }),
          wire(`${depth}-req-gout`, 'g', OUTPUT),
        ],
      }, {
        passthrough: ['Carry'],
        outputPorts: [{ name: 'Total', displayName: 'Total', type: 'String', sources: { Body: 'Sum' } }],
      }))
      later.push(() => { (c.edges.at(-1) as WorkflowEdge).mappings = { Carry: { type: 'literal', literalValue: 'c' } } })
      break
    case 'malformed-declared':
      n1.outputPorts = [{ name: 'Extra', displayName: 'Extra', type: 'Collection<' }]
      expect(`port-type-malformed ${at} n1 Extra`)
      break
    case 'malformed-projection-type':
      delete config.Message
      mapping.Message = { type: 'direct', sourceOutputName: 'Text', projection: { path: '$', type: 'Collection<' } }
      expect(`port-type-malformed ${at} n1 Message`)
      break
    case 'type-argument-unknown':
      n1.blockTypeId = 'generic'
      n1.typeArguments = { R: 'Int' }
      expect(`type-argument-unknown ${at} n1 R`)
      break
    case 'type-argument-malformed':
      n1.blockTypeId = 'generic'
      n1.typeArguments = { T: 'Int<' }
      expect(`port-type-malformed ${at} n1 T`)
      break
    case 'type-argument-declared':
      n1.blockTypeId = 'generic'
      n1.typeArguments = { T: 'Int' }
      break
    case 'wire-unknown-output': mapping.Message = { type: 'direct', sourceOutputName: 'Nope' }; expect(`wire-port-unknown ${at} src Nope`); break
    case 'wire-unknown-input': mapping.Bogus = { type: 'literal', literalValue: 'x' }; expect(`wire-port-unknown ${at} n1 Bogus`); break
    case 'wire-declared-output':
      n1.outputPorts = [{ name: 'Extra', displayName: 'Extra', type: 'String' }]
      later.push(() => {
        c.nodes.push(node('n2', 'notify', { Channel: 'c' }))
        c.edges.push(wire(`${depth}-n2`, 'n1', 'n2', { Message: { type: 'direct', sourceOutputName: 'Extra' } }))
      })
      break
    case 'wire-entry-unknown': reach.Message = { type: 'direct', sourceOutputName: 'seed' }; expect(`wire-port-unknown ${at} ${INPUT} seed`); break
    case 'wire-passthrough': case 'wire-passthrough-config-number': case 'wire-passthrough-config-text':
      n1.passthrough = ['Carry']
      mapping.Carry = { type: 'direct', sourceOutputName: 'Text' }
      if (variant === 'wire-passthrough-config-number') { config.Carry = 5; compilerOnly(`literal-port-value-invalid ${at} n1 Carry`) }
      if (variant === 'wire-passthrough-config-text') config.Carry = 'v'
      break
    case 'passthrough-untraced': case 'passthrough-config': case 'passthrough-config-object':
    case 'passthrough-config-empty-list': case 'passthrough-config-mixed-list': case 'passthrough-unknown-source': {
      n1.passthrough = ['Carry']
      if (variant === 'passthrough-config') config.Carry = 'v'
      if (variant === 'passthrough-config-object') config.Carry = { a: 1 }
      if (variant === 'passthrough-config-empty-list') config.Carry = []
      if (variant === 'passthrough-config-mixed-list') config.Carry = [1, 'a']
      if (variant === 'passthrough-unknown-source') mapping.Carry = { type: 'direct', sourceOutputName: 'Gone' }
      later.push(() => {
        c.nodes.push(node('n2', 'notify', { Channel: 'c' }))
        c.edges.push(wire(`${depth}-n2`, 'n1', 'n2', { Message: { type: 'direct', sourceOutputName: 'Carry' } }))
      })
      if (variant === 'passthrough-unknown-source') expect(`wire-port-unknown ${at} src Gone`)
      if (variant === 'passthrough-untraced' || variant === 'passthrough-unknown-source') expect(`wire-port-unknown ${at} n1 Carry`)
      break
    }
    case 'passthrough-declared-input': case 'passthrough-dynamic-input': case 'passthrough-last-mapping':
    case 'passthrough-blank-last': case 'passthrough-from-entry-unknown': case 'passthrough-from-declared-output':
    case 'passthrough-kept-back': case 'passthrough-from-reference': {
      let carrier = 'n1'
      let name = 'Carry'
      if (variant === 'passthrough-declared-input') { name = 'Message'; delete config.Message; expect(`required-input-unfed ${at} n1 Message`) }
      if (variant === 'passthrough-dynamic-input') {
        carrier = 'd'
        name = 'P'
        later.push(() => { c.nodes.push({ ...node('d', 'dyn', { InputPorts: DYNAMIC_P }), passthrough: ['P'] }); c.edges.push(wire(`${depth}-d`, INPUT, 'd')) })
        expect(`required-input-unfed ${at} d P`)
      }
      if (variant === 'passthrough-last-mapping') {
        reach.Carry = { type: 'literal', literalValue: 'x' }
        mapping.Carry = { type: 'direct', sourceOutputName: 'Gone' }
        expect(`duplicate-input-mapping ${at} n1 Carry`, `wire-port-unknown ${at} src Gone`, `wire-port-unknown ${at} n1 Carry`)
      }
      if (variant === 'passthrough-blank-last') { reach.Carry = { type: 'literal', literalValue: 'x' }; mapping.Carry = { type: 'direct', sourceOutputName: ' ' } }
      if (variant === 'passthrough-from-entry-unknown') {
        reach.Carry = { type: 'direct', sourceOutputName: 'seed' }
        expect(`wire-port-unknown ${at} ${INPUT} seed`, `wire-port-unknown ${at} n1 Carry`)
      }
      if (variant === 'passthrough-from-declared-output') {
        mapping.Carry = { type: 'direct', sourceOutputName: 'Extra' }
        later.push(() => { (c.nodes.find((n) => n.id === 'src') as WorkflowNode).outputPorts = [{ name: 'Extra', displayName: 'Extra', type: 'String' }] })
      }
      if (variant === 'passthrough-kept-back') { name = 'retryCount'; config.retryCount = 2; expect(`wire-port-unknown ${at} n1 retryCount`) }
      if (variant === 'passthrough-from-reference') {
        later.push(() => {
          c.nodes.push(node('r', 'run.ref', { Ref: 'wf' }))
          c.edges.push(wire(`${depth}-r`, INPUT, 'r'), wire(`${depth}-rn1`, 'r', 'n1', { Carry: { type: 'direct', sourceOutputName: 'Out' } }))
        })
      }
      if (carrier === 'n1') n1.passthrough = [name]
      later.push(() => {
        c.nodes.push(node('n2', 'notify', { Channel: 'c' }))
        c.edges.push(wire(`${depth}-n2`, carrier, 'n2', { Message: { type: 'direct', sourceOutputName: name } }))
      })
      break
    }
    case 'branch-passthrough-from-entry':
      later.push(() => withBranch(c, 'req', 'loop.req', 'Body', {
        nodes: [node(INPUT, 'WorkflowInput'), node('f', 'notify', configured()), node(OUTPUT, 'WorkflowOutput'), {
          ...node('lp', 'loop.for'),
          passthrough: ['Carry'],
          subCanvases: { Body: { nodes: [node(INPUT, 'WorkflowInput'), node('g', 'notify', { Channel: 'c' })],
            edges: [wire(`${depth}-lp-g`, INPUT, 'g', { Message: { type: 'direct', sourceOutputName: 'Carry' } })] } },
        }],
        edges: [
          wire(`${depth}-req-f`, INPUT, 'f'), wire(`${depth}-req-lp`, INPUT, 'lp', { Carry: { type: 'direct', sourceOutputName: 'Item' } }),
          wire(`${depth}-req-out`, 'f', OUTPUT, { Done: { type: 'direct', sourceOutputName: 'Text' } }),
        ],
      }))
      break
    case 'branch-passthrough-parent-cycle':
      later.push(() => withBranch(c, 'req', 'loop.req', 'Body', {
        nodes: [node(INPUT, 'WorkflowInput'), node('f', 'notify', configured()), node('g', 'notify', { Channel: 'c' }), node(OUTPUT, 'WorkflowOutput')],
        edges: [
          wire(`${depth}-req-f`, INPUT, 'f'),
          wire(`${depth}-req-g`, INPUT, 'g', { Message: { type: 'direct', sourceOutputName: 'Carry' } }),
          wire(`${depth}-req-out`, 'f', OUTPUT, { Done: { type: 'direct', sourceOutputName: 'Text' } }),
        ],
      }, { passthrough: ['Carry'] }))
      later.push(() => c.edges.push(wire(`${depth}-back`, 'n1', 'src')))
      expect(`cycle ${at}`)
      break
    case 'passthrough-chain': case 'passthrough-chain-untraced':
      n1.passthrough = ['Carry']
      mapping.Carry = { type: 'direct', sourceOutputName: 'Carry' }
      later.push(() => {
        const src = c.nodes.find((n) => n.id === 'src') as WorkflowNode
        src.passthrough = ['Carry']
        if (variant === 'passthrough-chain') src.configuration.Carry = 'v'
        c.nodes.push(node('n2', 'notify', { Channel: 'c' }))
        c.edges.push(wire(`${depth}-n2`, 'n1', 'n2', { Message: { type: 'direct', sourceOutputName: 'Carry' } }))
      })
      if (variant === 'passthrough-chain-untraced') expect(`wire-port-unknown ${at} src Carry`, `wire-port-unknown ${at} n1 Carry`)
      break
    case 'branch-passthrough-untraced': case 'branch-passthrough-config':
      later.push(() => withBranch(c, 'req', 'loop.req', 'Body', {
        nodes: [node(INPUT, 'WorkflowInput'), node('f', 'notify', configured()), node('g', 'notify', { Channel: 'c' }), node(OUTPUT, 'WorkflowOutput')],
        edges: [
          wire(`${depth}-req-f`, INPUT, 'f'),
          wire(`${depth}-req-g`, INPUT, 'g', { Message: { type: 'direct', sourceOutputName: 'Carry' } }),
          wire(`${depth}-req-out`, 'f', OUTPUT, { Done: { type: 'direct', sourceOutputName: 'Text' } }),
          wire(`${depth}-req-gout`, 'g', OUTPUT),
        ],
      }, { passthrough: ['Carry'], configuration: variant === 'branch-passthrough-config' ? { Carry: 'c' } : {} }))
      if (variant === 'branch-passthrough-untraced') expect(`wire-port-unknown ${below(at, 'req/Body')} ${INPUT} Carry`)
      break
    case 'wire-malformed-end':
      n1.outputPorts = [{ name: 'Extra', displayName: 'Extra', type: 'Collection<' }]
      later.push(() => {
        c.nodes.push(node('n2', 'notify', { Channel: 'c' }))
        c.edges.push(wire(`${depth}-n2`, 'n1', 'n2', { Message: { type: 'direct', sourceOutputName: 'Nope' } }))
      })
      expect(`port-type-malformed ${at} n1 Extra`)
      break
    case 'wire-unknown-block-end':
      later.push(() => {
        c.nodes.push(node('g', 'plugin.gone'), node('n2', 'notify', { Channel: 'c' }))
        c.edges.push(wire(`${depth}-g`, INPUT, 'g', { In: { type: 'literal', literalValue: 1 } }), wire(`${depth}-n2`, 'g', 'n2', { Message: { type: 'direct', sourceOutputName: 'Out' } }))
      })
      break
    case 'wire-reference-end':
      later.push(() => {
        c.nodes.push(node('r', 'run.ref', { Ref: 'wf' }), node('n2', 'notify', { Channel: 'c' }))
        c.edges.push(wire(`${depth}-r`, INPUT, 'r'), wire(`${depth}-n2`, 'r', 'n2', { Message: { type: 'direct', sourceOutputName: 'Out' } }))
      })
      break
    case 'error-policy-in-configuration': config.errorPolicy = { policy: 'Skip' }; expect(`error-policy-in-configuration ${at} n1 errorPolicy`); break
    case 'error-policy-on-entry':
      later.push(() => { (c.nodes.find((n) => n.id === INPUT) as WorkflowNode).configuration.errorPolicy = { policy: 'Skip' } })
      expect(`error-policy-in-configuration ${at} ${INPUT} errorPolicy`)
      break
    case 'error-policy-in-unknown-block':
      later.push(() => { c.nodes.push(node('g', 'plugin.gone', { errorPolicy: { policy: 'Skip' } })); c.edges.push(wire(`${depth}-g`, INPUT, 'g')) })
      break
    case 'error-policy-on-entry-node': case 'error-policy-on-exit-node': {
      const onExit = variant === 'error-policy-on-exit-node'
      const exit: WorkflowNode = { ...node(OUTPUT, 'WorkflowOutput', { Done: 'done' }), errorPolicy: { policy: 'Skip' } }
      later.push(() => {
        if (onExit) {
          withBranch(c, 'req', 'loop.req', 'Body', {
            nodes: [node(INPUT, 'WorkflowInput'), node('f', 'notify', configured()), exit],
            edges: [wire(`${depth}-req-f`, INPUT, 'f'), wire(`${depth}-req-out`, 'f', OUTPUT)],
          })
        } else {
          ;(c.nodes.find((n) => n.id === INPUT) as WorkflowNode).errorPolicy = { policy: 'FailWorkflow' }
        }
      })
      compilerOnly(onExit ? `error-policy-on-boundary-node ${below(at, 'req/Body')} ${OUTPUT} errorPolicy` : `error-policy-on-boundary-node ${at} ${INPUT} errorPolicy`)
      break
    }
    case 'error-policy-route': n1.errorPolicy = { policy: 'Skip', routeToErrorPort: true }; expect(`error-policy-route-unread ${at} n1 errorPolicy`); break
    case 'default-output-text': case 'default-output-number': case 'default-output-null': {
      const value = { 'default-output-text': '"fallback"', 'default-output-number': '5', 'default-output-null': 'null' }[variant]
      n1.errorPolicy = { policy: 'ContinueWithDefault', defaultOutputJson: `{"Text":${value}}` }
      if (variant !== 'default-output-text') compilerOnly(`literal-port-value-invalid ${at} n1 Text`)
      break
    }
    case 'default-output-declared-int': case 'default-output-declared-fraction':
      n1.outputPorts = [{ name: 'Count', displayName: 'Count', type: 'Int' }]
      n1.errorPolicy = { policy: 'ContinueWithDefault', defaultOutputJson: `{"Count":${variant === 'default-output-declared-int' ? '2' : '1.5'}}` }
      if (variant === 'default-output-declared-fraction') compilerOnly(`literal-port-value-invalid ${at} n1 Count`)
      break
    case 'default-output-passthrough':
      n1.passthrough = ['Message']
      n1.errorPolicy = { policy: 'ContinueWithDefault', defaultOutputJson: '{"Message":"x"}' }
      break
    case 'default-output-unknown':
      n1.errorPolicy = { policy: 'ContinueWithDefault', defaultOutputJson: '{"Nope":1}' }
      compilerOnly(`wire-port-unknown ${at} n1 Nope`)
      break
    case 'error-policy-other-case': config.ErrorPolicy = { policy: 'Skip' }; break
    case 'error-output-as-input':
      n1.errorPolicy = { policy: 'EmitError' }
      mapping._error = { type: 'literal', literalValue: 'x' }
      expect(`wire-port-unknown ${at} n1 _error`)
      break
    case 'passthrough-design-time': {
      const name = '__note'
      n1.passthrough = [name]
      config.__note = 'v'
      later.push(() => {
        c.nodes.push(node('n2', 'notify', { Channel: 'c' }))
        c.edges.push(wire(`${depth}-n2`, 'n1', 'n2', { Message: { type: 'direct', sourceOutputName: name } }))
      })
      expect(`wire-port-unknown ${at} n1 __note`)
      break
    }
    case 'error-output-wired': case 'error-output-unoffered':
      n1.errorPolicy = { policy: variant === 'error-output-wired' ? 'EmitError' : 'Skip' }
      later.push(() => {
        c.nodes.push(node('n2', 'notify', { Channel: 'c' }))
        c.edges.push(wire(`${depth}-n2`, 'n1', 'n2', { Message: { type: 'direct', sourceOutputName: '_error' } }))
      })
      if (variant === 'error-output-unoffered') expect(`wire-port-unknown ${at} n1 _error`)
      break
    case 'error-output-on-entry-node':
      later.push(() => { (c.nodes.find((n) => n.id === INPUT) as WorkflowNode).errorPolicy = { policy: 'EmitError' } })
      reach.Message = { type: 'direct', sourceOutputName: '_error' }
      compilerOnly(`error-policy-on-boundary-node ${at} ${INPUT} errorPolicy`)
      expect(`wire-port-unknown ${at} ${INPUT} _error`)
      break
    case 'error-output-passthrough':
      n1.errorPolicy = { policy: 'EmitError' }
      later.push(() => {
        c.nodes.push({ ...node('n2', 'notify', { Channel: 'c', Message: 'm' }), passthrough: ['Carry'] }, node('n3', 'notify', { Channel: 'c' }))
        c.edges.push(
          wire(`${depth}-n2`, 'n1', 'n2', { Carry: { type: 'direct', sourceOutputName: '_error' } }),
          wire(`${depth}-n3`, 'n2', 'n3', { Message: { type: 'direct', sourceOutputName: 'Carry' } }),
        )
      })
      break
    case 'dynamic-kept-keys': {
      const ports = JSON.stringify(['retryCount', '__note'].map((name) => ({ name, displayName: name, type: 'String', isRequired: true })))
      later.push(() => {
        c.nodes.push(node('d', 'dyn', { InputPorts: ports, retryCount: 2, __note: 'n' }))
        c.edges.push(wire(`${depth}-d`, INPUT, 'd'))
      })
      expect(`required-input-unfed ${at} d retryCount`, `required-input-unfed ${at} d __note`)
      break
    }
    case 'port-list-input': case 'port-list-output': {
      const key = variant === 'port-list-input' ? 'InputPorts' : 'OutputPorts'
      later.push(() => { c.nodes.push(node('p', 'plain', { [key]: DYNAMIC_P })); c.edges.push(wire(`${depth}-p`, INPUT, 'p')) })
      expect(`port-list-without-dynamic-ports ${at} p ${key}`)
      break
    }
    case 'declared-unsupported':
      later.push(() => {
        c.nodes.push({ ...node('p', 'plain'), outputPorts: [{ name: 'Extra', displayName: 'Extra', type: 'String' }] })
        c.edges.push(wire(`${depth}-p`, INPUT, 'p'))
      })
      expect(`declared-outputs-unsupported ${at} p `)
      break
    case 'port-list-on-entry': case 'declared-on-entry': {
      const entry = () => c.nodes.find((n) => n.id === INPUT) as WorkflowNode
      later.push(() => {
        if (variant === 'port-list-on-entry') entry().configuration.InputPorts = DYNAMIC_P
        else entry().outputPorts = [{ name: 'Extra', displayName: 'Extra', type: 'String' }]
      })
      expect(variant === 'port-list-on-entry' ? `port-list-without-dynamic-ports ${at} ${INPUT} InputPorts` : `declared-outputs-unsupported ${at} ${INPUT} `)
      break
    }
    case 'port-list-on-exit': case 'declared-on-exit': {
      const inner = below(at, 'req/Body')
      const exit = variant === 'port-list-on-exit'
        ? node(OUTPUT, 'WorkflowOutput', { Done: 'done', OutputPorts: DYNAMIC_P })
        : { ...node(OUTPUT, 'WorkflowOutput', { Done: 'done' }), outputPorts: [{ name: 'Extra', displayName: 'Extra', type: 'String' }] }
      later.push(() => withBranch(c, 'req', 'loop.req', 'Body', {
        nodes: [node(INPUT, 'WorkflowInput'), node('f', 'notify', configured()), exit],
        edges: [wire(`${depth}-req-f`, INPUT, 'f'), wire(`${depth}-req-out`, 'f', OUTPUT)],
      }))
      expect(variant === 'port-list-on-exit' ? `port-list-without-dynamic-ports ${inner} ${OUTPUT} OutputPorts` : `declared-outputs-unsupported ${inner} ${OUTPUT} `)
      break
    }
    case 'declared-source-unknown':
      later.push(() => withBranch(c, 'dec', 'decl', 'Body', { nodes: [node(INPUT, 'WorkflowInput')], edges: [] }, {
        outputPorts: [{ name: 'Total', displayName: 'Total', type: 'String', sources: { Nowhere: 'Sum' } }],
      }))
      expect(`declared-output-source-unknown ${at} dec Total`)
      break
    case 'collect-shadow': case 'collect-json': {
      const [name, type] = variant === 'collect-shadow' ? ['Iterations', 'Int'] : ['Rows', 'Json']
      later.push(() => withBranch(c, 'col', 'loop.collect', 'Body', { nodes: [node(INPUT, 'WorkflowInput')], edges: [] }, {
        outputPorts: [{ name, displayName: name, type, sources: { Body: 'Rows' } }],
      }))
      if (variant === 'collect-shadow') expect(`declared-output-shadows-static ${at} col Iterations`)
      break
    }
    case 'collect-default-array': case 'collect-default-scalar': {
      const value = variant === 'collect-default-array' ? '[1.5,null]' : '3'
      later.push(() => withBranch(c, 'col', 'loop.collect', 'Body', { nodes: [node(INPUT, 'WorkflowInput')], edges: [] }, {
        outputPorts: [{ name: 'Rows', displayName: 'Rows', type: 'Number', sources: { Body: 'Rows' } }],
        errorPolicy: { policy: 'ContinueWithDefault', defaultOutputJson: `{"Rows":${value}}` },
      }))
      if (variant === 'collect-default-scalar') compilerOnly(`literal-port-value-invalid ${at} col Rows`)
      break
    }
    case 'dynamic-unfed': case 'dynamic-fed': case 'dynamic-unknown': {
      const dynConfig: WorkflowNode['configuration'] = { InputPorts: DYNAMIC_P, ...(variant === 'dynamic-unknown' ? { P: 'p' } : {}) }
      const into: Record<string, VariableMapping> = variant === 'dynamic-fed' ? { P: { type: 'literal', literalValue: 'p' } }
        : variant === 'dynamic-unknown' ? { Q: { type: 'literal', literalValue: 'q' } } : {}
      later.push(() => { c.nodes.push(node('d', 'dyn', dynConfig)); c.edges.push(wire(`${depth}-d`, INPUT, 'd', into)) })
      if (variant === 'dynamic-unfed') expect(`required-input-unfed ${at} d P`)
      if (variant === 'dynamic-unknown') expect(`wire-port-unknown ${at} d Q`)
      break
    }
  }
  if (variant === 'declared') n1.outputPorts = [{ name: 'Extra', displayName: 'Extra', type: 'String' }]
  c.nodes.push(node(INPUT, 'WorkflowInput'), node('src', 'notify', configured()), n1)
  c.edges.push(
    wire(`${depth}-start`, INPUT, 'src'),
    wire(`${depth}-reach`, INPUT, 'n1', reach),
    wire(`${depth}-feed`, source, 'n1', mapping),
  )
  return c
}

function document(name: string, specs: readonly Spec[]): Case {
  const expected: string[] = []
  const compilerOnly: string[] = []
  let inner: WorkflowDefinition | undefined
  const awayAt: Depth = specs.some((s) => s.variant?.endsWith('from-another-canvas') === true && s.depth === 'root') ? 'nested' : 'root'
  for (let level = DEPTHS.length - 1; level >= 0; level--) {
    const depth = DEPTHS[level] as Depth
    const own = caseCanvas(depth, specs.find((s) => s.depth === depth)?.variant)
    if (depth === awayAt) own.nodes.push(node('away', 'notify', configured()))
    if (depth === awayAt) own.edges.push(wire(`${depth}-away`, INPUT, 'away'))
    if (inner) {
      const container = CONTAINERS[level] as string
      own.nodes.push({ ...node(container, 'loop.for'), subCanvases: { Body: inner } })
      own.edges.push(wire(`${depth}-enter`, INPUT, container))
    }
    for (const step of own.later) step()
    expected.push(...own.expect)
    compilerOnly.push(...own.compilerOnly)
    inner = { nodes: own.nodes, edges: own.edges }
  }
  return { name, definition: inner as WorkflowDefinition, expect: expected.sort(), ...(compilerOnly.length > 0 && { compilerOnly: compilerOnly.sort() }) }
}

function rootOutputCase(
  name: string, into: Record<string, VariableMapping>, exitConfig: WorkflowNode['configuration'], expected: string[], errorPolicy?: WorkflowNode['errorPolicy'],
  compilerOnly: string[] = [],
): Case {
  const base = document(name, [])
  base.definition.nodes.push({ ...node(OUTPUT, 'WorkflowOutput', exitConfig), ...(errorPolicy && { errorPolicy }) })
  base.definition.edges.push(wire('root-out', INPUT, OUTPUT, into))
  const signature: Signature = { inputs: [port('seed')], outputs: [port('result'), port('note', false)] }
  return { ...base, signature, expect: expected.sort(), ...(compilerOnly.length > 0 && { compilerOnly }) }
}

function literalTypingCase(name: string, target: 'output' | 'collection', literalValue: unknown, expected: string[]): Case {
  const base = document(name, [])
  const mapping = { type: 'literal', literalValue } as VariableMapping
  if (target === 'output') {
    base.definition.nodes.push(node(OUTPUT, 'WorkflowOutput'))
    base.definition.edges.push(wire('root-out', INPUT, OUTPUT, { count: mapping }))
    return { ...base, signature: { inputs: [], outputs: [port('count', false, 'Int')] }, expect: expected.sort() }
  }
  base.definition.nodes.push(node('g', 'gather'))
  base.definition.edges.push(wire('root-g', INPUT, 'g', { Items: mapping }))
  return { ...base, expect: expected.sort() }
}

function configuredCollectionCase(name: string, value: unknown, expected: string[]): Case {
  const base = document(name, [])
  base.definition.nodes.push(node('g', 'gather', { Items: value as never }))
  base.definition.edges.push(wire('root-g', INPUT, 'g'))
  return { ...base, expect: expected.sort() }
}

function expressionInputCase(name: string, value: unknown, road: 'config' | 'wire', refused: boolean): Case {
  const base = document(name, [])
  const literal = { Predicate: { type: 'literal', literalValue: value } as VariableMapping }
  base.definition.nodes.push(node('p', 'predicate', road === 'config' ? { Predicate: value as never } : {}))
  base.definition.edges.push(wire('root-p', INPUT, 'p', road === 'wire' ? literal : {}))
  return { ...base, expect: refused ? ['literal-port-value-invalid root p Predicate'] : [] }
}

function enumConfigCase(name: string, value: unknown, refused: boolean, block = 'setter', expected?: string[]): Case {
  const base = document(name, [])
  base.definition.nodes.push(node('e', block, { Kind: value as never }))
  base.definition.edges.push(wire('root-e', INPUT, 'e'))
  return { ...base, expect: expected ?? (refused ? ['literal-port-value-invalid root e Kind'] : []) }
}

function enumWireCase(name: string, literalValue: unknown, refused: boolean): Case {
  const base = document(name, [])
  base.definition.nodes.push(node('e', 'setter.port'))
  base.definition.edges.push(wire('root-e', INPUT, 'e', { Kind: { type: 'literal', literalValue } as VariableMapping }))
  return { ...base, expect: refused ? ['literal-port-value-invalid root e Kind'] : [] }
}

function outputMappingCase(): Case {
  const c = document('root-output-mapping-source-unknown', [])
  c.definition.nodes.push(node(OUTPUT, 'WorkflowOutput'))
  for (const [source, output] of [['src', 'kept'], ['away', 'far'], ['loop2', 'inner'], ['gone', 'result']] as const)
    c.definition.edges.push(wire(`root-to-${source}`, source, OUTPUT, { [output]: { type: 'direct', sourceOutputName: 'Text' } }))
  const undeclared = [`wire-port-unknown root ${OUTPUT} far`, `wire-port-unknown root ${OUTPUT} kept`]
  return { ...c, expect: ['edge-endpoint-unknown root root-to-gone source', 'edge-endpoint-unknown root root-to-loop2 source', ...undeclared] }
}

function exitWireSourceOutputUnknownCase(): Case {
  const c = document('root-output-wire-source-output-unknown', [])
  c.definition.nodes.push(node(OUTPUT, 'WorkflowOutput'))
  c.definition.edges.push(wire('root-out', INPUT, OUTPUT, {}))
  c.definition.edges.push(wire('root-m', 'src', OUTPUT, { kept: { type: 'direct', sourceOutputName: 'Nope' } }))
  return { ...c, signature: { inputs: [], outputs: [port('kept', false)] }, expect: [...c.expect, 'wire-port-unknown root src Nope'].sort() }
}

const MAPPING_DECLARATIONS = ['none', 'empty', 'other', 'declared', 'other-case', 'input-only'] as const
const MAPPING_SOURCES = ['node', 'entry', 'unknown-block', 'absent', 'body-only'] as const
function rootMappingCase(declaration: (typeof MAPPING_DECLARATIONS)[number], from: (typeof MAPPING_SOURCES)[number], cyclic: boolean): Case {
  const c = document(`root-output-mapping/${declaration}/${from}/${cyclic ? 'cycle' : 'sorts'}`, cyclic ? [{ depth: 'root', variant: 'cycle' }] : [])
  const sourceNodeId = { node: 'src', entry: INPUT, 'unknown-block': 'g', absent: 'gone', 'body-only': 'loop2' }[from]
  c.definition.nodes.push(node(OUTPUT, 'WorkflowOutput'))
  const kept = { kept: { type: 'direct', sourceOutputName: 'Text' } } as Record<string, VariableMapping>
  c.definition.edges.push(wire('root-out', INPUT, OUTPUT, from === 'entry' ? kept : {}))
  if (from !== 'entry') c.definition.edges.push(wire('root-m', sourceNodeId, OUTPUT, kept))
  if (from === 'unknown-block') {
    c.definition.nodes.push(node('g', 'plugin.gone'))
    c.definition.edges.push(wire('root-g', INPUT, 'g'))
  }
  const outputs = (...ports: PortRow[]): Signature => ({ inputs: [], outputs: ports })
  const signature = {
    none: undefined, empty: outputs(), other: outputs(port('other', false)), declared: outputs(port('kept', false)),
    'other-case': outputs(port('Kept', false)), 'input-only': { inputs: [port('kept', false)], outputs: [] },
  }[declaration]
  const expected = [...c.expect]
  if (from === 'absent' || from === 'body-only') expected.push('edge-endpoint-unknown root root-m source')
  else if (!cyclic && from !== 'unknown-block' && declaration !== 'declared') expected.push(`wire-port-unknown root ${OUTPUT} kept`)
  if (from === 'entry' && !cyclic) expected.push(`wire-port-unknown root ${INPUT} Text`)
  return { ...c, ...(signature && { signature }), expect: expected.sort() }
}

function twoEntries(entry: string): Case[] {
  return DEPTHS.flatMap((depth) => (['first', 'second'] as const).map((order) => {
    const c = withIds({ entry, exit: RESERVED.exit }, () => document(`two-entries-${entry}-${order}/${depth}`, []))
    const extra = node(RESERVED.entry, 'WorkflowInput')
    const canvas = canvasAt(c.definition, depth)
    canvas.nodes = order === 'first' ? [...canvas.nodes, extra] : [extra, ...canvas.nodes]
    const other = order === 'first' ? RESERVED.entry : entry
    return { ...c, expect: depth === 'root' ? [`extra-root root ${other}`] : [] }
  }))
}

function reservedIdOfAnotherType(entry: string): Case {
  const c = withIds({ entry, exit: RESERVED.exit }, () => document(`reserved-id-of-another-type-${entry}`, []))
  for (const depth of DEPTHS) {
    const canvas = canvasAt(c.definition, depth)
    canvas.nodes.push(node(RESERVED.entry, 'notify', configured()))
    canvas.edges.push(wire(`${depth}-reserved`, entry, RESERVED.entry))
  }
  return c
}

function canvasAt(definition: WorkflowDefinition, depth: Depth): WorkflowDefinition {
  let canvas = definition
  for (const container of CONTAINERS.slice(0, DEPTHS.indexOf(depth))) {
    const body = canvas.nodes.find((n) => n.id === container)?.subCanvases?.['Body']
    if (!body) throw new Error(`no ${container}/Body`)
    canvas = body
  }
  return canvas
}

function generate(): string {
  const cases: Case[] = []
  for (const depth of DEPTHS) for (const variant of VARIANTS) cases.push(document(`${depth}-${variant}`, [{ depth, variant }]))
  cases.push(document('three-canvases-at-once-a', [
    { depth: 'root', variant: 'unfed' }, { depth: 'body', variant: 'shadow' }, { depth: 'nested', variant: 'config-missing' },
  ]))
  cases.push(document('three-canvases-at-once-b', [
    { depth: 'root', variant: 'wired-from-another-canvas' }, { depth: 'body', variant: 'wired-whitespace' },
    { depth: 'nested', variant: 'unfed' },
  ]))
  cases.push(document('three-canvases-at-once-c', [
    { depth: 'root', variant: 'extra-root' }, { depth: 'body', variant: 'branch-output-unfed' }, { depth: 'nested', variant: 'cycle-unfed' },
  ]))
  cases.push(rootOutputCase('root-output-unfed', {}, {}, [`required-input-unfed root ${OUTPUT} result`]))
  cases.push(rootOutputCase('root-output-fed', { result: { type: 'literal', literalValue: 'r' } }, {}, []))
  cases.push(rootOutputCase('root-output-configured', {}, { result: 'r' }, []))
  cases.push(rootOutputCase('root-output-unknown', { other: { type: 'literal', literalValue: 'r' } }, {},
    [`required-input-unfed root ${OUTPUT} result`, `wire-port-unknown root ${OUTPUT} other`]))
  cases.push(rootOutputCase('root-output-error-policy', { result: { type: 'literal', literalValue: 'r' } }, {},
    [], { policy: 'FailWorkflow' }, [`error-policy-on-boundary-node root ${OUTPUT} errorPolicy`]))
  cases.push(rootOutputCase('root-input-known', { result: { type: 'direct', sourceOutputName: 'seed' } }, {}, []))
  cases.push(outputMappingCase(), exitWireSourceOutputUnknownCase())
  cases.push(
    literalTypingCase('literal-text-into-int-output', 'output', 'abc', [`literal-port-value-invalid root ${OUTPUT} count`]),
    literalTypingCase('literal-number-into-int-output', 'output', 5, []),
    literalTypingCase('literal-object-into-collection', 'collection', { a: 1 }, ['literal-port-value-invalid root g Items']),
    literalTypingCase('literal-array-into-collection', 'collection', [1, 2], []),
    literalTypingCase('literal-mixed-array-into-collection', 'collection', [{ a: 1 }, 2], ['literal-port-value-invalid root g Items']),
    configuredCollectionCase('configured-text-into-collection-of-int', 'x', ['literal-port-value-invalid root g Items']),
    configuredCollectionCase('configured-object-into-collection-of-int', { a: 1 }, ['literal-port-value-invalid root g Items']),
    configuredCollectionCase('configured-array-into-collection-of-int', [1, 2], []),
    configuredCollectionCase('configured-mixed-array-into-collection-of-int', [{ a: 1 }, 2], ['literal-port-value-invalid root g Items']),
  )
  cases.push(
    enumConfigCase('enum-first-name', 'Toggle', false),
    enumConfigCase('enum-last-name', 'Scene', false),
    enumConfigCase('enum-expression', 'expr:"Dim"', false),
    enumConfigCase('enum-ordinal', 1, true),
    enumConfigCase('enum-bool', true, true),
    enumConfigCase('enum-object', { a: 1 }, true),
    enumConfigCase('enum-array', ['Toggle'], true),
    enumConfigCase('enum-unlisted-name', 'Other', true),
    enumConfigCase('enum-other-case', 'toggle', true),
    enumConfigCase('enum-empty-text', '', false),
    enumConfigCase('enum-required-empty-text', '', false, 'setter.required', ['required-config-missing root e Kind']),
    enumWireCase('enum-wire-name', 'Dim', false),
    enumWireCase('enum-wire-unlisted-name', 'Bogus', true),
    enumWireCase('enum-wire-empty-text', '', false),
  )
  cases.push(
    expressionInputCase('expression-input-configured-text', 'hello', 'config', true),
    expressionInputCase('expression-input-configured-boolean', true, 'config', true),
    expressionInputCase('expression-input-configured-expression', 'expr:true', 'config', false),
    expressionInputCase('expression-input-wired-text', 'hello', 'wire', true),
    expressionInputCase('expression-input-wired-expression', 'expr:true', 'wire', false),
  )
  for (const declaration of MAPPING_DECLARATIONS) {
    for (const from of MAPPING_SOURCES) for (const cyclic of [false, true]) cases.push(rootMappingCase(declaration, from, cyclic))
  }
  for (const ids of ID_SETS) {
    const tag = `ids-${ids.entry}-${ids.exit}`
    withIds(ids, () => {
      for (const depth of DEPTHS) for (const variant of ID_VARIANTS) cases.push(document(`${tag}/${depth}-${variant ?? 'valid'}`, [{ depth, variant }]))
      cases.push(rootOutputCase(`${tag}/root-output-unfed`, {}, {}, [`required-input-unfed root ${OUTPUT} result`]))
      cases.push(rootOutputCase(`${tag}/root-output-fed`, { result: { type: 'literal', literalValue: 'r' } }, {}, []))
    })
    cases.push(...twoEntries(ids.entry), reservedIdOfAnotherType(ids.entry))
  }
  const descriptors = BLOCKS.map(descriptor)
  for (const c of cases) {
    const read = readStoredDefinitionText(JSON.stringify(c.definition))
    if (!read.ok) {
      const rule = read.fault.rule
      if (rule !== 'duplicate-node-id' && rule !== 'stored-key-unknown' && rule !== 'error-policy-route-unread') {
        throw new Error(`${c.name}: the reader refuses it under ${rule} (${read.fault.detail}); it is not an agreement case`)
      }
      const at = `${rule} ${pathOf(read.fault.canvasPath)}`
      const field = rule === 'error-policy-route-unread' ? 'errorPolicy' : ''
      c.expect = [rule === 'duplicate-node-id' ? `${at} ${read.fault.nodeId}` : `${at} ${read.fault.nodeId ?? '-'} ${field}`]
    }
    c.editor = editorFindingsPerOpenCanvas(c, descriptors).lines
  }
  const file = {
    $note: [
      'Generated by packages/core/src/editor/ruleAgreement.test.ts; a mismatch writes .actual beside this file.',
      'Each case is a definition of three canvases, the root, a loop body and a body inside that, whose nodes share',
      'ids; one canvas or more breaks a rule rule-parity.json classes editor, or comes close. `expect` lists every',
      'finding either side makes, keyed as ruleAgreement.test.ts and RuleAgreementTests.cs key it, whose rule the parity',
      'file classes editor; a finding of a compiler-only rule, or of a compiler-only part of an editor rule, is not in it.',
      'The editor grades each case through its store with each loop body open in turn, the compiler through Compile with',
      'the case signature or none; each must find exactly the list.',
      '`compilerOnly`, where a case has it, lists such findings the compiler makes besides `expect`, in the same keys; it',
      'shares no entry with `expect`, the compiler finds each, and the editor finds none and shows the server\'s.',
      '`editor` is each finding the editor makes, with its message and its wire, whichever canvas is open: a host reads both.',
      '`expect` entries are keyed by the rule id the compiler reports, which is the editor finding code without its `.variant`',
      'suffix (`wire-port-unknown.input` is graded as `wire-port-unknown`), then canvas path and target.',
    ],
    blocks: BLOCKS,
    cases,
  }
  return `${JSON.stringify(file, null, 2)}\n`
}

function findContracts(): string {
  for (let dir = __dirname; ; dir = dirname(dir)) {
    const candidate = join(dir, 'contracts')
    if (existsSync(join(candidate, 'workflow-validation-corpus.json'))) return candidate
    if (dirname(dir) === dir) throw new Error('contracts/ not found above ' + __dirname)
  }
}

const descriptor = (b: BlockRow): BlockTypeDescriptor => ({
  blockTypeId: b.blockTypeId,
  displayName: b.displayName,
  inputs: b.inputs.map((p) => ({ name: p.name, displayName: p.name, type: p.type ?? 'String', isRequired: p.isRequired })),
  outputs: b.outputs.map((name) => ({ name, displayName: name, type: 'String', isRequired: false })),
  ...(b.typeParameters === undefined ? {} : { typeParameters: b.typeParameters }),
  configurationSchema: [
    ...b.requiredConfigFields.map((name) => ({ name, label: name, type: 'string', required: true })),
    ...(b.enumFields ?? []).map((f) => ({ name: f.name, label: f.name, type: 'Enum', required: f.required ?? false, allowedValues: f.values, ...(f.defaultValue === undefined ? {} : { defaultValue: f.defaultValue }) })),
  ],
  subCanvasNames: b.subCanvases,
  subCanvasInputs: Object.fromEntries(b.subCanvases.map((s) => [s, (b.subCanvasInputs[s] ?? []).map((p) =>
    ({ name: p.name, displayName: p.name, type: 'String', isRequired: p.isRequired }))])),
  subCanvasOutputs: Object.fromEntries(Object.entries(b.subCanvasOutputs).map(([s, ports]) =>
    [s, ports.map((p) => ({ name: p.name, displayName: p.name, type: 'String', isRequired: p.isRequired }))])),
  runsWorkflowFrom: b.runsWorkflowFrom,
  hasDynamicPorts: b.hasDynamicPorts,
  supportsDeclaredOutputs: b.supportsDeclaredOutputs,
  ...(b.declaredOutputsCollect === true ? { declaredOutputsCollect: true } : {}),
})
const parameter = (p: PortRow): WorkflowParameter => ({ name: p.name, displayName: p.name, type: p.type ?? 'String', isRequired: p.isRequired })

const pathOf = (path: readonly CanvasPathSegment[] | undefined): string =>
  path && path.length > 0 ? path.map((s) => `${s.nodeId}/${s.branchName}`).join('>') : 'root'
const CANVAS_ONLY = new Set(['cycle', 'no-start-node', 'input-has-incoming', 'duplicate-edge-pair'])
const NODE_ONLY = new Set(['extra-root', 'unreachable', 'duplicate-node-id', 'reference-input-mapped', 'node-ports-unresolved', 'sub-canvas-name-unknown'])

function key(f: ValidationFinding): string {
  const at = `${f.ruleId ?? f.code} ${pathOf(f.canvasPath)}`
  if (f.ruleId === undefined) return `unclassified: ${f.code} ${pathOf(f.canvasPath)} ${f.nodeId} ${f.message}`
  if (CANVAS_ONLY.has(f.ruleId)) return at
  if (NODE_ONLY.has(f.ruleId)) return `${at} ${f.nodeId}`
  if (f.ruleId === 'edge-endpoint-unknown' && f.edgeId === undefined) return `${at} ${f.nodeId}`
  if (f.ruleId === 'edge-endpoint-unknown') return `${at} ${f.edgeId} ${/ names (source|target) node /.exec(f.message)?.[1]}`
  return `${at} ${f.nodeId ?? '-'} ${f.portName ?? ''}`
}

function editorFindingsPerOpenCanvas(testCase: Case, blocks: BlockTypeDescriptor[]) {
  const store = createWorkflowEditorStore({ portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES) })
  store.getState().setBlockTypes(blocks)
  store.getState().loadStoredWorkflow(readStoredDefinitionText(JSON.stringify(testCase.definition)))
  if (testCase.signature) {
    store.getState().setWorkflowParams(testCase.signature.inputs.map(parameter), testCase.signature.outputs.map(parameter))
  }
  const loadedAsIs = store.getState().loadedDocumentFault === null
    && JSON.stringify(store.getState().getWorkflowDefinition()) === JSON.stringify(testCase.definition)
  const all = () => [...store.getState().validateStructureFindings(), ...store.getState().validateWorkflowFindings()]
  const grade = () => [...new Set(all().map(key))].sort()
  const lines = (findings: ValidationFinding[]) => findings.map(line).sort()
  const workflowAtRoot = store.getState().validateWorkflowFindings()
  const pure = validateStructureFindings({ ...testCase.definition, blockTypes: blocks })
  const graded = [grade()]
  const every: ValidationFinding[] = [...all()]
  const loadedFault = store.getState().loadedDocumentFault
  const described = [lines(loadedFault ? workflowAtRoot : loadedAsIs ? all() : [...pure, ...workflowAtRoot])]
  for (const container of CONTAINERS) {
    store.getState().enterSubCanvas(container, 'Body')
    graded.push(grade())
    every.push(...all())
    described.push(lines(loadedFault ? workflowAtRoot : all()))
  }
  return { every: loadedAsIs ? every : [...every, ...pure, ...workflowAtRoot], graded, described, lines: described[0] as string[], loadedAsIs, loadedFault, workflowAtRoot: workflowAtRoot.map(key), structure: pure.map(key) }
}

function savesAfterNavigating(testCase: Case, blocks: BlockTypeDescriptor[]) {
  const store = createWorkflowEditorStore({ portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES) })
  store.getState().setBlockTypes(blocks)
  store.getState().loadStoredWorkflow(readStoredDefinitionText(JSON.stringify(testCase.definition)))
  if (testCase.signature) {
    store.getState().setWorkflowParams(testCase.signature.inputs.map(parameter), testCase.signature.outputs.map(parameter))
  }
  const enterAll = () => { for (const container of CONTAINERS) store.getState().enterSubCanvas(container, 'Body') }
  enterAll()
  store.getState().exitToLevel(0)
  const navigated = JSON.stringify(store.getState().getWorkflowDefinition())
  enterAll()
  const moved = store.getState().nodes.some((n) => n.id === 'n1')
  if (moved) store.getState().updateNodePosition('n1', { x: 9, y: 9 })
  store.getState().exitToLevel(0)
  const edited = JSON.stringify(store.getState().getWorkflowDefinition())

  const expected = structuredClone(testCase.definition)
  for (const depth of ['body', 'nested'] as const) {
    const canvas = canvasAt(expected, depth)
    if (!canvas.nodes.some((n) => n.blockTypeId === 'WorkflowInput')) {
      canvas.nodes.unshift({ id: RESERVED.entry, blockTypeId: 'WorkflowInput', position: { x: 50, y: 100 }, configuration: {} })
    }
  }
  const navigatedExpected = JSON.stringify(expected)
  const n1 = canvasAt(expected, 'nested').nodes.filter((n) => n.id === 'n1')
  for (const n of n1) n.position = { x: 9, y: 9 }
  return { navigated, navigatedExpected, edited, editedExpected: JSON.stringify(expected), moved }
}

const line = (f: ValidationFinding): string => `${key(f)} | ${f.message}${f.edgeId === undefined ? '' : ` | wire ${f.edgeId}`}`

const generatedText = generate()
const file = JSON.parse(generatedText) as { blocks: BlockRow[]; cases: Case[] }
const blocks = file.blocks.map(descriptor)
const parity = (JSON.parse(readFileSync(join(findContracts(), 'rule-parity.json'), 'utf8')) as { rules: ParityRow[] }).rules
const graded = parity.filter((r) => r.class === 'editor' && r.depth !== 'registration'
  && r.agreement?.includes('contracts/rule-agreement.json'))

describe('rule agreement, the editor', () => {
  const path = join(findContracts(), 'rule-agreement.json')

  it('the committed file is what this generator writes', () => {
    const generated = generatedText
    const committed = existsSync(path) ? readFileSync(path, 'utf8') : ''
    if (committed !== generated) writeFileSync(`${path}.actual`, generated)
    expect(committed, `regenerated file written to ${path}.actual`).toBe(generated)
  })

  it('covers every depth and variant', () => {
    const mappingCases = MAPPING_DECLARATIONS.length * MAPPING_SOURCES.length * 2
    expect(file.cases).toHaveLength(DEPTHS.length * VARIANTS.length + 3 + 7 + 9 + 14 + 6 + mappingCases + ID_SETS.length * (DEPTHS.length * ID_VARIANTS.length + 2 + 6 + 1))
  })

  it.each(graded.map((r) => [r.id, r] as const))('expects %s at the depth rule-parity.json gives it', (id, rule) => {
    const paths = new Set(file.cases.flatMap((c) => c.expect).filter((k) => k.startsWith(`${id} `)).map((k) => k.split(' ')[1]))
    const at = rule.depth === 'every' ? ['root', 'loop/Body', 'loop/Body>loop2/Body'] : ['root']
    for (const p of at) expect(paths, `${id} expected at ${p}`).toContain(p)
    if (rule.depth === 'root') expect([...paths], `${id} expected only at the root`).toEqual(['root'])
  })

  it('expects nothing of a root-only rule when its variant is drawn below the root', () => {
    for (const variant of ['extra-root', 'no-start', 'input-has-incoming'] as const) {
      for (const depth of ['body', 'nested'] as const) {
        expect(file.cases.find((c) => c.name === `${depth}-${variant}`)?.expect, `${depth}-${variant}`).toEqual([])
      }
    }
  })

  it.each(file.cases.map((c) => [c.name, c] as const))('%s', (_, testCase) => {
    const { graded: [root, body, nested], described, every, loadedAsIs, loadedFault, workflowAtRoot, structure } = editorFindingsPerOpenCanvas(testCase, blocks)
    for (const finding of every) {
      expect(validationMessage(finding), `${finding.code} renders as the message it carries`).toBe(finding.message)
      const variantOfRule = finding.ruleId === undefined || finding.code === finding.ruleId || finding.code.startsWith(`${finding.ruleId}.`)
      expect(variantOfRule, `${finding.code} is its rule id ${String(finding.ruleId)} or a variant of it`).toBe(true)
    }
    if (loadedAsIs) {
      expect(root).toEqual(testCase.expect)
      expect(body, 'with the loop body open').toEqual(testCase.expect)
      expect(nested, 'with the inner body open').toEqual(testCase.expect)
      for (const [i, open] of described.entries()) expect(open, `messages and wires, open canvas ${i}`).toEqual(testCase.editor)
      const saves = savesAfterNavigating(testCase, blocks)
      expect(saves.navigated, 'saved after opening and leaving both bodies').toBe(saves.navigatedExpected)
      expect(saves.edited, 'saved after moving the nested n1').toBe(saves.editedExpected)
    } else {
      if (loadedFault) {
        expect(root).toEqual(testCase.expect)
        expect(body).toEqual(testCase.expect)
        expect(nested).toEqual(testCase.expect)
        for (const [i, open] of described.entries()) expect(open, `messages and wires, open canvas ${i}`).toEqual(testCase.editor)
      } else {
        expect(testCase.name).toMatch(/(^|\/)root-no-start$/)
        expect([...new Set([...structure, ...workflowAtRoot])].sort()).toEqual(testCase.expect)
      }
    }
  })
})
