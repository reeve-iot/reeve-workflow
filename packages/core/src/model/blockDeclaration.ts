/** The rule id a block declaration is refused under when the catalogue is read. */
export const BLOCK_PORT_DECLARATION_INVALID = 'block-port-declaration-invalid'

/** The refusal of a block declaration, carrying what a finding needs so no reader has to parse the message. `where`
 * names the port (`port 'A'`, or `sub-canvas 'Body' port 'A'`) or a scope variable. */
export class BlockPortDeclarationInvalidError extends Error {
  constructor(
    readonly blockTypeId: string,
    readonly where: string,
    readonly reason: string,
  ) {
    super(`${BLOCK_PORT_DECLARATION_INVALID}: Block '${blockTypeId}' ${where} ${reason}.`)
    this.name = 'BlockPortDeclarationInvalidError'
  }
}
