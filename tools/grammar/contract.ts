import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import type { Contract } from './check.js'

export function repoFile(...parts: string[]): string {
  for (let dir = __dirname; ; dir = dirname(dir)) {
    if (existsSync(join(dir, 'contracts', 'rule-parity.json'))) return join(dir, ...parts)
    if (dirname(dir) === dir) throw new Error(`No contracts/ directory above ${__dirname}.`)
  }
}

export const readText = (...parts: string[]): string => readFileSync(repoFile(...parts), 'utf8')

export const readJson = <T>(...parts: string[]): T => JSON.parse(readText(...parts)) as T

export const readContract = (): Contract => readJson<Contract>('contracts', 'stored-document-grammar.json')

export const ruleParityIds = (): string[] =>
  readJson<{ rules: { id: string }[] }>('contracts', 'rule-parity.json').rules.map((rule) => rule.id)

export const EDITOR_RULE = 'definition-undrawable'

export const EDITOR_STORE = ['packages', 'core', 'src', 'editor', 'store.ts']

const RULE_ID = /ruleId:\s*(?:[^,\n]*?\?\s*)?'([a-z][a-zA-Z0-9-]*)'(?:\s*:\s*'([a-z][a-zA-Z0-9-]*)')?/g

function sourcesIn(dir: string, parts: string[] = dir.split('/')): string[][] {
  return readdirSync(repoFile(dir), { withFileTypes: true }).flatMap((entry) => {
    const at = [...parts, entry.name]
    if (entry.isDirectory()) return sourcesIn(at.join('/'), at)
    const source = /\.tsx?$/.test(entry.name) && !/\.test\.tsx?$/.test(entry.name)
    return source ? [at] : []
  })
}

export function sourceRuleIds(): string[] {
  const sources = readdirSync(repoFile('packages'), { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((pack) => `packages/${pack.name}/src`)
    .filter((dir) => existsSync(repoFile(dir)))
  const named = sources
    .flatMap((dir) => sourcesIn(dir).flatMap((parts) => [...readText(...parts).matchAll(RULE_ID)]))
    .flatMap((match) => [match[1], match[2]])
    .filter((id) => id !== undefined)
  return [...new Set(named)]
}
