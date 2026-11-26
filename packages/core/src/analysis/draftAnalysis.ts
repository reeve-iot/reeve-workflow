import type { CanvasPathSegment, SavedWorkflowDefinition, WorkflowParameter } from '../model/workflow.js'
import { isClosedPortTerm } from '../portTypes/closedTerm.js'
import { parsePortTerm } from '../portTypes/grammar.js'
import { foldAsciiCase } from '../portTypes/ascii.js'
import type { PortTypeName } from '../portTypes/name.js'

/** What the server says of one port: `bound` has an effective term, `unbound` names a type parameter nothing binds
 * (`unbound` lists them), `refused` has none because a reported finding stands in the way. */
export type DraftPortState = 'bound' | 'unbound' | 'refused'

/** One port of a node in a draft analysis. `declared` is the term the block declares, which may name a type
 * parameter as `$T`, and is null for a name only a `passthrough` entry gives; `effective` is the closed term after binding
 * (for such a name, its feed's term), or null. */
export interface DraftPortAnalysis {
  name: string
  declared: string | null
  effective: string | null
  state: DraftPortState
  unbound: readonly string[]
  /** The result term of an `expr:` value feeding this input. */
  expression?: string
  /** The term of a projection wire into this input. */
  projection?: string
}

/** One node of a draft analysis, on the canvas `canvasPath` (root first). `typeArguments` maps each type parameter of the
 * node's block to its bound term, or null while nothing binds it. */
export interface DraftNodeAnalysis {
  canvasPath: readonly CanvasPathSegment[]
  nodeId: string
  typeArguments: Readonly<Record<string, string | null>>
  inputs: readonly DraftPortAnalysis[]
  outputs: readonly DraftPortAnalysis[]
}

/** A finding of the compile that produced a draft analysis. */
export interface DraftAnalysisDiagnostic {
  ruleId: string
  message: string
  nodeId: string | null
  portName: string | null
  canvasPath: readonly CanvasPathSegment[]
}

/** The server's answer for one draft: what the compile found and every node's ports as the compiler typed them. */
export interface DraftAnalysis {
  /** The editor revision the draft was posted at, echoed. */
  revision: number
  /** The generation of the port-type vocabulary the compile ran under. */
  registryGeneration: number
  diagnostics: readonly DraftAnalysisDiagnostic[]
  nodes: readonly DraftNodeAnalysis[]
}

/** The document an analysis is asked for: what a save would write. */
export interface DraftAnalysisDocument {
  definition: SavedWorkflowDefinition
  inputParameters: readonly WorkflowParameter[]
  outputParameters: readonly WorkflowParameter[]
}

/** How the editor asks the server about a draft; the host supplies it, `core` never fetches. Resolve with the answer for
 * `revision`; reject, or stop when `signal` aborts, for a draft that was superseded. */
export interface DraftAnalysisSource {
  analyze(document: DraftAnalysisDocument, revision: number, signal: AbortSignal): Promise<DraftAnalysis>
}

/** Whether the last answer describes the draft on screen: `current` for the draft's own revision, `stale` for an earlier
 * one, `none` before any answer. */
export type DraftAnalysisFreshness = 'none' | 'current' | 'stale'

export function draftAnalysisFreshness(analysis: DraftAnalysis | null, revision: number): DraftAnalysisFreshness {
  if (analysis === null) return 'none'
  return analysis.revision === revision ? 'current' : 'stale'
}

const pathKey = (path: readonly CanvasPathSegment[]): string => path.map((s) => `${s.nodeId}\u0001${s.branchName}`).join('\u0002')

/** The analysis of one node and its findings, found by canvas and node id. */
export interface DraftAnalysisIndex {
  node(canvasPath: readonly CanvasPathSegment[], nodeId: string): DraftNodeAnalysis | undefined
  port(canvasPath: readonly CanvasPathSegment[], nodeId: string, side: 'inputs' | 'outputs', name: string): DraftPortAnalysis | undefined
  /** The messages of the findings that name this node, and the port when one is given. */
  findings(canvasPath: readonly CanvasPathSegment[], nodeId: string, portName?: string): readonly string[]
}

const EMPTY_INDEX: DraftAnalysisIndex = { node: () => undefined, port: () => undefined, findings: () => [] }

const indexed = new WeakMap<DraftAnalysis, DraftAnalysisIndex>()

/** The index of an answer, built once per answer however many components ask. */
export function indexDraftAnalysis(analysis: DraftAnalysis | null): DraftAnalysisIndex {
  if (analysis === null) return EMPTY_INDEX
  const known = indexed.get(analysis)
  if (known !== undefined) return known
  const built = buildIndex(analysis)
  indexed.set(analysis, built)
  return built
}

function buildIndex(analysis: DraftAnalysis): DraftAnalysisIndex {
  const nodes = new Map<string, DraftNodeAnalysis>()
  for (const node of analysis.nodes) nodes.set(`${pathKey(node.canvasPath)}\u0000${node.nodeId}`, node)
  const findings = new Map<string, DraftAnalysisDiagnostic[]>()
  for (const diagnostic of analysis.diagnostics) {
    if (diagnostic.nodeId === null) continue
    const key = `${pathKey(diagnostic.canvasPath)}\u0000${diagnostic.nodeId}`
    findings.set(key, [...(findings.get(key) ?? []), diagnostic])
  }
  const node = (canvasPath: readonly CanvasPathSegment[], nodeId: string) => nodes.get(`${pathKey(canvasPath)}\u0000${nodeId}`)
  return {
    node,
    port: (canvasPath, nodeId, side, name) => node(canvasPath, nodeId)?.[side].find((p) => p.name === name),
    findings: (canvasPath, nodeId, portName) =>
      (findings.get(`${pathKey(canvasPath)}\u0000${nodeId}`) ?? [])
        .filter((d) => portName === undefined || d.portName === portName)
        .map((d) => d.message),
  }
}

/** Whether a declaration term names a type parameter (`$T`). */
export const namesTypeParameter = (declared: string): boolean => declared.includes('$')

/** How a port's term is shown, and why: `declared` is the block's own term, `bound` the server's effective one, `unbound` a term
 * with a parameter nothing binds, `refused` a port a finding stands in front of. */
export interface PortTermDisplay {
  text: string
  knowledge: 'declared' | 'bound' | 'unbound' | 'refused'
  /** True when `text` comes from an answer older than the draft on screen. */
  stale: boolean
  /** The messages of the findings on this port, for a port the server refused. */
  findings: readonly string[]
}

const withoutMarker = (declared: string): string => declared.replaceAll('$', '')

/** The term a port shows. With no answer, or none for this port, it is the declared term, a parameter shown as `T (unbound)`.
 * With an answer it is the effective term, `T (unbound)` where nothing binds the parameter, and a refused port is marked with its
 * findings. An answer older than the draft keeps showing, marked stale. A name only a passthrough entry gives has no declared term:
 * it shows what the answer says, and nothing before one. */
export function displayPortTerm(
  declared: string | null | undefined, port: DraftPortAnalysis | undefined, freshness: DraftAnalysisFreshness, findings: readonly string[] = [],
): PortTermDisplay | undefined {
  if (port === undefined || freshness === 'none') {
    if (declared === null || declared === undefined) return undefined
    return namesTypeParameter(declared)
      ? { text: `${withoutMarker(declared)} (unbound)`, knowledge: 'unbound', stale: false, findings: [] }
      : { text: declared, knowledge: 'declared', stale: false, findings: [] }
  }
  const stale = freshness === 'stale'
  const marked = (state: 'refused' | 'unbound'): string => (port.declared === null ? state : `${withoutMarker(port.declared)} (${state})`)
  if (port.state === 'bound' && port.effective !== null) return { text: port.effective, knowledge: 'bound', stale, findings: [] }
  if (port.state === 'refused') return { text: marked('refused'), knowledge: 'refused', stale, findings }
  return { text: marked('unbound'), knowledge: 'unbound', stale, findings: [] }
}

/** The term a literal on this port is written in: the declared term where it is closed, else the effective one of the last answer,
 * else the declared term with the parameters that answer's node bindings fix. The last answer serves while an edit is waiting
 * for the next one, so a field never changes its control between two keystrokes. Undefined where none is closed. */
export function literalWriteTerm(
  declared: string, port: DraftPortAnalysis | undefined, typeArguments: Readonly<Record<string, string | null>> = {},
): string | undefined {
  if (isClosedPortTerm(declared as PortTypeName)) return declared
  if (port?.state === 'bound' && port.effective !== null) return port.effective
  let open = false
  const substituted = declared.replace(/\$([A-Za-z_][A-Za-z0-9_]*)/g, (_, name: string) => {
    const bound = typeArguments[name]
    if (bound === null || bound === undefined) open = true
    return bound ?? ''
  })
  return open || !isClosedPortTerm(substituted as PortTypeName) ? undefined : substituted
}

/** What a literal is written as where no term is closed: parsed JSON for a list term or a map term, nullable or not for a
 * map, text for any other. The one rule the config panel and the mapping dialog share. */
export function openTermLiteralType(term: string): 'Json' | 'String' {
  const parsed = parsePortTerm(term, 'declaration')
  if (!parsed.ok || parsed.term.kind !== 'constructor') return 'String'
  return parsed.term.name === 'Map' || (parsed.term.name === 'Collection' && !parsed.term.nullable) ? 'Json' : 'String'
}

/** Whether `term` is a map of any value, nullable or not, whose literal is a JSON object. */
export function isMapTerm(term: string): boolean {
  const parsed = parsePortTerm(term, 'declaration')
  return parsed.ok && parsed.term.kind === 'constructor' && parsed.term.name === 'Map'
}

/** Whether `term` is exactly `Map<String,String>` (key and value in any ASCII case, neither nullable, the map not nullable):
 * the one map whose literal an editor can draw as key/value rows of text. */
export function isStringMapTerm(term: string): boolean {
  const parsed = parsePortTerm(term, 'declaration')
  if (!parsed.ok || parsed.term.kind !== 'constructor' || parsed.term.name !== 'Map' || parsed.term.nullable) return false
  const value = parsed.term.argument
  return value.kind === 'atom' && !value.nullable && foldAsciiCase(value.name) === 'string'
    && parsed.term.key !== undefined && foldAsciiCase(parsed.term.key.name) === 'string'
}
