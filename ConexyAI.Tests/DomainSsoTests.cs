using System.Runtime.CompilerServices;
using ConexyAI.Configuration;

// DOMAIN_SSO: проверяется то, на чём держится безопасность переноса сессии между conexyai.ru и
// conexy.ru, — список доменов (иначе получился бы открытый редирект с кодом на чужой сайт) и
// адрес возврата (иначе `next` увёл бы человека наружу уже с сессией).
internal static class DomainSsoTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("domain sso: only the product domains and local return paths are accepted", DomainRulesAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task DomainRulesAsync()
    {
        Assert(ProductDomains.IsKnownHost("conexy.ru"), "the new domain is a product host");
        Assert(ProductDomains.IsKnownHost("www.conexyai.ru"), "both domains have a www host");
        Assert(ProductDomains.IsKnownHost("WWW.CONEXY.RU"), "hosts compare case-insensitively");
        Assert(!ProductDomains.IsKnownHost("conexy.ru.evil.com"), "a suffix look-alike is not a product host");
        Assert(!ProductDomains.IsKnownHost("evil.com"), "a foreign host is rejected");
        Assert(!ProductDomains.IsKnownHost(null), "a missing host is rejected");

        Assert(ProductDomains.IsKnownOrigin("https://conexy.ru"), "the new origin may call the API");
        Assert(ProductDomains.IsKnownOrigin("https://www.conexyai.ru"), "the www origin may call the API");
        Assert(!ProductDomains.IsKnownOrigin("http://conexy.ru"), "the origin must be https");
        Assert(!ProductDomains.IsKnownOrigin("https://conexy.ru.evil.com"), "a look-alike origin is rejected");
        Assert(!ProductDomains.IsKnownOrigin(null), "a missing origin is rejected");

        Assert(ProductDomains.SafeLocalPath("/chat/abc?x=1") == "/chat/abc?x=1", "a local path survives as is");
        Assert(ProductDomains.SafeLocalPath("/") == "/", "the root is allowed");
        Assert(ProductDomains.SafeLocalPath("//evil.com") == "/", "a protocol-relative url collapses to the root");
        Assert(ProductDomains.SafeLocalPath("/\\evil.com") == "/", "a backslash-smuggled host collapses to the root");
        Assert(ProductDomains.SafeLocalPath("https://evil.com/x") == "/", "an absolute foreign url collapses to the root");
        Assert(ProductDomains.SafeLocalPath(null) == "/", "a missing path means the root");
        Assert(ProductDomains.SafeLocalPath("") == "/", "an empty path means the root");
        return Task.CompletedTask;
    }
}
