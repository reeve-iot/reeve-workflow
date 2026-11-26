import { describe, expect, it } from 'vitest'
import { PORT_LABEL_GAP, PORT_LABEL_INSET, estimatePortLabelWidth, layoutPortLabelRow } from './portLabelLayout.js'

const NODE = 220
const inputRoom = (input: string, output?: string) => layoutPortLabelRow(input, output, NODE).inputEnd - PORT_LABEL_INSET
const outputRoom = (output: string, input?: string) => NODE - PORT_LABEL_INSET - layoutPortLabelRow(input, output, NODE).outputStart

describe('estimatePortLabelWidth', () => {
  it('measures narrow glyphs narrower than wide ones', () => {
    expect(estimatePortLabelWidth('iiii')).toBeLessThan(estimatePortLabelWidth('MMMM'))
  })

  it('counts a wide-script glyph wider than a Latin one', () => {
    expect(estimatePortLabelWidth('温度')).toBeGreaterThan(estimatePortLabelWidth('ab'))
  })

  it('grows with the font size', () => {
    expect(estimatePortLabelWidth('Value', 22)).toBeCloseTo(2 * estimatePortLabelWidth('Value', 11))
  })
})

describe('layoutPortLabelRow', () => {
  const long = 'Value* (T (unbound))'
  const longer = 'Previous Log ID (String)'

  it('gives a lone input the whole row, so a label longer than half the node is not cut', () => {
    expect(estimatePortLabelWidth(longer)).toBeGreaterThan((NODE - 2 * PORT_LABEL_INSET) / 2)
    expect(inputRoom(longer)).toBeGreaterThanOrEqual(estimatePortLabelWidth(longer))
  })

  it('gives a lone output the whole row', () => {
    expect(outputRoom(longer)).toBeGreaterThanOrEqual(estimatePortLabelWidth(longer))
  })

  it('keeps both labels whole when together they fit, with the gap between them', () => {
    const row = layoutPortLabelRow('Device*', 'Status (String)', NODE)
    expect(row.inputEnd - PORT_LABEL_INSET).toBeGreaterThanOrEqual(estimatePortLabelWidth('Device*'))
    expect(NODE - PORT_LABEL_INSET - row.outputStart).toBeGreaterThanOrEqual(estimatePortLabelWidth('Status (String)'))
    expect(row.outputStart - row.inputEnd).toBe(PORT_LABEL_GAP)
  })

  it('cuts only the long label when a short one shares its row', () => {
    expect(outputRoom('Done', long)).toBeGreaterThanOrEqual(estimatePortLabelWidth('Done'))
    expect(inputRoom(long, 'Done')).toBeGreaterThan(inputRoom(long, longer))
    expect(inputRoom('Done', long)).toBeGreaterThanOrEqual(estimatePortLabelWidth('Done'))
    expect(outputRoom(long, 'Done')).toBeGreaterThan(outputRoom(long, longer))
  })

  it('splits the row evenly when neither label fits half of it', () => {
    const row = layoutPortLabelRow(longer, longer, NODE)
    expect(row.inputEnd - PORT_LABEL_INSET).toBeCloseTo(NODE - PORT_LABEL_INSET - row.outputStart)
  })
})
