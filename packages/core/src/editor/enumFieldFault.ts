import type { BlockTypeDescriptor } from '../model/blockType.js'

/** The reason a block's Enum configuration fields are malformed, under the rule ids C# registration refuses them with. */
export function enumFieldFault(block: BlockTypeDescriptor): string | undefined {
  for (const field of block.configurationSchema ?? []) {
    const where = `Block type '${block.blockTypeId}' configuration field '${field.name}'`
    const names = field.allowedValues
    if (field.type !== 'Enum') {
      if (names !== undefined && names !== null) return `${where} is not an Enum field and declares allowedValues (rule enum-values-on-non-enum-field).`
      continue
    }
    if (!names || names.length === 0) return `${where} is an Enum field and declares no allowedValues (rule enum-field-no-values).`
    const seen = new Set<string>()
    for (const name of names) {
      if (typeof name !== 'string' || name.trim() === '') return `${where} declares an empty or whitespace-only allowed value (rule enum-field-blank-value).`
      if (seen.has(name)) return `${where} declares the allowed value '${name}' more than once, compared ordinally (rule enum-field-duplicate-value).`
      seen.add(name)
    }
    if (field.defaultValue !== undefined && field.defaultValue !== null && !seen.has(field.defaultValue)) {
      return `${where} has the default '${field.defaultValue}', which is not one of its allowedValues (rule enum-field-default-not-allowed).`
    }
  }
  return undefined
}
