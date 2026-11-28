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
  ['\u2026', 1],
]
const UNLISTED_EM_WIDTH = 0.6
const WIDE_SCRIPT_START = 0x2e80
const ESTIMATE_MARGIN = 1.1
const ELLIPSIS = '\u2026'
const REQUIRED_MARK = '*'
const ROUNDING_TOLERANCE = 1e-6
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

/** A port label in its two drawable forms: the whole label, and the port's name without the type. */
export interface PortLabelText {
  full: string
  name: string
}

export interface FittedPortLabelRow extends PortLabelRowLayout {
  input: string | undefined
  output: string | undefined
}

const fits = (text: string, room: number) => estimatePortLabelWidth(text) * ESTIMATE_MARGIN <= room + ROUNDING_TOLERANCE

/** The longest form of a label that fits `room`: the whole label, else the name alone, else the name
 * shortened to an ellipsis, keeping a required input's mark. A label is never cut mid-glyph by the node edge. */
export function fitPortLabel(label: PortLabelText, room: number): string {
  if (fits(label.full, room)) return label.full
  if (fits(label.name, room)) return label.name
  const mark = label.name.endsWith(REQUIRED_MARK) ? REQUIRED_MARK : ''
  const glyphs = [...label.name.slice(0, label.name.length - mark.length)]
  for (let keep = glyphs.length - 1; keep > 1; keep--) {
    const shortened = glyphs.slice(0, keep).join('').trimEnd() + ELLIPSIS + mark
    if (fits(shortened, room)) return shortened
  }
  return glyphs.slice(0, 1).join('') + ELLIPSIS + mark
}

/** Lays out one port row and chooses the text each side draws. A side whose whole label does not fit
 * its share falls back to the port name; the row is then shared out again, and a side gets its whole
 * label back when the room the other side freed is enough for it. */
export function layoutFittedPortLabelRow(
  input: PortLabelText | undefined, output: PortLabelText | undefined, nodeWidth: number,
): FittedPortLabelRow {
  const rowEnd = nodeWidth - PORT_LABEL_INSET
  const layout = (inputText: string | undefined, outputText: string | undefined) => {
    const row = layoutPortLabelRow(inputText, outputText, nodeWidth)
    return { row, inputRoom: row.inputEnd - PORT_LABEL_INSET, outputRoom: rowEnd - row.outputStart }
  }
  const whole = layout(input?.full, output?.full)
  let inputText = input === undefined || fits(input.full, whole.inputRoom) ? input?.full : input.name
  let outputText = output === undefined || fits(output.full, whole.outputRoom) ? output?.full : output.name
  if (input !== undefined && inputText !== input.full) {
    const tried = layout(input.full, outputText)
    if (fits(input.full, tried.inputRoom) && (outputText === undefined || fits(outputText, tried.outputRoom))) inputText = input.full
  }
  if (output !== undefined && outputText !== output.full) {
    const tried = layout(inputText, output.full)
    if (fits(output.full, tried.outputRoom) && (inputText === undefined || fits(inputText, tried.inputRoom))) outputText = output.full
  }
  const { row, inputRoom, outputRoom } = layout(inputText, outputText)
  return {
    ...row,
    input: input === undefined ? undefined : fitPortLabel({ full: inputText ?? input.full, name: input.name }, inputRoom),
    output: output === undefined ? undefined : fitPortLabel({ full: outputText ?? output.full, name: output.name }, outputRoom),
  }
}
