namespace TornStockTravel.Services;

public sealed record GitHubUpdateInfo(
    string TagName,
    Version Version,
    string Name,
    string? Body,
    Uri HtmlUrl,
    Uri AssetDownloadUrl,
    string AssetName,
    long AssetSize)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? TagName : Name;

    public string AssetSizeText => AssetSize <= 0
        ? "unknown size"
        : $"{AssetSize / 1024m / 1024m:N1} MB";
}
