namespace PcServ.Web.Services;

/// <summary>
/// Settings unique to one organization, kept out of the code and out of git. They come from district.json
/// when running locally, or from deploy/.env in production. Each one is optional.
/// </summary>
public sealed class DistrictSettings(IConfiguration configuration)
{
    /// <summary>What to call the system that tracks assets and checkouts, as it reads in the middle of a sentence.</summary>
    public string InventoryName { get; } = NameOr(configuration["PcServ:District:InventoryName"], "the asset system");

    /// <summary>That system's address, or null if it isn't set or isn't a web address.</summary>
    public string? InventoryUrl { get; } = WebAddressOrNull(configuration["PcServ:District:InventoryUrl"]);

    /// <summary>What to call the console that devices are managed from, as it reads in the middle of a sentence.</summary>
    public string DeviceConsoleName { get; } = NameOr(configuration["PcServ:District:DeviceConsoleName"], "the device console");

    /// <summary>That console's address, or null if it isn't set or isn't a web address.</summary>
    public string? DeviceConsoleUrl { get; } = WebAddressOrNull(configuration["PcServ:District:DeviceConsoleUrl"]);

    /// <summary>A name as the start of a sentence: "the asset system" becomes "The asset system".</summary>
    public static string Sentence(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>A name as a label: "the asset system" becomes "Asset system".</summary>
    public static string Label(string name) => Sentence(name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name);

    private static string NameOr(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>How many digits an asset tag has, or null if tags vary. Lets the camera ignore other barcodes, like serial numbers.</summary>
    public int? AssetTagDigits { get; } = int.TryParse(configuration["PcServ:District:AssetTagDigits"], out var digits) && digits is > 0 and <= 40 ? digits : null;

    /// <summary>Whether a scanned code could be an asset tag. Without a set length, anything that was read counts.</summary>
    public bool CouldBeAssetTag(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 40) return false;
        return AssetTagDigits is not { } length || (code.Length == length && code.All(char.IsAsciiDigit));
    }

    /// <summary>This becomes a link, so only ordinary web addresses are allowed through.</summary>
    public static string? WebAddressOrNull(string? value)
    {
        var address = value?.Trim();
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? address : null;
    }
}
