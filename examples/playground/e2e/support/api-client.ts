import { createWorkflowClient } from '@reeve-iot/workflow-client'
import type { WorkflowClient } from '@reeve-iot/workflow-client'
import { OPERATOR_HEADER } from '../../src/host'
import { readHarnessState } from './state'

export type ReadOnlyApi = Pick<
  WorkflowClient,
  | 'getBlockTypes'
  | 'getWorkflows'
  | 'getWorkflow'
  | 'getVersions'
  | 'getVersion'
  | 'getExecutions'
  | 'getExecution'
  | 'executeWorkflow'
  | 'validateImport'
>

export function readOnlyApi(): ReadOnlyApi {
  const state = readHarnessState()
  const origin = new URL(state.baseUrl)
  if (origin.pathname !== '/' || origin.search !== '' || origin.hash !== '') {
    throw new Error(`The harness base URL is its origin alone, and '${state.baseUrl}' carries more than that.`)
  }
  const client = createWorkflowClient({ baseUrl: state.baseUrl, headers: { [OPERATOR_HEADER]: state.operatorId } })
  return {
    getBlockTypes: (args) => client.getBlockTypes(args),
    getWorkflows: (args) => client.getWorkflows(args),
    getWorkflow: (args) => client.getWorkflow(args),
    getVersions: (args) => client.getVersions(args),
    getVersion: (args) => client.getVersion(args),
    getExecutions: (args) => client.getExecutions(args),
    getExecution: (args) => client.getExecution(args),
    executeWorkflow: (args) => client.executeWorkflow(args),
    validateImport: (args) => client.validateImport(args),
  }
}

export async function healthy(baseUrl: string, timeoutMs: number): Promise<boolean> {
  const response = await fetch(`${baseUrl}/health`, { method: 'GET', signal: AbortSignal.timeout(timeoutMs) })
  return response.ok
}
