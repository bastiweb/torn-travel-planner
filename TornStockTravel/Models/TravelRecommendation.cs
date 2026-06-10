namespace TornStockTravel.Services;

public sealed record TravelRecommendation(
    string DestinationName,
    string DestinationCode,
    string ItemName,
    DateTimeOffset ReadyAt,
    DateTimeOffset DepartAt,
    DateTimeOffset ArriveAt,
    decimal Profit,
    decimal ProfitPerHour,
    decimal CashNeeded,
    decimal? Wallet,
    decimal? Vault,
    int MaxSpendPercent,
    string Reason)
{
    public decimal? TotalFunds => Wallet is null && Vault is null ? null : (Wallet ?? 0) + (Vault ?? 0);

    public decimal? SpendLimit => TotalFunds is null ? null : TotalFunds.Value * MaxSpendPercent / 100m;

    public bool HasEnoughWalletCash => Wallet is null || Wallet.Value >= CashNeeded;

    public decimal WalletShortfall => Wallet is null ? 0 : Math.Max(0, CashNeeded - Wallet.Value);

    public string DestinationText => $"{DestinationName} ({DestinationCode.ToUpperInvariant()})";

    public string DepartText => $"{DepartAt.LocalDateTime:HH:mm} local / {DepartAt.UtcDateTime:HH:mm} TCT";

    public string ArriveText => $"{ArriveAt.LocalDateTime:HH:mm} local / {ArriveAt.UtcDateTime:HH:mm} TCT";

    public string ProfitText => Profit.ToString("N0");

    public string ProfitPerHourText => ProfitPerHour.ToString("N0");

    public string CashNeededText => CashNeeded.ToString("N0");

    public string SpendLimitText => SpendLimit is null ? "-" : SpendLimit.Value.ToString("N0");

    public string CashStatusText => TotalFunds is null
        ? "Wallet/vault unavailable"
        : HasEnoughWalletCash
            ? $"Within {MaxSpendPercent}% limit"
            : $"Withdraw ${WalletShortfall:N0} from vault";
}
