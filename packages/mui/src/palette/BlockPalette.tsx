import type { DragEvent } from 'react'
import { Fragment, useMemo, useState } from 'react'
import {
  Box, Collapse, List, ListItem, ListItemButton, ListItemIcon, ListItemText, Paper, TextField, Tooltip, Typography,
} from '@mui/material'
import ExpandLess from '@mui/icons-material/ExpandLess'
import ExpandMore from '@mui/icons-material/ExpandMore'
import LockIcon from '@mui/icons-material/Lock'
import WidgetsIcon from '@mui/icons-material/Widgets'
import type { BlockTypeDescriptor } from '@reeve-iot/workflow-core'
import {
  WORKFLOW_INPUT_BLOCK_TYPE_ID,
  WORKFLOW_OUTPUT_BLOCK_TYPE_ID,
  useWorkflowEditorStore,
} from '@reeve-iot/workflow-core'

/** The MIME type a palette drag carries. The canvas reads the same key. */
export const BLOCK_DRAG_MIME = 'application/reeve-workflow-block'

/** Why a block cannot be used here. The library has no notion of permission or licence, so a host that
 * gates blocks answers this instead. */
export interface BlockLock {
  reason: string
}

export interface BlockPaletteProps {
  /** Return a lock to grey a block out and refuse the drag; return null to leave it usable. */
  isBlockLocked?: (blockType: BlockTypeDescriptor) => BlockLock | null
  /** Heading for blocks whose descriptor declares no category. */
  uncategorisedLabel?: string
  title?: string
  subtitle?: string
}

export function BlockPalette({
  isBlockLocked,
  uncategorisedLabel = 'Other',
  title = 'Block Palette',
  subtitle = 'Drag blocks to the canvas or click to add',
}: BlockPaletteProps) {
  const blockTypes = useWorkflowEditorStore((s) => s.blockTypes)
  const setPaletteDragActive = useWorkflowEditorStore((s) => s.setPaletteDragActive)
  const requestQuickAddBlock = useWorkflowEditorStore((s) => s.requestQuickAddBlock)
  const [openCategories, setOpenCategories] = useState<Record<string, boolean>>({})
  const [searchTerm, setSearchTerm] = useState('')

  const search = searchTerm.trim().toLowerCase()

  const categories = useMemo(() => {
    const grouped: Record<string, BlockTypeDescriptor[]> = {}
    for (const bt of blockTypes) {
      if (bt.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID || bt.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID) continue
      const category = bt.category ?? uncategorisedLabel
      const matches =
        search.length === 0 ||
        bt.displayName.toLowerCase().includes(search) ||
        (bt.description ?? '').toLowerCase().includes(search) ||
        category.toLowerCase().includes(search)
      if (!matches) continue
      ;(grouped[category] ??= []).push(bt)
    }
    return grouped
  }, [blockTypes, search, uncategorisedLabel])

  const startDrag = (event: DragEvent, blockTypeId: string, locked: boolean) => {
    if (locked) {
      event.preventDefault()
      return
    }
    event.dataTransfer.setData(BLOCK_DRAG_MIME, blockTypeId)
    event.dataTransfer.effectAllowed = 'move'
    setPaletteDragActive(true)
  }

  return (
    <Paper elevation={2} sx={{ width: 280, height: '100%', zIndex: 5, overflowY: 'auto', borderRadius: 0, borderRight: 1, borderColor: 'divider' }}>
      <Box sx={{ p: 2, borderBottom: 1, borderColor: 'divider' }}>
        <Typography variant="h6">{title}</Typography>
        <Typography variant="body2" color="textSecondary">{subtitle}</Typography>
        <TextField
          fullWidth size="small" label="Search blocks" value={searchTerm}
          onChange={(event) => setSearchTerm(event.target.value)} sx={{ mt: 1.5 }}
        />
      </Box>
      <List component="nav">
        {Object.entries(categories).map(([category, types]) => (
          <Fragment key={category}>
            <ListItemButton onClick={() => setOpenCategories((prev) => ({ ...prev, [category]: !prev[category] }))} data-testid={`palette-category-${category}`}>
              <ListItemIcon><WidgetsIcon /></ListItemIcon>
              <ListItemText primary={category} />
              {openCategories[category] ? <ExpandLess /> : <ExpandMore />}
            </ListItemButton>
            <Collapse in={openCategories[category] ?? false} timeout="auto" unmountOnExit>
              <List component="div" disablePadding>
                {types.map((bt) => {
                  const lock = isBlockLocked?.(bt) ?? null
                  return (
                    <Tooltip key={bt.blockTypeId} title={lock?.reason ?? ''} placement="right" arrow>
                      <span>
                        <ListItem
                          data-testid={`workflow-block-${bt.blockTypeId}`}
                          sx={{
                            pl: 4,
                            cursor: lock ? 'not-allowed' : 'grab',
                            opacity: lock ? 0.5 : 1,
                            '&:hover': { bgcolor: lock ? undefined : 'action.hover' },
                          }}
                          draggable={!lock}
                          onDragStart={(e) => startDrag(e, bt.blockTypeId, !!lock)}
                          onDragEnd={() => setPaletteDragActive(false)}
                          onClick={() => {
                            if (!lock) requestQuickAddBlock(bt.blockTypeId)
                          }}
                        >
                          <ListItemText
                            primary={
                              <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                                {bt.displayName}
                                {lock && <LockIcon sx={{ fontSize: 12, color: 'text.disabled' }} />}
                              </Box>
                            }
                            secondary={bt.description}
                            slotProps={{ primary: { variant: 'body2', sx: { fontWeight: 'bold' } }, secondary: { variant: 'caption' } }}
                          />
                        </ListItem>
                      </span>
                    </Tooltip>
                  )
                })}
              </List>
            </Collapse>
          </Fragment>
        ))}
      </List>
    </Paper>
  )
}
