/**
 * Minimal, dependency-free syntax highlighter for the Workspace code viewer.
 * It escapes HTML first and then wraps recognized tokens in spans, so the
 * output is always safe to inject via `dangerouslySetInnerHTML`.
 */

const KEYWORDS = new Set([
  // JavaScript / TypeScript
  'const', 'let', 'var', 'function', 'return', 'if', 'else', 'for', 'while', 'do',
  'switch', 'case', 'break', 'continue', 'new', 'class', 'extends', 'super', 'this',
  'typeof', 'instanceof', 'in', 'of', 'try', 'catch', 'finally', 'throw', 'async',
  'await', 'yield', 'import', 'from', 'export', 'default', 'interface', 'type',
  'enum', 'implements', 'public', 'private', 'protected', 'static', 'readonly',
  'void', 'null', 'undefined', 'true', 'false', 'delete', 'namespace', 'declare',
  'abstract', 'readonly', 'keyof', 'as', 'satisfies', 'is', 'infer',
  // C#
  'using', 'internal', 'sealed', 'partial', 'virtual', 'override', 'record', 'get',
  'set', 'value', 'string', 'int', 'long', 'double', 'decimal', 'bool', 'object',
  'base', 'when', 'ref', 'out', 'params', 'where', 'nameof', 'operator', 'explicit',
  'implicit', 'delegate', 'event', 'struct', 'checked', 'unchecked', 'fixed',
  // Python / general
  'def', 'lambda', 'None', 'and', 'or', 'not', 'elif', 'pass', 'raise', 'with',
  'global', 'nonlocal', 'self', 'cls', 'del', 'assert', 'async', 'await',
]);

const BUILTINS = new Set([
  'console', 'Math', 'JSON', 'Promise', 'Array', 'Object', 'String', 'Number',
  'Boolean', 'Date', 'RegExp', 'Map', 'Set', 'Error', 'Symbol', 'BigInt', 'Proxy',
  'Reflect', 'Intl', 'parseInt', 'parseFloat', 'isNaN',
  'System', 'Console', 'Task', 'Guid', 'DateTime', 'IEnumerable', 'List',
  'Dictionary', 'File', 'Directory', 'Path', 'Environment', 'ArgumentNullException',
  'NotImplementedException', 'InvalidOperationException', 'async', 'await',
  'print', 'len', 'range', 'str', 'int', 'float', 'dict', 'list', 'set', 'tuple',
  'enumerate', 'zip', 'map', 'filter', 'isinstance', 'getattr', 'hasattr', 'super',
]);

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

function isHashCommentLang(lang: string): boolean {
  const normalized = lang.toLowerCase();
  return ['py', 'python', 'sh', 'bash', 'shell', 'yaml', 'yml', 'toml', 'rb', 'ruby', 'pl', 'perl', 'r', 'dockerfile', 'makefile'].includes(normalized);
}

function isHtmlLang(lang: string): boolean {
  const normalized = lang.toLowerCase();
  return ['html', 'xml', 'vue', 'svg', 'markdown', 'md'].includes(normalized);
}

function detectLang(fileName: string): string {
  const ext = fileName.split('.').pop()?.toLowerCase() ?? '';
  const map: Record<string, string> = {
    ts: 'ts', tsx: 'tsx', js: 'js', jsx: 'jsx', json: 'json',
    cs: 'cs', py: 'py', html: 'html', htm: 'html', css: 'css',
    xml: 'xml', svg: 'svg', md: 'markdown', sh: 'sh', yml: 'yaml',
    yaml: 'yaml', sql: 'sql', csproj: 'xml', sln: 'text',
  };
  return map[ext] ?? 'text';
}

export function highlight(code: string, fileName: string): string {
  const lang = detectLang(fileName);
  const hashComments = isHashCommentLang(lang);
  const htmlComments = isHtmlLang(lang);

  const commentPattern = htmlComments
    ? '<!--[\\s\\S]*?-->'
    : hashComments
      ? '#[^\\n]*'
      : '//[^\\n]*|/\\*[\\s\\S]*?\\*/';

  const pattern = new RegExp(
    `(${commentPattern})|("(?:[^"\\\\\\n]|\\\\.)*"|'(?:[^'\\\\\\n]|\\\\.)*'|\`(?:[^\`\\\\]|\\\\.)*\`)|\\b(\\d+(?:\\.\\d+)?)\\b|\\b([A-Za-z_$][\\w$]*)\\b`,
    'g',
  );

  let out = '';
  let last = 0;
  let match: RegExpExecArray | null;

  while ((match = pattern.exec(code)) !== null) {
    out += escapeHtml(code.slice(last, match.index));

    const [full, comment, str, num, ident] = match;
    if (comment !== undefined) {
      out += `<span class="tok-comment">${escapeHtml(comment)}</span>`;
    } else if (str !== undefined) {
      out += `<span class="tok-string">${escapeHtml(str)}</span>`;
    } else if (num !== undefined) {
      out += `<span class="tok-number">${escapeHtml(num)}</span>`;
    } else if (ident !== undefined) {
      if (KEYWORDS.has(ident)) {
        out += `<span class="tok-keyword">${escapeHtml(ident)}</span>`;
      } else if (BUILTINS.has(ident)) {
        out += `<span class="tok-builtin">${escapeHtml(ident)}</span>`;
      } else {
        out += escapeHtml(ident);
      }
    }

    last = match.index + full.length;
  }

  out += escapeHtml(code.slice(last));
  return out;
}
