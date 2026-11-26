import type { DraftAnalysisResponse, WorkflowEditorClient } from '@reeve-iot/workflow-client'
import type {
  CanvasPathSegment, DraftAnalysis, DraftAnalysisDocument, DraftAnalysisSource, DraftPortAnalysis, DraftPortState, SavedWorkflowDefinition,
} from '@reeve-iot/workflow-core'
import { WORKFLOW_INPUT_BLOCK_TYPE_ID } from '@reeve-iot/workflow-core'

const STATES: readonly DraftPortState[] = ['bound', 'unbound', 'refused']

function readState(state: string, where: string): DraftPortState {
  const found = STATES.find((candidate) => candidate === state)
  if (found === undefined) throw new Error(`The server answered a draft analysis with port state '${state}' at ${where}, which this editor does not know.`)
  return found
}

function readPath(path: ReadonlyArray<{ readonly nodeId: string; readonly branchName: string }>): readonly CanvasPathSegment[] {
  return path.map((segment) => ({ nodeId: segment.nodeId, branchName: segment.branchName }))
}

type WirePort = DraftAnalysisResponse['nodes'][number]['inputs'][number]

const pathKey = (path: ReadonlyArray<{ readonly nodeId: string; readonly branchName: string }>): string =>
  path.map((s) => `${s.nodeId}\u0001${s.branchName}`).join('\u0002')

function passthroughNames(canvas: SavedWorkflowDefinition, path: string, owner: ReadonlySet<string> | undefined, into: Map<string, ReadonlySet<string>>): void {
  if (!Array.isArray(canvas.nodes)) return
  for (const node of canvas.nodes) {
    const own = new Set(Array.isArray(node.passthrough) ? node.passthrough : [])
    into.set(`${path}\u0000${node.id}`, node.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID && owner !== undefined ? owner : own)
    if (node.subCanvases === null || typeof node.subCanvases !== 'object') continue
    for (const [branch, sub] of Object.entries(node.subCanvases)) {
      const segment = `${node.id}\u0001${branch}`
      passthroughNames(sub, path === '' ? segment : `${path}\u0002${segment}`, own, into)
    }
  }
}

function readPort(port: WirePort, where: string, given: ReadonlySet<string> | undefined): DraftPortAnalysis {
  if (port.declared === null && given?.has(port.name) !== true) {
    throw new Error(`The server answered a draft analysis with no declared term for ${where}.${port.name}, which no passthrough entry names.`)
  }
  const read: DraftPortAnalysis = {
    name: port.name,
    declared: port.declared,
    effective: port.effective,
    state: readState(port.state, `${where}.${port.name}`),
    unbound: [...port.unbound],
  }
  if (port.expression !== undefined && port.expression !== null) read.expression = port.expression
  if (port.projection !== undefined && port.projection !== null) read.projection = port.projection
  if (read.state === 'bound' && read.effective === null) {
    throw new Error(`The server answered a draft analysis with a bound port with no effective term at ${where}.${port.name}.`)
  }
  if (read.state === 'unbound' && read.declared === null) {
    throw new Error(`The server answered a draft analysis with an unbound port with no declared term at ${where}.${port.name}.`)
  }
  return read
}

/** Reads the server's draft analysis as the editor holds it, against the `definition` it was asked for. Throws, naming the
 * place, for an unknown state, a bound port with no term, or a null `declared` that no `passthrough` entry of the definition names. */
export function readDraftAnalysis(response: DraftAnalysisResponse, definition: SavedWorkflowDefinition): DraftAnalysis {
  const names = new Map<string, ReadonlySet<string>>()
  passthroughNames(definition, '', undefined, names)
  return {
    revision: response.revision,
    registryGeneration: response.registryGeneration,
    diagnostics: response.diagnostics.map((d) => ({
      ruleId: d.ruleId,
      message: d.message,
      nodeId: d.nodeId,
      portName: d.portName,
      canvasPath: readPath(d.canvasPath),
    })),
    nodes: response.nodes.map((node) => {
      const where = `node '${node.nodeId}'`
      const given = names.get(`${pathKey(node.canvasPath)}\u0000${node.nodeId}`)
      return {
        canvasPath: readPath(node.canvasPath),
        nodeId: node.nodeId,
        typeArguments: { ...node.typeArguments },
        inputs: node.inputs.map((port) => readPort(port, `${where} input`, given)),
        outputs: node.outputs.map((port) => readPort(port, `${where} output`, given)),
      }
    }),
  }
}

export interface DraftAnalysisSourceOptions {
  /** The workflow the draft belongs to, read when each request is sent, or null for one not yet created. The server
   * checks that the caller owns it; the compile is the one a save runs. */
  workflowId?: () => string | null
}

/** A {@link DraftAnalysisSource} over the Editor client: posts the draft to `analyzeDraft` and reads the answer. A request the
 * editor supersedes is aborted through the signal. */
export function createDraftAnalysisSource(
  client: Pick<WorkflowEditorClient, 'analyzeDraft'>, options: DraftAnalysisSourceOptions = {},
): DraftAnalysisSource {
  return {
    analyze: async (document: DraftAnalysisDocument, revision: number, signal: AbortSignal): Promise<DraftAnalysis> => {
      const response = await client.analyzeDraft({
        body: {
          workflowId: options.workflowId?.() ?? null,
          revision,
          definitionJson: JSON.stringify(document.definition),
          inputParametersJson: JSON.stringify(document.inputParameters),
          outputParametersJson: JSON.stringify(document.outputParameters),
        },
      }, { signal })
      return readDraftAnalysis(response, document.definition)
    },
  }
}
