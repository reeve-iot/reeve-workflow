import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from 'react'
import { Alert, Box, Button, Divider, Stack, Typography } from '@mui/material'
import { WorkflowApiError } from '@reeve-iot/workflow-client'
import type {
  CompilationDiagnostic, WorkflowResponse, WorkflowSummaryResponse, WorkflowVersionSummaryResponse,
} from '@reeve-iot/workflow-client'
import type { StoredDocumentFault, WorkflowConcurrencyMode, WorkflowOverflowBehavior } from '@reeve-iot/workflow-mui'
import {
  BlockPalette, MappingDialog, NodeConfigPanel, SubCanvasBreadcrumb, WorkflowCanvas, usePortTypes, useWorkflowEditorStore,
  useWorkflowEditorStoreApi,
} from '@reeve-iot/workflow-mui'
import { createReadCache } from '@reeve-iot/workflow-adapters'
import type { Seams } from './seams.js'
import { CreateWorkflowDialog } from './CreateWorkflowDialog.js'
import { FindingsList } from './FindingsList.js'
import { RunPanel } from './RunPanel.js'
import type { RunResult } from './RunPanel.js'
import { RunInputsDialog } from './RunInputsDialog.js'
import { SettingsDialog } from './SettingsDialog.js'
import type { WorkflowSettings } from './SettingsDialog.js'
import { VersionList } from './VersionList.js'
import { WorkflowList } from './WorkflowList.js'
import { explain } from './explainError.js'
import { portValueEditorProps } from './hostSlots.js'
import { readExport } from './readExport.js'

interface Status {
  readonly kind: 'info' | 'error'
  readonly text: string
}

const EMPTY_WORKFLOWS: readonly WorkflowSummaryResponse[] = []

const EMPTY_SETTINGS: WorkflowSettings = {
  name: '',
  description: null,
  executionTimeoutSeconds: null,
  retryPolicyJson: null,
  checkpointPolicyJson: null,
  concurrency: { mode: 'Single', maxConcurrentRuns: 1, maxQueuedRuns: 10, overflowBehavior: 'Drop' },
}

const CONCURRENCY_MODES: readonly WorkflowConcurrencyMode[] = ['Single', 'Restart', 'Queued', 'Parallel']
const OVERFLOW_BEHAVIORS: readonly WorkflowOverflowBehavior[] = ['Drop', 'Log', 'Error']

function declared<T extends string>(members: readonly T[], value: string, what: string): T {
  const found = members.find((member) => member === value)
  if (found === undefined) throw new Error(`The server answered a ${what} this build does not know: '${value}'.`)
  return found
}

function settingsOf(workflow: WorkflowResponse): WorkflowSettings {
  return {
    name: workflow.name,
    description: workflow.description,
    executionTimeoutSeconds: workflow.executionTimeoutSeconds,
    retryPolicyJson: workflow.retryPolicyJson,
    checkpointPolicyJson: workflow.checkpointPolicyJson,
    concurrency: {
      mode: declared(CONCURRENCY_MODES, workflow.mode, 'concurrency mode'),
      maxConcurrentRuns: workflow.maxConcurrentRuns,
      maxQueuedRuns: workflow.maxQueuedRuns,
      overflowBehavior: declared(OVERFLOW_BEHAVIORS, workflow.overflowBehavior, 'overflow behavior'),
    },
  }
}

function diagnosticsFrom(error: unknown): readonly CompilationDiagnostic[] {
  if (!(error instanceof WorkflowApiError) || typeof error.body !== 'object' || error.body === null) return []
  if (!('diagnostics' in error.body) || !Array.isArray(error.body.diagnostics)) return []
  return error.body.diagnostics.filter(isCompilationDiagnostic)
}

function isCompilationDiagnostic(value: unknown): value is CompilationDiagnostic {
  if (typeof value !== 'object' || value === null) return false
  if (!('blockSource' in value) || (value.blockSource !== null && typeof value.blockSource !== 'string')) return false
  if (!('canvasPath' in value) || !Array.isArray(value.canvasPath)) return false
  if (!value.canvasPath.every((segment: unknown) =>
    typeof segment === 'object' && segment !== null && 'branchName' in segment && typeof segment.branchName === 'string'
      && 'nodeId' in segment && typeof segment.nodeId === 'string')) return false
  return 'message' in value && typeof value.message === 'string'
    && 'nodeId' in value && (value.nodeId === null || typeof value.nodeId === 'string')
    && 'portName' in value && (value.portName === null || typeof value.portName === 'string')
    && 'ruleId' in value && typeof value.ruleId === 'string'
}

function WorkflowWorkspace({
  seams, selectedId, status, onStatus, onListChanged, onNew,
}: {
  seams: Seams
  selectedId: string | null
  status: Status | null
  onStatus: (status: Status | null) => void
  onListChanged: () => void
  onNew: () => void
}) {
  const loaded = seams.document.useWorkflow(selectedId)
  const blockTypes = seams.catalogue.useBlockTypes()
  const portTypes = usePortTypes()

  const [row, setRow] = useState<WorkflowResponse | null>(null)
  const [versions, setVersions] = useState<readonly WorkflowVersionSummaryResponse[]>([])
  const [settings, setSettings] = useState<WorkflowSettings>(EMPTY_SETTINGS)
  const [busy, setBusy] = useState(false)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [openedFault, setOpenedFault] = useState<StoredDocumentFault | null>(null)
  const [publishDiagnostics, setPublishDiagnostics] = useState<readonly CompilationDiagnostic[]>([])
  const [run, setRun] = useState<RunResult | null>(null)
  const [runInputsOpen, setRunInputsOpen] = useState(false)
  const [openedDocument, setOpenedDocument] = useState(false)

  const setBlockTypes = useWorkflowEditorStore((s) => s.setBlockTypes)
  const storeApi = useWorkflowEditorStoreApi()
  const loadStoredWorkflow = useWorkflowEditorStore((s) => s.loadStoredWorkflow)
  const setWorkflowParams = useWorkflowEditorStore((s) => s.setWorkflowParams)
  const setSelectedNode = useWorkflowEditorStore((s) => s.setSelectedNode)
  const getWorkflowDefinition = useWorkflowEditorStore((s) => s.getWorkflowDefinition)
  const markSaved = useWorkflowEditorStore((s) => s.markSaved)
  const isDirty = useWorkflowEditorStore((s) => s.isDirty)
  const inputParams = useWorkflowEditorStore((s) => s.workflowInputParams)
  const savedInputParams = loaded?.inputParams ?? []
  const outputParams = useWorkflowEditorStore((s) => s.workflowOutputParams)
  const selectedEdgeId = useWorkflowEditorStore((s) => s.selectedEdgeId)
  const analysisError = useWorkflowEditorStore((s) => s.analysisError)
  const setSelectedEdge = useWorkflowEditorStore((s) => s.setSelectedEdge)

  const settingsDirty = row !== null && JSON.stringify(settings) !== JSON.stringify(settingsOf(row))
  const parametersDirty = loaded !== null && (JSON.stringify(inputParams) !== JSON.stringify(savedInputParams)
    || JSON.stringify(outputParams) !== JSON.stringify(loaded.outputParams))
  const hasUnsavedChanges = isDirty || settingsDirty || parametersDirty || openedDocument

  const report = useCallback((error: unknown, what: string) => {
    onStatus({ kind: 'error', text: `${what}: ${explain(error)}` })
  }, [onStatus])

  useEffect(() => { setBlockTypes([...blockTypes]) }, [blockTypes, setBlockTypes])

  useEffect(() => {
    seams.openWorkflow.set(selectedId)
    return () => { seams.openWorkflow.set(null) }
  }, [seams, selectedId])

  useEffect(() => storeApi.subscribe((state, previous) => {
    if (!previous.isDirty && state.isDirty) setPublishDiagnostics([])
  }), [storeApi])

  useEffect(() => {
    if (loaded === null) return
    setWorkflowParams(loaded.inputParams, loaded.outputParams)
    loadStoredWorkflow(loaded.definition)
  }, [loaded, loadStoredWorkflow, setWorkflowParams])

  useEffect(() => {
    if (selectedId === null) return
    let live = true
    void (async () => {
      try {
        const [workflow, versionList] = await Promise.all([
          seams.reads.getWorkflow({ id: selectedId }),
          seams.reads.getVersions({ id: selectedId }),
        ])
        if (!live) return
        const next = settingsOf(workflow)
        setRow(workflow)
        setSettings(next)
        setVersions(versionList)
      } catch (error) {
        if (live) report(error, 'Loading the workflow')
      }
    })()
    return () => { live = false }
  }, [selectedId, seams, report])

  const showRow = useCallback((response: WorkflowResponse) => {
    const next = settingsOf(response)
    setRow(response)
    setSettings(next)
    seams.document.refresh()
    seams.reference.refresh()
    onListChanged()
    void seams.reads.getVersions({ id: response.id }).then(setVersions, (error: unknown) => {
      report(error, 'Loading the versions')
    })
  }, [seams, onListChanged, report])

  const save = useCallback(async (autoPublish: boolean): Promise<WorkflowResponse | null> => {
    if (selectedId === null) return null
    try {
      const response = await seams.writes.updateWorkflow({
        id: selectedId,
        body: {
          name: settings.name,
          description: settings.description,
          definitionJson: JSON.stringify(getWorkflowDefinition()),
          inputParametersJson: JSON.stringify(inputParams),
          outputParametersJson: JSON.stringify(outputParams),
          hostMetadata: row?.hostMetadata ?? null,
          executionTimeoutSeconds: settings.executionTimeoutSeconds,
          expectedRevision: row?.revision ?? null,
          retryPolicyJson: settings.retryPolicyJson,
          checkpointPolicyJson: settings.checkpointPolicyJson,
          changeMessage: null,
          autoPublish,
          mode: settings.concurrency.mode,
          maxConcurrentRuns: settings.concurrency.maxConcurrentRuns,
          maxQueuedRuns: settings.concurrency.maxQueuedRuns,
          overflowBehavior: settings.concurrency.overflowBehavior,
        },
      })
      markSaved()
      setOpenedDocument(false)
      showRow(response)
      return response
    } catch (error) {
      report(error, autoPublish ? 'Saving and publishing' : 'Saving')
      return null
    }
  }, [
    selectedId, seams, settings, row, getWorkflowDefinition, inputParams, outputParams, markSaved, showRow, report,
  ])

  const publish = useCallback(async () => {
    if (selectedId === null) return
    setPublishDiagnostics([])
    setBusy(true)
    try {
      if (hasUnsavedChanges) {
        const saved = await save(false)
        if (saved === null) return
        if (saved.draftVersionId === null) {
          onStatus({ kind: 'info', text: 'Saved; the stored version is already current, nothing to publish.' })
          return
        }
      } else if (row?.draftVersionId === null) {
        onStatus({ kind: 'info', text: 'Nothing to publish.' })
        return
      }
      const response = await seams.writes.publishWorkflow({ id: selectedId })
      showRow(response)
      onStatus({ kind: 'info', text: `Published version ${String(response.currentVersionNumber ?? 0)}.` })
    } catch (error) {
      setPublishDiagnostics(diagnosticsFrom(error))
      report(error, 'Publishing')
    } finally {
      setBusy(false)
    }
  }, [selectedId, seams, hasUnsavedChanges, row, save, showRow, report, onStatus])

  const runWorkflow = useCallback(async (inputParameters: Record<string, unknown> | null = null) => {
    if (selectedId === null) return
    setBusy(true)
    try {
      const outputs = await seams.reads.executeWorkflow({
        id: selectedId,
        body: { debugMode: false, inputParameters },
      })
      const page = await seams.reads.getExecutions({ id: selectedId, take: 1 })
      const newest = page.items[0]
      const detail = newest === undefined
        ? null
        : await seams.reads.getExecution({ id: selectedId, executionId: newest.id })
      setRun({
        outputs,
        executionStatus: detail?.status ?? newest?.status ?? null,
        steps: detail?.steps ?? [],
      })
      onStatus({ kind: 'info', text: `Ran the workflow: ${detail?.status ?? 'no execution recorded'}.` })
    } catch (error) {
      report(error, 'Running')
    } finally {
      setBusy(false)
    }
  }, [selectedId, seams, report, onStatus])

  const rollback = useCallback(async (versionNumber: number) => {
    if (selectedId === null) return
    setBusy(true)
    try {
      showRow(await seams.writes.rollbackWorkflow({ id: selectedId, versionNumber }))
      onStatus({ kind: 'info', text: `Rolled back to version ${String(versionNumber)}.` })
    } catch (error) {
      report(error, 'Rolling back')
    } finally {
      setBusy(false)
    }
  }, [selectedId, seams, showRow, report, onStatus])

  const openFile = useCallback(async (file: File) => {
    setBusy(true)
    try {
      const read = readExport(await file.text(), portTypes)
      if (!read.ok) {
        onStatus({ kind: 'error', text: read.error })
        return
      }
      const { opened } = read
      setWorkflowParams([...opened.inputParams], [...opened.outputParams])
      loadStoredWorkflow(opened.definition)
      setOpenedDocument(true)
      setOpenedFault(opened.definition.ok ? opened.fault : null)
      if (opened.name !== '') setSettings((current) => ({ ...current, name: opened.name }))
      setPublishDiagnostics([])
      try {
        const validation = await seams.reads.validateImport({ body: opened.dto })
        const unresolved = Object.keys(validation.referenceIdsByPortType)
        onStatus({
          kind: 'info',
          text: `Opened '${opened.name}': the server reports ${String(validation.missingBlockTypeIds.length)} missing`
            + ` block type(s), ${String(validation.missingWorkflowIds.length)} missing workflow(s) and unresolved references`
            + ` for ${String(unresolved.length)} port type(s). Nothing was stored.`,
        })
      } catch (error) {
        setPublishDiagnostics(!opened.definition.ok || opened.fault !== null ? [] : diagnosticsFrom(error))
        onStatus({ kind: 'error', text: `Opened '${opened.name}'; import/validate: ${explain(error)}` })
      }
    } catch (error) {
      report(error, 'Opening the file')
    } finally {
      setBusy(false)
    }
  }, [seams, portTypes, setWorkflowParams, loadStoredWorkflow, onStatus, report])

  const open = selectedId !== null

  return (
    <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', p: 1, borderBottom: 1, borderColor: 'divider' }}>
        <Typography variant="h6" sx={{ mr: 1 }}>Reeve playground</Typography>
        <Button variant="contained" disabled={busy} onClick={onNew}>New workflow</Button>
        <Button disabled={busy || !open || row === null} onClick={() => { void save(false) }}>Save</Button>
        <Button variant="contained" disabled={busy || !open || row === null} onClick={() => { void publish() }}>
          Publish
        </Button>
        <Button disabled={busy || !open} onClick={() => {
          if (savedInputParams.length === 0) void runWorkflow()
          else setRunInputsOpen(true)
        }}>Run</Button>
        <Button disabled={busy || row === null} onClick={() => { setSettingsOpen(true) }}>Settings</Button>
        <Button component="label" disabled={busy}>
          Open file
          <input
            type="file" hidden accept="application/json,.json"
            aria-label="Open file"
            data-testid="open-file-input"
            onChange={(event) => {
              const picked = event.target.files?.[0]
              event.target.value = ''
              if (picked !== undefined) void openFile(picked)
            }}
          />
        </Button>
        <Box sx={{ flex: 1 }} />
        <Typography variant="body2" data-testid="dirty" data-dirty={hasUnsavedChanges ? 'true' : 'false'}>
          {hasUnsavedChanges ? 'Unsaved changes' : 'Saved'}
        </Typography>
      </Stack>

      {status !== null && (
        <Alert severity={status.kind} data-testid="status" onClose={() => { onStatus(null) }}>{status.text}</Alert>
      )}
      {analysisError !== null && (
        <Alert severity="warning" data-testid="analysis-error">The server could not analyse the draft: {explain(analysisError)}</Alert>
      )}

      <Box sx={{ display: 'flex', flex: 1, minHeight: 0 }}>
        <BlockPalette />
        <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
          <SubCanvasBreadcrumb />
          <Box sx={{ flex: 1, minHeight: 0 }}>
            <WorkflowCanvas onCanvasClick={() => { setSelectedNode(null) }} />
          </Box>
        </Box>
        <NodeConfigPanel portValueEditor={portValueEditorProps()} />
      </Box>
      {selectedEdgeId !== null && <MappingDialog edgeId={selectedEdgeId} onClose={() => { setSelectedEdge(null) }} />}
      {runInputsOpen && (
        <RunInputsDialog
          open
          parameters={savedInputParams}
          onCancel={() => { setRunInputsOpen(false) }}
          onRun={(values) => { setRunInputsOpen(false); void runWorkflow(values) }}
        />
      )}

      <Divider />
      <Box sx={{ display: 'flex', height: 220, minHeight: 0 }}>
        <Box sx={{ flex: 3, minWidth: 0, borderRight: 1, borderColor: 'divider' }}>
          <FindingsList openedFault={openedFault} publishDiagnostics={publishDiagnostics} />
        </Box>
        <Box sx={{ flex: 2, minWidth: 0, borderRight: 1, borderColor: 'divider' }}>
          <VersionList
            versions={versions}
            currentVersionNumber={row?.currentVersionNumber ?? null}
            busy={busy}
            onRollback={(n) => { void rollback(n) }}
          />
        </Box>
        <Box sx={{ flex: 3, minWidth: 0 }}>
          <RunPanel result={run} />
        </Box>
      </Box>

      <SettingsDialog
        open={settingsOpen}
        settings={settings}
        onChange={(next) => { setSettings(next); setPublishDiagnostics([]) }}
        onClose={() => { setSettingsOpen(false) }}
      />
    </Box>
  )
}

export function EditorShell({ seams }: { seams: Seams }) {
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState<Status | null>(null)

  const report = useCallback((error: unknown, what: string) => {
    setStatus({ kind: 'error', text: `${what}: ${explain(error)}` })
  }, [])

  const workflows = useMemo(
    () => createReadCache<readonly WorkflowSummaryResponse[]>(
      async () => seams.reads.getWorkflows(),
      EMPTY_WORKFLOWS,
      (_key, error: unknown) => { report(error, 'Loading the workflows') },
    ),
    [seams, report],
  )
  const list = useSyncExternalStore(workflows.subscribe, () => workflows.read('workflows'))
  const refreshWorkflows = useCallback(() => { workflows.invalidate() }, [workflows])

  const create = useCallback(async (name: string) => {
    setBusy(true)
    try {
      const response = await seams.writes.createWorkflow({
        body: {
          name,
          description: null,
          definitionJson: JSON.stringify({ nodes: [], edges: [] }),
          inputParametersJson: null,
          outputParametersJson: null,
          hostMetadata: null,
          executionTimeoutSeconds: EMPTY_SETTINGS.executionTimeoutSeconds,
          retryPolicyJson: EMPTY_SETTINGS.retryPolicyJson,
          checkpointPolicyJson: EMPTY_SETTINGS.checkpointPolicyJson,
          mode: null,
          maxConcurrentRuns: null,
          maxQueuedRuns: null,
          overflowBehavior: null,
          importWarningsJson: null,
          origin: null,
        },
      })
      setCreating(false)
      setSelectedId(response.id)
      refreshWorkflows()
      setStatus({ kind: 'info', text: `Created '${response.name}'.` })
    } catch (error) {
      report(error, 'Creating the workflow')
    } finally {
      setBusy(false)
    }
  }, [seams, refreshWorkflows, report])

  return (
    <Box sx={{ display: 'flex', height: '100vh', minHeight: 0 }}>
      <WorkflowList
        workflows={list}
        selectedId={selectedId}
        busy={busy}
        onSelect={setSelectedId}
        onRefresh={refreshWorkflows}
      />
      <WorkflowWorkspace
        key={selectedId ?? ''}
        seams={seams}
        selectedId={selectedId}
        status={status}
        onStatus={setStatus}
        onListChanged={refreshWorkflows}
        onNew={() => { setCreating(true) }}
      />
      <CreateWorkflowDialog
        open={creating}
        busy={busy}
        onCancel={() => { setCreating(false) }}
        onCreate={(name) => { void create(name) }}
      />
    </Box>
  )
}
