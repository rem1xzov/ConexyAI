namespace ConexyAI.Model;

public enum ConexyModelType
{
    Unknown = 0,
    ConexyV1Flash = 1,
    ConexyV1Pro = 2,
    ConexyCoder = 3,
    // COWORK_MODE: добавлено 2026-09-23 — агент для нетехнических задач (исследования, документы,
    // аналитика). Тот же агентский конвейер, что у ConexyCoder; отличаются только промпт и инструменты.
    ConexyCowork = 4
}

public static class ConexyModelMapper
{
    public static bool TryParse(string? input, out ConexyModelType modelType)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            modelType = ConexyModelType.Unknown;
            return false;
        }

        var span = input.AsSpan().Trim();

        if (span.Equals("conexyv1-flash", StringComparison.OrdinalIgnoreCase))
        {
            modelType = ConexyModelType.ConexyV1Flash;
            return true;
        }
        if (span.Equals("conexyv1-pro", StringComparison.OrdinalIgnoreCase))
        {
            modelType = ConexyModelType.ConexyV1Pro;
            return true;
        }
        if (span.Equals("conexy-coder", StringComparison.OrdinalIgnoreCase))
        {
            modelType = ConexyModelType.ConexyCoder;
            return true;
        }
        if (span.Equals("conexy-cowork", StringComparison.OrdinalIgnoreCase))
        {
            modelType = ConexyModelType.ConexyCowork;
            return true;
        }

        modelType = ConexyModelType.Unknown;
        return false;
    }

    public static string ToPublicName(this ConexyModelType type) => type switch
    {
        ConexyModelType.ConexyV1Flash => "ConexyV1-flash",
        ConexyModelType.ConexyV1Pro   => "ConexyV1-pro",
        ConexyModelType.ConexyCoder   => "conexy-coder",
        ConexyModelType.ConexyCowork  => "conexy-cowork",
        _ => throw new ArgumentOutOfRangeException(nameof(type), "Model type is invalid or unknown")
    };

    // COWORK_MODE: добавлено 2026-09-23
    /// <summary>
    /// True for the autonomous agent modes (Coder and Cowork). Every "is this the agent pipeline?"
    /// branch must use this instead of comparing with <see cref="ConexyModelType.ConexyCoder"/>, so a
    /// new agent mode inherits the whole pipeline (runner, token budget, no chat smart-search) at once.
    /// </summary>
    public static bool IsAgent(this ConexyModelType type) =>
        type is ConexyModelType.ConexyCoder or ConexyModelType.ConexyCowork;
}
