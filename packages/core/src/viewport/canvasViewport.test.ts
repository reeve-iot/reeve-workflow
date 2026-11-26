import { describe, expect, it } from 'vitest'
import { computeCenteringPan } from './canvasViewport.js'

const node = { x: 100, y: 100 }
const size = { width: 220, height: 120 }
const viewport = { width: 800, height: 600 }

describe('computeCenteringPan', () => {
  it('places the pan so the node centre lands at the viewport centre', () => {
    expect(computeCenteringPan(node, size, viewport, 1)).toEqual({ x: 190, y: 140 })
  })

  it('scales the node centre by the zoom before centering', () => {
    expect(computeCenteringPan(node, size, viewport, 2)).toEqual({ x: -20, y: -20 })
  })

  it('centres on the viewport it was handed, not on the window', () => {
    expect(computeCenteringPan(node, size, { width: 500, height: 600 }, 1).x).toBe(40)
  })
})
