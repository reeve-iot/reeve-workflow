import { describe, expect, it } from 'vitest'
import type { BlockPort, BlockTypeDescriptor } from '../model/blockType.js'
import type { SavedWorkflowDefinition, ValidationFinding, WorkflowDefinition, WorkflowNode } from '../model/workflow.js'
import { WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_INPUT_NODE_ID } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import { emptyBranch } from './declaredBranches.js'
import { createWorkflowEditorStore } from './store.js'

const OPENED_ENTRY_ONLY =
  '{"nodes":[{"id":"__workflow_input__","blockTypeId":"WorkflowInput","position":{"x":50,"y":100},"configuration":{}}],"edges":[]}'
const OPENED_WITH_EXIT =
  '{"nodes":[{"id":"__workflow_input__","blockTypeId":"WorkflowInput","position":{"x":50,"y":100},"configuration":{}},'
  + '{"id":"__workflow_output__","blockTypeId":"WorkflowOutput","position":{"x":800,"y":100},"configuration":{}}],"edges":[]}'

const portTypes = createPortTypeRegistry([...BUILTIN_PORT_TYPES])
const port = (name: string, isRequired: boolean): BlockPort => ({ name, displayName: name, type: 'String', isRequired })
const container = (blockTypeId: string, branches: string[], outputs: Record<string, BlockPort[]> = {}): BlockTypeDescriptor => ({
  blockTypeId, displayName: blockTypeId, description: '', category: 'Test', inputs: [], outputs: [], hasDynamicPorts: false,
  configurationSchema: [], subCanvasNames: branches, subCanvasInputs: {}, subCanvasOutputs: outputs, supportsDeclaredOutputs: true,
})
const BLOCK_TYPES: BlockTypeDescriptor[] = [
  container('IfElse', ['Then', 'Else']),
  container('TryCatch', ['Try', 'Catch', 'Finally']),
  container('WhileLoop', ['Body'], { Body: [port('Continue', false)] }),
  container('ForLoop', ['Body']),
  container('IterateOver', ['Body'], { Body: [port('Result', false)] }),
  container('Collect', ['Body'], { Body: [port('Done', true), port('Note', false)] }),
]

const CASES = BLOCK_TYPES.flatMap((b) => (b.subCanvasNames ?? []).flatMap((branch) => [false, true].map((routed) => [b.blockTypeId, branch, routed, b] as const)))

function document(blockType: BlockTypeDescriptor, branch: string, routed: boolean): WorkflowDefinition {
  const names = blockType.subCanvasNames ?? []
  const node: WorkflowNode = { id: 'c', blockTypeId: blockType.blockTypeId, position: { x: 0, y: 0 }, configuration: {} }
  if (routed) node.outputPorts = [{ name: 'v', displayName: 'v', type: 'String', sources: Object.fromEntries(names.map((n) => [n, 'v'])) }]
  const others = names.filter((n) => n !== branch)
  if (others.length > 0) node.subCanvases = Object.fromEntries(others.map((n) => [n, emptyBranch(node, blockType, n)]))
  const entry: WorkflowNode = { id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 50, y: 100 }, configuration: {} }
  return { nodes: [entry, node], edges: [{ id: 'e', sourceNodeId: WORKFLOW_INPUT_NODE_ID, targetNodeId: 'c', mappings: {} }] }
}

const storeWith = (definition: WorkflowDefinition | SavedWorkflowDefinition) => {
  const store = createWorkflowEditorStore({ portTypes })
  store.getState().setBlockTypes(BLOCK_TYPES)
  store.getState().loadWorkflow(definition)
  return store
}

function opened(definition: WorkflowDefinition, branch: string): SavedWorkflowDefinition {
  const store = storeWith(definition)
  store.getState().enterSubCanvas('c', branch)
  store.getState().exitSubCanvas()
  return store.getState().getWorkflowDefinition()
}

const text = (f: ValidationFinding) => JSON.stringify([f.ruleId, f.nodeId, f.portName, f.canvasPath ?? [], f.edgeId, f.message])
function graded(definition: WorkflowDefinition | SavedWorkflowDefinition): string[][] {
  const state = storeWith(definition).getState()
  return [state.validateWorkflowFindings(), state.validateWorkflowWarningFindings(), state.validateStructureFindings()]
    .map((list) => list.map(text).sort())
}

describe('a declared branch the document lacks', () => {
  it.each(CASES)('is written on opening as emptyBranch, whose text the .NET run reads (%s %s, routed %s)', (_, branch, routed, blockType) => {
    const absent = document(blockType, branch, routed)
    const saved = opened(absent, branch).nodes?.find((n) => n.id === 'c')?.subCanvases?.[branch]
    const exit = routed || (blockType.subCanvasOutputs?.[branch] ?? []).length > 0

    expect(saved).toEqual(emptyBranch(absent.nodes[1], blockType, branch))
    expect(JSON.stringify(saved)).toBe(exit ? OPENED_WITH_EXIT : OPENED_ENTRY_ONLY)
  })

  it.each(CASES)('is graded as the branch opened and left empty (%s %s, routed %s)', (_, branch, routed, blockType) => {
    const absent = document(blockType, branch, routed)

    expect(graded(absent)).toEqual(graded(opened(absent, branch)))
  })

  it('whose declared output is required gets that output reported unfed on the exit node', () => {
    const findings = storeWith(document(BLOCK_TYPES[5] as BlockTypeDescriptor, 'Body', false)).getState().validateWorkflowFindings()

    expect(findings.map((f) => [f.ruleId, f.nodeId, f.portName, f.canvasPath])).toEqual([
      ['required-input-unfed', '__workflow_output__', 'Done', [{ nodeId: 'c', branchName: 'Body' }]],
    ])
  })

  it('writes no exit node for a declared output whose key for the branch is blank, as DeclaredBranches.WritesExit', () => {
    const node: WorkflowNode = {
      id: 'c', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {},
      outputPorts: [{ name: 'v', displayName: 'v', type: 'String', sources: { Else: '' } }],
    }

    expect(JSON.stringify(emptyBranch(node, BLOCK_TYPES[0] as BlockTypeDescriptor, 'Else'))).toBe(OPENED_ENTRY_ONLY)
  })

  it('leaves the stored document as it is until the author opens it', () => {
    const absent = document(BLOCK_TYPES[0] as BlockTypeDescriptor, 'Else', false)
    const store = storeWith(absent)
    store.getState().validateWorkflowFindings()
    store.getState().validateWorkflowWarningFindings()

    expect(JSON.stringify(store.getState().getWorkflowDefinition())).toBe(JSON.stringify(absent))
  })
})
