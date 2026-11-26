import type { BlockTypeDescriptor } from './blockType.js'

/** The branches a descriptor declares, in declared order; a descriptor that omits the list is read from its output
 * map, so every reader walks the same branches. */
export function declaredBranchNames(block: BlockTypeDescriptor): readonly string[] {
  return block.subCanvasNames ?? Object.keys(block.subCanvasOutputs ?? {})
}
