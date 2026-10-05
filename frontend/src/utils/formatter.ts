import type { Options } from 'prettier';

// FORMATTER: добавлено 2026-10-05 — форматирование Prettier целиком во фронтенде (без песочницы,
// значит работает и в проде, и мгновенно). Парсеры/плагины подключены ленивыми dynamic import, поэтому
// в основной бандл они не попадают и грузятся только при первом форматировании нужного языка.
//
// Поддерживаются языки самого Prettier (JS/TS/JSON/CSS/SCSS/Less/HTML/Vue/Markdown/YAML/GraphQL).
// C#/Python/Go/Rust и прочее Prettier не форматирует — для них возвращаем null и содержимое не трогаем.

type PluginLoader = () => Promise<unknown>;

const PLUGINS: Record<string, PluginLoader> = {
  babel: () => import('prettier/plugins/babel'),
  estree: () => import('prettier/plugins/estree'),
  typescript: () => import('prettier/plugins/typescript'),
  postcss: () => import('prettier/plugins/postcss'),
  html: () => import('prettier/plugins/html'),
  markdown: () => import('prettier/plugins/markdown'),
  yaml: () => import('prettier/plugins/yaml'),
  graphql: () => import('prettier/plugins/graphql'),
};

interface FormatterTarget {
  parser: string;
  plugins: PluginLoader[];
}

/** Language ids Prettier can handle; used to register the Monaco formatting provider. */
export const FORMATTER_LANGUAGES = [
  'javascript',
  'typescript',
  'json',
  'css',
  'scss',
  'less',
  'html',
  'markdown',
  'yaml',
  'graphql',
];

function resolveTarget(path: string): FormatterTarget | null {
  const ext = (path.split('.').pop() ?? '').toLowerCase();
  switch (ext) {
    case 'js':
    case 'jsx':
    case 'mjs':
    case 'cjs':
      return { parser: 'babel', plugins: [PLUGINS.babel, PLUGINS.estree] };
    case 'ts':
    case 'tsx':
    case 'mts':
    case 'cts':
      return { parser: 'typescript', plugins: [PLUGINS.typescript, PLUGINS.estree] };
    case 'json':
    case 'jsonc':
      return { parser: 'json', plugins: [PLUGINS.babel, PLUGINS.estree] };
    case 'css':
    case 'scss':
    case 'less':
      return { parser: 'css', plugins: [PLUGINS.postcss] };
    case 'html':
    case 'htm':
    case 'vue':
      return {
        parser: 'html',
        plugins: [PLUGINS.html, PLUGINS.babel, PLUGINS.estree, PLUGINS.postcss, PLUGINS.typescript],
      };
    case 'md':
    case 'markdown':
      return { parser: 'markdown', plugins: [PLUGINS.markdown] };
    case 'yml':
    case 'yaml':
      return { parser: 'yaml', plugins: [PLUGINS.yaml] };
    case 'graphql':
    case 'gql':
      return { parser: 'graphql', plugins: [PLUGINS.graphql] };
    default:
      return null;
  }
}

/** True when Prettier has a parser for this file. */
export function formatterSupports(path: string): boolean {
  return resolveTarget(path) !== null;
}

// A single project-wide style. Kept close to the codebase's own conventions (2 spaces, single quotes).
const DEFAULT_OPTIONS = {
  printWidth: 120,
  tabWidth: 2,
  useTabs: false,
  singleQuote: true,
  semi: true,
  trailingComma: 'all',
  bracketSpacing: true,
  arrowParens: 'always',
  endOfLine: 'lf',
} as const;

/**
 * Formats the source with Prettier. Returns the formatted text, or null when the file type is not
 * supported or the input does not parse (an unparseable file is left untouched, never corrupted).
 */
export async function formatCode(path: string, code: string): Promise<string | null> {
  const target = resolveTarget(path);
  if (!target) return null;

  try {
    const prettier = await import('prettier/standalone');
    const plugins = await Promise.all(target.plugins.map((load) => load()));
    const options = { ...DEFAULT_OPTIONS, parser: target.parser, plugins } as unknown as Options;
    return await prettier.format(code, options);
  } catch {
    return null;
  }
}
