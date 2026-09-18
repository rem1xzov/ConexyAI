using ConexyAI.Contract;

namespace ConexyAI.Service;

public interface IWebSearchService
{
    /// <summary>Runs a web search and returns the top results formatted for the model.</summary>
    Task<WebSearchResult> SearchAsync(string query, CancellationToken ct = default);
}
