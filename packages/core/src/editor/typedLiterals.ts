import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { VariableMapping, WorkflowEdge, WorkflowNode } from '../model/workflow.js'
import { readMapping } from '../model/workflow.js'
import { literalWriteTerm, namesTypeParameter } from '../analysis/draftAnalysis.js'
import { literalFormOfType } from '../portTypes/literalCoercion.js'
import type { PortLiteralForm } from '../portTypes/literalForm.js'
import type { PortTypeName } from '../portTypes/name.js'
import type { PortTypeRegistry } from '../portTypes/types.js'

const EXPRESSION_PREFIX = 'expr:'

type ScalarLiteral = string | number | boolean

function canonicalNumber(text: string, integral: boolean): number | undefined {
  const parsed = Number(text)
  if (!Number.isFinite(parsed) || String(parsed) !== text) return undefined
  return !integral || Number.isSafeInteger(parsed) ? parsed : undefined
}

function candidateOf(value: unknown, form: PortLiteralForm): ScalarLiteral | undefined {
  switch (form.gradedAs) {
    case 'Boolean':
      return value === 'true' ? true : value === 'false' ? false : undefined
    case 'Int':
    case 'Number':
      return typeof value === 'string' ? canonicalNumber(value, form.gradedAs === 'Int') : undefined
    case 'String':
      return (form.format ?? undefined) === undefined && (typeof value === 'number' || typeof value === 'boolean') ? String(value) : undefined
    default:
      return undefined
  }
}

/** `value` re-read as a literal of the closed `term`, or undefined when it already is one, is an expression, or is not the
 * canonical text of a value of the term's form: exactly `true` or `false` for a Boolean; for a Number the text a number
 * prints as (`1.5`, not `1.50`, `+1.5` or `0x1F`), for an Int that of a whole number within ±(2^53 − 1); for a String with
 * no format, a number or a boolean as it prints. Each rewrite reads back to the value it came from. */
export function retypeLiteral(value: unknown, term: PortTypeName, registry: PortTypeRegistry): { value: ScalarLiteral } | undefined {
  if (value === undefined || value === null || registry.isLiteralOf(value, term)) return undefined
  if (typeof value === 'string' && value.startsWith(EXPRESSION_PREFIX)) return undefined
  const form = literalFormOfType(term, registry)
  if (form === null || form === undefined) return undefined
  const candidate = candidateOf(value, form)
  return candidate !== undefined && registry.isLiteralOf(candidate, term) ? { value: candidate } : undefined
}

/** The literals of `node`'s generic inputs, on its incoming `edges` and in its configuration, re-read in the form each input's
 * term takes under `typeArguments`; a literal that does not parse is left as stored. */
export function retypeNodeLiterals(
  node: WorkflowNode,
  edges: readonly WorkflowEdge[],
  blockType: BlockTypeDescriptor | undefined,
  typeArguments: Readonly<Record<string, string>>,
  registry: PortTypeRegistry,
): { configuration: WorkflowNode['configuration']; edges: WorkflowEdge[] } {
  let configuration = node.configuration
  const retypedEdges = new Map<string, WorkflowEdge>()
  const enumFields = new Set((blockType?.configurationSchema ?? []).filter((f) => f.type === 'Enum').map((f) => f.name))
  for (const input of blockType?.inputs ?? []) {
    if (!namesTypeParameter(input.type)) continue
    const term = literalWriteTerm(input.type, undefined, typeArguments)
    if (term === undefined) continue
    if (!enumFields.has(input.name) && Object.hasOwn(configuration, input.name)) {
      const retyped = retypeLiteral(configuration[input.name], term as PortTypeName, registry)
      if (retyped !== undefined) configuration = { ...configuration, [input.name]: retyped.value }
    }
    for (const edge of edges) {
      if (edge.targetNodeId !== node.id) continue
      const current = retypedEdges.get(edge.id) ?? edge
      const mapping = Object.hasOwn(current.mappings, input.name) ? current.mappings[input.name] : undefined
      if (mapping === undefined) continue
      const read = readMapping(mapping)
      if (!read.literal) continue
      const retyped = retypeLiteral(read.value, term as PortTypeName, registry)
      if (retyped === undefined) continue
      const written: VariableMapping = { ...mapping, literalValue: retyped.value }
      retypedEdges.set(edge.id, { ...current, mappings: { ...current.mappings, [input.name]: written } })
    }
  }
  return { configuration, edges: edges.map((e) => retypedEdges.get(e.id) ?? e) }
}
