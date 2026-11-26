import {
  STORED_DOCUMENT_RULE_IDS,
  grammarObjects,
  grammarPaths,
} from '../../packages/core/src/model/storedDocumentGrammar.js'
import { slug } from './check.js'
import type { Generated } from './check.js'
import { EDITOR_RULE, ruleParityIds } from './contract.js'

export function generatedByKindTable(): Generated {
  const paths = grammarPaths()
  const objects = grammarObjects()
  return {
    paths: paths.map((entry) => entry.path),
    objects: objects.map((entry) => entry.path),
    values: [...paths, ...objects].flatMap((entry) => entry.values.map((value) => `${slug(entry.path)}#${value}`)),
    rules: [...STORED_DOCUMENT_RULE_IDS, ...ruleParityIds(), EDITOR_RULE],
  }
}
