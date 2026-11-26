import { Alert, Button } from '@mui/material'

/** The stored value as an invalid literal shows it: text in quotes, anything else as JSON. */
export function describeStoredLiteral(value: unknown): string {
  return typeof value === 'string' ? `"${value}"` : String(JSON.stringify(value))
}

/** A stored literal that cannot be read as `expected`, shown as it is, with the action that replaces it. */
export function RefusedLiteral({ testId, value, expected, onReplace }: { testId: string; value: unknown; expected: string; onReplace: () => void }) {
  return (
    <Alert
      severity="error" data-testid={testId} data-rule-id="literal-port-value-invalid"
      action={<Button color="inherit" size="small" onClick={onReplace}>Replace</Button>}
    >
      The stored value {describeStoredLiteral(value)} cannot be read as {expected}.
    </Alert>
  )
}
