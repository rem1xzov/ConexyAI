/** Maps a file path to a Monaco language id for syntax highlighting. */
export function detectLanguageFromExtension(path: string): string {
  const ext = path.split('.').pop()?.toLowerCase() ?? '';
  const map: Record<string, string> = {
    cs: 'csharp',
    ts: 'typescript',
    tsx: 'typescript',
    js: 'javascript',
    jsx: 'javascript',
    json: 'json',
    html: 'html',
    css: 'css',
    md: 'markdown',
    yml: 'yaml',
    yaml: 'yaml',
    xml: 'xml',
    sql: 'sql',
    sh: 'shell',
    py: 'python',
    csproj: 'xml',
    props: 'xml',
    target: 'xml',
    php: 'php',
    rs: 'rust',
    go: 'go',
    java: 'java',
    rb: 'ruby',
    // RENDER_HL: добавлено 2026-09-24 — ещё несколько языков, которые Monaco знает из коробки.
    kt: 'kotlin',
    kts: 'kotlin',
    swift: 'swift',
    c: 'c',
    h: 'c',
    cpp: 'cpp',
    cc: 'cpp',
    hpp: 'cpp',
    ps1: 'powershell',
    scss: 'scss',
    less: 'less',
    ini: 'ini',
    dockerfile: 'dockerfile',
    mmd: 'markdown',
  };
  const name = path.split('/').pop()?.toLowerCase() ?? '';
  if (name === 'dockerfile' || name.startsWith('dockerfile.')) return 'dockerfile';
  return map[ext] ?? 'plaintext';
}

export type FileKind = 'code' | 'image' | 'json' | 'markdown' | 'text';

/** Rough file category used to choose a tree/tab icon and tint. */
export function getFileKind(path: string): FileKind {
  const ext = path.split('.').pop()?.toLowerCase() ?? '';
  if (['cs', 'ts', 'tsx', 'js', 'jsx', 'py', 'sh', 'sql', 'css', 'html', 'xml', 'yml', 'yaml', 'php', 'rs', 'go', 'java', 'rb'].includes(ext)) {
    return 'code';
  }
  if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'bmp', 'ico'].includes(ext)) return 'image';
  if (ext === 'json') return 'json';
  if (ext === 'md') return 'markdown';
  return 'text';
}
