import { useSyncExternalStore } from 'react'
import type { WorkflowClient } from '@reeve-iot/workflow-client'
import { createReadCache } from './cache.js'

/** What a picker needs to offer a workflow, and nothing more. */
export interface WorkflowOption {
  readonly id: string
  readonly name: string
}

const NONE: readonly WorkflowOption[] = []
const ALL = 'all'

export interface WorkflowOptionsSource {
  /** The workflows a picker can offer. Empty on the first render and while the list is loading. */
  useWorkflowOptions: () => readonly WorkflowOption[]
  /** Drops the list, so the next render loads it again — call it after the host creates a workflow. */
  refresh: () => void
}

/**
 * The list behind a picker for a `Workflow` port, fetched once and shared. It returns data rather than a
 * control on purpose: what a workflow choice looks like is the host's decision, not this package's.
 */
export function createWorkflowOptionsSource(
  client: WorkflowClient,
  options: { readonly onError?: (error: unknown) => void } = {},
): WorkflowOptionsSource {
  const cache = createReadCache<readonly WorkflowOption[]>(
    async () => {
      const workflows = await client.getWorkflows()
      return workflows.map((w) => ({ id: w.id, name: w.name }))
    },
    NONE,
    (_, error) => { options.onError?.(error) },
  )

  const snapshot = (): readonly WorkflowOption[] => cache.read(ALL)

  return {
    useWorkflowOptions: () => useSyncExternalStore(cache.subscribe, snapshot, snapshot),
    refresh: cache.invalidate,
  }
}
