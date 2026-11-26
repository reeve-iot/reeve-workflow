import type { BlockTypeDescriptor } from '../model/blockType.js'
import { declaredBranchNames } from '../model/blockTypeInternal.js'
import type {
  CanvasPathSegment,
  DeclaredOutputPort,
  ValidationFinding,
  WorkflowDefinition,
  WorkflowEdge,
  WorkflowNode,
  WorkflowParameter,
} from '../model/workflow.js'
import { WORKFLOW_OUTPUT_BLOCK_TYPE_ID } from '../model/workflow.js'
import type { PortTypeRegistry } from '../portTypes/types.js'
import type { PortTypeScope } from '../ports/portTypeResolver.js'
import { createPortTypeResolver } from '../ports/portTypeResolver.js'
import { withDeclaredBranches } from './declaredBranches.js'
import { feedsInput } from './mappingFeeds.js'
import type { NodeValidationRule } from './validationRules.js'
import { runNodeRules } from './validationRules.js'
import { documentNodeLabels } from './nodeLabels.js'

function describeBranchSets(sets: string[][]): string {
  return sets
    .filter((set) => set.length > 0)
    .map((set) => {
      if (set.length === 1) return `in ${set[0]}`
      if (set.length === 2) return `in both ${set[0]} and ${set[1]}`
      return `in all of ${set.slice(0, -1).join(', ')} and ${set[set.length - 1]}`
    })
    .join(', or ')
}

/** Warnings about declared outputs a branch may leave unset. It compares no types: the compiler grades what a branch feeds an
 * output. Walks every sub-canvas, so one call covers the whole graph; each carries its canvasPath. */
export function validateBranchOutputs(input: {
  definition: WorkflowDefinition
  blockTypes: BlockTypeDescriptor[]
  workflowInputParams: WorkflowParameter[]
  portTypes: PortTypeRegistry
  rules?: readonly NodeValidationRule[]
}): ValidationFinding[] {
  const { definition, blockTypes, workflowInputParams, portTypes, rules = [] } = input
  const filled = withDeclaredBranches(definition, blockTypes)
  const nodeLabel = documentNodeLabels(filled, blockTypes)
  const warnings: ValidationFinding[] = []
  const descriptorOf = (blockTypeId: string) => blockTypes.find((bt) => bt.blockTypeId === blockTypeId)

  const walk = (
    nodes: WorkflowNode[],
    edges: WorkflowEdge[],
    path: readonly CanvasPathSegment[],
    outerScopes: PortTypeScope[],
    currentScopeParent: PortTypeScope['parent'],
  ): void => {
    const warn = (...found: ValidationFinding[]) => { warnings.push(...found.map((f) => ({ ...f, canvasPath: path }))) }
    const currentScope: PortTypeScope = { nodes, edges, parent: currentScopeParent }
    const scopes = [currentScope, ...outerScopes]
    const canvasResolver = createPortTypeResolver({ scopes, blockTypes, workflowInputParams })
    const resolveOutputType = (nodeId: string, portName: string) => canvasResolver.resolveOutputType(0, nodeId, portName)

    for (const node of nodes) {
      const blockType = descriptorOf(node.blockTypeId)
      const label = nodeLabel(node)
      warn(...runNodeRules(rules, { node, edges, blockType, label, resolveOutputType, portTypes }))
    }

    for (const node of nodes) {
      const blockType = descriptorOf(node.blockTypeId)
      const label = nodeLabel(node)
      const branchNames = blockType ? declaredBranchNames(blockType) : Object.keys(node.subCanvases ?? {})
      const satisfyingSets = blockType?.outputSatisfyingBranchSets

      for (const port of node.outputPorts ?? []) {
        const sourced = (branch: string): string | undefined => port.sources?.[branch]

        if (satisfyingSets && satisfyingSets.length > 0) {
          if (!satisfyingSets.some((set) => set.every((branch) => !!sourced(branch)))) {
            warn({
              code: 'branch-output-maybe-unset',
              params: { nodeLabel: label, portName: port.name, satisfyingBranches: describeBranchSets(satisfyingSets) },
              nodeId: node.id,
              message: `Output '${port.name}' of '${label}' may be null on some execution path (set it ${describeBranchSets(satisfyingSets)})`,
            })
          }
          for (const branch of branchNames.filter((b) => sourced(b))) {
            checkBranchFed(node, port, branch, label, warn)
          }
          continue
        }

        for (const branch of branchNames) {
          if (!sourced(branch)) {
            warn({ code: 'branch-output-unset', params: { branchName: branch, nodeLabel: label, portName: port.name }, nodeId: node.id, message: `Branch '${branch}' of '${label}' does not set output '${port.name}' (returns null)` })
            continue
          }
          checkBranchFed(node, port, branch, label, warn)
        }
      }

      for (const [branchName, branchDef] of Object.entries(node.subCanvases ?? {})) {
        walk(branchDef.nodes, branchDef.edges, [...path, { nodeId: node.id, branchName }], scopes, { nodeId: node.id, subCanvasName: branchName })
      }
    }
  }

  function checkBranchFed(
    node: WorkflowNode,
    port: DeclaredOutputPort,
    branch: string,
    label: string,
    warn: (finding: ValidationFinding) => void,
  ): void {
    const sourceName = port.sources?.[branch]
    const branchDef = node.subCanvases?.[branch]
    if (!sourceName || !branchDef) return

    const branchOutputNode = branchDef.nodes.find((n) => n.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID)
    if (!branchOutputNode) return

    const branchNodeIds = new Set(branchDef.nodes.map((n) => n.id))
    const feedsOutput = branchDef.edges.some((e) => e.targetNodeId === branchOutputNode.id && feedsInput(e, sourceName, branchNodeIds))
    if (!feedsOutput) {
      warn({ code: 'branch-output-unset', params: { branchName: branch, nodeLabel: label, portName: port.name }, nodeId: node.id, message: `Branch '${branch}' of '${label}' does not set output '${port.name}' (returns null)` })
      return
    }
  }

  walk(filled.nodes, filled.edges, [], [], undefined)
  return warnings
}
