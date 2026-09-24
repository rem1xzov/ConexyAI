/**
 * Lightweight, dependency-free file-type icons for the tree and tabs.
 *
 * Each extension maps to a recognizable monogram + colour drawn inside a
 * document glyph; folders keep the classic folder shape. Unknown types fall
 * back to a generic document.
 */

interface FileTypeIconProps {
  path: string;
  isFolder?: boolean;
  size?: number;
  className?: string;
}

interface IconMeta {
  label: string;
  color: string;
}

function metaFor(path: string): IconMeta {
  const ext = path.split('.').pop()?.toLowerCase() ?? '';

  if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'bmp', 'ico'].includes(ext)) {
    return { label: '▣', color: '#c98bd4' };
  }

  const map: Record<string, IconMeta> = {
    cs: { label: 'C#', color: '#a78bfa' },
    csproj: { label: '◆', color: '#a78bfa' },
    sln: { label: '◈', color: '#a78bfa' },
    ts: { label: 'TS', color: '#60a5fa' },
    tsx: { label: 'TS', color: '#60a5fa' },
    js: { label: 'JS', color: '#fbbf24' },
    jsx: { label: 'JS', color: '#fbbf24' },
    json: { label: '{}', color: '#e6c07b' },
    md: { label: 'M↓', color: '#7db4a0' },
    html: { label: '<>', color: '#fb923c' },
    htm: { label: '<>', color: '#fb923c' },
    css: { label: '#', color: '#93c5fd' },
    yml: { label: 'Y', color: '#a79bae' },
    yaml: { label: 'Y', color: '#a79bae' },
    xml: { label: 'X', color: '#a79bae' },
    sh: { label: '>_', color: '#4ade80' },
    py: { label: 'PY', color: '#4ade80' },
    rs: { label: 'RS', color: '#fb923c' },
    go: { label: 'GO', color: '#60a5fa' },
    java: { label: 'JV', color: '#fb923c' },
    rb: { label: 'RB', color: '#f87171' },
    php: { label: 'PH', color: '#a5b4fc' },
    sql: { label: 'DB', color: '#e6c07b' },
    // FILE_CARDS: добавлено 2026-09-24 — documents the agent produces get their familiar colours.
    docx: { label: 'W', color: '#4f8ef7' },
    doc: { label: 'W', color: '#4f8ef7' },
    odt: { label: 'W', color: '#4f8ef7' },
    rtf: { label: 'W', color: '#4f8ef7' },
    xlsx: { label: 'X', color: '#22b365' },
    xls: { label: 'X', color: '#22b365' },
    ods: { label: 'X', color: '#22b365' },
    csv: { label: 'CSV', color: '#22b365' },
    tsv: { label: 'TSV', color: '#22b365' },
    pptx: { label: 'P', color: '#f07c3c' },
    ppt: { label: 'P', color: '#f07c3c' },
    odp: { label: 'P', color: '#f07c3c' },
    pdf: { label: 'PDF', color: '#ef5350' },
    txt: { label: 'TXT', color: '#a79bae' },
    zip: { label: 'ZIP', color: '#d8a657' },
    kt: { label: 'KT', color: '#a78bfa' },
    swift: { label: 'SW', color: '#fb923c' },
    c: { label: 'C', color: '#60a5fa' },
    cpp: { label: 'C+', color: '#60a5fa' },
    h: { label: 'H', color: '#60a5fa' },
    ps1: { label: '>_', color: '#60a5fa' },
    scss: { label: '#', color: '#f472b6' },
    toml: { label: 'T', color: '#a79bae' },
  };

  return map[ext] ?? { label: '·', color: '#a79bae' };
}

export function FileTypeIcon({ path, isFolder, size = 14, className }: FileTypeIconProps) {
  if (isFolder) {
    return (
      <svg
        className={className}
        width={size}
        height={size}
        viewBox="0 0 24 24"
        fill="none"
        stroke="#d8a657"
        strokeWidth={1.5}
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
      </svg>
    );
  }

  const { label, color } = metaFor(path);

  return (
    <svg
      className={className}
      width={size}
      height={size}
      viewBox="0 0 16 16"
      aria-hidden="true"
    >
      <path
        d="M4 1.5h5.5L13 5v8a1.5 1.5 0 0 1-1.5 1.5h-7A1.5 1.5 0 0 1 3 13V3a1.5 1.5 0 0 1 1-1.42Z"
        fill="none"
        stroke={color}
        strokeWidth={1.1}
        strokeLinejoin="round"
      />
      <text
        x="8"
        y="11.4"
        textAnchor="middle"
        fontSize={label.length > 2 ? 5 : label.length > 1 ? 6 : 7}
        fontFamily="ui-monospace, SFMono-Regular, Menlo, monospace"
        fontWeight="700"
        fill={color}
      >
        {label}
      </text>
    </svg>
  );
}
