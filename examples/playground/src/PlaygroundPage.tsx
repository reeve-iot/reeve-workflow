import { useEffect, useState } from 'react'
import { Alert } from '@mui/material'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import {
  MappingCustomizationContext, NodeConfigCustomizationContext, WorkflowEditorStoreProvider, WorkflowReferenceResolverContext,
} from '@reeve-iot/workflow-mui'
import type { PortTypeRegistry } from '@reeve-iot/workflow-core'
import { EditorShell } from './EditorShell.js'
import { MAPPING_CUSTOMIZATION, useNodeConfigCustomization } from './hostSlots.js'
import { useSeams } from './seams.js'
import { VALIDATION_MESSAGES } from './validationMessages.js'

export function PlaygroundPage() {
  const seams = useSeams()
  const [seed, setSeed] = useState<PortTypeRegistry | null>(null)
  const [seedError, setSeedError] = useState<unknown>(null)
  useEffect(() => {
    const controller = new AbortController()
    seams.readVocabulary(controller.signal).then(setSeed, (error: unknown) => { if (!controller.signal.aborted) setSeedError(error) })
    return () => { controller.abort() }
  }, [seams])
  const [theme] = useState(() => createTheme())
  const [blockTypeError, setBlockTypeError] = useState<unknown>(null)
  const customization = useNodeConfigCustomization(seams.workflowOptions)

  if (seed === null) {
    return seedError === null ? null : <Alert severity="error">{`The port-type vocabulary could not be read: ${String(seedError)}`}</Alert>
  }

  return (
    <ThemeProvider theme={theme}>
      <WorkflowEditorStoreProvider options={{
          portTypes: seed,
          validationMessages: VALIDATION_MESSAGES,
          onBlockTypeError: setBlockTypeError,
          draftAnalysis: seams.analysis,
        }}>
        {blockTypeError !== null && <Alert severity="error">{String(blockTypeError)}</Alert>}
        <WorkflowReferenceResolverContext.Provider value={seams.reference.resolver}>
          <NodeConfigCustomizationContext.Provider value={customization}>
            <MappingCustomizationContext.Provider value={MAPPING_CUSTOMIZATION}>
              <EditorShell seams={seams} />
            </MappingCustomizationContext.Provider>
          </NodeConfigCustomizationContext.Provider>
        </WorkflowReferenceResolverContext.Provider>
      </WorkflowEditorStoreProvider>
    </ThemeProvider>
  )
}
