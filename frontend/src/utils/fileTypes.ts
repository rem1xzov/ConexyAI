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
  };
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
