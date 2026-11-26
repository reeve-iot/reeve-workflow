import { useState } from 'react'
import {
  Accordion, AccordionDetails, AccordionSummary, Box, Button, FormHelperText, MenuItem, Select, TextField, Typography,
} from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import type { ErrorPolicyName, NodeErrorPolicy } from '@reeve-iot/workflow-core'
import { describeRepeatedMember, findRepeatedMember } from '@reeve-iot/workflow-core'

const POLICY_OPTIONS: { value: ErrorPolicyName; label: string }[] = [
  { value: 'FailWorkflow', label: 'Fail Workflow (Default)' },
  { value: 'ContinueWithDefault', label: 'Continue With Default Output' },
  { value: 'EmitError', label: 'Emit Error to Virtual Port' },
  { value: 'Skip', label: 'Skip Node & Downstream' },
]

function defaultOutputVerdict(text: string): string | null {
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    return 'Must be valid JSON'
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return 'Must be a JSON object'
  const repeat = findRepeatedMember(text)
  return repeat === undefined ? null : `Ambiguous JSON: ${describeRepeatedMember(repeat)}`
}

export interface ErrorHandlingSectionProps {
  /** The node's own policy, the one the run reads. */
  errorPolicy: NodeErrorPolicy | null | undefined
  /** Receives the policy to store on the node, never with `routeToErrorPort`, which no run reads. */
  onChange: (policy: NodeErrorPolicy) => void
  /** Writes no policy at all onto the node. Absent without it, for a host whose store cannot drop one, and
   * the four explicit policies remain either way. */
  onReset?: () => void
}

/** What happens when the block throws. Collapsed by default — most nodes never change it. */
export function ErrorHandlingSection({ errorPolicy, onChange, onReset }: ErrorHandlingSectionProps) {
  const [expanded, setExpanded] = useState(false)
  const [draft, setDraft] = useState<string | null>(null)
  const [seen, setSeen] = useState(errorPolicy?.defaultOutputJson ?? '')
  const storedText = errorPolicy?.defaultOutputJson ?? ''
  if (seen !== storedText) {
    setSeen(storedText)
    setDraft(null)
  }
  const policy: NodeErrorPolicy = { ...errorPolicy }
  delete policy.routeToErrorPort

  const jsonError = draft === null || draft.trim() === '' ? null : defaultOutputVerdict(draft)

  return (
    <Accordion
      expanded={expanded}
      onChange={() => { setExpanded(!expanded) }}
      sx={{ backgroundColor: 'transparent', boxShadow: 'none', '&.MuiAccordion-root:before': { display: 'none' } }}
    >
      <AccordionSummary expandIcon={<ExpandMoreIcon />}>
        <Typography variant="subtitle2">Error Handling</Typography>
      </AccordionSummary>
      <AccordionDetails>
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <Box>
            <Typography variant="body2" sx={{ mb: 1 }}>Error Policy</Typography>
            <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
              <Select
                fullWidth size="small" value={policy.policy ?? 'FailWorkflow'}
                inputProps={{ 'aria-label': 'Error Policy' }} sx={{ flex: 1 }}
              >
                {POLICY_OPTIONS.map((o) => (
                  <MenuItem
                    key={o.value} value={o.value}
                    onClick={() => {
                      setDraft(null)
                      onChange({ ...policy, policy: o.value })
                    }}
                  >
                    {o.label}
                  </MenuItem>
                ))}
              </Select>
              {onReset !== undefined && (
                <Button size="small" onClick={onReset} aria-label="Reset to default">Reset to default</Button>
              )}
            </Box>
            <FormHelperText>
              {policy.policy === 'EmitError'
                ? 'A failure is sent out of the _error output instead of the block\'s own'
                : 'How to handle execution failures'}
            </FormHelperText>
          </Box>

          {policy.policy === 'ContinueWithDefault' && (
            <TextField
              fullWidth size="small" label="Default Output (JSON)" multiline minRows={3} maxRows={6}
              placeholder='{"Result": 42}'
              value={draft ?? storedText}
              error={jsonError !== null}
              helperText={jsonError ?? 'JSON object to use as output when block fails'}
              slotProps={{ htmlInput: { 'aria-label': 'Default Output (JSON)', style: { fontFamily: 'monospace', fontSize: 11 } } }}
              onChange={(e) => {
                const json = e.target.value
                setDraft(json)
                if (json.trim() === '') {
                  const cleared = { ...policy }
                  delete cleared.defaultOutputJson
                  onChange(cleared)
                  return
                }
                if (defaultOutputVerdict(json) === null) onChange({ ...policy, defaultOutputJson: json })
              }}
            />
          )}
        </Box>
      </AccordionDetails>
    </Accordion>
  )
}
