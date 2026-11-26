// Generated from surface.json by generateTypes(). Do not edit by hand.
// generated.test.ts writes types.ts.actual beside this file when the two disagree.

export interface BlockConfigFieldResponse {
  readonly allowedValues: ReadonlyArray<string> | null
  readonly defaultValue: string | null
  readonly description: string | null
  readonly label: string
  readonly name: string
  readonly required: boolean
  readonly type: string
}

export interface BlockPortResponse {
  readonly allowedValues: ReadonlyArray<string> | null
  readonly displayName: string
  readonly isRequired: boolean
  readonly name: string
  readonly scopeVariables: ReadonlyArray<BlockScopeVariableResponse>
  readonly secretValues: boolean
  readonly type: string
}

export interface BlockScopeVariableResponse {
  readonly name: string
  readonly type: string
}

export interface BlockTypeParameterResponse {
  readonly constraints: ReadonlyArray<string>
  readonly name: string
}

export interface CanvasPathSegment {
  readonly branchName: string
  readonly nodeId: string
}

export interface CompilationDiagnostic {
  readonly blockSource: string | null
  readonly canvasPath: ReadonlyArray<CanvasPathSegment>
  readonly message: string
  readonly nodeId: string | null
  readonly portName: string | null
  readonly ruleId: string
}

export interface CompilationProblemDetails {
  readonly code: string | null
  readonly detail: string
  readonly diagnostics: ReadonlyArray<CompilationDiagnostic>
  readonly status: number
  readonly title: string
  readonly type: string
}

export interface ExecuteWorkflowRequest {
  readonly debugMode: boolean
  readonly inputParameters: Readonly<Record<string, unknown>> | null
}

export interface ExpressionPreviewErrorDto {
  readonly message: string
  readonly position: number
  readonly ruleId: string | null
  readonly suggestion: string | null
}

export interface ExpressionPreviewRequest {
  readonly contextScope: string | null
  readonly expression: string
}

export interface ExpressionPreviewResponse {
  readonly elapsedMicroseconds: number
  readonly error: ExpressionPreviewErrorDto | null
  readonly ok: boolean
  readonly type: string | null
  readonly value: unknown
}

export interface PagedWorkflowExecutionsResponse {
  readonly items: ReadonlyArray<WorkflowExecutionResponse>
  readonly skip: number
  readonly take: number
  readonly totalCount: number
}

export interface ProblemDetails {
  readonly detail: string | null
  readonly instance: string | null
  readonly status: number | null
  readonly title: string | null
  readonly type: string | null
}

export interface WorkflowBlockTypeResponse {
  readonly blockTypeId: string
  readonly category: string
  readonly configurationSchema: ReadonlyArray<BlockConfigFieldResponse>
  readonly declaredOutputsCollect: boolean
  readonly description: string
  readonly displayName: string
  readonly hasDynamicPorts: boolean
  readonly inputs: ReadonlyArray<BlockPortResponse>
  readonly outputs: ReadonlyArray<BlockPortResponse>
  readonly runsWorkflowFrom: string | null
  readonly subCanvasInputs: Readonly<Record<string, ReadonlyArray<BlockPortResponse>>>
  readonly subCanvasNames: ReadonlyArray<string>
  readonly subCanvasOutputs: Readonly<Record<string, ReadonlyArray<BlockPortResponse>>>
  readonly supportsDeclaredOutputs: boolean
  readonly typeParameters: ReadonlyArray<BlockTypeParameterResponse>
}

export interface WorkflowConcurrencyConflictResponse {
  readonly message: string
  readonly mode: string
  readonly outcome: string
  readonly workflowId: string
}

export interface WorkflowDiffCanvas {
  readonly canvasPath: ReadonlyArray<WorkflowDiffCanvasStep>
  readonly changedFields: ReadonlyArray<string>
}

export interface WorkflowDiffCanvasStep {
  readonly nodeId: string
  readonly subCanvas: string
}

export interface WorkflowDiffEdge {
  readonly canvasPath: ReadonlyArray<WorkflowDiffCanvasStep>
  readonly id: string
  readonly mappingsJson: string
  readonly sourceNodeId: string
  readonly targetNodeId: string
}

export interface WorkflowDiffModifiedNode {
  readonly canvasPath: ReadonlyArray<WorkflowDiffCanvasStep>
  readonly changedFields: ReadonlyArray<string>
  readonly configChanges: Readonly<Record<string, WorkflowDiffValueChange>>
  readonly errorPolicyChanged: boolean
  readonly id: string
  readonly retryPolicyChanged: boolean
}

export interface WorkflowDiffNode {
  readonly blockTypeId: string
  readonly canvasPath: ReadonlyArray<WorkflowDiffCanvasStep>
  readonly id: string
  readonly name: string
}

export interface WorkflowDiffParameters {
  readonly added: ReadonlyArray<string>
  readonly modified: ReadonlyArray<string>
  readonly removed: ReadonlyArray<string>
}

export interface WorkflowDiffResponse {
  readonly addedEdges: ReadonlyArray<WorkflowDiffEdge>
  readonly addedNodes: ReadonlyArray<WorkflowDiffNode>
  readonly modifiedCanvases: ReadonlyArray<WorkflowDiffCanvas>
  readonly modifiedNodes: ReadonlyArray<WorkflowDiffModifiedNode>
  readonly outputParameterChanges: WorkflowDiffParameters
  readonly parameterChanges: WorkflowDiffParameters
  readonly removedEdges: ReadonlyArray<WorkflowDiffEdge>
  readonly removedNodes: ReadonlyArray<WorkflowDiffNode>
}

export interface WorkflowDiffValueChange {
  readonly from: unknown
  readonly to: unknown
}

export interface WorkflowDroppedSecret {
  readonly canvasPath: string
  readonly edgeId: string | null
  readonly input: string
  readonly key: string
  readonly nodeId: string
  readonly reason: string
}

export interface WorkflowExecutionDetailResponse {
  readonly automationId: string | null
  readonly completedAtUtc: string | null
  readonly durationMs: number | null
  readonly entries: ReadonlyArray<WorkflowExecutionLogEntryResponse>
  readonly errorMessage: string | null
  readonly failureReason: string | null
  readonly id: string
  readonly outputsJson: string | null
  readonly startedAtUtc: string
  readonly status: string
  readonly steps: ReadonlyArray<WorkflowExecutionStepResponse>
  readonly triggerId: string | null
  readonly workflowId: string
}

export interface WorkflowExecutionLogEntryResponse {
  readonly blockTypeId: string
  readonly callStackJson: string | null
  readonly dataJson: string | null
  readonly id: string
  readonly iterationIndex: number | null
  readonly level: string
  readonly message: string
  readonly nodeId: string
  readonly timestamp: string
}

export interface WorkflowExecutionResponse {
  readonly automationId: string | null
  readonly completedAtUtc: string | null
  readonly durationMs: number | null
  readonly errorMessage: string | null
  readonly failureReason: string | null
  readonly id: string
  readonly startedAtUtc: string
  readonly status: string
  readonly triggerId: string | null
  readonly workflowId: string
}

export interface WorkflowExecutionStepResponse {
  readonly durationMs: number
  readonly endedAt: string
  readonly errorJson: string | null
  readonly errorMessage: string | null
  readonly errorStackTrace: string | null
  readonly id: string
  readonly inputsJson: string | null
  readonly nodeId: string
  readonly nodeType: string
  readonly outputsJson: string | null
  readonly sortOrder: number
  readonly startedAt: string
  readonly status: string
}

export interface WorkflowExportDto {
  readonly checkpointPolicyJson: string | null
  readonly definitionJson: string | null
  readonly description: string | null
  readonly executionTimeoutSeconds: number | null
  readonly exportedAtUtc: string
  readonly inputParametersJson: string | null
  readonly maxConcurrentRuns: number | null
  readonly maxQueuedRuns: number | null
  readonly mode: string | null
  readonly name: string
  readonly outputParametersJson: string | null
  readonly overflowBehavior: string | null
  readonly retryPolicyJson: string | null
  readonly schemaVersion: string
}

export interface WorkflowImportValidationResult {
  readonly definitionJson: string
  readonly droppedSecrets: ReadonlyArray<WorkflowDroppedSecret>
  readonly inputParametersJson: string | null
  readonly missingBlockTypeIds: ReadonlyArray<string>
  readonly missingWorkflowIds: ReadonlyArray<string>
  readonly outputParametersJson: string | null
  readonly referenceIdsByPortType: Readonly<Record<string, ReadonlyArray<string>>>
}

export interface WorkflowResponse {
  readonly checkpointPolicyJson: string | null
  readonly createdAtUtc: string
  readonly createdByUserId: string
  readonly currentVersionId: string | null
  readonly currentVersionNumber: number | null
  readonly definitionJson: string
  readonly description: string | null
  readonly documentsFrom: string | null
  readonly draftVersionId: string | null
  readonly draftVersionNumber: number | null
  readonly executionTimeoutSeconds: number | null
  readonly hasImportWarnings: boolean
  readonly hostMetadata: Readonly<Record<string, string>> | null
  readonly id: string
  readonly importWarningsJson: string | null
  readonly inputParametersJson: string | null
  readonly maxConcurrentRuns: number
  readonly maxQueuedRuns: number
  readonly mode: string
  readonly name: string
  readonly outputParametersJson: string | null
  readonly overflowBehavior: string
  readonly retryPolicyJson: string | null
  readonly revision: number
  readonly updatedAtUtc: string
}

export interface WorkflowSummaryResponse {
  readonly currentVersionNumber: number | null
  readonly description: string | null
  readonly executionCount: number
  readonly hasImportWarnings: boolean
  readonly hasInputParameters: boolean
  readonly hasOutputParameters: boolean
  readonly hasUnpublishedChanges: boolean
  readonly hostMetadata: Readonly<Record<string, string>> | null
  readonly id: string
  readonly importWarningsJson: string | null
  readonly lastExecutedAtUtc: string | null
  readonly lastExecutionStatus: string | null
  readonly name: string
  readonly updatedAtUtc: string
}

export interface WorkflowVersionResponse {
  readonly changeMessage: string | null
  readonly createdAtUtc: string
  readonly createdByUserId: string
  readonly definitionJson: string
  readonly id: string
  readonly inputParametersJson: string | null
  readonly isPublished: boolean
  readonly outputParametersJson: string | null
  readonly publishedAtUtc: string | null
  readonly versionNumber: number
  readonly workflowId: string
}

export interface WorkflowVersionSummaryResponse {
  readonly changeMessage: string | null
  readonly createdAtUtc: string
  readonly createdByUserId: string
  readonly id: string
  readonly isPublished: boolean
  readonly publishedAtUtc: string | null
  readonly versionNumber: number
}

export interface WorkflowOperations {
  readonly previewExpression: {
    readonly method: 'POST'
    readonly path: '/api/expressions/preview'
    readonly query: never
    readonly args: {
      readonly body: ExpressionPreviewRequest
    }
    readonly result: ExpressionPreviewResponse
  }
  readonly getBlockTypes: {
    readonly method: 'GET'
    readonly path: '/api/workflow-blocks'
    readonly query: never
    readonly args: Record<never, never>
    readonly result: ReadonlyArray<WorkflowBlockTypeResponse>
  }
  readonly getWorkflows: {
    readonly method: 'GET'
    readonly path: '/api/workflows'
    readonly query: never
    readonly args: Record<never, never>
    readonly result: ReadonlyArray<WorkflowSummaryResponse>
  }
  readonly validateImport: {
    readonly method: 'POST'
    readonly path: '/api/workflows/import/validate'
    readonly query: never
    readonly args: {
      readonly body: WorkflowExportDto
    }
    readonly result: WorkflowImportValidationResult
  }
  readonly getWorkflow: {
    readonly method: 'GET'
    readonly path: '/api/workflows/{id}'
    readonly query: never
    readonly args: {
      readonly id: string
    }
    readonly result: WorkflowResponse
  }
  readonly executeWorkflow: {
    readonly method: 'POST'
    readonly path: '/api/workflows/{id}/execute'
    readonly query: never
    readonly args: {
      readonly id: string
      readonly body?: ExecuteWorkflowRequest
    }
    readonly result: Readonly<Record<string, unknown>>
  }
  readonly getExecutions: {
    readonly method: 'GET'
    readonly path: '/api/workflows/{id}/executions'
    readonly query: 'hasErrors' | 'skip' | 'status' | 'take'
    readonly args: {
      readonly id: string
      readonly hasErrors?: boolean | null
      readonly skip?: number
      readonly status?: string | null
      readonly take?: number
    }
    readonly result: PagedWorkflowExecutionsResponse
  }
  readonly getExecution: {
    readonly method: 'GET'
    readonly path: '/api/workflows/{id}/executions/{executionId}'
    readonly query: never
    readonly args: {
      readonly executionId: string
      readonly id: string
    }
    readonly result: WorkflowExecutionDetailResponse
  }
  readonly getExecutionReplay: {
    readonly method: 'GET'
    readonly path: '/api/workflows/{id}/executions/{executionId}/replay'
    readonly query: never
    readonly args: {
      readonly executionId: string
      readonly id: string
    }
    readonly result: ReadonlyArray<WorkflowExecutionStepResponse>
  }
  readonly getVersions: {
    readonly method: 'GET'
    readonly path: '/api/workflows/{id}/versions'
    readonly query: 'page' | 'pageSize'
    readonly args: {
      readonly id: string
      readonly page?: number
      readonly pageSize?: number
    }
    readonly result: ReadonlyArray<WorkflowVersionSummaryResponse>
  }
  readonly diffVersions: {
    readonly method: 'GET'
    readonly path: '/api/workflows/{id}/versions/{versionA}/diff/{versionB}'
    readonly query: never
    readonly args: {
      readonly id: string
      readonly versionA: number
      readonly versionB: number
    }
    readonly result: WorkflowDiffResponse
  }
  readonly getVersion: {
    readonly method: 'GET'
    readonly path: '/api/workflows/{id}/versions/{versionNumber}'
    readonly query: never
    readonly args: {
      readonly id: string
      readonly versionNumber: number
    }
    readonly result: WorkflowVersionResponse
  }
}

export const WORKFLOW_ROUTES = {
  previewExpression: { method: 'POST', path: '/api/expressions/preview', query: [], empty: false },
  getBlockTypes: { method: 'GET', path: '/api/workflow-blocks', query: [], empty: false },
  getWorkflows: { method: 'GET', path: '/api/workflows', query: [], empty: false },
  validateImport: { method: 'POST', path: '/api/workflows/import/validate', query: [], empty: false },
  getWorkflow: { method: 'GET', path: '/api/workflows/{id}', query: [], empty: false },
  executeWorkflow: { method: 'POST', path: '/api/workflows/{id}/execute', query: [], empty: false },
  getExecutions: { method: 'GET', path: '/api/workflows/{id}/executions', query: ['hasErrors', 'skip', 'status', 'take'], empty: false },
  getExecution: { method: 'GET', path: '/api/workflows/{id}/executions/{executionId}', query: [], empty: false },
  getExecutionReplay: { method: 'GET', path: '/api/workflows/{id}/executions/{executionId}/replay', query: [], empty: false },
  getVersions: { method: 'GET', path: '/api/workflows/{id}/versions', query: ['page', 'pageSize'], empty: false },
  diffVersions: { method: 'GET', path: '/api/workflows/{id}/versions/{versionA}/diff/{versionB}', query: [], empty: false },
  getVersion: { method: 'GET', path: '/api/workflows/{id}/versions/{versionNumber}', query: [], empty: false },
} as const satisfies Readonly<
  Record<keyof WorkflowOperations, { method: string; path: string; query: readonly string[]; empty: boolean }>
>
