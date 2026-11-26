import { execFileSync } from 'node:child_process'
import { repoFile } from './contract.js'

const CONFIG = ['examples', 'playground', 'e2e', 'playwright.config.ts']
const TSCONFIG = ['tools', 'grammar', 'e2e.tsconfig.json']

interface Suite {
  readonly specs?: readonly { readonly title: string }[]
  readonly suites?: readonly Suite[]
  readonly title: string
}

const titles = (suite: Suite): string[] => [
  ...(suite.specs ?? []).map((spec) => spec.title),
  ...(suite.suites ?? []).flatMap(titles),
]

export function listedTests(): string[] {
  const binary = repoFile('node_modules', '.bin', 'playwright')
  const args = ['test', '--config', repoFile(...CONFIG), '--tsconfig', repoFile(...TSCONFIG), '--list', '--reporter=json']
  const report = execFileSync(binary, args, { cwd: repoFile(), encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })
  const listed = JSON.parse(report.slice(report.indexOf('{'))) as { errors?: readonly { message: string }[]; suites?: readonly Suite[] }
  const errors = listed.errors ?? []
  if (errors.length > 0) throw new Error(`The suite cannot be listed: ${errors.map((error) => error.message).join('; ')}`)
  return (listed.suites ?? []).flatMap(titles)
}
