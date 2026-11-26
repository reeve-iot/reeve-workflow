import { stopHarness } from './support/harness'

export default async function (): Promise<void> {
  await stopHarness()
}
