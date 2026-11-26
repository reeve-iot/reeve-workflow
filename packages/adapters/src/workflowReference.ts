import { useCallback, useMemo, useSyncExternalStore } from 'react'
import type { WorkflowClient } from '@reeve-iot/workflow-client'
import { describeStoredDocumentFault, readParameterListText, usePortTypes } from '@reeve-iot/workflow-core'
import type {
  PortInfo, PortTypeRegistry, StoredDocumentFault, WorkflowParameterPorts, WorkflowReferenceResolver,
} from '@reeve-iot/workflow-core'
import { createReadCache } from './cache.js'

const EMPTY: WorkflowParameterPorts = { inputs: [], outputs: [] }

/** A parameter list read as ports, or the first rule its stored text breaks. */
export type DeclaredPortsRead = { ok: true; ports: PortInfo[] } | { ok: false; fault: StoredDocumentFault }

/**
 * Reads one of the two parameter fields through the stored-document reader: an absent list and `[]` are no
 * parameters, blank text and `null` are refused, and so is a list the reader refuses, whole, never read as
 * the entries that happen to parse.
 * A type is normalised into the host's vocabulary; an absent one is blank, never Any.
 */
export function readDeclaredPorts(json: string | null, portTypes: PortTypeRegistry): DeclaredPortsRead {
  const read = readParameterListText(json, 'input')
  if (!read.ok) return read
  return {
    ok: true,
    ports: read.sites.map((site, i) => {
      const displayName = read.entries[i]?.['displayName']
      return {
        name: site.portName,
        displayName: typeof displayName === 'string' && displayName !== '' ? displayName : site.portName,
        isRequired: site.isRequired,
        type: site.rawType === null ? '' : portTypes.normalize(site.rawType),
      }
    }),
  }
}

/** Thrown into `onError` for a referenced workflow whose parameter list cannot be read; it then draws no ports. */
export class WorkflowReferenceUnreadableError extends Error {
  constructor(readonly workflowId: string, readonly fault: StoredDocumentFault) {
    super(`The declared parameters of workflow '${workflowId}' cannot be read: ${describeStoredDocumentFault(fault)}.`)
    this.name = 'WorkflowReferenceUnreadableError'
  }
}

export interface WorkflowReferenceResolverOptions {
  /** Called when a workflow cannot be read. Without it a failure is silent, which is the editor's own
   * contract: an unresolvable reference draws no ports rather than breaking the canvas. */
  readonly onError?: (workflowId: string, error: unknown) => void
}

export interface WorkflowReferenceSource {
  /** The seam `core` declares. Pass it to `WorkflowReferenceResolverContext`. */
  resolver: WorkflowReferenceResolver
  /** Forgets every referenced workflow, so the next render reads them again. */
  refresh: () => void
}

/**
 * The seam `core` declares, implemented once. Call it outside render — the resolver's identity has to
 * stay stable for the editor's lifetime — and pass `resolver` to `WorkflowReferenceResolverContext`.
 *
 * Each referenced workflow is fetched once and shared by every node that references it. A workflow that
 * cannot be read resolves to no ports and is not asked for again until `refresh()`, which is also what
 * makes a child's newly declared parameter appear on the parent that runs it.
 */
export function createWorkflowReferenceSource(
  client: WorkflowClient,
  options: WorkflowReferenceResolverOptions = {},
): WorkflowReferenceSource {
  const cache = createReadCache<{ inputParametersJson: string | null; outputParametersJson: string | null } | null>(
    async (id) => {
      const workflow = await client.getWorkflow({ id })
      for (const json of [workflow.inputParametersJson, workflow.outputParametersJson]) {
        const read = readParameterListText(json, 'input')
        if (!read.ok) throw new WorkflowReferenceUnreadableError(id, read.fault)
      }
      return workflow
    },
    null,
    options.onError,
  )

  function useWorkflowReference(workflowId: string | null): WorkflowParameterPorts {
    const portTypes = usePortTypes()
    const getSnapshot = useCallback(
      () => (workflowId === null || workflowId === '' ? null : cache.read(workflowId)),
      [workflowId],
    )
    const workflow = useSyncExternalStore(cache.subscribe, getSnapshot, getSnapshot)

    return useMemo(() => {
      if (workflow === null) return EMPTY
      const inputs = readDeclaredPorts(workflow.inputParametersJson, portTypes)
      const outputs = readDeclaredPorts(workflow.outputParametersJson, portTypes)
      return inputs.ok && outputs.ok ? { inputs: inputs.ports, outputs: outputs.ports } : EMPTY
    }, [workflow, portTypes])
  }

  return { resolver: useWorkflowReference, refresh: cache.invalidate }
}
