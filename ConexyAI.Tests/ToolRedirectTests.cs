using System.Runtime.CompilerServices;
using ConexyAI.Service;

// P0_TOOL_REDIRECT: добавлено 2026-10-10 — классификатор, который отправляет модель к профильному
// инструменту вместо перекрывающейся bash-команды. Тесты фиксируют, что отклоняется, а что нет.
internal static class ToolRedirectTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("redirect: git and gh operations go to github_action", GitHubAsync);
        TestRegistry.Add("redirect: test runners go to run_tests", TestsAsync);
        TestRegistry.Add("redirect: build and type checks go to get_diagnostics", ChecksAsync);
        TestRegistry.Add("redirect: ordinary commands are left alone", OrdinaryAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static bool Refused(string command) => AgentToolRedirect.RefusedBashCommand(command) is not null;

    private static Task GitHubAsync()
    {
        Assert(Refused("git push origin main"), "git push must be refused");
        Assert(Refused("git -C /repo push"), "git push with -C must be refused");
        Assert(Refused("cd /repo && git push"), "git push after && must be refused");
        Assert(Refused("sudo git push"), "sudo git push must be refused");
        Assert(Refused("git pull"), "git pull must be refused (use github_action pull)");
        Assert(Refused("git fetch --all"), "git fetch must be refused (use github_action fetch)");
        Assert(Refused("gh pr create --fill"), "gh must be refused");
        Assert(Refused("git clone https://x-access-token:ghp_secret@github.com/a/b.git"),
            "a credentialed clone must be refused");
        Assert(Refused("git config --global credential.helper store"),
            "credential.helper config must be refused");
        Assert(Refused("git remote set-url origin https://x-access-token:ghp_secret@github.com/a/b.git"),
            "a credentialed remote must be refused");

        // Локальные, публичные и безобидные read-only операции трогать нельзя.
        Assert(!Refused("git status"), "git status must be allowed");
        Assert(!Refused("git diff --stat"), "git diff must be allowed");
        Assert(!Refused("git log --oneline -5"), "git log must be allowed");
        Assert(!Refused("git add . && git commit -m x"), "local add/commit must be allowed");
        Assert(!Refused("git remote -v"), "git remote -v must be allowed (read-only)");
        Assert(!Refused("git remote add origin https://github.com/owner/public.git"),
            "a plain remote add must be allowed");
        Assert(!Refused("git clone https://github.com/owner/public.git"), "a public clone must be allowed");
        return Task.CompletedTask;
    }

    private static Task TestsAsync()
    {
        Assert(Refused("npm test"), "npm test must be refused");
        Assert(Refused("npm run test"), "npm run test must be refused");
        Assert(Refused("npm run test:unit"), "npm run test:unit must be refused");
        Assert(Refused("yarn test"), "yarn test must be refused");
        Assert(Refused("pnpm test"), "pnpm test must be refused");
        Assert(Refused("pytest -q"), "pytest must be refused");
        Assert(Refused("python -m pytest"), "python -m pytest must be refused");
        Assert(Refused("dotnet test"), "dotnet test must be refused");
        Assert(Refused("cargo test"), "cargo test must be refused");
        Assert(Refused("go test ./..."), "go test must be refused");
        Assert(Refused("vitest run"), "vitest must be refused");
        return Task.CompletedTask;
    }

    private static Task ChecksAsync()
    {
        // `dotnet build` намеренно остаётся в bash: на нём держится гейт самокоррекции.
        Assert(!Refused("dotnet build"), "dotnet build stays in bash (self-correction gate)");
        Assert(Refused("tsc --noEmit"), "tsc must be refused");
        Assert(Refused("npx tsc --noEmit"), "npx tsc must be refused");
        Assert(Refused("eslint ."), "eslint must be refused");
        Assert(Refused("npm run lint"), "npm run lint must be refused");
        Assert(Refused("npm run typecheck"), "npm run typecheck must be refused");
        Assert(Refused("cargo check"), "cargo check must be refused");
        Assert(Refused("go vet ./..."), "go vet must be refused");
        Assert(Refused("mypy ."), "mypy must be refused");
        return Task.CompletedTask;
    }

    private static Task OrdinaryAsync()
    {
        Assert(!Refused("ls -la"), "ls must be allowed");
        Assert(!Refused("rm -rf node_modules"), "rm must be allowed");
        Assert(!Refused("npm run build"), "npm run build must be allowed (produces artifacts)");
        Assert(!Refused("dotnet run"), "dotnet run must be allowed");
        Assert(!Refused("cargo build"), "cargo build must be allowed");
        Assert(!Refused("node script.js"), "node script must be allowed");
        Assert(!Refused("echo \"git push\""), "git inside an echo must not trigger a redirect");
        Assert(!Refused(null), "null must be allowed");
        return Task.CompletedTask;
    }
}
