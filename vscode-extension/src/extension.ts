import * as vscode from 'vscode';
import { spawn } from 'child_process';

let channel: vscode.OutputChannel;
let status: vscode.StatusBarItem;

export function activate(context: vscode.ExtensionContext): void {
    channel = vscode.window.createOutputChannel('ConexyAI');
    status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
    status.text = '$(hubot) Conexy';
    status.tooltip = 'Открыть агента ConexyAI в терминале';
    status.command = 'conexy.openTerminal';
    status.show();

    context.subscriptions.push(
        channel,
        status,
        vscode.commands.registerCommand('conexy.openTerminal', openTerminal),
        vscode.commands.registerCommand('conexy.ask', ask),
        vscode.commands.registerCommand('conexy.explainSelection', explainSelection),
        vscode.commands.registerCommand('conexy.reviewChanges', reviewChanges),
    );
}

export function deactivate(): void {
    channel?.dispose();
    status?.dispose();
}

function settings(): vscode.WorkspaceConfiguration {
    return vscode.workspace.getConfiguration('conexy');
}

function workspaceRoot(): string {
    return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();
}

function executable(): string {
    return settings().get<string>('executable') || 'conexy';
}

function commonArgs(): string[] {
    const args: string[] = [];
    if (settings().get<boolean>('autoApprove')) {
        args.push('--auto');
    }
    const extra = settings().get<string[]>('extraArgs') ?? [];
    return args.concat(extra);
}

function openTerminal(): void {
    const terminal = vscode.window.createTerminal({ name: 'Conexy', cwd: workspaceRoot() });
    terminal.show();
    terminal.sendText([executable(), ...commonArgs()].map(quoteForShell).join(' '));
}

async function ask(): Promise<void> {
    const prompt = await vscode.window.showInputBox({
        prompt: 'Задача для ConexyAI',
        placeHolder: 'например: добавь тесты для парсера и прогони их',
        ignoreFocusOut: true,
    });
    if (!prompt) {
        return;
    }
    const context = selectionContext();
    await runAgent(prompt, context?.text, context?.where);
}

async function explainSelection(): Promise<void> {
    const context = selectionContext();
    if (!context) {
        vscode.window.showWarningMessage('Откройте файл и выделите код.');
        return;
    }
    const prompt = `Объясни код из ${context.where}. Что он делает, какие у него риски и что стоит улучшить. ` +
        'Не меняй файлы: это вопрос, а не задача на правку. Ответь по-русски.';
    await runAgent(prompt, context.text, context.where);
}

async function reviewChanges(): Promise<void> {
    const prompt = 'Проверь незакоммиченные изменения в этом проекте: выполни git diff (и git status), ' +
        'найди баги, риски и пропущенные тесты. Ничего не меняй, дай отчёт по-русски.';
    await runAgent(prompt);
}

function selectionContext(): { text: string; where: string } | undefined {
    const editor = vscode.window.activeTextEditor;
    if (!editor) {
        return undefined;
    }
    const document = editor.document;
    const relative = vscode.workspace.asRelativePath(document.uri);
    const selection = editor.selection;
    if (selection.isEmpty) {
        return { text: document.getText(), where: relative };
    }
    const start = selection.start.line + 1;
    const end = selection.end.line + 1;
    return { text: document.getText(selection), where: `${relative}:${start}-${end}` };
}

async function runAgent(prompt: string, stdin?: string, where?: string): Promise<void> {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) {
        vscode.window.showWarningMessage('ConexyAI: откройте папку проекта.');
        return;
    }

    channel.show(true);
    channel.appendLine('');
    channel.appendLine(`── ${new Date().toLocaleTimeString()} · ${where ?? folder.name} · ${prompt}`);
    channel.appendLine('');

    await vscode.window.withProgress(
        {
            location: vscode.ProgressLocation.Notification,
            title: 'ConexyAI',
            cancellable: true,
        },
        (progress, token) => new Promise<void>((resolve) => {
            const args = ['-p', prompt, '--output-format', 'text', ...commonArgs()];
            progress.report({ message: 'работаю…' });

            const child = spawn(executable(), args, {
                cwd: folder.uri.fsPath,
                env: process.env,
                windowsHide: true,
            });

            token.onCancellationRequested(() => {
                child.kill();
            });

            child.stdout?.on('data', (data: Buffer) => channel.append(data.toString()));
            child.stderr?.on('data', (data: Buffer) => channel.append(data.toString()));

            child.on('error', (error: Error) => {
                channel.appendLine('');
                channel.appendLine(`Не удалось запустить «${executable()}»: ${error.message}`);
                channel.appendLine('Проверьте настройку conexy.executable и что клиент установлен.');
                vscode.window.showErrorMessage(`ConexyAI: ${error.message}`);
                resolve();
            });

            child.on('close', (code: number | null) => {
                channel.appendLine('');
                channel.appendLine(code === 0 ? '── готово' : `── завершено с кодом ${code ?? '—'}`);
                if (code !== 0 && code !== null) {
                    vscode.window.showWarningMessage(
                        'ConexyAI завершился с ошибкой — подробности в канале «ConexyAI».');
                }
                resolve();
            });

            if (stdin) {
                child.stdin?.write(stdin);
            }
            child.stdin?.end();
        }),
    );
}

/** Простое экранирование для строки, которую печатаем в терминал. */
function quoteForShell(value: string): string {
    return /[\s"]/g.test(value) ? `"${value.replace(/"/g, '\\"')}"` : value;
}
