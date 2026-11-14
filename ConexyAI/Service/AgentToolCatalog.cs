using System.Text.Json;
using System.Text.RegularExpressions;
using ConexyAI.Contract;

namespace ConexyAI.Service;

/// <summary>
/// MODEL-FACING TOOL CATALOG — the single place that decides what the agent is offered, in which
/// order, with which description and with which argument contract.
///
/// The shape follows the DeepSeek Harness tool-catalog conventions, because the model reads these
/// descriptions on every step and its tool choice is decided by them:
/// <list type="bullet">
/// <item>name — one job per tool, snake_case, no synonym for the same job. A second name for one
/// action is exactly what makes a model call the wrong tool, get a redirect and burn a step.</item>
/// <item>description — what it does, when to use it, when NOT to use it and which tool to use
/// instead, the limits it enforces, and what comes back. No marketing, no repetition of the
/// charter.</item>
/// <item>parameter — type, default, bounds and the format rule models actually get wrong.</item>
/// <item>additionalProperties: false — a misspelled argument is a schema violation the dispatcher
/// reports, not a silently ignored key that turns into "the tool did nothing".</item>
/// </list>
///
/// The order is stable on purpose: the catalog sits in the cached prompt prefix, so reordering it —
/// or switching it per prompt wording — invalidates the cache and, worse, removes a tool the model
/// was about to use.
/// </summary>
public static class AgentToolCatalog
{
    /// <summary>The tool schemas the model is offered, in prompt order.</summary>
    public static List<object> Build() =>
    [
        // ---------------------------------------------------------------- orchestra
        Tool("spawn_agents",
            "Run 2-4 helper agents in parallel, each with a role, a self-contained brief and a " +
            "non-overlapping `scope` of path prefixes it may change. Nothing is shared between " +
            "agents: two helpers may never own the same file, and the lead still edits everything " +
            "outside the scopes it handed out. Use it for work that splits cleanly across areas " +
            "(backend / frontend / qa / browser); for a single-file change do it yourself.",
            Object(new()
            {
                ["agents"] = Array(
                    "Helpers to run. 2-4 items; scopes must not intersect.",
                    Object(new()
                    {
                        ["role"] = Str("Short role name: backend, frontend, design, qa, browser, or your own."),
                        ["brief"] = Str("Self-contained task: what to do and what to return. The helper does not see this conversation."),
                        ["scope"] = Array("Path prefixes this helper may change. Non-empty and disjoint from every other helper.", Str("Path prefix, e.g. 'ConexyAI/Service' or 'frontend/src'."), minItems: 1),
                        // AFTER: порядок важен, когда один агент проверяет работу других.
                        ["after"] = Array("Roles that must finish first. Use it for a checker (qa, review) so it tests the finished work, not an empty tree.", Str("Role name of a helper in this same call.")),
                    }, "role", "brief", "scope"),
                    minItems: 2, maxItems: 4),
            }, "agents")),

        Tool("team_message",
            "Send a message or an artifact to another agent of this run: a helper role, or 'lead' " +
            "for the leading agent. Use it to hand a dependent result to a helper or to ask for a " +
            "clarification, then continue your own work — do not wait for the reply.",
            Object(new()
            {
                ["to"] = Str("Recipient role, or 'lead'."),
                ["text"] = Str("Message text or the result being passed."),
            }, "to", "text")),

        Tool("agents_status",
            "State of the team started with `spawn_agents`: which helpers are running, done or " +
            "failed, plus the reports of the finished ones. Call it before your final answer; " +
            "`wait_seconds` > 0 blocks until a helper finishes. Do not poll it in a loop.",
            Object(new()
            {
                ["wait_seconds"] = Int("Seconds to wait for unfinished helpers (0-120, default 0).", 0, 120, 0),
            })),

        // ---------------------------------------------------------------- browser
        Tool("browser_open",
            "Open a URL in this chat's built-in browser and start (or reset) its page session. " +
            "Accepts a public http(s) URL, an HTML file of the workspace, or " +
            "http://localhost:<port>/ of a service you started with `start_service`; other " +
            "localhost and private addresses are refused. Then read the page with `browser_extract`.",
            Object(new()
            {
                ["url"] = Str("Public http(s) URL, a workspace file such as 'index.html', or http://localhost:<port>/ from start_service."),
            }, "url")),

        Tool("browser_extract",
            "Text of the open page plus its interactive elements with CSS selectors — the way to " +
            "read a page and to choose what to click. Text is cut at `max_chars`.",
            Object(new()
            {
                ["max_chars"] = Int("Maximum characters of page text (default 8000).", 500, 60000, 8000),
            })),

        Tool("browser_click",
            "Click one element of the open page: `selector` from `browser_extract` (preferred) or " +
            "the visible `text` of a link or button. Returns the resulting page text; after a click " +
            "that loads content in a SPA use `browser_wait_for` instead of re-extracting blindly.",
            Object(new()
            {
                ["selector"] = Str("CSS selector of the element (preferred)."),
                ["text"] = Str("Visible text of the element, when the selector is unknown."),
            })),

        Tool("browser_type",
            "Type text into the field matched by `selector`. With `submit: true` it presses Enter " +
            "after typing — that is how forms and search boxes are submitted.",
            Object(new()
            {
                ["selector"] = Str("CSS selector of the input field."),
                ["text"] = Str("Text to type."),
                ["submit"] = Bool("Press Enter after typing (default false).", false),
            }, "selector")),

        Tool("browser_press",
            "Press one keyboard key on the open page, e.g. 'Enter', 'Escape', 'Tab'.",
            Object(new()
            {
                ["key"] = Str("Key name."),
            }, "key")),

        Tool("browser_scroll",
            "Scroll the open page up or down. Returns the text visible after scrolling.",
            Object(new()
            {
                ["direction"] = Enum("Scroll direction.", "down", "up"),
                ["amount"] = Int("Pixels to scroll (default 600).", 100, 20000, 600),
            })),

        Tool("browser_screenshot",
            "Capture the CURRENT state of the open page and attach the image, so you can see the " +
            "layout, spacing, responsive defects or what a click did. It does not re-render a " +
            "target: for a fresh URL or a workspace file use `take_screenshot`.",
            Object(new())),

        Tool("browser_console",
            "Errors of the open page: console.error/warning, uncaught JS exceptions, failed network " +
            "requests and HTTP 4xx/5xx responses. Call it first when a page renders empty or behaves " +
            "unlike its code.",
            Object(new()
            {
                ["clear"] = Bool("Clear the log after reading it (default false).", false),
            })),

        Tool("browser_wait_for",
            "Wait until an element by `selector` or visible `text` reaches `state`, or — with no " +
            "arguments — until the network goes quiet. Use it after a click or a navigation in a SPA " +
            "instead of repeating `browser_extract`.",
            Object(new()
            {
                ["selector"] = Str("CSS selector to wait for."),
                ["text"] = Str("Visible text to wait for."),
                ["state"] = Enum("State the selector must reach (default visible).", "visible", "hidden", "attached", "detached"),
                ["timeout_ms"] = Int("Timeout in milliseconds (default 10000, max 30000).", 500, 30000, 10000),
            })),

        Tool("browser_close",
            "Close this agent's browser session and free its page. Call it when the work with the " +
            "page is done; the next `browser_open` starts a new session.",
            Object(new())),

        Tool("start_service",
            "Start a long-lived project server (dev server, static site, API) in its own container " +
            "and wait until its port answers. The server MUST listen on 0.0.0.0, not 127.0.0.1. One " +
            "service per chat: a second call replaces the first. After it answers, " +
            "`browser_open('http://localhost:<port>/')` opens exactly this service and the user sees " +
            "it in the Preview tab. Install dependencies with `bash` first; a server started inside " +
            "`bash` dies with the command and the browser cannot reach it.",
            Object(new()
            {
                ["command"] = Str("Start command, e.g. 'npx serve -l 4173 dist' or 'npm run dev -- --host 0.0.0.0 --port 5173'."),
                ["port"] = Int("Port the server listens on (1024-65535).", 1024, 65535),
            }, "command", "port")),

        Tool("stop_service",
            "Stop the service started with `start_service`.",
            Object(new())),

        // ---------------------------------------------------------------- files
        // FILECIOUS: имена и параметры как у DeepSeek Harness (`read`/`write`/`edit`, `file_path`,
        // `offset` с 1): модель, знакомая с их схемами, не тратит шаг на угадывание.
        Tool("read",
            "Read a UTF-8 text file of the workspace and return numbered lines. Long files are " +
            "paginated: at most 2000 lines per call — continue with `offset`/`limit` instead of " +
            "re-reading from the start. Read a file before editing it, unless you created or just " +
            "edited it in this session. Not for .docx/.xlsx/.pptx/.pdf (`read_document_file`), " +
            "images (`read_image`) or office binaries.",
            Object(new()
            {
                ["file_path"] = Str("Relative path inside the workspace, e.g. 'src/App.tsx'."),
                ["offset"] = Int("1-based first line to return (default 1).", 1, null, 1),
                ["limit"] = Int("Maximum number of lines to return (default and max 2000).", 1, 2000, 2000),
            }, "file_path")),

        Tool("write",
            "Create a workspace file, or replace an existing one completely, with `content`. Use it " +
            "for new files and deliberate full rewrites only: for a change inside an existing file " +
            "use `edit` (one exact fragment) or `apply_patch` (several hunks or files), which keep " +
            "the rest of the file intact.",
            Object(new()
            {
                ["file_path"] = Str("Relative path inside the workspace, e.g. 'index.html'."),
                ["content"] = Str("Complete new content of the file."),
                // OVERWRITE: явное подтверждение полной замены непустого файла. Без него клиент отказывает,
                // если новый текст заметно короче старого — так оборванный или пустой ответ не стирает файл.
                ["overwrite"] = Bool("Set true to replace a non-empty file with much less content on purpose."),
            }, "file_path", "content")),

        Tool("edit",
            "Replace one exact fragment of an existing workspace file: `old_string` must occur " +
            "exactly once, otherwise the call is refused and the file is unchanged — add " +
            "surrounding lines to make it unique. This is the default way to change code: read the " +
            "file first, then replace a fragment you actually saw. For several hunks or several " +
            "files use `apply_patch`; to create a file use `write`.",
            Object(new()
            {
                ["file_path"] = Str("Relative path inside the workspace."),
                ["old_string"] = Str("Exact text to replace; must occur exactly once."),
                ["new_string"] = Str("Replacement text."),
            }, "file_path", "old_string", "new_string")),

        Tool("apply_patch",
            "Edit one or more workspace files with a unified diff (git format: '-' removed, '+' " +
            "added, '@@' opens a hunk). Use it when a change spans several hunks or files, or when " +
            "you can express it exactly as a diff — it is safer than rewriting a file and leaves the " +
            "rest untouched. Headers are '--- a/path' / '+++ b/path' (several files allowed; " +
            "'/dev/null' creates or deletes). For a single file you may pass only the hunks plus " +
            "`path`. A hunk that does not match is reported and nothing is changed.",
            Object(new()
            {
                ["patch"] = Str("Unified diff text in git format."),
                ["path"] = Str("Target file, when the patch carries only hunks without ---/+++ headers."),
            }, "patch")),

        Tool("workspace_list_files",
            "List files of the chat workspace recursively, optionally under `path`. Shows what " +
            "exists, including the user's attachments. To find files by name pattern use `glob`, by " +
            "content `grep`.",
            Object(new()
            {
                ["path"] = Str("Optional relative directory to list (default: workspace root)."),
            })),

        Tool("grep",
            "Search file contents with a regular expression (ripgrep semantics). Returns " +
            "'path:line: text' grouped by file. Restrict it with `glob` (which files) and `path` " +
            "(which directory); binary and .gitignore'd files are skipped unless `include_ignored`. " +
            "Prefer it over escaping to the shell (grep/findstr are refused). At most `max_results` " +
            "lines come back (default 80, max 400) — when the result is cut, narrow the pattern.",
            Object(new()
            {
                ["pattern"] = Str("Regular expression to search for."),
                ["path"] = Str("Optional relative directory to search in (default: workspace root)."),
                ["glob"] = Str("Optional glob restricting which files are searched, e.g. '*.cs' or 'src/**/*.ts'."),
                ["ignore_case"] = Bool("Case-insensitive search (default false; a short case-sensitive result is retried automatically without case).", false),
                ["max_results"] = Int("Maximum matching lines to return (default 80, max 400).", 1, 400, 80),
                ["context_lines"] = Int("Lines of context before and after each match, like grep -C (0-10, default 0).", 0, 10, 0),
                ["output_mode"] = Enum("What to return: matching lines, the files that match, or a match count per file.", "content", "files", "count"),
                ["include_ignored"] = Bool("Also search files ignored by .gitignore (default false).", false),
            }, "pattern")),

        Tool("glob",
            "Find files (not directories) whose path matches a glob with '**' support, e.g. " +
            "'**/*.ts' or 'src/**/test_*.py'. A pattern without '/' also matches at any depth. Paths " +
            "are relative to the workspace root and sorted. Use it to discover files before reading " +
            "or searching them. Returns at most `max_results` paths (default 200, max 2000).",
            Object(new()
            {
                ["pattern"] = Str("Glob pattern to match file paths against, e.g. '**/*.cs'."),
                ["path"] = Str("Optional relative directory to search in (default: workspace root)."),
                ["max_results"] = Int("Maximum files to return (default 200, max 2000).", 1, 2000, 200),
            }, "pattern")),

        // ---------------------------------------------------------------- execution
        Tool("bash",
            "Run a shell command in the isolated one-shot sandbox. The workspace root is the working " +
            "directory, so no 'cd' is needed, and every process started in the background dies when " +
            "the call returns. Use it for builds, package managers, git and file utilities. Do NOT " +
            "use it to read, search or write files — `read`, `grep`, `glob`, `edit`, `apply_patch` " +
            "exist for that, and the shell forms (cat, head, sed, Get-Content, Out-File) are refused " +
            "with a pointer. Tests go to `run_tests`, type checks to `get_diagnostics`, " +
            "git push/pull/fetch and gh to `github_action`. A non-zero exit code is the command's " +
            "result, not a broken tool: read the output. Output over 80 lines is truncated to the " +
            "first 40 and the last 40.",
            Object(new()
            {
                ["command"] = Str("Shell command to run."),
                ["description"] = Str("What the command does in plain words, positive form: 'Build the project', 'Install dependencies'. The user sees this instead of the flags."),
                ["timeout_seconds"] = Int("Optional timeout in seconds (default 60, max 300).", 1, 300, 60),
            }, "command", "description")),

        Tool("run_tests",
            "Run the project's tests and return a STRUCTURED report — pass/fail counts and, per " +
            "failure, its name, file:line and expected/actual — instead of raw logs. The framework " +
            "is auto-detected (npm test with jest/vitest/mocha, pytest, dotnet test, go test, cargo " +
            "test, rspec, phpunit). Prefer it over `bash: npm test` / `pytest` / `dotnet test`, which " +
            "are refused. Pass `command` only to override with a custom command — it then needs the " +
            "same confirmation as `bash`.",
            Object(new()
            {
                ["framework"] = Enum("Force a test framework (normally auto-detected).", "pytest", "jest", "vitest", "mocha", "dotnet", "go", "cargo", "rspec", "phpunit"),
                ["path"] = Str("Optional relative directory to run the tests in (default: workspace root)."),
                ["command"] = Str("Optional explicit shell command; overrides auto-detection and needs the same confirmation as bash."),
                ["timeout_seconds"] = Int("Optional timeout in seconds (default 600, max 900).", 1, 900, 600),
            })),

        Tool("get_diagnostics",
            "Compiler, type and linter problems WITHOUT running the app or its tests — the editor's " +
            "Problems panel. Auto-detects dotnet build, tsc --noEmit, eslint, cargo check, go vet, " +
            "pyright/mypy/ruff and returns each issue as " +
            "'severity: file:line:column [code] message'. Use it after edits to catch errors before " +
            "a build; `tool` forces a checker, `command` supplies a custom one (same confirmation as " +
            "`bash`).",
            Object(new()
            {
                ["tool"] = Enum("Force a checker (normally auto-detected).", "dotnet", "tsc", "eslint", "cargo", "go", "pyright", "mypy", "ruff"),
                ["path"] = Str("Optional relative directory to check (default: workspace root)."),
                ["command"] = Str("Optional explicit check command; overrides auto-detection and needs confirmation like bash."),
                ["timeout_seconds"] = Int("Optional timeout in seconds (default 600, max 900).", 1, 900, 600),
            })),

        // ---------------------------------------------------------------- images
        // HARNESS_ALIASES: у harness картинку читает `read_image`; имя и параметр те же, чтобы модель не
        // путалась между двумя наборами схем.
        Tool("read_image",
            "Look at an image: a design mock, a screenshot of a bug, a diagram. Pass `file_path` for an " +
            "image of the workspace or `name` for an image attached to the current message. " +
            "Supported: png, jpg, jpeg, webp, gif, bmp, up to 8 MB. Attachments are also saved into " +
            "the workspace root, so they can be reopened by path. Use it whenever the user says " +
            "'make it look like this image' or 'the screenshot shows ...' — text tools show images " +
            "as binary.",
            Object(new()
            {
                ["file_path"] = Str("Relative path to an image file in the workspace."),
                ["name"] = Str("File name of an image attached to the current message."),
            })),

        Tool("take_screenshot",
            "Render a public http(s) URL or an HTML file of the chat workspace with headless " +
            "Chromium and attach the screenshot for a visual check. Use it for a static look at a " +
            "page or file; for interaction (click, type, console errors) use `browser_open` + " +
            "`browser_screenshot`.",
            Object(new()
            {
                ["url"] = Str("Public http(s) URL, or a path relative to the workspace root (e.g. 'index.html', 'dist/index.html')."),
                ["viewport_width"] = Int("Viewport width in pixels (default 1280).", 200, 4000, 1280),
                ["viewport_height"] = Int("Viewport height in pixels (default 800).", 200, 4000, 800),
            }, "url")),

        // ---------------------------------------------------------------- plan
        Tool("todo_write",
            "Create or replace the todo list of the current multi-step task. Pass the FULL list " +
            "every time (not a delta): it replaces the previous state. Use it when the work needs " +
            "several steps or several files — mark the active step `in_progress` and update the list " +
            "after each finished step; skip it for a single-step change.",
            Object(new()
            {
                ["todos"] = Array(
                    "Complete task list, in order.",
                    Object(new()
                    {
                        ["id"] = Str("Short stable step id, e.g. '1', '2'."),
                        ["content"] = Str("Step description."),
                        ["status"] = Enum("Step state.", "pending", "in_progress", "completed", "skipped"),
                        ["skip_reason"] = Str("Required when status = skipped."),
                    }, "id", "content", "status"),
                    minItems: 1),
            }, "todos")),

        // ---------------------------------------------------------------- github
        Tool("github_action",
            "Run a GitHub workflow operation with the user's own Personal Access Token: clone, " +
            "fetch/pull, branch, commit+push, merge, delete branch, pull request. Use `clone_repo` " +
            "for a FRESH copy and `fetch`/`pull` (with `repo_folder`) to UPDATE an existing clone — " +
            "never clone twice. The sandbox has no git credentials: `git push/pull/fetch/remote`, " +
            "`gh` and credentialed clones are refused in `bash`, so do not look for a token there. " +
            "If it answers that the token is missing or rejected, stop and tell the user to add a " +
            "Personal Access Token in Settings -> GitHub.",
            Object(new()
            {
                ["operation"] = Enum("Operation to perform.", "clone_repo", "create_branch", "switch_branch", "fetch", "pull", "merge_branch", "commit_and_push", "delete_branch", "create_pull_request"),
                ["repo_url"] = Str("Repository URL, owner/repo or a full URL (clone_repo)."),
                ["target_folder"] = Str("Target folder for clone_repo, relative to the workspace."),
                ["repo_folder"] = Str("Folder of the cloned repository inside the workspace; also auto-detected when the workspace holds a single repo."),
                ["repo"] = Str("Repository as owner/repo."),
                ["branch"] = Str("Git branch name."),
                ["branch_name"] = Str("Git branch name; same as `branch`."),
                ["delete_remote"] = Bool("For delete_branch: also delete the branch on origin (default true).", true),
                ["commit_message"] = Str("Commit message."),
                ["changed_files"] = Array("Files to stage (default: all).", Str("Relative path of a file.")),
                ["title"] = Str("Pull request title."),
                ["body"] = Str("Pull request body."),
                ["head_branch"] = Str("PR head branch."),
                ["base_branch"] = Str("PR base branch (default main)."),
            }, "operation")),

        Tool("github_api",
            "GitHub REST API without a local clone: branches, commits, issues, pull requests " +
            "(including changed files, diffs and discussion) and Actions workflows / CI status. " +
            "Uses the user's own Personal Access Token. Use it to read a repository, file or reply " +
            "to an issue, review a pull request (read its files and comments first), check whether " +
            "CI passed, or trigger a workflow. Git operations on the working copy live in " +
            "`github_action`. If the token is missing, stop and ask the user to add one in " +
            "Settings -> GitHub.",
            Object(new()
            {
                ["operation"] = Enum("Operation to perform.", "list_branches", "list_commits", "list_issues", "create_issue", "comment_issue", "close_issue", "get_issue", "list_pull_requests", "get_pull_request", "get_pull_request_files", "get_pull_request_comments", "comment_pull_request", "review_pull_request", "merge_pull_request", "close_pull_request", "get_commit_status", "list_workflows", "run_workflow"),
                ["repo"] = Str("Repository as owner/repo or a full URL; omit to use the workspace clone's origin."),
                ["repo_folder"] = Str("Folder of the cloned repository inside the workspace, when `repo` is omitted."),
                ["state"] = Enum("Filter for list_issues / list_pull_requests (default open).", "open", "closed", "all"),
                ["limit"] = Int("Maximum items for list operations (default 20, max 100).", 1, 100, 20),
                ["branch"] = Str("Branch for list_commits / get_commit_status."),
                ["number"] = Int("Issue or pull request number.", 1),
                ["title"] = Str("Title for create_issue."),
                ["body"] = Str("Body text for create_issue / comment_* / the review body."),
                ["event"] = Enum("Review verdict for review_pull_request.", "approve", "request_changes", "comment"),
                ["method"] = Enum("Merge method for merge_pull_request.", "merge", "squash", "rebase"),
                ["workflow"] = Str("Workflow file name or id for run_workflow (see list_workflows)."),
                ["ref"] = Str("Branch, tag or commit SHA for run_workflow / get_commit_status (workflow defaults to main)."),
            }, "operation")),

        // ---------------------------------------------------------------- documents
        Tool("search_documents",
            "Semantic search over the documents, files and project knowledge base the user " +
            "uploaded. Returns the most relevant chunks with their id, title and a short excerpt. " +
            "Use it FIRST when the question is about facts, contracts, regulations or the user's " +
            "own files, then `read_document_chunk` for the full text; cite the document name and " +
            "section. An empty result means the documents do not answer it — say so instead of " +
            "guessing.",
            Object(new()
            {
                ["query"] = Str("Search query, phrased by meaning rather than by keywords."),
                ["limit"] = Int("Maximum number of chunks (default 5).", 1, 20, 5),
                ["document_name"] = Str("Filter by a specific file name or extension, when known."),
            }, "query")),

        Tool("read_document_chunk",
            "Read a document's text page by page (up to about 12,000 characters per call) starting at " +
            "`chunk_index`. Use it when a `search_documents` excerpt is not enough; the answer " +
            "carries `next_chunk_index` for the continuation.",
            Object(new()
            {
                ["document_id"] = Str("Document id from search_documents."),
                ["chunk_index"] = Int("Chunk to start from (default: the beginning).", 0, null, 0),
            }, "document_id")),

        // ---------------------------------------------------------------- web
        WebSearchTool.Schema(),
        FetchWebPageTool.Schema(),

        Tool("search_user_chats",
            "Search THIS user's previous conversations (other chats, never the current one) by " +
            "keywords or topic. Use it when the user refers to an earlier discussion ('how did we " +
            "set up Nginx last time?', 'do it like before') instead of asking again. Words match " +
            "case-insensitively; pass 1-5 distinctive keywords (technologies, file or project names, " +
            "terms) and retry with synonyms or English variants when nothing is found.",
            Object(new()
            {
                ["query"] = Str("Keywords or topic, e.g. 'nginx reverse proxy ssl'."),
                ["limit"] = Int("Maximum number of chats to return (default 5, max 10).", 1, 10, 5),
            }, "query")),

        // ---------------------------------------------------------------- office output
        Tool("create_document",
            "Create a Word (.docx), Excel (.xlsx) or PowerPoint (.pptx) file in the workspace from " +
            "Markdown; the format comes from the path extension. .docx: headings, paragraphs with " +
            "**bold** / *italic* / `code` / links, nested bullet, numbered and task lists, tables, " +
            "code blocks, quotes. .xlsx: every Markdown table becomes a sheet named after the " +
            "heading above it, with a styled header, filters, fitted columns and typed cells " +
            "(numbers, dates, booleans, real formulas such as =SUM(B2:B5)). .pptx: a leading '#' is " +
            "the title slide, every '#'/'##' heading starts a slide, the lines under it become " +
            "bullets and tables become native tables. Use it when the user asks for a document or " +
            "the result is long; the chat itself gets a short summary.",
            Object(new()
            {
                ["path"] = Str("Relative path ending in .docx, .xlsx or .pptx."),
                ["content"] = Str("Document content in Markdown."),
                ["title"] = Str("Optional title, used when the content has no heading."),
            }, "path", "content")),

        Tool("read_document_file",
            "Read the text of a workspace document: .docx, .xlsx (every sheet as a table), .pptx " +
            "(every slide), .pdf or any text file. Use it for files the user attached or uploaded; " +
            "`read` shows these formats as binary.",
            Object(new()
            {
                ["path"] = Str("Relative path to the file."),
            }, "path")),
    ];

    /// <summary>Every advertised tool name, in catalog order.</summary>
    public static IReadOnlyList<string> Names() => [.. ByName.Value.Keys];

    // TOOL_GUIDANCE: harness печатает по строчке подсказки на каждый ВЫДАННЫЙ инструмент, а не один
    // общий список. Подсказки берём отсюда и отдаём только для тех схем, что реально предложены.
    private static readonly Dictionary<string, string> Guidance = new(StringComparer.Ordinal)
    {
        ["read"] = "Use `read` — not shell commands like cat, head, sed or Get-Content — to inspect text files. Use offset/limit to continue a large file.",
        ["write"] = "Use `write` only for new files or a deliberate full rewrite; for a change inside an existing file use `edit`.",
        ["edit"] = "Use `edit` for a targeted change: read the fragment first, then replace exactly what you saw. If it is not unique, widen it or use `apply_patch`.",
        ["apply_patch"] = "Use `apply_patch` when a change spans several hunks or files; a hunk that does not match changes nothing.",
        ["read_image"] = "Use `read_image` to look at a mock, a screenshot or a diagram; text tools show images as binary.",
        ["grep"] = "Use `grep` to find code by content; do not escape to the shell for search. Search by identifier for code, by a plain word for a concept.",
        ["glob"] = "Use `glob` to discover file paths before reading them.",
        ["bash"] = "Use `bash` for builds, package managers, git and tests; reading, searching and writing files goes through `read`, `grep`, `edit` and `apply_patch`. If the same failure comes back twice with different arguments, the arguments are not the cause: verify the fact with another tool, or ask the user.",
        ["run_tests"] = "After changing code run `run_tests` instead of guessing that it works.",
        ["agents_status"] = "Check the team with `agents_status` at least once a minute while helpers run; do not poll it in a tight loop.",
    };

    /// <summary>Подсказки выбора для выданного набора схем (в порядке каталога).</summary>
    public static string GuidanceFor(IEnumerable<string> advertisedNames)
    {
        var offered = advertisedNames.ToHashSet(StringComparer.Ordinal);
        var lines = Build()
            .Select(schema => JsonSerializer.SerializeToElement(schema).GetProperty("function").GetProperty("name").GetString())
            .Where(name => name is not null && offered.Contains(name) && Guidance.ContainsKey(name))
            .Select(name => "- " + Guidance[name!])
            .ToList();
        return lines.Count == 0 ? string.Empty : "Как выбирать инструменты:\n" + string.Join("\n", lines);
    }

    // HARNESS_ALIASES: модель видит имена harness, но внутри агент работает со своими — все стражи,
    // диспетчер и UI остаются на прежних именах. Один шаг перевода на входе, без дублирования логики.
    private static readonly Dictionary<string, string> InternalNames = new(StringComparer.Ordinal)
    {
        ["read"] = "file_read",
        ["write"] = "file_write",
        ["edit"] = "str_replace_editor",
        ["read_image"] = "view_image",
        ["shell"] = "bash",
    };

    /// <summary>Внутреннее имя инструмента для имени схемы (read → file_read); прочие — без изменений.</summary>
    public static string InternalNameFor(string advertisedName) =>
        InternalNames.GetValueOrDefault(advertisedName, advertisedName);

    /// <summary>Переводит вызов модели с именем harness на внутреннее имя и аргументы.</summary>
    public static Contract.LlmToolCall Normalize(Contract.LlmToolCall call)
    {
        var name = call.Function.Name;
        if (!InternalNames.TryGetValue(name, out var internalName))
            return call;

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return call;
            var source = document.RootElement;

            object? Take(string from, string to) => source.TryGetProperty(from, out var value) ? value : null;

            var mapped = new Dictionary<string, object?>(StringComparer.Ordinal);
            switch (name)
            {
                case "read":
                    mapped["path"] = Take("file_path", "path");
                    if (source.TryGetProperty("offset", out var offset) && offset.TryGetInt32(out var offsetValue))
                        mapped["offset"] = Math.Max(0, offsetValue - 1); // 1-based у модели, 0-based внутри
                    mapped["limit"] = Take("limit", "limit");
                    break;
                case "write":
                    mapped["path"] = Take("file_path", "path");
                    mapped["content"] = Take("content", "content");
                    mapped["overwrite"] = Take("overwrite", "overwrite");
                    break;
                case "edit":
                    mapped["command"] = "str_replace";
                    mapped["path"] = Take("file_path", "path");
                    mapped["old_str"] = Take("old_string", "old_str");
                    mapped["new_str"] = Take("new_string", "new_str");
                    break;
                case "read_image":
                    mapped["path"] = Take("file_path", "path");
                    mapped["name"] = Take("name", "name");
                    break;
                default: // shell -> bash: аргументы совпадают
                    foreach (var property in source.EnumerateObject())
                        mapped[property.Name] = property.Value;
                    break;
            }

            var arguments = JsonSerializer.Serialize(mapped.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
            return call with { Function = call.Function with { Name = internalName, Arguments = arguments } };
        }
        catch (JsonException)
        {
            return call;
        }
    }

    /// <summary>One advertised schema by name, or null when the name is not ours (MCP, device, phone).</summary>
    public static JsonElement? SchemaFor(string name) =>
        ByName.Value.TryGetValue(name, out var schema) ? schema : null;

    /// <summary>Catalog indexed by tool name; built once, because every tool call validates against it.</summary>
    private static readonly Lazy<Dictionary<string, JsonElement>> ByName = new(() =>
    {
        var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var schema in Build())
        {
            var element = JsonSerializer.SerializeToElement(schema);
            byName[element.GetProperty("function").GetProperty("name").GetString() ?? string.Empty] = element;
        }
        return byName;
    });

    /// <summary>
    /// Catalog invariants, checked by the test suite. Every message names the offending tool, so a
    /// broken schema fails the build instead of quietly degrading the model's tool choice.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var schema in Build())
        {
            var function = JsonSerializer.SerializeToElement(schema).GetProperty("function");
            var name = function.GetProperty("name").GetString() ?? string.Empty;
            var description = function.GetProperty("description").GetString() ?? string.Empty;

            if (!NamePattern.IsMatch(name)) problems.Add($"{name}: name must be snake_case starting with a letter");
            if (!seen.Add(name)) problems.Add($"{name}: duplicate name");
            if (description.Length < 40) problems.Add($"{name}: description is too short to steer the choice");
            if (description.Length > 1200) problems.Add($"{name}: description is too long ({description.Length} chars)");
            if (!description.EndsWith('.')) problems.Add($"{name}: description must end with a period");

            var parameters = function.GetProperty("parameters");
            if (parameters.GetProperty("type").GetString() != "object")
                problems.Add($"{name}: parameters must be an object schema");
            if (!parameters.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
                problems.Add($"{name}: parameters must set additionalProperties: false");

            var properties = parameters.GetProperty("properties");
            var declared = properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var required = parameters.GetProperty("required").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty).ToList();

            foreach (var requiredName in required)
            {
                if (!declared.Contains(requiredName))
                    problems.Add($"{name}: required '{requiredName}' is not a declared property");
            }
            if (required.Count != required.Distinct(StringComparer.Ordinal).Count())
                problems.Add($"{name}: duplicate entry in required");

            foreach (var property in properties.EnumerateObject())
            {
                var value = property.Value;
                if (!value.TryGetProperty("type", out var type) || string.IsNullOrEmpty(type.GetString()))
                    problems.Add($"{name}.{property.Name}: missing type");
                if (!value.TryGetProperty("description", out var text) || string.IsNullOrWhiteSpace(text.GetString()))
                    problems.Add($"{name}.{property.Name}: missing description");

                if (type.ValueKind == JsonValueKind.String && type.GetString() == "array" &&
                    !value.TryGetProperty("items", out _))
                    problems.Add($"{name}.{property.Name}: array without items");

                if (value.TryGetProperty("enum", out var values) &&
                    (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0))
                    problems.Add($"{name}.{property.Name}: empty enum");
            }
        }

        return problems;
    }

    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    // ---------------------------------------------------------------- schema DSL

    private static object Tool(string name, string description, Dictionary<string, object> parameters, params string[] required)
    {
        // Object(...) уже несёт required: явный список здесь только уточняет его (вызовы, где
        // обязательные параметры удобнее перечислить рядом с описанием инструмента).
        if (required.Length > 0) parameters["required"] = required;
        return new Dictionary<string, object>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object>
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters,
            },
        };
    }

    private static Dictionary<string, object> Object(Dictionary<string, object> properties, params string[] required) =>
        new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };

    private static Dictionary<string, object> Str(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static Dictionary<string, object> Int(string description, int? min = null, int? max = null, int? @default = null)
    {
        var property = new Dictionary<string, object> { ["type"] = "integer", ["description"] = description };
        if (min is not null) property["minimum"] = min;
        if (max is not null) property["maximum"] = max;
        if (@default is not null) property["default"] = @default;
        return property;
    }

    private static Dictionary<string, object> Bool(string description, bool @default = false) =>
        new() { ["type"] = "boolean", ["description"] = description, ["default"] = @default };

    private static Dictionary<string, object> Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = values };

    private static Dictionary<string, object> Array(string description, Dictionary<string, object> items, int? minItems = null, int? maxItems = null)
    {
        var property = new Dictionary<string, object> { ["type"] = "array", ["description"] = description, ["items"] = items };
        if (minItems is not null) property["minItems"] = minItems;
        if (maxItems is not null) property["maxItems"] = maxItems;
        return property;
    }
}
