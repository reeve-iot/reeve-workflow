import { foldAsciiCase } from '../portTypes/ascii.js'
import type { PortTypeName } from '../portTypes/name.js'

/** The graph model. Port types are open names on purpose — the vocabulary is open and supplied by the
 * host through a PortTypeRegistry, so a host can add its own domain types without a change here. */

/** Ids of the two synthetic nodes the editor maintains: the single entry point every canvas has, and the
 * exit node it grows once the canvas declares outputs. Both are reserved — a node may not take them. */
export const WORKFLOW_INPUT_NODE_ID = '__workflow_input__'
export const WORKFLOW_OUTPUT_NODE_ID = '__workflow_output__'

export const WORKFLOW_INPUT_BLOCK_TYPE_ID = 'WorkflowInput'
export const WORKFLOW_OUTPUT_BLOCK_TYPE_ID = 'WorkflowOutput'

/** The `Workflow Input` node's start-anchor output port: a drag HANDLE, not a mappable output, so the mandatory
 * start edge can be drawn when the workflow declares no input parameters. Edges from it carry no mappings. */
export const WORKFLOW_START_ANCHOR_PORT_NAME = '__start__'

export interface WorkflowDefinition {
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
}

export interface WorkflowNode {
  id: string
  blockTypeId: string
  position: { x: number; y: number }
  /** No value is null: clearing a value is removing its key, and the reader refuses a null. */
  configuration: Record<string, string | number | boolean | Record<string, unknown> | unknown[]>
  subCanvases?: Record<string, WorkflowDefinition>
  passthrough?: string[]
  outputPorts?: DeclaredOutputPort[]
  typeArguments?: Record<string, string>
  /** What the run does when the block throws: the one policy it reads. A configuration key of this name is none. */
  errorPolicy?: NodeErrorPolicy | null
}

/** The four policies a run applies when a node's block throws, as the engine names them. */
export type ErrorPolicyName = 'FailWorkflow' | 'ContinueWithDefault' | 'EmitError' | 'Skip'

/** A node's error policy as stored. `routeToErrorPort` is read by no run and refused when true; the editor never writes it. */
export interface NodeErrorPolicy {
  policy?: ErrorPolicyName
  /** The outputs, as JSON text, a node under ContinueWithDefault offers when its block throws. */
  defaultOutputJson?: string | null
  routeToErrorPort?: boolean
}

/** The output a node offers under the EmitError policy when its block throws. */
export const ERROR_OUTPUT_NAME = '_error'

/** The term of `_error`, an object the compiler types Json. */
export const ERROR_OUTPUT_TYPE = 'Json'

/** Whether a node offers `_error`: its policy is EmitError, and it is no boundary node, which never runs a block. */
export function offersErrorOutput(node: WorkflowNode): boolean {
  return node.errorPolicy?.policy === 'EmitError'
    && node.blockTypeId !== WORKFLOW_INPUT_BLOCK_TYPE_ID && node.blockTypeId !== WORKFLOW_OUTPUT_BLOCK_TYPE_ID
}

/** Whether `_error` is added beside a node's other outputs: the node offers it and none of them already carries the name. */
export function addsErrorOutput(node: WorkflowNode, otherOutputs: readonly { name: string }[]): boolean {
  return offersErrorOutput(node) && !otherOutputs.some((o) => o.name === ERROR_OUTPUT_NAME)
}

export interface WorkflowEdge {
  id: string
  sourceNodeId: string
  targetNodeId: string
  /** targetInputName -> mapping */
  mappings: Record<string, VariableMapping>
}

export type MappingType = 'direct' | 'literal'

/** What a direct mapping selects from its source value: a path into a Json value, read as text when `mode` is
 * `jsonText`, and optionally the port type term the selection is delivered as. The server grades all three. */
export interface MappingProjection {
  path: string
  mode?: 'jsonText'
  type?: string
}

/** One mapping of an edge's object form. The editor writes `type` as a MappingType and the source under
 * `sourceOutputName`; a stored one may hold any type or none, and `readMapping` reads it as a run does. */
export interface VariableMapping {
  type?: MappingType | (string & Record<never, never>) | null
  sourceOutputName?: string | null
  literalValue?: string | number | boolean | null | unknown[] | Record<string, unknown>
  /** What a direct mapping selects from its source; a host's projection control writes it through `setMappingProjection`. */
  projection?: MappingProjection
}

/** A mapping as a run reads it (ReadMappings' object form): a literal when `type` is `literal` in any case, otherwise
 * a wire from `sourceOutputName`; absent or null is ''. */
export type MappingAsRead =
  | { readonly literal: true; readonly value: VariableMapping['literalValue'] }
  | { readonly literal: false; readonly source: string }

/** `mapping` as a run reads it; every editor reader that draws or grades a mapping reads it through this. */
export function readMapping(mapping: VariableMapping): MappingAsRead {
  if (typeof mapping.type === 'string' && foldAsciiCase(mapping.type) === 'literal') return { literal: true, value: mapping.literalValue }
  return { literal: false, source: mapping.sourceOutputName ?? '' }
}

/** What a save writes (`getWorkflowDefinition`): the editor's model, except that an empty configuration or node list is
 * written as the loaded document held it, absent or null; the edge list and each mapping set are always written. */
export interface SavedWorkflowDefinition {
  nodes?: SavedWorkflowNode[] | null
  edges?: SavedWorkflowEdge[] | null
}

/** A node as a save writes it: each member the reader lets be null may be. */
export interface SavedWorkflowNode extends Omit<WorkflowNode, 'configuration' | 'subCanvases' | 'passthrough' | 'outputPorts'> {
  configuration?: WorkflowNode['configuration'] | null
  subCanvases?: Record<string, SavedWorkflowDefinition> | null
  passthrough?: string[] | null
  outputPorts?: DeclaredOutputPort[] | null
}

export interface SavedWorkflowEdge extends Omit<WorkflowEdge, 'mappings'> {
  mappings?: WorkflowEdge['mappings'] | [] | null
}

export interface WorkflowParameter {
  name: string
  displayName: string
  type: PortTypeName
  isRequired: boolean
}

export interface DeclaredOutputPort {
  name: string
  /** The user-facing label, omitted when the editor leaves it empty. */
  displayName?: string
  type: PortTypeName
  /** subCanvasName -> branch WorkflowOutput port name */
  sources?: Record<string, string>
}

export interface PortInfo {
  name: string
  displayName: string
  isRequired: boolean
  type: PortTypeName
  [key: string]: unknown
}

/** One step from a canvas down into a sub-canvas: the node that owns it and its branch name on that node. The
 * compiler's `CanvasPathSegment`, so a finding and a diagnostic name one canvas alike. */
export interface CanvasPathSegment {
  nodeId: string
  branchName: string
}

/** A validation result tied to the canvas node it concerns, so the editor can focus the EXACT block when several
 * share a type; `nodeId` is null for graph-wide findings. */
export interface ValidationFinding {
  nodeId: string | null
  /** Stable rule identifier shared with compiler diagnostics. */
  code: string
  /** Values a host catalogue may use to render this finding. */
  params?: Readonly<Record<string, unknown>>
  message: string
  /** The canvas the finding is on, root first; absent or empty for the root canvas. */
  canvasPath?: readonly CanvasPathSegment[]
  /** The wire the finding is about, when it is about a wire: `removeEdge(edgeId, canvasPath)` removes it. */
  edgeId?: string
  /** The compiler's rule id (CompilationRules) for a finding the library raises; absent on a host rule's finding. */
  ruleId?: string
  /** The port or configuration key the finding names, when it names one. */
  portName?: string
}

/** The configuration keys holding null, in order: a form the reader refuses, since clearing a value is removing
 * its key, and one the editor therefore never stores. A run reads a null key as present, so it is not an absence. */
export function nullConfigurationKeys(configuration: Readonly<Record<string, unknown>>): string[] {
  return Object.keys(configuration).filter((key) => configuration[key] === null)
}
