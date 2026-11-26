import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { ValidationFinding, WorkflowEdge, WorkflowNode } from '../model/workflow.js'
import type { PortTypeName } from '../portTypes/name.js'
import type { PortTypeRegistry } from '../portTypes/types.js'

/** What a host-contributed rule is given about one node on the canvas being validated. */
export interface NodeValidationContext {
  node: WorkflowNode
  /** Edges of the canvas the node sits on, not the whole graph. */
  edges: WorkflowEdge[]
  /** The node's descriptor, or undefined when the catalogue does not know this block type. */
  blockType: BlockTypeDescriptor | undefined
  /** Display name for messages: the descriptor's, falling back to the raw block type id. */
  label: string
  /** The closed declared term of an output port on any node of this canvas, as the editor reads it without the server.
   * `undefined` means the editor does not know the type (a term naming a type parameter, a name only a passthrough entry gives): treat it
   * as unknown, never as an error. */
  resolveOutputType: (nodeId: string, portName: string) => PortTypeName | undefined
  portTypes: PortTypeRegistry
}

/** A host-contributed check. Return no findings when the node is fine — never throw; a rule that throws
 * would take the whole validation pass down with it. */
export type NodeValidationRule = (context: NodeValidationContext) => ValidationFinding[]

/** Runs every rule and flattens the result. A rule that throws is reported as a finding of its own rather
 * than being allowed to abort validation for every other node. */
export function runNodeRules(rules: readonly NodeValidationRule[], context: NodeValidationContext): ValidationFinding[] {
  const findings: ValidationFinding[] = []
  for (const rule of rules) {
    try {
      findings.push(...rule(context))
    } catch (error) {
      findings.push({
        code: 'validation-rule-failed',
        params: { nodeLabel: context.label, reason: error instanceof Error ? error.message : String(error) },
        nodeId: context.node.id,
        message: `A validation rule failed on '${context.label}': ${error instanceof Error ? error.message : String(error)}`,
      })
    }
  }
  return findings
}
