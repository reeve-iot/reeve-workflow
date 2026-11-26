import { useMemo, useState } from 'react'
import {
  Box, Button, Checkbox, IconButton, MenuItem, Select, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteIcon from '@mui/icons-material/Delete'
import type { DeclaredOutputPort, StoredListRead } from '@reeve-iot/workflow-core'
import { describeStoredDocumentFault, readPortList, usePortTypes } from '@reeve-iot/workflow-core'
import { BLANK_NAME_ACCEPTED, BLANK_NAME_REFUSED, draftInScope, nameFault } from '../nameDraft.js'
import type { NameDraft } from '../nameDraft.js'
import { CollectionComposer, typesWithCurrent } from './collectionComposer.js'

interface DeclaredPort {
  name: string
  displayName?: string
  type: string
  isRequired?: boolean
}

export interface PortDeclarationTableProps {
  label: string
  /** The block's stored port declaration, as the JSON string the node configuration holds. */
  value: string | null | undefined
  onChange: (json: string) => void
}

/** Editor for the ports a block declares in its own configuration. The type choices come from the host's
 * registry, so a host type appears here without this component knowing it exists. */
export function PortDeclarationTable({ label, value, onChange }: PortDeclarationTableProps) {
  const portTypes = usePortTypes()
  const typeNames = useMemo(() => portTypes.list().map((t) => t.name), [portTypes])

  const read = useMemo((): StoredListRead => (value === null || value === undefined
    ? { ok: true, sites: [], entries: [] }
    : typeof value === 'string'
      ? readPortList(value, 'input')
      : { ok: false, fault: { rule: 'L7', location: '', nodeId: null, detail: 'a value that is not a string where a port list written as a string is required' } }), [value])
  const ports = useMemo<DeclaredPort[]>(
    () => (read.ok ? read.entries.map((p) => ({ ...(p as unknown as DeclaredPort), isRequired: p['isRequired'] === true })) : []),
    [read],
  )

  const write = (next: DeclaredPort[]): string => {
    const json = JSON.stringify(next)
    onChange(json)
    return json
  }
  const setField = (index: number, field: keyof DeclaredPort, fieldValue: string | boolean) =>
    write(ports.map((p, i) => {
      if (i !== index) return p
      if (field === 'displayName' && fieldValue === '') {
        const next = { ...p }
        delete next.displayName
        return next
      }
      return { ...p, [field]: fieldValue }
    }))

  const [nameDrafts, setNameDrafts] = useState<Record<number, NameDraft>>({})
  const scope = typeof value === 'string' ? value : ''
  const changeName = (index: number, text: string) => {
    if (nameFault(text, BLANK_NAME_REFUSED) !== null) {
      setNameDrafts((drafts) => ({ ...drafts, [index]: { scope, text } }))
      return
    }
    const produced = write(ports.map((p, i) => (i === index ? { ...p, name: text } : p)))
    setNameDrafts((drafts) => ({ ...drafts, [index]: { scope: produced, text } }))
  }

  if (!read.ok) {
    return (
      <Box sx={{ mt: 2, mb: 2 }}>
        <Typography variant="subtitle2" sx={{ mb: 1 }}>{label}</Typography>
        <Typography role="alert" variant="body2" color="error">
          {`This port list cannot be read: ${describeStoredDocumentFault(read.fault)}.`}
        </Typography>
      </Box>
    )
  }

  return (
    <Box sx={{ mt: 2, mb: 2 }}>
      <Typography variant="subtitle2" sx={{ mb: 1 }}>{label}</Typography>
      <TableContainer component={Box} sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 1 }}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell sx={{ py: 0.5 }}>Name</TableCell>
              <TableCell sx={{ py: 0.5 }}>Label</TableCell>
              <TableCell sx={{ py: 0.5 }}>Type</TableCell>
              <TableCell sx={{ py: 0.5 }}>Req</TableCell>
              <TableCell sx={{ py: 0.5, width: 40 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {ports.map((port, index) => {
              const name = draftInScope(nameDrafts[index], scope)?.text ?? port.name
              const nameFaultText = nameFault(name, BLANK_NAME_REFUSED)
              return (
                <TableRow key={index}>
                  <TableCell sx={{ py: 0.5 }}>
                    <TextField
                      size="small" variant="standard" fullWidth value={name}
                      error={nameFaultText !== null}
                      helperText={nameFaultText ?? undefined}
                      slotProps={{ htmlInput: { 'aria-label': `Port ${index + 1} name` } }}
                      onChange={(e) => { changeName(index, e.target.value) }}
                    />
                  </TableCell>
                  <TableCell sx={{ py: 0.5 }}>
                    <TextField
                      size="small" variant="standard" fullWidth value={port.displayName ?? ''}
                      slotProps={{ htmlInput: { 'aria-label': `Port ${index + 1} label` } }}
                      onChange={(e) => setField(index, 'displayName', e.target.value)}
                    />
                  </TableCell>
                  <TableCell sx={{ py: 0.5 }}>
                    <Box sx={{ display: 'flex', gap: 0.5, alignItems: 'center' }}>
                      <Select
                        size="small" variant="standard" fullWidth value={port.type}
                        inputProps={{ 'aria-label': `Port ${index + 1} type` }}
                        onChange={(e) => setField(index, 'type', e.target.value)}
                      >
                        {typesWithCurrent(typeNames, port.type).map((type) => (
                          <MenuItem key={type} value={type}>{type}</MenuItem>
                        ))}
                      </Select>
                      <CollectionComposer
                        type={port.type} portTypes={portTypes}
                        ariaLabel={`Port ${index + 1} type wrapped in a collection`}
                        onWrap={(wrapped) => setField(index, 'type', wrapped)}
                      />
                    </Box>
                  </TableCell>
                  <TableCell sx={{ py: 0.5 }}>
                    <Checkbox
                      size="small" checked={port.isRequired ?? false}
                      slotProps={{ input: { 'aria-label': `Port ${index + 1} required` } }}
                      onChange={(e) => setField(index, 'isRequired', e.target.checked)}
                    />
                  </TableCell>
                  <TableCell sx={{ py: 0.5 }}>
                    <IconButton
                      size="small" color="error" aria-label={`Remove port ${index + 1}`}
                      onClick={() => { write(ports.filter((_, i) => i !== index)) }}
                    >
                      <DeleteIcon fontSize="small" />
                    </IconButton>
                  </TableCell>
                </TableRow>
              )
            })}
            {ports.length === 0 && (
              <TableRow>
                <TableCell colSpan={5} align="center">
                  <Typography variant="caption" color="text.secondary">No ports declared</Typography>
                </TableCell>
              </TableRow>
            )}
          </TableBody>
        </Table>
      </TableContainer>
      <Button
        startIcon={<AddIcon />} size="small" variant="outlined" fullWidth sx={{ mt: 1 }}
        onClick={() => {
          write([...ports, { name: 'NewPort', displayName: 'New Port', type: 'String', isRequired: false }])
        }}
      >
        Add Port
      </Button>
    </Box>
  )
}

function newDeclaredOutput(ports: DeclaredOutputPort[], subCanvasNames: string[]): DeclaredOutputPort {
  const taken = new Set(ports.map((p) => p.name))
  let name = 'output'
  for (let n = 2; taken.has(name); n += 1) name = `output${String(n)}`
  return {
    name,
    displayName: 'Output',
    type: '',
    sources: Object.fromEntries(subCanvasNames.map((branch) => [branch, name])),
  }
}

export interface DeclaredOutputPortTableProps {
  label: string
  subCanvasNames: string[]
  value: DeclaredOutputPort[] | undefined
  onChange: (ports: DeclaredOutputPort[]) => void
}

/** Editor for a node's declared outputs, including which port of each branch feeds them. */
export function DeclaredOutputPortTable({ label, subCanvasNames, value, onChange }: DeclaredOutputPortTableProps) {
  const portTypes = usePortTypes()
  const typeNames = useMemo(() => portTypes.list().map((t) => t.name), [portTypes])
  const ports = value ?? []

  const setField = (index: number, field: 'name' | 'displayName' | 'type', v: string) => onChange(ports.map((p, i) => {
    if (i !== index) return p
    if (field === 'displayName' && v === '') {
      const next = { ...p }
      delete next.displayName
      return next
    }
    return { ...p, [field]: v }
  }))
  const setSource = (index: number, branch: string, v: string) => onChange(ports.map((p, i) => {
    if (i !== index) return p
    const sources = { ...(p.sources ?? {}), [branch]: v }
    if (Object.values(sources).every((source) => source === '')) {
      const withoutSources = { ...p }
      delete withoutSources.sources
      return withoutSources
    }
    return { ...p, sources }
  }))

  const [nameDrafts, setNameDrafts] = useState<Record<number, NameDraft>>({})
  const scope = JSON.stringify(ports)
  const changeName = (index: number, text: string) => {
    if (nameFault(text, BLANK_NAME_ACCEPTED) !== null) {
      setNameDrafts((drafts) => ({ ...drafts, [index]: { scope, text } }))
      return
    }
    const next = ports.map((p, i) => (i === index ? { ...p, name: text } : p))
    setNameDrafts((drafts) => ({ ...drafts, [index]: { scope: JSON.stringify(next), text } }))
    onChange(next)
  }

  return (
    <Box sx={{ mt: 2, mb: 2 }}>
      <Typography variant="subtitle2" sx={{ mb: 1 }}>{label}</Typography>
      {ports.map((port, index) => {
        const storedName = port.name ?? ''
        const name = draftInScope(nameDrafts[index], scope)?.text ?? storedName
        const nameFaultText = nameFault(name, BLANK_NAME_ACCEPTED)
        return (
          <Box key={index} sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 1, p: 1, mb: 1 }}>
            <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
              <TextField
                size="small" label="Name" variant="standard" value={name}
                error={nameFaultText !== null}
                helperText={nameFaultText ?? undefined}
                onChange={(e) => { changeName(index, e.target.value) }}
              />
              <TextField size="small" label="Label" variant="standard" value={port.displayName ?? ''} onChange={(e) => setField(index, 'displayName', e.target.value)} />
              <Select
                size="small" variant="standard" value={port.type} sx={{ minWidth: 120 }}
                inputProps={{ 'aria-label': `Output ${index + 1} type` }}
                onChange={(e) => setField(index, 'type', e.target.value)}
              >
                {typesWithCurrent(typeNames, port.type).map((t) => <MenuItem key={t} value={t}>{t}</MenuItem>)}
              </Select>
              <CollectionComposer
                type={port.type} portTypes={portTypes}
                ariaLabel={`Output ${index + 1} type wrapped in a collection`}
                onWrap={(wrapped) => setField(index, 'type', wrapped)}
              />
              <IconButton
                size="small" color="error" aria-label={`Remove output ${index + 1}`}
                onClick={() => { onChange(ports.filter((_, i) => i !== index)) }}
              >
                <DeleteIcon fontSize="small" />
              </IconButton>
            </Box>
            <Box sx={{ pl: 1, mt: 0.5 }}>
              {subCanvasNames.map((branch) => (
                <TextField
                  key={branch} size="small" variant="standard" sx={{ mr: 1, width: 140 }}
                  label={`${branch} →`} placeholder={port.name}
                  value={port.sources?.[branch] ?? ''}
                  onChange={(e) => setSource(index, branch, e.target.value)}
                />
              ))}
            </Box>
          </Box>
        )
      })}
      <Button
        startIcon={<AddIcon />} size="small" variant="outlined" fullWidth
        onClick={() => { onChange([...ports, newDeclaredOutput(ports, subCanvasNames)]) }}
      >
        Add Output Port
      </Button>
    </Box>
  )
}
