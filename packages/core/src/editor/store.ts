import type { StoreApi } from 'zustand'
import { createStore } from 'zustand'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { CanvasFrame } from '../model/canvas.js'
import type {
  CanvasPathSegment,
  DeclaredOutputPort,
  MappingProjection,
  NodeErrorPolicy,
  SavedWorkflowDefinition,
  ValidationFinding,
  VariableMapping,
  WorkflowDefinition,
  WorkflowEdge,
  WorkflowNode,
  WorkflowParameter,
} from '../model/workflow.js'
import {
  WORKFLOW_INPUT_BLOCK_TYPE_ID,
  WORKFLOW_INPUT_NODE_ID,
  WORKFLOW_OUTPUT_BLOCK_TYPE_ID,
  WORKFLOW_OUTPUT_NODE_ID,
  nullConfigurationKeys,
  readMapping,
} from '../model/workflow.js'
import type { StoredDefinitionRead, StoredDocumentFault } from '../model/storedDocument.js'
import { describeStoredDocumentFault, readMappingProjection, readStoredDefinition } from '../model/storedDocument.js'
import type { DraftAnalysis, DraftAnalysisDocument, DraftAnalysisSource } from '../analysis/draftAnalysis.js'
import { documentSignature } from '../analysis/documentSignature.js'
import type { PortTypeRegistry } from '../portTypes/types.js'
import type { PortTypeScope } from '../ports/portTypeResolver.js'
import { createPortTypeResolver } from '../ports/portTypeResolver.js'
import { createDefaultId } from './createId.js'
import type { UndrawableDocumentFault } from './editableDocument.js'
import { editorForm, restoredDefinition, undrawableMember } from './editableDocument.js'
import { validateBranchOutputs } from './validateBranchOutputs.js'
import { writesExit } from './declaredBranches.js'
import type { DocumentCanvas } from './canvasFrames.js'
import { boundBlockTypes, canvasSorts, documentCanvases } from './canvasFrames.js'
import { tracedPassthrough } from './passthroughTrace.js'
import { enumFieldFault } from './enumFieldFault.js'
import { validateNodes } from './validateNodes.js'
import { storedDocumentFaultFinding, validatePortTypeNames } from './validatePortTypeNames.js'
import type { NodeValidationRule } from './validationRules.js'
import type { ValidationMessageCatalogue } from './messageCatalogue.js'
import { validateStructureFindings } from './validateStructure.js'
import { retypeNodeLiterals } from './typedLiterals.js'
import { documentNodeLabels } from './nodeLabels.js'

export interface WorkflowEditorState {
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
  canvasStack: CanvasFrame[]
  selectedNodeId: string | null
  selectedEdgeId: string | null
  blockTypes: BlockTypeDescriptor[]
  pan: { x: number; y: number }
  zoom: number
  workflowInputParams: WorkflowParameter[]
  workflowOutputParams: WorkflowParameter[]
  isPaletteDragActive: boolean
  quickAddBlockTypeId: string | null
  /** True when the workflow has been modified since it was last loaded or saved. */
  isDirty: boolean
  /** Node the canvas should scroll to and pulse. */
  flashNodeId: string | null
  /** Bumped on every flashNode call, so asking for the SAME node twice re-triggers the pulse. */
  flashSeq: number
  /** The rule the last loaded document broke (see readStoredDefinition), or the member the editor cannot draw, or
   * null. While it is set the canvas is empty, it is the one finding, and getWorkflowDefinition throws. */
  loadedDocumentFault: LoadedDocumentFault | null
  /** The last loaded definition as the reader read it: getWorkflowDefinition writes back from it what the editor
   * does not hold, as it was loaded. Null before a load and after a refused one. */
  loadedDefinition: WorkflowDefinition | null
  /** The entry and exit nodes the editor added, each as its canvas path and id: syncSpecialNodes removes only these,
   * never a node the document or the author placed. */
  editorBoundaryNodes: readonly string[]
  /** The vocabulary the editor grades and offers terms by, read by the host when the editor mounts. */
  portTypes: PortTypeRegistry
  /** Counts the edits that change what a draft analysis could say: a node's position is not one. An answer is current only
   * while its revision is this one. */
  revision: number
  /** The last answer the server gave for a draft of this editor, or null before the first and after a document is loaded. */
  analysis: DraftAnalysis | null
  /** The error of the last request for an analysis or a vocabulary, or null once one succeeds. */
  analysisError: unknown

  addNode: (blockTypeId: string, position: { x: number; y: number }) => void
  updateNodePosition: (id: string, position: { x: number; y: number }) => void
  /** Replaces the node's configuration; throws a TypeError, writing nothing, when a value is null. */
  updateNodeConfig: (nodeId: string, config: WorkflowNode['configuration']) => void
  /** Writes some keys and leaves the rest, throwing as updateNodeConfig does. For the panel that owns a hidden
   * port: writing that one key through updateNodeConfig drops every other setting on the node, silently. */
  patchNodeConfig: (nodeId: string, patch: WorkflowNode['configuration']) => void
  updateNodePassthrough: (nodeId: string, passthrough: string[]) => void
  updateNodeOutputPorts: (nodeId: string, outputPorts: DeclaredOutputPort[]) => void
  /** Sets the node's own error policy, the one the run reads; null removes it, which the run reads as FailWorkflow. */
  updateNodeErrorPolicy: (nodeId: string, errorPolicy: NodeErrorPolicy | null) => void
  removeNode: (id: string) => void
  /** Wires source to target and selects the wire. A pair that already has one gets that one selected
   * instead of a second, invisible wire on top of it; a node is never wired to itself. */
  addEdge: (sourceNodeId: string, targetNodeId: string) => void
  /** Removes the wire with this id from the canvas at `canvasPath` (root first), or from the open canvas when it
   * is absent, whichever canvas is open and whatever its ends name: a finding's `edgeId` and `canvasPath` fit. */
  removeEdge: (id: string, canvasPath?: readonly CanvasPathSegment[]) => void
  updateEdgeMappings: (edgeId: string, mappings: Record<string, VariableMapping>) => void
  /** Sets, or with null clears, the projection of the open canvas's direct mapping into `inputName` on `edgeId`. Throws a
   * TypeError, writing nothing, on a shape the reader refuses or a wire with no direct mapping into that input. */
  setMappingProjection: (edgeId: string, inputName: string, projection: MappingProjection | null) => void
  /** Replaces the node's `typeArguments` with `typeArguments`; null or an empty record removes the key. What a host's
   * type-argument picker writes. A literal of one of the node's generic inputs, on an incoming wire of the open canvas or in its
   * configuration, that the closed term reads in another form is rewritten in it: the text `true` for a `Boolean`, a number's text
   * for an `Int` or a `Number`, a number or a boolean for a `String`. A literal that does not parse is left as stored. Throws a
   * TypeError, writing nothing, when a value is not a string. */
  setNodeTypeArguments: (nodeId: string, typeArguments: Readonly<Record<string, string>> | null) => void
  setSelectedNode: (id: string | null) => void
  setSelectedEdge: (id: string | null) => void
  setBlockTypes: (types: BlockTypeDescriptor[]) => void
  setPan: (pan: { x: number; y: number }) => void
  setZoom: (zoom: number) => void
  /** Loads a definition the host holds as a value, read first under the stored-document rules: one the reader
   * refuses loads as an empty canvas carrying loadedDocumentFault, never as a repaired graph. */
  loadWorkflow: (definition: WorkflowDefinition | SavedWorkflowDefinition) => void
  /** Loads what readStoredDefinitionText answered for the stored text, so a fault only the text shows (a
   * duplicate key, a surrogate pair written half raw) is kept: the adapters hand the editor this. */
  loadStoredWorkflow: (read: StoredDefinitionRead) => void
  /** Node-tagged errors that block a save, on every canvas of the document whichever is open, each with its
   * canvasPath. A loaded document that could not be read is the one finding, until another is loaded. */
  validateWorkflowFindings: () => ValidationFinding[]
  /** Node-tagged warnings that do not block a save. */
  validateWorkflowWarningFindings: () => ValidationFinding[]
  /** Alias of validateWorkflowFindings. */
  validateWorkflow: () => ValidationFinding[]
  /** Alias of validateWorkflowWarningFindings. */
  validateWorkflowWarnings: () => ValidationFinding[]
  /** Alias of validateStructureFindings. */
  validateStructure: () => ValidationFinding[]
  /** Graph-shape errors on every canvas, each at the depth the compiler grades it (see validateStructureFindings),
   * with its `canvasPath` and, for a wire, its `edgeId`; the same whichever canvas is open. */
  validateStructureFindings: () => ValidationFinding[]
  flashNode: (id: string) => void
  setWorkflowParams: (inputParams: WorkflowParameter[], outputParams: WorkflowParameter[]) => void
  /** Adds an entry or exit node where the open canvas calls for one and holds none of that block type; removes only one
   * it added. Nothing while loadedDocumentFault is set. */
  syncSpecialNodes: () => void
  enterSubCanvas: (nodeId: string, subCanvasName: string) => void
  exitSubCanvas: () => void
  /** Opens the canvas at `canvasPath` (root when absent or empty) and selects `nodeId` there, as a finding click does. */
  revealNode: (nodeId: string, canvasPath?: readonly CanvasPathSegment[]) => void
  exitToLevel: (depth: number) => void
  /** The document to save: what the editor holds, with what it does not hold written back from loadedDefinition.
   * Throws WorkflowDocumentUnreadableError while loadedDocumentFault is set, so nothing saves over that document. */
  getWorkflowDefinition: () => SavedWorkflowDefinition
  setPaletteDragActive: (active: boolean) => void
  requestQuickAddBlock: (blockTypeId: string) => void
  clearQuickAddBlock: () => void
  markSaved: () => void
}

export type { UndrawableDocumentFault } from './editableDocument.js'

/** Why a loaded document holds no canvas: a stored-document rule it broke, or a member the editor cannot draw. */
export type LoadedDocumentFault = StoredDocumentFault | UndrawableDocumentFault

/** Thrown by getWorkflowDefinition while the loaded document could not be read or drawn: there is no document to
 * save, and an empty one saved in its place would overwrite it. `fault` says why. */
export class WorkflowDocumentUnreadableError extends Error {
  constructor(readonly fault: LoadedDocumentFault) {
    const why = fault.rule === 'undrawable' ? 'cannot be drawn by the editor' : 'cannot be read'
    super(`The workflow definition ${why}, so it cannot be saved: ${describeStoredDocumentFault(fault)}.`)
    this.name = 'WorkflowDocumentUnreadableError'
  }
}

export interface WorkflowEditorStoreOptions {
  /** Host translations for validation codes. Individual components may override this catalogue. */
  validationMessages?: ValidationMessageCatalogue
  /** The host's port-type vocabulary. Assignability answers come from here, never from a built-in list. */
  portTypes: PortTypeRegistry
  /** Extra per-node checks that block a save. Run on every canvas, including sub-canvases. */
  nodeErrorRules?: readonly NodeValidationRule[]
  /** Extra per-node checks that only warn. Run on every canvas, including sub-canvases. */
  nodeWarningRules?: readonly NodeValidationRule[]
  /** Id generator for new nodes and edges. Defaults to a v4 uuid that does not require a secure context;
   * override to match a host's own id scheme or to make a test deterministic. */
  createId?: () => string
  /** Applied to every descriptor passed to `setBlockTypes`. A host whose catalogue does not yet carry a
   * field the editor reads — `outputSatisfyingBranchSets`, say — fills it in here, once, rather than at
   * each of the places that load the catalogue. */
  adaptBlockType?: (blockType: BlockTypeDescriptor) => BlockTypeDescriptor
  /** Supply a handler to omit and report block descriptors that fail host adaptation or registration.
   * Without a handler, setBlockTypes throws; the handler runs once per refused descriptor on each setter call. */
  onBlockTypeError?: (error: unknown) => void
  /** Asks the server about the draft after each edit, debounced: a request still running when the next edit comes is aborted,
   * and an answer is kept only for the revision on screen. Without it a port shows its declared term. */
  draftAnalysis?: DraftAnalysisSource
  /** Milliseconds an edit waits before its draft is posted. Default 300. */
  draftAnalysisDelayMs?: number
  /** Told of a failed analysis; the store keeps the last answer and `analysisError` either way. */
  onDraftAnalysisError?: (error: unknown) => void
}

type WorkflowEditorData = Pick<
  WorkflowEditorState,
  | 'nodes' | 'edges' | 'canvasStack' | 'selectedNodeId' | 'selectedEdgeId' | 'blockTypes' | 'pan' | 'zoom'
  | 'workflowInputParams' | 'workflowOutputParams' | 'isPaletteDragActive' | 'quickAddBlockTypeId'
  | 'isDirty' | 'flashNodeId' | 'flashSeq' | 'loadedDocumentFault' | 'loadedDefinition' | 'editorBoundaryNodes'
  | 'revision' | 'analysis' | 'analysisError'
>

function refuseNullValues(configuration: Readonly<Record<string, unknown>>): void {
  const keys = nullConfigurationKeys(configuration)
  if (keys.length > 0) throw new TypeError(`A configuration value is never null (${keys.join(', ')}): remove the key to clear it`)
}

const samePath = (a: readonly CanvasPathSegment[], b: readonly CanvasPathSegment[]): boolean =>
  a.length === b.length && a.every((s, i) => s.nodeId === b[i]?.nodeId && s.branchName === b[i]?.branchName)

function updateCanvasAt(
  canvas: WorkflowDefinition,
  path: readonly CanvasPathSegment[],
  edit: (canvas: WorkflowDefinition) => WorkflowDefinition | undefined,
): WorkflowDefinition | undefined {
  const [step, ...rest] = path
  if (!step) return edit(canvas)
  const owner = canvas.nodes.find((n) => n.id === step.nodeId)
  const sub = owner?.subCanvases?.[step.branchName]
  const updated = owner && sub ? updateCanvasAt(sub, rest, edit) : undefined
  if (!owner || !updated) return undefined
  return {
    ...canvas,
    nodes: canvas.nodes.map((n) => (n === owner ? { ...n, subCanvases: { ...n.subCanvases, [step.branchName]: updated } } : n)),
  }
}

export const createInitialWorkflowEditorData = (): WorkflowEditorData => ({
  nodes: [],
  edges: [],
  canvasStack: [],
  selectedNodeId: null,
  selectedEdgeId: null,
  blockTypes: [],
  pan: { x: 0, y: 0 },
  zoom: 1,
  workflowInputParams: [],
  workflowOutputParams: [],
  isPaletteDragActive: false,
  quickAddBlockTypeId: null,
  isDirty: false,
  flashNodeId: null,
  flashSeq: 0,
  loadedDocumentFault: null,
  loadedDefinition: null,
  editorBoundaryNodes: [],
  revision: 0,
  analysis: null,
  analysisError: null,
})

export type WorkflowEditorStore = ReturnType<typeof createWorkflowEditorStore>

export function createWorkflowEditorStore(options: WorkflowEditorStoreOptions) {
  const { nodeErrorRules = [], nodeWarningRules = [], createId = createDefaultId, adaptBlockType, onBlockTypeError } = options

  const store = createStore<WorkflowEditorState>((set, get) => {
  const boundaryNeeds = (): { input: boolean; output: boolean } => {
    const { canvasStack, blockTypes, workflowOutputParams } = get()
    const frame = canvasStack[canvasStack.length - 1]
    if (!frame) return { input: true, output: workflowOutputParams.length > 0 }
    const parentNode = frame.nodes.find((n) => n.id === frame.parentNodeId)
    const blockType = blockTypes.find((bt) => bt.blockTypeId === parentNode?.blockTypeId)
    return blockType ? { input: true, output: writesExit(parentNode, blockType, frame.subCanvasName) } : { input: false, output: false }
  }
  const isLastNeededBoundary = (node: WorkflowNode): boolean => {
    const needs = boundaryNeeds()
    const needed = node.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID ? needs.input : node.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID && needs.output
    return needed && get().nodes.filter((n) => n.blockTypeId === node.blockTypeId).length === 1
  }

  const currentDefinition = (): WorkflowDefinition => {
    const { nodes, edges, canvasStack } = get()
    let currentNodes = nodes
    let currentEdges = edges
    for (let i = canvasStack.length - 1; i >= 0; i--) {
      const frame = canvasStack[i]
      if (!frame) continue
      currentNodes = frame.nodes.map((n) =>
        n.id === frame.parentNodeId
          ? {
              ...n,
              subCanvases: { ...(n.subCanvases ?? {}), [frame.subCanvasName]: { nodes: currentNodes, edges: currentEdges } },
            }
          : n,
      )
      currentEdges = frame.edges
    }
    return { nodes: currentNodes, edges: currentEdges }
  }

  return {
    ...createInitialWorkflowEditorData(),
    portTypes: options.portTypes,

    addNode: (blockTypeId, position) => {
      const node: WorkflowNode = { id: createId(), blockTypeId, position, configuration: {} }
      set((state) => ({ nodes: [...state.nodes, node], selectedNodeId: node.id, selectedEdgeId: null, isDirty: true }))
    },

    updateNodePosition: (id, position) =>
      set((state) => ({ nodes: state.nodes.map((n) => (n.id === id ? { ...n, position } : n)), isDirty: true })),

    updateNodeConfig: (nodeId, configuration) => {
      refuseNullValues(configuration)
      set((state) => ({ nodes: state.nodes.map((n) => (n.id === nodeId ? { ...n, configuration } : n)), isDirty: true }))
    },

    patchNodeConfig: (nodeId, patch) => {
      refuseNullValues(patch)
      set((state) => ({
        nodes: state.nodes.map((n) =>
          (n.id === nodeId ? { ...n, configuration: { ...n.configuration, ...patch } } : n)),
        isDirty: true,
      }))
    },

    updateNodePassthrough: (nodeId, passthrough) =>
      set((state) => ({ nodes: state.nodes.map((n) => (n.id === nodeId ? { ...n, passthrough } : n)), isDirty: true })),

    updateNodeOutputPorts: (nodeId, outputPorts) =>
      set((state) => ({ nodes: state.nodes.map((n) => (n.id === nodeId ? { ...n, outputPorts } : n)), isDirty: true })),

    updateNodeErrorPolicy: (nodeId, errorPolicy) =>
      set((state) => ({
        nodes: state.nodes.map((n) => {
          if (n.id !== nodeId) return n
          if (errorPolicy !== null) return { ...n, errorPolicy }
          const rest = { ...n }
          delete rest.errorPolicy
          return rest
        }),
        isDirty: true,
      })),

    removeNode: (id) => {
      const removed = get().nodes.find((n) => n.id === id)
      if (removed && isLastNeededBoundary(removed)) return
      set((state) => {
        const edges = state.edges.filter((e) => e.sourceNodeId !== id && e.targetNodeId !== id)
        return {
          nodes: state.nodes.filter((n) => n.id !== id),
          edges,
          selectedNodeId: state.selectedNodeId === id ? null : state.selectedNodeId,
          selectedEdgeId: edges.some((e) => e.id === state.selectedEdgeId) ? state.selectedEdgeId : null,
          isDirty: true,
        }
      })
    },

    addEdge: (sourceNodeId, targetNodeId) => {
      if (sourceNodeId === targetNodeId) return
      const existing = get().edges.find((e) => e.sourceNodeId === sourceNodeId && e.targetNodeId === targetNodeId)
      if (existing) {
        set({ selectedEdgeId: existing.id, selectedNodeId: null })
        return
      }
      const edge: WorkflowEdge = { id: createId(), sourceNodeId, targetNodeId, mappings: {} }
      set((state) => ({ edges: [...state.edges, edge], selectedEdgeId: edge.id, selectedNodeId: null, isDirty: true }))
    },

    removeEdge: (id, canvasPath) => {
      const { canvasStack } = get()
      const openPath = canvasStack.map((f) => ({ nodeId: f.parentNodeId, branchName: f.subCanvasName }))
      if (!canvasPath || samePath(canvasPath, openPath)) {
        if (!get().edges.some((e) => e.id === id)) return
        set((state) => ({
          edges: state.edges.filter((e) => e.id !== id),
          selectedEdgeId: state.selectedEdgeId === id ? null : state.selectedEdgeId,
          isDirty: true,
        }))
        return
      }
      const definition = updateCanvasAt(currentDefinition(), canvasPath, (canvas) =>
        canvas.edges.some((e) => e.id === id) ? { ...canvas, edges: canvas.edges.filter((e) => e.id !== id) } : undefined)
      if (!definition) return
      const frames: CanvasFrame[] = []
      let open: WorkflowDefinition = definition
      for (const frame of canvasStack) {
        frames.push({ ...frame, nodes: open.nodes, edges: open.edges })
        open = open.nodes.find((n) => n.id === frame.parentNodeId)?.subCanvases?.[frame.subCanvasName] ?? { nodes: [], edges: [] }
      }
      set({ canvasStack: frames, nodes: open.nodes, edges: open.edges, isDirty: true })
    },

    updateEdgeMappings: (edgeId, mappings) => {
      const canonical = canonicalizeEdgeMappings(mappings)
      set((state) => ({ edges: state.edges.map((e) => (e.id === edgeId ? { ...e, mappings: canonical } : e)), isDirty: true }))
    },

    setMappingProjection: (edgeId, inputName, projection) => {
      const checked = projection === null ? null : readProjection(inputName, projection)
      const edge = get().edges.find((e) => e.id === edgeId)
      const mapping = edge && Object.hasOwn(edge.mappings, inputName) ? edge.mappings[inputName] : undefined
      if (checked === null && (!edge || !mapping || !Object.hasOwn(mapping, 'projection'))) return
      if (!edge || !mapping || readMapping(mapping).literal) throw new TypeError(`Wire '${edgeId}' has no direct mapping into '${inputName}' to project`)
      const written: VariableMapping = { ...mapping }
      delete written.projection
      if (checked !== null) written.projection = checked
      set((state) => ({
        edges: state.edges.map((e) => (e.id === edgeId ? { ...e, mappings: { ...e.mappings, [inputName]: written } } : e)),
        isDirty: true,
      }))
    },

    setNodeTypeArguments: (nodeId, typeArguments) => {
      if (typeArguments !== null && Object.values(typeArguments).some((term) => typeof term !== 'string')) {
        throw new TypeError('A type argument must be a port type term written as a string')
      }
      set((state) => {
        let edges = state.edges
        const nodes = state.nodes.map((n) => {
          if (n.id !== nodeId) return n
          const written: WorkflowNode = { ...n }
          delete written.typeArguments
          if (typeArguments === null || Object.keys(typeArguments).length === 0) return written
          written.typeArguments = { ...typeArguments }
          const retyped = retypeNodeLiterals(written, state.edges, state.blockTypes.find((bt) => bt.blockTypeId === n.blockTypeId), typeArguments, state.portTypes)
          edges = retyped.edges
          return { ...written, configuration: retyped.configuration }
        })
        return { nodes, edges, isDirty: true }
      })
    },

    setSelectedNode: (id) => set({ selectedNodeId: id, selectedEdgeId: null }),
    setSelectedEdge: (id) => set({ selectedEdgeId: id, selectedNodeId: null }),
    flashNode: (id) => set((state) => ({ flashNodeId: id, flashSeq: state.flashSeq + 1 })),
    setBlockTypes: (blockTypes) => {
      const adapted: BlockTypeDescriptor[] = []
      for (const descriptor of blockTypes) {
        try {
          const block = adaptBlockType ? adaptBlockType(descriptor) : descriptor
          const ports = [
            ...block.inputs, ...block.outputs,
            ...Object.values(block.subCanvasInputs ?? {}).flat(),
            ...Object.values(block.subCanvasOutputs ?? {}).flat(),
          ]
          const dotted = ports.find((port) => port.name.includes('.'))
          if (dotted !== undefined) throw new Error(`Block type '${block.blockTypeId}' declares name '${dotted.name}' containing a dot (rule dotted-name).`)
          const malformed = enumFieldFault(block)
          if (malformed !== undefined) throw new Error(malformed)
          adapted.push(block)
        } catch (error) {
          if (!onBlockTypeError) throw error
          const why = error instanceof Error ? error.message : String(error)
          onBlockTypeError(new Error(`Block type '${descriptor.blockTypeId}' is left out of the palette: ${why}`, { cause: error }))
        }
      }
      set({ blockTypes: adapted })
    },
    setPan: (pan) => set({ pan }),
    setZoom: (zoom) => set({ zoom }),
    setPaletteDragActive: (isPaletteDragActive) => set({ isPaletteDragActive }),
    requestQuickAddBlock: (quickAddBlockTypeId) => set({ quickAddBlockTypeId }),
    clearQuickAddBlock: () => set({ quickAddBlockTypeId: null }),
    markSaved: () => set({ isDirty: false }),

    loadWorkflow: (definition) => get().loadStoredWorkflow(readStoredDefinition(definition)),

    loadStoredWorkflow: (read) => {
      const fault = read.ok ? undrawableMember(read.definition) : read.fault
      if (!read.ok || fault) {
        set({
          nodes: [], edges: [], canvasStack: [], selectedNodeId: null, selectedEdgeId: null, isDirty: false,
          loadedDocumentFault: fault, loadedDefinition: null, editorBoundaryNodes: [],
        })
        return
      }
      const { nodes, edges } = editorForm(read.definition)
      set({
        nodes,
        edges,
        canvasStack: [],
        selectedNodeId: null,
        selectedEdgeId: null,
        isDirty: false,
        loadedDocumentFault: null,
        loadedDefinition: read.definition,
        editorBoundaryNodes: [],
      })
      get().syncSpecialNodes()
    },

    enterSubCanvas: (nodeId, subCanvasName) => {
      const { nodes, edges } = get()
      const node = nodes.find((n) => n.id === nodeId)
      if (!node) return
      const subDefinition = node.subCanvases?.[subCanvasName] ?? { nodes: [], edges: [] }
      set((state) => ({
        canvasStack: [...state.canvasStack, { parentNodeId: nodeId, subCanvasName, nodes, edges }],
        nodes: subDefinition.nodes,
        edges: subDefinition.edges,
        selectedNodeId: null,
        selectedEdgeId: null,
      }))
      get().syncSpecialNodes()
    },

    exitSubCanvas: () => {
      const { nodes, edges, canvasStack } = get()
      const frame = canvasStack[canvasStack.length - 1]
      if (!frame) return
      set({
        canvasStack: canvasStack.slice(0, -1),
        nodes: frame.nodes.map((n) =>
          n.id === frame.parentNodeId
            ? { ...n, subCanvases: { ...(n.subCanvases ?? {}), [frame.subCanvasName]: { nodes, edges } } }
            : n,
        ),
        edges: frame.edges,
        selectedNodeId: null,
        selectedEdgeId: null,
      })
      get().syncSpecialNodes()
    },

    revealNode: (nodeId, canvasPath = []) => {
      const open = get().canvasStack
      let common = 0
      while (
        common < open.length && common < canvasPath.length
        && open[common]?.parentNodeId === canvasPath[common]?.nodeId && open[common]?.subCanvasName === canvasPath[common]?.branchName
      ) common++
      get().exitToLevel(common)
      for (const segment of canvasPath.slice(common)) get().enterSubCanvas(segment.nodeId, segment.branchName)
      const arrived = get().canvasStack.length === canvasPath.length
      if (arrived && get().nodes.some((n) => n.id === nodeId)) get().setSelectedNode(nodeId)
    },

    exitToLevel: (depth) => {
      for (let i = get().canvasStack.length; i > depth; i--) get().exitSubCanvas()
    },

    getWorkflowDefinition: () => {
      const { loadedDocumentFault, loadedDefinition } = get()
      if (loadedDocumentFault) throw new WorkflowDocumentUnreadableError(loadedDocumentFault)
      return restoredDefinition(currentDefinition(), loadedDefinition)
    },

    setWorkflowParams: (workflowInputParams, workflowOutputParams) => {
      set({ workflowInputParams, workflowOutputParams })
      get().syncSpecialNodes()
    },

    syncSpecialNodes: () => {
      if (get().loadedDocumentFault) return
      const { nodes, edges, canvasStack, editorBoundaryNodes } = get()
      const needs = boundaryNeeds()
      const path = canvasPathKey(canvasStack)
      const added = new Set(editorBoundaryNodes)
      let updatedNodes = [...nodes]
      let updatedEdges = [...edges]

      const sync = (blockTypeId: string, reservedId: string, needed: boolean, place: (node: WorkflowNode) => void) => {
        if (needed && !updatedNodes.some((n) => n.blockTypeId === blockTypeId)) {
          let id = reservedId
          for (let i = 2; updatedNodes.some((n) => n.id === id); i++) id = `${reservedId}${String(i)}`
          place({ id, blockTypeId, position: blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID ? { x: 50, y: 100 } : { x: 800, y: 100 }, configuration: {} })
          added.add(`${path}\u0000${id}`)
        } else if (!needed) {
          const dropped = new Set(updatedNodes.filter((n) => n.blockTypeId === blockTypeId && added.has(`${path}\u0000${n.id}`)).map((n) => n.id))
          updatedNodes = updatedNodes.filter((n) => !dropped.has(n.id))
          updatedEdges = updatedEdges.filter((e) => !dropped.has(e.sourceNodeId) && !dropped.has(e.targetNodeId))
        }
      }
      sync(WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_INPUT_NODE_ID, needs.input, (n) => { updatedNodes = [n, ...updatedNodes] })
      sync(WORKFLOW_OUTPUT_BLOCK_TYPE_ID, WORKFLOW_OUTPUT_NODE_ID, needs.output, (n) => { updatedNodes = [...updatedNodes, n] })

      set({ nodes: updatedNodes, edges: updatedEdges, editorBoundaryNodes: [...added] })
    },

    validateStructure: () => get().validateStructureFindings(),

    validateStructureFindings: () => validateStructureFindings({ ...currentDefinition(), blockTypes: get().blockTypes }),

    validateWorkflowFindings: () => {
      const { blockTypes, workflowInputParams, workflowOutputParams, loadedDocumentFault } = get()
      if (loadedDocumentFault) {
        const fault = describeStoredDocumentFault(loadedDocumentFault)
        if (loadedDocumentFault.rule !== 'undrawable') {
          return [storedDocumentFaultFinding(loadedDocumentFault, 'The stored workflow', 'definition-unreadable', {
            labelOf: (type) => blockTypes.find((b) => b.blockTypeId === type)?.displayName ?? type,
          })]
        }
        const undrawable: ValidationFinding = {
          code: 'definition-undrawable', ruleId: 'definition-undrawable', params: { fault },
          nodeId: loadedDocumentFault.nodeId, message: `The editor cannot draw this workflow: ${fault}.`,
        }
        if (loadedDocumentFault.edgeId !== null) undrawable.edgeId = loadedDocumentFault.edgeId
        return [undrawable]
      }
      const definition = currentDefinition()
      const findings: ValidationFinding[] = []
      const frames = documentCanvases(definition, blockTypes)
      const nodeLabel = documentNodeLabels(frames[0]?.canvas ?? definition, blockTypes)
      const scopesOf = new Map<DocumentCanvas, PortTypeScope[]>()
      const entryOf = new Map<DocumentCanvas, readonly string[]>()
      const forwarded = (frame: DocumentCanvas): string[] => {
        const { parent, container } = frame
        if (!parent || !container) return []
        const { nodes, edges } = parent.canvas
        if (!canvasSorts(parent.canvas)) return container.node.passthrough ?? []
        return [...tracedPassthrough({ nodes, edges, bound: boundBlockTypes(nodes, blockTypes), entryPorts: entryOf.get(parent) }, container.node)]
      }
      for (const frame of frames) {
        const { nodes, edges } = frame.canvas
        const parent = frame.container && { nodeId: frame.container.node.id, subCanvasName: frame.container.branchName }
        const scopes: PortTypeScope[] = [{ nodes, edges, parent }, ...((frame.parent && scopesOf.get(frame.parent)) ?? [])]
        scopesOf.set(frame, scopes)
        const resolver = createPortTypeResolver({ scopes, blockTypes, workflowInputParams })
        const at = (finding: ValidationFinding): ValidationFinding => ({ ...finding, canvasPath: frame.path })
        const resolveOutputType = (nodeId: string, portName: string) => resolver.resolveOutputType(0, nodeId, portName)
        const branch = frame.container && {
          ...frame.container,
          outputs: frame.container.blockType?.subCanvasOutputs?.[frame.container.branchName] ?? [],
        }
        const exitPorts = branch ? branch.outputs : workflowOutputParams
        const entry = branch
          ? [...(branch.blockType?.subCanvasInputs?.[branch.branchName] ?? []).map((p) => p.name), ...forwarded(frame)]
          : workflowInputParams.map((p) => p.name)
        entryOf.set(frame, entry)
        findings.push(...validateNodes({
          nodes,
          edges,
          blockTypes,
          workflowOutputParams: exitPorts,
          portTypes: get().portTypes,
          resolveOutputType,
          rules: nodeErrorRules,
          signatureKnown: frame.signatureKnown,
          exitNoun: branch ? `Output of branch '${branch.branchName}'` : 'Workflow output',
          nodeLabel,
          canvasPorts: {
            entry,
            exit: [
              ...exitPorts.map((p) => p.name),
              ...(branch ? (branch.node.outputPorts ?? []).flatMap((p) => p.sources?.[branch.branchName] ?? []) : []),
            ],
          },
        }).map(at))
      }
      const graded = new Set(frames.map((f) => JSON.stringify(f.path)))
      const names = validatePortTypeNames({ definition, workflowInputParams, workflowOutputParams, blockTypes })
      return [...findings, ...names.filter((f) => graded.has(JSON.stringify(f.canvasPath ?? [])))]
    },

    validateWorkflow: () => get().validateWorkflowFindings(),
    validateWorkflowWarnings: () => get().validateWorkflowWarningFindings(),

    validateWorkflowWarningFindings: () =>
      get().loadedDocumentFault
        ? []
        : validateBranchOutputs({
            definition: currentDefinition(),
            blockTypes: get().blockTypes,
            workflowInputParams: get().workflowInputParams,
            portTypes: get().portTypes,
            rules: nodeWarningRules,
          }),
  }
  })

  followDraft(store, options)
  return store
}

function followDraft(store: StoreApi<WorkflowEditorState>, options: WorkflowEditorStoreOptions): void {
  const { draftAnalysis: source, draftAnalysisDelayMs = 300, onDraftAnalysisError } = options
  const documentOf = (state: WorkflowEditorState): DraftAnalysisDocument | null =>
    state.loadedDocumentFault
      ? null
      : { definition: state.getWorkflowDefinition(), inputParameters: state.workflowInputParams, outputParameters: state.workflowOutputParams }
  const signatureOf = (state: WorkflowEditorState): string => {
    const document = documentOf(state)
    return document === null ? 'unreadable' : documentSignature(document)
  }
  const fail = (error: unknown): void => {
    store.setState({ analysisError: error })
    onDraftAnalysisError?.(error)
  }

  let timer: ReturnType<typeof setTimeout> | undefined
  let running: AbortController | undefined
  const ask = async (): Promise<void> => {
    const state = store.getState()
    const document = documentOf(state)
    if (source === undefined || document === null) return
    const controller = new AbortController()
    running = controller
    const { revision } = state
    try {
      const answer = await source.analyze(document, revision, controller.signal)
      if (controller.signal.aborted || store.getState().revision !== revision || answer.revision !== revision) return
      store.setState({ analysis: answer, analysisError: null })
    } catch (error) {
      if (!controller.signal.aborted) fail(error)
    }
  }
  const schedule = (): void => {
    if (source === undefined) return
    clearTimeout(timer)
    running?.abort()
    running = undefined
    if (store.getState().loadedDocumentFault) return
    timer = setTimeout(() => void ask(), draftAnalysisDelayMs)
  }

  let signature = signatureOf(store.getState())
  store.subscribe((state, previous) => {
    const loaded = state.loadedDefinition !== previous.loadedDefinition || state.loadedDocumentFault !== previous.loadedDocumentFault
    const touched = loaded || state.nodes !== previous.nodes || state.edges !== previous.edges || state.canvasStack !== previous.canvasStack
      || state.workflowInputParams !== previous.workflowInputParams || state.workflowOutputParams !== previous.workflowOutputParams
    if (!touched) return
    if (!loaded && onlyMoved(state, previous)) return
    const next = signatureOf(state)
    if (!loaded && next === signature) return
    signature = next
    store.setState({ revision: state.revision + 1, ...(loaded ? { analysis: null, analysisError: null } : {}) })
    schedule()
  })
}

function onlyMoved(state: WorkflowEditorState, previous: WorkflowEditorState): boolean {
  return state.edges === previous.edges && state.canvasStack === previous.canvasStack
    && state.workflowInputParams === previous.workflowInputParams && state.workflowOutputParams === previous.workflowOutputParams
    && state.nodes.length === previous.nodes.length
    && state.nodes.every((node, index) => {
      const was = previous.nodes[index]
      return node === was || (was !== undefined && differsOnlyInPosition(node, was))
    })
}

function differsOnlyInPosition(node: WorkflowNode, was: WorkflowNode): boolean {
  const now = node as unknown as Record<string, unknown>
  const before = was as unknown as Record<string, unknown>
  const keys = new Set([...Object.keys(now), ...Object.keys(before)])
  return [...keys].every((key) => key === 'position' || now[key] === before[key])
}

function canvasPathKey(canvasStack: readonly CanvasFrame[]): string {
  return canvasStack.map((f) => `${f.parentNodeId}\u0001${f.subCanvasName}`).join('\u0002')
}

function readProjection(target: string, raw: unknown): MappingProjection {
  const read = readMappingProjection(raw)
  if (!read.ok) throw new TypeError(`Mapping '${target}' has a projection the reader refuses: ${describeStoredDocumentFault(read.fault)}`)
  return read.projection
}

function canonicalizeEdgeMappings(mappings: Record<string, VariableMapping>): Record<string, VariableMapping> {
  const canonical: Record<string, VariableMapping> = {}
  if (Array.isArray(mappings)) throw new TypeError('Mappings must be an object, not an array (rule L7)')
  for (const [target, raw] of Object.entries(mappings)) {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw new TypeError(`Mapping '${target}' must be an object`)
    if (Object.keys(raw).some((key) => !['type', 'sourceOutputName', 'literalValue', 'projection'].includes(key))) {
      throw new TypeError(`Mapping '${target}' contains an unlisted key`)
    }
    const literal = typeof raw.type === 'string' && raw.type.toLowerCase() === 'literal'
    if (Object.hasOwn(raw, 'literalValue') && raw.literalValue === null) {
      throw new TypeError(`Mapping '${target}' cannot have a null literalValue`)
    }
    const projection = Object.hasOwn(raw, 'projection') ? { projection: readProjection(target, raw.projection) } : {}
    if (literal) {
      canonical[target] = { type: 'literal', ...(Object.hasOwn(raw, 'literalValue') ? { literalValue: raw.literalValue } : {}), ...projection }
      continue
    }
    const source = raw.sourceOutputName
    if (typeof source !== 'string' || source.length === 0) continue
    canonical[target] = { type: 'direct', sourceOutputName: source, ...projection }
  }
  return canonical
}
