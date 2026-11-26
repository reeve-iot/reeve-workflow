import { createContext, useContext } from 'react'
import type { PortInfo } from '../model/workflow.js'

/** The declared parameters of another workflow. Empty while the host is still fetching, or when the
 * reference points at a workflow that no longer exists. */
export interface WorkflowParameterPorts {
  inputs: PortInfo[]
  outputs: PortInfo[]
}

/** Resolves a workflow reference to that workflow's declared parameters.
 *
 * **Called as a React hook**, once per node that declares `runsWorkflowFrom`, so it may fetch — which is
 * exactly why it is a seam: the library has no persistence and no idea where a host keeps its workflows.
 * The identity must stay stable for the editor's lifetime, and name the implementation `use…` or the
 * rules-of-hooks lint will not recognise it as one. */
export type WorkflowReferenceResolver = (workflowId: string | null) => WorkflowParameterPorts

const NONE: WorkflowReferenceResolver = () => ({ inputs: [], outputs: [] })

export const WorkflowReferenceResolverContext = createContext<WorkflowReferenceResolver>(NONE)

export function useWorkflowReferenceResolver(): WorkflowReferenceResolver {
  return useContext(WorkflowReferenceResolverContext)
}

/** The workflow a node references, or null when the block does not run one or nothing is chosen yet. */
export function readWorkflowReference(
  configuration: Record<string, unknown>,
  runsWorkflowFrom: string | null | undefined,
): string | null {
  if (!runsWorkflowFrom) return null
  const value = configuration[runsWorkflowFrom]
  return typeof value === 'string' && value !== '' ? value : null
}
