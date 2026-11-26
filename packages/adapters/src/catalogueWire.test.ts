import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { WorkflowBlockTypeResponse } from '@reeve-iot/workflow-client'
import {
  BUILTIN_PORT_TYPES,
  WORKFLOW_INPUT_BLOCK_TYPE_ID,
  WORKFLOW_INPUT_NODE_ID,
  WORKFLOW_OUTPUT_BLOCK_TYPE_ID,
  WORKFLOW_OUTPUT_NODE_ID,
  buildPortTypeScopes,
  createPortTypeRegistry,
  createPortTypeResolver,
  createWorkflowEditorStore,
} from '@reeve-iot/workflow-core'
import type {
  BlockTypeDescriptor,
  NodeValidationRule,
  PortTypeResolver,
  WorkflowDefinition,
  WorkflowEdge,
  WorkflowParameter,
} from '@reeve-iot/workflow-core'
import { readBlockType } from './blockCatalogue.js'

const wireText = readFileSync(resolve(process.cwd(), 'packages/adapters/src/standard-catalogue.wire.json'), 'utf8')
const portTypes = createPortTypeRegistry(BUILTIN_PORT_TYPES)

const raw = JSON.parse(wireText) as BlockTypeDescriptor[]
const adapted = (JSON.parse(wireText) as WorkflowBlockTypeResponse[]).map((r) => readBlockType(r, portTypes))

const narrow = (type: string): string => type.replace(/\bAny\b|\$[A-Za-z]\w*/g, 'Number')

function canvasFor(block: BlockTypeDescriptor): {
  definition: WorkflowDefinition
  params: WorkflowParameter[]
  results: WorkflowParameter[]
} {
  const params = block.inputs.map((p) => ({ name: p.name, displayName: p.name, type: narrow(p.type), isRequired: false }))
  const results = block.outputs.map((p) => ({ name: p.name, displayName: p.name, type: narrow(p.type), isRequired: false }))
  const subCanvases = Object.fromEntries((block.subCanvasNames ?? []).map((name) => {
    const inputs = block.subCanvasInputs?.[name] ?? []
    const from = inputs.find((p) => p.type.includes('$')) ?? inputs[0]
    const outputs = block.subCanvasOutputs?.[name] ?? []
    const edges: WorkflowEdge[] = from === undefined || outputs.length === 0 ? [] : [{
      id: 'feed', sourceNodeId: 'entry', targetNodeId: 'exit',
      mappings: Object.fromEntries(outputs.map((o) => [o.name, { type: 'direct' as const, sourceOutputName: from.name }])),
    }]
    return [name, {
      nodes: [
        { id: 'entry', blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} },
        { id: 'exit', blockTypeId: WORKFLOW_OUTPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} },
      ],
      edges,
    }]
  }))
  return {
    params,
    results,
    definition: {
      nodes: [
        { id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} },
        { id: 'n', blockTypeId: block.blockTypeId, position: { x: 0, y: 0 }, configuration: {}, subCanvases },
        { id: WORKFLOW_OUTPUT_NODE_ID, blockTypeId: WORKFLOW_OUTPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} },
      ],
      edges: [{
        id: 'in', sourceNodeId: WORKFLOW_INPUT_NODE_ID, targetNodeId: 'n',
        mappings: Object.fromEntries(block.inputs.map((p) => [p.name, { type: 'direct' as const, sourceOutputName: p.name }])),
      }, {
        id: 'out', sourceNodeId: 'n', targetNodeId: WORKFLOW_OUTPUT_NODE_ID,
        mappings: Object.fromEntries(block.outputs.map((p) => [p.name, { type: 'direct' as const, sourceOutputName: p.name }])),
      }],
    },
  }
}

const outcome = (resolve: () => string | undefined): string => {
  try {
    return resolve() ?? '(unknown)'
  } catch (error) {
    return `throws: ${error instanceof Error ? error.message : String(error)}`
  }
}

function resolvedPorts(block: BlockTypeDescriptor, blockTypes: BlockTypeDescriptor[]): Record<string, string> {
  const { definition, params } = canvasFor(block)
  const top: PortTypeResolver = createPortTypeResolver({
    scopes: buildPortTypeScopes({ nodes: definition.nodes, edges: definition.edges, canvasStack: [] }),
    blockTypes,
    workflowInputParams: params,
  })
  const out: Record<string, string> = {}
  for (const p of block.inputs) out[`in ${p.name}`] = outcome(() => top.resolveInputType(0, 'n', p.name))
  for (const p of block.outputs) out[`out ${p.name}`] = outcome(() => top.resolveOutputType(0, 'n', p.name))
  const node = definition.nodes[1]
  for (const name of block.subCanvasNames ?? []) {
    const child = node?.subCanvases?.[name]
    if (!child) continue
    const inBranch = createPortTypeResolver({
      scopes: buildPortTypeScopes({
        nodes: child.nodes,
        edges: child.edges,
        canvasStack: [{ parentNodeId: 'n', subCanvasName: name, nodes: definition.nodes, edges: definition.edges }],
      }),
      blockTypes,
      workflowInputParams: params,
    })
    for (const p of block.subCanvasInputs?.[name] ?? [])
      out[`${name} in ${p.name}`] = outcome(() => inBranch.resolveOutputType(0, 'entry', p.name))
    for (const p of block.subCanvasOutputs?.[name] ?? [])
      out[`${name} out ${p.name}`] = outcome(() => inBranch.resolveInputType(0, 'exit', p.name))
  }
  return out
}

const reportsTypes: NodeValidationRule = ({ node, edges, blockType, resolveOutputType }) => [
  ...edges.filter((e) => e.targetNodeId === node.id).flatMap((e) => Object.entries(e.mappings).map(([input, m]) =>
    ({ nodeId: node.id, code: 'wire-type-report', message: `${input} <- ${resolveOutputType(e.sourceNodeId, m.sourceOutputName ?? '')}` }))),
  ...(blockType?.outputs ?? []).map((o) => ({ nodeId: node.id, code: 'wire-type-report', message: `${o.name}: ${resolveOutputType(node.id, o.name)}` })),
]

function findings(block: BlockTypeDescriptor, blockTypes: BlockTypeDescriptor[]) {
  const { definition, params, results } = canvasFor(block)
  const store = createWorkflowEditorStore({ portTypes, nodeErrorRules: [reportsTypes], nodeWarningRules: [reportsTypes] })
  store.getState().setBlockTypes(blockTypes)
  store.getState().setWorkflowParams(params, results)
  store.getState().loadWorkflow(definition)
  return { errors: store.getState().validateWorkflowFindings(), warnings: store.getState().validateWorkflowWarningFindings() }
}

describe('the standard catalogue as the server writes it, handed to core with no adapter', () => {
  it('is the standard set, and carries the nulls the wire sends', () => {
    expect(raw.map((b) => b.blockTypeId)).toContain('Filter')
    expect(wireText).toContain('"allowedValues":null')
    expect(wireText).toContain('"runsWorkflowFrom":null')
  })

  it.each(raw.map((b) => [b.blockTypeId, b] as const))('%s: every port resolves as through the adapter', (id, block) => {
    const throughAdapter = resolvedPorts(adapted.find((b) => b.blockTypeId === id) ?? block, adapted)
    expect(Object.values(throughAdapter).filter((t) => t.startsWith('throws'))).toEqual([])
    expect(resolvedPorts(block, raw)).toEqual(throughAdapter)
  })

  it.each(raw.map((b) => [b.blockTypeId, b] as const))('%s: no validation finding differs', (id, block) => {
    const throughAdapter = findings(adapted.find((b) => b.blockTypeId === id) ?? block, adapted)
    expect(findings(block, raw)).toEqual(throughAdapter)
  })
})
