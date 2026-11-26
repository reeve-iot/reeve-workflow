import { createContext, useContext } from 'react'
import type { Theme } from '@mui/material/styles'
import { alpha, useTheme } from '@mui/material/styles'

/** The canvas paints with SVG presentation attributes (`fill=`, `stroke=`), which do NOT resolve
 * `var(--…)`. A theme built with MUI's CSS-variables mode yields exactly those, so such a host must
 * supply literal colours through EditorColorsProvider rather than rely on the derived default. */
export interface EditorColors {
  canvasBg: string
  grid: string
  nodeFill: string
  nodeHeader: string
  nodeStroke: string
  text: string
  textDim: string
  selected: string
  edge: string
  ok: string
  warn: string
  danger: string
  info: string
  /** Background tint for the sub-canvas affordance on a node. */
  infoBg: string
  infoBorder: string
  /** Header tint of the two synthetic nodes. */
  headerInput: string
  headerOutput: string
}

export function deriveEditorColors(theme: Theme): EditorColors {
  const p = theme.palette
  return {
    canvasBg: p.background.default,
    grid: p.divider,
    nodeFill: p.background.paper,
    nodeHeader: p.action.hover,
    nodeStroke: p.divider,
    text: p.text.primary,
    textDim: p.text.secondary,
    selected: p.primary.main,
    edge: p.text.secondary,
    ok: p.success.main,
    warn: p.warning.main,
    danger: p.error.main,
    info: p.info.main,
    infoBg: alpha(p.info.main, 0.08),
    infoBorder: alpha(p.info.main, 0.3),
    headerInput: p.success.main,
    headerOutput: p.error.main,
  }
}

export const EditorColorsContext = createContext<EditorColors | null>(null)

/** Colours for the canvas: whatever the host provided, else derived from the active MUI theme. */
export function useEditorColors(): EditorColors {
  const theme = useTheme()
  return useContext(EditorColorsContext) ?? deriveEditorColors(theme)
}
