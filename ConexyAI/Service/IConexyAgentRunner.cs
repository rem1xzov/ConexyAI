using ConexyAI.Contract;

namespace ConexyAI.Service;

public interface IConexyAgentRunner
{
    Task<string> RunLoopAsync(ConexyJob job, CancellationToken ct = default);
}