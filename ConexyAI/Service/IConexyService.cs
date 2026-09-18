using ConexyAI.Contract;

namespace ConexyAI.Service;

public interface IConexyService
{
    Task<ConexyResponse> ExecuteAsync(Guid userId, ConexyRequest request, CancellationToken ct = default);
    Task<ConexyResponse?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct = default);
}