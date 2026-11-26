import { useMemo } from 'react'
import { Box, Button, MenuItem, Select, Typography } from '@mui/material'
import { typeArgumentBases, usePortTypes, wrapTypeArgument } from '@reeve-iot/workflow-core'
import type { BlockTypeParameter } from '@reeve-iot/workflow-core'
import { typesWithCurrent } from '../ports/collectionComposer.js'
import type { TypeArgumentPickerContext } from './nodeConfigCustomization.js'

const INFERRED = ' inferred'

function ParameterRow({ parameter, context }: { parameter: BlockTypeParameter; context: TypeArgumentPickerContext }) {
  const portTypes = usePortTypes()
  const constraints = useMemo(() => parameter.constraints ?? [], [parameter])
  const bases = useMemo(() => typeArgumentBases(portTypes, constraints), [portTypes, constraints])
  const stored = Object.hasOwn(context.typeArguments, parameter.name) ? context.typeArguments[parameter.name] : undefined
  const name = parameter.name

  const write = (term: string | undefined) => {
    const next = { ...context.typeArguments }
    if (term === undefined) delete next[name]
    else next[name] = term
    context.onChange(next)
  }
  const collection = stored === undefined ? undefined : wrapTypeArgument(portTypes, stored, 'collection', constraints)
  const nullable = stored === undefined ? undefined : wrapTypeArgument(portTypes, stored, 'nullable', constraints)

  return (
    <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', flexWrap: 'wrap', mb: 1 }} data-testid={`type-argument-${name}`}>
      <Typography variant="body2" sx={{ minWidth: 40 }}>{name}</Typography>
      <Select
        size="small" variant="standard" displayEmpty value={stored ?? INFERRED} sx={{ minWidth: 140 }}
        inputProps={{ 'aria-label': `Type argument ${name}` }}
        onChange={(event) => { write(event.target.value === INFERRED ? undefined : event.target.value) }}
      >
        <MenuItem value={INFERRED}><em>Inferred</em></MenuItem>
        {typesWithCurrent(bases, stored).map((term) => <MenuItem key={term} value={term}>{term}</MenuItem>)}
        {stored === '' && <MenuItem value="">(empty)</MenuItem>}
      </Select>
      <Button
        size="small" variant="outlined" disabled={collection === undefined} aria-label={`Type argument ${name} in a collection`}
        onClick={() => { if (collection !== undefined) write(collection) }}
      >
        Collection
      </Button>
      <Button
        size="small" variant="outlined" disabled={nullable === undefined} aria-label={`Type argument ${name} nullable`}
        onClick={() => { if (nullable !== undefined) write(nullable) }}
      >
        Nullable
      </Button>
      <Button size="small" disabled={stored === undefined} aria-label={`Clear type argument ${name}`} onClick={() => { write(undefined) }}>
        Clear
      </Button>
      {constraints.length > 0 && <Typography variant="caption" color="text.secondary">{constraints.join(', ')}</Typography>}
    </Box>
  )
}

/** One row per type parameter: the registered terms implementing its constraints, a collection or nullable level, and Clear,
 * which removes the entry so the compiler infers it again. */
export function TypeArgumentPicker({ context }: { context: TypeArgumentPickerContext }) {
  return (
    <Box sx={{ mt: 2, mb: 2 }}>
      <Typography variant="subtitle2" sx={{ mb: 1 }}>Type Arguments</Typography>
      {context.typeParameters.map((parameter) => <ParameterRow key={parameter.name} parameter={parameter} context={context} />)}
    </Box>
  )
}
