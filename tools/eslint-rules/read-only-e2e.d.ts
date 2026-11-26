import type { ESLint } from 'eslint'

/** The rule is JavaScript, because eslint.config.js loads it by path: its shape is declared here so the
 * rule's own test can call it. */
declare const plugin: ESLint.Plugin
export default plugin

export declare const CLIENT_MODULE: string
export declare const CLIENT_OPERATIONS: readonly string[]
export declare const GRANTS: ReadonlyMap<string, {
  readonly reason: string
  readonly modules: ReadonlyMap<string, ReadonlySet<string> | null>
  readonly process: readonly string[]
}>
