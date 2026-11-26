import { useState } from 'react'
import type { ReactNode } from 'react'
import {
  Box, Checkbox, IconButton, InputAdornment, List, ListItem, ListItemIcon, ListItemText, TextField,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import type { IncomingVariable, PortTermDisplay } from '@reeve-iot/workflow-core'
import { termText } from '../canvas/analysedPortTerm.js'
import { BLANK_NAME_ACCEPTED, draftInScope, nameFault } from '../nameDraft.js'

export interface PassthroughSectionProps {
  incoming: readonly IncomingVariable[]
  passthrough: readonly string[]
  /** Ticking a name is more than adding it to a list — a host may have to feed it on the wire too — so the
   * section reports the name and leaves the meaning to its owner. */
  onToggle: (name: string) => void
  /** Branch names this block owns, named in the hint: a tick here also reaches those sub-canvases. */
  subCanvasNames?: readonly string[]
  /** A value editor for a forwarded name, or null for a name that must not be authored here. */
  renderValueEditor?: (name: string) => ReactNode
  /** The term the server's analysis gives a name this node forwards, shown in place of the incoming one; the feed decides it. */
  termOf?: (name: string) => PortTermDisplay | undefined
}

const incomingTerm = (v: IncomingVariable, own: PortTermDisplay | undefined): string | undefined =>
  own !== undefined ? termText(own) : v.type === undefined ? undefined : v.stale ? `${v.type} (stale)` : v.type
const customTerm = (own: PortTermDisplay | undefined): string => own === undefined ? 'Custom' : `Custom (${termText(own)})`

interface PassthroughDraft {
  scope: string
  text: string
  touched: boolean
}

/** Which incoming variables this node forwards to its own outputs, plus names typed by hand for a value
 * the editor cannot see yet — a plugin output the catalogue has not caught up with. */
export function PassthroughSection({
  incoming, passthrough, onToggle, subCanvasNames = [], renderValueEditor, termOf,
}: PassthroughSectionProps) {
  const context = JSON.stringify([incoming.map((v) => v.name), passthrough])
  const [draft, setDraft] = useState<PassthroughDraft>({ scope: context, text: '', touched: false })
  const live = draftInScope(draft, context) ?? { scope: context, text: '', touched: false }

  const toggle = onToggle

  const addCustom = () => {
    setDraft({ scope: context, text: live.text, touched: true })
    if (nameFault(live.text, BLANK_NAME_ACCEPTED) !== null || passthrough.includes(live.text)) return
    onToggle(live.text)
    setDraft({ scope: context, text: '', touched: false })
  }

  const nameFaultText = live.text === '' && !live.touched ? null : nameFault(live.text, BLANK_NAME_ACCEPTED)

  const valueEditors = passthrough
    .map((name) => [name, renderValueEditor?.(name) ?? null] as const)
    .filter(([, editor]) => editor !== null)

  const custom = passthrough.filter((p) => !incoming.some((v) => v.name === p))

  return (
    <Box>
      <Typography variant="subtitle2" sx={{ mb: 1 }}>Passthrough Variables</Typography>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
        Incoming variables that should be passed to the output.
      </Typography>
      {subCanvasNames.length > 0 && (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
          Each also reaches {subCanvasNames.join(' / ')}: a sub-canvas is seeded with this block&apos;s
          resolved inputs, so ticking one gives Workflow Input inside the branch a matching port.
        </Typography>
      )}

      <List disablePadding>
        {incoming.map((v) => (
          <ListItem key={v.name} dense disableGutters>
            <ListItemIcon sx={{ minWidth: 36 }}>
              <Checkbox
                size="small" edge="start" checked={passthrough.includes(v.name)}
                slotProps={{ input: { 'aria-label': `Forward ${v.name}` } }}
                onChange={() => { toggle(v.name) }}
              />
            </ListItemIcon>
            <ListItemText
              primary={v.name} secondary={incomingTerm(v, passthrough.includes(v.name) ? termOf?.(v.name) : undefined)}
              slotProps={{ primary: { variant: 'body2' } }}
            />
          </ListItem>
        ))}
        {custom.map((p) => (
          <ListItem key={p} dense disableGutters>
            <ListItemIcon sx={{ minWidth: 36 }}>
              <Checkbox
                size="small" edge="start" checked
                slotProps={{ input: { 'aria-label': `Forward ${p}` } }}
                onChange={() => { toggle(p) }}
              />
            </ListItemIcon>
            <ListItemText
              primary={p} secondary={customTerm(termOf?.(p))}
              slotProps={{ primary: { variant: 'body2' } }}
            />
          </ListItem>
        ))}
      </List>

      {valueEditors.length > 0 && (
        <Box sx={{ mt: 2, display: 'flex', flexDirection: 'column', gap: 1.5 }}>
          <Typography variant="caption" color="text.secondary">
            {subCanvasNames.length > 0
              ? 'Values — a variable no wire feeds can be given a constant or an expression, and reaches the sub-canvas the same way.'
              : 'Values'}
          </Typography>
          {valueEditors.map(([name, editor]) => <Box key={`passthrough-${name}`}>{editor}</Box>)}
        </Box>
      )}

      <Box sx={{ mt: 2, display: 'flex', gap: 1 }}>
        <TextField
          size="small" placeholder="Add variable name" value={live.text}
          error={nameFaultText !== null}
          helperText={nameFaultText ?? undefined}
          slotProps={{
            htmlInput: { 'aria-label': 'Add variable name' },
            input: {
              endAdornment: (
                <InputAdornment position="end">
                  <IconButton size="small" aria-label="Add passthrough variable" onClick={addCustom}>
                    <AddIcon fontSize="small" />
                  </IconButton>
                </InputAdornment>
              ),
            },
          }}
          onChange={(e) => { setDraft({ scope: context, text: e.target.value, touched: true }) }}
          onKeyDown={(e) => { if (e.key === 'Enter') addCustom() }}
        />
      </Box>
    </Box>
  )
}
