namespace ConexyAI.Model;

public enum ConexyModelType
{
    Unknown = 0,
    ConexyV1Flash = 1,
    ConexyV1Pro = 2,
    ConexyCoder = 3
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

        modelType = ConexyModelType.Unknown;
        return false;
    }

    public static string ToPublicName(this ConexyModelType type) => type switch
    {
        ConexyModelType.ConexyV1Flash => "ConexyV1-flash",
        ConexyModelType.ConexyV1Pro   => "ConexyV1-pro",
        ConexyModelType.ConexyCoder   => "conexy-coder",
        _ => throw new ArgumentOutOfRangeException(nameof(type), "Model type is invalid or unknown")
    };
}
