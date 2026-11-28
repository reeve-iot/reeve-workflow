import { describe, expect, it } from 'vitest'
import { PORT_LABEL_GAP, PORT_LABEL_INSET, estimatePortLabelWidth, fitPortLabel, layoutFittedPortLabelRow, layoutPortLabelRow } from './portLabelLayout.js'

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

describe('fitPortLabel', () => {
  const label = { full: 'Condition* (Boolean)', name: 'Condition*' }

  it('keeps the whole label when it fits', () => {
    expect(fitPortLabel(label, 200)).toBe('Condition* (Boolean)')
  })

  it('drops the type before it cuts the name', () => {
    const room = estimatePortLabelWidth('Condition*') * 1.2
    expect(fitPortLabel(label, room)).toBe('Condition*')
  })

  it('shortens a name that does not fit to an ellipsis that does', () => {
    const room = estimatePortLabelWidth('Condit') * 1.2
    const drawn = fitPortLabel(label, room)
    expect(drawn).toMatch(/^Co.*\u2026\*$/)
    expect(estimatePortLabelWidth(drawn)).toBeLessThanOrEqual(room)
  })

  it('keeps at least one glyph, the ellipsis and the required mark in a room too small for anything', () => {
    expect(fitPortLabel(label, 1)).toBe('C\u2026*')
  })

  it('keeps the required mark after the ellipsis', () => {
    const required = { full: 'Very long required input name* (Number)', name: 'Very long required input name*' }
    const drawn = fitPortLabel(required, estimatePortLabelWidth('Very long requ') * 1.2)
    expect(drawn).toMatch(/\u2026\*$/)
  })
})

describe('layoutFittedPortLabelRow', () => {
  const text = (name: string, type: string) => ({ full: `${name} (${type})`, name })
  const drawnWithin = (row: ReturnType<typeof layoutFittedPortLabelRow>) => {
    if (row.input !== undefined) expect(estimatePortLabelWidth(row.input)).toBeLessThanOrEqual(row.inputEnd - PORT_LABEL_INSET)
    if (row.output !== undefined) expect(estimatePortLabelWidth(row.output)).toBeLessThanOrEqual(NODE - PORT_LABEL_INSET - row.outputStart)
  }

  it('draws both whole labels when they fit together', () => {
    const row = layoutFittedPortLabelRow(text('Device*', 'Device'), text('Done', 'Flow'), NODE)
    expect(row.input).toBe('Device* (Device)')
    expect(row.output).toBe('Done (Flow)')
  })

  it('drops the type of the side that does not fit and keeps the other whole', () => {
    const row = layoutFittedPortLabelRow(text('Radiator', 'Property<Number>'), text('Setpoint', 'Number'), NODE)
    expect(row.input).toBe('Radiator')
    expect(row.output).toBe('Setpoint (Number)')
    drawnWithin(row)
  })

  it('gives a side its whole label back when the other side dropping its type made room', () => {
    const row = layoutFittedPortLabelRow(text('Item', 'T (unbound)'), text('Many', 'Collection<T?> (unbound)'), NODE)
    expect(row.input).toBe('Item (T (unbound))')
    expect(row.output).toBe('Many')
    drawnWithin(row)
  })

  it('gives the other side its whole label back too', () => {
    const row = layoutFittedPortLabelRow(text('Many', 'Collection<T?> (unbound)'), text('Item', 'T (unbound)'), NODE)
    expect(row.input).toBe('Many')
    expect(row.output).toBe('Item (T (unbound))')
    drawnWithin(row)
  })

  it('keeps a short label whole beside a long one, whatever the rounding of its share', () => {
    const long = text('Temperature Setpoint*', 'Property<Number>')
    for (const short of ['Value (String)', 'Result (String)', 'Done* (Flow)', 'Input (Json)']) {
      const name = short.slice(0, short.indexOf(' ('))
      expect(layoutFittedPortLabelRow(long, { full: short, name }, NODE).output).toBe(short)
      expect(layoutFittedPortLabelRow({ full: short, name }, long, NODE).input).toBe(short)
    }
  })

  it('gives a lone side the whole row', () => {
    const row = layoutFittedPortLabelRow(undefined, text('Reading', 'Property<Number>'), NODE)
    expect(row.input).toBeUndefined()
    expect(row.output).toBe('Reading (Property<Number>)')
    drawnWithin(row)
  })
})
