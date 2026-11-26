import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import type { WorkflowEdge, WorkflowNode } from '@reeve-iot/workflow-core'
import { Connection } from './Connection.js'
import { EditorColorsContext } from '../theme/editorColors.js'
import type { EditorColors } from '../theme/editorColors.js'

const nodes: WorkflowNode[] = [
  { id: 'a', blockTypeId: 'X', position: { x: 0, y: 0 }, configuration: {} },
  { id: 'b', blockTypeId: 'X', position: { x: 400, y: 120 }, configuration: {} },
]
const edge: WorkflowEdge = { id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {} }

const draw = (props: Partial<{ isSelected: boolean; onClick: () => void; nodes: WorkflowNode[] }> = {}) =>
  render(
    <ThemeProvider theme={createTheme()}>
      <svg>
        <Connection edge={edge} nodes={props.nodes ?? nodes} isSelected={props.isSelected ?? false} onClick={props.onClick ?? (() => {})} />
      </svg>
    </ThemeProvider>,
  )

const wire = () => screen.getByTestId('workflow-edge-a-b').querySelector('path')

describe('Connection', () => {
  it('is findable by the pair of nodes it joins', () => {
    draw()
    expect(screen.getByTestId('workflow-edge-a-b')).toBeInTheDocument()
  })

  it('leaves the source at its right edge and enters the target at its left', () => {
    draw()
    expect(wire()?.getAttribute('d')).toMatch(/^M 220 \d+ C .* 400 \d+$/)
  })

  it('draws nothing when an endpoint is missing rather than throwing', () => {
    draw({ nodes: [nodes[0]!] })
    expect(wire()?.getAttribute('d')).toBe('')
  })

  it('thickens when selected, so selection survives a colour-blind palette', () => {
    draw({ isSelected: true })
    expect(wire()?.getAttribute('stroke-width')).toBe('4')
  })

  it('reports clicks', async () => {
    const onClick = vi.fn()
    draw({ onClick })
    await userEvent.click(screen.getByTestId('workflow-edge-a-b'))
    expect(onClick).toHaveBeenCalledOnce()
  })

  it('reports a click on the band laid over the line, which follows the line as its nodes move', async () => {
    const onClick = vi.fn()
    const view = draw({ onClick })
    const hit = screen.getByTestId('workflow-edge-hit-a-b')
    expect(screen.getByTestId('workflow-edge-a-b').lastElementChild).toBe(hit)
    const moved = nodes.map((n) => (n.id === 'b' ? { ...n, position: { x: 600, y: 300 } } : n))
    view.rerender(
      <ThemeProvider theme={createTheme()}>
        <svg>
          <Connection edge={edge} nodes={moved} isSelected={false} onClick={onClick} />
        </svg>
      </ThemeProvider>,
    )
    expect(hit.getAttribute('d')).toMatch(/ 600 \d+$/)
    expect(hit.getAttribute('d')).toBe(wire()?.getAttribute('d'))
    await userEvent.click(hit)
    expect(onClick).toHaveBeenCalledOnce()
  })

  it('sets pointer-events to stroke values on the line and the band, not to a value that includes fill', () => {
    draw()
    expect(wire()?.getAttribute('pointer-events')).toBe('visibleStroke')
    expect(screen.getByTestId('workflow-edge-hit-a-b').getAttribute('pointer-events')).toBe('stroke')
  })

  it('marks its band as a non-scaling stroke', () => {
    draw()
    expect(screen.getByTestId('workflow-edge-hit-a-b').getAttribute('vector-effect')).toBe('non-scaling-stroke')
  })

  it('paints with literal colours a host supplied, since SVG cannot resolve CSS variables', () => {
    const colors = { edge: '#123456', selected: '#abcdef' } as EditorColors
    render(
      <ThemeProvider theme={createTheme()}>
        <EditorColorsContext.Provider value={colors}>
          <svg>
            <Connection edge={edge} nodes={nodes} isSelected={false} onClick={() => {}} />
          </svg>
        </EditorColorsContext.Provider>
      </ThemeProvider>,
    )
    expect(wire()?.getAttribute('stroke')).toBe('#123456')
  })
})
