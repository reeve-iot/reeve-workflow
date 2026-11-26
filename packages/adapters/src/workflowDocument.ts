import { useCallback, useMemo, useSyncExternalStore } from 'react'
import type { WorkflowClient, WorkflowResponse } from '@reeve-iot/workflow-client'
import { readStoredDefinitionText, usePortTypes } from '@reeve-iot/workflow-core'
import type { PortTypeRegistry, StoredDefinitionRead, StoredDocumentFault, WorkflowParameter } from '@reeve-iot/workflow-core'
import { createReadCache } from './cache.js'
import { readDeclaredPorts } from './workflowReference.js'

/** One workflow as the editor loads it: the graph as the stored-document reader read it, and the parameters its
 * entry and exit nodes carry, the draft's while there is one. Pass `definition` to the store's `loadStoredWorkflow`. */
export interface WorkflowDocument {
  readonly id: string
  readonly name: string
  /** The definition and both parameter lists as read: a fault in any of them is the fault here, so a store
   * loading it holds an empty canvas that cannot be saved over the stored workflow. */
  readonly definition: StoredDefinitionRead
  readonly inputParams: WorkflowParameter[]
  readonly outputParams: WorkflowParameter[]
}

/** The stored graph through the stored-document reader, every reader rule over the text: a graph it
 * refuses is a fault, never an empty canvas or the half that happened to parse; an absent text is the fault no document. */
export function readWorkflowDefinition(json: string | null | undefined): StoredDefinitionRead {
  return readStoredDefinitionText(json)
}

function listFault(fault: StoredDocumentFault, side: string): StoredDefinitionRead {
  return { ok: false, fault: { ...fault, detail: `the declared ${side} parameters: ${fault.detail}` } }
}

export function readWorkflowDocument(response: WorkflowResponse, portTypes: PortTypeRegistry): WorkflowDocument {
  const inputs = readDeclaredPorts(response.inputParametersJson, portTypes)
  const outputs = readDeclaredPorts(response.outputParametersJson, portTypes)
  return {
    id: response.id,
    name: response.name,
    definition: !inputs.ok
      ? listFault(inputs.fault, 'input')
      : !outputs.ok
        ? listFault(outputs.fault, 'output')
        : readWorkflowDefinition(response.definitionJson),
    inputParams: inputs.ok ? inputs.ports : [],
    outputParams: outputs.ok ? outputs.ports : [],
  }
}

async function withDraftDocuments(client: WorkflowClient, response: WorkflowResponse): Promise<WorkflowResponse> {
  if (!response.draftVersionId || response.documentsFrom === 'Draft') return response
  if (typeof response.draftVersionNumber !== 'number') {
    throw new Error(`The workflow '${response.id}' has the draft '${response.draftVersionId}' and no draft version number to read it by.`)
  }
  const draft = await client.getVersion({ id: response.id, versionNumber: response.draftVersionNumber })
  return {
    ...response,
    definitionJson: draft.definitionJson,
    inputParametersJson: draft.inputParametersJson,
    outputParametersJson: draft.outputParametersJson,
    documentsFrom: 'Draft',
  }
}

export interface WorkflowDocumentSource {
  /** The workflow to edit, or null while it loads and for one that could not be read. */
  useWorkflow: (workflowId: string | null) => WorkflowDocument | null
  /** Drops every loaded workflow, so the next render fetches again — call it after a save. */
  refresh: () => void
}

/**
 * What the editor is editing, refreshed on save; the draft's documents while there is a draft, read with
 * `getVersion`. A draft that cannot be read goes to `onError` and loads as null, never as the current documents.
 */
export function createWorkflowDocumentSource(
  client: WorkflowClient,
  options: { readonly onError?: (workflowId: string, error: unknown) => void } = {},
): WorkflowDocumentSource {
  const cache = createReadCache<WorkflowResponse | null>(
    async (id) => withDraftDocuments(client, await client.getWorkflow({ id })),
    null,
    options.onError,
  )

  return {
    useWorkflow(workflowId) {
      const portTypes = usePortTypes()
      const getSnapshot = useCallback(
        () => (workflowId === null || workflowId === '' ? null : cache.read(workflowId)),
        [workflowId],
      )
      const response = useSyncExternalStore(cache.subscribe, getSnapshot, getSnapshot)
      return useMemo(() => (response === null ? null : readWorkflowDocument(response, portTypes)), [response, portTypes])
    },
    refresh: cache.invalidate,
  }
}
