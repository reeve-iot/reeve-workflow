import { useEffect, useMemo, useState } from 'react'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import {
  createBlockCatalogueSource,
  createWorkflowDocumentSource,
} from '@reeve-iot/workflow-adapters'
import { createWorkflowClient } from '@reeve-iot/workflow-client'
import type { WorkflowClientOptions } from '@reeve-iot/workflow-client'
import {
  BUILTIN_PORT_TYPES,
  BlockPalette,
  NodeConfigPanel,
  NodePortCustomizationContext,
  WorkflowCanvas,
  WorkflowEditorStoreProvider,
  createPortTypeRegistry,
  useWorkflowEditorStore,
} from '@reeve-iot/workflow-mui'
import type { NodePortCustomization, PortInfo, WorkflowNode } from '@reeve-iot/workflow-mui'
import { DEPOTS, DISPATCH_PORT_TYPES, bondedDepotRule } from './domain.js'

export interface DispatchEditorProps extends WorkflowClientOptions {
  workflowId?: string | null
}

function useDispatchSeams(options: WorkflowClientOptions) {
  return useState(() => {
    const client = createWorkflowClient(options)
    return {
      catalogue: createBlockCatalogueSource(client),
      document: createWorkflowDocumentSource(client),
    }
  })[0]
}

type Seams = ReturnType<typeof useDispatchSeams>

const dispatchPorts: NodePortCustomization = {
  overrideInputPortType: (node: WorkflowNode, port: PortInfo) =>
    node.blockTypeId === 'Dispatch' && port.name === 'Freight' && node.configuration?.Consolidated === true
      ? 'Collection<Shipment>'
      : null,
  isInputPortHidden: (node: WorkflowNode, portName: string) =>
    node.blockTypeId === 'Dispatch' && portName === 'Depot',
}

function EditorBody({ seams, workflowId }: { seams: Seams; workflowId: string | null }) {
  const blockTypes = seams.catalogue.useBlockTypes()
  const workflow = seams.document.useWorkflow(workflowId)

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
      <BlockPalette />
      <WorkflowCanvas onCanvasClick={() => { setSelectedNode(null) }} />
      <NodeConfigPanel />
      <DepotBoard />
      <DispatchWarnings />
    </div>
  )
}

function DepotBoard() {
  const selectedNodeId = useWorkflowEditorStore((s) => s.selectedNodeId)
  const nodes = useWorkflowEditorStore((s) => s.nodes)
  const patchNodeConfig = useWorkflowEditorStore((s) => s.patchNodeConfig)
  const selected = nodes.find((n) => n.id === selectedNodeId)

  if (selected === undefined || selected.blockTypeId !== 'Dispatch') return null

  return (
    <fieldset>
      <legend>Depot</legend>
      {DEPOTS.map((depot) => (
        <button
          key={depot.code}
          type="button"
          aria-pressed={selected.configuration?.Depot === depot.code}
          onClick={() => { patchNodeConfig(selected.id, { Depot: depot.code }) }}
        >
          {depot.name}
        </button>
      ))}
    </fieldset>
  )
}

function DispatchWarnings() {
  useWorkflowEditorStore((s) => s.nodes)
  const warnings = useWorkflowEditorStore((s) => s.validateWorkflowWarnings)()

  if (warnings.length === 0) return null

  return (
    <ul aria-label="Dispatch warnings">
      {warnings.map((w, index) => <li key={`${w.code ?? w.ruleId ?? 'warning'}:${index}`}>{w.message}</li>)}
    </ul>
  )
}

export function DispatchEditor({ workflowId = null, ...clientOptions }: DispatchEditorProps) {
  const seams = useDispatchSeams(clientOptions)
  const [registry] = useState(() => createPortTypeRegistry([...BUILTIN_PORT_TYPES, ...DISPATCH_PORT_TYPES]))
  const [theme] = useState(() => createTheme())
  const warningRules = useMemo(() => [bondedDepotRule], [])

  return (
    <ThemeProvider theme={theme}>
      <WorkflowEditorStoreProvider options={{ portTypes: registry, nodeWarningRules: warningRules }}>
        <NodePortCustomizationContext.Provider value={dispatchPorts}>
          <EditorBody seams={seams} workflowId={workflowId} />
        </NodePortCustomizationContext.Provider>
      </WorkflowEditorStoreProvider>
    </ThemeProvider>
  )
}
