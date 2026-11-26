import { useEffect, useState } from 'react'
import { Alert } from '@mui/material'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import {
  createBlockCatalogueSource,
  createWorkflowDocumentSource,
  createWorkflowOptionsSource,
  createWorkflowReferenceSource,
} from '@reeve-iot/workflow-adapters'
import { createWorkflowClient } from '@reeve-iot/workflow-client'
import type { WorkflowClientOptions } from '@reeve-iot/workflow-client'
import {
  BUILTIN_PORT_TYPES,
  BlockPalette,
  NodeConfigPanel,
  WorkflowCanvas,
  WorkflowEditorStoreProvider,
  WorkflowReferenceResolverContext,
  createPortTypeRegistry,
  useWorkflowEditorStore,
} from '@reeve-iot/workflow-mui'
import type { BlockTypeDescriptor, PortTypeDefinition } from '@reeve-iot/workflow-mui'

export interface AdopterEditorProps extends WorkflowClientOptions {
  workflowId?: string | null
  portTypes?: readonly PortTypeDefinition[]
  adaptBlockType?: (blockType: BlockTypeDescriptor) => BlockTypeDescriptor
}

function useAdopterSeams(options: WorkflowClientOptions) {
  return useState(() => {
    const client = createWorkflowClient(options)
    return {
      catalogue: createBlockCatalogueSource(client),
      document: createWorkflowDocumentSource(client),
      options: createWorkflowOptionsSource(client),
      reference: createWorkflowReferenceSource(client),
    }
  })[0]
}

type Seams = ReturnType<typeof useAdopterSeams>

function EditorBody({ seams, workflowId }: { seams: Seams; workflowId: string | null }) {
  const blockTypes = seams.catalogue.useBlockTypes()
  const workflow = seams.document.useWorkflow(workflowId)
  const workflows = seams.options.useWorkflowOptions()

  const setBlockTypes = useWorkflowEditorStore((s) => s.setBlockTypes)
  const loadStoredWorkflow = useWorkflowEditorStore((s) => s.loadStoredWorkflow)
  const setWorkflowParams = useWorkflowEditorStore((s) => s.setWorkflowParams)
  const syncSpecialNodes = useWorkflowEditorStore((s) => s.syncSpecialNodes)
  const setSelectedNode = useWorkflowEditorStore((s) => s.setSelectedNode)

  useEffect(() => { setBlockTypes([...blockTypes]) }, [blockTypes, setBlockTypes])

  useEffect(() => {
    if (workflow === null) return
    setWorkflowParams(workflow.inputParams, workflow.outputParams)
    loadStoredWorkflow(workflow.definition)
    syncSpecialNodes()
  }, [workflow, loadStoredWorkflow, setWorkflowParams, syncSpecialNodes])

  return (
    <div>
      <label htmlFor="workflow-picker">Workflow</label>
      <select id="workflow-picker" defaultValue={workflowId ?? ''}>
        <option value="">—</option>
        {workflows.map((w) => (
          <option key={w.id} value={w.id}>{w.name}</option>
        ))}
      </select>
      <BlockPalette />
      <WorkflowCanvas onCanvasClick={() => { setSelectedNode(null) }} />
      <NodeConfigPanel />
    </div>
  )
}

export function AdopterEditor({ workflowId = null, portTypes, adaptBlockType, ...clientOptions }: AdopterEditorProps) {
  const seams = useAdopterSeams(clientOptions)
  const [registry] = useState(() => createPortTypeRegistry([...BUILTIN_PORT_TYPES, ...(portTypes ?? [])]))
  const [theme] = useState(() => createTheme())
  const [blockTypeError, setBlockTypeError] = useState<unknown>(null)

  return (
    <ThemeProvider theme={theme}>
      <WorkflowEditorStoreProvider options={{ portTypes: registry, adaptBlockType, onBlockTypeError: setBlockTypeError }}>
        {blockTypeError !== null && <Alert severity="error">{String(blockTypeError)}</Alert>}
        <WorkflowReferenceResolverContext.Provider value={seams.reference.resolver}>
          <EditorBody seams={seams} workflowId={workflowId} />
        </WorkflowReferenceResolverContext.Provider>
      </WorkflowEditorStoreProvider>
    </ThemeProvider>
  )
}
