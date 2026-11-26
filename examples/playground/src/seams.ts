import { useState } from 'react'
import {
  createBlockCatalogueSource,
  createDraftAnalysisSource,
  createWorkflowDocumentSource,
  createWorkflowOptionsSource,
  createWorkflowReferenceSource,
  readPortTypeConstructors,
  readPortTypeVocabulary,
} from '@reeve-iot/workflow-adapters'
import type {
  BlockCatalogueSource,
  WorkflowDocumentSource,
  WorkflowOptionsSource,
  WorkflowReferenceSource,
} from '@reeve-iot/workflow-adapters'
import { createPortTypeRegistry } from '@reeve-iot/workflow-core'
import type { DraftAnalysisSource, PortTypeDefinition, PortTypeRegistry } from '@reeve-iot/workflow-core'
import { DISPATCH_PORT_TYPES } from '@reeve-iot/workflow-example-dispatch'
import { createWorkflowClient, createWorkflowEditorClient } from '@reeve-iot/workflow-client'
import type { WorkflowClient, WorkflowClientOptions, WorkflowEditorClient } from '@reeve-iot/workflow-client'
import { OPERATOR_HEADER, apiBaseUrl, operatorId } from './host.js'

export interface Seams {
  readonly reads: WorkflowClient
  readonly writes: WorkflowEditorClient
  readonly catalogue: BlockCatalogueSource
  readonly document: WorkflowDocumentSource
  readonly workflowOptions: WorkflowOptionsSource
  readonly reference: WorkflowReferenceSource
  readonly analysis: DraftAnalysisSource
  readonly readVocabulary: (signal: AbortSignal) => Promise<PortTypeRegistry>
  readonly openWorkflow: { set(id: string | null): void }
}

export function clientOptions(): WorkflowClientOptions {
  return {
    baseUrl: apiBaseUrl(),
    headers: () => ({ [OPERATOR_HEADER]: operatorId() }),
  }
}

export function useSeams(): Seams {
  return useState<Seams>(() => {
    const options = clientOptions()
    const reads = createWorkflowClient(options)
    const writes = createWorkflowEditorClient(options)
    let openId: string | null = null
    return {
      reads,
      writes,
      catalogue: createBlockCatalogueSource(reads),
      document: createWorkflowDocumentSource(reads),
      workflowOptions: createWorkflowOptionsSource(reads),
      reference: createWorkflowReferenceSource(reads),
      analysis: createDraftAnalysisSource(writes, { workflowId: () => openId }),
      readVocabulary: async (signal) => {
        const response = await writes.getPortTypes({}, { signal })
        const adapt = (definitions: PortTypeDefinition[]) => definitions.map((type) => ({
          ...type, displayName: DISPATCH_PORT_TYPES.find((own) => own.name === type.name)?.displayName,
        }))
        return createPortTypeRegistry(adapt(readPortTypeVocabulary(response)), readPortTypeConstructors(response), { secretCodec: response.secretCodec })
      },
      openWorkflow: { set: (id) => { openId = id } },
    }
  })[0]
}
