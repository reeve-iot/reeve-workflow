import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import { createWorkflowDocumentSource, createWorkflowReferenceSource } from '@reeve-iot/workflow-adapters'
import { WorkflowApiError } from '@reeve-iot/workflow-client'
import type { WorkflowClient, WorkflowEditorClient, WorkflowExportDto, WorkflowResponse } from '@reeve-iot/workflow-client'
import {
  BUILTIN_PORT_TYPES, WorkflowEditorStoreProvider, WorkflowReferenceResolverContext, createPortTypeRegistry,
  useWorkflowEditorStoreApi,
} from '@reeve-iot/workflow-mui'
import type { DraftAnalysisSource, WorkflowEditorStore } from '@reeve-iot/workflow-mui'
import { EditorShell } from './EditorShell.js'
import type { Seams } from './seams.js'

const DELAY = 10

const original = createPortTypeRegistry([...BUILTIN_PORT_TYPES])

const widget = (id: string) => ({ id, blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: {} })

function stored(id: string, nodeId: string): WorkflowResponse {
  return {
    id,
    name: `Workflow ${id}`,
    description: null,
    definitionJson: JSON.stringify({ nodes: [widget(nodeId)], edges: [] }),
    inputParametersJson: null,
    outputParametersJson: null,
    checkpointPolicyJson: null,
    createdAtUtc: '2026-01-01T00:00:00Z',
    createdByUserId: 'u',
    currentVersionId: 'v1',
    currentVersionNumber: 1,
    documentsFrom: 'Current',
    draftVersionId: null,
    draftVersionNumber: null,
    executionTimeoutSeconds: null,
    hostMetadata: null,
    hasImportWarnings: false,
    importWarningsJson: null,
    maxConcurrentRuns: 1,
    maxQueuedRuns: 10,
    mode: 'Single',
    overflowBehavior: 'Drop',
    retryPolicyJson: null,
    revision: 1,
    updatedAtUtc: '2026-01-01T00:00:00Z',
  }
}

const WORKFLOWS: Readonly<Record<string, WorkflowResponse>> = { a: stored('a', 'from-a'), b: stored('b', 'from-b') }

function fakeSeams(
  analyses: { count: number }, refusal: { diagnostics: unknown[] } | null = null, writes: Partial<WorkflowEditorClient> = {},
): Seams {
  const reads = {
    getWorkflows: async () => Object.values(WORKFLOWS).map((workflow) => ({
      id: workflow.id, name: workflow.name, description: null, currentVersionNumber: 1, hostMetadata: null,
    })),
    getWorkflow: async ({ id }: { id: string }) => {
      const found = WORKFLOWS[id]
      if (found === undefined) throw new Error(`no workflow '${id}'`)
      return found
    },
    getVersions: async () => [],
    validateImport: async ({ body }: { body: WorkflowExportDto }) => {
      if (refusal !== null) throw new WorkflowApiError(400, 'validateImport', { detail: 'refused', ...refusal })
      return {
        missingBlockTypeIds: [], missingWorkflowIds: [], referenceIdsByPortType: {}, droppedSecrets: [],
        definitionJson: body.definitionJson ?? '', inputParametersJson: body.inputParametersJson, outputParametersJson: body.outputParametersJson,
      }
    },
  } as unknown as WorkflowClient
  return {
    reads,
    writes: writes as unknown as WorkflowEditorClient,
    catalogue: { useBlockTypes: () => [], refresh: () => {} },
    document: createWorkflowDocumentSource(reads),
    workflowOptions: { useWorkflowOptions: () => [], refresh: () => {} },
    reference: createWorkflowReferenceSource(reads),
    analysis: {
      analyze: async (_document, revision) => {
        analyses.count += 1
        return { revision, registryGeneration: 4, diagnostics: [], nodes: [] }
      },
    } satisfies DraftAnalysisSource,
    readVocabulary: async () => original,
    openWorkflow: { set: () => {} },
  }
}

function mount(seams: Seams) {
  const held: { store: WorkflowEditorStore | null } = { store: null }
  function Capture() {
    held.store = useWorkflowEditorStoreApi()
    return null
  }
  render(
    <ThemeProvider theme={createTheme()}>
      <WorkflowEditorStoreProvider options={{
        portTypes: original,
        draftAnalysis: seams.analysis,
        draftAnalysisDelayMs: DELAY,
      }}>
        <Capture />
        <WorkflowReferenceResolverContext.Provider value={seams.reference.resolver}>
          <EditorShell seams={seams} />
        </WorkflowReferenceResolverContext.Provider>
      </WorkflowEditorStoreProvider>
    </ThemeProvider>,
  )
  const store = () => {
    if (held.store === null) throw new Error('The editor store was never mounted.')
    return held.store
  }
  const nodeIds = () => store().getState().nodes.map((node) => node.id).filter((id) => id.startsWith('from-') || id.startsWith('opened-'))
  return { store, nodeIds }
}

describe('EditorShell', () => {
  beforeAll(() => {
    const proto = SVGSVGElement.prototype as unknown as Record<string, unknown>
    proto.createSVGPoint = function createSVGPoint() {
      return { x: 0, y: 0, matrixTransform() { return { x: this.x, y: this.y } } }
    }
    proto.getScreenCTM = function getScreenCTM() { return { inverse: () => null } }
  })
  afterAll(() => {
    const proto = SVGSVGElement.prototype as unknown as Record<string, unknown>
    delete proto.createSVGPoint
    delete proto.getScreenCTM
  })

  it('holds the file as written and reports what the server found in it', async () => {
    const user = userEvent.setup()
    const { store, nodeIds } = mount(fakeSeams({ count: 0 }))

    await user.click(await screen.findByTestId('workflow-a'))
    await waitFor(() => { expect(nodeIds()).toEqual(['from-a']) })

    const file = new File([JSON.stringify({
      name: 'Eight export',
      definitionJson: JSON.stringify({ nodes: [widget('opened-1')], edges: [] }),
      inputParametersJson: JSON.stringify([{ name: 'Room', displayName: 'Room', isRequired: true, type: 'Number' }]),
      outputParametersJson: null,
    })], 'export.json', { type: 'application/json' })
    await user.upload(screen.getByTestId('open-file-input'), file)

    await waitFor(() => { expect(nodeIds()).toEqual(['opened-1']) })
    expect(store().getState().workflowInputParams.map((parameter) => parameter.type)).toEqual(['Number'])
    expect(screen.getByTestId('status')).toHaveTextContent('Nothing was stored')
  })

  it('shows the file as written when the import is refused', async () => {
    const user = userEvent.setup()
    const { store, nodeIds } = mount(fakeSeams({ count: 0 }, { diagnostics: [] }))

    await user.click(await screen.findByTestId('workflow-a'))
    const file = new File([JSON.stringify({
      name: 'Refused export',
      definitionJson: JSON.stringify({ nodes: [widget('opened-1')], edges: [] }),
      inputParametersJson: JSON.stringify([{ name: 'Room', displayName: 'Room', isRequired: true, type: 'Float' }]),
      outputParametersJson: null,
    })], 'export.json', { type: 'application/json' })
    await user.upload(screen.getByTestId('open-file-input'), file)

    await waitFor(() => { expect(nodeIds()).toEqual(['opened-1']) })
    expect(store().getState().workflowInputParams.map((parameter) => parameter.name)).toEqual(['Room'])
    expect(screen.getByTestId('status')).toHaveTextContent('import/validate')
  })

  it('sends back the revision each save answered with', async () => {
    const user = userEvent.setup()
    const sent: (number | null)[] = []
    const updateWorkflow = async ({ body }: { body: { expectedRevision: number | null } }) => {
      sent.push(body.expectedRevision)
      return { ...stored('a', 'from-a'), revision: (body.expectedRevision ?? 0) + 1 }
    }
    const { nodeIds } = mount(fakeSeams({ count: 0 }, null, { updateWorkflow } as unknown as Partial<WorkflowEditorClient>))

    await user.click(await screen.findByTestId('workflow-a'))
    await waitFor(() => { expect(nodeIds()).toEqual(['from-a']) })
    await user.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => { expect(sent).toEqual([1]) })
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => { expect(sent).toEqual([1, 2]) })
  })

  it('loads the other workflow when another one is picked', async () => {
    const user = userEvent.setup()
    const { nodeIds } = mount(fakeSeams({ count: 0 }))

    await user.click(await screen.findByTestId('workflow-a'))
    await waitFor(() => { expect(nodeIds()).toEqual(['from-a']) })

    await user.click(screen.getByTestId('workflow-b'))
    await waitFor(() => { expect(nodeIds()).toEqual(['from-b']) })
    expect(screen.getByTestId('dirty')).toHaveAttribute('data-dirty', 'false')
  })
})
