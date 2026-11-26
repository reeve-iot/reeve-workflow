import type { WorkflowConcurrency } from '@reeve-iot/workflow-mui'
import { ParameterDeclarationDialog, useWorkflowEditorStore } from '@reeve-iot/workflow-mui'

export interface WorkflowSettings {
  name: string
  description: string | null
  executionTimeoutSeconds: number | null
  retryPolicyJson: string | null
  checkpointPolicyJson: string | null
  concurrency: WorkflowConcurrency
}

export function SettingsDialog({
  open, settings, onChange, onClose,
}: {
  open: boolean
  settings: WorkflowSettings
  onChange: (settings: WorkflowSettings) => void
  onClose: () => void
}) {
  const inputParams = useWorkflowEditorStore((s) => s.workflowInputParams)
  const outputParams = useWorkflowEditorStore((s) => s.workflowOutputParams)
  const setWorkflowParams = useWorkflowEditorStore((s) => s.setWorkflowParams)

  return (
    <ParameterDeclarationDialog
      open={open}
      onClose={onClose}
      title="Workflow settings"
      inputParameters={inputParams}
      onInputParametersChange={(next) => { setWorkflowParams(next, outputParams) }}
      outputParameters={outputParams}
      onOutputParametersChange={(next) => { setWorkflowParams(inputParams, next) }}
      name={settings.name}
      onNameChange={(name) => { onChange({ ...settings, name }) }}
      description={settings.description}
      onDescriptionChange={(description) => { onChange({ ...settings, description }) }}
      concurrency={settings.concurrency}
      onConcurrencyChange={(concurrency) => { onChange({ ...settings, concurrency }) }}
      executionTimeoutSeconds={settings.executionTimeoutSeconds}
      onExecutionTimeoutSecondsChange={(executionTimeoutSeconds) => { onChange({ ...settings, executionTimeoutSeconds }) }}
      retryPolicyJson={settings.retryPolicyJson}
      onRetryPolicyJsonChange={(retryPolicyJson) => { onChange({ ...settings, retryPolicyJson }) }}
      checkpointPolicyJson={settings.checkpointPolicyJson}
      onCheckpointPolicyJsonChange={(checkpointPolicyJson) => { onChange({ ...settings, checkpointPolicyJson }) }}
    />
  )
}
