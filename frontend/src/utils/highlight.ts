/**
 * Minimal, dependency-free syntax highlighter for chat code blocks and the artifact panel.
 *
 * RENDER_HL: переписано 2026-09-24 — раньше модуль строил HTML-строку для
 * `dangerouslySetInnerHTML` и нигде не использовался. Теперь он только режет код на токены
 * `{ type, text }`; разметку из них строит React (`HighlightedCode`), поэтому текст модели никогда
 * не попадает в innerHTML и подсветка безопасна по построению.
 *
 * The tokenizer is a single sticky-free regex scan per language: every language is described by
 * an ordered list of token rules; whatever no rule matches stays plain text. That keeps it tiny and
 * fast (one pass, no backtracking-heavy grammar), at the price of being a highlighter rather than a
 * parser — good enough for reading code in a chat.
 */

export type TokenType =
  | 'plain'
  | 'comment'
  | 'string'
  | 'number'
  | 'keyword'
  | 'builtin'
  | 'function'
  | 'tag'
  | 'attr'
  | 'property'
  | 'variable'
  | 'meta'
  | 'inserted'
  | 'deleted'
  | 'heading'
  | 'bold';

export interface Token {
  type: TokenType;
  text: string;
}

/** Above this size highlighting is skipped: the text is still shown, just without colours. */
const MAX_HIGHLIGHT_CHARS = 200_000;

// ---------------------------------------------------------------------------------------------
// Language aliases
// ---------------------------------------------------------------------------------------------

type LangId =
  | 'js'
  | 'ts'
  | 'json'
  | 'python'
  | 'csharp'
  | 'bash'
  | 'powershell'
  | 'markup'
  | 'css'
  | 'sql'
  | 'yaml'
  | 'toml'
  | 'go'
  | 'java'
  | 'kotlin'
  | 'rust'
  | 'c'
  | 'cpp'
  | 'php'
  | 'ruby'
  | 'swift'
  | 'dockerfile'
  | 'diff'
  | 'markdown';

const ALIASES: Record<string, LangId> = {
  js: 'js', javascript: 'js', jsx: 'js', mjs: 'js', cjs: 'js', node: 'js', es6: 'js',
  ts: 'ts', typescript: 'ts', tsx: 'ts', mts: 'ts', cts: 'ts',
  json: 'json', jsonc: 'json', json5: 'json', geojson: 'json',
  py: 'python', python: 'python', python3: 'python', py3: 'python', gyp: 'python',
  cs: 'csharp', csharp: 'csharp', 'c#': 'csharp', dotnet: 'csharp', csx: 'csharp',
  sh: 'bash', bash: 'bash', shell: 'bash', zsh: 'bash', shellscript: 'bash', console: 'bash', ksh: 'bash',
  ps: 'powershell', ps1: 'powershell', psm1: 'powershell', powershell: 'powershell', pwsh: 'powershell',
  html: 'markup', htm: 'markup', xhtml: 'markup', xml: 'markup', svg: 'markup', xaml: 'markup',
  csproj: 'markup', vue: 'markup', plist: 'markup', xsl: 'markup', rss: 'markup',
  css: 'css', scss: 'css', sass: 'css', less: 'css',
  sql: 'sql', mysql: 'sql', postgres: 'sql', postgresql: 'sql', pgsql: 'sql', plsql: 'sql',
  tsql: 'sql', sqlite: 'sql',
  yaml: 'yaml', yml: 'yaml',
  toml: 'toml', ini: 'toml', cfg: 'toml', conf: 'toml', properties: 'toml', env: 'toml', dotenv: 'toml',
  go: 'go', golang: 'go',
  java: 'java',
  kotlin: 'kotlin', kt: 'kotlin', kts: 'kotlin',
  rust: 'rust', rs: 'rust',
  c: 'c', h: 'c',
  cpp: 'cpp', 'c++': 'cpp', cc: 'cpp', cxx: 'cpp', hpp: 'cpp', hxx: 'cpp', arduino: 'cpp', ino: 'cpp',
  php: 'php',
  rb: 'ruby', ruby: 'ruby', gemfile: 'ruby',
  swift: 'swift',
  dockerfile: 'dockerfile', docker: 'dockerfile', containerfile: 'dockerfile',
  diff: 'diff', patch: 'diff', udiff: 'diff',
  md: 'markdown', markdown: 'markdown', mdx: 'markdown',
};

/** Canonical highlighter id for a fence info word / file extension, or null when unsupported. */
export function resolveLanguage(language: string | undefined | null): LangId | null {
  if (!language) return null;
  const key = language.trim().toLowerCase().replace(/^\./, '');
  return ALIASES[key] ?? null;
}

/** File extension for saving a snippet of the given language (e.g. "python" -> "py"). */
export function extensionForLanguage(language: string | undefined | null): string {
  const key = (language ?? '').trim().toLowerCase();
  const direct: Record<string, string> = {
    javascript: 'js', js: 'js', jsx: 'jsx', typescript: 'ts', ts: 'ts', tsx: 'tsx',
    python: 'py', py: 'py', python3: 'py', csharp: 'cs', 'c#': 'cs', cs: 'cs',
    bash: 'sh', sh: 'sh', shell: 'sh', zsh: 'sh', powershell: 'ps1', pwsh: 'ps1', ps1: 'ps1',
    html: 'html', xml: 'xml', svg: 'svg', css: 'css', scss: 'scss', less: 'less', sql: 'sql',
    yaml: 'yaml', yml: 'yml', toml: 'toml', ini: 'ini', json: 'json', go: 'go', golang: 'go',
    java: 'java', kotlin: 'kt', kt: 'kt', rust: 'rs', rs: 'rs', c: 'c', cpp: 'cpp', 'c++': 'cpp',
    php: 'php', ruby: 'rb', rb: 'rb', swift: 'swift', dockerfile: 'Dockerfile', diff: 'diff',
    patch: 'patch', markdown: 'md', md: 'md', mermaid: 'mmd', text: 'txt', plaintext: 'txt',
    txt: 'txt', r: 'r', lua: 'lua', dart: 'dart', scala: 'scala', vue: 'vue', graphql: 'graphql',
  };
  if (direct[key]) return direct[key];
  return /^[a-z0-9+#-]{1,10}$/.test(key) ? key.replace(/[^a-z0-9]/g, '') || 'txt' : 'txt';
}

// ---------------------------------------------------------------------------------------------
// Keyword sets
// ---------------------------------------------------------------------------------------------

const words = (list: string): Set<string> => new Set(list.split(/\s+/).filter(Boolean));

const JS_KEYWORDS = words(`
  break case catch class const continue debugger default delete do else export extends finally for
  from function if import in instanceof let new of return static super switch this throw try typeof
  var void while with yield async await get set as`);
const TS_KEYWORDS = words(`
  ${[...JS_KEYWORDS].join(' ')} interface type enum implements namespace declare abstract readonly
  keyof public private protected satisfies is infer module override unique asserts`);
const JS_LITERALS = words('true false null undefined NaN Infinity');
const JS_BUILTINS = words(`
  console window document globalThis Math JSON Promise Array Object String Number Boolean Date
  RegExp Map Set WeakMap WeakSet Error TypeError Symbol BigInt Proxy Reflect Intl parseInt
  parseFloat isNaN fetch setTimeout clearTimeout setInterval clearInterval require process module
  exports React useState useEffect useMemo useCallback useRef string number boolean any unknown
  never void object Record Partial Readonly Pick Omit`);

const PY_KEYWORDS = words(`
  and as assert async await break class continue def del elif else except finally for from global
  if import in is lambda nonlocal not or pass raise return try while with yield match case`);
const PY_LITERALS = words('True False None');
const PY_BUILTINS = words(`
  print len range str int float dict list set tuple bool bytes enumerate zip map filter isinstance
  getattr setattr hasattr super open type object sorted reversed sum min max abs any all input
  self cls Exception ValueError TypeError KeyError staticmethod classmethod property`);

const CS_KEYWORDS = words(`
  abstract as base break case catch checked class const continue default delegate do else enum event
  explicit extern finally fixed for foreach goto if implicit in interface internal is lock namespace
  new operator out override params private protected public readonly ref return sealed sizeof
  stackalloc static struct switch this throw try typeof unchecked unsafe using virtual volatile while
  async await var dynamic get set init value when where yield record partial required global nameof
  with file scoped not and or`);
const CS_BUILTINS = words(`
  bool byte sbyte char decimal double float int uint long ulong short ushort object string void
  nint nuint Console Task List Dictionary IEnumerable Guid DateTime String Math Exception`);
const C_LITERALS = words('true false null nullptr NULL');

const JAVA_KEYWORDS = words(`
  abstract assert break case catch class const continue default do else enum extends final finally
  for goto if implements import instanceof interface native new package private protected public
  return static strictfp super switch synchronized this throw throws transient try volatile while
  var record sealed permits yield non-sealed`);
const JAVA_BUILTINS = words(`
  boolean byte char double float int long short void String Object Integer Long Double List Map
  ArrayList HashMap System Math Exception Override`);

const KOTLIN_KEYWORDS = words(`
  as break class continue do else for fun if in interface is object package return super this throw
  try typealias typeof val var when while by catch constructor delegate dynamic field file finally
  get import init param property receiver set setparam where actual abstract annotation companion
  const crossinline data enum expect external final infix inline inner internal lateinit noinline
  open operator out override private protected public reified sealed suspend tailrec vararg`);
const KOTLIN_BUILTINS = words('Int Long Double Float Boolean String Unit Any Nothing List Map Set println listOf mapOf');

const GO_KEYWORDS = words(`
  break case chan const continue default defer else fallthrough for func go goto if import interface
  map package range return select struct switch type var`);
const GO_BUILTINS = words(`
  bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint
  uint8 uint16 uint32 uint64 uintptr any append cap close copy delete len make new panic print
  println recover fmt`);
const GO_LITERALS = words('true false nil iota');

const RUST_KEYWORDS = words(`
  as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod
  move mut pub ref return self Self static struct super trait type unsafe use where while`);
const RUST_BUILTINS = words(`
  i8 i16 i32 i64 i128 isize u8 u16 u32 u64 u128 usize f32 f64 bool char str String Vec Option Result
  Box Some None Ok Err`);
const RUST_LITERALS = words('true false');

const C_KEYWORDS = words(`
  auto break case char const continue default do double else enum extern float for goto if inline int
  long register restrict return short signed sizeof static struct switch typedef union unsigned void
  volatile while`);
const CPP_KEYWORDS = words(`
  ${[...C_KEYWORDS].join(' ')} alignas alignof and bool catch class concept consteval constexpr
  constinit const_cast co_await co_return co_yield decltype delete dynamic_cast explicit export
  friend mutable namespace new noexcept not operator or private protected public reinterpret_cast
  requires static_assert static_cast template this thread_local throw try typeid typename using
  virtual wchar_t override final`);
const CPP_BUILTINS = words('std string vector map cout cin endl printf malloc free size_t uint8_t int32_t int64_t');

const PHP_KEYWORDS = words(`
  abstract and array as break callable case catch class clone const continue declare default do echo
  else elseif empty enddeclare endfor endforeach endif endswitch endwhile enum extends final finally fn
  for foreach function global goto if implements include include_once instanceof insteadof interface
  isset list match namespace new or print private protected public readonly require require_once
  return static switch throw trait try unset use var while xor yield`);
const PHP_LITERALS = words('true false null TRUE FALSE NULL');

const RUBY_KEYWORDS = words(`
  alias and begin break case class def defined? do else elsif end ensure for if in module next not or
  redo rescue retry return self super then undef unless until when while yield require attr_accessor
  attr_reader private protected public puts`);
const RUBY_LITERALS = words('true false nil');

const SWIFT_KEYWORDS = words(`
  associatedtype class deinit enum extension fileprivate func import init inout internal let open
  operator private protocol public rethrows static struct subscript typealias var break case continue
  default defer do else fallthrough for guard if in repeat return switch where while as catch is
  throw throws try async await some any self Self super mutating override final lazy weak`);
const SWIFT_BUILTINS = words('Int Double Float Bool String Character Array Dictionary Set Optional print');
const SWIFT_LITERALS = words('true false nil');

const BASH_KEYWORDS = words(`
  if then else elif fi for while until do done case esac in function return select time local export
  readonly declare unset shift break continue exit source alias`);
const BASH_BUILTINS = words(`
  echo printf cd ls pwd cat grep sed awk find xargs mkdir rm cp mv chmod chown touch curl wget git
  npm npx yarn pnpm node python python3 pip pip3 dotnet docker kubectl sudo apt apt-get brew tar
  unzip ssh scp make cargo go java test read eval exec set trap kill ps tee head tail sort uniq wc`);

const PS_KEYWORDS = words(`
  begin break catch class continue data default do dynamicparam else elseif end exit filter finally
  for foreach from function if in param process return switch throw trap try until using var while
  workflow`);

const SQL_KEYWORDS = words(`
  select from where and or not insert into values update set delete create table alter drop index
  view primary key foreign references join inner left right full outer cross on as group by order
  having limit offset distinct union all exists in is null like between case when then else end
  begin commit rollback transaction grant revoke with recursive returning default constraint unique
  check asc desc if database schema procedure function trigger declare exec execute top fetch next
  rows only over partition natural using cascade`);
const SQL_BUILTINS = words(`
  count sum avg min max coalesce cast convert now current_timestamp int integer bigint smallint
  varchar nvarchar char text boolean bool date datetime timestamp decimal numeric float real serial
  uuid json jsonb`);
const SQL_LITERALS = words('true false null');

const DOCKER_KEYWORDS = words(`
  FROM RUN CMD LABEL MAINTAINER EXPOSE ENV ADD COPY ENTRYPOINT VOLUME USER WORKDIR ARG ONBUILD
  STOPSIGNAL HEALTHCHECK SHELL AS`);

const CSS_KEYWORDS = words('important');

// ---------------------------------------------------------------------------------------------
// Generic rule-based tokenizer
// ---------------------------------------------------------------------------------------------

interface Rule {
  type: TokenType | 'ident';
  /** Regex source WITHOUT capturing groups (use `(?:...)`), so group indexes stay aligned. */
  src: string;
}

interface Spec {
  rules: Rule[];
  keywords?: Set<string>;
  builtins?: Set<string>;
  literals?: Set<string>;
  /** Keyword lookup ignores case (SQL, PowerShell, Dockerfile). */
  caseInsensitive?: boolean;
  /** Capitalised identifiers read as types (C#, Java, TS...). */
  capitalizedTypes?: boolean;
  /** `name(` reads as a function call. */
  calls?: boolean;
}

const IDENT = '[A-Za-z_$][\\w$]*';
const NUMBER = '(?:0[xX][\\da-fA-F_]+|0[bB][01_]+|0[oO][0-7_]+|(?:\\d[\\d_]*\\.?[\\d_]*|\\.\\d[\\d_]*)(?:[eE][+-]?\\d+)?)[a-zA-Z]*';
const DQ_CLOSED = '"(?:[^"\\\\\\n]|\\\\[\\s\\S])*"';
const DQ = `${DQ_CLOSED}?`;
/** End of input. `$` cannot be used for that: the rules run with the `m` flag (for `^`). */
const EOF = '(?![\\s\\S])';
const SQ = "'(?:[^'\\\\\\n]|\\\\[\\s\\S])*'?";
const BT = '`(?:[^`\\\\]|\\\\[\\s\\S])*`?';
const C_BLOCK = `/\\*[\\s\\S]*?(?:\\*/|${EOF})`;
const C_LINE = '//[^\\n]*';
const HASH_LINE = '(?<![\\w$\\\\])#[^\\n]*';

function cLike(extra: Partial<Spec> & { strings?: string[]; pre?: Rule[] }): Spec {
  return {
    rules: [
      ...(extra.pre ?? []),
      { type: 'comment', src: `${C_BLOCK}|${C_LINE}` },
      { type: 'string', src: (extra.strings ?? [DQ, SQ]).join('|') },
      { type: 'number', src: `\\b${NUMBER}` },
      { type: 'ident', src: IDENT },
    ],
    calls: true,
    ...extra,
  };
}

const SPECS: Partial<Record<LangId, Spec>> = {
  js: cLike({
    strings: [DQ, SQ, BT],
    pre: [{ type: 'meta', src: '@[A-Za-z_][\\w.]*' }],
    keywords: JS_KEYWORDS,
    builtins: JS_BUILTINS,
    literals: JS_LITERALS,
    capitalizedTypes: true,
  }),
  ts: cLike({
    strings: [DQ, SQ, BT],
    pre: [{ type: 'meta', src: '@[A-Za-z_][\\w.]*' }],
    keywords: TS_KEYWORDS,
    builtins: JS_BUILTINS,
    literals: JS_LITERALS,
    capitalizedTypes: true,
  }),
  csharp: cLike({
    strings: ['@"(?:[^"]|"")*"?', '\\$@?"(?:[^"\\\\\\n]|\\\\[\\s\\S])*"?', '"""[\\s\\S]*?(?:"""|(?![\\s\\S]))', DQ, "'(?:[^'\\\\\\n]|\\\\.)'"],
    pre: [{ type: 'meta', src: '^[ \\t]*#[a-z]+[^\\n]*' }],
    keywords: CS_KEYWORDS,
    builtins: CS_BUILTINS,
    literals: C_LITERALS,
    capitalizedTypes: true,
  }),
  java: cLike({
    strings: ['"""[\\s\\S]*?(?:"""|(?![\\s\\S]))', DQ, "'(?:[^'\\\\\\n]|\\\\.)*'"],
    pre: [{ type: 'meta', src: '@[A-Za-z_][\\w.]*' }],
    keywords: JAVA_KEYWORDS,
    builtins: JAVA_BUILTINS,
    literals: C_LITERALS,
    capitalizedTypes: true,
  }),
  kotlin: cLike({
    strings: ['"""[\\s\\S]*?(?:"""|(?![\\s\\S]))', DQ, "'(?:[^'\\\\\\n]|\\\\.)*'"],
    pre: [{ type: 'meta', src: '@[A-Za-z_][\\w.]*' }],
    keywords: KOTLIN_KEYWORDS,
    builtins: KOTLIN_BUILTINS,
    literals: C_LITERALS,
    capitalizedTypes: true,
  }),
  go: cLike({
    strings: [DQ, "'(?:[^'\\\\\\n]|\\\\.)*'", '`[^`]*`?'],
    keywords: GO_KEYWORDS,
    builtins: GO_BUILTINS,
    literals: GO_LITERALS,
    capitalizedTypes: false,
  }),
  rust: cLike({
    strings: ['b?r#*"[\\s\\S]*?"#*', `b?${DQ}`, "b?'(?:[^'\\\\\\n]|\\\\.[^']*)'"],
    pre: [
      { type: 'meta', src: '#!?\\[[^\\]\\n]*\\]?' },
      { type: 'function', src: '[a-z_][\\w]*!(?=\\s*[([{])' },
      { type: 'meta', src: "'[a-z_]\\w*\\b(?!')" },
    ],
    keywords: RUST_KEYWORDS,
    builtins: RUST_BUILTINS,
    literals: RUST_LITERALS,
    capitalizedTypes: true,
  }),
  c: cLike({
    pre: [{ type: 'meta', src: '^[ \\t]*#[ \\t]*[a-z]+[^\\n]*' }],
    strings: [DQ, "'(?:[^'\\\\\\n]|\\\\.)*'"],
    keywords: C_KEYWORDS,
    builtins: CPP_BUILTINS,
    literals: C_LITERALS,
  }),
  cpp: cLike({
    pre: [{ type: 'meta', src: '^[ \\t]*#[ \\t]*[a-z]+[^\\n]*' }],
    strings: ['R"\\([\\s\\S]*?\\)"', DQ, "'(?:[^'\\\\\\n]|\\\\.)*'"],
    keywords: CPP_KEYWORDS,
    builtins: CPP_BUILTINS,
    literals: C_LITERALS,
    capitalizedTypes: true,
  }),
  swift: cLike({
    strings: ['"""[\\s\\S]*?(?:"""|(?![\\s\\S]))', DQ],
    pre: [{ type: 'meta', src: '[@#][A-Za-z_]\\w*' }],
    keywords: SWIFT_KEYWORDS,
    builtins: SWIFT_BUILTINS,
    literals: SWIFT_LITERALS,
    capitalizedTypes: true,
  }),
  php: {
    rules: [
      { type: 'meta', src: '<\\?(?:php|=)?|\\?>' },
      { type: 'comment', src: `${C_BLOCK}|${C_LINE}|${HASH_LINE}` },
      { type: 'string', src: `${DQ}|${SQ}` },
      { type: 'variable', src: '\\$[A-Za-z_]\\w*' },
      { type: 'number', src: `\\b${NUMBER}` },
      { type: 'ident', src: IDENT.replace('$', '') },
    ],
    keywords: PHP_KEYWORDS,
    literals: PHP_LITERALS,
    capitalizedTypes: true,
    calls: true,
    caseInsensitive: false,
  },
  python: {
    rules: [
      { type: 'comment', src: HASH_LINE },
      {
        type: 'string',
        src: `(?:[rRbBuUfF]{1,2})?(?:"""[\\s\\S]*?(?:"""|(?![\\s\\S]))|'''[\\s\\S]*?(?:'''|(?![\\s\\S]))|${DQ}|${SQ})`,
      },
      { type: 'meta', src: '^[ \\t]*@[A-Za-z_][\\w.]*' },
      { type: 'number', src: `\\b${NUMBER}` },
      { type: 'ident', src: '[A-Za-z_]\\w*' },
    ],
    keywords: PY_KEYWORDS,
    builtins: PY_BUILTINS,
    literals: PY_LITERALS,
    calls: true,
    capitalizedTypes: true,
  },
  ruby: {
    rules: [
      { type: 'comment', src: `^=begin[\\s\\S]*?(?:^=end|(?![\\s\\S]))|${HASH_LINE}` },
      { type: 'string', src: `${DQ}|${SQ}|%[qQwWi]?[({\\[][^)}\\]]*[)}\\]]` },
      { type: 'variable', src: '@@?[A-Za-z_]\\w*|\\$[A-Za-z_]\\w*' },
      { type: 'meta', src: '(?<![\\w:]):[A-Za-z_]\\w*[?!]?' },
      { type: 'number', src: `\\b${NUMBER}` },
      { type: 'ident', src: '[A-Za-z_]\\w*[?!]?' },
    ],
    keywords: RUBY_KEYWORDS,
    literals: RUBY_LITERALS,
    calls: true,
    capitalizedTypes: true,
  },
  bash: {
    rules: [
      { type: 'comment', src: HASH_LINE },
      { type: 'string', src: `${DQ}|'[^']*'?` },
      { type: 'variable', src: '\\$(?:\\{[^}\\n]*\\}?|[A-Za-z_]\\w*|[0-9@#?*!$-])' },
      { type: 'attr', src: '(?<=\\s)--?[A-Za-z][\\w-]*' },
      { type: 'number', src: '\\b\\d+(?:\\.\\d+)?\\b' },
      { type: 'ident', src: '[A-Za-z_][\\w.-]*' },
    ],
    keywords: BASH_KEYWORDS,
    builtins: BASH_BUILTINS,
  },
  powershell: {
    rules: [
      { type: 'comment', src: '<#[\\s\\S]*?(?:#>|(?![\\s\\S]))|#[^\\n]*' },
      { type: 'string', src: `@"[\\s\\S]*?(?:"@|(?![\\s\\S]))|@'[\\s\\S]*?(?:'@|(?![\\s\\S]))|"(?:[^"\`]|\`[\\s\\S])*"?|'(?:[^']|'')*'?` },
      { type: 'variable', src: '\\$(?:\\{[^}\\n]*\\}|[A-Za-z_][\\w:]*)' },
      { type: 'function', src: '\\b[A-Z][a-z]+-[A-Z][A-Za-z]+\\b' },
      { type: 'attr', src: '(?<=\\s)-[A-Za-z][\\w]*' },
      { type: 'number', src: '\\b\\d+(?:\\.\\d+)?\\b' },
      { type: 'ident', src: '[A-Za-z_][\\w]*' },
    ],
    keywords: PS_KEYWORDS,
    caseInsensitive: true,
  },
  sql: {
    rules: [
      { type: 'comment', src: `--[^\\n]*|${C_BLOCK}|(?<![\\w])#[^\\n]*` },
      { type: 'string', src: "'(?:[^']|'')*'?|\"(?:[^\"]|\"\")*\"?|`[^`]*`?|\\[[^\\]\\n]*\\]" },
      { type: 'variable', src: '[@:$][A-Za-z_]\\w*' },
      { type: 'number', src: '\\b\\d+(?:\\.\\d+)?\\b' },
      { type: 'ident', src: '[A-Za-z_]\\w*' },
    ],
    keywords: SQL_KEYWORDS,
    builtins: SQL_BUILTINS,
    literals: SQL_LITERALS,
    caseInsensitive: true,
    calls: true,
  },
  json: {
    rules: [
      { type: 'comment', src: `${C_BLOCK}|${C_LINE}` },
      { type: 'property', src: `${DQ_CLOSED}(?=\\s*:)` },
      { type: 'string', src: DQ },
      { type: 'number', src: '-?\\b\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?\\b' },
      { type: 'ident', src: '[A-Za-z_]\\w*' },
    ],
    literals: words('true false null'),
  },
  yaml: {
    rules: [
      { type: 'comment', src: HASH_LINE },
      { type: 'meta', src: '^(?:---|\\.\\.\\.)[ \\t]*$|![A-Za-z!]\\S*' },
      { type: 'property', src: `^[ \\t]*(?:-[ \\t]+)?(?:${DQ}|${SQ}|[^\\s#:'"\\-][^:#\\n]*?)(?=[ \\t]*:(?:\\s|$))` },
      { type: 'string', src: `${DQ}|${SQ}` },
      { type: 'variable', src: '[&*][A-Za-z_][\\w-]*' },
      { type: 'number', src: '(?<![\\w.-])-?\\d+(?:\\.\\d+)?(?![\\w.-])' },
      { type: 'ident', src: '[A-Za-z_][\\w-]*' },
    ],
    literals: words('true false null yes no on off True False Null ~'),
  },
  toml: {
    rules: [
      { type: 'comment', src: '(?<![\\w])[#;][^\\n]*' },
      { type: 'heading', src: '^[ \\t]*\\[\\[?[^\\]\\n]*\\]\\]?' },
      { type: 'property', src: '^[ \\t]*[A-Za-z0-9_.\\-"\']+(?=[ \\t]*=)' },
      { type: 'string', src: `"""[\\s\\S]*?(?:"""|(?![\\s\\S]))|${DQ}|'[^'\\n]*'?` },
      { type: 'number', src: '(?<![\\w.-])[+-]?\\d[\\d_:.TZ+-]*(?![\\w])' },
      { type: 'ident', src: '[A-Za-z_]\\w*' },
    ],
    literals: words('true false'),
  },
  dockerfile: {
    rules: [
      { type: 'comment', src: HASH_LINE },
      { type: 'keyword', src: '^[ \\t]*[A-Za-z]+(?=[ \\t])' },
      { type: 'string', src: `${DQ}|${SQ}` },
      { type: 'variable', src: '\\$(?:\\{[^}\\n]*\\}?|[A-Za-z_]\\w*)' },
      { type: 'attr', src: '(?<=\\s)--[A-Za-z][\\w-]*' },
      { type: 'number', src: '\\b\\d+(?:\\.\\d+)?\\b' },
      { type: 'ident', src: '[A-Za-z_]\\w*' },
    ],
    keywords: DOCKER_KEYWORDS,
    caseInsensitive: true,
  },
  css: {
    rules: [
      { type: 'comment', src: `${C_BLOCK}|(?<![:\\w])//[^\\n]*` },
      { type: 'string', src: `${DQ}|${SQ}` },
      { type: 'keyword', src: '@[A-Za-z-]+|!important' },
      { type: 'variable', src: '--[A-Za-z_][\\w-]*|\\$[A-Za-z_][\\w-]*' },
      { type: 'property', src: '-?[A-Za-z][\\w-]*(?=\\s*:[^;{}]*[;}])' },
      { type: 'number', src: '#[\\da-fA-F]{3,8}\\b|-?(?:\\d+\\.?\\d*|\\.\\d+)(?:%|[A-Za-z]+)?' },
      { type: 'function', src: '[A-Za-z-]+(?=\\()' },
      { type: 'tag', src: '(?<![\\w-])[.#][A-Za-z_][\\w-]*' },
      { type: 'ident', src: '[A-Za-z_][\\w-]*' },
    ],
    keywords: CSS_KEYWORDS,
  },
};

interface CompiledSpec {
  regex: RegExp;
  types: Array<TokenType | 'ident'>;
  spec: Spec;
}

const compiled = new Map<LangId, CompiledSpec>();

function compile(lang: LangId): CompiledSpec | null {
  const cached = compiled.get(lang);
  if (cached) return cached;
  const spec = SPECS[lang];
  if (!spec) return null;
  const regex = new RegExp(spec.rules.map((r) => `(${r.src})`).join('|'), 'gm');
  const result = { regex, types: spec.rules.map((r) => r.type), spec };
  compiled.set(lang, result);
  return result;
}

function classifyIdent(word: string, code: string, end: number, spec: Spec): TokenType {
  const key = spec.caseInsensitive ? word.toLowerCase() : word;
  const lookup = (set?: Set<string>) =>
    Boolean(set && (set.has(key) || (spec.caseInsensitive && set.has(word.toUpperCase()))));
  if (lookup(spec.literals)) return 'number';
  if (lookup(spec.keywords)) return 'keyword';
  if (lookup(spec.builtins)) return 'builtin';
  if (spec.calls) {
    let i = end;
    while (i < code.length && (code[i] === ' ' || code[i] === '\t')) i++;
    if (code[i] === '(') return 'function';
  }
  if (spec.capitalizedTypes && /^[A-Z][a-z0-9]\w*$/.test(word)) return 'builtin';
  return 'plain';
}

function push(out: Token[], type: TokenType, text: string): void {
  if (!text) return;
  const last = out[out.length - 1];
  if (last && last.type === type) last.text += text;
  else out.push({ type, text });
}

function tokenizeWithSpec(code: string, lang: LangId): Token[] {
  const c = compile(lang);
  if (!c) return [{ type: 'plain', text: code }];
  const out: Token[] = [];
  const re = c.regex;
  re.lastIndex = 0;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(code)) !== null) {
    const text = m[0];
    if (text.length === 0) {
      // A zero-width match (only possible with `$` in a rule) must not stall the scan.
      re.lastIndex++;
      continue;
    }
    push(out, 'plain', code.slice(last, m.index));
    let group = 1;
    while (group < m.length && m[group] === undefined) group++;
    const kind = c.types[group - 1] ?? 'plain';
    const end = m.index + text.length;
    push(out, kind === 'ident' ? classifyIdent(text, code, end, c.spec) : kind, text);
    last = end;
  }
  push(out, 'plain', code.slice(last));
  return out;
}

// ---------------------------------------------------------------------------------------------
// Special-purpose modes
// ---------------------------------------------------------------------------------------------

/** HTML / XML / SVG: tags, attributes, values, comments, plus embedded <script>/<style>. */
function tokenizeMarkup(code: string): Token[] {
  const out: Token[] = [];
  const re = /<!--[\s\S]*?(?:-->|$)|<!\[CDATA\[[\s\S]*?(?:\]\]>|$)|<![^>]*>?|<\?[\s\S]*?(?:\?>|$)|<\/?[A-Za-z][\w:.-]*|&#?\w+;/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(code)) !== null) {
    push(out, 'plain', code.slice(last, m.index));
    const text = m[0];
    last = m.index + text.length;
    if (text.startsWith('<!--')) {
      push(out, 'comment', text);
      continue;
    }
    if (text.startsWith('<!') || text.startsWith('<?')) {
      push(out, 'meta', text);
      continue;
    }
    if (text.startsWith('&')) {
      push(out, 'number', text);
      continue;
    }
    // Tag open: `<name` then attributes up to `>`.
    push(out, 'tag', text);
    const tagName = text.replace(/^<\/?/, '').toLowerCase();
    const closing = text.startsWith('</');
    const attrRe = /\s+|([^\s=>/"']+)(?:(\s*=\s*)("[^"]*"?|'[^']*'?|[^\s>"']+))?|(\/?>)|([\s\S])/gy;
    attrRe.lastIndex = last;
    let a: RegExpExecArray | null;
    let ended = false;
    while ((a = attrRe.exec(code)) !== null) {
      if (a[4]) {
        push(out, 'tag', a[4]);
        last = attrRe.lastIndex;
        // `<script src="..." />` has no body to highlight.
        ended = a[4] === '>';
        break;
      }
      if (a[1]) {
        push(out, 'attr', a[1]);
        if (a[2]) push(out, 'plain', a[2]);
        if (a[3]) push(out, 'string', a[3]);
      } else {
        push(out, 'plain', a[0]);
      }
      last = attrRe.lastIndex;
      if (attrRe.lastIndex >= code.length) break;
    }
    re.lastIndex = last;
    // Embedded languages: highlight the body of <script> and <style> with their own rules.
    if (ended && !closing && (tagName === 'script' || tagName === 'style')) {
      const closeIdx = code.toLowerCase().indexOf(`</${tagName}`, last);
      const bodyEnd = closeIdx < 0 ? code.length : closeIdx;
      const body = code.slice(last, bodyEnd);
      for (const tok of tokenizeWithSpec(body, tagName === 'script' ? 'js' : 'css')) push(out, tok.type, tok.text);
      last = bodyEnd;
      re.lastIndex = bodyEnd;
    }
  }
  push(out, 'plain', code.slice(last));
  return out;
}

/** Unified diff: whole-line colouring. */
function tokenizeDiff(code: string): Token[] {
  const out: Token[] = [];
  const lines = code.split('\n');
  lines.forEach((line, i) => {
    let type: TokenType = 'plain';
    if (/^(?:diff |index |\+\+\+ |--- |@@)/.test(line)) type = 'meta';
    else if (line.startsWith('+')) type = 'inserted';
    else if (line.startsWith('-')) type = 'deleted';
    push(out, type, line);
    if (i < lines.length - 1) push(out, 'plain', '\n');
  });
  return out;
}

/** Markdown source: headings, emphasis, inline code, links, list markers, fences, quotes. */
function tokenizeMarkdown(code: string): Token[] {
  const out: Token[] = [];
  const re =
    /^[ \t]*(?:```|~~~)[^\n]*|^#{1,6}[ \t][^\n]*|^[ \t]*>[^\n]*|^[ \t]*(?:[-*+]|\d+[.)])(?=[ \t])|\*\*[^*\n]+\*\*|__[^_\n]+__|`[^`\n]+`|\[[^\]\n]*\]\([^)\n]*\)/gm;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(code)) !== null) {
    push(out, 'plain', code.slice(last, m.index));
    const text = m[0];
    const trimmed = text.trimStart();
    let type: TokenType = 'plain';
    if (trimmed.startsWith('```') || trimmed.startsWith('~~~')) type = 'meta';
    else if (trimmed.startsWith('#')) type = 'heading';
    else if (trimmed.startsWith('>')) type = 'comment';
    else if (text.startsWith('**') || text.startsWith('__')) type = 'bold';
    else if (text.startsWith('`')) type = 'string';
    else if (text.startsWith('[')) type = 'function';
    else type = 'keyword';
    push(out, type, text);
    last = m.index + text.length;
  }
  push(out, 'plain', code.slice(last));
  return out;
}

/**
 * Splits `code` into highlight tokens for `language` (fence info word, alias or extension).
 * Unknown languages come back as a single plain token — never as HTML.
 */
export function tokenize(code: string, language: string | undefined | null): Token[] {
  const lang = resolveLanguage(language);
  if (!lang || code.length > MAX_HIGHLIGHT_CHARS) return [{ type: 'plain', text: code }];
  try {
    if (lang === 'markup') return tokenizeMarkup(code);
    if (lang === 'diff') return tokenizeDiff(code);
    if (lang === 'markdown') return tokenizeMarkdown(code);
    return tokenizeWithSpec(code, lang);
  } catch {
    // A pathological input must never break the message: fall back to plain text.
    return [{ type: 'plain', text: code }];
  }
}
