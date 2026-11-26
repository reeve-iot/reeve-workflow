import type { SyntheticEvent } from 'react'
import { Fragment } from 'react'
import { Box, Chip, Tab, Tabs, Typography } from '@mui/material'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import HomeIcon from '@mui/icons-material/Home'
import { useWorkflowEditorStore } from '@reeve-iot/workflow-core'

export interface SubCanvasBreadcrumbProps {
  /** Label for the outermost canvas. */
  rootLabel?: string
}

/** Where in the sub-canvas stack the editor currently is, plus one-click travel back out and sideways
 * into a sibling branch of the same block. */
export function SubCanvasBreadcrumb({ rootLabel = 'Main Workflow' }: SubCanvasBreadcrumbProps) {
  const canvasStack = useWorkflowEditorStore((s) => s.canvasStack)
  const blockTypes = useWorkflowEditorStore((s) => s.blockTypes)
  const exitToLevel = useWorkflowEditorStore((s) => s.exitToLevel)
  const exitSubCanvas = useWorkflowEditorStore((s) => s.exitSubCanvas)
  const enterSubCanvas = useWorkflowEditorStore((s) => s.enterSubCanvas)

  const lastFrame = canvasStack[canvasStack.length - 1]
  const parentNode = lastFrame?.nodes.find((n) => n.id === lastFrame.parentNodeId)
  const siblings = blockTypes.find((bt) => bt.blockTypeId === parentNode?.blockTypeId)?.subCanvasNames ?? []

  const switchBranch = (_: SyntheticEvent, next: string) => {
    if (lastFrame && next !== lastFrame.subCanvasName) {
      exitSubCanvas()
      enterSubCanvas(lastFrame.parentNodeId, next)
    }
  }

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', bgcolor: 'background.paper', borderBottom: '1px solid', borderColor: 'divider', zIndex: 5 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, px: 2, py: 1, flexWrap: 'wrap', minHeight: 48 }}>
        <Chip
          icon={<HomeIcon />}
          label={rootLabel}
          onClick={() => exitToLevel(0)}
          size="small"
          variant={canvasStack.length === 0 ? 'filled' : 'outlined'}
          color={canvasStack.length === 0 ? 'primary' : 'default'}
          clickable={canvasStack.length > 0}
        />
        {canvasStack.map((frame, index) => {
          const isLast = index === canvasStack.length - 1
          const frameParent = frame.nodes.find((n) => n.id === frame.parentNodeId)
          const nodeLabel =
            blockTypes.find((bt) => bt.blockTypeId === frameParent?.blockTypeId)?.displayName ?? frame.parentNodeId
          return (
            <Fragment key={`${frame.parentNodeId}:${frame.subCanvasName}:${index}`}>
              <ChevronRightIcon sx={{ fontSize: 16, color: 'text.disabled' }} />
              <Chip
                label={isLast ? nodeLabel : `${nodeLabel} › ${frame.subCanvasName}`}
                size="small"
                variant={isLast ? 'filled' : 'outlined'}
                color={isLast ? 'primary' : 'default'}
                clickable={!isLast}
                onClick={isLast ? undefined : () => exitToLevel(index + 1)}
              />
            </Fragment>
          )
        })}
        {canvasStack.length > 0 && (
          <Typography variant="caption" color="text.secondary" sx={{ ml: 1 }}>
            (editing sub-canvas)
          </Typography>
        )}
      </Box>

      {lastFrame && siblings.length > 1 && (
        <Box sx={{ px: 2, borderTop: '1px solid', borderColor: 'divider' }}>
          <Tabs
            value={lastFrame.subCanvasName}
            onChange={switchBranch}
            variant="scrollable"
            scrollButtons="auto"
            sx={{ minHeight: 40, '& .MuiTab-root': { minHeight: 40, py: 0.5, fontSize: '0.875rem' } }}
          >
            {siblings.map((name) => (
              <Tab key={name} label={name} value={name} />
            ))}
          </Tabs>
        </Box>
      )}
    </Box>
  )
}
