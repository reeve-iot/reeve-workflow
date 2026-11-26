export const PORT_LABEL_FONT_SIZE = 11
export const PORT_LABEL_INSET = 12
export const PORT_LABEL_GAP = 12

const EM_WIDTHS: ReadonlyArray<readonly [string, number]> = [
  ["ijl|'", 0.222],
  [' !,./:;I[]\\ft', 0.278],
  ['()-r`{}', 0.333],
  ['*"', 0.389],
  ['Jcksvxyz', 0.5],
  ['0123456789abdeghnopqu_#$?L', 0.556],
  ['+<=>~', 0.584],
  ['FTZ', 0.611],
  ['ABEKPSVXY&', 0.667],
  ['CDHNRUw', 0.722],
  ['GOQ', 0.778],
  ['Mm', 0.833],
  ['%', 0.889],
  ['W', 0.944],
  ['@', 1.015],
]
const UNLISTED_EM_WIDTH = 0.6
const WIDE_SCRIPT_START = 0x2e80
const ESTIMATE_MARGIN = 1.1
const emWidthOf = new Map(EM_WIDTHS.flatMap(([chars, width]) => [...chars].map((ch) => [ch, width] as const)))

export function estimatePortLabelWidth(text: string, fontSize = PORT_LABEL_FONT_SIZE): number {
  let em = 0
  for (const ch of text) em += emWidthOf.get(ch) ?? ((ch.codePointAt(0) ?? 0) >= WIDE_SCRIPT_START ? 1 : UNLISTED_EM_WIDTH)
  return em * fontSize
}

export interface PortLabelRowLayout {
  inputEnd: number
  outputStart: number
}

export function layoutPortLabelRow(input: string | undefined, output: string | undefined, nodeWidth: number): PortLabelRowLayout {
  const rowEnd = nodeWidth - PORT_LABEL_INSET
  if (input === undefined) return { inputEnd: PORT_LABEL_INSET, outputStart: PORT_LABEL_INSET }
  if (output === undefined) return { inputEnd: rowEnd, outputStart: rowEnd }

  const room = rowEnd - PORT_LABEL_INSET - PORT_LABEL_GAP
  const half = room / 2
  const inputWidth = estimatePortLabelWidth(input) * ESTIMATE_MARGIN
  const outputWidth = estimatePortLabelWidth(output) * ESTIMATE_MARGIN
  const inputShare =
    inputWidth + outputWidth <= room ? inputWidth + (room - inputWidth - outputWidth) / 2
      : inputWidth < half ? inputWidth
        : outputWidth < half ? room - outputWidth
          : half
  const inputEnd = PORT_LABEL_INSET + inputShare
  return { inputEnd, outputStart: inputEnd + PORT_LABEL_GAP }
}
