// Generated from surface.editor.json by generateTypes(). Do not edit by hand.
// generated.test.ts writes editor.ts.actual beside this file when the two disagree.

import type { CanvasPathSegment, CompilationDiagnostic, WorkflowResponse } from './types.js'

export interface CompilationReportResponse {
  readonly failed: ReadonlyArray<CompilationVersionResponse>
  readonly invalid: ReadonlyArray<WorkflowCompilationResponse>
  readonly shared: ReadonlyArray<CompilationSharedVersion>
  readonly skip: number
  readonly take: number
  readonly totalCount: number
  readonly unsaved: ReadonlyArray<CompilationVersionResponse>
}

export interface CompilationSharedVersion {
  readonly workflowIds: ReadonlyArray<string>
  readonly workflowVersionId: string
}

export interface CompilationVersionResponse {
  readonly workflowId: string
  readonly workflowVersionId: string
}

export interface CreateWorkflowRequest {
  readonly checkpointPolicyJson: string | null
  readonly definitionJson: string | null
  readonly description: string | null
  readonly executionTimeoutSeconds: number | null
  readonly hostMetadata: Readonly<Record<string, string>> | null
  readonly importWarningsJson: string | null
  readonly inputParametersJson: string | null
  readonly maxConcurrentRuns: number | null
  readonly maxQueuedRuns: number | null
  readonly mode: string | null
  readonly name: string
  readonly origin: string | null
  readonly outputParametersJson: string | null
  readonly overflowBehavior: string | null
  readonly retryPolicyJson: string | null
}

export interface DraftAnalysisRequest {
  readonly definitionJson: string | null
  readonly inputParametersJson: string | null
  readonly outputParametersJson: string | null
  readonly revision: number
  readonly workflowId: string | null
}

export interface DraftAnalysisResponse {
  readonly diagnostics: ReadonlyArray<CompilationDiagnostic>
  readonly nodes: ReadonlyArray<NodeAnalysis>
  readonly registryGeneration: number
  readonly revision: number
}

export interface NodeAnalysis {
  readonly canvasPath: ReadonlyArray<CanvasPathSegment>
  readonly inputs: ReadonlyArray<PortAnalysis>
  readonly nodeId: string
  readonly outputs: ReadonlyArray<PortAnalysis>
  readonly typeArguments: Readonly<Record<string, string | null>>
}

export interface PortAnalysis {
  readonly declared: string | null
  readonly effective: string | null
  readonly expression: string | null
  readonly name: string
  readonly projection: string | null
  readonly state: string
  readonly unbound: ReadonlyArray<string>
}

export interface PortCapabilityDescriptor {
  readonly associated: ReadonlyArray<string> | null
  readonly name: string
}

export interface PortLiteralDescriptor {
  readonly format: string | null
  readonly gradedAs: string
}

export interface PortTypeConstructorDescriptor {
  readonly allowedArguments: ReadonlyArray<string> | null
  readonly arity: number
  readonly implements: ReadonlyArray<string>
  readonly key: string | null
  readonly literal: PortLiteralDescriptor | null
  readonly name: string
  readonly owner: string
  readonly variance: string
}

export interface PortTypeDescriptor {
  readonly aliases: ReadonlyArray<string>
  readonly implements: ReadonlyArray<string>
  readonly literal: PortLiteralDescriptor | null
  readonly name: string
  readonly owner: string
  readonly widensTo: ReadonlyArray<string>
}

export interface PortTypeVocabularyResponse {
  readonly capabilities: ReadonlyArray<PortCapabilityDescriptor>
  readonly constructors: ReadonlyArray<PortTypeConstructorDescriptor>
  readonly generation: number
  readonly secretCodec: boolean
  readonly types: ReadonlyArray<PortTypeDescriptor>
}

export interface UpdateWorkflowRequest {
  readonly autoPublish: boolean
  readonly changeMessage: string | null
  readonly checkpointPolicyJson: string | null
  readonly definitionJson: string | null
  readonly description: string | null
  readonly executionTimeoutSeconds: number | null
  readonly expectedRevision: number | null
  readonly hostMetadata: Readonly<Record<string, string>> | null
  readonly inputParametersJson: string | null
  readonly maxConcurrentRuns: number | null
  readonly maxQueuedRuns: number | null
  readonly mode: string | null
  readonly name: string
  readonly outputParametersJson: string | null
  readonly overflowBehavior: string | null
  readonly retryPolicyJson: string | null
}

export interface WorkflowCompilationResponse {
  readonly diagnostics: ReadonlyArray<CompilationDiagnostic>
  readonly isValid: boolean
  readonly workflowId: string
  readonly workflowVersionId: string
}

export interface WorkflowSaveUnpublishedProblemDetails {
  readonly detail: string
  readonly status: number
  readonly title: string
  readonly type: string
  readonly versionId: string
  readonly versionNumber: number
  readonly workflow: WorkflowResponse
}

export interface WorkflowEditorOperations {
  readonly createWorkflow: {
    readonly method: 'POST'
    readonly path: '/api/workflows'
    readonly query: never
    readonly args: {
      readonly body: CreateWorkflowRequest
    }
    readonly result: WorkflowResponse
  }
  readonly analyzeDraft: {
    readonly method: 'POST'
    readonly path: '/api/workflows/analysis'
    readonly query: never
    readonly args: {
      readonly body: DraftAnalysisRequest
    }
    readonly result: DraftAnalysisResponse
  }
  readonly getInvalidCompilations: {
    readonly method: 'GET'
    readonly path: '/api/workflows/compilation/invalid'
    readonly query: 'skip' | 'take'
    readonly args: {
      readonly skip?: number
      readonly take?: number
    }
    readonly result: CompilationReportResponse
  }
  readonly getPortTypes: {
    readonly method: 'GET'
    readonly path: '/api/workflows/port-types'
    readonly query: never
    readonly args: Record<never, never>
    readonly result: PortTypeVocabularyResponse
  }
  readonly deleteWorkflow: {
    readonly method: 'DELETE'
    readonly path: '/api/workflows/{id}'
    readonly query: never
    readonly args: {
      readonly id: string
    }
    readonly result: void
  }
  readonly updateWorkflow: {
    readonly method: 'PUT'
    readonly path: '/api/workflows/{id}'
    readonly query: never
    readonly args: {
      readonly id: string
      readonly body: UpdateWorkflowRequest
    }
    readonly result: WorkflowResponse
  }
  readonly publishWorkflow: {
    readonly method: 'POST'
    readonly path: '/api/workflows/{id}/publish'
    readonly query: never
    readonly args: {
      readonly id: string
    }
    readonly result: WorkflowResponse
  }
  readonly rollbackWorkflow: {
    readonly method: 'POST'
    readonly path: '/api/workflows/{id}/versions/{versionNumber}/rollback'
    readonly query: never
    readonly args: {
      readonly id: string
      readonly versionNumber: number
    }
    readonly result: WorkflowResponse
  }
  readonly getVersionCompilation: {
    readonly method: 'GET'
    readonly path: '/api/workflows/{versionId}/compilation'
    readonly query: never
    readonly args: {
      readonly versionId: string
    }
    readonly result: WorkflowCompilationResponse
  }
}

export const WORKFLOW_EDITOR_ROUTES = {
  createWorkflow: { method: 'POST', path: '/api/workflows', query: [], empty: false },
  analyzeDraft: { method: 'POST', path: '/api/workflows/analysis', query: [], empty: false },
  getInvalidCompilations: { method: 'GET', path: '/api/workflows/compilation/invalid', query: ['skip', 'take'], empty: false },
  getPortTypes: { method: 'GET', path: '/api/workflows/port-types', query: [], empty: false },
  deleteWorkflow: { method: 'DELETE', path: '/api/workflows/{id}', query: [], empty: true },
  updateWorkflow: { method: 'PUT', path: '/api/workflows/{id}', query: [], empty: false },
  publishWorkflow: { method: 'POST', path: '/api/workflows/{id}/publish', query: [], empty: false },
  rollbackWorkflow: { method: 'POST', path: '/api/workflows/{id}/versions/{versionNumber}/rollback', query: [], empty: false },
  getVersionCompilation: { method: 'GET', path: '/api/workflows/{versionId}/compilation', query: [], empty: false },
} as const satisfies Readonly<
  Record<keyof WorkflowEditorOperations, { method: string; path: string; query: readonly string[]; empty: boolean }>
>
