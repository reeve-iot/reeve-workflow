import { createRoot } from 'react-dom/client'
import { PlaygroundPage } from './PlaygroundPage.js'

const container = document.getElementById('root')
if (container === null) throw new Error('index.html declares no #root to mount the playground into.')

createRoot(container).render(<PlaygroundPage />)
