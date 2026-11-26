import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

export interface HarnessState {
  readonly baseUrl: string
  readonly operatorId: string
  readonly hostPid: number
}

export const STATE_FILE = fileURLToPath(new URL('../test-results/harness.json', import.meta.url))

export function readHarnessState(): HarnessState {
  const text = readFileSync(STATE_FILE, 'utf8')
  return JSON.parse(text) as HarnessState
}
